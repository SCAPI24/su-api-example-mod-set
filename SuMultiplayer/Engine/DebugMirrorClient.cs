using System;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Players;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 调试镜像（debug-only host-side split-screen mirror）的**引擎动作**。
    ///
    /// 机制：**camera-only 的临时本机 PlayerData**（需求首选方案）。
    ///
    /// 为什么必须是一个"完整玩家"：引擎的第三个分屏视图必须是一个 GameWidget，而
    /// <c>ViewWidget.Draw</c> 只在 <c>GameWidget.PlayerData.ComponentPlayer != null &amp;&amp;
    /// IsReadyForPlaying</c> 时绘制（Source: Survivalcraft/Game/ViewWidget.cs:45-51）；
    /// <c>FppCamera</c> 又从 <c>GameWidget.Target.ComponentCreatureModel</c> 取眼睛位置 / 朝向
    /// （Source: Survivalcraft/Game/FppCamera.cs:21-44）。所以"纯相机"仍然需要一份
    /// PlayerData + Player 实体 —— 本类把这份玩家造成一个**完全脱离世界**的壳：
    ///
    ///   ① 实体**从不调用 Project.AddEntity**（与 <c>Engine/NetworkAvatarSpawner.cs</c> 的
    ///      "先 AddEntity 再摘 m_entities"不同，这里更彻底）：
    ///        → 不在 SaveProject 的实体表里（Source: Project.cs:267-283），
    ///        → 不进 SubsystemBodies（不可碰撞 / 不可被近战·爆炸·射线选中），
    ///        → 不进 SubsystemUpdate（不读输入、不移动、不挖掘、不受伤、不耗资源），
    ///        → 不进 SubsystemDrawing / SubsystemModelsRenderer（模型永远不渲染）。
    ///      唯一手动补的回调是 <c>PlayerData.OnEntityAdded(entity)</c>（public，正常由
    ///      SubsystemPlayers.OnEntityAdded 调用）：它负责填 ComponentPlayer、ActiveCamera、Target
    ///      （Source: Survivalcraft/Game/PlayerData.cs:404-421）。
    ///   ② PlayerData 建完立刻从 <c>SubsystemPlayers.m_playersData</c> 摘掉
    ///      → 不进 SubsystemPlayers.Save（Source: SubsystemPlayers.cs:126-138）、
    ///        不在 roster / live map / records / stats 枚举里；NextPlayerIndex 恢复原值。
    ///   ③ 状态机强制 Playing（ViewWidget 的 IsReadyForPlaying 闸门）。
    ///   ④ 视图相机 = 该 GameWidget 的 FppCamera，Target 直接绑到**远端化身的 ComponentPlayer**
    ///      → 每帧跟随客户端的眼睛位置与朝向；壳自己的身体停在世界外
    ///      （<see cref="ParkedPosition"/>，所有相机都看不到它，也不会和远端化身重叠渲染）。
    ///   ⑤ 背包清空、视图不接受命中测试 / 拖放（避免触摸端把物品拖到远端位置）。
    ///
    /// 目标（默认所选客户端槽位 0，未来 clientId#slot）只通过 <c>NetworkAvatarSpawner</c> 的
    /// 公开 API（<c>Keys</c> / <c>TryGet(PlayerKey)</c>）读取，不改它的任何文件。
    ///
    /// 本类**无静态可变状态**：ParkedPosition 是 static readonly 值，反射入口由组合根注入。
    /// </summary>
    public sealed class DebugMirrorClient : IDebugMirrorPort
    {
        /// <summary>
        /// 壳身体停放点：世界外 100000 格。所有相机（可见范围最多几百格）都看不到它；
        /// 它也不会进入任何物理 / 渲染 / 更新子系统。
        /// </summary>
        private static readonly Vector3 ParkedPosition = new Vector3(100000f, 0f, 100000f);

        private readonly NetworkAvatarSpawner m_avatars;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly MethodInfo m_removeGameWidget;
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private Project m_project;
        private PlayerData m_playerData;
        private Entity m_entity;
        private GameWidget m_gameWidget;
        private ComponentPlayer m_remotePlayer;
        private DebugMirrorTarget m_target;
        private int m_playerIndex;

        public DebugMirrorClient(NetworkAvatarSpawner avatars, IModParentField parentField,
            IModParentMethod parentMethod, IModuleDiagnostics diagnostics, string ownerId)
        {
            m_avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            if (parentMethod == null) throw new ArgumentNullException(nameof(parentMethod));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "debug-mirror" : ownerId;
            m_removeGameWidget = parentMethod.GetInstanceMethodInfo(
                typeof(SubsystemGameWidgets), "RemoveGameWidget", new[] { typeof(GameWidget) });
            if (m_removeGameWidget == null)
            {
                throw new InvalidOperationException(
                    "引擎反射入口缺失（SubsystemGameWidgets.RemoveGameWidget）：" +
                    "调试镜像无法在不影响正常玩法的前提下拆除，拒绝半实现");
            }
        }

        public bool IsAlive => m_gameWidget != null && m_playerData != null && m_entity != null;

        public int ViewCount
        {
            get
            {
                Project project = GameManager.Project;
                if (project == null) return 0;
                SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
                return widgets?.GameWidgets.Count ?? 0;
            }
        }

        public bool TryListTargets(List<DebugMirrorTarget> targets, out string reason)
        {
            reason = null;
            if (targets == null)
            {
                reason = "调用方没有给目标容器";
                return false;
            }
            targets.Clear();
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进入世界）";
                return false;
            }
            var keys = new List<PlayerKey>(m_avatars.Keys);
            keys.Sort(delegate (PlayerKey left, PlayerKey right)
            {
                if (left.ClientId != right.ClientId) return left.ClientId.CompareTo(right.ClientId);
                return left.Slot.CompareTo(right.Slot);
            });
            for (int i = 0; i < keys.Count; i++)
            {
                PlayerKey key = keys[i];
                // 主机自己的角色是 clientId 0：它们本来就在本机分屏里，不需要镜像。
                if (key.ClientId <= 0) continue;
                if (!m_avatars.TryGet(key, out PlayerData playerData) || playerData == null) continue;
                if (playerData.ComponentPlayer == null || playerData.ComponentPlayer.ComponentBody == null)
                    continue;
                targets.Add(new DebugMirrorTarget(key.ClientId, key.Slot));
            }
            if (targets.Count == 0)
            {
                reason = "主机上没有 ComponentPlayer/ComponentBody 已就绪的远端角色（" +
                    "客户端还没加入、角色实体还没建好，或当前没有任何远端连接）";
                return false;
            }
            return true;
        }

        public bool TryCreate(DebugMirrorTarget target, out bool noSlot, out string failureReason)
        {
            noSlot = false;
            failureReason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                failureReason = "没有加载中的 Project（还没进入世界）";
                return false;
            }
            if (IsAlive)
            {
                failureReason = "已经有一个调试镜像在运行（同一时间只允许 1 个）";
                return false;
            }
            if (!target.IsResolved)
            {
                failureReason = "镜像目标 " + target + " 不是具体目标（必须 clientId>0、slot 0.." +
                    DebugMirrorTarget.MaxSlot + "）";
                return false;
            }
            PlayerKey key = new PlayerKey(target.ClientId, target.Slot);
            if (!m_avatars.TryGet(key, out PlayerData remote) ||
                remote?.ComponentPlayer?.ComponentBody == null)
            {
                failureReason = "玩家键 " + key + " 的联机角色不存在（或 ComponentPlayer/ComponentBody 还没就绪）";
                return false;
            }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (players == null || widgets == null)
            {
                failureReason = "世界缺少必要的引擎子系统（SubsystemPlayers / SubsystemGameWidgets）";
                return false;
            }
            if (widgets.GameWidgets.Count >= SubsystemGameWidgets.MaxGameWidgets)
            {
                noSlot = true;
                failureReason = "引擎分屏已用满 " + SubsystemGameWidgets.MaxGameWidgets +
                    " 个 GameWidget（当前 " + widgets.GameWidgets.Count + " 个）";
                return false;
            }
            if (players.PlayersData.Count >= SubsystemPlayers.MaxPlayers)
            {
                noSlot = true;
                failureReason = "引擎玩家槽位已用满 " + SubsystemPlayers.MaxPlayers +
                    " 个 PlayerData（当前 " + players.PlayersData.Count + " 个本机玩家）";
                return false;
            }
            int playerIndex = FindFreePlayerIndex(players);
            if (playerIndex < 0)
            {
                noSlot = true;
                failureReason = "引擎 PlayerIndex 0.." + (SubsystemGameWidgets.MaxGameWidgets - 1) +
                    " 都被占用（本机玩家 + 联机角色），无法再建一个临时玩家";
                return false;
            }
            int counterBefore = m_parentField.GetParentField<int>(players, "m_nextPlayerIndex",
                typeof(SubsystemPlayers));
            PlayerData playerData = null;
            Entity entity = null;
            ComponentBody body = null;
            GameWidget gameWidget = null;
            try
            {
                playerData = new PlayerData(project)
                {
                    Name = Shorten(remote.Name, 14),
                    // 壳模型不会渲染（实体不加入世界），皮肤保持 PlayerData 自己摇出的内置皮肤：
                    // 远端可能用自定义皮肤，本地皮肤库不一定有那张图，抄过来在建实体时会读不到贴图。
                    PlayerClass = remote.PlayerClass,
                    InputDevice = WidgetInputDevice.None,
                    SpawnPosition = remote.ComponentPlayer.ComponentBody.Position
                };
                // 引擎发号：AddPlayerData 里 PlayerIndex = m_nextPlayerIndex++，
                // 所以先把计数器压到目标空位，再由 AddPlayerData 取出这个号；随后立刻恢复计数器，
                // 让临时玩家不改变世界的 NextPlayerIndex（存盘也不会因此变化）。
                // Source: Survivalcraft/Game/SubsystemPlayers.cs:62-75
                m_parentField.ModifyParentField(players, "m_nextPlayerIndex", playerIndex,
                    typeof(SubsystemPlayers));
                players.AddPlayerData(playerData);
                m_parentField.ModifyParentField(players, "m_nextPlayerIndex", counterBefore,
                    typeof(SubsystemPlayers));
                gameWidget = playerData.GameWidget;

                ValuesDictionary overrides = new ValuesDictionary
                {
                    { "Player", new ValuesDictionary { { "PlayerIndex", playerData.PlayerIndex } } },
                    { "Intro", new ValuesDictionary { { "PlayIntro", false } } }
                };
                entity = DatabaseManager.CreateEntity(project, playerData.GetEntityTemplateName(),
                    overrides, throwIfNotFound: true);
                body = entity.FindComponent<ComponentBody>(throwOnError: true);
                body.Position = ParkedPosition;
                body.Velocity = Vector3.Zero;
                body.IsGravityEnabled = false;
                body.IsGroundDragEnabled = false;
                body.IsWaterDragEnabled = false;

                // ★ 实体**不加入 Project**（不调用 AddEntity）：
                //   不存盘、不进 SubsystemBodies / SubsystemUpdate / SubsystemDrawing /
                //   SubsystemModelsRenderer，也不参与任何玩家枚举。
                //   唯一必须补的回调是 PlayerData.OnEntityAdded（正常由 SubsystemPlayers 调）。
                // Source: Survivalcraft/Game/PlayerData.cs:404-421
                playerData.OnEntityAdded(entity);

                // PlayerData 立刻离开本机玩家列表：Save / roster / live map / records / stats 都看不到它。
                // Source: Survivalcraft/Game/SubsystemPlayers.cs:126-138（Save 只遍历 m_playersData）
                m_parentField.GetParentField<List<PlayerData>>(players, "m_playersData",
                    typeof(SubsystemPlayers)).Remove(playerData);
                ComponentPlayer componentPlayer = playerData.ComponentPlayer;
                if (componentPlayer != null)
                {
                    m_parentField.GetParentField<List<ComponentPlayer>>(players, "m_componentPlayers",
                        typeof(SubsystemPlayers)).Remove(componentPlayer);
                }

                // 视图只有在状态机 = Playing 时才画（IsReadyForPlaying）。
                // Source: Survivalcraft/Game/PlayerData.cs:124-134
                // Source: Survivalcraft/Game/ViewWidget.cs:45-51
                m_parentField.GetParentField<StateMachine>(playerData, "m_stateMachine",
                    typeof(PlayerData)).TransitionTo("Playing");

                if (componentPlayer == null)
                {
                    throw new InvalidOperationException(
                        "临时玩家实体已创建，但 PlayerData.ComponentPlayer 仍为空（引擎没有回填）");
                }

                // 相机 Target = 远端化身：FppCamera 每帧读 Target 的 EyePosition / EyeRotation。
                // Source: Survivalcraft/Game/FppCamera.cs:21-44
                gameWidget.Target = remote.ComponentPlayer;
                // 立刻更新一次相机，避免本帧 Draw 用到 LoadingCamera 留下的零向量朝向。
                gameWidget.ActiveCamera.Update(0f);
                // 调试视图不接受命中测试 / 拖放：避免触摸端把物品"拖进"镜像视图后
                // 由 ViewWidget.DragDrop 在远端位置生成拾取物（Source: ViewWidget.cs:59-79）。
                gameWidget.ViewWidget.IsHitTestVisible = false;

                int clearedSlots = 0;
                int clearedItems = 0;
                IInventory inventory = componentPlayer.ComponentMiner?.Inventory;
                if (inventory != null)
                {
                    for (int slot = 0; slot < inventory.SlotsCount; slot++)
                    {
                        int count = inventory.GetSlotCount(slot);
                        if (count <= 0) continue;
                        int removed = inventory.RemoveSlotItems(slot, count);
                        if (removed > 0)
                        {
                            clearedSlots++;
                            clearedItems += removed;
                        }
                    }
                }

                m_project = project;
                m_playerData = playerData;
                m_entity = entity;
                m_gameWidget = gameWidget;
                m_remotePlayer = remote.ComponentPlayer;
                m_target = target;
                m_playerIndex = playerData.PlayerIndex;
                Log("Info", "debug mirror 已创建：目标=" + target + " · PlayerIndex=" + m_playerIndex +
                    " · GameWidget#" + gameWidget.GameWidgetIndex + " · 视图数=" +
                    widgets.GameWidgets.Count + " · 实体未加入 Project（未调用 AddEntity）" +
                    " → 不参与保存 / 更新 / 碰撞 / 渲染 · 身体停在 " + ParkedPosition +
                    " · 背包清空 slots=" + clearedSlots + "/items=" + clearedItems +
                    " · 相机 Target=远端角色的 ComponentPlayer");
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.GetType().Name + " " + ex.Message;
                noSlot = failureReason.IndexOf("Too many GameWidgets", StringComparison.Ordinal) >= 0 ||
                    failureReason.IndexOf("Too many players", StringComparison.Ordinal) >= 0;
                // 回滚：不允许留下半个角色（与 NetworkAvatarSpawner 同一语义）。
                try
                {
                    if (entity != null) entity.Dispose();
                    // AddPlayerData 可能**先加入列表、再在 PlayerAdded 里抛**（4 GameWidget 上限），
                    // 所以这里无条件摘一次（List.Remove 对不存在的元素是 no-op），
                    // 否则会留下一个会被存盘的半个玩家。
                    if (playerData != null && players != null)
                    {
                        m_parentField.GetParentField<List<PlayerData>>(players, "m_playersData",
                            typeof(SubsystemPlayers)).Remove(playerData);
                        if (playerData.ComponentPlayer != null)
                        {
                            m_parentField.GetParentField<List<ComponentPlayer>>(players,
                                "m_componentPlayers", typeof(SubsystemPlayers))
                                .Remove(playerData.ComponentPlayer);
                        }
                    }
                    if (gameWidget != null && widgets != null)
                    {
                        try { m_removeGameWidget.Invoke(widgets, new object[] { gameWidget }); }
                        catch (Exception widgetError)
                        {
                            Log("Warn", "回滚时摘除镜像 GameWidget 失败：" + widgetError.GetType().Name +
                                " " + widgetError.Message);
                        }
                        gameWidget.Dispose();
                    }
                    if (playerData != null) playerData.Dispose();
                    // 计数器恢复：无论 AddPlayerData 有没有走到 ++，都回到调用前的值。
                    m_parentField.ModifyParentField(players, "m_nextPlayerIndex", counterBefore,
                        typeof(SubsystemPlayers));
                }
                catch (Exception rollbackError)
                {
                    Log("Error", "回滚调试镜像时又失败：" + rollbackError.GetType().Name + " " +
                        rollbackError.Message);
                }
                return false;
            }
        }

        public bool Validate(out string reason)
        {
            reason = null;
            if (!IsAlive)
            {
                reason = "镜像视图不完整（GameWidget / PlayerData / Entity 有缺失）";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "世界已卸载（GameManager.Project 为空）";
                return false;
            }
            if (!ReferenceEquals(project, m_project))
            {
                reason = "已经切换到另一个世界（Project 引用变了）";
                return false;
            }
            if (!m_avatars.TryGet(new PlayerKey(m_target.ClientId, m_target.Slot),
                    out PlayerData remote) || remote == null)
            {
                reason = "目标 " + m_target + " 的联机角色已不存在（客户端离开 / 角色被移除）";
                return false;
            }
            if (remote.ComponentPlayer == null || remote.ComponentPlayer.ComponentBody == null)
            {
                reason = "目标 " + m_target + " 的联机角色还没有 ComponentPlayer/ComponentBody";
                return false;
            }
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null)
            {
                reason = "世界缺少 SubsystemGameWidgets";
                return false;
            }
            bool registered = false;
            for (int i = 0; i < widgets.GameWidgets.Count; i++)
            {
                if (ReferenceEquals(widgets.GameWidgets[i], m_gameWidget))
                {
                    registered = true;
                    break;
                }
            }
            if (!registered)
            {
                reason = "镜像 GameWidget 已不在 SubsystemGameWidgets 的列表里";
                return false;
            }
            if (!ReferenceEquals(m_remotePlayer, remote.ComponentPlayer))
            {
                // 远端角色重生会换一个 ComponentPlayer：把相机重新绑到新的那个。
                m_gameWidget.Target = remote.ComponentPlayer;
                m_remotePlayer = remote.ComponentPlayer;
                LogRateLimited("rebind", "Info", "目标 " + m_target + " 的 ComponentPlayer 换了" +
                    "（重生 / 重新建角色）→ 镜像相机已重新绑定");
            }
            return true;
        }

        public void Destroy(string reason)
        {
            if (!IsAlive)
            {
                // 没有完整镜像时也把可能残留的对象清掉（回滚失败 / 世界卸载）。
                ClearFields();
                return;
            }
            Project project = GameManager.Project;
            SubsystemGameWidgets widgets = project?.FindSubsystem<SubsystemGameWidgets>(false);
            if (project == null || !ReferenceEquals(project, m_project))
            {
                // 世界已经 Dispose：GameWidget 随界面树一起释放；临时实体 / PlayerData 只被本适配器
                // 引用（从未加入 Project），这里清掉引用交给 GC —— 本来就不在存盘范围。
                Log("Info", "debug mirror 已拆除（" + reason +
                    "）：世界已卸载，清掉临时玩家引用（从未加入 Project）· 存盘=否 · 视图数=0");
                ClearFields();
                return;
            }
            try
            {
                if (m_gameWidget != null && widgets != null)
                {
                    m_removeGameWidget.Invoke(widgets, new object[] { m_gameWidget });
                }
            }
            catch (Exception ex)
            {
                Log("Warn", "拆除镜像 GameWidget 时异常（继续释放其余对象）：" + ex.GetType().Name +
                    " " + ex.Message);
            }
            try
            {
                m_gameWidget?.Dispose();
            }
            catch (Exception ex)
            {
                Log("Warn", "Dispose 镜像 GameWidget 异常：" + ex.GetType().Name + " " + ex.Message);
            }
            try
            {
                if (m_entity != null)
                {
                    // 实体从未加入 Project：清掉 PlayerData 对 ComponentPlayer 的引用后直接 Dispose。
                    m_playerData?.OnEntityRemoved(m_entity);
                    m_entity.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log("Warn", "Dispose 镜像实体时异常（继续释放 PlayerData）：" + ex.GetType().Name +
                    " " + ex.Message);
            }
            try
            {
                m_playerData?.Dispose();
            }
            catch (Exception ex)
            {
                Log("Warn", "Dispose 镜像 PlayerData 异常：" + ex.GetType().Name + " " + ex.Message);
            }
            int views = widgets?.GameWidgets.Count ?? 0;
            Log("Info", "debug mirror 已拆除（" + reason +
                "）：GameWidget 已摘除并 Dispose、临时实体与 PlayerData 已 Dispose" +
                " · 剩余视图数=" + views + " · 存盘=否");
            ClearFields();
        }

        private void ClearFields()
        {
            m_project = null;
            m_playerData = null;
            m_entity = null;
            m_gameWidget = null;
            m_remotePlayer = null;
            m_playerIndex = 0;
        }

        private int FindFreePlayerIndex(SubsystemPlayers players)
        {
            var used = new HashSet<int>();
            for (int i = 0; i < players.PlayersData.Count; i++)
            {
                used.Add(players.PlayersData[i].PlayerIndex);
            }
            foreach (PlayerKey key in m_avatars.Keys)
            {
                if (m_avatars.TryGet(key, out PlayerData avatar) && avatar != null)
                    used.Add(avatar.PlayerIndex);
            }
            for (int index = 0; index < SubsystemGameWidgets.MaxGameWidgets; index++)
            {
                if (!used.Contains(index)) return index;
            }
            return -1;
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "Mirror";
            string trimmed = text.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }

        private void Log(string level, string message)
        {
            if (m_diagnostics == null) return;
            if (level == "Warn") m_diagnostics.Warn(m_ownerId, message);
            else if (level == "Error") m_diagnostics.Error(m_ownerId, message, null);
            else m_diagnostics.Info(m_ownerId, message);
        }

        private void LogRateLimited(string key, string level, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < 5000) return;
            m_lastLogMs[key] = nowMs;
            Log(level, message);
        }
    }
}
