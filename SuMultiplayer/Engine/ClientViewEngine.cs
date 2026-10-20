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
    /// 自动客户端分屏的**引擎动作**（<see cref="IClientViewPort"/> 的实现）。
    ///
    /// 复用 <c>Engine/DebugMirrorClient.cs</c> 已验证的 camera-only 配方，每个客户端一份：
    ///   ① 建一个临时 <c>PlayerData</c> + 玩家实体，但**实体从不调用 <c>Project.AddEntity</c>**：
    ///        → 不在 SaveProject 的实体表里（Source: Project.cs:267-283），
    ///        → 不进 SubsystemBodies（不可碰撞 / 不可被近战·爆炸·射线选中），
    ///        → 不进 SubsystemUpdate（不读输入、不移动、不挖掘、不受伤、不耗资源），
    ///        → 不进 SubsystemDrawing / SubsystemModelsRenderer（壳模型永远不渲染）。
    ///      唯一手动补的回调是 <c>PlayerData.OnEntityAdded(entity)</c>（它填 ComponentPlayer /
    ///      ActiveCamera / Target；Source: Survivalcraft/Game/PlayerData.cs:404-421）。
    ///   ② PlayerData 建完立刻从 <c>SubsystemPlayers.m_playersData</c> 摘掉
    ///      → 不进 SubsystemPlayers.Save（Source: Survivalcraft/Game/SubsystemPlayers.cs:126-138）、
    ///        不在 roster / live map / records / stats 枚举里；m_nextPlayerIndex 恢复原值。
    ///   ③ 状态机强制 Playing（ViewWidget 的 IsReadyForPlaying 闸门；
    ///      Source: Survivalcraft/Game/ViewWidget.cs:45-51）。
    ///   ④ 视图相机 = 该 GameWidget 的 FppCamera，Target 直接绑到**远端化身的 ComponentPlayer**
    ///      → 每帧跟随客户端的眼睛位置与朝向（Source: Survivalcraft/Game/FppCamera.cs:21-44），
    ///      画面与客户端自己看到的第一人称完全一致；壳自己的身体停在世界外
    ///      （<see cref="ParkedPosition"/>，所有相机都看不到，也不会和远端化身重叠渲染）。
    ///   ⑤ 背包清空、视图不接受命中测试 / 拖放（避免触摸端把物品拖到远端位置）。
    ///
    /// 槽位账本：每个视图同时占一个 <c>PlayerIndex</c> + 一个 <c>GameWidget</c>；
    /// 远端化身虽然把 GameWidget 从界面摘掉，但仍占用 PlayerIndex。因此可用性判定
    /// = <see cref="ClientViewSlots"/> 的 <c>hostLocals + avatars + views ≤ 4</c>。
    ///
    /// 本类**无静态可变状态**：ParkedPosition 是 static readonly 值，反射入口由组合根注入。
    /// </summary>
    public sealed class ClientViewEngine : IClientViewPort, IDisposable
    {
        private const string OwnerId = "client-view";

        /// <summary>
        /// 壳身体停放点：世界外 100000 格。所有相机（可见范围最多几百格）都看不到它；
        /// 它也不会进入任何物理 / 渲染 / 更新子系统。
        /// </summary>
        private static readonly Vector3 ParkedPosition = new Vector3(100000f, 0f, 100000f);

        private sealed class View
        {
            public int ClientId;
            public Project Project;
            public PlayerData PlayerData;
            public Entity Entity;
            public GameWidget GameWidget;
            public ComponentPlayer RemotePlayer;
            public int PlayerIndex;
        }

        private readonly NetworkAvatarSpawner m_avatars;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly MethodInfo m_removeGameWidget;
        private readonly Dictionary<int, View> m_views = new Dictionary<int, View>();
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<PlayerData> m_localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
        private readonly List<int> m_usedPlayerIndexes = new List<int>(ClientViewSlots.MaxSlots);
        private readonly List<int> m_viewClientIds = new List<int>();

        public ClientViewEngine(NetworkAvatarSpawner avatars, IModParentField parentField,
            IModParentMethod parentMethod, IModuleDiagnostics diagnostics)
        {
            m_avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            if (parentMethod == null) throw new ArgumentNullException(nameof(parentMethod));
            m_diagnostics = diagnostics;
            m_removeGameWidget = parentMethod.GetInstanceMethodInfo(
                typeof(SubsystemGameWidgets), "RemoveGameWidget", new[] { typeof(GameWidget) });
            if (m_removeGameWidget == null)
            {
                throw new InvalidOperationException(
                    "引擎反射入口缺失（SubsystemGameWidgets.RemoveGameWidget）：" +
                    "自动客户端分屏无法在不影响正常玩法的前提下拆除，拒绝半实现");
            }
        }

        public int ViewCount => m_views.Count;

        public int HostLocalPlayerCount
        {
            get
            {
                Project project = GameManager.Project;
                if (project == null) return 0;
                if (!m_avatars.TryCollectLocalPlayers(project, m_localPlayers, out _, out _)) return 0;
                return m_localPlayers.Count;
            }
        }

        public int AvatarCount => m_avatars.Count;

        public bool IsAlive(int clientId) => m_views.ContainsKey(clientId);

        public bool TryListCandidates(List<ClientViewCandidate> candidates, out string reason)
        {
            reason = null;
            if (candidates == null)
            {
                reason = "调用方没有给候选列表";
                return false;
            }
            candidates.Clear();
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进入世界）";
                return false;
            }
            var seen = new HashSet<int>();
            foreach (PlayerKey key in m_avatars.Keys)
            {
                // 自动视图只跟随客户端的**第一个本机角色**（槽位 0），与 debugmirror 的默认目标口径一致。
                if (key.ClientId <= 0 || key.Slot != 0 || !seen.Add(key.ClientId)) continue;
                if (!m_avatars.TryGet(key, out PlayerData avatar) || avatar == null) continue;
                bool ready = avatar.ComponentPlayer != null &&
                    avatar.ComponentPlayer.ComponentBody != null &&
                    avatar.ComponentPlayer.ComponentCreatureModel != null;
                candidates.Add(new ClientViewCandidate(key.ClientId, key.Slot, ready,
                    avatar.PlayerIndex));
            }
            candidates.Sort(delegate (ClientViewCandidate left, ClientViewCandidate right)
            {
                return left.ClientId.CompareTo(right.ClientId);
            });
            if (candidates.Count == 0)
                reason = "当前没有已加入的客户端（还没有联机化身）";
            return true;
        }

        public void ListViews(List<int> clientIds)
        {
            if (clientIds == null) return;
            clientIds.Clear();
            foreach (int clientId in m_views.Keys) clientIds.Add(clientId);
            clientIds.Sort();
        }

        public bool TryCreate(int clientId, out bool noSlot, out int playerIndex,
            out string failureReason)
        {
            noSlot = false;
            playerIndex = -1;
            failureReason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                failureReason = "没有加载中的 Project（还没进入世界）";
                return false;
            }
            if (clientId <= 0)
            {
                failureReason = "clientId=" + clientId + " 非法（主机自己 clientId 0 不需要自动视图）";
                return false;
            }
            if (m_views.ContainsKey(clientId))
            {
                failureReason = "客户端 " + clientId + " 已经有自动分屏视图";
                return false;
            }
            var key = new PlayerKey(clientId, 0);
            if (!m_avatars.TryGet(key, out PlayerData remote) || remote == null)
            {
                failureReason = "玩家键 " + key + " 的联机角色不存在（客户端还没加入 / 已离开）";
                return false;
            }
            ComponentPlayer remotePlayer = remote.ComponentPlayer;
            if (remotePlayer == null || remotePlayer.ComponentBody == null)
            {
                failureReason = "客户端 " + clientId +
                    " 的联机角色还没有 ComponentPlayer/ComponentBody（角色未就绪）";
                return false;
            }
            if (remotePlayer.ComponentCreatureModel == null)
            {
                failureReason = "客户端 " + clientId +
                    " 的联机角色没有 ComponentCreatureModel（第一人称相机没有可跟随的眼睛）";
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
            if (!ClientViewSlots.HasCapacity(players.PlayersData.Count, m_avatars.Count,
                    m_views.Count))
            {
                noSlot = true;
                failureReason = ClientViewSlots.Describe(players.PlayersData.Count,
                    m_avatars.Count, m_views.Count) + "（本机角色 + 联机化身 + 视图已达 4，无空槽）";
                return false;
            }
            m_usedPlayerIndexes.Clear();
            foreach (PlayerData item in players.PlayersData)
                if (item != null) m_usedPlayerIndexes.Add(item.PlayerIndex);
            foreach (PlayerKey avatarKey in m_avatars.Keys)
                if (m_avatars.TryGet(avatarKey, out PlayerData avatar) && avatar != null)
                    m_usedPlayerIndexes.Add(avatar.PlayerIndex);
            foreach (View view in m_views.Values) m_usedPlayerIndexes.Add(view.PlayerIndex);
            if (!ClientViewSlots.TryAllocate(m_usedPlayerIndexes, out playerIndex))
            {
                noSlot = true;
                failureReason = "引擎 PlayerIndex 0.." + (ClientViewSlots.MaxSlots - 1) +
                    " 都被占用（本机角色 + 联机化身 + 已有视图），无法再建一个临时玩家 · " +
                    ClientViewSlots.Describe(players.PlayersData.Count, m_avatars.Count,
                        m_views.Count);
                return false;
            }

            int counterBefore = m_parentField.GetParentField<int>(players, "m_nextPlayerIndex",
                typeof(SubsystemPlayers));
            PlayerData playerData = null;
            Entity entity = null;
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
                    SpawnPosition = remotePlayer.ComponentBody.Position
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
                if (playerData.PlayerIndex != playerIndex)
                {
                    throw new InvalidOperationException(
                        "引擎发出的 PlayerIndex=" + playerData.PlayerIndex + " 与预留空位 " +
                        playerIndex + " 不一致（拒绝把视图建在错误槽位上）");
                }
                gameWidget = playerData.GameWidget;

                ValuesDictionary overrides = new ValuesDictionary
                {
                    { "Player", new ValuesDictionary { { "PlayerIndex", playerData.PlayerIndex } } },
                    { "Intro", new ValuesDictionary { { "PlayIntro", false } } }
                };
                entity = DatabaseManager.CreateEntity(project, playerData.GetEntityTemplateName(),
                    overrides, throwIfNotFound: true);
                ComponentBody body = entity.FindComponent<ComponentBody>(throwOnError: true);
                body.Position = ParkedPosition;
                body.Velocity = Vector3.Zero;
                body.IsGravityEnabled = false;
                body.IsGroundDragEnabled = false;
                body.IsWaterDragEnabled = false;

                // ★ 实体**不加入 Project**（不调用 Project.AddEntity）：
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
                gameWidget.Target = remotePlayer;
                // 立刻更新一次相机，避免本帧 Draw 用到 LoadingCamera 留下的零向量朝向。
                gameWidget.ActiveCamera.Update(0f);
                // 自动视图不接受命中测试 / 拖放：避免触摸端把物品"拖进"镜像视图后
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

                var view = new View
                {
                    ClientId = clientId,
                    Project = project,
                    PlayerData = playerData,
                    Entity = entity,
                    GameWidget = gameWidget,
                    RemotePlayer = remotePlayer,
                    PlayerIndex = playerData.PlayerIndex
                };
                m_views.Add(clientId, view);
                LogRateLimited("created-" + clientId, "Info", "自动客户端视图已创建：客户端=" + clientId + " · 玩家键=" + key +
                    " · PlayerIndex=" + view.PlayerIndex + " · GameWidget#" +
                    gameWidget.GameWidgetIndex + " · 视图数=" + m_views.Count +
                    " · 实体未加入 Project（未调用 Project.AddEntity）" +
                    " → 不参与保存 / 更新 / 碰撞 / 渲染 · 身体停在 " + ParkedPosition +
                    " · 背包清空 slots=" + clearedSlots + "/items=" + clearedItems +
                    " · 相机 Target=远端角色的 ComponentPlayer · " +
                    ClientViewSlots.Describe(players.PlayersData.Count, m_avatars.Count,
                        m_views.Count));
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.GetType().Name + " " + ex.Message;
                noSlot = failureReason.IndexOf("Too many GameWidgets", StringComparison.Ordinal) >= 0 ||
                    failureReason.IndexOf("Too many players", StringComparison.Ordinal) >= 0;
                // 回滚：不允许留下半个角色（与 NetworkAvatarSpawner / DebugMirrorClient 同一语义）。
                try
                {
                    if (entity != null) entity.Dispose();
                    // AddPlayerData 可能**先加入列表、再在 PlayerAdded 里抛**（4 GameWidget 上限），
                    // 所以这里无条件摘一次（List.Remove 对不存在的元素是 no-op）。
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
                            Log("Warn", "回滚时摘除自动视图 GameWidget 失败：" +
                                widgetError.GetType().Name + " " + widgetError.Message);
                        }
                        gameWidget.Dispose();
                    }
                    if (playerData != null) playerData.Dispose();
                    m_parentField.ModifyParentField(players, "m_nextPlayerIndex", counterBefore,
                        typeof(SubsystemPlayers));
                }
                catch (Exception rollbackError)
                {
                    Log("Error", "回滚自动客户端视图时又失败：" + rollbackError.GetType().Name + " " +
                        rollbackError.Message);
                }
                return false;
            }
        }

        public bool Validate(int clientId, out bool targetMissing, out string reason)
        {
            targetMissing = false;
            reason = null;
            if (!m_views.TryGetValue(clientId, out View view) || view == null)
            {
                reason = "客户端 " + clientId + " 没有自动分屏视图";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "世界已卸载（GameManager.Project 为空）";
                return false;
            }
            if (!ReferenceEquals(project, view.Project))
            {
                reason = "已经切换到另一个世界（Project 引用变了）";
                return false;
            }
            if (!m_avatars.TryGet(new PlayerKey(clientId, 0), out PlayerData remote) ||
                remote == null)
            {
                targetMissing = true;
                reason = "客户端 " + clientId + " 的联机角色已不存在（离开 / 角色被移除）";
                return false;
            }
            if (remote.ComponentPlayer == null || remote.ComponentPlayer.ComponentBody == null ||
                remote.ComponentPlayer.ComponentCreatureModel == null)
            {
                targetMissing = true;
                reason = "客户端 " + clientId +
                    " 的联机角色还没有 ComponentPlayer/ComponentBody/ComponentCreatureModel";
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
                if (!ReferenceEquals(widgets.GameWidgets[i], view.GameWidget)) continue;
                registered = true;
                break;
            }
            if (!registered)
            {
                reason = "自动视图的 GameWidget 已不在 SubsystemGameWidgets 的列表里";
                return false;
            }
            if (!ReferenceEquals(view.RemotePlayer, remote.ComponentPlayer))
            {
                // 远端角色重生会换一个 ComponentPlayer：把相机重新绑到新的那个。
                view.GameWidget.Target = remote.ComponentPlayer;
                view.RemotePlayer = remote.ComponentPlayer;
                LogRateLimited("rebind-" + clientId, "Info", "客户端 " + clientId +
                    " 的 ComponentPlayer 换了（重生 / 重新建角色）→ 自动视图相机已重新绑定");
            }
            return true;
        }

        public void Destroy(int clientId, string reason)
        {
            if (!m_views.TryGetValue(clientId, out View view) || view == null)
            {
                m_views.Remove(clientId);
                return;
            }
            m_views.Remove(clientId);
            Project project = GameManager.Project;
            SubsystemGameWidgets widgets = project?.FindSubsystem<SubsystemGameWidgets>(false);
            if (project == null || !ReferenceEquals(project, view.Project))
            {
                // 世界已经 Dispose：GameWidget 随界面树一起释放；临时实体 / PlayerData 只被本适配器
                // 引用（从未加入 Project），这里清掉引用交给 GC —— 本来就不在存盘范围。
                LogRateLimited("destroyed-" + clientId, "Info", "自动客户端视图已拆除（" + reason + "）：客户端=" + clientId +
                    " · 世界已卸载，清掉临时玩家引用（从未加入 Project）· 存盘=否 · 视图数=" +
                    m_views.Count);
                return;
            }
            try
            {
                if (view.GameWidget != null && widgets != null)
                    m_removeGameWidget.Invoke(widgets, new object[] { view.GameWidget });
            }
            catch (Exception ex)
            {
                Log("Warn", "拆除自动视图 GameWidget 时异常（继续释放其余对象）：" +
                    ex.GetType().Name + " " + ex.Message);
            }
            try
            {
                view.GameWidget?.Dispose();
            }
            catch (Exception ex)
            {
                Log("Warn", "Dispose 自动视图 GameWidget 异常：" + ex.GetType().Name + " " +
                    ex.Message);
            }
            try
            {
                if (view.Entity != null)
                {
                    // 实体从未加入 Project：清掉 PlayerData 对 ComponentPlayer 的引用后直接 Dispose。
                    view.PlayerData?.OnEntityRemoved(view.Entity);
                    view.Entity.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log("Warn", "Dispose 自动视图实体时异常（继续释放 PlayerData）：" +
                    ex.GetType().Name + " " + ex.Message);
            }
            try
            {
                view.PlayerData?.Dispose();
            }
            catch (Exception ex)
            {
                Log("Warn", "Dispose 自动视图 PlayerData 异常：" + ex.GetType().Name + " " +
                    ex.Message);
            }
            LogRateLimited("destroyed-" + clientId, "Info", "自动客户端视图已拆除（" + reason + "）：客户端=" + clientId +
                " · PlayerIndex=" + view.PlayerIndex + " 已释放 · GameWidget 已摘除并 Dispose、" +
                "临时实体与 PlayerData 已 Dispose · 剩余视图数=" + m_views.Count +
                " · 存盘=否");
        }

        public void DestroyAll(string reason)
        {
            if (m_views.Count == 0) return;
            var clientIds = new List<int>(m_views.Keys);
            clientIds.Sort();
            for (int i = 0; i < clientIds.Count; i++)
                Destroy(clientIds[i], reason);
        }

        public void Dispose()
        {
            try
            {
                DestroyAll("ClientViewEngine.Dispose");
            }
            catch (Exception ex)
            {
                Log("Warn", "释放自动客户端视图时异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "ClientView";
            string trimmed = text.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }

        private void Log(string level, string message)
        {
            if (m_diagnostics == null) return;
            if (level == "Warn") m_diagnostics.Warn(OwnerId, message);
            else if (level == "Error") m_diagnostics.Error(OwnerId, message, null);
            else m_diagnostics.Info(OwnerId, message);
        }

        private void LogRateLimited(string key, string level, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < 10000) return;
            m_lastLogMs[key] = nowMs;
            Log(level, message);
        }
    }
}
