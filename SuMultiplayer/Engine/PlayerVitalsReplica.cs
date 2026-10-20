using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 玩家生命 / 体征 / 死亡状态同步的**引擎适配层**（<see cref="IPlayerVitalsPort"/> + 组合根注入）。
    ///
    /// 2026-10 产品口径：**主机权威**。
    ///   · 主机自己模拟**每一个**角色：主机本机角色 + 主机侧每个客户端化身。
    ///     <see cref="CaptureAuthorityVitals"/> / <see cref="PollAuthoritySlots"/> 只读这些组件，
    ///     主机**不再**摘掉远端化身的 ComponentHealth / ComponentVitalStats / ComponentSleep
    ///     Update，也**不再**对账回滚主机侧伤害；生物 / 爆炸 / 摔落 / 溺水 / 火焰 / 岩浆 /
    ///     饥饿 / 疾病在主机上按单机规则生效。
    ///   · 客户端**绝不**上报；客户端本机角色的
    ///     ComponentHealth / ComponentVitalStats / ComponentSleep / ComponentFlu /
    ///     ComponentSickness / ComponentOnFire 的 Update 被
    ///     <see cref="DisableLocalSimulation"/> 从 <c>SubsystemUpdate</c> 摘掉，并把
    ///     <c>IsInvulnerable</c> 置 true；值只由 <see cref="ApplyLocalVitals"/> 从主机流写入。
    ///     客户端上的远端化身仍由 <see cref="SuppressRemoteAvatars"/> 关掉模拟。
    ///   · <see cref="ApplyLocalVitals"/> / <see cref="ApplyLocalVitalEvent"/> 只做字段赋值 +
    ///     HUD / 受伤表现 / 死亡提示 / 复活落点，**绝不**调用 <c>Injure</c> / <c>Heal</c>、
    ///     绝不掉物品、绝不删实体、绝不本地复活。
    ///   · 客户端 HUD 的**温度条贴图 / 冰冻覆盖 / 血条食物条**由
    ///     <see cref="RefreshLocalPresentation"/> **每帧**补写：`ComponentVitalStats.Update` 被摘掉后
    ///     没有任何引擎代码再写它们（温度条 BarSubtexture 只在 UpdateTemperature 里写，
    ///     IceFactor 又被 ComponentScreenOverlays.Update 每帧清 0）。
    ///   · 主机裁决客户端化身的复活：<see cref="TryRespawnAvatar"/> 按该角色自己的出生点复位。
    ///   · H3 调试镜像（Engine/MirrorCharacterEngine.cs）读主机侧客户端化身的真实组件；
    ///     主机不抑制它们，所以 H3 读到的是权威状态。
    ///
    /// 引擎事实：
    ///   · 死亡视觉 / 掉落只在 <c>ComponentHealth.Update</c> 的
    ///     <c>Health == 0 &amp;&amp; HealthChange &lt; 0</c> 分支做（ComponentHealth.cs:267-278）；
    ///     主机化身保留 Update，所以这些表现由引擎照常完成；
    ///   · <c>ComponentCreatureModel.Update</c> 看到 <c>Health == 0</c> 会把 <c>DeathPhase</c>
    ///     推到 1；<c>ComponentHumanModel.Update</c> 也按 <c>Health &lt;= 0</c> 或
    ///     <c>ComponentSleep.IsSleeping</c> 躺下；
    ///   · 客户端本机角色死亡时，引擎 <c>PlayerData</c> 的 Playing 状态一旦看到
    ///     <c>Health &lt;= 0</c> 就会进入死亡屏，并在点击后**删除本机实体并本地复活** ——
    ///     产品要求死亡由主机裁决，所以本机 ComponentHealth 保留一个非致命的极小值
    ///     <see cref="LocalDeathHealthFloor"/>（血条 / 死亡提示显示主机下发的 0 与死因），
    ///     只有主机 Respawn 事件才把本机角色按主机落点摆回去。
    ///
    /// 无静态状态；所有上限与失败都有计数（<see cref="Describe"/>）与限流日志。
    /// </summary>
    public sealed class PlayerVitalsReplica : IPlayerVitalsPort, IPlayerSleepPort
    {
        /// <summary>同一条问题日志的最小间隔（毫秒）。</summary>
        private const long ProblemLogMs = 10000;

        /// <summary>复活 / 死亡默认生命值（引擎 PlayerData.SpawnPlayer 之后就是 1）。</summary>
        private const float FullHealth = 1f;

        /// <summary>
        /// 客户端本机角色"已死亡"时写进 ComponentHealth 的非致命极小值：让引擎 PlayerData
        /// 不进入"本地删实体 → 本地复活"流程，同时血条 / 死亡提示仍按主机下发的 0 显示。
        /// </summary>
        private const float LocalDeathHealthFloor = 0.001f;

        /// <summary>引擎的默认目标体温（<c>ComponentVitalStats.m_targetTemperature</c> 初值 8→12 口径见旧实现）。</summary>
        private const float DefaultTemperature = 12f;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>玩家键 → 已摘除模拟的远端化身实体标识（重建后标识变化 → 重新抑制）。</summary>
        private readonly Dictionary<PlayerKey, int> m_suppressedTokens =
            new Dictionary<PlayerKey, int>();
        /// <summary>本机槽位 → 已摘除生命 / 体征 / 睡眠 / 病程 / 火焰模拟的实体标识。</summary>
        private readonly Dictionary<int, int> m_disabledLocalSlots = new Dictionary<int, int>();
        /// <summary>本机槽位 → 客户端自己维护的火焰粒子（ComponentOnFire.Update 已摘掉，表现由这里驱动）。</summary>
        private readonly Dictionary<int, OnFireParticleSystem> m_localFireParticles =
            new Dictionary<int, OnFireParticleSystem>();
        /// <summary>本机槽位 → 主机是否已裁决死亡（显示死亡提示；引擎里保留非致命极小值）。</summary>
        private readonly HashSet<int> m_localDeadSlots = new HashSet<int>();

        /// <summary>
        /// 本机槽位 → HUD 显示缓存（主机权威值派生 + 已经写进控件的温度贴图档位）。
        /// 客户端本机 <c>ComponentVitalStats.Update</c> 已摘掉：温度条 <c>BarSubtexture</c>、
        /// 冰冻覆盖 <c>IceFactor</c>、血条 / 食物条的值都没有任何引擎代码再写 —— 由这里补。
        /// </summary>
        private readonly Dictionary<int, LocalHudState> m_localHud =
            new Dictionary<int, LocalHudState>();
        /// <summary>每帧刷新用的本机角色缓冲（复用，避免 60 Hz 反复分配）。</summary>
        private readonly List<PlayerData> m_localRefreshBuffer =
            new List<PlayerData>(PlayerKey.SlotCount);

        /// <summary>
        /// 本机槽位 → 睡眠表现缓存（主机权威睡眠状态 + 客户端外推的 elapsed + 淡入 / 淡出）。
        /// 客户端本机 <c>ComponentSleep.Update</c> 已被 <see cref="DisableLocalSimulation"/> 摘掉，
        /// 黑屏 / Zzz / 提示 / 控件隐藏全由 <see cref="RefreshSleepPresentation"/> 每帧补写。
        /// </summary>
        private readonly Dictionary<int, LocalSleepState> m_localSleep =
            new Dictionary<int, LocalSleepState>();
        /// <summary>主机槽位（H3 第三分屏视图所有者）→ 睡眠表现镜像状态。</summary>
        private readonly Dictionary<int, MirrorSleepState> m_mirrorSleep =
            new Dictionary<int, MirrorSleepState>();
        /// <summary>主机侧镜像视图刷新用的本机角色缓冲（复用）。</summary>
        private readonly List<PlayerData> m_mirrorRefreshBuffer =
            new List<PlayerData>(PlayerKey.SlotCount);
        /// <summary>本拍仍然是"已绑定视图所有者"的本机槽位集合（复用）。</summary>
        private readonly HashSet<int> m_mirrorPresent = new HashSet<int>();
        /// <summary>已经不再绑定视图的槽位（复用，避免每帧分配）。</summary>
        private readonly List<int> m_mirrorStale = new List<int>(PlayerKey.SlotCount);

        /// <summary>
        /// 一个本机槽位的睡眠表现缓存。字段口径：
        ///   · <see cref="SleepStartTime"/> / <see cref="AllowManualWake"/> /
        ///     <see cref="ForcedSleep"/> / <see cref="HasPresentation"/> = 主机权威（只读跟随）；
        ///   · <see cref="ElapsedAtReport"/> + <see cref="GameTimeAtReport"/> = 客户端用同步过的
        ///     世界时钟外推 elapsed 的起点（心跳也会刷新它）；
        ///   · <see cref="Factor"/> / <see cref="MessageFactor"/> = 我们按引擎速度推进的表现值
        ///     （<c>m_sleepFactor</c> / <c>m_messageFactor</c> 的字段写入源）。
        /// </summary>
        private sealed class LocalSleepState
        {
            public int EntityToken;
            public bool Sleeping;
            public bool Dead;
            public bool AllowManualWake;
            public bool ForcedSleep;
            public bool HasPresentation;
            public double SleepStartTime;
            public float ElapsedAtReport;
            public double GameTimeAtReport;
            public float Factor;
            public float MessageFactor;
            /// <summary>上一帧是否由我们写隐藏了操作控件（0 淡出那一帧要写回可见）。</summary>
            public bool ControlsOwned;
            public bool ForcedReported;
            public bool LegacyReported;
        }

        /// <summary>H3 第三分屏视图所有者的睡眠表现镜像状态（只写表现字段，不写睡眠本体）。</summary>
        private sealed class MirrorSleepState
        {
            public ComponentPlayer Owner;
            public int OwnerToken;
            public float Factor;
            public float MessageFactor;
            public bool ControlsOwned;
        }

        /// <summary>一个本机槽位的 HUD 缓存；实体换代（复活换实体）= 新 GUI，贴图必须重写。</summary>
        private sealed class LocalHudState
        {
            public int EntityToken;
            public PlayerVitalsHudValues Values;
            /// <summary>已经写进控件的贴图档位；<c>int.MinValue</c> = 还没写过。</summary>
            public int TextureIndex = int.MinValue;
        }

        private long m_authorityCaptures;
        private long m_authorityCaptureFailures;
        private long m_slotPolls;
        private long m_healthWrites;
        private long m_vitalsWrites;
        private long m_conditionWrites;
        private long m_sleepWrites;
        private long m_deathVisuals;
        private long m_respawnResets;
        private long m_applyFailures;
        private long m_noAvatar;
        private long m_avatarReads;
        private long m_localApplies;
        private long m_localDeaths;
        private long m_localRespawns;
        private long m_deathScreensShown;
        private long m_deathCameraFailures;
        private long m_deathScreenFailures;
        private long m_terminalDeathTaps;
        private long m_respawnRejectedMessages;
        private long m_hudWrites;
        private long m_hudPresentationRefreshes;
        private long m_hudTemperatureTextures;
        private long m_hudIceWrites;
        private long m_hudPresentationFailures;
        private long m_hudNonFinite;
        private long m_localSimDisableRuns;
        private long m_localSimDisabledNew;
        private long m_localSimRestores;
        private long m_suppressRuns;
        private long m_suppressedNew;
        private long m_suppressedMissing;
        private long m_deathParticleFailures;
        private long m_avatarRespawns;
        private long m_avatarRespawnFailures;
        private long m_authoritySleepReads;
        private long m_authorityWakes;
        private long m_sleepNoteFailures;
        private long m_sleepPresentationRuns;
        private long m_sleepMirrorRuns;
        private long m_sleepMirrorFailures;
        private long m_sleepMirrorClears;

        public PlayerVitalsReplica(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "player-vitals" : ownerId;
        }

        // ================================================================ 主机侧：抓取全部主机模拟的角色

        public int CaptureAuthorityVitals(List<PlayerVitalsSnapshot> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "调用方没有给上报容器";
                return 0;
            }
            target.Clear();
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                m_authorityCaptureFailures++;
                return 0;
            }

            int count = 0;
            var locals = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_spawner.TryCollectLocalPlayers(project, locals, out bool tooMany,
                    out string localReason) || locals.Count == 0)
            {
                Problem("authority-local", "本机角色枚举失败：" +
                    (localReason ?? "端口没有给原因") + " → 只抓联机化身（不拿别的角色顶替）");
            }
            else
            {
                if (tooMany) Problem("authority-too-many", localReason);
                for (int slot = 0; slot < locals.Count; slot++)
                {
                    ComponentPlayer player = locals[slot]?.ComponentPlayer;
                    if (player?.ComponentHealth == null || player.ComponentVitalStats == null)
                    {
                        Problem("authority-local-missing:" + slot, "本机槽位 " + slot +
                            " 还没有 ComponentPlayer / ComponentHealth / ComponentVitalStats" +
                            " → 本拍不报它（绝不拿别的角色的值顶替）");
                        m_authorityCaptureFailures++;
                        continue;
                    }
                    target.Add(CaptureOne(new PlayerKey(0, slot), player));
                    count++;
                }
            }

            var keys = new List<PlayerKey>();
            foreach (PlayerKey key in m_spawner.Keys) keys.Add(key);
            keys.Sort(CompareKeys);
            for (int i = 0; i < keys.Count; i++)
            {
                PlayerKey key = keys[i];
                if (!m_spawner.TryGet(key, out PlayerData playerData) ||
                    playerData?.ComponentPlayer == null)
                {
                    Problem("authority-avatar-missing:" + key, "联机化身 " + key +
                        " 还没有 ComponentPlayer → 本拍不报它（不拿别的角色的值顶替）");
                    m_authorityCaptureFailures++;
                    continue;
                }
                ComponentPlayer player = playerData.ComponentPlayer;
                if (player.ComponentHealth == null || player.ComponentVitalStats == null)
                {
                    Problem("authority-avatar-components:" + key, "联机化身 " + key +
                        " 缺少 ComponentHealth / ComponentVitalStats → 本拍不报它");
                    m_authorityCaptureFailures++;
                    continue;
                }
                target.Add(CaptureOne(key, player));
                count++;
            }

            if (count == 0 && reason == null) reason = "主机没有任何可抓取的角色";
            else m_authorityCaptures += count;
            return count;
        }

        public int PollAuthoritySlots(List<PlayerVitalsSlotSample> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "调用方没有给采样容器";
                return 0;
            }
            target.Clear();
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return 0;
            }

            int count = 0;
            var locals = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_spawner.TryCollectLocalPlayers(project, locals, out _, out string localReason) ||
                locals.Count == 0)
            {
                Problem("poll-local", "本机角色枚举失败：" +
                    (localReason ?? "端口没有给原因") + " → 只轮询联机化身");
            }
            else
            {
                for (int slot = 0; slot < locals.Count; slot++)
                {
                    PlayerData playerData = locals[slot];
                    ComponentPlayer player = playerData?.ComponentPlayer;
                    if (player == null)
                    {
                        // 没有 ComponentPlayer = 这个槽位此刻不可观测：仍然占位（HasHealth=false），
                        // 模块据此**不**推断死亡 / 复活边沿，等它回来。
                        target.Add(new PlayerVitalsSlotSample(new PlayerKey(0, slot), false, false,
                            0, new Vector3(float.NaN, float.NaN, float.NaN), null));
                        count++;
                        continue;
                    }
                    target.Add(SampleOne(new PlayerKey(0, slot), player));
                    count++;
                }
            }

            var keys = new List<PlayerKey>();
            foreach (PlayerKey key in m_spawner.Keys) keys.Add(key);
            keys.Sort(CompareKeys);
            for (int i = 0; i < keys.Count; i++)
            {
                PlayerKey key = keys[i];
                if (!m_spawner.TryGet(key, out PlayerData playerData) ||
                    playerData?.ComponentPlayer == null)
                    continue;
                target.Add(SampleOne(key, playerData.ComponentPlayer));
                count++;
            }
            m_slotPolls += count;
            if (count == 0 && reason == null) reason = "主机没有任何可轮询的角色";
            return count;
        }

        private static int CompareKeys(PlayerKey left, PlayerKey right) =>
            left.ClientId != right.ClientId
                ? left.ClientId.CompareTo(right.ClientId)
                : left.Slot.CompareTo(right.Slot);

        private PlayerVitalsSlotSample SampleOne(PlayerKey key, ComponentPlayer player)
        {
            ComponentHealth health = player.ComponentHealth;
            ComponentBody body = player.ComponentBody;
            Vector3 position = body != null
                ? body.Position
                : new Vector3(float.NaN, float.NaN, float.NaN);
            bool hasHealth = health != null;
            bool dead = hasHealth && health.Health <= 0f;
            return new PlayerVitalsSlotSample(key, hasHealth, dead, Token(player), position,
                health != null ? health.CauseOfDeath : null);
        }

        // ================================================================ 客户端侧：本地玩家

        public PlayerVitalsApplyStatus ApplyLocalVitals(PlayerKey key,
            in PlayerVitalsSnapshot snapshot, out string reason)
        {
            reason = null;
            if (!snapshot.TryValidate(out reason)) return PlayerVitalsApplyStatus.Rejected;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return PlayerVitalsApplyStatus.NoAvatar;
            try
            {
                ApplyLocalState(key.Slot, player, in snapshot);
                m_localApplies++;
                return PlayerVitalsApplyStatus.Applied;
            }
            catch (Exception e)
            {
                reason = "应用本机槽位 " + key.Slot + " 的生命状态时抛异常：" + e.GetType().Name +
                    " " + e.Message;
                m_applyFailures++;
                Error("local-apply:" + key.Slot, reason, e);
                return PlayerVitalsApplyStatus.Rejected;
            }
        }

        public PlayerVitalsApplyStatus ApplyLocalVitalEvent(in PlayerVitalEventSnapshot evt,
            out string reason)
        {
            reason = null;
            if (!evt.TryValidate(out reason)) return PlayerVitalsApplyStatus.Rejected;
            if (!TryResolveLocal(evt.Key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return PlayerVitalsApplyStatus.NoAvatar;
            try
            {
                if (evt.IsDeath) ApplyLocalDeath(evt.Key.Slot, player, evt.CauseOfDeath);
                else ApplyLocalRespawn(evt.Key.Slot, player, in evt);
                return PlayerVitalsApplyStatus.Applied;
            }
            catch (Exception e)
            {
                reason = "应用本机槽位 " + evt.Key.Slot + " 的 " + evt.Kind + " 事件时抛异常：" +
                    e.GetType().Name + " " + e.Message;
                m_applyFailures++;
                Error("local-event:" + evt.Key.Slot, reason, e);
                return PlayerVitalsApplyStatus.Rejected;
            }
        }

        /// <summary>
        /// 主机权威值 → 本机角色。只写字段 / HUD；死亡时引擎里保留
        /// <see cref="LocalDeathHealthFloor"/>，让引擎 PlayerData 不进入本地死亡 / 复活流程。
        /// </summary>
        private void ApplyLocalState(int slot, ComponentPlayer player,
            in PlayerVitalsSnapshot snapshot)
        {
            EnsureLocalPlayerNotLocallyDead(player.PlayerData, player);
            ComponentHealth health = player.ComponentHealth;
            bool dead = snapshot.Dead;
            bool wasDead = m_localDeadSlots.Contains(slot);
            float previousHealth = health != null ? health.Health : FullHealth;
            float engineHealth = dead ? LocalDeathHealthFloor : MathUtils.Saturate(snapshot.Health);
            if (health != null)
            {
                WriteField(health, "<Health>k__BackingField", engineHealth,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "m_lastHealth", engineHealth,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<Air>k__BackingField",
                    MathUtils.Saturate(snapshot.Air), typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<HealthChange>k__BackingField", 0f,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<CauseOfDeath>k__BackingField",
                    dead ? (string.IsNullOrEmpty(snapshot.CauseOfDeath)
                        ? "Unknown" : snapshot.CauseOfDeath) : string.Empty,
                    typeof(ComponentHealth));
                // 绝不写 DeathTime：那是引擎死亡分支（掉落 / 尸体）的入口，客户端不跑它。
                m_parentField.ModifyParentField(health, "<DeathTime>k__BackingField", null,
                    typeof(ComponentHealth));
                if (snapshot.Health < previousHealth - PlayerVitalsLimits.ValueEpsilon)
                    ShowHurt(player, previousHealth - snapshot.Health);
                m_healthWrites++;
            }

            if (player.ComponentVitalStats != null)
            {
                WriteVitalStats(player.ComponentVitalStats, in snapshot);
                m_vitalsWrites++;
            }
            if (player.ComponentSleep != null)
            {
                // 本机角色：只写"睡眠本体"（IsSleeping 姿势 / 手动唤醒权限），
                // m_sleepFactor 的淡入 / 淡出交给 RefreshSleepPresentation（引擎速度），
                // 不再像远端化身那样瞬间置 1/0。
                ApplyLocalSleep(player, in snapshot);
                NoteLocalSleepState(slot, player, in snapshot);
                m_sleepWrites++;
            }
            WriteConditions(player, in snapshot);
            WriteLocalFire(player, slot, snapshot.FireDuration);
            WriteLocalHud(slot, player, snapshot.Dead ? 0f : engineHealth, snapshot.Food,
                snapshot.Temperature, snapshot.TargetTemperature, snapshot.Dead);

            if (dead && !wasDead)
            {
                m_localDeadSlots.Add(slot);
                m_localDeaths++;
                ShowLocalDeath(player, snapshot.CauseOfDeath);
            }
            else if (!dead && wasDead)
            {
                m_localDeadSlots.Remove(slot);
                HideLocalDeath(player);
                ShowLocalRespawn(player);
            }
        }

        private void ApplyLocalDeath(int slot, ComponentPlayer player, string cause)
        {
            EnsureLocalPlayerNotLocallyDead(player.PlayerData, player);
            ComponentHealth health = player.ComponentHealth;
            if (health != null)
            {
                WriteField(health, "<Health>k__BackingField", LocalDeathHealthFloor,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "m_lastHealth", LocalDeathHealthFloor,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<HealthChange>k__BackingField", 0f,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<CauseOfDeath>k__BackingField",
                    string.IsNullOrEmpty(cause) ? "Unknown" : cause, typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<DeathTime>k__BackingField", null,
                    typeof(ComponentHealth));
            }
            ComponentVitalStats deathVital = player.ComponentVitalStats;
            WriteLocalHud(slot, player, 0f,
                deathVital != null ? deathVital.Food : 1f,
                deathVital != null ? deathVital.Temperature : DefaultTemperature,
                ReadTargetTemperature(deathVital), true);
            bool newlyDead = m_localDeadSlots.Add(slot);
            StopLocalFireParticles(slot);
            if (newlyDead) m_localDeaths++;
            ShowLocalDeath(player, cause);
        }

        private void ApplyLocalRespawn(int slot, ComponentPlayer player,
            in PlayerVitalEventSnapshot evt)
        {
            EnsureLocalPlayerNotLocallyDead(player.PlayerData, player);
            ComponentBody body = player.ComponentBody;
            if (body != null && evt.HasPosition)
            {
                body.Position = evt.Position;
                body.Velocity = Vector3.Zero;
                body.TargetCrouchFactor = 0f;
            }
            ComponentHealth health = player.ComponentHealth;
            if (health != null)
            {
                WriteField(health, "<Health>k__BackingField", FullHealth,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "m_lastHealth", FullHealth,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<Air>k__BackingField", 1f,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<HealthChange>k__BackingField", 0f,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<CauseOfDeath>k__BackingField",
                    string.Empty, typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<DeathTime>k__BackingField", null,
                    typeof(ComponentHealth));
            }
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model != null)
            {
                m_parentField.ModifyParentField(model, "<DeathPhase>k__BackingField", 0f,
                    typeof(ComponentCreatureModel));
                m_parentField.ModifyParentField(model, "<DeathCauseOffset>k__BackingField",
                    Vector3.Zero, typeof(ComponentCreatureModel));
            }
            StopLocalFireParticles(slot);
            player.Entity.FindComponent<ComponentOnFire>()?.Extinguish();
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep != null)
            {
                m_parentField.ModifyParentField(sleep, "m_sleepStartTime", null,
                    typeof(ComponentSleep));
                m_parentField.ModifyParentField(sleep, "m_sleepFactor", 0f, typeof(ComponentSleep));
                m_parentField.ModifyParentField(sleep, "m_messageFactor", 0f, typeof(ComponentSleep));
            }
            // 复活 = 旧睡眠期作废：表现缓存清掉，下一次入睡从 0 重新淡入。
            m_localSleep.Remove(slot);
            m_localDeadSlots.Remove(slot);
            ComponentVitalStats respawnVital = player.ComponentVitalStats;
            WriteLocalHud(slot, player, FullHealth,
                respawnVital != null ? respawnVital.Food : 1f,
                respawnVital != null ? respawnVital.Temperature : DefaultTemperature,
                ReadTargetTemperature(respawnVital), false);
            HideLocalDeath(player);
            ShowLocalRespawn(player);
            m_localRespawns++;
        }

        /// <summary>
        /// 客户端**不跑**自己的死亡流程：如果引擎 PlayerData 已经进入 PlayerDead（例如加入房间
        /// 前本机模拟先把它打死了），把它拉回 Playing 并清掉死亡计时；否则点击后 PlayerData
        /// 会删除本机实体并本地复活 —— 那是主机才有的裁决权。
        /// </summary>
        private void EnsureLocalPlayerNotLocallyDead(PlayerData playerData, ComponentPlayer player)
        {
            if (playerData == null) return;
            StateMachine stateMachine = m_parentField.GetParentField<StateMachine>(playerData,
                "m_stateMachine", typeof(PlayerData));
            m_parentField.ModifyParentField(playerData, "m_playerDeathTime", null,
                typeof(PlayerData));
            if (stateMachine == null || stateMachine.CurrentState != "PlayerDead") return;
            stateMachine.TransitionTo("Playing");
            GameWidget widget = player.GameWidget;
            if (widget != null && widget.ActiveCamera is DeathCamera)
                widget.ActiveCamera = widget.FindCamera<FppCamera>();
            Problem("local-playerdead:" + (player.PlayerData?.PlayerIndex ?? -1),
                "本机角色曾被引擎 PlayerData 判死（PlayerDead）→ 已拉回 Playing 并清死亡计时：" +
                "客户端不跑自己的死亡 / 复活流程（主机裁决）");
        }

        private void ShowHurt(ComponentPlayer player, float loss)
        {
            ComponentGui gui = player.ComponentGui;
            if (gui != null && gui.HealthBarWidget != null)
            {
                gui.HealthBarWidget.Flash(MathUtils.Clamp((int)(loss * 30f), 1, 10));
                gui.HealthBarWidget.Value = MathUtils.Max(0f,
                    player.ComponentHealth != null ? player.ComponentHealth.Health : 0f);
            }
            player.ComponentCreatureSounds?.PlayPainSound();
            ComponentScreenOverlays overlays = player.ComponentScreenOverlays;
            if (overlays != null)
            {
                overlays.RedoutFactor = MathUtils.Max(overlays.RedoutFactor,
                    MathUtils.Clamp(loss * 4f, 0f, 1f));
            }
        }

        /// <summary>
        /// 把一份主机权威体征写进本机角色的 HUD（血条 / 食物条 / 温度条贴图 / 冰冻覆盖）。
        /// 只写**显示属性**，不写任何组件字段、不跑模拟。输入非法时保留上一次有效显示并计数。
        /// </summary>
        private void WriteLocalHud(int slot, ComponentPlayer player, float healthValue,
            float foodValue, float temperature, float targetTemperature, bool dead)
        {
            if (!PlayerVitalsPresentation.TryCreate(healthValue, foodValue, temperature,
                    targetTemperature, dead, out PlayerVitalsHudValues values))
            {
                m_hudNonFinite++;
                Problem("hud-values:" + slot, "本机槽位 " + slot + " 的体征 HUD 输入不是有限数：" +
                    "health=" + healthValue + " food=" + foodValue + " temperature=" +
                    temperature + " target=" + targetTemperature +
                    " → 保留上一次有效显示（不写 NaN、不拿默认值顶替）");
                return;
            }
            LocalHudState state = GetOrCreateLocalHud(slot, player);
            state.Values = values;
            if (!ApplyLocalHud(slot, player, state, out string reason))
            {
                Problem("hud-apply:" + slot, "本机槽位 " + slot + " 的体征 HUD 写入失败：" +
                    reason);
                return;
            }
            m_hudWrites++;
        }

        /// <summary>
        /// **客户端每帧**：把缓存的主机权威体征刷新到本机角色显示层。
        /// 冰冻覆盖（<c>ComponentScreenOverlays.IceFactor</c>）会被
        /// <c>ComponentScreenOverlays.Update</c> 每帧清 0，所以必须每帧重写；温度条贴图只在档位
        /// 变化时写（<see cref="LocalHudState.TextureIndex"/> 去重）。实体换代 = 新 GUI，重新写。
        /// 返回刷新到的本机角色数；<paramref name="reason"/> 非空 = 有槽位刷新失败（限流日志由调用方做）。
        /// </summary>
        public int RefreshLocalPresentation(out string reason)
        {
            reason = null;
            if (m_disabledLocalSlots.Count == 0 && m_localHud.Count == 0) return 0;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（HUD 无法刷新）";
                return 0;
            }
            if (!m_spawner.TryCollectLocalPlayers(project, m_localRefreshBuffer, out bool tooMany,
                    out string localReason))
            {
                reason = "读取本机角色失败：" + (localReason ?? "端口没有给原因");
                return 0;
            }
            if (tooMany)
                Problem("hud-local-too-many", "本机角色数超过引擎上限（" + localReason +
                    "）→ HUD 只刷新前 " + PlayerKey.SlotCount + " 个槽位");
            int applied = 0;
            string firstFailure = null;
            foreach (int slot in m_disabledLocalSlots.Keys)
            {
                if (slot < 0 || slot >= m_localRefreshBuffer.Count) continue;
                ComponentPlayer player = m_localRefreshBuffer[slot]?.ComponentPlayer;
                if (player == null) continue;
                if (!m_localHud.TryGetValue(slot, out LocalHudState state) ||
                    state.EntityToken != Token(player))
                {
                    if (!SeedLocalHud(slot, player, out string seedReason))
                    {
                        if (firstFailure == null)
                            firstFailure = "槽位 " + slot + "：" + seedReason;
                        continue;
                    }
                    state = m_localHud[slot];
                }
                if (!ApplyLocalHud(slot, player, state, out string applyReason))
                {
                    if (firstFailure == null)
                        firstFailure = "槽位 " + slot + "：" + applyReason;
                    continue;
                }
                applied++;
            }
            if (applied > 0) m_hudPresentationRefreshes++;
            reason = firstFailure;
            return applied;
        }

        /// <summary>
        /// 还没有收到主机快照时，先用本机组件里的当前值（= 下载世界 / 上一次主机值）初始化 HUD，
        /// 避免温度条在第一条快照到达前保持 <c>BarSubtexture == null</c>（引擎会画成纯色矩形）。
        /// </summary>
        private bool SeedLocalHud(int slot, ComponentPlayer player, out string reason)
        {
            reason = null;
            ComponentVitalStats vital = player.ComponentVitalStats;
            ComponentHealth health = player.ComponentHealth;
            if (vital == null || health == null)
            {
                m_hudPresentationFailures++;
                reason = "缺少 ComponentVitalStats / ComponentHealth（HUD 无法从当前权威值初始化）";
                return false;
            }
            bool dead = m_localDeadSlots.Contains(slot) || health.Health <= 0f;
            float target = ReadTargetTemperature(vital);
            if (!PlayerVitalsPresentation.TryCreate(dead ? 0f : health.Health, vital.Food,
                    vital.Temperature, target, dead, out PlayerVitalsHudValues values))
            {
                m_hudNonFinite++;
                reason = "当前体征不是有限数（health=" + health.Health + " food=" + vital.Food +
                    " temperature=" + vital.Temperature + " target=" + target + "）";
                return false;
            }
            LocalHudState state = GetOrCreateLocalHud(slot, player);
            state.Values = values;
            return true;
        }

        private LocalHudState GetOrCreateLocalHud(int slot, ComponentPlayer player)
        {
            if (!m_localHud.TryGetValue(slot, out LocalHudState state))
            {
                state = new LocalHudState();
                m_localHud[slot] = state;
            }
            int token = Token(player);
            if (state.EntityToken != token)
            {
                // 角色实体换代 = 新 GUI：控件里的贴图不一定还是我们上次写的那张。
                state.EntityToken = token;
                state.TextureIndex = int.MinValue;
            }
            return state;
        }

        /// <summary>
        /// 写显示属性。温度条贴图按档位去重（<c>ContentManager.Get</c> 只在档位变化时调用）；
        /// 冰冻覆盖每次都写（引擎每帧清 0）。任何控件缺失都返回 false + 明确原因，不静默跳过。
        /// </summary>
        private bool ApplyLocalHud(int slot, ComponentPlayer player, LocalHudState state,
            out string reason)
        {
            reason = null;
            ComponentGui gui = player.ComponentGui;
            if (gui == null)
            {
                m_hudPresentationFailures++;
                reason = "角色没有 ComponentGui";
                return false;
            }
            bool complete = true;
            if (gui.HealthBarWidget != null)
                gui.HealthBarWidget.Value = state.Values.HealthBarValue;
            else
            {
                m_hudPresentationFailures++;
                complete = false;
                reason = "ComponentGui 没有 HealthBarWidget";
            }
            if (gui.FoodBarWidget != null)
                gui.FoodBarWidget.Value = state.Values.FoodBarValue;
            else
            {
                m_hudPresentationFailures++;
                complete = false;
                if (reason == null) reason = "ComponentGui 没有 FoodBarWidget";
            }
            ValueBarWidget temperatureBar = gui.TemperatureBarWidget;
            if (temperatureBar == null)
            {
                m_hudPresentationFailures++;
                reason = "ComponentGui 没有 TemperatureBarWidget（温度条无法显示）";
                return false;
            }
            // 温度条不是进度条：XML 里 Value 固定为 1，变的只有 BarSubtexture。
            temperatureBar.Value = state.Values.TemperatureBarValue;
            if (state.TextureIndex != state.Values.TemperatureTextureIndex)
            {
                string textureName = state.Values.TemperatureTextureName;
                Subtexture texture = null;
                try
                {
                    if (textureName != null) texture = ContentManager.Get<Subtexture>(textureName);
                }
                catch (Exception e)
                {
                    m_hudPresentationFailures++;
                    reason = "读取温度贴图 " + (textureName ?? "(null)") + " 抛异常：" +
                        e.GetType().Name + " " + e.Message;
                    return false;
                }
                if (texture == null)
                {
                    m_hudPresentationFailures++;
                    reason = "温度贴图 " + (textureName ?? "(null)") + " 不存在";
                    return false;
                }
                temperatureBar.BarSubtexture = texture;
                state.TextureIndex = state.Values.TemperatureTextureIndex;
                m_hudTemperatureTextures++;
            }
            ComponentScreenOverlays overlays = player.ComponentScreenOverlays;
            if (overlays == null)
            {
                m_hudPresentationFailures++;
                if (reason == null) reason = "角色没有 ComponentScreenOverlays（冰冻覆盖无法显示）";
                return false;
            }
            overlays.IceFactor = state.Values.IceFactor;
            m_hudIceWrites++;
            return complete && reason == null;
        }

        /// <summary>读主机权威目标体温（<c>m_targetTemperature</c>，客户端只读）。</summary>
        private float ReadTargetTemperature(ComponentVitalStats vital) =>
            vital == null
                ? DefaultTemperature
                : m_parentField.GetParentField<float>(vital, "m_targetTemperature",
                    typeof(ComponentVitalStats));

        // ================================================================ 客户端侧：引擎等价死亡屏

        /// <summary>
        /// 读世界复活规则（与引擎 <c>PlayerData.PlayerDead</c> 同一个 <c>WorldSettings</c> 口径）。
        /// 读不到世界时明确返回 <see cref="PlayerRespawnMode.Unknown"/>，不猜模式。
        /// </summary>
        public PlayerRespawnMode GetRespawnMode(out string reason)
        {
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return PlayerRespawnMode.Unknown;
            }
            SubsystemGameInfo info = project.FindSubsystem<SubsystemGameInfo>(false);
            if (info?.WorldSettings == null)
            {
                reason = "Project 没有 SubsystemGameInfo.WorldSettings（无法读游戏模式）";
                return PlayerRespawnMode.Unknown;
            }
            WorldSettings settings = info.WorldSettings;
            return PlayerRespawnRules.Evaluate(settings.GameMode == GameMode.Cruel,
                settings.GameMode == GameMode.Adventure, settings.IsAdventureRespawnAllowed);
        }

        /// <summary>
        /// 引擎等价的死亡屏（**不进入** PlayerData 的 PlayerDead 状态机）：
        ///   · 相机：<c>GameWidget.ActiveCamera = GameWidget.FindCamera&lt;DeathCamera&gt;()</c>。
        ///     每个 GameWidget 构造函数都会 <c>m_cameras.Add(new DeathCamera(this))</c>
        ///     （GameWidget.cs:63-69），setter 是 public（GameWidget.cs:26-56），
        ///     Activate 只读上一相机位置 + Target 身体（DeathCamera.cs:20-31），
        ///     所以**不需要**状态机就能激活；DeathCamera.IsEntityControlEnabled = false
        ///     还会让 ComponentInput 自动清零移动 / 跳跃（ComponentInput.cs:95-116），
        ///     与引擎死亡时的表现一致。复活时由 <see cref="HideLocalDeath"/> 切回 FppCamera。
        ///   · 文本：<c>ComponentGui.DisplayLargeMessage("YOU HAVE DIED", cause + 模式行, 30f, 1.5f)</c>，
        ///     与 PlayerData.cs:275-300 的大消息 / 时长 / 延迟完全一致。
        /// 不删实体、不掉物品、不本地复活（那些是主机裁决）。
        /// </summary>
        private bool ShowLocalDeath(ComponentPlayer player, string cause)
        {
            if (player == null)
            {
                m_deathScreenFailures++;
                return false;
            }
            ComponentGui gui = player.ComponentGui;
            if (gui == null)
            {
                m_deathScreenFailures++;
                Problem("death-screen-gui:" + (player.PlayerData?.PlayerIndex ?? -1),
                    "死亡屏无法显示：本机角色没有 ComponentGui（不静默跳过）");
                return false;
            }
            PlayerRespawnMode mode = GetRespawnMode(out string modeReason);
            string gameModeName = null;
            Project project = player.Project;
            SubsystemGameInfo info = project?.FindSubsystem<SubsystemGameInfo>(false);
            if (info?.WorldSettings != null) gameModeName = info.WorldSettings.GameMode.ToString();
            if (mode == PlayerRespawnMode.Unknown)
            {
                Problem("death-screen-mode:" + (player.PlayerData?.PlayerIndex ?? -1),
                    "死亡屏读不到世界模式（" + (modeReason ?? "端口没有给原因") +
                    "）→ 仍显示死因，但不提供请求路径（不猜模式）");
            }
            string small = PlayerRespawnRules.BuildDeathSmallText(cause, mode, gameModeName);
            GameWidget widget = player.GameWidget;
            if (widget == null)
            {
                m_deathCameraFailures++;
                Problem("death-camera-widget:" + (player.PlayerData?.PlayerIndex ?? -1),
                    "死亡相机无法激活：本机角色没有 GameWidget（大消息仍显示）");
            }
            else
            {
                DeathCamera camera = widget.FindCamera<DeathCamera>(false);
                if (camera == null)
                {
                    m_deathCameraFailures++;
                    Problem("death-camera-missing:" + (player.PlayerData?.PlayerIndex ?? -1),
                        "GameWidget 没有 DeathCamera（引擎构造时本应加入，GameWidget.cs:63-69）" +
                        "→ 死亡屏退化为大消息，不切相机");
                }
                else
                {
                    // 与引擎 PlayerDead enter 完全相同的一行（相机先切，再显示消息）。
                    try
                    {
                        widget.ActiveCamera = camera;
                    }
                    catch (Exception e)
                    {
                        m_deathCameraFailures++;
                        Problem("death-camera-activate:" + (player.PlayerData?.PlayerIndex ?? -1),
                            "激活 DeathCamera 抛异常：" + e.GetType().Name + " " + e.Message +
                            " → 死亡屏仍显示大消息（不假装相机切成功）");
                    }
                }
            }
            gui.DisplayLargeMessage(PlayerRespawnRules.DeathLargeText, small,
                PlayerRespawnRules.MessageDurationSeconds, PlayerRespawnRules.MessageDelaySeconds);
            m_deathScreensShown++;
            return true;
        }

        private void HideLocalDeath(ComponentPlayer player)
        {
            if (player == null) return;
            player.ComponentGui?.DisplayLargeMessage(string.Empty, string.Empty, 0f, 0f);
            GameWidget widget = player.GameWidget;
            if (widget == null || !(widget.ActiveCamera is DeathCamera)) return;
            FppCamera fpp = widget.FindCamera<FppCamera>(false);
            if (fpp == null)
            {
                Problem("death-camera-restore:" + (player.PlayerData?.PlayerIndex ?? -1),
                    "复活后无法切回 FppCamera（GameWidget 没有 FppCamera）→ 保持当前相机");
                return;
            }
            widget.ActiveCamera = fpp;
        }

        /// <summary>
        /// 每帧读死亡屏输入。口径与引擎 PlayerData.PlayerDead update 完全一致：
        /// <c>!DialogsManager.HasDialogs(ComponentPlayer.GuiWidget)</c> 且
        /// <c>ComponentPlayer.GameWidget.Input.Any</c>（tap / click / key 都会置 Any，
        /// WidgetInput.cs:683-739 / 864 / 1031）。1.5 秒门槛由模块的纯逻辑门判定。
        /// </summary>
        public bool TryPollLocalDeathTap(PlayerKey key, out bool tapped, out string reason)
        {
            tapped = false;
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            GameWidget widget = player.GameWidget;
            if (widget == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 GameWidget（无法读死亡屏输入）";
                return false;
            }
            try
            {
                tapped = !DialogsManager.HasDialogs(player.GuiWidget) && widget.Input.Any;
                return true;
            }
            catch (Exception e)
            {
                reason = "读取死亡屏输入异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 不可复活模式的点击（不发送 PLAYER_RESPAWN_REQUEST）：
        ///   · Cruel：与引擎 PlayerData.cs:308-311 一样打开 <c>GameMenuDialog</c>；
        ///   · Adventure 禁复活：引擎的终止路径是
        ///     <c>ScreensManager.SwitchScreen("GameLoading", GameManager.WorldInfo, "AdventureRestart")</c>
        ///     （PlayerData.cs:312-315）。联机客户端不能重启主机世界（会把本端拉出共享会话、
        ///     造成主机侧死亡角色 / 世界状态分叉），所以这里**不执行**它，改为打开同一个
        ///     GameMenuDialog 并把原因写进日志 —— 这是"报告为什么不能照搬"，不是静默兜底。
        /// </summary>
        public bool TryHandleTerminalDeathTap(PlayerKey key, PlayerRespawnMode mode,
            out string reason)
        {
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            try
            {
                if (mode == PlayerRespawnMode.Cruel)
                {
                    DialogsManager.ShowDialog(player.GuiWidget, new GameMenuDialog(player));
                    m_terminalDeathTaps++;
                    reason = "cruel: 已打开 GameMenuDialog（与引擎 PlayerData.cs:308-311 相同）";
                    return true;
                }
                if (mode == PlayerRespawnMode.AdventureRestart)
                {
                    DialogsManager.ShowDialog(player.GuiWidget, new GameMenuDialog(player));
                    m_terminalDeathTaps++;
                    Problem("adventure-restart:" + key,
                        "Adventure 未允许复活：引擎会在点击后 ScreensManager.SwitchScreen(" +
                        "\"GameLoading\", GameManager.WorldInfo, \"AdventureRestart\")" +
                        "（PlayerData.cs:312-315）重启冒险；联机客户端不能重启主机世界" +
                        "（会退出共享会话并造成主机侧状态分叉）→ 本次点击只打开 GameMenuDialog，" +
                        "不发送复活请求");
                    reason = "adventure-restart: 引擎路径会重启主机世界，联机客户端不执行；" +
                        "已打开 GameMenuDialog 并记录原因";
                    return true;
                }
                reason = "模式 " + PlayerRespawnRules.Describe(mode) +
                    " 没有可复活的终止点击路径（不发送请求）";
                return false;
            }
            catch (Exception e)
            {
                reason = "不可复活模式点击处理异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        public bool TryShowLocalRespawnRejected(PlayerKey key, string reasonCode,
            out string reason)
        {
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            ComponentGui gui = player.ComponentGui;
            if (gui == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 ComponentGui（无法显示拒绝原因）";
                return false;
            }
            gui.DisplaySmallMessage("Respawn rejected: " +
                (string.IsNullOrEmpty(reasonCode) ? "unknown" : reasonCode),
                Color.White, false, true);
            m_respawnRejectedMessages++;
            return true;
        }

        private void ShowLocalRespawn(ComponentPlayer player)
        {
            player.ComponentGui?.DisplaySmallMessage("Respawned", Color.White, false, true);
        }

        private void WriteLocalFire(ComponentPlayer player, int slot, float fireDuration)
        {
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            if (onFire == null) return;
            // 不调 SetOnFire（它会取 max，主机火焰衰减时降不下来）；直接写权威时长，
            // 粒子表现由客户端自己维护（ComponentOnFire.Update 已被摘掉）。
            m_parentField.ModifyParentField(onFire, "m_fireDuration",
                MathUtils.Max(fireDuration, 0f), typeof(ComponentOnFire));
            if (fireDuration > 0.01f) EnsureLocalFireParticles(player, slot);
            else StopLocalFireParticles(slot);
            m_conditionWrites++;
        }

        private void EnsureLocalFireParticles(ComponentPlayer player, int slot)
        {
            ComponentBody body = player.ComponentBody;
            Project project = player.Project;
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            if (body == null || particles == null)
            {
                Problem("local-fire-particles:" + slot,
                    "本机角色火焰粒子无法创建（缺少 ComponentBody / SubsystemParticles）" +
                    "（火焰状态仍然跟随主机）");
                return;
            }
            if (!m_localFireParticles.TryGetValue(slot, out OnFireParticleSystem system) ||
                system == null)
            {
                system = new OnFireParticleSystem();
                particles.AddParticleSystem(system);
                m_localFireParticles[slot] = system;
            }
            BoundingBox box = body.BoundingBox;
            system.Position = 0.5f * (box.Min + box.Max);
            system.Radius = 0.5f * MathUtils.Min(box.Max.X - box.Min.X, box.Max.Z - box.Min.Z);
        }

        private void StopLocalFireParticles(int slot)
        {
            if (!m_localFireParticles.TryGetValue(slot, out OnFireParticleSystem system)) return;
            m_localFireParticles.Remove(slot);
            if (system != null) system.IsStopped = true;
        }

        // ================================================================ 客户端侧：本地模拟闸门

        public int DisableLocalSimulation(out int totalDisabled, out string reason)
        {
            reason = null;
            totalDisabled = 0;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return 0;
            }
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (update == null)
            {
                reason = "找不到 SubsystemUpdate（无法摘掉本机角色的生命 / 体征模拟）";
                return 0;
            }
            var locals = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_spawner.TryCollectLocalPlayers(project, locals, out bool tooMany,
                    out string localReason) || locals.Count == 0)
            {
                reason = localReason ?? "本机角色枚举失败";
                return 0;
            }
            if (tooMany) Problem("local-sim-too-many", localReason);
            m_localSimDisableRuns++;
            int added = 0;
            var present = new HashSet<int>();
            for (int slot = 0; slot < locals.Count; slot++)
            {
                ComponentPlayer player = locals[slot]?.ComponentPlayer;
                if (player?.Entity == null) continue;
                present.Add(slot);
                int token = Token(player);
                if (m_disabledLocalSlots.TryGetValue(slot, out int previousToken) &&
                    previousToken == token)
                    continue;
                Entity entity = player.Entity;
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    // 客户端本机角色的这六个组件都**不模拟**：生命 / 饥饿 / 睡眠 / 流感 /
                    // 食物中毒 / 火焰。值只由主机下发覆盖；直接 Injure 的调用方（生物 / 爆炸）
                    // 由 IsInvulnerable 挡住。
                    if (!(updateable is ComponentHealth) && !(updateable is ComponentVitalStats) &&
                        !(updateable is ComponentSleep) && !(updateable is ComponentFlu) &&
                        !(updateable is ComponentSickness) && !(updateable is ComponentOnFire))
                        continue;
                    update.RemoveUpdateable(updateable);
                }
                if (player.ComponentHealth != null)
                {
                    m_parentField.ModifyParentField(player.ComponentHealth,
                        "<IsInvulnerable>k__BackingField", true, typeof(ComponentHealth));
                }
                EnsureLocalPlayerNotLocallyDead(player.PlayerData, player);
                m_disabledLocalSlots[slot] = token;
                // 摘掉模拟的同一拍就把 HUD 从当前（下载世界 / 上一次主机）值初始化：
                // 温度条的 BarSubtexture 不能等到第一条快照才第一次被写。
                if (!m_localHud.TryGetValue(slot, out LocalHudState hud) ||
                    hud.EntityToken != token)
                {
                    if (!SeedLocalHud(slot, player, out string hudReason))
                        Problem("hud-seed:" + slot, "本机槽位 " + slot +
                            " 的 HUD 初始值未能建立：" + hudReason);
                }
                added++;
                m_localSimDisabledNew++;
            }
            // 已经不在本机槽位表里的记录：清掉（角色被移除 / 槽位重排）。
            var stale = new List<int>();
            foreach (int slot in m_disabledLocalSlots.Keys)
                if (!present.Contains(slot)) stale.Add(slot);
            for (int i = 0; i < stale.Count; i++)
            {
                m_disabledLocalSlots.Remove(stale[i]);
                StopLocalFireParticles(stale[i]);
                m_localDeadSlots.Remove(stale[i]);
                m_localHud.Remove(stale[i]);
                m_localSleep.Remove(stale[i]);
            }
            totalDisabled = m_disabledLocalSlots.Count;
            if (added > 0)
            {
                Info("已摘掉 " + added + " 个本机角色的生命 / 体征 / 睡眠 / 病程 / 火焰模拟" +
                    "（当前 " + totalDisabled + " 个）：客户端只跟随主机下发的值");
            }
            return added;
        }

        public bool EnableLocalSimulation(out string reason)
        {
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                // 没有世界可恢复；清掉账本即可（下次进世界会重新判断）。
                m_disabledLocalSlots.Clear();
                m_localDeadSlots.Clear();
                m_localFireParticles.Clear();
                m_localHud.Clear();
                m_localSleep.Clear();
                return true;
            }
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (update == null)
            {
                reason = "找不到 SubsystemUpdate（无法恢复本机角色的生命 / 体征模拟）";
                return false;
            }
            foreach (KeyValuePair<int, int> item in m_disabledLocalSlots)
            {
                if (!TryResolveLocal(item.Key, out PlayerData playerData,
                        out ComponentPlayer player, out _))
                    continue;
                Entity entity = player.Entity;
                if (entity == null) continue;
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    if (updateable is ComponentHealth || updateable is ComponentVitalStats ||
                        updateable is ComponentSleep || updateable is ComponentFlu ||
                        updateable is ComponentSickness || updateable is ComponentOnFire)
                        update.AddUpdateable(updateable);
                }
                if (player.ComponentHealth != null)
                {
                    m_parentField.ModifyParentField(player.ComponentHealth,
                        "<IsInvulnerable>k__BackingField", false, typeof(ComponentHealth));
                }
                StopLocalFireParticles(item.Key);
                m_localSimRestores++;
            }
            m_disabledLocalSlots.Clear();
            m_localDeadSlots.Clear();
            // 本地模拟恢复后，引擎的 ComponentVitalStats.UpdateTemperature 会重新写温度条 /
            // 冰冻覆盖 —— 清掉我们这份只读显示缓存，避免两套来源。
            m_localHud.Clear();
            // 睡眠表现同理：ComponentSleep.Update 已重新加回 SubsystemUpdate，淡入 / 淡出 /
            // 黑屏 / 提示重新由引擎写；我们停止每帧补写，避免两套来源。
            m_localSleep.Clear();
            if (m_localSimRestores > 0)
                Info("本机角色的生命 / 体征 / 睡眠 / 病程 / 火焰模拟已恢复（累计恢复 " +
                    m_localSimRestores + " 个槽位）");
            return true;
        }

        // ================================================================ 客户端侧：远端化身

        public bool TryGetAvatarVitals(PlayerKey key, out PlayerVitalsSnapshot snapshot,
            out string reason)
        {
            snapshot = default;
            reason = null;
            if (!TryResolveAvatar(key, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            if (player.ComponentHealth == null || player.ComponentVitalStats == null)
            {
                reason = "玩家键 " + key + " 的化身缺少 ComponentHealth / ComponentVitalStats";
                return false;
            }
            snapshot = CaptureOne(key, player);
            m_avatarReads++;
            return true;
        }

        public PlayerVitalsApplyStatus ApplyRemoteVitals(PlayerKey key,
            in PlayerVitalsSnapshot snapshot, out string reason)
        {
            reason = null;
            if (!snapshot.TryValidate(out reason)) return PlayerVitalsApplyStatus.Rejected;
            if (!TryResolveAvatar(key, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return PlayerVitalsApplyStatus.NoAvatar;
            try
            {
                ActivePlayerState state = ApplyRemoteState(player, in snapshot);
                if (state == ActivePlayerState.None)
                {
                    reason = "玩家键 " + key + " 的化身在这批字段上什么都没写成（组件缺失）";
                    m_applyFailures++;
                    return PlayerVitalsApplyStatus.Rejected;
                }
                return PlayerVitalsApplyStatus.Applied;
            }
            catch (Exception e)
            {
                reason = "应用玩家键 " + key + " 的生命状态时抛异常：" + e.GetType().Name + " " +
                    e.Message;
                m_applyFailures++;
                Error("apply:" + key, reason, e);
                return PlayerVitalsApplyStatus.Rejected;
            }
        }

        public PlayerVitalsApplyStatus ApplyVitalEvent(in PlayerVitalEventSnapshot evt,
            out string reason)
        {
            reason = null;
            if (!evt.TryValidate(out reason)) return PlayerVitalsApplyStatus.Rejected;
            if (!TryResolveAvatar(evt.Key, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return PlayerVitalsApplyStatus.NoAvatar;
            try
            {
                if (evt.IsRespawn) ResetAfterRespawn(player, in evt);
                else ApplyDeath(player, evt.CauseOfDeath);
                return PlayerVitalsApplyStatus.Applied;
            }
            catch (Exception e)
            {
                reason = "应用玩家键 " + evt.Key + " 的 " + evt.Kind + " 事件时抛异常：" +
                    e.GetType().Name + " " + e.Message;
                m_applyFailures++;
                Error("event:" + evt.Key, reason, e);
                return PlayerVitalsApplyStatus.Rejected;
            }
        }

        public int SuppressRemoteAvatars(out int suppressedTotal, out string reason)
        {
            reason = null;
            suppressedTotal = 0;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return 0;
            }
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (update == null)
            {
                reason = "找不到 SubsystemUpdate（无法摘掉远端化身的生命 / 体征模拟）";
                return 0;
            }
            m_suppressRuns++;
            var keys = new List<PlayerKey>(m_suppressedTokens.Count + 4);
            int added = 0;
            foreach (PlayerKey key in m_spawner.Keys)
            {
                keys.Add(key);
                if (!m_spawner.TryGet(key, out PlayerData playerData) ||
                    playerData?.ComponentPlayer == null)
                {
                    m_suppressedMissing++;
                    continue;
                }
                ComponentPlayer player = playerData.ComponentPlayer;
                int token = Token(player);
                if (m_suppressedTokens.TryGetValue(key, out int previousToken) &&
                    previousToken == token)
                    continue;
                m_suppressedTokens[key] = token;
                Entity entity = player.Entity;
                if (entity == null) continue;
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    // 只摘**模拟**类组件：ComponentHealth / ComponentVitalStats / ComponentSleep。
                    // ComponentFlu / ComponentSickness / ComponentOnFire 保留：它们的 Update 只做表现
                    // （喷嚏 / 呕吐粒子 / 火焰粒子），伤害路径被 IsInvulnerable 挡住，
                    // 病程值每次下发都会被覆盖。
                    if (!(updateable is ComponentHealth) && !(updateable is ComponentVitalStats) &&
                        !(updateable is ComponentSleep))
                        continue;
                    update.RemoveUpdateable(updateable);
                }
                // 直接 Injure(...) 的调用方（生物攻击 / 爆炸 / 摔落结算）走 invulnerability 兜底：
                // 我们自己的写入是字段赋值，不经过 Injure，所以不受它影响。
                if (player.ComponentHealth != null)
                {
                    m_parentField.ModifyParentField(player.ComponentHealth,
                        "<IsInvulnerable>k__BackingField", true, typeof(ComponentHealth));
                }
                added++;
                m_suppressedNew++;
            }
            if (m_suppressedTokens.Count > keys.Count)
            {
                var stale = new List<PlayerKey>();
                foreach (PlayerKey key in m_suppressedTokens.Keys)
                    if (!keys.Contains(key)) stale.Add(key);
                for (int i = 0; i < stale.Count; i++) m_suppressedTokens.Remove(stale[i]);
            }
            suppressedTotal = m_suppressedTokens.Count;
            return added;
        }

        // ================================================================ 主机侧：复活客户端化身

        public bool TryRespawnAvatar(PlayerKey key, out Vector3 position, out string reason)
        {
            position = Vector3.Zero;
            reason = null;
            if (!TryResolveAvatar(key, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            if (player.ComponentBody == null)
            {
                reason = "玩家键 " + key + " 的化身没有 ComponentBody（无法设置复活落点）";
                m_avatarRespawnFailures++;
                return false;
            }
            Vector3 spawn = playerData.SpawnPosition;
            if (!IsFinite(spawn) || spawn == Vector3.Zero)
            {
                SubsystemPlayers players = GameManager.Project?
                    .FindSubsystem<SubsystemPlayers>(false);
                spawn = players != null ? players.GlobalSpawnPosition : Vector3.Zero;
            }
            if (!IsFinite(spawn) || spawn == Vector3.Zero)
            {
                reason = "玩家键 " + key + " 没有可用的出生点（PlayerData.SpawnPosition 与 " +
                    "SubsystemPlayers.GlobalSpawnPosition 都是零 / 非有限值）→ 不猜落点";
                m_avatarRespawnFailures++;
                return false;
            }
            try
            {
                var evt = new PlayerVitalEventSnapshot(key, 1, PlayerVitalEventKind.Respawn,
                    string.Empty, true, spawn);
                ResetAfterRespawn(player, in evt);
                position = spawn;
                m_avatarRespawns++;
                return true;
            }
            catch (Exception e)
            {
                reason = "复活玩家键 " + key + " 的化身时抛异常：" + e.GetType().Name + " " +
                    e.Message;
                m_avatarRespawnFailures++;
                Error("respawn:" + key, reason, e);
                return false;
            }
        }

        private static bool IsFinite(Vector3 value) =>
            PlayerVitalsSnapshot.IsFiniteValue(value.X) &&
            PlayerVitalsSnapshot.IsFiniteValue(value.Y) &&
            PlayerVitalsSnapshot.IsFiniteValue(value.Z);

        // ================================================================ 抓取 / 写入公共实现

        private PlayerVitalsSnapshot CaptureOne(PlayerKey key, ComponentPlayer player)
        {
            ComponentHealth health = player.ComponentHealth;
            ComponentVitalStats vital = player.ComponentVitalStats;
            ComponentSleep sleep = player.ComponentSleep;
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            Vector3 position = player.ComponentBody != null
                ? player.ComponentBody.Position
                : Vector3.Zero;
            PlayerSleepState sleepState = CaptureSleepState(player, sleep);
            // revision 由模块持有并覆盖（WithRevision）；这里给 1 只是让快照自身通过校验。
            return new PlayerVitalsSnapshot(key, 1, health.Health, health.Air, vital.Food,
                vital.Stamina, vital.Sleep, vital.Temperature, vital.Wetness,
                m_parentField.GetParentField<float>(vital, "m_targetTemperature",
                    typeof(ComponentVitalStats)),
                flu != null
                    ? m_parentField.GetParentField<float>(flu, "m_fluDuration", typeof(ComponentFlu))
                    : 0f,
                flu != null
                    ? m_parentField.GetParentField<float>(flu, "m_fluOnset", typeof(ComponentFlu))
                    : 0f,
                sickness != null
                    ? m_parentField.GetParentField<float>(sickness, "m_sicknessDuration",
                        typeof(ComponentSickness))
                    : 0f,
                onFire != null
                    ? m_parentField.GetParentField<float>(onFire, "m_fireDuration",
                        typeof(ComponentOnFire))
                    : 0f,
                sleepState.Sleeping,
                health.CauseOfDeath,
                CaptureSatiation(vital),
                position,
                sleepState.SleepStartTime,
                sleepState.SleepElapsed,
                sleepState.AllowManualWake,
                sleepState.ForcedSleep);
        }

        /// <summary>
        /// 抓主机侧角色的睡眠权威字段（<c>ComponentSleep</c>）。
        ///   · 醒着 → 全默认（forced 恒 false，elapsed / start 都为 0）；
        ///   · 睡着且 <c>m_sleepStartTime &gt; 0</c> → 起始时 + 当前 elapsed + 手动唤醒允许；
        ///   · 睡着但起始时不可读（引擎 <c>Load()</c> 把持久化的 0.0 还原成 null / 老数据）→
        ///     只有 sleeping=true（客户端按"老格式"淡入 + Zzz，但没有提示 / 唤醒路径，计数
        ///     <c>sleepLegacy</c>），绝不编一个假的起始时间。
        /// Source: Survivalcraft/Game/ComponentSleep.cs:35/81-103/166-186
        /// </summary>
        private PlayerSleepState CaptureSleepState(ComponentPlayer player, ComponentSleep sleep)
        {
            if (sleep == null || !sleep.IsSleeping)
                return new PlayerSleepState(false, false, false, false, 0.0, 0f);
            double start = ReadSleepStartTime(sleep);
            if (start <= 0.0)
                return new PlayerSleepState(true, false, false, false, 0.0, 0f);
            bool allow = m_parentField.GetParentField<bool>(sleep, "m_allowManualWakeUp",
                typeof(ComponentSleep));
            double elapsed = CurrentGameTime(player) - start;
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0.0)
                elapsed = 0.0;
            if (elapsed > PlayerSleepLimits.MaxSleepElapsedSeconds)
                elapsed = PlayerSleepLimits.MaxSleepElapsedSeconds;
            return new PlayerSleepState(true, false, allow, !allow, start, (float)elapsed);
        }

        /// <summary>读 <c>ComponentSleep.m_sleepStartTime</c>（<c>double?</c>；null / 非有限 → 0）。</summary>
        private double ReadSleepStartTime(ComponentSleep sleep)
        {
            object raw = m_parentField.GetParentField(sleep, "m_sleepStartTime",
                typeof(ComponentSleep));
            if (!(raw is double value)) return 0.0;
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0) return 0.0;
            return value;
        }

        private IReadOnlyList<PlayerSatiation> CaptureSatiation(ComponentVitalStats vital)
        {
            Dictionary<int, float> satiation = m_parentField.GetParentField<Dictionary<int, float>>(
                vital, "m_satiation", typeof(ComponentVitalStats));
            if (satiation == null || satiation.Count == 0) return Array.Empty<PlayerSatiation>();
            var result = new List<PlayerSatiation>(Math.Min(satiation.Count,
                PlayerVitalsLimits.MaxSatiationEntries));
            foreach (KeyValuePair<int, float> item in satiation)
            {
                if (item.Key <= 0 || !(item.Value > 0f)) continue;
                if (result.Count >= PlayerVitalsLimits.MaxSatiationEntries) break;
                result.Add(new PlayerSatiation(item.Key, item.Value));
            }
            return result;
        }

        private enum ActivePlayerState
        {
            None = 0,
            Alive = 1,
            Dead = 2
        }

        /// <summary>
        /// 把一份主机权威值写进远端化身。返回写成了哪一类（Alive/Dead）；组件全缺时返回 None。
        /// 所有写入都是**字段赋值**：不经过 Injure / Heal，不产生伤害、不掉物品、不写地形。
        /// </summary>
        private ActivePlayerState ApplyRemoteState(ComponentPlayer player,
            in PlayerVitalsSnapshot snapshot)
        {
            ComponentHealth health = player.ComponentHealth;
            ComponentVitalStats vital = player.ComponentVitalStats;
            bool wrote = false;
            if (health != null)
            {
                float previous = health.Health;
                if (snapshot.Dead)
                {
                    if (previous > 0f) ApplyDeath(player, snapshot.CauseOfDeath);
                    else if (health.DeathTime == null) SetDeathTime(player);
                    WriteField(health, "<Health>k__BackingField", 0f, typeof(ComponentHealth));
                    m_parentField.ModifyParentField(health, "m_lastHealth", 0f,
                        typeof(ComponentHealth));
                }
                else
                {
                    float safeHealth = MathUtils.Saturate(snapshot.Health);
                    if (previous <= 0f) ResetDeathState(player);
                    WriteField(health, "<Health>k__BackingField", safeHealth,
                        typeof(ComponentHealth));
                    m_parentField.ModifyParentField(health, "m_lastHealth", safeHealth,
                        typeof(ComponentHealth));
                }
                m_parentField.ModifyParentField(health, "<Air>k__BackingField",
                    MathUtils.Saturate(snapshot.Air), typeof(ComponentHealth));
                m_healthWrites++;
                wrote = true;
            }
            if (vital != null)
            {
                WriteVitalStats(vital, in snapshot);
                m_vitalsWrites++;
                wrote = true;
            }
            if (player.ComponentSleep != null)
            {
                ApplySleep(player, snapshot.Sleeping);
                wrote = true;
            }
            WriteConditions(player, in snapshot);
            return wrote
                ? (snapshot.Dead ? ActivePlayerState.Dead : ActivePlayerState.Alive)
                : ActivePlayerState.None;
        }

        private void WriteVitalStats(ComponentVitalStats vital, in PlayerVitalsSnapshot snapshot)
        {
            float food = MathUtils.Saturate(snapshot.Food);
            float stamina = MathUtils.Saturate(snapshot.Stamina);
            float sleep = MathUtils.Saturate(snapshot.Sleep);
            float temperature = MathUtils.Clamp(snapshot.Temperature, 0f, 24f);
            float wetness = MathUtils.Saturate(snapshot.Wetness);
            float target = MathUtils.Clamp(snapshot.TargetTemperature, 0f, 24f);
            m_parentField.ModifyParentField(vital, "m_food", food, typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_stamina", stamina, typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_sleep", sleep, typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_temperature", temperature,
                typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_wetness", wetness, typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_targetTemperature", target,
                typeof(ComponentVitalStats));
            // 这些"上一次值"是引擎"跨越阈值才提示一次"的边沿判据
            //（ComponentVitalStats.cs:312/374/445/546/651）；不一起写会让远端化身在阈值附近刷提示。
            m_parentField.ModifyParentField(vital, "m_lastFood", food, typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_lastStamina", stamina,
                typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_lastSleep", sleep, typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_lastTemperature", temperature,
                typeof(ComponentVitalStats));
            m_parentField.ModifyParentField(vital, "m_lastWetness", wetness,
                typeof(ComponentVitalStats));
            Dictionary<int, float> satiation = m_parentField.GetParentField<Dictionary<int, float>>(
                vital, "m_satiation", typeof(ComponentVitalStats));
            if (satiation != null)
            {
                satiation.Clear();
                IReadOnlyList<PlayerSatiation> entries = snapshot.Satiation;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Contents <= 0 || !(entries[i].Value > 0f)) continue;
                    satiation[entries[i].Contents] = entries[i].Value;
                }
            }
        }

        private void WriteConditions(ComponentPlayer player, in PlayerVitalsSnapshot snapshot)
        {
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            if (flu != null)
            {
                m_parentField.ModifyParentField(flu, "m_fluDuration", MathUtils.Max(
                    snapshot.FluDuration, 0f), typeof(ComponentFlu));
                m_parentField.ModifyParentField(flu, "m_fluOnset", MathUtils.Max(
                    snapshot.FluOnset, 0f), typeof(ComponentFlu));
                m_conditionWrites++;
            }
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            if (sickness != null)
            {
                m_parentField.ModifyParentField(sickness, "m_sicknessDuration", MathUtils.Max(
                    snapshot.SicknessDuration, 0f), typeof(ComponentSickness));
                m_conditionWrites++;
            }
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            if (onFire != null)
            {
                // 远端化身的 ComponentOnFire.Update 保留（表现）；本地玩家的 Update 被摘掉，
                // 由 ApplyLocalVitals 里的 WriteLocalFire 维护自己的粒子表现。
                // 写精确的权威时长（不用 SetOnFire 的 max：主机火焰衰减时远端要跟着降）。
                m_parentField.ModifyParentField(onFire, "m_fireDuration",
                    MathUtils.Max(snapshot.FireDuration, 0f), typeof(ComponentOnFire));
                m_conditionWrites++;
            }
        }

        /// <summary>
        /// **本机角色**的睡眠本体（主机权威）：写 <c>m_sleepStartTime</c>（= IsSleeping，
        /// 驱动 <c>ComponentHumanModel</c> 躺下姿势）与 <c>m_allowManualWakeUp</c>。
        ///   · <c>m_sleepFactor</c> **不在这里写**：淡入 0.33/s / 淡出 1/s 由
        ///     <see cref="RefreshSleepPresentation"/> 按引擎速度推进（恢复引擎表现，而不是
        ///     瞬间跳到 1/0）；
        ///   · 不调用 <c>Sleep()</c> / <c>WakeUp()</c>：那两个会写统计 / SpawnPosition，属于主机权威；
        ///   · 老版本主机没有 start（0）时用客户端当前游戏时占位，保证姿势正确（提示 / 唤醒
        ///     路径此时不会出现，见 <see cref="PlayerSleepState.HasPresentation"/>）。
        /// </summary>
        private void ApplyLocalSleep(ComponentPlayer player, in PlayerVitalsSnapshot snapshot)
        {
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null) return;
            if (snapshot.Sleeping)
            {
                double start = snapshot.SleepStartTime > 0.0
                    ? snapshot.SleepStartTime
                    : CurrentGameTime(player);
                double current = ReadSleepStartTime(sleep);
                if (!sleep.IsSleeping || !current.Equals(start))
                {
                    m_parentField.ModifyParentField(sleep, "m_sleepStartTime", (object)start,
                        typeof(ComponentSleep));
                    m_parentField.ModifyParentField(sleep, "m_messageFactor", 0f,
                        typeof(ComponentSleep));
                }
                m_parentField.ModifyParentField(sleep, "m_allowManualWakeUp",
                    snapshot.AllowManualWake, typeof(ComponentSleep));
            }
            else if (sleep.IsSleeping)
            {
                m_parentField.ModifyParentField(sleep, "m_sleepStartTime", null,
                    typeof(ComponentSleep));
                m_parentField.ModifyParentField(sleep, "m_messageFactor", 0f,
                    typeof(ComponentSleep));
            }
        }

        /// <summary>
        /// 睡眠姿势（**远端化身**）：<c>ComponentHumanModel.Update</c> 用
        /// <c>ComponentSleep.IsSleeping</c> 决定躺下（Source: ComponentHumanModel.cs:86-94）。
        /// 直接写 <c>m_sleepStartTime</c>（不调 <c>Sleep()</c> / <c>WakeUp()</c>：那两个会写统计与
        /// SpawnPosition，属于主机的权威）。远端化身没有自己的视图屏幕，factor 仍按"睡=1 / 醒=0"
        /// 瞬切（只有本机角色与 H3 视图需要按引擎速度淡入 / 淡出）。
        /// </summary>
        private void ApplySleep(ComponentPlayer player, bool sleeping)
        {
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null) return;
            bool isSleeping = sleep.IsSleeping;
            if (sleeping == isSleeping) return;
            if (sleeping)
            {
                m_parentField.ModifyParentField(sleep, "m_sleepStartTime",
                    (object)CurrentGameTime(player), typeof(ComponentSleep));
                m_parentField.ModifyParentField(sleep, "m_sleepFactor", 1f, typeof(ComponentSleep));
            }
            else
            {
                m_parentField.ModifyParentField(sleep, "m_sleepStartTime", null,
                    typeof(ComponentSleep));
                m_parentField.ModifyParentField(sleep, "m_sleepFactor", 0f, typeof(ComponentSleep));
                m_parentField.ModifyParentField(sleep, "m_messageFactor", 0f, typeof(ComponentSleep));
            }
            m_sleepWrites++;
        }

        private static double CurrentGameTime(ComponentPlayer player) =>
            player.Project?.FindSubsystem<SubsystemGameInfo>(false)?.TotalElapsedGameTime ?? 0.0;

        // ================================================================ 客户端侧：睡眠表现 + 点击唤醒

        public bool NoteLocalSleepReport(PlayerKey key, in PlayerVitalsSnapshot snapshot,
            out string reason)
        {
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            try
            {
                if (!NoteLocalSleepState(key.Slot, player, in snapshot))
                {
                    reason = "本机槽位 " + key.Slot +
                        " 没有 ComponentSleep（睡眠表现无法跟随主机）";
                    m_sleepNoteFailures++;
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                m_sleepNoteFailures++;
                reason = "记录本机槽位 " + key.Slot + " 的睡眠状态时抛异常：" +
                    e.GetType().Name + " " + e.Message;
                Error("local-sleep-note:" + key.Slot, reason, e);
                return false;
            }
        }

        /// <summary>
        /// 把一份主机权威睡眠状态写进表现缓存（不写引擎字段）。**心跳也会调用**：同一 revision 的
        /// 心跳携带新的 <c>sleep_elapsed</c>，用来校准客户端外推起点。返回 false = 没有
        /// ComponentSleep（例如角色正在重建）。
        /// </summary>
        private bool NoteLocalSleepState(int slot, ComponentPlayer player,
            in PlayerVitalsSnapshot snapshot)
        {
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null) return false;
            LocalSleepState state = GetOrCreateLocalSleep(slot, player);
            bool sleeping = snapshot.Sleeping;
            bool newEpisode = sleeping &&
                (!state.Sleeping || !state.SleepStartTime.Equals(snapshot.SleepStartTime));
            if (newEpisode || (!sleeping && state.Sleeping)) state.MessageFactor = 0f;
            state.Sleeping = sleeping;
            state.Dead = snapshot.Dead;
            state.AllowManualWake = snapshot.AllowManualWake;
            state.ForcedSleep = snapshot.ForcedSleep;
            state.HasPresentation = snapshot.SleepStartTime > 0.0;
            state.SleepStartTime = snapshot.SleepStartTime;
            state.ElapsedAtReport = snapshot.SleepElapsed;
            state.GameTimeAtReport = CurrentGameTime(player);
            return true;
        }

        private LocalSleepState GetOrCreateLocalSleep(int slot, ComponentPlayer player)
        {
            if (!m_localSleep.TryGetValue(slot, out LocalSleepState state))
            {
                state = new LocalSleepState();
                m_localSleep[slot] = state;
            }
            int token = Token(player);
            if (state.EntityToken != token)
            {
                // 角色实体换代（复活 / 重建）= 旧睡眠表现作废，从 0 开始淡入。
                state.EntityToken = token;
                state.Factor = 0f;
                state.MessageFactor = 0f;
                state.ControlsOwned = false;
                state.Sleeping = false;
                state.ForcedReported = false;
                state.LegacyReported = false;
            }
            return state;
        }

        /// <summary>
        /// **客户端每帧**：用主机权威睡眠状态 + 客户端（已同步的）世界时钟推进表现，写
        /// <c>m_sleepFactor</c> / <c>BlackoutFactor</c> / Zzz / 提示 / 控件可见性。
        ///
        /// 为什么每帧：<c>ComponentScreenOverlays.Update</c>（UpdateOrder.Reset）把
        /// Blackout / FloatingMessage / Message 每帧清 0，而客户端本机角色的
        /// <c>ComponentSleep.Update</c> 已被摘掉且**绝不重新启用**；表现只能由这里补。
        /// 本方法只写**表现字段**，不做任何唤醒决定。
        /// </summary>
        public PlayerSleepPresentationCounts RefreshSleepPresentation(float dt, out string reason)
        {
            reason = null;
            int applied = 0;
            int zzz = 0;
            int blackout = 0;
            int forced = 0;
            int legacy = 0;
            int missing = 0;
            int failures = 0;
            if (m_localSleep.Count == 0) return PlayerSleepPresentationCounts.Empty;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（本机睡眠表现无法刷新）";
                return new PlayerSleepPresentationCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
            }
            if (m_disabledLocalSlots.Count == 0)
            {
                // 本地模拟没有被摘掉 = 引擎自己的 ComponentSleep.Update 在驱动表现：
                // 绝不与引擎抢写，清掉缓存即可（离开房间 / 断线后恢复本端模拟）。
                m_localSleep.Clear();
                return PlayerSleepPresentationCounts.Empty;
            }
            string firstFailure = null;
            foreach (KeyValuePair<int, LocalSleepState> item in m_localSleep)
            {
                int slot = item.Key;
                LocalSleepState state = item.Value;
                if (!TryResolveLocal(slot, out PlayerData playerData, out ComponentPlayer player,
                        out string resolveReason))
                {
                    missing++;
                    if (firstFailure == null)
                        firstFailure = "槽位 " + slot + "：" + resolveReason;
                    continue;
                }
                if (state.EntityToken != Token(player))
                    state = GetOrCreateLocalSleep(slot, player);
                var sleepState = new PlayerSleepState(state.Sleeping, state.Dead,
                    state.AllowManualWake, state.ForcedSleep, state.SleepStartTime,
                    state.ElapsedAtReport);
                float elapsed = state.Sleeping ? EstimateSleepElapsed(player, state) : 0f;
                PlayerSleepFrame frame = PlayerSleepRules.Advance(in sleepState, elapsed,
                    state.Factor, state.MessageFactor, dt);
                bool needsWrite = frame.PresentationActive || state.ControlsOwned;
                if (!needsWrite)
                {
                    state.Factor = frame.Factor;
                    state.MessageFactor = frame.MessageFactor;
                    state.ControlsOwned = false;
                    if (!state.Sleeping)
                    {
                        state.ForcedReported = false;
                        state.LegacyReported = false;
                    }
                    continue;
                }
                if (!WriteSleepPresentation(player, in frame, out string writeReason))
                {
                    failures++;
                    if (firstFailure == null)
                        firstFailure = "槽位 " + slot + "：" + writeReason;
                    continue;
                }
                state.Factor = frame.Factor;
                state.MessageFactor = frame.MessageFactor;
                state.ControlsOwned = frame.ControlsHidden;
                applied++;
                if (frame.ZzzVisible) zzz++;
                if (frame.BlackoutFactor > 0f) blackout++;
                if (state.Sleeping && state.ForcedSleep && !state.ForcedReported)
                {
                    forced++;
                    state.ForcedReported = true;
                }
                if (state.Sleeping && !state.HasPresentation && !state.LegacyReported)
                {
                    legacy++;
                    state.LegacyReported = true;
                }
                if (!state.Sleeping)
                {
                    state.ForcedReported = false;
                    state.LegacyReported = false;
                }
            }
            if (applied > 0) m_sleepPresentationRuns++;
            reason = firstFailure;
            return new PlayerSleepPresentationCounts(applied, zzz, blackout, forced, 0, legacy, 0,
                0, missing, failures);
        }

        /// <summary>
        /// 客户端外推的睡眠时长（秒）：<c>sleep_elapsed</c>（抓取时刻）+ （客户端当前
        /// <c>TotalElapsedGameTime</c> - 收到该值时的游戏时）。游戏时由世界信息持续对齐
        /// （<c>ClientReplicaEngine</c> 的 0.35 s snap 阈值），所以与主机口径一致；世界时钟
        /// 回拨（snap 向后）时按 0 处理，绝不产生负的睡眠时长。
        /// </summary>
        private float EstimateSleepElapsed(ComponentPlayer player, LocalSleepState state)
        {
            double delta = CurrentGameTime(player) - state.GameTimeAtReport;
            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0.0) delta = 0.0;
            double elapsed = state.ElapsedAtReport + delta;
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0.0)
                elapsed = 0.0;
            if (elapsed > PlayerSleepLimits.MaxSleepElapsedSeconds)
                elapsed = PlayerSleepLimits.MaxSleepElapsedSeconds;
            return (float)elapsed;
        }

        public bool TryReadLocalSleep(PlayerKey key, out PlayerSleepViewState state,
            out string reason)
        {
            state = default;
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            if (!m_localSleep.TryGetValue(key.Slot, out LocalSleepState cached))
            {
                reason = "本机槽位 " + key.Slot + " 还没有收到主机的睡眠状态";
                return false;
            }
            if (cached.EntityToken != Token(player))
            {
                reason = "本机槽位 " + key.Slot + " 的角色实体已换代（旧睡眠状态作废）";
                return false;
            }
            float elapsed = cached.Sleeping ? EstimateSleepElapsed(player, cached) : 0f;
            state = new PlayerSleepViewState(cached.Sleeping, cached.Dead,
                cached.AllowManualWake, cached.ForcedSleep, cached.HasPresentation,
                cached.SleepStartTime, elapsed);
            return true;
        }

        /// <summary>
        /// 睡眠中的输入（tap / click / key）：口径与引擎
        /// <c>ComponentSleep.Update:133</c> 完全一致 ——
        /// <c>!DialogsManager.HasDialogs(GameWidget) &amp;&amp; GameWidget.Input.Any</c>。
        /// 只读，不发送、不清输入（清输入只在真正发请求时做）。
        /// </summary>
        public bool TryPollLocalSleepTap(PlayerKey key, out bool tapped, out string reason)
        {
            tapped = false;
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            GameWidget widget = player.GameWidget;
            if (widget == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 GameWidget（无法读睡眠输入）";
                return false;
            }
            try
            {
                tapped = !DialogsManager.HasDialogs(player.GuiWidget) && widget.Input.Any;
                return true;
            }
            catch (Exception e)
            {
                reason = "读取睡眠输入异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>真正发出唤醒请求时清掉本帧输入（引擎在 <c>WakeUp()</c> 前做 <c>Input.Clear()</c>）。</summary>
        public bool TryConsumeLocalSleepInput(PlayerKey key, out string reason)
        {
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            GameWidget widget = player.GameWidget;
            if (widget == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 GameWidget（无法清睡眠输入）";
                return false;
            }
            try
            {
                widget.Input.Clear();
                return true;
            }
            catch (Exception e)
            {
                reason = "清睡眠输入异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        // ================================================================ 主机侧：唤醒权威（引擎 WakeUp）

        public bool TryReadAuthoritySleep(PlayerKey key, out PlayerSleepAuthorityState state,
            out string reason)
        {
            state = default;
            reason = null;
            if (!TryResolveAuthorityPlayer(key, out PlayerData playerData,
                    out ComponentPlayer player, out reason))
                return false;
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null)
            {
                reason = "玩家键 " + key + " 没有 ComponentSleep（无法读取睡眠权威）";
                return false;
            }
            bool sleeping = sleep.IsSleeping;
            bool dead = player.ComponentHealth != null && player.ComponentHealth.Health <= 0f;
            bool allow = m_parentField.GetParentField<bool>(sleep, "m_allowManualWakeUp",
                typeof(ComponentSleep));
            double start = ReadSleepStartTime(sleep);
            float elapsed = 0f;
            if (sleeping && start > 0.0)
            {
                double value = CurrentGameTime(player) - start;
                if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0)
                    elapsed = (float)Math.Min(value,
                        (double)PlayerSleepLimits.MaxSleepElapsedSeconds);
            }
            state = new PlayerSleepAuthorityState(sleeping, dead, allow, start, elapsed);
            m_authoritySleepReads++;
            return true;
        }

        /// <summary>
        /// 主机：对主机侧那个角色调用**引擎** <c>ComponentSleep.WakeUp()</c>
        /// （清 <c>m_sleepStartTime</c>，引擎自己把 SpawnPosition 写成当前位置）。
        /// 只由校验通过的 <c>PLAYER_WAKE_REQUEST</c> 调用；客户端没有任何本地唤醒路径。
        /// </summary>
        public bool TryWakeAuthorityPlayer(PlayerKey key, out string reason)
        {
            reason = null;
            if (!TryResolveAuthorityPlayer(key, out PlayerData playerData,
                    out ComponentPlayer player, out reason))
                return false;
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null)
            {
                reason = "玩家键 " + key + " 没有 ComponentSleep（无法调用引擎 WakeUp）";
                return false;
            }
            try
            {
                sleep.WakeUp();
                m_authorityWakes++;
                return true;
            }
            catch (Exception e)
            {
                reason = "对玩家键 " + key + " 调用 ComponentSleep.WakeUp() 抛异常：" +
                    e.GetType().Name + " " + e.Message;
                Error("authority-wake:" + key, reason, e);
                return false;
            }
        }

        private bool TryResolveAuthorityPlayer(PlayerKey key, out PlayerData playerData,
            out ComponentPlayer player, out string reason)
        {
            if (key.ClientId == 0)
                return TryResolveLocal(key.Slot, out playerData, out player, out reason);
            return TryResolveAvatar(key, out playerData, out player, out reason);
        }

        // ================================================================ 主机侧：H3 第三分屏睡眠表现镜像

        /// <summary>
        /// **主机每帧**：H3（replace 模式）第三个分屏显示的是 H3 自己的
        /// <c>ComponentGui / ComponentScreenOverlays</c>（<c>ViewWidget.Draw</c> 只认自己 GameWidget
        /// 的 PlayerData），但相机 Target 是客户端角色；H3 自己的
        /// <c>ComponentSleep.Update</c> 已被调试镜像摘掉。于是这里把"被镜像客户端"的睡眠表现
        /// 用**同一个写入器**写到 H3（视图所有者）身上。
        ///
        /// 与 <c>Engine/MirrorCharacterEngine.cs</c> 解耦：不引用它的内部状态，只按 replace 模式的
        /// 公开特征识别绑定 —— <c>GameWidget.Target != 自己</c> 且 Target 是 <c>NetworkAvatarSpawner</c>
        /// 里的某个客户端化身。只写表现字段（<c>m_sleepFactor</c> / overlays / 控件），
        /// **不写** <c>m_sleepStartTime</c> / <c>m_allowManualWakeUp</c>：H3 的存档状态与
        /// 镜像引擎的"不复制任何状态"口径保持不变。
        /// </summary>
        public PlayerSleepPresentationCounts RefreshMirrorSleepPresentation(float dt,
            out string reason)
        {
            reason = null;
            int applied = 0;
            int cleared = 0;
            int failures = 0;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（H3 分屏睡眠表现无法刷新）";
                return new PlayerSleepPresentationCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
            }
            if (!m_spawner.TryCollectLocalPlayers(project, m_mirrorRefreshBuffer, out bool tooMany,
                    out string localReason))
            {
                reason = "读取本机角色失败：" + (localReason ?? "端口没有给原因");
                return new PlayerSleepPresentationCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
            }
            if (tooMany)
                Problem("sleep-mirror-too-many", "本机角色数超过引擎上限（" + localReason +
                    "）→ H3 睡眠表现镜像只处理前 " + PlayerKey.SlotCount + " 个槽位");
            m_mirrorPresent.Clear();
            string firstFailure = null;
            for (int slot = 0; slot < m_mirrorRefreshBuffer.Count; slot++)
            {
                ComponentPlayer owner = m_mirrorRefreshBuffer[slot]?.ComponentPlayer;
                if (owner?.Entity == null) continue;
                m_mirrorPresent.Add(slot);
                GameWidget widget = owner.GameWidget;
                ComponentCreature target = widget?.Target;
                bool bound = target != null && target.Entity != null &&
                    target.Entity != owner.Entity;
                if (!bound)
                {
                    if (!m_mirrorSleep.TryGetValue(slot, out MirrorSleepState old)) continue;
                    m_mirrorSleep.Remove(slot);
                    m_sleepMirrorClears++;
                    if (!ClearMirrorSleep(old, out string clearReason))
                    {
                        failures++;
                        if (firstFailure == null)
                            firstFailure = "槽位 " + slot + "：" + clearReason;
                        continue;
                    }
                    cleared++;
                    continue;
                }
                ComponentPlayer source = target as ComponentPlayer;
                if (source == null || !TryFindAvatarKey(source, out PlayerKey sourceKey))
                    continue;   // 不是"客户端角色"的镜像视图（例如别的本机角色）→ 不是本功能的范围
                ComponentSleep sourceSleep = source.ComponentSleep;
                if (sourceSleep == null)
                {
                    failures++;
                    if (firstFailure == null)
                        firstFailure = "槽位 " + slot + "：客户端 " + sourceKey +
                            " 没有 ComponentSleep";
                    continue;
                }
                MirrorSleepState state = GetOrCreateMirrorSleep(slot, owner);
                bool sleeping = sourceSleep.IsSleeping;
                bool allow = m_parentField.GetParentField<bool>(sourceSleep,
                    "m_allowManualWakeUp", typeof(ComponentSleep));
                bool dead = source.ComponentHealth != null &&
                    source.ComponentHealth.Health <= 0f;
                double start = ReadSleepStartTime(sourceSleep);
                float elapsed = 0f;
                if (sleeping && start > 0.0)
                {
                    double value = CurrentGameTime(owner) - start;
                    if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0)
                        elapsed = (float)Math.Min(value,
                            (double)PlayerSleepLimits.MaxSleepElapsedSeconds);
                }
                var sleepState = new PlayerSleepState(sleeping, dead, allow,
                    sleeping && !allow, start, elapsed);
                PlayerSleepFrame frame = PlayerSleepRules.Advance(in sleepState, elapsed,
                    state.Factor, state.MessageFactor, dt);
                bool needsWrite = frame.PresentationActive || state.ControlsOwned;
                if (!needsWrite)
                {
                    state.Factor = frame.Factor;
                    state.MessageFactor = frame.MessageFactor;
                    state.ControlsOwned = false;
                    continue;
                }
                if (!WriteSleepPresentation(owner, in frame, out string writeReason))
                {
                    failures++;
                    if (firstFailure == null)
                        firstFailure = "槽位 " + slot + "：" + writeReason;
                    continue;
                }
                state.Factor = frame.Factor;
                state.MessageFactor = frame.MessageFactor;
                state.ControlsOwned = frame.ControlsHidden;
                applied++;
            }
            if (m_mirrorSleep.Count > 0)
            {
                m_mirrorStale.Clear();
                foreach (int slot in m_mirrorSleep.Keys)
                    if (!m_mirrorPresent.Contains(slot)) m_mirrorStale.Add(slot);
                for (int i = 0; i < m_mirrorStale.Count; i++)
                {
                    int slot = m_mirrorStale[i];
                    MirrorSleepState old = m_mirrorSleep[slot];
                    m_mirrorSleep.Remove(slot);
                    m_sleepMirrorClears++;
                    if (!ClearMirrorSleep(old, out string clearReason))
                    {
                        failures++;
                        if (firstFailure == null)
                            firstFailure = "槽位 " + slot + "：" + clearReason;
                        continue;
                    }
                    cleared++;
                }
            }
            if (applied > 0) m_sleepMirrorRuns++;
            if (failures > 0) m_sleepMirrorFailures++;
            reason = firstFailure;
            return new PlayerSleepPresentationCounts(0, 0, 0, 0, 0, 0, applied, cleared, 0,
                failures);
        }

        private MirrorSleepState GetOrCreateMirrorSleep(int slot, ComponentPlayer owner)
        {
            if (!m_mirrorSleep.TryGetValue(slot, out MirrorSleepState state))
            {
                state = new MirrorSleepState();
                m_mirrorSleep[slot] = state;
            }
            int token = Token(owner);
            if (state.OwnerToken != token)
            {
                state.OwnerToken = token;
                state.Factor = 0f;
                state.MessageFactor = 0f;
                state.ControlsOwned = false;
            }
            state.Owner = owner;
            return state;
        }

        /// <summary>视图解绑 / 所有者消失：把 H3 的表现写回"醒着"（否则 SleepFactor 会永远 &gt; 0）。</summary>
        private bool ClearMirrorSleep(MirrorSleepState state, out string reason)
        {
            reason = null;
            ComponentPlayer owner = state.Owner;
            if (owner?.Entity == null || !owner.Entity.IsAddedToProject)
            {
                reason = "视图所有者的实体已不存在（无需写回表现）";
                return true;
            }
            var frame = new PlayerSleepFrame(0f, 0f, 0f, false, 0f,
                PlayerSleepMessageKind.None, null, false, 0f, false);
            if (!WriteSleepPresentation(owner, in frame, out reason)) return false;
            state.Factor = 0f;
            state.MessageFactor = 0f;
            state.ControlsOwned = false;
            return true;
        }

        /// <summary>客户端化身 → 玩家键（主机镜像视图的 Target 反查；化身表 ≤ 4 项）。</summary>
        private bool TryFindAvatarKey(ComponentPlayer source, out PlayerKey key)
        {
            key = default;
            if (source == null) return false;
            foreach (PlayerKey candidate in m_spawner.Keys)
            {
                if (!m_spawner.TryGet(candidate, out PlayerData data)) continue;
                if (data?.ComponentPlayer == source)
                {
                    key = candidate;
                    return true;
                }
            }
            return false;
        }

        // ================================================================ 睡眠表现写入器（本机 / H3 镜像共用）

        /// <summary>
        /// 把一帧表现写进角色的引擎显示层（**本机角色与 H3 视图所有者共用**）：
        ///   · <c>ComponentSleep.m_sleepFactor</c>（引擎 <c>SleepFactor</c> 属性 / <c>ComponentGui</c>
        ///     的控件判定 / <c>SubsystemTime</c> 的"所有人都在睡"加速都读它）；
        ///   · <c>ComponentScreenOverlays.BlackoutFactor = max(现有, factor)</c>（引擎同款）；
        ///   · <c>FloatingMessage = "Zzz..."</c> + <c>FloatingMessageFactor</c>；
        ///   · <c>Message</c> / <c>MessageFactor</c>（引擎原文的两句提示）；
        ///   · <c>ControlsContainerWidget.IsVisible</c> = 引擎
        ///     <c>IsReadyForPlaying &amp;&amp; ActiveCamera.IsEntityControlEnabled &amp;&amp; SleepFactor &lt;= 0</c>。
        /// 只写表现字段；<c>m_sleepStartTime</c> / <c>m_allowManualWakeUp</c> 由
        /// <see cref="ApplyLocalSleep"/> 按主机权威写（H3 镜像路径不写，保持其存档状态）。
        /// </summary>
        private bool WriteSleepPresentation(ComponentPlayer player, in PlayerSleepFrame frame,
            out string reason)
        {
            reason = null;
            if (player == null)
            {
                reason = "角色为空";
                return false;
            }
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null)
            {
                reason = "角色没有 ComponentSleep";
                return false;
            }
            ComponentScreenOverlays overlays = player.ComponentScreenOverlays;
            if (overlays == null)
            {
                reason = "角色没有 ComponentScreenOverlays（黑屏 / 提示无法显示）";
                return false;
            }
            ComponentGui gui = player.ComponentGui;
            if (gui == null || gui.ControlsContainerWidget == null)
            {
                reason = "角色没有 ComponentGui.ControlsContainerWidget（控件无法隐藏）";
                return false;
            }
            m_parentField.ModifyParentField(sleep, "m_sleepFactor", frame.Factor,
                typeof(ComponentSleep));
            overlays.BlackoutFactor = MathUtils.Max(overlays.BlackoutFactor,
                frame.BlackoutFactor);
            overlays.FloatingMessage = frame.ZzzVisible ? PlayerSleepTexts.Zzz : null;
            overlays.FloatingMessageFactor = frame.ZzzFactor;
            overlays.Message = frame.MessageText;
            overlays.MessageFactor = frame.MessageFactor;
            GameWidget widget = player.GameWidget;
            bool ready = player.PlayerData != null && player.PlayerData.IsReadyForPlaying;
            bool entityControl = widget != null && widget.ActiveCamera != null &&
                widget.ActiveCamera.IsEntityControlEnabled;
            gui.ControlsContainerWidget.IsVisible = ready && entityControl &&
                !frame.ControlsHidden;
            return true;
        }

        /// <summary>
        /// 远端化身死亡视觉路径（**只表现**）：<c>DeathTime</c> + <c>CauseOfDeath</c> + 同一个
        /// <c>KillParticleSystem</c>（引擎源码 267-278 的粒子/时间部分）。
        /// <c>ComponentCreatureModel.Update</c> 看到 <c>Health == 0</c> 会把 <c>DeathPhase</c>
        /// 推到 1（躺下）；化身 ComponentHealth 更新已被摘掉，所以这里显式做这一步。
        /// 掉落物品是**主机**的权威结果，这里绝不做。
        /// </summary>
        private void ApplyDeath(ComponentPlayer player, string cause)
        {
            ComponentHealth health = player.ComponentHealth;
            ComponentBody body = player.ComponentBody;
            if (health == null) return;
            WriteField(health, "<Health>k__BackingField", 0f, typeof(ComponentHealth));
            m_parentField.ModifyParentField(health, "m_lastHealth", 0f, typeof(ComponentHealth));
            m_parentField.ModifyParentField(health, "<HealthChange>k__BackingField", 0f,
                typeof(ComponentHealth));
            string causeText = string.IsNullOrEmpty(cause) ? "Unknown" : cause;
            m_parentField.ModifyParentField(health, "<CauseOfDeath>k__BackingField", causeText,
                typeof(ComponentHealth));
            SetDeathTime(player);
            SpawnDeathParticles(player, body);
            m_deathVisuals++;
        }

        private void SetDeathTime(ComponentPlayer player)
        {
            ComponentHealth health = player.ComponentHealth;
            if (health == null) return;
            double now = CurrentGameTime(player);
            m_parentField.ModifyParentField(health, "<DeathTime>k__BackingField", (object)now,
                typeof(ComponentHealth));
        }

        /// <summary>清掉死亡状态与死亡姿势（复活路径）。</summary>
        private void ResetDeathState(ComponentPlayer player)
        {
            ComponentHealth health = player.ComponentHealth;
            if (health != null)
            {
                m_parentField.ModifyParentField(health, "<DeathTime>k__BackingField", null,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<CauseOfDeath>k__BackingField",
                    string.Empty, typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<HealthChange>k__BackingField", 0f,
                    typeof(ComponentHealth));
            }
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model != null)
            {
                m_parentField.ModifyParentField(model, "<DeathPhase>k__BackingField", 0f,
                    typeof(ComponentCreatureModel));
                m_parentField.ModifyParentField(model, "<DeathCauseOffset>k__BackingField",
                    Vector3.Zero, typeof(ComponentCreatureModel));
            }
            ComponentSpawn spawn = player.Entity.FindComponent<ComponentSpawn>();
            if (spawn != null)
            {
                m_parentField.ModifyParentField(spawn, "<DespawnTime>k__BackingField", null,
                    typeof(ComponentSpawn));
            }
        }

        /// <summary>
        /// 复活：照旧实现 <c>ResetNetworkPlayerAfterRespawn</c> / <c>ResetNetworkPlayerVitals</c> 逐字段复位。
        /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:3123-3260
        /// </summary>
        private void ResetAfterRespawn(ComponentPlayer player, in PlayerVitalEventSnapshot evt)
        {
            ComponentBody body = player.ComponentBody;
            if (body != null)
            {
                if (evt.HasPosition)
                {
                    body.Position = evt.Position;
                    body.Velocity = Vector3.Zero;
                }
                body.TargetCrouchFactor = 0f;
            }
            ComponentHealth health = player.ComponentHealth;
            if (health != null)
            {
                WriteField(health, "<Health>k__BackingField", FullHealth, typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "<Air>k__BackingField", 1f,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(health, "m_lastHealth", FullHealth,
                    typeof(ComponentHealth));
            }
            ResetDeathState(player);
            ComponentVitalStats vital = player.ComponentVitalStats;
            if (vital != null)
            {
                m_parentField.ModifyParentField(vital, "m_food", 1f, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_stamina", 1f, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_sleep", 1f, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_temperature", DefaultTemperature,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_wetness", 0f, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastFood", 1f, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastStamina", 1f,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastSleep", 1f, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastTemperature", DefaultTemperature,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastWetness", 0f,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_environmentTemperature", 8f,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_targetTemperature", DefaultTemperature,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_targetTemperatureFlux", 0f,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_sleepBlackoutFactor", 0f,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_sleepBlackoutDuration", 0f,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_temperatureBlackoutFactor", 0f,
                    typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_temperatureBlackoutDuration", 0f,
                    typeof(ComponentVitalStats));
                Dictionary<int, float> satiation =
                    m_parentField.GetParentField<Dictionary<int, float>>(vital, "m_satiation",
                        typeof(ComponentVitalStats));
                satiation?.Clear();
            }
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep != null)
            {
                m_parentField.ModifyParentField(sleep, "m_sleepStartTime", null,
                    typeof(ComponentSleep));
                m_parentField.ModifyParentField(sleep, "m_sleepFactor", 0f, typeof(ComponentSleep));
                m_parentField.ModifyParentField(sleep, "m_messageFactor", 0f, typeof(ComponentSleep));
            }
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            if (flu != null)
            {
                string[] fields = { "m_fluOnset", "m_fluDuration", "m_coughDuration",
                    "m_sneezeDuration", "m_blackoutDuration", "m_blackoutFactor" };
                for (int i = 0; i < fields.Length; i++)
                    m_parentField.ModifyParentField(flu, fields[i], 0f, typeof(ComponentFlu));
            }
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            if (sickness != null)
            {
                if (m_parentField.GetParentField(sickness, "m_pukeParticleSystem",
                        typeof(ComponentSickness)) is PukeParticleSystem puke)
                    puke.IsStopped = true;
                m_parentField.ModifyParentField(sickness, "m_pukeParticleSystem", null,
                    typeof(ComponentSickness));
                m_parentField.ModifyParentField(sickness, "m_sicknessDuration", 0f,
                    typeof(ComponentSickness));
                m_parentField.ModifyParentField(sickness, "m_greenoutDuration", 0f,
                    typeof(ComponentSickness));
                m_parentField.ModifyParentField(sickness, "m_greenoutFactor", 0f,
                    typeof(ComponentSickness));
            }
            player.Entity.FindComponent<ComponentOnFire>()?.Extinguish();
            m_respawnResets++;
        }

        private void SpawnDeathParticles(ComponentPlayer player, ComponentBody body)
        {
            if (body == null) return;
            Project project = player.Project;
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            if (particles == null || terrain == null)
            {
                m_deathParticleFailures++;
                Problem("death-particles:" + player.PlayerData?.PlayerIndex,
                    "死亡粒子没有生成：Project 缺少 SubsystemParticles / SubsystemTerrain" +
                    "（死亡姿势仍然由 Health == 0 驱动，不受影响）");
                return;
            }
            // Source: Survivalcraft/Game/ComponentHealth.cs:267-271（同一位置 / 同一尺寸）
            Vector3 position = body.Position + new Vector3(0f, body.StanceBoxSize.Y / 2f, 0f);
            particles.AddParticleSystem(new KillParticleSystem(terrain, position,
                body.StanceBoxSize.X));
        }

        // ================================================================ 诊断

        public string Describe()
        {
            return "vitalsEngine{authorityCap=" + m_authorityCaptures + "/" +
                m_authorityCaptureFailures + " polls=" + m_slotPolls +
                " healthWrites=" + m_healthWrites + " vitalWrites=" + m_vitalsWrites +
                " condWrites=" + m_conditionWrites + " sleepWrites=" + m_sleepWrites +
                " deathVisuals=" + m_deathVisuals + " respawnResets=" + m_respawnResets +
                " localApply=" + m_localApplies + " localDeaths=" + m_localDeaths +
                " localRespawns=" + m_localRespawns + " deathScreens=" +
                m_deathScreensShown + "/" + m_deathScreenFailures + " deathCamFail=" +
                m_deathCameraFailures + " terminalTaps=" + m_terminalDeathTaps +
                " rejectedUi=" + m_respawnRejectedMessages + " hudWrites=" + m_hudWrites +
                " hudRefresh=" + m_hudPresentationRefreshes + " tempTex=" +
                m_hudTemperatureTextures + " ice=" + m_hudIceWrites + " hudFail=" +
                m_hudPresentationFailures + " hudNonFinite=" + m_hudNonFinite +
                " localSimOff=" + m_localSimDisabledNew + "/" + m_localSimDisableRuns +
                " localSimOn=" + m_localSimRestores + " avatarReads=" + m_avatarReads +
                " noAvatar=" + m_noAvatar + " applyFail=" + m_applyFailures +
                " avatarRespawns=" + m_avatarRespawns + "/" + m_avatarRespawnFailures +
                " suppressRuns=" + m_suppressRuns + " suppressedNew=" + m_suppressedNew +
                " suppressed=" + m_suppressedTokens.Count + " missing=" + m_suppressedMissing +
                " deathParticleFail=" + m_deathParticleFailures +
                " sleepStates=" + m_localSleep.Count + " sleepRuns=" +
                m_sleepPresentationRuns + " mirrorViews=" + m_mirrorSleep.Count +
                " sleepMirrorRuns=" + m_sleepMirrorRuns + " sleepMirrorFail=" +
                m_sleepMirrorFailures + " sleepMirrorClears=" + m_sleepMirrorClears +
                " sleepNoteFail=" + m_sleepNoteFailures + " authoritySleepReads=" +
                m_authoritySleepReads + " authorityWakes=" + m_authorityWakes + "}";
        }

        // ================================================================ 工具

        private bool TryResolveAvatar(PlayerKey key, out PlayerData playerData,
            out ComponentPlayer player, out string reason)
        {
            playerData = null;
            player = null;
            reason = null;
            if (!key.IsValid)
            {
                reason = "玩家键 " + key + " 非法";
                return false;
            }
            if (!m_spawner.TryGet(key, out playerData) || playerData == null)
            {
                m_noAvatar++;
                reason = "本机没有玩家键 " + key + " 的联机角色（还没建好或已经移除）";
                return false;
            }
            player = playerData.ComponentPlayer;
            if (player == null)
            {
                m_noAvatar++;
                reason = "玩家键 " + key + " 的联机角色还没有 ComponentPlayer";
                return false;
            }
            return true;
        }

        private bool TryResolveLocal(int slot, out PlayerData playerData, out ComponentPlayer player,
            out string reason)
        {
            playerData = null;
            player = null;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, slot, out playerData, out reason))
                return false;
            player = playerData.ComponentPlayer;
            if (player == null)
            {
                reason = "本机槽位 " + slot + " 还没有 ComponentPlayer";
                return false;
            }
            return true;
        }

        private static int Token(ComponentPlayer player) =>
            player?.Entity != null ? RuntimeHelpers.GetHashCode(player.Entity) : 0;

        private void WriteField(object target, string field, object value, Type declaringType) =>
            m_parentField.ModifyParentField(target, field, value, declaringType);

        private void Info(string message) => m_diagnostics?.Info(m_ownerId, message);

        private void Problem(string key, string message)
        {
            long now = Environment.TickCount64;
            if (m_lastProblemMs.TryGetValue(key, out long last) && now - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = now;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void Error(string key, string message, Exception exception)
        {
            long now = Environment.TickCount64;
            if (m_lastProblemMs.TryGetValue(key, out long last) && now - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = now;
            m_diagnostics?.Error(m_ownerId, message, exception);
        }
    }
}
