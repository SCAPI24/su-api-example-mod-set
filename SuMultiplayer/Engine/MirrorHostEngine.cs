using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Players;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 联机调试功能「镜像宿主」的**引擎动作**（<see cref="IMirrorHostPort"/> /
    /// <see cref="IMirrorHostClientSource"/> 的实现）。
    ///
    /// 产品口径：主机上每个加入的客户端，在同屏可用池（= 4 − 主机真实本地角色数）还有空位时，
    /// 自动获得一个**真实的引擎本地角色**（= 引擎的第二 / 第三 / 第四个分屏），这样第三 / 第四分屏
    /// 就有引擎自带的触控控件 / HUD / 界面镜像 / 睡眠与受击表现；这个分屏的相机再绑到客户端的第一人称。
    ///
    /// 添加 / 移除**只用引擎公开链路**（不反射、不改私有字段）：
    ///   ① 添加：<c>new PlayerData(project)</c>
    ///      （Source: Survivalcraft/Game/PlayerScreen.cs:121 `m_playerData = new PlayerData(...)`）
    ///      → 设 <c>Name</c> / <c>InputDevice</c>（PlayerData.cs:84-98、:122）
    ///      → <c>SubsystemPlayers.AddPlayerData(playerData)</c>
    ///      （SubsystemPlayers.cs:62-75：上限 4、引擎自己给 PlayerIndex 发号、触发 PlayerAdded
    ///      → SubsystemGameWidgets.cs:85-100 自动建 GameWidget、GamesWidget 分屏布局自动切换）
    ///      → 之后 PlayerData 状态机自己 PrepareSpawn → WaitForTerrain → SpawnPlayer（数秒，PlayerData.cs:136-323）。
    ///   ② 移除：<c>SubsystemPlayers.RemovePlayerData(playerData)</c>
    ///      （SubsystemPlayers.cs:77-90：触发 PlayerRemoved → 摘 GameWidget + 删实体 + Dispose）。
    ///   ③ 绑定：复用 <c>MirrorCharacterEngine.TryBind</c>（replace = 相机绑客户端化身 + 宿主身体隐藏不可碰撞 +
    ///      每拍 ViewFeedback；copy = 逐拍复制）。**只在宿主 <c>IsReadyForPlaying &amp;&amp; ComponentPlayer != null</c>
    ///      之后绑**（PlayerData.cs:124-134）。
    ///
    /// 谁是"主机真实本地角色"：<c>SubsystemPlayers.PlayersData</c> 里**不是**联机化身的那些
    /// （<c>NetworkAvatarSpawner.Contains</c>，与 <c>TryCollectLocalPlayers</c> 同一口径）。
    /// 远端化身已被 NetworkAvatarSpawner 移出 PlayersData（也摘掉了 GameWidget），所以它们不占池；
    /// 自动创建的宿主**在** PlayersData 里，所以它同样占池（4 个上限不放宽）。
    ///
    /// auto / 手动角色的区分（决定"能不能删"）：
    ///   · 本会话新建或收编的 → 会话归属表（<c>m_sessionOwned</c>）里有它；
    ///   · 世界里**预先存在**的（世界存档里的角色）：只有**注册表**（`RegistrySubsystem`）里
    ///     `AutoHost&lt;PlayerIndex&gt;` = "1"、且名字是本功能的前缀命名 <c>&lt;前缀&gt;#&lt;N&gt;</c>
    ///     时才算 auto；其余一律按手动角色处理，**绝不**在客户端离开时删除。
    ///
    /// 持久化口径（写进 doc/MIRROR-CHARACTER.md）：
    ///   · 宿主是真实本地角色 ⇒ 在 <c>SubsystemPlayers.Save</c> 的遍历范围内，**会进世界存档**；
    ///   · 本功能在**注册表子系统**（注入 `Game.SubsystemPlayers`）的 ValuesDictionary 里给它打标记
    ///     （"离开时应被移除、可被复用"）；
    ///   · 客户端离开 / 关配置 / 卸载时**只删**带标记的 auto 角色；
    ///   · 世界卸载 / 主机退出时无法在已 Dispose 的 Project 上安全移除 ⇒ 下次进世界的第一拍
    ///     由 <see cref="ReconcileOrphans"/> 补做（只删带标记的前缀命名角色）。
    ///
    /// 本类**无静态可变状态**：化身表 / 镜像引擎 / 诊断由组合根注入。
    /// </summary>
    public sealed class MirrorHostEngine : IMirrorHostPort, IMirrorHostClientSource, IDisposable
    {
        private const string OwnerId = "mirror-host";

        /// <summary>
        /// 注册表里标记"这是本功能自动创建的宿主"的键（值 "1"；离开时移除、可复用）。
        /// 键名带前缀，绝不与引擎自带键冲突。
        /// </summary>
        public const string AutoFlagKey = "SuMultiplayerAutoMirrorHost";

        /// <summary>注册表里记录归属客户端（排查 / 复用参考）的键（值 = clientId 字符串）。</summary>
        public const string AutoClientKey = "SuMultiplayerMirrorHostClient";

        /// <summary>本会话里被本功能创建 / 收编的宿主（clientId → PlayerData）。</summary>
        private sealed class Host
        {
            public int ClientId;
            public PlayerData PlayerData;
        }

        private readonly NetworkAvatarSpawner m_avatars;

        /// <summary>
        /// 自动宿主卡死 → 触发引擎复活次数 / 失败次数，以及命中卡死判定的次数。
        /// 三个都是**引擎侧累计值**：维护路径（<see cref="TryMaintainAutoHosts"/>）与
        /// 绑定前修复路径（<see cref="TryBind"/> → <see cref="TryReviveStuckAutoHost"/>）都会加，
        /// 再按增量进 <see cref="MirrorHostMaintenance"/> 上报（不会因为走了另一条入口就漏计）。
        /// </summary>
        private long m_revives;
        private long m_reviveFailures;
        private long m_stuckDetected;

        /// <summary>已经上报过的累计值（增量上报用，避免同一件事被算两次）。</summary>
        private long m_reportedStuck;
        private long m_reportedRevives;
        private long m_reportedReviveFailures;

        /// <summary>免疫 / 卡死修复里被闸门拒掉的目标数（联机化身 / 不在账本 / 非自动命名）。</summary>
        private long m_immunityRefusals;

        /// <summary>读宿主 PlayerData 状态机失败次数（读不到就记失败，不当成"没卡死"）。</summary>
        private long m_stateReadFailures;

        private readonly MirrorCharacterEngine m_mirror;
        private readonly MirrorHostImmunityEngine m_immunity;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly Dictionary<int, Host> m_sessionOwned = new Dictionary<int, Host>();
        private readonly HashSet<string> m_reportedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<MirrorHostAutoHost> m_autoHosts = new List<MirrorHostAutoHost>();
        private readonly List<PlayerData> m_localPlayers = new List<PlayerData>();

        private string m_namePrefix;

        public MirrorHostEngine(NetworkAvatarSpawner avatars, MirrorCharacterEngine mirror,
            MirrorHostImmunityEngine immunity, IModuleDiagnostics diagnostics)
        {
            m_avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            m_mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
            m_immunity = immunity ?? throw new ArgumentNullException(nameof(immunity));
            m_diagnostics = diagnostics;
        }

        /// <summary>
        /// 免疫写入引擎动作（组合根注入；本类不用反射入口，源码守卫按此分文件）。
        /// </summary>
        public MirrorHostImmunityEngine Immunity => m_immunity;

        /// <summary>
        /// 自动角色的前缀（由组合根按配置设置）。只用于"预先存在的 auto 角色"识别
        /// （&lt;前缀&gt;#&lt;N&gt; 命名 + auto 标记，两道条件缺一不可）。
        /// </summary>
        public string NamePrefix
        {
            get => m_namePrefix ?? MirrorHostSettings.DefaultNamePrefix;
            set => m_namePrefix = value;
        }

        // ------------------------------------------------------------------ 端口：只读状态

        /// <summary>
        /// 主机真实本地角色数 = <c>SubsystemPlayers.PlayersData.Count</c>（不含联机化身）。
        /// 世界没加载 = 0（纯逻辑只在世界内调用）。
        /// </summary>
        public int HostLocalPlayerCount => CollectLocalPlayers()?.Count ?? 0;

        public int AutoHostCount
        {
            get
            {
                try
                {
                    List<MirrorHostAutoHost> hosts = SnapshotAutoHosts();
                    return hosts?.Count ?? 0;
                }
                catch (Exception e)
                {
                    WarnOnce("count:" + e.GetType().Name, "统计自动镜像宿主失败：" + e.Message);
                    return 0;
                }
            }
        }

        public int BoundClientId => m_mirror.IsBound ? m_mirror.BoundClientId : -1;

        /// <summary>世界模式（读 <c>SubsystemGameInfo.WorldSettings.GameMode</c>）。</summary>
        public MirrorHostWorldMode WorldMode
        {
            get
            {
                Project project = GameManager.Project;
                if (project == null) return MirrorHostWorldMode.Normal;
                SubsystemGameInfo info = project.FindSubsystem<SubsystemGameInfo>(false);
                if (info?.WorldSettings == null)
                {
                    WarnOnce("no-game-info", "世界缺少 SubsystemGameInfo：无法判断 Cruel / Adventure，" +
                        "按 Normal 处理（池计算不受影响）");
                    return MirrorHostWorldMode.Normal;
                }
                return MirrorHostPool.FromGameModeName(info.WorldSettings.GameMode.ToString());
            }
        }

        // ------------------------------------------------------------------ 端口：列举

        public bool TryListLocalPlayers(List<MirrorHostLocalPlayer> players, out string reason)
        {
            reason = null;
            if (players == null)
            {
                reason = "调用方没有给列表";
                return false;
            }
            players.Clear();
            var local = CollectLocalPlayers();
            if (local == null)
            {
                reason = "没有加载中的 Project / SubsystemPlayers（还没进入世界）";
                return false;
            }
            for (int i = 0; i < local.Count; i++)
            {
                PlayerData playerData = local[i];
                bool hasComponentPlayer = playerData.ComponentPlayer != null;
                players.Add(new MirrorHostLocalPlayer(playerData.Name ?? string.Empty,
                    playerData.PlayerIndex, hasComponentPlayer && playerData.IsReadyForPlaying,
                    hasComponentPlayer, IsAutoHost(playerData)));
            }
            return true;
        }

        public bool TryListAutoHosts(List<MirrorHostAutoHost> hosts, out string reason)
        {
            reason = null;
            if (hosts == null)
            {
                reason = "调用方没有给列表";
                return false;
            }
            hosts.Clear();
            List<MirrorHostAutoHost> snapshot = SnapshotAutoHosts();
            if (snapshot == null)
            {
                // 世界已卸载 / 还没进世界：这属于**正常状态**而不是失败 —— 没有世界就等于没有自动宿主。
                // 返回 true + 空列表，避免控制器把"切到 Play 屏"计成 mirrorHostFailures（曾每帧累加）。
                reason = "没有加载中的 Project / SubsystemPlayers（还没进入世界；按空列表处理）";
                return true;
            }
            hosts.AddRange(snapshot);
            return true;
        }

        public bool TryListClientIds(List<int> clientIds, out string reason)
        {
            if (clientIds == null)
            {
                reason = "调用方没有给客户端列表";
                return false;
            }
            clientIds.Clear();
            var seen = new HashSet<int>();
            foreach (PlayerKey key in m_avatars.Keys)
            {
                if (key.ClientId <= 0 || !seen.Add(key.ClientId)) continue;
                clientIds.Add(key.ClientId);
            }
            clientIds.Sort();
            reason = clientIds.Count == 0 ? "当前没有已加入的客户端（还没有联机化身）" : null;
            return true;
        }

        // ------------------------------------------------------------------ 端口：创建 / 收编 / 移除

        public bool TryCreate(int clientId, string name, out MirrorHostAutoHost host, out string reason)
        {
            host = default(MirrorHostAutoHost);
            reason = null;
            if (clientId <= 0)
            {
                reason = "clientId=" + clientId.ToString(CultureInfo.InvariantCulture) +
                    " 非法（主机自己 0 不需要镜像宿主）";
                return false;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                reason = "自动角色名为空";
                return false;
            }
            if (m_sessionOwned.ContainsKey(clientId))
            {
                reason = "客户端 " + clientId + " 已经有本会话创建的镜像宿主";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（世界已卸载）";
                return false;
            }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null)
            {
                reason = "世界里找不到 SubsystemPlayers";
                return false;
            }
            // 引擎上限：AddPlayerData 超过 4 会抛 "Too many players." —— 先在这里明确拒绝（不靠异常）。
            if (players.PlayersData.Count >= SubsystemPlayers.MaxPlayers)
            {
                reason = "本地角色已到引擎上限 " + players.PlayersData.Count + "/" +
                    SubsystemPlayers.MaxPlayers + "（SubsystemPlayers.MaxPlayers）";
                return false;
            }
            foreach (PlayerData existing in players.PlayersData)
            {
                if (existing == null) continue;
                if (!string.Equals((existing.Name ?? string.Empty).Trim(), name.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                reason = "世界里已经有叫「" + name + "」的本机角色（不覆盖、不重名）";
                return false;
            }

            PlayerData playerData;
            try
            {
                // ① 引擎官方链路：new PlayerData → 设名字 / 输入设备 → AddPlayerData。
                // Source: Survivalcraft/Game/PlayerScreen.cs:121,223
                playerData = new PlayerData(project);
            }
            catch (Exception e)
            {
                reason = "new PlayerData 失败：" + e.GetType().Name + " " + e.Message;
                m_diagnostics?.Error(OwnerId, reason, e);
                return false;
            }
            playerData.Name = name.Trim();
            // 自动角色不做游戏逻辑代理：输入设备 = None（它只作为"客户端画面宿主"）。
            playerData.InputDevice = WidgetInputDevice.None;
            // ② 标记（写进它自己的 ValuesDictionary：SubsystemPlayers.Save 写的就是这一份）—— 先标记后加入，
            //    这样即使 AddPlayerData 刚触发 PlayerAdded 就被引擎存档，标记也已经在了。
            MarkAuto(playerData, clientId);
            try
            {
                players.AddPlayerData(playerData);
            }
            catch (Exception e)
            {
                reason = "SubsystemPlayers.AddPlayerData 失败：" + e.GetType().Name + " " + e.Message;
                m_diagnostics?.Error(OwnerId,
                    "为客户端 " + clientId + " 创建本地角色「" + playerData.Name + "」失败：" + reason, e);
                return false;
            }
            m_sessionOwned[clientId] = new Host
            {
                ClientId = clientId,
                PlayerData = playerData
            };
            host = new MirrorHostAutoHost(clientId, playerData.Name ?? name, playerData.PlayerIndex,
                IsReady(playerData));
            Log("镜像宿主已创建：客户端 " + clientId + " → 本地角色「" + playerData.Name +
                "」（PlayerIndex=" + playerData.PlayerIndex +
                " · InputDevice=None · 引擎自动建 GameWidget / 切分屏布局；" +
                "PlayerData 状态机随后自己 PrepareSpawn/WaitForTerrain/SpawnPlayer）");
            return true;
        }

        public bool TryAdopt(int clientId, string name, out MirrorHostAutoHost host, out string reason)
        {
            host = default(MirrorHostAutoHost);
            reason = null;
            if (clientId <= 0)
            {
                reason = "clientId=" + clientId.ToString(CultureInfo.InvariantCulture) + " 非法";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（世界已卸载）";
                return false;
            }
            if (!TryFindLocalPlayer(project, name, out PlayerData found, out string findReason))
            {
                reason = findReason;
                return false;
            }
            if (!IsAutoHost(found))
            {
                reason = "「" + (found.Name ?? name) +
                    "」不是本功能自动创建的角色（手动角色绝不归入 auto 账本）";
                return false;
            }
            if (m_sessionOwned.TryGetValue(clientId, out Host owned) &&
                !ReferenceEquals(owned.PlayerData, found))
            {
                reason = "客户端 " + clientId + " 已经收编了另一个自动角色「" +
                    (owned.PlayerData?.Name ?? "?") + "」";
                return false;
            }
            MarkAuto(found, clientId);
            m_sessionOwned[clientId] = new Host
            {
                ClientId = clientId,
                PlayerData = found
            };
            host = new MirrorHostAutoHost(clientId, found.Name ?? name, found.PlayerIndex,
                IsReady(found));
            Log("镜像宿主复用：客户端 " + clientId + " → 世界里已有的自动角色「" + found.Name +
                "」（PlayerIndex=" + found.PlayerIndex + "；不新建）");
            return true;
        }

        public bool TryRemove(int clientId, out string reason)
        {
            reason = null;
            if (!m_sessionOwned.TryGetValue(clientId, out Host owned) || owned.PlayerData == null)
            {
                reason = "客户端 " + clientId + " 没有本功能创建 / 收编的镜像宿主（手动角色绝不移除）";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（世界已卸载；角色随存档保留，下次进世界由 ReconcileOrphans 清理）";
                return false;
            }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null)
            {
                reason = "世界里找不到 SubsystemPlayers";
                return false;
            }
            PlayerData playerData = owned.PlayerData;
            if (!players.PlayersData.Contains(playerData))
            {
                m_sessionOwned.Remove(clientId);
                reason = "镜像宿主「" + (playerData.Name ?? "?") +
                    "」已经不在 PlayersData 里（被引擎 / 玩家删掉了）";
                return false;
            }
            // 最后一道闸门：只删**严格前缀命名**的角色（<前缀>#<clientId>[-N]）。
            // 手动创建的角色（h1/h2/H3 等）不满足该命名，永远不会被删。
            // 存档标记只作为佐证（旧版本可能没有标记），不作为唯一判据。
            if (!IsAutoName(playerData.Name))
            {
                reason = "镜像宿主「" + (playerData.Name ?? "?") +
                    "」不是本功能的自动命名（<前缀>#<clientId>）→ 拒绝移除";
                return false;
            }
            try
            {
                // 引擎官方移除链路：PlayerRemoved → 摘 GameWidget + 删实体 + Dispose。
                // Source: Survivalcraft/Game/SubsystemPlayers.cs:77-90
                players.RemovePlayerData(playerData);
            }
            catch (Exception e)
            {
                reason = "SubsystemPlayers.RemovePlayerData 失败：" + e.GetType().Name + " " + e.Message;
                m_diagnostics?.Error(OwnerId,
                    "移除镜像宿主「" + (playerData.Name ?? "?") + "」（client " + clientId +
                    "）失败：" + reason, e);
                return false;
            }
            m_sessionOwned.Remove(clientId);
            Log("镜像宿主已移除：客户端 " + clientId + " → 本地角色「" + (playerData.Name ?? "?") +
                "」（只移除本功能自动创建的角色；手动创建的角色不受影响）");
            return true;
        }

        // ------------------------------------------------------------------ 端口：绑定 / 解绑

        // ------------------------------------------------------------------ 端口：每拍维护（免疫 / 卡死修复）

        /// <summary>
        /// 自动宿主是否"卡死在死亡状态"（三条条件任一命中：引擎 PlayerData 停在
        /// <c>PlayerDead</c> / 相机是 <c>DeathCamera</c>（且不是客户端死亡的预期表现）/
        /// 血量 ≤ 0）。命中时用引擎链路重建：
        /// <c>Project.RemoveEntity(entity, disposeEntity:true)</c> 后 PlayerData 状态机会走
        /// PrepareSpawn → WaitForTerrain → SpawnPlayer，同一个 PlayerData 复活
        /// （<c>OnEntityRemoved</c> 先把 <c>ComponentPlayer</c> 置 null，
        /// Source: PlayerData.cs:423-429；状态机随后 PrepareSpawn，Source: PlayerData.cs:302-305）。
        /// 只对**本功能自动创建的宿主**调用；手动角色绝不动。
        /// 卡死且**已绑定镜像**时先解绑（把快照写回到还活着的实体），再移除实体 ——
        /// 否则解绑会把快照写回到已销毁的组件上。
        /// </summary>
        private bool TryReviveStuckAutoHost(Project project, PlayerData host, bool playerDataDead,
            out bool reviving, out bool reviveFailed, out string reason)
        {
            reviving = false;
            reviveFailed = false;
            reason = null;
            if (project == null || host == null) return false;
            ComponentPlayer player = host.ComponentPlayer;
            if (player == null) return false; // 还没出生：交给 IsReady 的 pending 路径
            ComponentHealth health = player.ComponentHealth;
            bool deadCamera = false;
            try { deadCamera = host.GameWidget != null && host.GameWidget.ActiveCamera is DeathCamera; }
            catch (Exception) { /* 没有 GameWidget 就不是"卡死相机"，按不卡死处理 */ }
            bool offHealth = health != null && health.Health <= 0f;
            // 客户端角色死亡时，第三分屏的 DeathCamera 是 MirrorCharacterEngine 主动切的
            // **预期死亡表现**（Source: Engine/MirrorCharacterEngine.cs:729-776），不是宿主卡死：
            // 不排除它的话，客户端每次死亡都会把宿主解绑 + RemoveEntity，第三屏反复重建。
            bool clientDeathPresentation = deadCamera && IsClientDeathPresentation(host);
            if (!MirrorHostStuck.IsStuck(offHealth, deadCamera, clientDeathPresentation,
                    playerDataDead, out string detail))
                return false;
            // 命中即计数（引擎侧累计值；维护路径与绑定前修复路径共用，增量上报到 mirrorHostStuckDead）。
            m_stuckDetected++;
            reviving = true;
            // 已绑定的镜像先解绑：MirrorCharacterEngine 持有宿主的组件引用，实体销毁后再 Restore
            // 会把绑定前快照写回到已销毁组件 / 已摘除的碰撞集合上。解绑失败就**不移除实体**。
            if (m_mirror.IsBound && string.Equals((m_mirror.BoundCharacterName ?? string.Empty).Trim(),
                    (host.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (!m_mirror.TryRestore(out MirrorRestoreOutcome restoreOutcome,
                        out string restoreReason))
                {
                    reviveFailed = true;
                    m_reviveFailures++;
                    reason = "自动镜像宿主「" + (host.Name ?? "?") + "」" + detail +
                        "，但先解绑镜像失败：" + (restoreReason ?? "原因未知") +
                        "；本拍不移除实体（避免把绑定快照写回到已销毁实体）";
                    m_diagnostics?.Error(OwnerId, reason, null);
                    return true;
                }
                Log("自动镜像宿主「" + (host.Name ?? "?") + "」" + detail +
                    " → 先解绑镜像（" + restoreOutcome.Describe() + "）再走引擎复活链路");
            }
            Entity entity = player.Entity;
            if (entity == null)
            {
                reason = "自动镜像宿主「" + (host.Name ?? "?") +
                    "」" + detail + " 但没有实体，等待引擎重建";
                return true;
            }
            try
            {
                project.RemoveEntity(entity, disposeEntity: true);
                m_revives++;
                Log("自动镜像宿主「" + (host.Name ?? "?") + "」" + detail + "（health=" +
                    (health == null ? "?" : health.Health.ToString("0.###")) +
                    "）→ 用引擎复活链路移除实体并等待重新出生（不硬改状态机）");
                reason = "自动镜像宿主卡死（" + detail + "）→ 已触发引擎复活，等待重新出生";
            }
            catch (Exception e)
            {
                reviveFailed = true;
                m_reviveFailures++;
                m_diagnostics?.Error(OwnerId, "自动镜像宿主「" + (host.Name ?? "?") +
                    "」复活失败（RemoveEntity 抛异常）：" + e.GetType().Name + " " + e.Message, e);
                reason = "自动镜像宿主复活失败（" + detail + "）：" + e.GetType().Name + " " +
                    e.Message;
            }
            return true;
        }

        /// <summary>
        /// 这台 <c>DeathCamera</c> 是不是"客户端角色死亡"的**预期表现**（而不是宿主自己卡死）。
        /// 判据（缺一不可）：镜像正绑在这个宿主上 + 客户端化身（槽位 0）的
        /// <c>ComponentHealth.Health &lt;= 0</c>。判定口径与
        /// <c>MirrorCharacterEngine.ValidateBound</c> 切 DeathCamera 的条件一致
        /// （Source: Engine/MirrorCharacterEngine.cs:729-776：replace + 客户端角色死亡 → DeathCamera，
        /// 客户端复活 → 切回 FppCamera）。
        /// 读不到客户端化身 / 没有 ComponentHealth = 不是预期表现（按宿主卡死处理，宁可修也不留黑屏）。
        /// </summary>
        private bool IsClientDeathPresentation(PlayerData host)
        {
            if (host == null || !m_mirror.IsBound) return false;
            if (!string.Equals((m_mirror.BoundCharacterName ?? string.Empty).Trim(),
                    (host.Name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!m_avatars.TryGet(m_mirror.BoundClientId, out PlayerData source) || source == null)
                return false;
            ComponentPlayer sourcePlayer = source.ComponentPlayer;
            ComponentHealth sourceHealth = sourcePlayer == null ? null : sourcePlayer.ComponentHealth;
            return sourceHealth != null && sourceHealth.Health <= 0f;
        }

        /// <summary>
        /// 读宿主 <c>PlayerData</c> 是否卡在 <c>PlayerDead</c>（读失败 = 计数 + 限流日志，
        /// 本拍按"没命中这条"继续，另外两条卡死条件仍然生效）。
        /// </summary>
        private bool TryReadPlayerDead(PlayerData host, out bool dead, out string failure)
        {
            dead = false;
            failure = null;
            if (m_immunity.TryReadPlayerDead(host, out dead, out failure)) return true;
            m_stateReadFailures++;
            WarnOnce("read-dead:" + (failure ?? "?"),
                "读自动镜像宿主「" + (host?.Name ?? "?") +
                "」的 PlayerData 状态机失败（本拍按「不在 PlayerDead」继续，health / 相机两条条件仍生效）：" +
                (failure ?? "原因未知") + " · 累计=" + m_stateReadFailures);
            return false;
        }

        /// <summary>
        /// 每拍维护**自动镜像宿主**（端口 <see cref="IMirrorHostPort.TryMaintainAutoHosts"/>）：
        ///   ① 只遍历宿主账本（<c>m_sessionOwned</c>）里、世界里还在的宿主；
        ///   ② 过 <see cref="MirrorHostImmunityTarget"/> 三道闸门（账本 / 自动命名 / 不是联机化身）——
        ///      手动角色（h1/h2/H3）与客户端化身**一个字段都不写**；
        ///   ③ 先按 <see cref="MirrorHostImmunityFields"/> 整套写免疫字段（免伤 / 免病 / 着火 / 体征归位）；
        ///   ④ 再判卡死（health≤0 或 DeathCamera（不是客户端死亡的预期表现）或 PlayerData=PlayerDead）
        ///      → 引擎复活链路，本拍不再算 immune（实体已被移除，等重新出生）。
        ///      顺序不能反：HUD 桥接同一拍更早把客户端 Health 写进宿主，先判会把"客户端死亡"
        ///      误当成"宿主死亡"。
        /// 返回 false = 引擎侧根本维护不了（世界没加载 / 找不到 SubsystemPlayers）；
        /// 单宿主单字段失败不算 false，记在 <see cref="MirrorHostMaintenance.ImmunityFailures"/>。
        /// </summary>
        public bool TryMaintainAutoHosts(out MirrorHostMaintenance outcome, out string reason)
        {
            outcome = default(MirrorHostMaintenance);
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（世界已卸载）";
                return false;
            }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null)
            {
                reason = "世界里找不到 SubsystemPlayers";
                return false;
            }
            string firstFailure = null;
            foreach (KeyValuePair<int, Host> pair in m_sessionOwned)
            {
                PlayerData host = pair.Value?.PlayerData;
                if (host == null) continue;
                if (!players.PlayersData.Contains(host)) continue; // 已不在世界里：本拍不碰
                if (!MirrorHostImmunityTarget.IsEligible(sessionOwned: true, IsAutoName(host.Name),
                        autoHostFlag: HasAutoFlag(host), isNetworkAvatar: m_avatars.Contains(host),
                        out string refused))
                {
                    m_immunityRefusals++;
                    // 闸门拒掉的目标**永不**写入，但也不能静默：同一原因只写一行（WarnOnce），
                    // 带上累计次数与角色名，排查时能直接看出是谁被跳过、为什么。
                    WarnOnce("refused:" + refused,
                        "自动宿主维护跳过一个目标（累计 refuses=" + m_immunityRefusals + "）：「" +
                        (host.Name ?? "?") + "」" + refused +
                        " · 只维护自动镜像宿主，手动角色与客户端化身永不写入");
                    if (firstFailure == null) firstFailure = refused;
                    continue;
                }
                ComponentPlayer player = host.ComponentPlayer;
                if (player == null)
                {
                    outcome.CountAwaitingSpawn();
                    continue; // 还没出生：等引擎 PlayerData 状态机 PrepareSpawn → SpawnPlayer
                }
                bool playerDataDead = false;
                if (!TryReadPlayerDead(host, out playerDataDead, out string readFailure))
                {
                    outcome.CountImmunityFailure();
                    if (firstFailure == null) firstFailure = readFailure;
                }
                // **先写免疫，再判卡死**：replace 模式的 HUD 桥接在同一拍更早的位置
                // （MirrorCharacterEngine.ValidateBound → TickHudBridge，Source:
                // Engine/MirrorCharacterEngine.cs:1544-1578）会把客户端化身的 Health / 体征写进宿主，
                // 客户端死亡时那是 0。若先判卡死，这个"刚被桥接写成 0"会被误判成宿主自己死了，
                // 客户端每次死亡都触发一轮解绑 + RemoveEntity，第三屏反复重建。
                // 先免疫后判定：health<=0 只剩一种含义 —— 免疫字段没写进去（已计数 + 带原因）。
                bool immune = m_immunity.TryApply(player, out _, out string applyFailure);
                if (!immune)
                {
                    outcome.CountImmunityFailure();
                    if (firstFailure == null) firstFailure = applyFailure;
                }
                if (TryReviveStuckAutoHost(project, host, playerDataDead, out bool reviving,
                        out bool reviveFailed, out string reviveReason))
                {
                    // 计不在这里加：卡死 / 复活 / 复活失败都在方法末尾按引擎侧累计值**增量**上报，
                    // 这样绑定前修复（TryBind → TryReviveStuckAutoHost）也会计入同一组计数。
                    if (reviveFailed && firstFailure == null) firstFailure = reviveReason;
                    else if (!reviveFailed && !reviving && firstFailure == null)
                        firstFailure = reviveReason;
                    continue;
                }
                if (immune) outcome.CountImmune();
            }
            // 增量上报（见上）：绑定前的卡死修复也进 mirrorHostStuckDead / mirrorHostRevived /
            // mirrorHostReviveFailures；同一件事只算一次（已上报值随累计值一起前进）。
            outcome.StuckDetected += TakeDelta(ref m_reportedStuck, m_stuckDetected);
            outcome.Revived += TakeDelta(ref m_reportedRevives, m_revives);
            outcome.ReviveFailures += TakeDelta(ref m_reportedReviveFailures, m_reviveFailures);
            if (firstFailure != null) reason = firstFailure;
            return true;
        }

        /// <summary>
        /// 累计值 → 本次增量（并把"已上报"推进到累计值）。超过 int 上限时按上限报一次，
        /// 剩下的留到下一次（不会溢出成负数）。
        /// </summary>
        private static int TakeDelta(ref long reported, long total)
        {
            if (total <= reported) return 0;
            long delta = total - reported;
            if (delta > int.MaxValue)
            {
                reported += int.MaxValue;
                return int.MaxValue;
            }
            reported = total;
            return (int)delta;
        }

        public bool TryBind(int clientId, string name, MirrorCharacterMode mode, out bool pending,
            out string reason)
        {
            pending = false;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（世界已卸载）";
                return false;
            }
            if (!TryFindLocalPlayer(project, name, out PlayerData host, out string findReason))
            {
                reason = findReason;
                return false;
            }
            // 自动宿主是"一次性"的：出生在冬夜/危险环境可能在绑定前被冻死/打死，
            // 而绑定后的 HUD 桥接会把血量写回客户端值 → 引擎 PlayerData 状态机却停在
            // PlayerDead（实测 Mirror#4：state=PlayerDead、hp=1）→ ViewWidget 不再绘制，
            // 第三屏永久黑屏。这里用**引擎自己的复活链路**重建它（移除实体 → PlayerData
            // 检测到 ComponentPlayer==null → PrepareSpawn → 重新出生），不硬改状态机字段。
            // 与每拍维护（TryMaintainAutoHosts）同一条判定：三条卡死条件任一命中
            // （DeathCamera 那条排除"客户端角色死亡的预期表现"）。修复次数进引擎累计值，
            // 由同一拍的 TryMaintainAutoHosts 增量上报，不会因为走绑定入口就漏计。
            bool playerDataDead = TryReadPlayerDead(host, out bool deadState, out _) && deadState;
            if (TryReviveStuckAutoHost(project, host, playerDataDead, out bool reviving,
                    out _, out string reviveReason))
            {
                pending = true;
                reason = reviveReason;
                return false;
            }
            if (!IsReady(host))
            {
                pending = true;
                reason = "宿主「" + (host.Name ?? name) + "」还没就绪（IsReadyForPlaying=" +
                    host.IsReadyForPlaying + " · ComponentPlayer=" +
                    (host.ComponentPlayer != null ? "有" : "无") + "）";
                return false;
            }
            if (!m_avatars.TryGet(new PlayerKey(clientId, 0), out PlayerData source) || source == null)
            {
                reason = "客户端 " + clientId + " 的联机角色（槽位 0）还没建好";
                return false;
            }
            ComponentPlayer sourcePlayer = source.ComponentPlayer;
            if (sourcePlayer == null || sourcePlayer.ComponentBody == null ||
                sourcePlayer.ComponentCreatureModel == null)
            {
                pending = true;
                reason = "客户端 " + clientId +
                    " 的联机角色还没就绪（ComponentPlayer / 身体 / 模型）";
                return false;
            }
            if (m_mirror.IsBound)
            {
                if (m_mirror.BoundClientId == clientId &&
                    string.Equals((m_mirror.BoundCharacterName ?? string.Empty).Trim(),
                        (host.Name ?? name).Trim(), StringComparison.OrdinalIgnoreCase))
                    return true; // 已经绑在这个客户端上了（幂等）
                reason = "镜像引擎已经绑定了另一个角色（「" + m_mirror.BoundCharacterName +
                    "」← client " + m_mirror.BoundClientId + "）；先解绑再绑新的";
                return false;
            }
            MirrorCharacterPlan plan = MirrorCharacterPlan.For(mode, targetReady: true,
                sourceReady: true);
            if (plan.IsEmpty)
            {
                reason = "绑定计划为空（模式 / 就绪状态不满足）：" + plan.Describe();
                return false;
            }
            // 自动宿主由 MirrorHostImmunity 托管：绑定期间宿主 Health / 体征每拍末尾会被钉回安全值，
            // 所以不写宿主权威组件，第三分屏底部 HUD 只写表现层（数据源 = 客户端化身的权威值）。
            // 手动角色仍走 IMirrorCharacterPort.TryBind（只保留 IsInvulnerable 防死）。
            if (!m_mirror.TryBindPresentationOnly(host.Name, clientId, plan,
                    out MirrorBindOutcome outcome, out string bindReason))
            {
                reason = bindReason ?? "镜像引擎绑定失败";
                return false;
            }
            Log("镜像宿主已绑定：「" + host.Name + "」← client " + clientId + " · plan=" +
                plan.Describe() + " → " + outcome.Describe());
            return true;
        }

        public bool TryRestore(string reason, out string failureReason)
        {
            failureReason = null;
            if (!m_mirror.IsBound) return true;
            string character = m_mirror.BoundCharacterName;
            if (!m_mirror.TryRestore(out MirrorRestoreOutcome outcome, out string restoreReason))
            {
                failureReason = restoreReason ?? "镜像引擎解绑失败";
                return false;
            }
            Log("镜像宿主已解绑（" + reason + "）：「" + character + "」· " + outcome.Describe());
            return true;
        }

        public bool ValidateBound(out string reason) => m_mirror.ValidateBound(out reason);

        // ------------------------------------------------------------------ 世界卸载 / 进世界补做

        /// <summary>
        /// 世界卸载（<c>GameManager.ProjectDisposed</c>，在 <c>Project.Dispose</c> **之前**触发）：
        /// 趁宿主与客户端化身都还活着把镜像解绑、写回快照。
        /// **不**在这里调用 RemovePlayerData：引擎随后会把整个 Project 连同子系统一起 Dispose
        /// （Source: EntitySystem/GameEntitySystem/Project.cs:285-302），此时再动子系统只会引入无意义的异常；
        /// 会话账本在下一拍进世界时由 <see cref="ReconcileOrphans"/> 接管。
        /// </summary>
        public void OnProjectDisposed(Project project)
        {
            TryRestoreDuringDispose("世界卸载");
        }

        /// <summary>应用退出 / Mod 卸载：同样先解绑（宿主实体随后随进程 / 世界消失）。</summary>
        public void Detach(string reason) => TryRestoreDuringDispose(reason ?? "模块卸载");

        private void TryRestoreDuringDispose(string reason)
        {
            if (!m_mirror.IsBound) return;
            try
            {
                if (!m_mirror.TryRestore(out MirrorRestoreOutcome outcome, out string restoreReason))
                    m_diagnostics?.Warn(OwnerId,
                        reason + "时解绑镜像宿主失败：" + (restoreReason ?? "原因未知"));
                else
                    Log(reason + " → 已解绑镜像宿主 · " + outcome.Describe());
            }
            catch (Exception e)
            {
                m_diagnostics?.Warn(OwnerId,
                    reason + "时解绑镜像宿主抛异常：" + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 进入（新）世界后的第一拍调用一次：清掉**上一次会话创建、但世界存档里还留着**的 auto 角色
        /// （客户端离开那一刻世界已经卸载，RemovePlayerData 做不了 —— 只能在这里补做）。
        /// 只删带 <see cref="AutoFlagKey"/> 标记、且名字是前缀命名的角色；手动角色绝不触及。
        /// 配置关闭时不删（保持"关闭 = 零引擎动作"），只写一条可供手工清理的日志。
        /// </summary>
        public void ReconcileOrphans(Action<string> info, bool enabled)
        {
            m_sessionOwned.Clear();
            List<PlayerData> local = CollectLocalPlayers();
            if (local == null || local.Count == 0) return;
            var orphans = new List<PlayerData>();
            for (int i = 0; i < local.Count; i++)
            {
                PlayerData playerData = local[i];
                // 判据：**严格**的 <前缀>#<数字>[-N] 命名即视为本功能创建（旧版本没有写存档标记，
                // 只用标记会把它们当成"手动角色"永久留下，导致每次进世界都多一个孤儿屏）。
                // 手动角色（h1/h2/H3…）不匹配该命名模式，绝不会被这里选中。
                if (!IsAutoName(playerData.Name)) continue;
                orphans.Add(playerData);
            }
            for (int i = 0; i < orphans.Count; i++)
            {
                PlayerData orphan = orphans[i];
                string name = orphan.Name ?? "?";
                if (!enabled)
                {
                    info?.Invoke("[SuMP][mirror-host] 世界里还留着上一次会话的自动角色「" + name +
                        "」，但 mirrorcharacterauto=false → 不自动删除（关闭 = 零引擎动作）；" +
                        "需要清理请手动在 Edit Players 里删除");
                    continue;
                }
                Project project = GameManager.Project;
                SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(false);
                if (players == null) return;
                try
                {
                    players.RemovePlayerData(orphan);
                    info?.Invoke("[SuMP][mirror-host] 清理上一次会话留下的自动角色「" + name +
                        "」（PlayerIndex=" + orphan.PlayerIndex +
                        "；只删带 auto 标记的前缀命名角色，手动角色不受影响）");
                }
                catch (Exception e)
                {
                    m_diagnostics?.Error(OwnerId,
                        "清理上一次会话留下的自动角色「" + name + "」失败：" + e.GetType().Name + " " +
                        e.Message, e);
                }
            }
        }

        public void Dispose() => m_sessionOwned.Clear();

        // ------------------------------------------------------------------ 内部

        /// <summary>主机真实本地角色（按 PlayerIndex 升序；排除联机化身）。世界没加载 = null。</summary>
        private List<PlayerData> CollectLocalPlayers()
        {
            Project project = GameManager.Project;
            if (project == null) return null;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return null;
            m_localPlayers.Clear();
            foreach (PlayerData playerData in players.PlayersData)
            {
                if (playerData == null || m_avatars.Contains(playerData)) continue;
                m_localPlayers.Add(playerData);
            }
            m_localPlayers.Sort((left, right) => left.PlayerIndex.CompareTo(right.PlayerIndex));
            return m_localPlayers;
        }

        /// <summary>当前世界里被本功能创建 / 收编的镜像宿主（按 clientId 升序）。</summary>
        private List<MirrorHostAutoHost> SnapshotAutoHosts()
        {
            List<PlayerData> local = CollectLocalPlayers();
            if (local == null) return null;
            m_autoHosts.Clear();
            foreach (KeyValuePair<int, Host> pair in m_sessionOwned)
            {
                PlayerData playerData = pair.Value?.PlayerData;
                if (playerData == null) continue;
                bool present = false;
                for (int i = 0; i < local.Count; i++)
                {
                    if (!ReferenceEquals(local[i], playerData)) continue;
                    present = true;
                    break;
                }
                if (!present) continue;
                m_autoHosts.Add(new MirrorHostAutoHost(pair.Key, playerData.Name ?? string.Empty,
                    playerData.PlayerIndex, IsReady(playerData)));
            }
            m_autoHosts.Sort((left, right) => left.ClientId.CompareTo(right.ClientId));
            return new List<MirrorHostAutoHost>(m_autoHosts);
        }

        private static bool IsReady(PlayerData playerData) =>
            playerData != null && playerData.ComponentPlayer != null && playerData.IsReadyForPlaying;

        /// <summary>
        /// 是不是本功能自动创建的角色：会话归属表里有它，或它自己的 ValuesDictionary 带 auto 标记
        /// **且**名字是本功能的前缀命名（两道条件缺一不可，避免误判 / 误删玩家自定义角色）。
        /// </summary>
        private bool IsAutoHost(PlayerData playerData)
        {
            if (playerData == null) return false;
            foreach (Host host in m_sessionOwned.Values)
                if (host.PlayerData != null && ReferenceEquals(host.PlayerData, playerData)) return true;
            return IsAutoName(playerData.Name); // 名字前缀是主判据（旧存档可能没有 auto 标记）
        }

        /// <summary>
        /// 这个本地角色是否被标记为"本功能自动创建"（写在世界存档里的
        /// `<see cref="RegistrySubsystem"/>` 的 ValuesDictionary，按 <c>PlayerIndex → clientId</c>）。
        /// 读不到注册表 / 没有条目 = 不是 auto（按手动角色处理，绝不删除）。
        /// </summary>
        private bool HasAutoFlag(PlayerData playerData)
        {
            if (playerData == null) return false;
            Project project = GameManager.Project;
            SuSubsystemPlayers registry = project?.FindSubsystem<SuSubsystemPlayers>(false);
            if (registry == null) return false;
            return registry.TryGetAutoHost(playerData.PlayerIndex, out _);
        }

        /// <summary>名字是否本功能的前缀命名（<c>&lt;前缀&gt;#&lt;N&gt;</c>，前缀取配置）。</summary>
        private bool IsAutoName(string name) =>
            MirrorHostNaming.ParseClientId(name, NamePrefix) > 0;

        private void MarkAuto(PlayerData playerData, int clientId)
        {
            Project project = GameManager.Project;
            SuSubsystemPlayers registry = project?.FindSubsystem<SuSubsystemPlayers>(false);
            if (registry == null)
            {
                // 注册表子系统不可用 = 世界存档里认不出这个 auto 角色 → 明确失败（不假装标记成功）。
                // 本次会话仍按内存账本工作；下次进世界它会按"手动角色"保留（不会自动删除）。
                m_diagnostics?.Error(OwnerId,
                    "世界里找不到镜像宿主注册表子系统（SuSubsystemPlayers）→ auto 标记写不进世界存档：" +
                    "宿主「" + (playerData?.Name ?? "?") + "」本次会话仍可用，但下次进世界会按手动角色保留", null);
                return;
            }
            string reason;
            if (!registry.MarkAutoHost(playerData.PlayerIndex, clientId, out reason))
            {
                m_diagnostics?.Error(OwnerId,
                    "写 auto 标记失败（宿主「" + (playerData?.Name ?? "?") + "」PlayerIndex=" +
                    playerData.PlayerIndex + "）：" + reason, null);
            }
        }

        private bool TryFindLocalPlayer(Project project, string name, out PlayerData found,
            out string reason)
        {
            found = null;
            reason = null;
            List<PlayerData> local = CollectLocalPlayers();
            if (local == null)
            {
                reason = "世界里找不到 SubsystemPlayers";
                return false;
            }
            int matches = 0;
            for (int i = 0; i < local.Count; i++)
            {
                PlayerData playerData = local[i];
                if (!string.Equals((playerData.Name ?? string.Empty).Trim(),
                        (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                matches++;
                if (found == null) found = playerData;
            }
            if (matches == 0)
            {
                reason = "主机本机角色里没有名为「" + name + "」的角色";
                return false;
            }
            if (matches > 1)
            {
                reason = "有 " + matches + " 个本机角色都叫「" + name + "」（不猜是哪一个）";
                found = null;
                return false;
            }
            return true;
        }

        private void Log(string message) => m_diagnostics?.Info(OwnerId, message);

        private void WarnOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            m_diagnostics?.Warn(OwnerId, message);
        }

    }
}
