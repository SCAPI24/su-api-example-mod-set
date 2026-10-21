using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Engine;
using Engine.Input;
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
    ///     <c>Health &lt;= 0</c> 就会进入原生死亡屏（DeathCamera + "YOU HAVE DIED" + 等级减半，
    ///     <c>PlayerData.cs:270-300</c>），点击后**删除本机实体并本地复活**
    ///     （<c>PlayerData.cs:306-320</c>）。产品要求死亡与复活都由主机裁决，所以：
    ///     ① 死亡只由 <see cref="ApplyLocalVitals"/> / <see cref="ApplyLocalVitalEvent"/>
    ///        按主机数据写 <c>CauseOfDeath</c> + <c>Health = 0</c>（**不写 m_lastHealth**），
    ///        死亡表现交给引擎；
    ///     ② 死亡期间在本机 <c>ComponentPlayer.GuiWidget</c> 上挂一个挡门对话框，
    ///        使引擎的"点击 → 删实体"分支永远不成立（详见 ShowDeathGateDialog）；
    ///     ③ 主机批准后由 <see cref="TryExecuteLocalRespawn"/> 关掉挡门框并执行引擎那一行
    ///        <c>RemoveEntity(..., disposeEntity: true)</c>，之后 PrepareSpawn / SpawnDialog /
    ///        WaitForTerrain / SpawnPlayer / FppCamera 全是引擎原生的；
    ///     ④ 新实体出生后由 <see cref="TryReplayLocalRespawn"/> 用主机权威数据重放
    ///        （落点 / 等级 / 五项体征 / HUD / 模拟闸门），并推进死亡 epoch 挡住复活之前的旧包。
    ///        背包 / 手持 / 衣服 / 合成格**不恢复**：引擎死亡分支已把它们掉在死亡地点
    ///        （单机行为），复活后背包为空，由玩家自己捡回（否则会凭空多一份）。
    ///
    /// 无静态状态；所有上限与失败都有计数（<see cref="Describe"/>）与限流日志。
    /// </summary>
    public sealed class PlayerVitalsReplica : IPlayerVitalsPort, IPlayerSleepPort,
        IPlayerSelfDamagePort
    {
        /// <summary>同一条问题日志的最小间隔（毫秒）。</summary>
        private const long ProblemLogMs = 10000;

        /// <summary>复活 / 死亡默认生命值（引擎 PlayerData.SpawnPlayer 之后就是 1）。</summary>
        private const float FullHealth = 1f;

        /// <summary>
        /// 引擎 <c>PlayerData.PlayerDead</c> 的点击门槛（<c>Time.RealTime - m_playerDeathTime &gt; 1.5</c>，
        /// <c>Survivalcraft/Game/PlayerData.cs:306</c>）。诊断用：记录本机死亡请求时的实际等待。
        /// </summary>
        private const double EngineDeathTapGuardSeconds = 1.5;

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

        /// <summary>主机侧读不到复活锚点的次数（<c>ResolveHostRespawnAnchor</c> 不可用）。</summary>
        private long m_respawnAnchorFailures;

        /// <summary>
        /// 本机槽位 → **最近一份主机权威生命**（95 上报 / 已接受的 107 结果）。
        /// 这是客户端唯一允许用来回滚本地预测的值：引擎 <c>VitalStatsWidget</c> 的骷髅按钮
        /// 会在客户端本地先 <c>Injure(0.1f)</c>，客户端 <c>ComponentHealth.Update</c> 已摘掉，
        /// 若不回滚，本地可能先掉到 0 让引擎 <c>PlayerData</c> 自己判死 —— 主机却还活着。
        /// </summary>
        private readonly Dictionary<int, float> m_localAuthorityHealth =
            new Dictionary<int, float>();

        /// <summary>本机槽位 → 107 接受结果触发的红屏因子（每帧衰减后写 RedoutFactor）。</summary>
        private readonly Dictionary<int, float> m_selfDamageRedout =
            new Dictionary<int, float>();
        /// <summary>每帧刷新红屏 / 守卫时复用的本机角色与键缓冲。</summary>
        private readonly List<PlayerData> m_selfDamageLocalBuffer =
            new List<PlayerData>(PlayerKey.SlotCount);
        private readonly List<int> m_selfDamageRedoutSlots = new List<int>(PlayerKey.SlotCount);
        /// <summary>
        /// 客户端"本地预测回滚"守卫：<c>SubsystemUpdate</c> 里的一个 <c>IUpdateable</c>
        /// （<see cref="UpdateOrder.Reset"/> = −100，早于 <c>SubsystemPlayers</c> 的 −20）。
        /// 在引擎控件本地扣血之后、<c>PlayerData.Update</c> 判死之前把生命写回主机权威值。
        /// </summary>
        private SelfDamageGuard m_selfDamageGuard;
        private Project m_selfDamageGuardProject;
        private long m_selfDamageGuardInstalls;
        private long m_selfDamageGuardRemovals;
        private long m_selfDamageGuardFrames;
        private long m_selfDamageLocalPredictionsCanceled;
        private long m_selfDamageLocalDeathsRepaired;
        private long m_selfDamageGuardNoAuthority;
        private long m_selfDamageGuardFailures;
        private long m_selfDamageAuthorityApplies;
        private long m_selfDamageAuthorityFailures;
        private long m_selfDamageLocalResults;
        private long m_selfDamageLocalResultFailures;
        private long m_selfDamageRollbacks;
        private long m_selfDamageRollbackFailures;
        private long m_selfDamageFeedbackSounds;
        private long m_selfDamageRedoutArms;
        private long m_selfDamageRedoutFrames;
        private long m_selfDamageRedoutFailures;

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
            /// <summary>这一份权威状态是否已死亡（死亡后冰覆盖必须像引擎一样立刻消失）。</summary>
            public bool Dead;
            /// <summary>已经写进控件的贴图档位；<c>int.MinValue</c> = 还没写过。</summary>
            public int TextureIndex = int.MinValue;
            /// <summary>主机权威冻结进度（vitals.proto 字段 22）：每帧插值的目标值。</summary>
            public float TargetFreezeProgress;
            /// <summary>主机权威冻结阶段（字段 23）：只用于阶段变化计数 / 状态行。</summary>
            public uint TargetFreezeStage;
            /// <summary>是否收到过主机冻结字段（没有 = 用 <see cref="Values"/> 里的初始化值）。</summary>
            public bool HasFreezeTarget;
            /// <summary>这一份冻结值来自"老版本主机没带字段 → 用权威温度推导"（显式计数）。</summary>
            public bool FreezeLegacy;
            /// <summary>
            /// 冰冻覆盖的连续插值（每帧按时间常数 + 最大速率逼近主机值）。5 Hz 快照直接写
            /// <c>IceFactor</c> 会一顿一顿地跳，第三分屏（读主机连续温度）也会与客户端不同步。
            /// </summary>
            public readonly PlayerPresentationInterpolator Freeze =
                new PlayerPresentationInterpolator();

            /// <summary>
            /// 血条 / 食物条的连续插值（实机问题 2 的通用要求：5 Hz 快照不直接阶跃写表现）。
            /// 时间常数比冰冻覆盖短（0.12 s）：掉血仍然"立刻看到"，只是把 200 ms 的台阶抹平；
            /// 伤害本身 / 闪烁 / 红屏仍由权威值即时触发。
            /// </summary>
            public readonly PlayerPresentationInterpolator HealthBar =
                new PlayerPresentationInterpolator(0.12f, 6f);

            public readonly PlayerPresentationInterpolator FoodBar =
                new PlayerPresentationInterpolator(0.12f, 2f);
        }

        /// <summary>
        /// 一个本机槽位的**表现事件状态**（实机问题 3/4）：
        ///   · <see cref="RedFactor"/> = 引擎 <c>ComponentHealth.m_redScreenFactor</c> 的镜像
        ///     （主机权威掉血驱动，每帧按 <c>Pow(0.2, dt)</c> 衰减后写 <c>RedoutFactor</c>）；
        ///   · <see cref="FluBlackoutDuration"/> / <see cref="FluBlackoutFactor"/> = 引擎
        ///     <c>ComponentFlu.m_blackoutDuration/m_blackoutFactor</c> 的表现（咳嗽 / 喷嚏事件驱动，
        ///     0.5/s 渐入到 0.95、0.5/s 渐出，每帧写 <c>BlackoutFactor</c>）；
        ///   · <see cref="GreenoutDuration"/> / <see cref="GreenoutFactor"/> = 引擎
        ///     <c>ComponentSickness.m_greenoutDuration/m_greenoutFactor</c>（恶心 / 呕吐事件驱动）；
        ///   · <see cref="Puke"/> = 引擎 <c>PukeParticleSystem</c>（呕吐粒子，每帧跟随眼位 / 朝向）。
        /// 全部只在**表现层**：不写任何权威字段、不扣血（扣血由 95 上报）。
        /// </summary>
        private sealed class LocalEffectState
        {
            public int EntityToken;
            public float RedFactor;
            public float FluBlackoutDuration;
            public float FluBlackoutFactor;
            public float GreenoutDuration;
            public float GreenoutFactor;
            public PukeParticleSystem Puke;

            public void Reset()
            {
                RedFactor = 0f;
                FluBlackoutDuration = 0f;
                FluBlackoutFactor = 0f;
                GreenoutDuration = 0f;
                GreenoutFactor = 0f;
                if (Puke != null)
                {
                    Puke.IsStopped = true;
                    Puke = null;
                }
            }
        }

        /// <summary>
        /// 主机侧一个客户端化身的表现事件**边沿检测状态**：保存上一拍读到的引擎私有计时字段。
        /// 只在**上升沿**产出一条事件（咳嗽 4 秒计时被重置 / 喷嚏 1 秒 / 恶心 / 呕吐时刻变化 /
        /// 火从无到有 / 从有到无）。<see cref="Sequence"/> 跨实体换代保持单调（客户端按它去重）。
        /// </summary>
        private sealed class AuthorityEffectWatch
        {
            public int Token;
            public bool Seeded;
            public float CoughDuration;
            public float SneezeDuration;
            public bool HasNausea;
            public double NauseaTime;
            public bool HasPuke;
            public double PukeTime;
            public float FireDuration;
            public uint Sequence;
        }

        /// <summary>
        /// 一个本机槽位的**死亡期引擎状态**（方案 A：引擎原生死亡流程）。
        ///   · <see cref="GateDialog"/> = 挂在 <c>ComponentPlayer.GuiWidget</c> 上的挡门对话框。
        ///     引擎 <c>PlayerDead</c> 的点击分支要求 <c>!DialogsManager.HasDialogs(GuiWidget)</c>
        ///     （<c>PlayerData.cs:306</c>），挂着它就挡住了"未获主机批准就删实体"的自动复活；
        ///   · <see cref="DeathToken"/> = 执行 <c>RemoveEntity</c> 时旧实体的标识（识别新实体出生）；
        ///   · <see cref="ReplayedToken"/> = 已经重放过的实体标识（幂等，重复调用返回 NoPending）。
        /// </summary>
        private sealed class LocalDeathState
        {
            public Dialog GateDialog;
            public int GateToken;
            public int DeathToken;
            public int ReplayedToken;
            /// <summary>
            /// 复活窗口（respawnInProgress）：RemoveEntity 之后 → 第一份主机权威存活快照之前。
            /// 窗口内 Dead 快照 / Death 事件一律拒绝；关闭时主动清理死亡表现（见
            /// <see cref="ClearRespawnDeathPresentation"/>）。
            /// </summary>
            public readonly PlayerRespawnWindow Window = new PlayerRespawnWindow();
            /// <summary>这次死亡见过的最大 revision（窗口下界；复活事件未到时的 epoch 下界）。</summary>
            public uint DeathRevision;
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
        /// <summary>已把本机角色推进引擎原生死亡流程（Health 写 0 → PlayerData.PlayerDead）的次数。</summary>
        private long m_deathScreensShown;
        private long m_terminalDeathTaps;
        /// <summary>死亡屏上"只有 <c>Input.Any</c> 电平、没有按下沿"的帧数（按住 / 键盘自动重复）。</summary>
        private long m_deathTapLevelsIgnored;
        private long m_respawnRejectedMessages;
        /// <summary>
        /// 本机槽位 → 死亡期引擎状态（挡门对话框 / 旧实体标识）。
        /// 只在主机裁决死亡时创建，出生后权威重放完成即销毁。
        /// </summary>
        private readonly Dictionary<int, LocalDeathState> m_localDeathStates =
            new Dictionary<int, LocalDeathState>();
        /// <summary>本机槽位 → 死亡 epoch（最近一次复活裁决的 revision）。陈旧死亡状态一律拒写。</summary>
        private readonly Dictionary<int, uint> m_localDeathEpoch = new Dictionary<int, uint>();
        // 复活窗口（respawnInProgress）诊断计数（状态行见 Describe）。
        private long m_replicaWindowOpened;
        private long m_replicaWindowDeadSuppressed;
        private long m_replicaWindowClosed;
        private long m_replicaWindowNewDeath;
        private long m_replicaWindowCleanupFailures;
        private long m_gateShown;
        private long m_gateHidden;
        private long m_gateFailures;
        /// <summary>挡门对话框的黑色遮罩被改成透明的次数（引擎死亡屏保持原样）。</summary>
        private long m_gateCoverNeutralized;
        /// <summary>挡门对话框的遮罩没被找到的次数（引擎内部分层变化 → 死亡屏会变暗，限流日志）。</summary>
        private long m_gateCoverMisses;
        /// <summary>mod 执行引擎 <c>RemoveEntity</c>（主机批准后推进复活流程）的成功次数。</summary>
        private long m_engineRespawnExecuted;
        private long m_engineRespawnFailures;
        /// <summary>新实体出生后权威重放成功的次数。</summary>
        private long m_replayApplied;
        private long m_replayWaitFrames;
        private long m_replayFailures;
        /// <summary>复活重放**没有**恢复背包 / 手持 / 衣服 / 合成格的次数（物品只由引擎死亡掉落一次）。</summary>
        private long m_respawnInventoryDroppedByEngine;
        /// <summary>revision 不新于死亡 epoch 的死亡状态被拒写的次数（陈旧上报）。</summary>
        private long m_staleDeathRejects;
        /// <summary>把本机 PlayerData 从引擎死亡状态恢复回 Playing 的次数（主机确认角色活着时）。</summary>
        private long m_localPlayerDataRestores;
        private long m_hudWrites;
        private long m_hudPresentationRefreshes;
        private long m_hudTemperatureTextures;
        private long m_hudIceWrites;
        private long m_hudPresentationFailures;
        private long m_hudNonFinite;
        // 冻结权威 / 连续插值（实机问题 1/2）
        private float m_freezeHostValue;
        private long m_freezeClientApplied;
        private long m_freezeInterpolatedFrames;
        private long m_freezeStageChanges;
        private long m_freezeLegacy;
        private long m_freezeInvalid;
        private long m_freezeCaptureFailures;
        private long m_freezeSeeds;
        private long m_iceFactorWriteFailures;
        private readonly Dictionary<PlayerKey, uint> m_freezeHostStage =
            new Dictionary<PlayerKey, uint>();
        /// <summary>
        /// 主机为每个角色算出的冻结进度（= vitals.proto 字段 22 的**原值**，随 95 下发）。
        /// <see cref="TryGetAuthorityFreeze"/> 只读它 —— 第三分屏的冰覆盖必须与客户端同一个数
        /// （实机问题 1：第三屏不得再按温度自己推导）。
        /// </summary>
        private readonly Dictionary<PlayerKey, float> m_freezeHostProgress =
            new Dictionary<PlayerKey, float>();
        /// <summary>只读查询成功 / 未命中的次数（第三分屏冰覆盖的可观测性）。</summary>
        private long m_freezeAuthorityReads;
        private long m_freezeAuthorityMisses;
        // 受伤反馈（实机问题 3）
        private long m_hurtFeedbackSound;
        private long m_hurtRedoutHits;
        private long m_hurtRedoutFrames;
        private long m_hurtRedoutFailures;
        // 远端化身受伤闪红：引擎的闪红只看 ComponentHealth.HealthChange
        // （ComponentCreatureModel.cs:185-193），而本层把 Health 与 m_lastHealth 写成同一个值
        // ⇒ HealthChange 恒为 0 ⇒ 远端化身永远不闪。这里记住"上一次已应用的权威血量"，
        // 只在真实下降时补一次 m_injuryColorFactor（与 AvatarActionBridge.HurtFlash 同一手法）。
        private readonly Dictionary<PlayerKey, float> m_flashHealth =
            new Dictionary<PlayerKey, float>();
        private long m_hurtFlashes;
        // 咳嗽 / 呕吐 / 着火表现事件（实机问题 4）
        private readonly Dictionary<int, LocalEffectState> m_localEffects =
            new Dictionary<int, LocalEffectState>();
        private readonly Dictionary<PlayerKey, AuthorityEffectWatch> m_effectWatch =
            new Dictionary<PlayerKey, AuthorityEffectWatch>();
        private readonly Dictionary<PlayerKey, uint> m_effectLastSequence =
            new Dictionary<PlayerKey, uint>();
        private long m_effectPolls;
        private long m_effectPollFailures;
        private long m_effectEventsEmitted;
        private long m_effectPositionFailures;
        private long m_effectSoundReadFailures;
        private long m_fluEffectApplied;
        private long m_fluEffectSounds;
        private long m_fluEffectParticles;
        private long m_fluEffectRejected;
        private long m_fluEffectDuplicate;
        private long m_fluEffectStale;
        private long m_fluEffectSoundMismatch;
        private long m_fluEffectParticleFailures;
        private long m_fluBlackoutFrames;
        private long m_fluGreenoutFrames;
        private long m_effectSoundReadFailuresLocal;
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
        private long m_authoritySleeps;
        private long m_sleepAuthoritySleepFailures;
        private long m_sleepLocalReverts;
        private long m_sleepLocalForcedRejected;
        private long m_sleepRejectMessages;
        private long m_sleepNoteFailures;
        private long m_sleepPresentationRuns;
        private long m_sleepMirrorRuns;
        private long m_sleepMirrorFailures;
        private long m_sleepMirrorClears;
        /// <summary>
        /// 主机侧客户端化身的死亡减半账本：这些化身不在 <c>SubsystemPlayers.m_playersData</c> 里，
        /// 引擎 <c>PlayerDead</c> 不会跑 → 由主机在抓取时减一次，并写回 <c>PlayerData.Level</c>
        /// （<c>NetworkAvatarSpawner.CapturePlayerRecord</c> → <c>PlayerAvatarModule.RefreshHostRecords</c>
        /// 每 5 秒把它持久化到身份记录）。本机槽位由引擎自己减半，这里不记账。
        /// </summary>
        private readonly HostAvatarLevelAuthority m_hostLevelAuthority =
            new HostAvatarLevelAuthority();
        /// <summary>等级同步计数（输出在 <see cref="Describe"/> 的 vitals 状态行里）。</summary>
        private readonly PlayerLevelCounters m_levelCounters = new PlayerLevelCounters();
        /// <summary>
        /// 本机槽位 → 最近一次主机权威等级（&gt; 0）。**死亡快照也缓存**：主机在死亡时已经把
        /// 等级减半，复活重放要把这份权威值写进引擎新建的实体（默认 Level=1）。
        /// </summary>
        private readonly Dictionary<int, float> m_localAuthorityLevel =
            new Dictionary<int, float>();
        /// <summary>本机槽位 → 那份权威等级对应的 revision。</summary>
        private readonly Dictionary<int, uint> m_localAuthorityLevelRevision =
            new Dictionary<int, uint>();
        /// <summary>本机槽位 → 已经写进 <c>PlayerData.Level</c> 的 revision（0 = 还没写过）。</summary>
        private readonly Dictionary<int, uint> m_localLevelAppliedRevision =
            new Dictionary<int, uint>();
        /// <summary>
        /// 本机槽位 → "复活重放已经发生、等主机权威等级落位"：下一次真正写入计
        /// <c>levelRespawnOverrides</c>（不是普通 <c>levelApplied</c>）。
        /// </summary>
        private readonly HashSet<int> m_localLevelRespawnPending = new HashSet<int>();

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
                    PlayerKey localKey = new PlayerKey(0, slot);
                    float localLevel = ReadCapturedLevel(locals[slot], localKey);
                    target.Add(CaptureOne(localKey, player, localLevel));
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
                bool dead = player.ComponentHealth.Health <= 0f;
                float level = ReadHostAuthorityLevel(key, playerData, player, dead);
                target.Add(CaptureOne(key, player, level));
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

        /// <summary>
        /// **主机侧 / 每帧**：轮询主机模拟的客户端化身（client_id &gt; 0）的
        /// <c>ComponentFlu</c> / <c>ComponentSickness</c> / <c>ComponentOnFire</c> 私有计时字段，
        /// 在**上升沿**产出一条表现事件（实机问题 4：客户端自己的化身咳嗽 / 呕吐 / 着火时，
        /// 以前只有主机设备上能看到 / 听到，客户端只掉血）。
        ///
        /// 引擎事实（逐条已核对）：
        ///   · <c>ComponentFlu.Cough()</c>：<c>m_coughDuration = 4f</c> + <c>PlayCoughSound</c>；
        ///     <c>Sneeze()</c>：<c>m_sneezeDuration = 1f</c> + <c>PlaySneezeSound</c>；
        ///     <c>FluEffect()</c> 还设 <c>m_blackoutDuration</c>（黑屏渐入）；
        ///   · <c>ComponentSickness.NauseaEffect()</c>：<c>m_lastNauseaTime = GameTime</c>
        ///     + <c>PlayMoanSound</c>；可吐时建 <c>PukeParticleSystem</c> + <c>PlayPukeSound</c>
        ///     并写 <c>m_lastPukeTime</c>；<c>m_greenoutDuration</c> 驱动绿屏；
        ///   · <c>ComponentOnFire.Update</c>：<c>m_fireDuration &gt; 0</c> 时维护
        ///     <c>OnFireParticleSystem</c>；水中熄灭时播 <c>Audio/SizzleLong</c>。
        /// 客户端这些 Update 全被摘掉，所以必须由主机把"发生了哪一次表现"下发（音效 / 粒子 /
        /// 覆盖层），**扣血仍由 95 上报下发**（本方法绝不产生伤害）。
        ///
        /// 只有客户端化身（client_id &gt; 0）：主机自己的本机角色表现本来就在主机上跑。
        /// 第一次观察一个角色只播种（不把"已经咳嗽了"当成新事件）；实体换代保留 sequence
        /// （客户端按每键 sequence 去重，跨复活仍然单调）。
        /// </summary>
        public int PollAuthorityEffects(List<PlayerEffectEventSnapshot> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "调用方没有给表现事件容器";
                return 0;
            }
            target.Clear();
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return 0;
            }
            m_effectPolls++;
            var keys = new List<PlayerKey>();
            foreach (PlayerKey key in m_spawner.Keys)
            {
                if (key.ClientId > 0) keys.Add(key);
            }
            keys.Sort(CompareKeys);
            int emitted = 0;
            string firstFailure = null;
            for (int i = 0; i < keys.Count; i++)
            {
                PlayerKey key = keys[i];
                if (!m_spawner.TryGet(key, out PlayerData playerData) ||
                    playerData?.ComponentPlayer == null)
                    continue;
                ComponentPlayer player = playerData.ComponentPlayer;
                ComponentBody body = player.ComponentBody;
                if (body == null) continue;
                int token = Token(player);
                if (!m_effectWatch.TryGetValue(key, out AuthorityEffectWatch watch))
                {
                    watch = new AuthorityEffectWatch();
                    m_effectWatch[key] = watch;
                }
                if (watch.Token != token)
                {
                    // 新实体：重新播种；Sequence 保留（每键跨复活单调，客户端才能去重）。
                    watch.Token = token;
                    watch.Seeded = false;
                }
                float cough = 0f, sneeze = 0f, blackout = 0f, greenout = 0f, fire = 0f;
                double? nausea = null, puke = null;
                try
                {
                    ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
                    if (flu != null)
                    {
                        cough = m_parentField.GetParentField<float>(flu, "m_coughDuration",
                            typeof(ComponentFlu));
                        sneeze = m_parentField.GetParentField<float>(flu, "m_sneezeDuration",
                            typeof(ComponentFlu));
                        blackout = m_parentField.GetParentField<float>(flu, "m_blackoutDuration",
                            typeof(ComponentFlu));
                    }
                    ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
                    if (sickness != null)
                    {
                        greenout = m_parentField.GetParentField<float>(sickness,
                            "m_greenoutDuration", typeof(ComponentSickness));
                        nausea = ReadNullableDouble(m_parentField.GetParentField(sickness,
                            "m_lastNauseaTime", typeof(ComponentSickness)));
                        puke = ReadNullableDouble(m_parentField.GetParentField(sickness,
                            "m_lastPukeTime", typeof(ComponentSickness)));
                    }
                    ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
                    if (onFire != null)
                    {
                        fire = m_parentField.GetParentField<float>(onFire, "m_fireDuration",
                            typeof(ComponentOnFire));
                    }
                }
                catch (Exception e)
                {
                    m_effectPollFailures++;
                    watch.Seeded = false;
                    if (firstFailure == null)
                    {
                        firstFailure = "玩家键 " + key + " 的病程 / 火焰计时读不到：" +
                            e.GetType().Name + " " + e.Message;
                    }
                    continue;
                }
                if (!watch.Seeded)
                {
                    watch.Seeded = true;
                    watch.CoughDuration = cough;
                    watch.SneezeDuration = sneeze;
                    watch.HasNausea = nausea.HasValue;
                    watch.NauseaTime = nausea ?? 0.0;
                    watch.HasPuke = puke.HasValue;
                    watch.PukeTime = puke ?? 0.0;
                    watch.FireDuration = fire;
                    continue;
                }
                double hostTime = CurrentGameTime(player);
                Vector3 position = body.Position;
                ComponentCreatureSounds sounds = player.Entity.FindComponent<ComponentCreatureSounds>();
                const float active = 0.0001f;
                // 咳嗽 / 喷嚏：计时被重置 = 新的一次表现（引擎 Cough 4 秒 / Sneeze 1 秒）。
                if (cough > active && cough > watch.CoughDuration + active)
                {
                    emitted += EmitEffect(target, key, watch, PlayerEffectKind.FluCough, position,
                        hostTime, blackout, ReadSoundName(sounds, "m_coughSound"));
                }
                if (sneeze > active && sneeze > watch.SneezeDuration + active)
                {
                    emitted += EmitEffect(target, key, watch, PlayerEffectKind.FluSneeze, position,
                        hostTime, blackout, ReadSoundName(sounds, "m_sneezeSound"));
                }
                // 恶心 / 呕吐：引擎写的是 GameTime 时刻（double?），变化即新的一次表现。
                if (nausea.HasValue &&
                    (!watch.HasNausea || Math.Abs(nausea.Value - watch.NauseaTime) > 1e-6))
                {
                    emitted += EmitEffect(target, key, watch, PlayerEffectKind.SicknessNausea,
                        position, hostTime, greenout, ReadSoundName(sounds, "m_moanSound"));
                }
                if (puke.HasValue &&
                    (!watch.HasPuke || Math.Abs(puke.Value - watch.PukeTime) > 1e-6))
                {
                    emitted += EmitEffect(target, key, watch, PlayerEffectKind.SicknessPuke,
                        position, hostTime, PlayerEffectPolicy.PukeGreenoutSeconds,
                        ReadSoundName(sounds, "m_pukeSound"));
                }
                // 着火 / 熄灭：权威火时长从 0 变正 / 从正变 0。
                if (fire > active && watch.FireDuration <= active)
                {
                    emitted += EmitEffect(target, key, watch, PlayerEffectKind.FireIgnite, position,
                        hostTime, fire, string.Empty);
                }
                else if (fire <= active && watch.FireDuration > active)
                {
                    bool water = body.ImmersionFactor > 0.5f &&
                        body.ImmersionFluidBlock is WaterBlock;
                    emitted += EmitEffect(target, key, watch, PlayerEffectKind.FireExtinguish,
                        position, hostTime, water ? 1f : 0f, "Audio/SizzleLong");
                }
                watch.CoughDuration = cough;
                watch.SneezeDuration = sneeze;
                watch.HasNausea = nausea.HasValue;
                watch.NauseaTime = nausea ?? watch.NauseaTime;
                watch.HasPuke = puke.HasValue;
                watch.PukeTime = puke ?? watch.PukeTime;
                watch.FireDuration = fire;
            }
            // 注意：**不**清理已经离开的玩家的边沿状态 —— 每条事件的 sequence 必须在同一个
            // 玩家键上跨化身重建保持单调（客户端按它去重）。主机进程内这个字典只会随"曾经
            // 出现过的玩家键"增长（每项几十字节），换世界 / 重开会话时整体作废即可。
            reason = firstFailure;
            return emitted;
        }

        private int EmitEffect(List<PlayerEffectEventSnapshot> target, PlayerKey key,
            AuthorityEffectWatch watch, PlayerEffectKind kind, Vector3 position, double hostTime,
            float strength, string sound)
        {
            if (!PlayerVitalsSnapshot.IsFiniteValue(position.X) ||
                !PlayerVitalsSnapshot.IsFiniteValue(position.Y) ||
                !PlayerVitalsSnapshot.IsFiniteValue(position.Z))
            {
                m_effectPositionFailures++;
                Problem("effect-position:" + key, "玩家键 " + key + " 的表现事件坐标不是有限数" +
                    " → 丢弃这一条（不拿 Zero 顶替）");
                return 0;
            }
            watch.Sequence = watch.Sequence == uint.MaxValue ? 1u : watch.Sequence + 1u;
            target.Add(new PlayerEffectEventSnapshot(key, watch.Sequence, kind, position, hostTime,
                MathUtils.Max(strength, 0f), sound ?? string.Empty));
            m_effectEventsEmitted++;
            return 1;
        }

        private string ReadSoundName(ComponentCreatureSounds sounds, string field)
        {
            if (sounds == null) return string.Empty;
            try
            {
                return m_parentField.GetParentField(sounds, field,
                    typeof(ComponentCreatureSounds)) as string ?? string.Empty;
            }
            catch (Exception e)
            {
                m_effectSoundReadFailures++;
                Problem("effect-sound-read:" + field,
                    "读取主机角色 ComponentCreatureSounds." + field + " 失败：" +
                    e.GetType().Name + " " + e.Message + " → 该事件不带资源名（客户端仍用引擎" +
                    "同款方法发声，资源名只用于核对）");
                return string.Empty;
            }
        }

        private static double? ReadNullableDouble(object raw)
        {
            if (raw is double value && !double.IsNaN(value) && !double.IsInfinity(value))
                return value;
            return null;
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
                // ApplyLocalState 用 out 参数报告"复活窗口清理失败"（方法签名保持
                // private void ApplyLocalState(int slot, ComponentPlayer player, ...)：既有源码守卫
                // 按这个前缀定位方法体）。失败 = 不写 LastRevision，模块下一拍重试。
                ApplyLocalState(key.Slot, player, in snapshot, out string applyFailure);
                if (applyFailure != null)
                {
                    reason = applyFailure;
                    m_applyFailures++;
                    return PlayerVitalsApplyStatus.Rejected;
                }
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
                if (evt.IsDeath)
                    ApplyLocalDeath(evt.Key.Slot, player, evt.CauseOfDeath, evt.Revision);
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
        /// 主机权威值 → 本机角色。只写字段 / HUD。
        ///
        /// **死亡路径（方案 A：引擎原生）**：<c>snapshot.Dead</c> 时写 <c>CauseOfDeath</c> 并把
        /// <c>Health</c> 写成 0，**且不写 <c>m_lastHealth</c>**（引擎要看到"掉到 0"的边沿）；
        /// 引擎 <c>PlayerData.Playing</c> 因此自己进入 <c>PlayerDead</c>
        /// （<c>Survivalcraft/Game/PlayerData.cs:270-300</c>）：DeathCamera + "YOU HAVE DIED" +
        /// 等级减半全是引擎的，mod 不再自己切相机 / 写大字 / 写死亡姿态。
        /// 客户端**不删实体、不掉物品、不本地复活**：删实体要等主机批准
        /// （<see cref="TryExecuteLocalRespawn"/>），新实体出生后再整体重放
        /// （<see cref="TryReplayLocalRespawn"/>）。
        ///
        /// 死亡 epoch：revision 不新于本槽位死亡 epoch 的死亡状态是"复活之前就在飞的旧包"，
        /// 写进新实体会让引擎立刻再判一次死 → 这里直接拒写（计数 + 限流日志，不静默）。
        /// </summary>
        /// <summary>
        /// **客户端侧**：播放一条主机下发的表现事件（<c>PLAYER_EFFECT_EVENTS</c> = 109）。
        ///
        /// 只播表现（实机问题 4：客户端自己的化身流感咳嗽 / 呕吐 / 着火以前只有主机看得到）：
        ///   · 人声 = 引擎同款 <c>ComponentCreatureSounds.PlayCoughSound / PlaySneezeSound /
        ///     PlayMoanSound / PlayPukeSound</c>（资源名 / 音量 / 最小距离都是引擎配置的，
        ///     事件里带的 <c>sound</c> 只用于核对，不一致计 <c>fluEffectSoundMismatch</c>）；
        ///   · 呕吐粒子 = 引擎 <c>PukeParticleSystem</c>（每帧跟随眼位 / 朝向）；
        ///   · 黑屏 / 绿屏 = 引擎 <c>ComponentFlu</c> / <c>ComponentSickness</c> 的
        ///     0.5/s 渐入渐出（<see cref="AdvanceLocalEffectPresentation"/> 每帧写）；
        ///   · 火 = 客户端已维护的 <c>OnFireParticleSystem</c>（<see cref="TryEnsureLocalFireParticles"/>），
        ///     熄灭按引擎在**水中**熄灭时播 <c>Audio/SizzleLong</c>。
        /// **绝不**写生命 / 体征、绝不 Injure、绝不判死：扣血仍由 95 上报下发。
        ///
        /// 去重 / 过期：每键 <c>sequence</c> 必须严格递增；事件的 <c>host_time</c> 与客户端已对齐的
        /// 主机游戏时钟相差超过 <see cref="PlayerEffectPolicy.TtlSeconds"/> = 过期（计数后丢弃）。
        /// </summary>
        public PlayerEffectApplyStatus ApplyEffect(in PlayerEffectEventSnapshot evt,
            out string reason)
        {
            reason = null;
            if (!evt.TryValidate(out reason))
            {
                m_fluEffectRejected++;
                return PlayerEffectApplyStatus.Rejected;
            }
            if (evt.Key.ClientId <= 0)
            {
                m_fluEffectRejected++;
                reason = "表现事件的 clientId = " + evt.Key.ClientId +
                    "（主机本机角色的表现不会下发，收到即协议错误）";
                return PlayerEffectApplyStatus.Rejected;
            }
            if (!TryResolveLocal(evt.Key.Slot, out PlayerData playerData,
                    out ComponentPlayer player, out reason))
                return PlayerEffectApplyStatus.NoAvatar;
            uint last = m_effectLastSequence.TryGetValue(evt.Key, out uint applied)
                ? applied
                : 0u;
            if (last != 0 && evt.Sequence <= last)
            {
                m_fluEffectDuplicate++;
                reason = "sequence " + evt.Sequence + " <= 已应用 " + last;
                return PlayerEffectApplyStatus.Duplicate;
            }
            float ttl = PlayerEffectPolicy.TtlSeconds(evt.Kind);
            if (!(ttl > 0f))
            {
                m_fluEffectRejected++;
                reason = "表现种类 " + (int)evt.Kind + " 没有过期上限（未知种类）";
                return PlayerEffectApplyStatus.Rejected;
            }
            double age = CurrentGameTime(player) - evt.HostTime;
            if (age > ttl)
            {
                // 记下序号：同一条事件重发仍然是过期，不重复计数 / 播放。
                m_effectLastSequence[evt.Key] = evt.Sequence;
                m_fluEffectStale++;
                reason = "事件已过期 " + age.ToString("0.###") + "s > TTL " +
                    ttl.ToString("0.##") + "s";
                return PlayerEffectApplyStatus.Stale;
            }
            try
            {
                LocalEffectState state = GetOrCreateLocalEffect(evt.Key.Slot, player);
                float strength = MathUtils.Max(evt.Strength, 0f);
                switch (evt.Kind)
                {
                    case PlayerEffectKind.FluCough:
                    case PlayerEffectKind.FluSneeze:
                    {
                        if (!TryPlayCreatureSound(player, evt.Kind, evt.Sound, out reason))
                        {
                            m_fluEffectRejected++;
                            return PlayerEffectApplyStatus.Rejected;
                        }
                        // 黑屏时长 = 主机 ComponentFlu.m_blackoutDuration（引擎 FluEffect 才设置）；
                        // 0 = 这次表现引擎没有黑屏（例如单纯受冷打喷嚏），客户端也不黑屏。
                        if (strength > 0f)
                        {
                            state.FluBlackoutDuration = MathUtils.Clamp(
                                MathUtils.Max(state.FluBlackoutDuration, strength), 0f,
                                PlayerVitalsLimits.MaxConditionSeconds);
                        }
                        break;
                    }
                    case PlayerEffectKind.SicknessNausea:
                    {
                        if (!TryPlayCreatureSound(player, evt.Kind, evt.Sound, out reason))
                        {
                            m_fluEffectRejected++;
                            return PlayerEffectApplyStatus.Rejected;
                        }
                        // 绿屏时长 = 主机 ComponentSickness.m_greenoutDuration（0 = 不绿屏）。
                        if (strength > 0f)
                        {
                            state.GreenoutDuration = MathUtils.Clamp(
                                MathUtils.Max(state.GreenoutDuration, strength), 0f,
                                PlayerVitalsLimits.MaxConditionSeconds);
                        }
                        break;
                    }
                    case PlayerEffectKind.SicknessPuke:
                    {
                        if (!TryPlayCreatureSound(player, evt.Kind, evt.Sound, out reason))
                        {
                            m_fluEffectRejected++;
                            return PlayerEffectApplyStatus.Rejected;
                        }
                        state.GreenoutDuration = MathUtils.Max(state.GreenoutDuration,
                            PlayerEffectPolicy.PukeGreenoutSeconds);
                        if (!TryStartLocalPuke(player, state, out string pukeReason))
                        {
                            m_fluEffectParticleFailures++;
                            Problem("effect-puke:" + evt.Key.Slot,
                                "本机槽位 " + evt.Key.Slot + " 的呕吐粒子无法创建：" + pukeReason +
                                "（呕吐音效已播放；扣血仍由体征同步）");
                        }
                        break;
                    }
                    case PlayerEffectKind.FireIgnite:
                    {
                        if (TryEnsureLocalFireParticles(player, evt.Key.Slot)) m_fluEffectParticles++;
                        else m_fluEffectParticleFailures++;
                        break;
                    }
                    case PlayerEffectKind.FireExtinguish:
                    {
                        StopLocalFireParticles(evt.Key.Slot);
                        if (strength >= 0.5f)
                        {
                            SubsystemAudio audio = player.Project?.FindSubsystem<SubsystemAudio>(false);
                            if (audio == null)
                            {
                                m_fluEffectRejected++;
                                reason = "Project 缺少 SubsystemAudio（无法播放水中熄灭的 SizzleLong）";
                                return PlayerEffectApplyStatus.Rejected;
                            }
                            // Source: ComponentOnFire.cs:62-65（水中熄灭：Audio/SizzleLong）
                            audio.PlaySound("Audio/SizzleLong", 1f, 0f, evt.Position, 4f,
                                autoDelay: true);
                            m_fluEffectSounds++;
                        }
                        break;
                    }
                    default:
                        m_fluEffectRejected++;
                        reason = "表现种类 " + (int)evt.Kind + " 没有播放实现";
                        return PlayerEffectApplyStatus.Rejected;
                }
                m_effectLastSequence[evt.Key] = evt.Sequence;
                m_fluEffectApplied++;
                return PlayerEffectApplyStatus.Applied;
            }
            catch (Exception e)
            {
                m_fluEffectRejected++;
                reason = "播放玩家键 " + evt.Key + " 的表现事件时抛异常：" + e.GetType().Name +
                    " " + e.Message;
                Error("effect-apply:" + evt.Key, reason, e);
                return PlayerEffectApplyStatus.Rejected;
            }
        }

        /// <summary>用引擎同款人声方法播放（资源名 / 音量 / 最小距离都来自数据库配置）。</summary>
        private bool TryPlayCreatureSound(ComponentPlayer player, PlayerEffectKind kind,
            string hostSound, out string reason)
        {
            reason = null;
            ComponentCreatureSounds sounds = player.ComponentCreatureSounds;
            if (sounds == null)
            {
                reason = "本机角色没有 ComponentCreatureSounds（无法播放 " +
                    PlayerEffectPolicy.Describe(kind) + "）";
                return false;
            }
            string localSound = ReadLocalSoundName(sounds, kind);
            if (hostSound.Length > 0 && localSound.Length > 0 &&
                !string.Equals(hostSound, localSound, StringComparison.Ordinal))
            {
                m_fluEffectSoundMismatch++;
                Problem("effect-sound-mismatch:" + kind, "主机资源名 " + hostSound +
                    " 与本机配置 " + localSound + " 不一致（仍按引擎同款方法播放本机资源）");
            }
            switch (kind)
            {
                case PlayerEffectKind.FluCough:
                    sounds.PlayCoughSound();
                    break;
                case PlayerEffectKind.FluSneeze:
                    sounds.PlaySneezeSound();
                    break;
                case PlayerEffectKind.SicknessNausea:
                    sounds.PlayMoanSound();
                    break;
                case PlayerEffectKind.SicknessPuke:
                    sounds.PlayPukeSound();
                    break;
                default:
                    reason = "表现种类 " + (int)kind + " 不是人声（不该走到这里）";
                    return false;
            }
            m_fluEffectSounds++;
            return true;
        }

        private string ReadLocalSoundName(ComponentCreatureSounds sounds, PlayerEffectKind kind)
        {
            string field;
            switch (kind)
            {
                case PlayerEffectKind.FluCough: field = "m_coughSound"; break;
                case PlayerEffectKind.FluSneeze: field = "m_sneezeSound"; break;
                case PlayerEffectKind.SicknessNausea: field = "m_moanSound"; break;
                case PlayerEffectKind.SicknessPuke: field = "m_pukeSound"; break;
                default: return string.Empty;
            }
            try
            {
                return m_parentField.GetParentField(sounds, field,
                    typeof(ComponentCreatureSounds)) as string ?? string.Empty;
            }
            catch (Exception e)
            {
                m_effectSoundReadFailuresLocal++;
                Problem("local-sound-read:" + field,
                    "读取本机 ComponentCreatureSounds." + field + " 失败：" +
                    e.GetType().Name + " " + e.Message + "（资源名核对跳过，仍用引擎方法播放）");
                return string.Empty;
            }
        }

        private bool TryStartLocalPuke(ComponentPlayer player, LocalEffectState state,
            out string reason)
        {
            reason = null;
            if (state.Puke != null && !state.Puke.IsStopped)
            {
                UpdatePukeTransform(player, state);
                return true;
            }
            Project project = player.Project;
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            SubsystemTerrain terrain = project?.FindSubsystem<SubsystemTerrain>(false);
            if (particles == null || terrain == null)
            {
                reason = "Project 缺少 SubsystemParticles / SubsystemTerrain";
                return false;
            }
            state.Puke = new PukeParticleSystem(terrain);
            particles.AddParticleSystem(state.Puke);
            UpdatePukeTransform(player, state);
            m_fluEffectParticles++;
            return true;
        }

        private void UpdatePukeTransform(ComponentPlayer player, LocalEffectState state)
        {
            if (state.Puke == null) return;
            if (state.Puke.IsStopped)
            {
                state.Puke = null;
                return;
            }
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model == null) return;
            Vector3 up = model.EyeRotation.GetUpVector();
            Vector3 forward = model.EyeRotation.GetForwardVector();
            // Source: ComponentSickness.cs:150-153（同一位置 / 方向式子）
            state.Puke.Position = model.EyePosition - 0.08f * up + 0.3f * forward;
            state.Puke.Direction = Vector3.Normalize(forward + 0.5f * up);
        }

        /// <summary>
        /// 每帧把表现事件驱动的覆盖层写回引擎（<c>ComponentScreenOverlays.Update</c> 每帧清 0）：
        ///   · 红屏 = <c>PlayerHurtFeedback</c> 的衰减因子（受伤音效在 <see cref="ShowHurt"/>）；
        ///   · 黑屏 = 引擎 <c>ComponentFlu.Update:147-159</c> 同款 0.5/s 渐入到 0.95 / 0.5/s 渐出；
        ///   · 绿屏 = 引擎 <c>ComponentSickness.Update:112-131</c> 同款；
        ///   · 呕吐粒子跟随眼位 / 朝向（引擎 <c>ComponentSickness.Update:150-153</c>）。
        /// </summary>
        private bool AdvanceLocalEffectPresentation(int slot, ComponentPlayer player,
            LocalEffectState state, float deltaTime, out string reason)
        {
            reason = null;
            if (!(deltaTime >= 0f) || float.IsInfinity(deltaTime))
            {
                m_hurtRedoutFailures++;
                reason = "槽位 " + slot + " 的表现帧时长非法（" + deltaTime + "）";
                return false;
            }
            ComponentScreenOverlays overlays = player.ComponentScreenOverlays;
            if (overlays == null)
            {
                m_hurtRedoutFailures++;
                reason = "槽位 " + slot + " 的角色没有 ComponentScreenOverlays";
                return false;
            }
            bool complete = true;
            state.RedFactor = PlayerHurtFeedback.Decay(state.RedFactor, deltaTime);
            if (state.RedFactor > PlayerHurtFeedback.RedoutCutoff)
            {
                try
                {
                    overlays.RedoutFactor = MathUtils.Max(overlays.RedoutFactor,
                        state.RedFactor);
                    m_hurtRedoutFrames++;
                }
                catch (Exception e)
                {
                    m_hurtRedoutFailures++;
                    complete = false;
                    reason = "写 RedoutFactor 失败：" + e.GetType().Name + " " + e.Message;
                }
            }
            if (state.FluBlackoutDuration > 0f)
            {
                state.FluBlackoutDuration = MathUtils.Max(state.FluBlackoutDuration - deltaTime, 0f);
                state.FluBlackoutFactor = MathUtils.Min(state.FluBlackoutFactor +
                    0.5f * deltaTime, 0.95f);
            }
            else if (state.FluBlackoutFactor > 0f)
            {
                state.FluBlackoutFactor = MathUtils.Max(state.FluBlackoutFactor -
                    0.5f * deltaTime, 0f);
            }
            if (state.FluBlackoutFactor > 0.01f)
            {
                overlays.BlackoutFactor = MathUtils.Max(overlays.BlackoutFactor,
                    state.FluBlackoutFactor);
                m_fluBlackoutFrames++;
            }
            if (state.GreenoutDuration > 0f)
            {
                state.GreenoutDuration = MathUtils.Max(state.GreenoutDuration - deltaTime, 0f);
                state.GreenoutFactor = MathUtils.Min(state.GreenoutFactor + 0.5f * deltaTime, 0.95f);
            }
            else if (state.GreenoutFactor > 0f)
            {
                state.GreenoutFactor = MathUtils.Max(state.GreenoutFactor - 0.5f * deltaTime, 0f);
            }
            if (state.GreenoutFactor > 0.01f)
            {
                overlays.GreenoutFactor = MathUtils.Max(overlays.GreenoutFactor,
                    state.GreenoutFactor);
                m_fluGreenoutFrames++;
            }
            UpdatePukeTransform(player, state);
            return complete;
        }

        private void ApplyLocalState(int slot, ComponentPlayer player,
            in PlayerVitalsSnapshot snapshot, out string failure)
        {
            failure = null;
            ComponentHealth health = player.ComponentHealth;
            bool dead = snapshot.Dead;
            bool wasDead = m_localDeadSlots.Contains(slot);
            m_localDeathStates.TryGetValue(slot, out LocalDeathState deathState);
            bool windowActive = deathState != null && deathState.Window.Active;
            if (dead && windowActive)
            {
                if (deathState.Window.IsDeathAfterRespawn(snapshot.Revision))
                {
                    // 比复活裁决更新的死亡 = 玩家复活后真的又死了：本拍结束窗口并按真实死亡应用，
                    // 绝不把真实死亡挡在门外（旧死亡 rev 一定 ≤ 复活裁决 rev）。
                    m_replicaWindowNewDeath++;
                    deathState.Window.Close();
                    Info("本机槽位 " + slot + " 的复活窗口收到比复活裁决更新的死亡（rev=" +
                        snapshot.Revision + " > respawn=" +
                        deathState.Window.RespawnRevision +
                        "）→ 结束窗口并按真实新死亡应用");
                }
                else
                {
                    m_replicaWindowDeadSuppressed++;
                    Problem("respawn-window-dead:" + slot,
                        "复活窗口拒绝本机槽位 " + slot + " 的死亡状态（rev=" +
                        snapshot.Revision + "）：" + deathState.Window.Describe() +
                        " → 不写引擎、不挂挡门框 / 死亡屏（累计拒绝 " +
                        m_replicaWindowDeadSuppressed + " 条）");
                    return;
                }
            }
            if (dead && PlayerRespawnEpoch.IsStaleDead(EpochOf(slot), snapshot.Revision))
            {
                m_staleDeathRejects++;
                Problem("stale-dead:" + slot,
                    "拒绝把本机槽位 " + slot + " 的陈旧死亡状态写进引擎（revision=" +
                    snapshot.Revision + " ≤ 死亡 epoch " + EpochOf(slot) +
                    "）：它属于复活之前的那次死亡，写进去会让刚出生的角色立刻再判一次死" +
                    "（累计拒绝 " + m_staleDeathRejects + " 条）");
                return;
            }
            float previousHealth = health != null ? health.Health : FullHealth;
            float engineHealth = dead ? 0f : MathUtils.Saturate(snapshot.Health);
            if (health != null)
            {
                WriteField(health, "<Health>k__BackingField", engineHealth,
                    typeof(ComponentHealth));
                if (!dead)
                {
                    // 活着：m_lastHealth 必须跟着 Health（否则引擎的损伤 / 恢复增量算错）。
                    m_parentField.ModifyParentField(health, "m_lastHealth", engineHealth,
                        typeof(ComponentHealth));
                }
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
                if (!dead && snapshot.Health < previousHealth - PlayerVitalsLimits.ValueEpsilon)
                    ShowAuthoritativeHurt(slot, player, previousHealth - snapshot.Health);
                // 本机槽位唯一的权威生命缓存：自我伤害守卫 / 107 拒绝回滚都只认这个值。
                m_localAuthorityHealth[slot] = engineHealth;
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
            WriteLocalHud(slot, player, dead ? 0f : engineHealth, snapshot.Food,
                snapshot.Temperature, snapshot.TargetTemperature, dead,
                snapshot.FreezeProgress, snapshot.FreezeStage);

            if (dead && !wasDead)
            {
                m_localDeadSlots.Add(slot);
                m_localDeaths++;
                // 死亡边沿（主机权威状态先到、Death 事件后到或丢失）：红屏 / 黑屏 / 绿屏 /
                // 呕吐粒子收掉，火焰粒子也停 —— 与 ApplyLocalDeath 同一条表现口径。
                GetOrCreateLocalEffect(slot, player).Reset();
                StopLocalFireParticles(slot);
                ArmNativeDeath(slot, player, snapshot.CauseOfDeath, snapshot.Revision);
            }
            else if (!dead && wasDead)
            {
                // 主机把同一个实体改回活着（没走"删旧实体 → 新实体"那条路时才会出现）：
                // 生命已按权威写回，这里把死亡期状态（挡门对话框 / 引擎死亡状态）收掉。
                m_localDeadSlots.Remove(slot);
                ClearNativeDeath(slot, "主机权威状态显示本机角色已复活");
                RestorePlayerDataToPlaying(slot, player);
                m_localRespawns++;
            }
            // 等级：死亡快照只缓存（DeadDeferred），活着才写 PlayerData.Level。
            // 若引擎已经开始复活（旧实体已删、新实体已出生但重放还没跑），这一条活着的主机快照
            // 同样算"复活覆盖"（新实体默认 Level=1）。
            bool respawnPending = deathState != null && deathState.DeathToken != 0 &&
                deathState.ReplayedToken != Token(player);
            ApplyLocalLevel(slot, player, snapshot.Level, dead, snapshot.Revision,
                respawnPending, "state");
            // 复活窗口关闭：主机权威存活快照（rev 严格大于复活裁决 rev）。
            // 必须在这里主动清理死亡表现 —— 重放已经先把 m_localDeadSlots 清掉，旧的
            // "!dead && wasDead" 分支不会成立（实机死亡屏残留的根因之一）。
            if (!dead && windowActive && deathState.Window.CanCloseOnAlive(snapshot.Revision,
                    snapshot.Health))
            {
                if (!ClearRespawnDeathPresentation(slot, player, in snapshot,
                        out string cleanupReason))
                {
                    // 计数在 ClearRespawnDeathPresentation 里（失败即 +1，和 Describe 同源）。
                    failure = "复活窗口清理死亡表现失败：" + cleanupReason;
                    Problem("respawn-window-cleanup:" + slot,
                        "本机槽位 " + slot + " 的复活窗口关闭失败：清理死亡表现失败（" +
                        cleanupReason + "）→ 保持窗口、下一拍重试（累计 " +
                        m_replicaWindowCleanupFailures +
                        " 次；挡门框 / 死亡屏可能仍在，绝不假装清掉）");
                    return;
                }
                deathState.Window.Close();
                m_replicaWindowClosed++;
                Info("本机槽位 " + slot + " 的复活窗口关闭（respawnInProgress 结束）：主机权威存活 rev=" +
                    snapshot.Revision + " > respawn=" + deathState.Window.RespawnRevision +
                    " / epoch=" + EpochOf(slot) + "（死亡 rev=" +
                    deathState.Window.DeathRevision + "）→ 死亡表现已主动清除");
            }
        }

        /// <summary>
        /// 主机权威等级 → 本机 <c>PlayerData.Level</c>（vitals.proto 字段 21）。
        ///
        /// 判定全部走 <see cref="PlayerLevelPolicy.Decide"/>：
        ///   · Apply → 写 <c>PlayerData.Level</c>（自动属性；setter 维护
        ///     <c>&lt;Level&gt;k__BackingField</c>）；
        ///   · DeadDeferred → **不写**（引擎死亡时自己会减半；此时写主机值 = 双端各减一次），
        ///     但快照里的等级是主机减半后的权威值，照样缓存给复活重放；
        ///   · Stale → 忽略并计数；Invalid → 拒绝 + 计数（分类进 <c>levelRejectedBy</c>）+ 限流日志；
        ///   · Absent（0 = 老版本主机）/ Unchanged → 忽略。
        ///
        /// <paramref name="isRespawn"/> = 这次写入发生在"复活重放"链路（新实体默认 Level=1）：
        /// 成功写入计 <c>levelRespawnOverrides</c>。
        /// </summary>
        private void ApplyLocalLevel(int slot, ComponentPlayer player, float hostLevel, bool dead,
            uint revision, bool isRespawn, string source)
        {
            PlayerData playerData = player?.PlayerData;
            if (playerData == null)
            {
                m_levelCounters.NoteWriteFailure();
                Problem("level-apply:" + slot + ":" + source,
                    "本机槽位 " + slot + " 没有 PlayerData → 等级无法应用（" + source + "）");
                return;
            }
            float localLevel = playerData.Level;
            uint lastApplied = m_localLevelAppliedRevision.TryGetValue(slot,
                out uint appliedRevision)
                ? appliedRevision
                : 0u;
            PlayerLevelDecision decision = PlayerLevelPolicy.Decide(hostLevel, localLevel, dead,
                revision, lastApplied, out string reason);
            switch (decision)
            {
                case PlayerLevelDecision.Apply:
                {
                    try
                    {
                        playerData.Level = hostLevel;
                    }
                    catch (Exception e)
                    {
                        m_levelCounters.NoteWriteFailure();
                        Error("level-write:" + slot,
                            "把主机权威等级写进本机槽位 " + slot + " 的 PlayerData.Level 失败", e);
                        return;
                    }
                    m_localLevelAppliedRevision[slot] = revision;
                    bool pendingOverride = m_localLevelRespawnPending.Remove(slot);
                    bool respawnOverride = isRespawn || pendingOverride;
                    m_levelCounters.NoteApplied(respawnOverride);
                    m_localAuthorityLevel[slot] = hostLevel;
                    m_localAuthorityLevelRevision[slot] = revision;
                    Info("本机槽位 " + slot + " 的等级已由主机权威覆盖（" + source + "）：" +
                        localLevel.ToString("0.####") + " → " + hostLevel.ToString("0.####") +
                        "（rev=" + revision + (respawnOverride ? "，复活覆盖" : string.Empty) + "）");
                    return;
                }
                case PlayerLevelDecision.DeadDeferred:
                    m_levelCounters.NoteDeadDeferred();
                    if (PlayerLevelPolicy.IsValid(hostLevel) && hostLevel > 0f)
                    {
                        m_localAuthorityLevel[slot] = hostLevel;
                        m_localAuthorityLevelRevision[slot] = revision;
                    }
                    return;
                case PlayerLevelDecision.Stale:
                    m_levelCounters.NoteStale();
                    Problem("level-stale:" + slot, "忽略本机槽位 " + slot + " 的陈旧等级（" +
                        reason + "，来源 " + source + "）");
                    return;
                case PlayerLevelDecision.Invalid:
                    m_levelCounters.NoteInvalid(PlayerLevelPolicy.RejectionTag(hostLevel));
                    Problem("level-invalid:" + slot, "拒绝本机槽位 " + slot + " 的主机等级（" +
                        reason + "，来源 " + source + "）：不夹紧、不写入，等下一拍权威值");
                    return;
                case PlayerLevelDecision.Absent:
                    // 0 = 老版本主机没有携带等级：不是失败，忽略并计数（状态行 levelAbsent 可见）。
                    m_levelCounters.NoteAbsent();
                    return;
                default:
                    m_localLevelRespawnPending.Remove(slot);
                    m_levelCounters.NoteUnchanged();
                    return;
            }
        }

        /// <summary>
        /// 复活重放：把缓存的主机权威等级写进引擎新建的实体。缓存来自最近一条主机快照
        /// （死亡快照也缓存 —— 主机在死亡时已经把等级减半）。没有缓存时**不猜**：
        /// 计数 <c>levelAbsent</c> + 限流日志，等主机复活后的权威上报第一次到达时按 policy 写入
        /// （那时仍计 <c>levelRespawnOverrides</c>）。
        /// </summary>
        private void ApplyCachedAuthorityLevel(int slot, ComponentPlayer player,
            uint deathRevision, uint fallbackRevision, string source)
        {
            if (!m_localAuthorityLevel.TryGetValue(slot, out float hostLevel) || hostLevel <= 0f)
            {
                m_levelCounters.NoteAbsent();
                Problem("level-respawn:" + slot, "复活重放没有可用的主机权威等级（" + source +
                    "）→ 新实体保持引擎默认 Level=1，等主机复活后的权威上报（不猜等级）");
                return;
            }
            uint revision = m_localAuthorityLevelRevision.TryGetValue(slot, out uint cached)
                ? cached
                : fallbackRevision;
            // 缓存必须来自本次死亡之后（死亡事件 revision 之后的那条快照才是主机已减半的值）。
            // 死亡之前的活着快照 Revision < 死亡事件 Revision：写进去等于把未减半的旧等级带回新实体。
            if (deathRevision != 0 && revision <= deathRevision)
            {
                m_levelCounters.NoteAbsent();
                Problem("level-respawn-cache:" + slot, "复活重放的缓存等级 revision=" + revision +
                    " ≤ 死亡 revision=" + deathRevision +
                    "（属于死亡之前的快照）→ 不写进新实体，等复活后的权威上报");
                return;
            }
            ApplyLocalLevel(slot, player, hostLevel, dead: false, revision, isRespawn: true,
                source);
        }

        private void ApplyLocalDeath(int slot, ComponentPlayer player, string cause,
            uint revision)
        {
            LocalDeathState deathState = GetOrCreateLocalDeathState(slot);
            deathState.DeathRevision = PlayerRespawnEpoch.Advance(deathState.DeathRevision,
                revision);
            if (deathState.Window.Active)
            {
                if (deathState.Window.IsDeathAfterRespawn(revision))
                {
                    m_replicaWindowNewDeath++;
                    deathState.Window.Close();
                    Info("本机槽位 " + slot + " 的复活窗口收到比复活裁决更新的 Death 事件（rev=" +
                        revision + " > respawn=" + deathState.Window.RespawnRevision +
                        "）→ 结束窗口并按真实新死亡应用");
                }
                else
                {
                    m_replicaWindowDeadSuppressed++;
                    Problem("respawn-window-death-event:" + slot,
                        "复活窗口拒绝本机槽位 " + slot + " 的 Death 事件（rev=" + revision +
                        "，死因=" + (string.IsNullOrEmpty(cause) ? "未记录" : cause) + "）：" +
                        deathState.Window.Describe() +
                        " → 不写引擎、不挂挡门框 / 死亡屏（累计拒绝 " +
                        m_replicaWindowDeadSuppressed + " 条）");
                    return;
                }
            }
            if (PlayerRespawnEpoch.IsStaleDead(EpochOf(slot), revision))
            {
                m_staleDeathRejects++;
                Problem("stale-death-event:" + slot,
                    "拒绝应用本机槽位 " + slot + " 的陈旧 Death 事件（rev=" + revision +
                    " ≤ 死亡 epoch " + EpochOf(slot) +
                    "）：它属于复活之前的那次死亡（累计拒绝 " + m_staleDeathRejects + " 条）");
                return;
            }
            ComponentHealth health = player.ComponentHealth;
            if (health != null)
            {
                // 死因先写：引擎 PlayerDead enter 读的就是它（PlayerData.cs:277-283）。
                m_parentField.ModifyParentField(health, "<CauseOfDeath>k__BackingField",
                    string.IsNullOrEmpty(cause) ? "Unknown" : cause, typeof(ComponentHealth));
                if (health.Health > 0f)
                {
                    WriteField(health, "<Health>k__BackingField", 0f, typeof(ComponentHealth));
                    m_healthWrites++;
                }
            }
            // 死亡也是主机权威生命的一部分：自我伤害守卫不能把本地预测从 0 回滚成"活着"。
            m_localAuthorityHealth[slot] = 0f;
            ComponentVitalStats deathVital = player.ComponentVitalStats;
            WriteLocalHud(slot, player, 0f,
                deathVital != null ? deathVital.Food : 1f,
                deathVital != null ? deathVital.Temperature : DefaultTemperature,
                ReadTargetTemperature(deathVital), true, 0f, PlayerFreezePolicy.StageNone);
            // 死亡边沿：红屏 / 黑屏 / 绿屏 / 呕吐粒子全部收掉（引擎死亡画面接管）。
            GetOrCreateLocalEffect(slot, player).Reset();
            bool newlyDead = m_localDeadSlots.Add(slot);
            StopLocalFireParticles(slot);
            if (newlyDead) m_localDeaths++;
            ArmNativeDeath(slot, player, cause, revision);
            // 死亡边沿没有等级字段：按"死亡中"显式推迟（绝不在这里写 0 / 猜一个值）。
            ApplyLocalLevel(slot, player, 0f, dead: true, revision, isRespawn: false,
                "death-event");
        }

        /// <summary>
        /// 主机裁决"本机角色复活"（事件）。方案 A 下复活 = 引擎删旧实体 + 新建实体，
        /// 落点 / 数值由 <see cref="TryReplayLocalRespawn"/> 在**新实体出生后**一次性写入，
        /// 所以这里不做任何搬运。唯一例外：旧实体还在、而且引擎已经把它判死
        /// （主机在客户端动手之前就裁决复活）—— 那就把生命写回并让 PlayerData 回到 Playing，
        /// 否则引擎会一直停在 PlayerDead，等玩家点一下删掉自己。
        /// </summary>
        private void ApplyLocalRespawn(int slot, ComponentPlayer player,
            in PlayerVitalEventSnapshot evt)
        {
            ComponentHealth health = player.ComponentHealth;
            if (health != null && health.Health <= 0f)
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
                m_healthWrites++;
            }
            ComponentBody body = player.ComponentBody;
            if (body != null && evt.HasPosition)
            {
                body.Position = evt.Position;
                body.Velocity = Vector3.Zero;
                body.TargetCrouchFactor = 0f;
            }
            StopLocalFireParticles(slot);
            m_localSleep.Remove(slot);
            m_localDeadSlots.Remove(slot);
            // 主机裁决复活 = 新的权威生命从满血开始（新实体的模板值；主机随后会再下发确认）。
            m_localAuthorityHealth[slot] = FullHealth;
            ClearNativeDeath(slot, "主机裁决复活（旧实体仍在）");
            RestorePlayerDataToPlaying(slot, player);
            // 等级：旧实体仍活着时直接用缓存的主机权威值（主机死亡时已减半）。
            m_localLevelRespawnPending.Add(slot);
            ApplyCachedAuthorityLevel(slot, player, 0u, evt.Revision, "respawn-event");
            ComponentVitalStats respawnVital = player.ComponentVitalStats;
            GetOrCreateLocalEffect(slot, player).Reset();
            WriteLocalHud(slot, player, FullHealth,
                respawnVital != null ? respawnVital.Food : 1f,
                respawnVital != null ? respawnVital.Temperature : DefaultTemperature,
                ReadTargetTemperature(respawnVital), false, 0f, PlayerFreezePolicy.StageNone);
            m_localRespawns++;
        }

        /// <summary>
        /// 引擎 <c>PlayerData</c> 若还停在 <c>PlayerDead</c>（或残留 <c>m_playerDeathTime</c>），
        /// 把它拉回 <c>Playing</c> 并切回第一人称相机。
        ///
        /// **只在主机确认"这个角色还活着"时调用**（<see cref="ApplyLocalRespawn"/> /
        /// 主机权威状态从 Dead 翻成 Alive）。方案 A 下死亡本身**不**走这里：
        /// 客户端必须让引擎自己进 PlayerDead，死亡画面才是原生的。
        /// </summary>
        private bool RestorePlayerDataToPlaying(int slot, ComponentPlayer player)
        {
            PlayerData playerData = player?.PlayerData;
            if (playerData == null) return false;
            bool restored = false;
            try
            {
                object deathTime = m_parentField.GetParentField(playerData, "m_playerDeathTime",
                    typeof(PlayerData));
                if (deathTime != null)
                {
                    m_parentField.ModifyParentField(playerData, "m_playerDeathTime", null,
                        typeof(PlayerData));
                    restored = true;
                }
                StateMachine stateMachine = m_parentField.GetParentField<StateMachine>(playerData,
                    "m_stateMachine", typeof(PlayerData));
                if (stateMachine != null && stateMachine.CurrentState == "PlayerDead")
                {
                    stateMachine.TransitionTo("Playing");
                    restored = true;
                }
                if (!restored) return false;
                GameWidget widget = player.GameWidget;
                if (widget != null && widget.ActiveCamera is DeathCamera)
                {
                    FppCamera fpp = widget.FindCamera<FppCamera>(false);
                    if (fpp != null) widget.ActiveCamera = fpp;
                }
                // 客户端**不写**死亡大字（引擎 PlayerData.PlayerDead 的 DisplayLargeMessage 由引擎
                // 自己管，30 秒后自己过期，见 ComponentGui.cs:385-388）；第三屏的死亡文字
                // 由 Engine/MirrorViewFeedback.cs 负责（引擎同款时长 + 复活清除）。
                m_localPlayerDataRestores++;
                Info("本机槽位 " + slot + "：主机确认角色活着 → 已把引擎 PlayerData 从死亡状态" +
                    "恢复到 Playing 并切回第一人称相机（累计 " + m_localPlayerDataRestores + " 次）");
                return true;
            }
            catch (Exception e)
            {
                m_replayFailures++;
                Problem("restore-playing:" + slot,
                    "把本机槽位 " + slot + " 的引擎 PlayerData 从死亡状态恢复失败：" +
                    e.GetType().Name + " " + e.Message +
                    " → 引擎可能停在 PlayerDead（点一下会删实体本地复活）");
                return false;
            }
        }

        /// <summary>
        /// 掉血的即时反馈（血条闪烁 + 受伤音效）。**红屏不在这里写一次**：引擎
        /// <c>ComponentScreenOverlays.Update</c> 每帧把 <c>RedoutFactor</c> 清 0，只写一次 =
        /// 只闪一帧（实机问题 3"客户端受伤没有红屏"就是这么来的）。红屏因子由
        /// <see cref="ShowAuthoritativeHurt"/>（95 上报）/ 自我伤害 107 的每帧衰减路径维护。
        /// </summary>
        private void ShowHurt(ComponentPlayer player, float loss)
        {
            ComponentGui gui = player.ComponentGui;
            if (gui != null && gui.HealthBarWidget != null)
            {
                gui.HealthBarWidget.Flash(PlayerHurtFeedback.FlashCount(loss));
                gui.HealthBarWidget.Value = MathUtils.Max(0f,
                    player.ComponentHealth != null ? player.ComponentHealth.Health : 0f);
            }
            player.ComponentCreatureSounds?.PlayPainSound();
            m_hurtFeedbackSound++;
        }

        /// <summary>
        /// 主机权威掉血（PLAYER_VITALS_REPORT 95）→ 受伤音效 + **每帧衰减**的红屏因子
        /// （引擎 <c>ComponentHealth.Update:141-147/256-261</c> 的同等行为）。
        /// 只写表现，不调用 <c>Injure</c>、不判死（死亡仍由主机 Death 事件 / Health=0 驱动）。
        /// </summary>
        private void ShowAuthoritativeHurt(int slot, ComponentPlayer player, float loss)
        {
            ShowHurt(player, loss);
            LocalEffectState state = GetOrCreateLocalEffect(slot, player);
            state.RedFactor = PlayerHurtFeedback.AddDamage(state.RedFactor, loss);
            m_hurtRedoutHits++;
        }

        private LocalEffectState GetOrCreateLocalEffect(int slot, ComponentPlayer player)
        {
            if (!m_localEffects.TryGetValue(slot, out LocalEffectState state))
            {
                state = new LocalEffectState();
                m_localEffects[slot] = state;
            }
            int token = Token(player);
            if (state.EntityToken != token)
            {
                state.Reset();
                state.EntityToken = token;
            }
            return state;
        }

        /// <summary>
        /// 把一份主机权威体征写进本机角色的 HUD（血条 / 食物条 / 温度条贴图 / 冰冻覆盖）。
        /// 只写**显示属性**，不写任何组件字段、不跑模拟。输入非法时保留上一次有效显示并计数。
        ///
        /// 冻结覆盖（实机问题 1/2）：**只用主机下发的 <c>freeze_progress</c> / <c>freeze_stage</c>**
        /// （vitals.proto 字段 22/23）作为插值目标，不再从本机温度推导。老版本主机（两者都是 0）
        /// 且权威温度 &lt; 6 时，用**主机权威温度**推导并计 <c>freezeLegacy</c>（仍不是本地模拟）。
        /// </summary>
        private void WriteLocalHud(int slot, ComponentPlayer player, float healthValue,
            float foodValue, float temperature, float targetTemperature, bool dead,
            float freezeProgress, uint freezeStage)
        {
            LocalHudState state = GetOrCreateLocalHud(slot, player);
            float targetFreeze;
            if (PlayerFreezePolicy.TryResolve(freezeProgress, freezeStage, temperature,
                    out float resolvedProgress, out uint resolvedStage, out bool legacy,
                    out string freezeError))
            {
                if (legacy) m_freezeLegacy++;
                state.HasFreezeTarget = true;
                state.FreezeLegacy = legacy;
                if (state.TargetFreezeStage != resolvedStage) m_freezeStageChanges++;
                state.TargetFreezeProgress = resolvedProgress;
                state.TargetFreezeStage = resolvedStage;
                m_freezeHostValue = resolvedProgress;
                m_freezeClientApplied++;
                targetFreeze = resolvedProgress;
            }
            else
            {
                m_freezeInvalid++;
                Problem("freeze-values:" + slot, "本机槽位 " + slot + " 的主机冻结值非法：" +
                    freezeError + "（progress=" + freezeProgress + " stage=" + freezeStage +
                    "）→ 保留上一次有效冻结目标（不拿 0 顶替、不改本机权威）");
                targetFreeze = state.HasFreezeTarget
                    ? state.TargetFreezeProgress
                    : state.Values.IceFactor;
            }
            if (!PlayerVitalsPresentation.TryCreateWithFreeze(healthValue, foodValue, temperature,
                    targetTemperature, targetFreeze, dead, out PlayerVitalsHudValues values))
            {
                m_hudNonFinite++;
                Problem("hud-values:" + slot, "本机槽位 " + slot + " 的体征 HUD 输入非法：" +
                    "health=" + healthValue + " food=" + foodValue + " temperature=" +
                    temperature + " target=" + targetTemperature + " freeze=" + targetFreeze +
                    " → 保留上一次有效显示（不写 NaN、不拿默认值顶替）");
                return;
            }
            state.Values = values;
            state.Dead = dead;
            if (!ApplyLocalHud(slot, player, state, 0f, out string reason))
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
        public int RefreshLocalPresentation(float deltaTime, out string reason)
        {
            reason = null;
            if (m_disabledLocalSlots.Count == 0 && m_localHud.Count == 0 &&
                m_localDeadSlots.Count == 0 && m_localEffects.Count == 0)
                return 0;
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
                if (!ApplyLocalHud(slot, player, state, deltaTime, out string applyReason))
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
            state.Dead = dead;
            // 第一条主机快照到达前：用当前组件值（= 下载世界 / 上一次主机值）作为起点，
            // 插值器直接贴合（不从 0 淡入）。第一条真正的冻结字段会立刻覆盖它。
            if (!state.HasFreezeTarget)
            {
                state.TargetFreezeProgress = values.IceFactor;
                state.TargetFreezeStage = PlayerFreezePolicy.StageNone;
                if (state.Freeze.TrySetImmediate(values.IceFactor, out string freezeError))
                    m_freezeSeeds++;
                else
                    Problem("hud-seed-freeze:" + slot, "本机槽位 " + slot +
                        " 的冰冻插值播种失败：" + freezeError);
                state.HealthBar.TrySetImmediate(values.HealthBarValue, out _);
                state.FoodBar.TrySetImmediate(values.FoodBarValue, out _);
            }
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
                // 角色实体换代 = 新 GUI：控件里的贴图不一定还是我们上次写的那张；
                // 冰冻插值也不能从上一次死亡画面的值淡入（下次推进直接播种到主机目标）。
                state.EntityToken = token;
                state.TextureIndex = int.MinValue;
                state.HasFreezeTarget = false;
                state.TargetFreezeProgress = 0f;
                state.TargetFreezeStage = PlayerFreezePolicy.StageNone;
                state.Freeze.Reset();
                state.HealthBar.Reset();
                state.FoodBar.Reset();
            }
            return state;
        }

        /// <summary>
        /// 写显示属性。温度条贴图按档位去重（<c>ContentManager.Get</c> 只在档位变化时调用）；
        /// 冰冻覆盖每次都写（引擎每帧清 0）。任何控件缺失都返回 false + 明确原因，不静默跳过。
        /// </summary>
        private bool ApplyLocalHud(int slot, ComponentPlayer player, LocalHudState state,
            float deltaTime, out string reason)
        {
            reason = null;
            bool effectsComplete = AdvanceLocalEffectPresentation(slot, player,
                GetOrCreateLocalEffect(slot, player), deltaTime, out string effectReason);
            if (!effectsComplete && reason == null) reason = effectReason;
            ComponentGui gui = player.ComponentGui;
            if (gui == null)
            {
                m_hudPresentationFailures++;
                if (reason == null) reason = "角色没有 ComponentGui";
                return false;
            }
            bool complete = true;
            // 死亡：血条立刻归零（引擎 ComponentHealth.Update 在死亡前已经把 Health/血条写成 0），
            // 不从上一次血量慢慢滑下去。
            if (state.Dead) state.HealthBar.Reset();
            float healthTarget = state.Dead ? 0f : state.Values.HealthBarValue;
            if (!state.HealthBar.TryAdvance(healthTarget, deltaTime, out float healthBar,
                    out string healthError))
            {
                m_hudPresentationFailures++;
                complete = false;
                if (reason == null) reason = "血条插值失败：" + healthError;
                healthBar = state.HealthBar.Value;
            }
            if (gui.HealthBarWidget != null)
                gui.HealthBarWidget.Value = healthBar;
            else
            {
                m_hudPresentationFailures++;
                complete = false;
                if (reason == null) reason = "ComponentGui 没有 HealthBarWidget";
            }
            if (!state.FoodBar.TryAdvance(state.Values.FoodBarValue, deltaTime,
                    out float foodBar, out string foodError))
            {
                m_hudPresentationFailures++;
                complete = false;
                if (reason == null) reason = "食物条插值失败：" + foodError;
                foodBar = state.FoodBar.Value;
            }
            if (gui.FoodBarWidget != null)
                gui.FoodBarWidget.Value = foodBar;
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
                m_iceFactorWriteFailures++;
                if (reason == null) reason = "角色没有 ComponentScreenOverlays（冰冻覆盖无法显示）";
                return false;
            }
            // 连续插值：每帧按时间常数 + 最大速率逼近主机权威冻结进度，绝不把 5 Hz 快照直接阶跃写入。
            // 死亡后引擎不再写 IceFactor（整个画面由死亡相机接管）：这里也立刻归零，与单机一致。
            if (state.Dead) state.Freeze.Reset();
            float target = state.Dead
                ? 0f
                : (state.HasFreezeTarget ? state.TargetFreezeProgress : state.Values.IceFactor);
            if (!state.Freeze.TryAdvance(target, deltaTime, out float iceFactor,
                    out string freezeError))
            {
                m_freezeInvalid++;
                Problem("freeze-advance:" + slot, "本机槽位 " + slot +
                    " 的冰冻插值推进失败：" + freezeError + " → 保留当前值 " +
                    state.Freeze.Value.ToString("F3"));
                iceFactor = state.Freeze.Value;
            }
            if (target > 0f || iceFactor > 0f) m_freezeInterpolatedFrames++;
            try
            {
                overlays.IceFactor = iceFactor;
                m_hudIceWrites++;
            }
            catch (Exception e)
            {
                m_iceFactorWriteFailures++;
                complete = false;
                if (reason == null)
                    reason = "写 IceFactor 失败：" + e.GetType().Name + " " + e.Message;
            }
            return complete && effectsComplete && reason == null;
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

        // ---------------------------------------------------------------- 死亡期：挡门对话框 / 冻结副本

        /// <summary>本槽位当前的死亡 epoch（0 = 还没复活过）。</summary>
        private uint EpochOf(int slot) =>
            m_localDeathEpoch.TryGetValue(slot, out uint epoch) ? epoch : 0u;

        /// <summary>当前仍在 respawnInProgress 的槽位数（诊断 / 状态行）。</summary>
        private int CountActiveRespawnWindows()
        {
            int active = 0;
            foreach (LocalDeathState state in m_localDeathStates.Values)
            {
                if (state.Window.Active) active++;
            }
            return active;
        }

        private LocalDeathState GetOrCreateLocalDeathState(int slot)
        {
            if (!m_localDeathStates.TryGetValue(slot, out LocalDeathState state))
            {
                state = new LocalDeathState();
                m_localDeathStates[slot] = state;
            }
            return state;
        }

        /// <summary>
        /// 主机裁决死亡 → 把引擎推进**原生**死亡流程，并（按世界模式）挂上挡门对话框。
        ///
        /// 引擎事实（照做，不另造表现）：
        ///   · <c>PlayerData.Playing</c>：<c>ComponentHealth.Health &lt;= 0f</c> → 只记
        ///     <c>m_playerDeathTime = Time.RealTime</c>（<c>PlayerData.cs:270</c>）；
        ///   · <c>PlayerDead</c> enter：切 <c>DeathCamera</c> + <c>DisplayLargeMessage("YOU HAVE DIED",
        ///     cause + 模式行)</c> + <c>Level = max(floor(Level/2), 1)</c>
        ///     （<c>PlayerData.cs:275-300</c>）；
        ///   · <c>PlayerDead</c> update：<c>Time.RealTime - m_playerDeathTime &gt; 1.5 &amp;&amp;
        ///     !DialogsManager.HasDialogs(ComponentPlayer.GuiWidget) &amp;&amp;
        ///     ComponentPlayer.GameWidget.Input.Any</c> → <c>m_project.RemoveEntity(...)</c>
        ///     （<c>PlayerData.cs:306-320</c>）。
        ///
        /// 所以挡门对话框的唯一作用就是让上面第三个条件永远不成立，**直到主机批准**
        /// （<see cref="TryExecuteLocalRespawn"/> 关掉它并执行同一行 RemoveEntity）。
        /// 死亡屏（相机 / 文字 / 等级减半 / 死亡姿态）全部由引擎自己完成。
        /// </summary>
        private void ArmNativeDeath(int slot, ComponentPlayer player, string cause, uint revision)
        {
            LocalDeathState state = GetOrCreateLocalDeathState(slot);
            state.DeathRevision = PlayerRespawnEpoch.Advance(state.DeathRevision, revision);
            m_deathScreensShown++;
            PlayerRespawnMode mode = GetRespawnMode(out string modeReason);
            if (mode == PlayerRespawnMode.Cruel)
            {
                // Cruel：引擎自己的点击分支就是"打开 GameMenuDialog"（PlayerData.cs:308-311），
                // 所以**不挂挡门**：死亡屏与菜单都是原生的，mod 不参与、也不发复活请求。
                Info("本机槽位 " + slot + " 在 Cruel 模式死亡：不挂挡门对话框，" +
                    "点击后由引擎自己打开 GameMenuDialog（PlayerData.cs:308-311），不发复活请求");
                return;
            }
            ShowDeathGateDialog(slot, player, mode, modeReason);
        }

        /// <summary>
        /// 挂上挡门对话框（挂在 <c>ComponentPlayer.GuiWidget</c> 上，因为引擎
        /// <c>DialogsManager.HasDialogs</c> 比较的正是这个父控件）。
        /// </summary>
        private void ShowDeathGateDialog(int slot, ComponentPlayer player, PlayerRespawnMode mode,
            string modeReason)
        {
            LocalDeathState state = GetOrCreateLocalDeathState(slot);
            int token = Token(player);
            if (state.GateDialog != null && state.GateToken == token) return;
            GameWidget widget = player.GameWidget;
            ContainerWidget guiWidget = widget?.GuiWidget;
            if (guiWidget == null)
            {
                m_gateFailures++;
                Problem("gate-widget:" + slot,
                    "挡门对话框挂不上：本机槽位 " + slot + " 没有 GameWidget.GuiWidget → " +
                    "引擎会在死亡后 1.5 秒内自己删实体并本地复活（主机裁决失效）");
                return;
            }
            try
            {
                var dialog = new Dialog();
                // Dispatcher.Dispatch 在主线程立刻执行：对话框这一帧就挂上，
                // 早于引擎下一次 PlayerDead.Update 的条件判定。
                DialogsManager.ShowDialog(guiWidget, dialog);
                state.GateDialog = dialog;
                state.GateToken = token;
                m_gateShown++;
                NeutralizeDialogCover(slot, guiWidget, dialog);
                Info("本机槽位 " + slot + " 已挂挡门对话框（模式=" +
                    PlayerRespawnRules.Describe(mode) +
                    "）：引擎 PlayerDead 的点击分支被挡住（!DialogsManager.HasDialogs，" +
                    "PlayerData.cs:306），等主机批准后才执行 RemoveEntity" +
                    (mode == PlayerRespawnMode.Unknown
                        ? "；读不到世界复活模式（" + (modeReason ?? "端口没有给原因") +
                          "）→ 不给请求路径（不猜模式）"
                        : mode == PlayerRespawnMode.AdventureRestart
                            ? "；Adventure 未允许复活：引擎点击后会 SwitchScreen(\"GameLoading\", " +
                              "..., \"AdventureRestart\") 重启本地世界（PlayerData.cs:312-315），" +
                              "联机不能照搬"
                            : string.Empty));
            }
            catch (Exception e)
            {
                m_gateFailures++;
                Error("gate-show:" + slot,
                    "挂挡门对话框失败（本机槽位 " + slot + "）：引擎可能自行删实体本地复活", e);
            }
        }

        /// <summary>
        /// 引擎 <c>DialogsManager.ShowDialog</c> 会在同一个父控件里紧挨着对话框插一个全屏黑色遮罩
        /// （<c>DialogsManager.cs:8-20</c> 的 <c>CoverWidget</c>，<c>Alpha=192</c>，
        /// <c>DialogsManager.cs:67</c> 加入 <c>parentWidget.Children</c>）。它不参与
        /// <c>HasDialogs</c> 判定，但会把引擎原生的死亡屏（"YOU HAVE DIED" 大字）压暗到 25%。
        /// 这里只把它改成全透明，让死亡屏保持原生亮度；遮罩不参与点击判定，改它不影响任何逻辑。
        ///
        /// 找不到遮罩（引擎内部分层变了）时**不静默**：计数 + 限流日志，死亡屏会变暗但仍可用。
        /// </summary>
        private void NeutralizeDialogCover(int slot, ContainerWidget parent, Dialog dialog)
        {
            try
            {
                WidgetsList children = parent.Children;
                int index = children.IndexOf(dialog);
                if (index <= 0)
                {
                    m_gateCoverMisses++;
                    Problem("gate-cover-index:" + slot,
                        "挡门对话框在 GuiWidget.Children 里的位置是 " + index +
                        " → 找不到它前面的遮罩，引擎死亡屏会被压暗（不假装成功）");
                    return;
                }
                Widget cover = children[index - 1];
                if (cover == null || cover.GetType().Name != "CoverWidget")
                {
                    m_gateCoverMisses++;
                    Problem("gate-cover-type:" + slot,
                        "挡门对话框前面的控件不是 DialogsManager.CoverWidget（实际=" +
                        (cover == null ? "null" : cover.GetType().Name) +
                        "）→ 引擎死亡屏会被压暗（不假装成功）");
                    return;
                }
                if (!(cover is RectangleWidget rectangle))
                {
                    m_gateCoverMisses++;
                    Problem("gate-cover-shape:" + slot,
                        "DialogsManager.CoverWidget 不是 RectangleWidget → 无法改成透明");
                    return;
                }
                rectangle.FillColor = Color.Transparent;
                m_gateCoverNeutralized++;
            }
            catch (Exception e)
            {
                m_gateCoverMisses++;
                Error("gate-cover:" + slot, "把挡门对话框的遮罩改成透明失败", e);
            }
        }

        /// <summary>
        /// 关掉某个槽位的挡门对话框（幂等）。返回 true = 这次真的关了一个；
        /// false = 没有对话框或**关闭失败**（失败时保留引用，下一次清理重试，绝不假装关掉）。
        /// </summary>
        private bool HideDeathGateDialog(int slot, string why)
        {
            if (!m_localDeathStates.TryGetValue(slot, out LocalDeathState state) ||
                state.GateDialog == null)
                return false;
            Dialog dialog = state.GateDialog;
            try
            {
                // DialogsManager.HideDialog 立刻把对话框从 m_dialogs 移除（淡出动画由 Update 收尾），
                // 所以引擎 PlayerDead 的 !HasDialogs 条件当帧就成立。
                DialogsManager.HideDialog(dialog);
            }
            catch (Exception e)
            {
                m_gateFailures++;
                // 限流日志（10 s）：清理失败可能每拍重试，不能用 Error 每帧刷屏。
                Problem("gate-hide:" + slot,
                    "关闭挡门对话框失败（本机槽位 " + slot + "：" + why + "）：" +
                    e.GetType().Name + " " + e.Message +
                    " → 保留引用，下一次清理重试（不假装关掉）");
                return false;
            }
            state.GateDialog = null;
            state.GateToken = 0;
            m_gateHidden++;
            Info("已关掉本机槽位 " + slot + " 的挡门对话框（" + why + "）");
            return true;
        }

        /// <summary>死亡期结束（复活 / 主机改回活着 / 世界切换）：关掉挡门对话框并计数。</summary>
        private void ClearNativeDeath(int slot, string why)
        {
            if (m_localDeathStates.TryGetValue(slot, out LocalDeathState state) &&
                state.GateDialog != null && !HideDeathGateDialog(slot, why))
            {
                Problem("gate-clear:" + slot,
                    "死亡期结束但挡门对话框没关掉（本机槽位 " + slot + "：" + why +
                    "）→ 保留引用，下一次清理重试（绝不假装已关）");
            }
        }

        /// <summary>
        /// 关闭复活窗口前的**主动清理**：把可能已经误挂的死亡表现全部收掉。
        /// 只在主机权威 Health &gt; 0 时调用（调用方已确认）。
        ///
        /// 为什么必须主动清：复活 = 删旧实体 + 新实体，重放会先把 <c>m_localDeadSlots</c>
        /// 清掉；旧的"同一实体从死改活"分支（<c>!dead &amp;&amp; wasDead</c>）因此永远不会成立。
        /// 实机 rev=3513 的旧死亡状态在新实体上又走了一次 PlayerDead，挡门框 / 死亡屏被挂回，
        /// 而复活重放只写数值 —— 没有人清表现。
        ///
        /// 清理项（任何一项失败都返回 false + 原因；调用方计数 + 10 s 限流日志，下一拍重试）：
        ///   ① 引擎 PlayerData 回 Playing（含 m_playerDeathTime）；
        ///   ② ComponentHealth：Health / CauseOfDeath / DeathTime / HealthChange / m_lastHealth；
        ///   ③ ComponentCreatureModel.DeathPhase / DeathCauseOffset（死亡姿态）；
        ///   ④ 引擎死亡大消息（ComponentGui.m_message：玩家看到的 "YOU HAVE DIED"）；
        ///   ⑤ 挡门对话框；
        ///   ⑥ 相机 DeathCamera → FppCamera（只在死亡相机还挂着时切）。
        /// </summary>
        private bool ClearRespawnDeathPresentation(int slot, ComponentPlayer player,
            in PlayerVitalsSnapshot snapshot, out string reason)
        {
            reason = null;
            if (snapshot.Dead || !(snapshot.Health > 0f))
            {
                reason = "主机权威不是存活（health=" + snapshot.Health + "）→ 不清理死亡表现";
                return false;
            }
            var failures = new List<string>(4);
            float authoritative = MathUtils.Saturate(snapshot.Health);
            // ① PlayerData：先离开 PlayerDead（PlayerDead.Update 一旦看到输入就会 RemoveEntity）。
            try
            {
                RestorePlayerDataToPlaying(slot, player);
                PlayerData playerData = player.PlayerData;
                StateMachine stateMachine = playerData != null
                    ? m_parentField.GetParentField<StateMachine>(playerData, "m_stateMachine",
                        typeof(PlayerData))
                    : null;
                object deathTime = playerData != null
                    ? m_parentField.GetParentField(playerData, "m_playerDeathTime",
                        typeof(PlayerData))
                    : null;
                if (stateMachine == null || stateMachine.CurrentState != "Playing" ||
                    deathTime != null)
                {
                    failures.Add("PlayerData 未回到 Playing（state=" +
                        (stateMachine != null ? stateMachine.CurrentState : "null") +
                        (deathTime != null ? "，m_playerDeathTime 仍有值" : string.Empty) + "）");
                }
            }
            catch (Exception e)
            {
                failures.Add("恢复 PlayerData 异常：" + e.GetType().Name + " " + e.Message);
            }
            // ② 生命字段：Health 用主机权威值；其余三项是引擎死亡分支的入口 / 残留。
            ComponentHealth health = player.ComponentHealth;
            if (health == null)
            {
                failures.Add("缺少 ComponentHealth");
            }
            else
            {
                try
                {
                    WriteField(health, "<Health>k__BackingField", authoritative,
                        typeof(ComponentHealth));
                    m_parentField.ModifyParentField(health, "m_lastHealth", authoritative,
                        typeof(ComponentHealth));
                    m_parentField.ModifyParentField(health, "<CauseOfDeath>k__BackingField",
                        string.Empty, typeof(ComponentHealth));
                    m_parentField.ModifyParentField(health, "<DeathTime>k__BackingField", null,
                        typeof(ComponentHealth));
                    m_parentField.ModifyParentField(health, "<HealthChange>k__BackingField", 0f,
                        typeof(ComponentHealth));
                    if (!(health.Health > 0f))
                        failures.Add("Health 仍为 " + health.Health);
                    if (!string.IsNullOrEmpty(health.CauseOfDeath))
                        failures.Add("CauseOfDeath 仍为 " + health.CauseOfDeath);
                    if (health.DeathTime != null)
                        failures.Add("DeathTime 仍为 " + health.DeathTime);
                    if (health.HealthChange != 0f)
                        failures.Add("HealthChange 仍为 " + health.HealthChange);
                }
                catch (Exception e)
                {
                    failures.Add("清 ComponentHealth 异常：" + e.GetType().Name + " " + e.Message);
                }
            }
            // ③ 死亡姿态：Health 回到 >0 后 ComponentCreatureModel.Update 不会再推进 DeathPhase，
            //    已经推到 1 的姿势必须显式归零（与远端化身 ResetDeathState 同一口径）。
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model == null)
            {
                failures.Add("缺少 ComponentCreatureModel");
            }
            else
            {
                try
                {
                    m_parentField.ModifyParentField(model, "<DeathPhase>k__BackingField", 0f,
                        typeof(ComponentCreatureModel));
                    m_parentField.ModifyParentField(model, "<DeathCauseOffset>k__BackingField",
                        Vector3.Zero, typeof(ComponentCreatureModel));
                    if (model.DeathPhase > 0f)
                        failures.Add("DeathPhase 仍为 " + model.DeathPhase);
                }
                catch (Exception e)
                {
                    failures.Add("清 DeathPhase 异常：" + e.GetType().Name + " " + e.Message);
                }
            }
            // ④ 死亡大消息（ComponentGui.m_message）：30 秒才自然过期，复活必须立刻清。
            ComponentGui gui = player.ComponentGui;
            if (gui == null)
            {
                failures.Add("缺少 ComponentGui");
            }
            else
            {
                try
                {
                    m_parentField.ModifyParentField(gui, "m_message", null, typeof(ComponentGui));
                    object message = m_parentField.GetParentField(gui, "m_message",
                        typeof(ComponentGui));
                    if (message != null) failures.Add("死亡大消息 m_message 未清空");
                }
                catch (Exception e)
                {
                    failures.Add("清死亡大消息异常：" + e.GetType().Name + " " + e.Message);
                }
            }
            // ⑤ 挡门对话框（挂着的对话框会让引擎 !HasDialogs 条件永远不成立 / 界面挡输入）。
            if (m_localDeathStates.TryGetValue(slot, out LocalDeathState gateState) &&
                gateState.GateDialog != null &&
                !HideDeathGateDialog(slot, "复活窗口关闭：主机权威存活"))
            {
                failures.Add("挡门对话框关闭失败");
            }
            // ⑥ 相机：死亡相机必须回第一人称（PlayerData 回 Playing 时引擎通常已切，但
            //    "同一实体改活"等路径可能仍停在 DeathCamera 上）。
            GameWidget widget = player.GameWidget;
            if (widget == null)
            {
                failures.Add("缺少 GameWidget");
            }
            else
            {
                try
                {
                    if (widget.ActiveCamera is DeathCamera)
                    {
                        FppCamera fpp = widget.FindCamera<FppCamera>(false);
                        if (fpp == null)
                        {
                            failures.Add("找不到 FppCamera");
                        }
                        else
                        {
                            widget.ActiveCamera = fpp;
                            if (!(widget.ActiveCamera is FppCamera))
                                failures.Add("ActiveCamera 仍是 " + widget.ActiveCamera);
                        }
                    }
                }
                catch (Exception e)
                {
                    failures.Add("切回第一人称相机异常：" + e.GetType().Name + " " + e.Message);
                }
            }
            // 死亡期缓存 / 表现：全部归零（幂等）。
            m_localDeadSlots.Remove(slot);
            m_localEffects.Remove(slot);
            StopLocalFireParticles(slot);
            m_localAuthorityHealth[slot] = authoritative;
            if (m_localHud.TryGetValue(slot, out LocalHudState hud)) hud.Dead = false;
            if (failures.Count > 0)
            {
                m_replicaWindowCleanupFailures++;
                reason = string.Join("；", failures);
                return false;
            }
            Info("本机槽位 " + slot + " 的死亡表现已清理（PlayerData=Playing / 死亡相机→Fpp / " +
                "死亡大消息=清 / 挡门框=关 / DeathPhase=0 / Health=" +
                authoritative.ToString("F3") + "）");
            return true;
        }

        /// <summary>
        /// 与引擎 <c>!DialogsManager.HasDialogs(ComponentPlayer.GuiWidget)</c>
        /// （<c>PlayerData.cs:306</c>）同口径，但**排除本槽位自己的挡门对话框** ——
        /// 否则玩家永远点不动（挡门框一直在）。
        /// </summary>
        private static bool HasForeignDialogs(Widget guiWidget, Dialog own)
        {
            if (guiWidget == null) return false;
            var dialogs = DialogsManager.Dialogs;
            for (int i = 0; i < dialogs.Count; i++)
            {
                Dialog dialog = dialogs[i];
                if (dialog == null || ReferenceEquals(dialog, own)) continue;
                if (dialog.ParentWidget == guiWidget) return true;
            }
            return false;
        }

        /// <summary>
        /// 每帧读死亡屏输入，口径是**按下沿**而不是 <c>Input.Any</c> 电平（根因与引擎证据见
        /// <see cref="PlayerDeathInputSample"/>）：返回 true 仅当这一帧出现了**新的按下**
        /// （触摸按下帧 / 鼠标左(右)键按下帧 / 手柄 A 按下帧 / 键盘的**非自动重复**按下）。
        /// <c>GameWidget.Input.Any</c> 只是"这一帧有输入事件"的标志，而键盘的
        /// <c>LastKey</c> 在自动重复时也会置位（<c>WidgetInput.cs:704</c> +
        /// <c>Keyboard.cs:170-176</c>）—— 直接用它判点击会让"死亡瞬间正按着的方向键"
        /// 在 1.5 s 门槛后自动复活。按住 / 重复的电平只计数 + 限流日志，绝不当点击。
        /// 对话框打开时不判定（与引擎 <c>PlayerData.cs:306</c> 的 <c>!DialogsManager.HasDialogs</c> 同口径）。
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
                PlayerDeathInputSample sample = ReadDeathInput(widget.Input);
                // 引擎 PlayerData.cs:306 同口径，但排除**我们自己**的挡门对话框：
                // 挡门框一直挂着，若不排除就永远点不动（详见 HasForeignDialogs）。
                m_localDeathStates.TryGetValue(key.Slot, out LocalDeathState gateState);
                bool noDialogs = !HasForeignDialogs(player.GuiWidget,
                    gateState?.GateDialog);
                tapped = noDialogs && sample.IsFreshPress;
                if (!noDialogs && sample.IsFreshPress)
                {
                    Problem("death-tap-dialog:" + key.Slot,
                        "本机槽位 " + key.Slot + " 的死亡屏上有**别的**对话框（退出菜单等）→ " +
                        "本帧的按下不当作复活点击（与引擎 PlayerData.cs:306 同口径）");
                }
                if (noDialogs && sample.IsLevelOnly)
                {
                    m_deathTapLevelsIgnored++;
                    Problem("death-tap-level:" + key.Slot,
                        "本机槽位 " + key.Slot + " 的死亡屏输入只有 Any 电平（按住 / 键盘自动重复），" +
                        "没有任何新的按下 → **不当作点击**（累计忽略 " + m_deathTapLevelsIgnored +
                        " 帧）：必须是按下沿（松手后再次按下）才发复活请求");
                }
                return true;
            }
            catch (Exception e)
            {
                reason = "读取死亡屏输入异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 从一帧 <c>WidgetInput</c> 采样死亡屏点击信号（纯读，不消费、不清输入）。
        /// 引擎事实：
        ///   · 触摸只在 <c>TouchLocationState.Pressed</c> 那一帧置 <c>Tap</c>（<c>WidgetInput.cs:915-923</c>），
        ///     之后 <c>Touch.AfterFrame</c> 把它改成 <c>Moved</c>（<c>Engine/Engine/Input/Touch.cs:56-66</c>）；
        ///   · 鼠标 <c>Tap</c> 只在 <c>IsMouseButtonDownOnce</c> 那一帧（<c>WidgetInput.cs:864-871</c>）；
        ///   · 手柄 A 按下帧置 <c>Ok</c> + <c>Tap</c>（<c>WidgetInput.cs:845</c>）；
        ///   · 键盘 <c>LastKey</c> 在自动重复时也会被置位，只有 <c>IsKeyDownOnce</c> 才代表第一次按下
        ///     （<c>Engine/Engine/Input/Keyboard.cs:170-176</c>）。
        /// </summary>
        private static PlayerDeathInputSample ReadDeathInput(WidgetInput input)
        {
            if (input == null) return default;
            Key? lastKey = input.LastKey;
            bool lastKeyPresent = lastKey.HasValue && lastKey.Value != Key.Escape;
            // Escape 留给引擎的菜单路径（WidgetInput.cs:704 同样把它排除在 Any 之外）；
            // 只有真正"第一次按下"才查 IsKeyDownOnce —— 自动重复期间它为 false。
            bool lastKeyDownOnce = lastKeyPresent && input.IsKeyDownOnce(lastKey.Value);
            return new PlayerDeathInputSample(input.Any, input.Tap.HasValue, input.Click.HasValue,
                input.SpecialClick.HasValue, input.Ok, lastKeyPresent, lastKeyDownOnce);
        }

        /// <summary>
        /// 不可复活模式的点击（不发送 PLAYER_RESPAWN_REQUEST）：
        ///   · Cruel：**不需要 mod 做任何事** —— 挡门对话框不挂，引擎自己的
        ///     <c>PlayerData.cs:308-311</c> 会在点击后打开 <c>GameMenuDialog</c>；
        ///     走到这里说明上层的模式判定与端口不一致，明确失败（不重复打开菜单）；
        ///   · Adventure 禁复活：引擎的终止路径是
        ///     <c>ScreensManager.SwitchScreen("GameLoading", GameManager.WorldInfo, "AdventureRestart")</c>
        ///     （<c>PlayerData.cs:312-315</c>）。联机客户端不能重启主机世界（会把本端拉出共享会话、
        ///     造成主机侧死亡角色 / 世界状态分叉），所以挡门对话框继续挂着，这里打开同一个
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
                    m_terminalDeathTaps++;
                    reason = "cruel：引擎自己的 PlayerDead 点击分支就会打开 GameMenuDialog" +
                        "（PlayerData.cs:308-311），mod 不重复处理";
                    return false;
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
                m_gateFailures++;
                Error("terminal-death-tap:" + key, "不可复活模式点击处理失败", e);
                return false;
            }
        }

        /// <summary>
        /// **主机批准之后**由 mod 推进引擎死亡流程：
        ///   ① 关掉挡门对话框；
        ///   ② 执行引擎 <c>PlayerData.PlayerDead</c> 点击分支里的那一行
        ///      <c>m_project.RemoveEntity(ComponentPlayer.Entity, disposeEntity: true)</c>
        ///      （<c>Survivalcraft/Game/PlayerData.cs:318</c>）。
        /// 之后 PrepareSpawn → SpawnDialog → WaitForTerrain → SpawnPlayer → FppCamera 全自动
        /// （<c>PlayerData.cs:163-256 / 404-421</c>）。
        ///
        /// 前置条件必须真实成立（否则明确失败，不强行推状态机）：
        ///   · 本机槽位有 Project / 有槽位记录；
        ///   · 引擎 PlayerData 真的在 PlayerDead（<c>m_playerDeathTime</c> 有值）。
        /// 幂等：已经删过（没有 ComponentPlayer）= 返回 true + 说明。
        /// </summary>
        public bool TryExecuteLocalRespawn(PlayerKey key, out string reason)
        {
            reason = null;
            int slot = key.Slot;
            Project project = GameManager.Project;
            if (project == null)
            {
                m_engineRespawnFailures++;
                reason = "没有加载中的 Project（还没进世界）";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, slot, out PlayerData playerData, out reason))
            {
                m_engineRespawnFailures++;
                return false;
            }
            LocalDeathState state = GetOrCreateLocalDeathState(slot);
            ComponentPlayer player = playerData?.ComponentPlayer;
            if (player == null)
            {
                if (state.DeathToken != 0)
                {
                    reason = "本机槽位 " + slot + " 的旧实体已经删除（引擎在 PrepareSpawn / " +
                        "WaitForTerrain 中）";
                    return true;
                }
                m_engineRespawnFailures++;
                reason = "本机槽位 " + slot + " 没有 ComponentPlayer，也没有执行过删除 → " +
                    "不能推进复活流程";
                return false;
            }
            object deathTime;
            try
            {
                deathTime = m_parentField.GetParentField(playerData, "m_playerDeathTime",
                    typeof(PlayerData));
            }
            catch (Exception e)
            {
                m_engineRespawnFailures++;
                Error("execute-death-time:" + slot,
                    "读取引擎 PlayerData.m_playerDeathTime 失败（无法确认引擎已判死）", e);
                reason = "读取 m_playerDeathTime 失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            if (deathTime == null)
            {
                m_engineRespawnFailures++;
                reason = "引擎 PlayerData 不在 PlayerDead（m_playerDeathTime 为空）：" +
                    "本机槽位 " + slot + " 还没被引擎判死 → 不删实体";
                return false;
            }
            try
            {
                HideDeathGateDialog(slot, "主机已批准复活");
                state.DeathToken = Token(player);
                state.ReplayedToken = 0;
                // Source: Survivalcraft/Game/PlayerData.cs:318（引擎点击分支的同一行）
                project.RemoveEntity(player.Entity, disposeEntity: true);
                m_engineRespawnExecuted++;
                m_localDeadSlots.Remove(slot);
                state.Window.Open(state.DeathRevision, 0u);
                m_replicaWindowOpened++;
                Info("本机槽位 " + slot + " 进入复活窗口（respawnInProgress）：死亡 rev=" +
                    state.DeathRevision + " / epoch=" + state.Window.Epoch +
                    "（复活裁决 revision 等重放请求 / 主机复活事件带入）→ 第一份权威存活快照" +
                    "之前拒绝任何死亡状态（旧死亡不得再挂死亡屏）");
                Info("本机槽位 " + slot + " 已执行引擎 RemoveEntity(disposeEntity: true) → " +
                    "引擎开始原生 PrepareSpawn → SpawnDialog → WaitForTerrain → SpawnPlayer" +
                    "（出生后由 TryReplayLocalRespawn 整体重放主机权威状态）");
                return true;
            }
            catch (Exception e)
            {
                m_engineRespawnFailures++;
                Error("execute-respawn:" + slot,
                    "执行引擎 RemoveEntity 失败（本机槽位 " + slot + "）", e);
                reason = "执行 RemoveEntity 异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// **新实体出生后的权威重放**（方案 A 的第三个闸门）。
        ///
        /// 引擎 <c>SpawnPlayer</c> 建的是全新实体（模板血量 / 空背包 / 空衣服），落点还会被
        /// <c>FindNoIntroSpawnPosition</c> 偏移最多 8 格（<c>PlayerData.cs:454-477</c>）。
        /// 所以新实体一出现就用主机权威数据覆盖：
        ///   ① 落点 = 主机裁决的落点，同时写 <c>PlayerData.SpawnPosition</c>（以后复活的锚点）；
        ///   ② **不恢复**背包 / 手持 / 衣服 / 合成格：引擎死亡分支已把它们掉在死亡地点
        ///      （<c>ComponentHealth.cs:275</c>，单机原版行为），复活后背包为空、由玩家自己捡回。
        ///      在此重放冻结副本会与地上那一份重复（用户实机缺陷），所以只计数"跳过"；
        ///   ③ 重新摘掉新实体的生命 / 体征 / 睡眠 / 病程 / 火焰模拟 + <c>IsInvulnerable</c>；
        ///   ④ 重新初始化 HUD（新 GUI 是新的）；
        ///   ⑤ 推进死亡 epoch（此后不新于它的死亡状态一律拒写）；
        ///   ⑥ 五项体征由模块用最近一条主机快照补写（模块持有快照，端口不缓存）。
        /// </summary>
        public PlayerRespawnReplayStatus TryReplayLocalRespawn(PlayerKey key,
            in PlayerRespawnReplayRequest request, out string reason)
        {
            reason = null;
            int slot = key.Slot;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return PlayerRespawnReplayStatus.Failed;
            }
            if (!m_spawner.TryGetLocalPlayer(project, slot, out PlayerData playerData, out reason))
                return PlayerRespawnReplayStatus.Failed;
            LocalDeathState state = GetOrCreateLocalDeathState(slot);
            if (state.Window.Active)
            {
                state.Window.NoteDeathRevision(request.DeathRevision);
                state.Window.NoteRespawnRevision(request.RespawnRevision);
            }
            ComponentPlayer player = playerData?.ComponentPlayer;
            if (player == null)
            {
                m_replayWaitFrames++;
                reason = "引擎还没建出新实体（ComponentPlayer 为空：PrepareSpawn / " +
                    "WaitForTerrain 中；已等待 " + m_replayWaitFrames + " 帧）";
                return PlayerRespawnReplayStatus.Waiting;
            }
            int token = Token(player);
            if (state.ReplayedToken == token)
            {
                reason = "本机槽位 " + slot + " 的这个实体已经重放过";
                return PlayerRespawnReplayStatus.NoPending;
            }
            try
            {
                if (request.HasPosition && request.HasFinitePosition)
                {
                    ComponentBody body = player.ComponentBody;
                    if (body != null)
                    {
                        body.Position = request.Position;
                        body.Velocity = Vector3.Zero;
                        body.TargetCrouchFactor = 0f;
                    }
                    // 落点锚点：以后每次复活的搜索中心都是主机裁决的这个点。
                    playerData.SpawnPosition = request.Position;
                }
                else
                {
                    Problem("replay-no-position:" + slot,
                        "主机没有给出有限落点（" + request.Describe() +
                        "）→ 落点保持引擎 FindNoIntroSpawnPosition 的结果，其余状态照常重放");
                }
                // 物品只由引擎死亡掉落一次：这里**绝不**把冻结副本写回新实体，
                // 否则地上那一份 + 手里这一份 = 凭空多一份（用户实机缺陷）。
                m_respawnInventoryDroppedByEngine++;
                // 新实体的组件实例是新的：重新摘模拟 + IsInvulnerable（内部按实体标识去重）。
                if (DisableLocalSimulation(out int disabled, out string simReason) < 0)
                {
                    Problem("replay-sim:" + slot, "摘模拟失败：" + simReason);
                }
                if (!string.IsNullOrEmpty(simReason))
                {
                    Problem("replay-sim:" + slot,
                        "重放时重新摘本机模拟失败：" + simReason + "（已摘 " + disabled + " 个）");
                }
                // 新 GUI = 新控件：HUD 重新初始化（DisableLocalSimulation 里已按新 token 做一次，
                // 这里再显式保证一次，避免它被上一步的失败路径跳过）。
                m_localHud.Remove(slot);
                if (!SeedLocalHud(slot, player, out string hudReason))
                {
                    Problem("replay-hud:" + slot,
                        "重放时初始化 HUD 失败：" + (hudReason ?? "端口没有给原因"));
                }
                m_localSleep.Remove(slot);
                m_localDeadSlots.Remove(slot);
                m_localEffects.Remove(slot);
                StopLocalFireParticles(slot);
                // 新实体的模板生命是 1；在主机下一拍权威上报到达之前，自我伤害守卫必须以它为准，
                // 否则会拿死亡前的 0 回滚，把刚出生的角色立刻写死。
                m_localAuthorityHealth[slot] = FullHealth;
                m_localDeathEpoch[slot] = PlayerRespawnEpoch.Advance(EpochOf(slot), request.Epoch);
                // 等级也是"主机权威整体重放"的一部分：引擎新实体默认 Level=1，用最近一条
                // 主机快照（死亡快照也缓存 —— 主机已减半）覆盖；没有缓存则显式 absent。
                m_localLevelRespawnPending.Add(slot);
                ApplyCachedAuthorityLevel(slot, player, request.DeathRevision,
                    request.RespawnRevision, "respawn-replay");
                state.ReplayedToken = token;
                state.DeathToken = 0;
                m_replayApplied++;
                Info("本机槽位 " + slot + " 的复活权威重放完成（落点=" +
                    (request.HasPosition ? request.Position.ToString() : "未提供") +
                    "，死亡 rev=" + request.DeathRevision + " / 复活 rev=" +
                    (request.RespawnRevision != 0
                        ? request.RespawnRevision.ToString()
                        : "0（事件未到，epoch 先取死亡 rev 下界）") +
                    " / 复活 epoch=" + EpochOf(slot) +
                    "，背包/手持/衣服/合成格=跳过（引擎死亡掉落，累计跳过 " +
                    m_respawnInventoryDroppedByEngine + " 次））");
                return PlayerRespawnReplayStatus.Applied;
            }
            catch (Exception e)
            {
                m_replayFailures++;
                Error("replay:" + slot, "本机槽位 " + slot + " 的复活权威重放失败", e);
                reason = "重放异常：" + e.GetType().Name + " " + e.Message;
                return PlayerRespawnReplayStatus.Failed;
            }
        }

        /// <summary>
        /// 把"已向主机请求复活、正在等裁决"显示在本机死亡屏上（小消息）。
        /// 只做表现，不改请求状态。
        /// </summary>
        public bool TryShowLocalRespawnPending(PlayerKey key, out string reason)
        {
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            ComponentGui gui = player.ComponentGui;
            if (gui == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 ComponentGui（无法显示等待提示）";
                return false;
            }
            gui.DisplaySmallMessage("Respawn requested", Color.White, false, true);
            return true;
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

        private void WriteLocalFire(ComponentPlayer player, int slot, float fireDuration)
        {
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            if (onFire == null) return;
            // 不调 SetOnFire（它会取 max，主机火焰衰减时降不下来）；直接写权威时长，
            // 粒子表现由客户端自己维护（ComponentOnFire.Update 已被摘掉）。
            m_parentField.ModifyParentField(onFire, "m_fireDuration",
                MathUtils.Max(fireDuration, 0f), typeof(ComponentOnFire));
            if (fireDuration > 0.01f) TryEnsureLocalFireParticles(player, slot);
            else StopLocalFireParticles(slot);
            m_conditionWrites++;
        }

        private bool TryEnsureLocalFireParticles(ComponentPlayer player, int slot)
        {
            ComponentBody body = player.ComponentBody;
            Project project = player.Project;
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            if (body == null || particles == null)
            {
                Problem("local-fire-particles:" + slot,
                    "本机角色火焰粒子无法创建（缺少 ComponentBody / SubsystemParticles）" +
                    "（火焰状态仍然跟随主机）");
                return false;
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
            return true;
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
                m_localEffects.Remove(stale[i]);
                m_localSleep.Remove(stale[i]);
                m_localAuthorityLevel.Remove(stale[i]);
                m_localAuthorityLevelRevision.Remove(stale[i]);
                m_localLevelAppliedRevision.Remove(stale[i]);
                m_localLevelRespawnPending.Remove(stale[i]);
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
                ResetNativeDeathStates("没有加载中的 Project");
                ResetLocalLevelState("没有加载中的 Project");
                m_hostLevelAuthority.Reset();
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
            // 退出客户端角色：本地预测回滚守卫 / 权威生命缓存 / 未决红屏全部作废，
            // 否则下次进世界会拿上一个会话的权威值回滚。
            RemoveSelfDamageGuard();
            m_localAuthorityHealth.Clear();
            m_selfDamageRedout.Clear();
            // 冻结 / 表现事件的每键状态作废：本机模拟恢复后由引擎自己写 IceFactor / 病程表现，
            // 旧的主机目标值不能带进下一个会话。
            foreach (KeyValuePair<int, LocalEffectState> effect in m_localEffects)
                effect.Value.Reset();
            m_localEffects.Clear();
            m_effectLastSequence.Clear();
            m_effectWatch.Clear();
            // 受伤闪红的"上一拍血量"同样作废：新会话第一次应用不闪（见 m_flashHealth 注释）。
            m_flashHealth.Clear();
            m_freezeHostStage.Clear();
            // 第三分屏冰覆盖的只读缓存同理：新会话开始，旧的权威冻结值不能带过去。
            m_freezeHostProgress.Clear();
            // 本地模拟恢复后，引擎的 ComponentVitalStats.UpdateTemperature 会重新写温度条 /
            // 冰冻覆盖 —— 清掉我们这份只读显示缓存，避免两套来源。
            m_localHud.Clear();
            // 睡眠表现同理：ComponentSleep.Update 已重新加回 SubsystemUpdate，淡入 / 淡出 /
            // 黑屏 / 提示重新由引擎写；我们停止每帧补写，避免两套来源。
            m_localSleep.Clear();
            // 客户端角色结束（退出房间 / 断开 / 改当主机）：死亡期的挡门对话框与冻结副本全部作废，
            // 否则下次进世界会带着上一次的对话框 / 旧副本。
            ResetNativeDeathStates("本机模拟恢复（退出客户端角色）");
            // 等级权威缓存同理：新世界 / 新会话的 revision 从头开始，旧等级不能带过去。
            ResetLocalLevelState("本机模拟恢复（退出客户端角色）");
            m_hostLevelAuthority.Reset();
            if (m_localSimRestores > 0)
                Info("本机角色的生命 / 体征 / 睡眠 / 病程 / 火焰模拟已恢复（累计恢复 " +
                    m_localSimRestores + " 个槽位）");
            return true;
        }

        /// <summary>
        /// 清空死亡期状态（挡门对话框 / 冻结副本 / 死亡 epoch）。离开世界、断开连接、
        /// 改当主机时调用 —— 不留待重放项，也不留旧 epoch（新世界的 revision 从头开始）。
        /// </summary>
        private void ResetNativeDeathStates(string why)
        {
            if (m_localDeathStates.Count == 0 && m_localDeathEpoch.Count == 0) return;
            Info("清空本机死亡期状态（" + why + "）：挡门对话框 " + m_localDeathStates.Count +
                " 个，死亡 epoch " + m_localDeathEpoch.Count + " 个");
            foreach (KeyValuePair<int, LocalDeathState> item in m_localDeathStates)
            {
                if (item.Value.GateDialog == null) continue;
                try
                {
                    DialogsManager.HideDialog(item.Value.GateDialog);
                    m_gateHidden++;
                }
                catch (Exception e)
                {
                    m_gateFailures++;
                    Error("gate-reset:" + item.Key, "清空死亡期状态时关闭挡门对话框失败", e);
                }
            }
            m_localDeathStates.Clear();
            m_localDeathEpoch.Clear();
        }

        /// <summary>
        /// 清空本机等级权威缓存（离开世界 / 断开连接 / 改当主机时）。只清"每槽位状态"，
        /// 累计计数器保留（状态行仍能看到本次会话同步过多少等级）。
        /// </summary>
        private void ResetLocalLevelState(string why)
        {
            if (m_localAuthorityLevel.Count == 0 && m_localAuthorityLevelRevision.Count == 0 &&
                m_localLevelAppliedRevision.Count == 0 && m_localLevelRespawnPending.Count == 0)
                return;
            Info("清空本机等级权威缓存（" + why + "）：权威 " + m_localAuthorityLevel.Count +
                " 个，已应用 " + m_localLevelAppliedRevision.Count + " 个，待复活覆盖 " +
                m_localLevelRespawnPending.Count + " 个");
            m_localAuthorityLevel.Clear();
            m_localAuthorityLevelRevision.Clear();
            m_localLevelAppliedRevision.Clear();
            m_localLevelRespawnPending.Clear();
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
            snapshot = CaptureOne(key, player, ReadObservedLevel(playerData));
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
                ActivePlayerState state = ApplyRemoteState(key, player, in snapshot);
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

        /// <summary>
        /// 主机：批准客户端复活请求后**立即**复活化身（不再等待任何客户端握手）。
        /// 落点 = 主机锚点（<c>PlayerData.SpawnPosition</c>，零 / 非有限时退到
        /// <c>SubsystemPlayers.GlobalSpawnPosition</c>）；客户端随后由 mod 执行 RemoveEntity、
        /// 引擎原生 PrepareSpawn / SpawnPlayer，并在新实体出生后按 Respawn 事件重放落点与体征。
        ///
        /// Source: Survivalcraft/Game/PlayerData.cs:454-477（FindNoIntroSpawnPosition ±8 格搜索）
        /// Source: Survivalcraft/Game/PlayerData.cs:586-640（SpawnPlayer 之后才写位置）
        /// </summary>
        public bool TryRespawnAvatar(PlayerKey key, out Vector3 position, out string reason)
        {
            return TryRespawnAvatarCore(key, out position, out reason);
        }

        private bool TryRespawnAvatarCore(PlayerKey key, out Vector3 position, out string reason)
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
            Vector3 spawn = ResolveHostRespawnAnchor(playerData);
            if (!IsFinite(spawn) || spawn == Vector3.Zero)
            {
                reason = "玩家键 " + key + " 没有可用的出生点（PlayerData.SpawnPosition 与 " +
                    "SubsystemPlayers.GlobalSpawnPosition 都是零 / 非有限值）→ 不猜落点";
                m_avatarRespawnFailures++;
                m_respawnAnchorFailures++;
                return false;
            }
            Vector3 target = spawn;
            try
            {
                var evt = new PlayerVitalEventSnapshot(key, 1, PlayerVitalEventKind.Respawn,
                    string.Empty, true, target);
                ResetAfterRespawn(player, in evt);
                // 落点锚点与客户端重放保持同一语义（PlayerData.cs:454 的搜索中心）。
                playerData.SpawnPosition = target;
                // 化身复活回到同一个 PlayerData：清掉"本次死亡已减半"标记；
                // 等级保持主机已减半的权威值（下一次死亡可以再减一次）。
                m_hostLevelAuthority.NoteAlive(key);
                position = target;
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

        /// <summary>主机侧的复活锚点：<c>PlayerData.SpawnPosition</c>，不可用时退到全局出生点。</summary>
        private Vector3 ResolveHostRespawnAnchor(PlayerData playerData)
        {
            Vector3 spawn = playerData != null ? playerData.SpawnPosition : Vector3.Zero;
            if (IsFinite(spawn) && spawn != Vector3.Zero) return spawn;
            SubsystemPlayers players = GameManager.Project?
                .FindSubsystem<SubsystemPlayers>(false);
            return players != null ? players.GlobalSpawnPosition : Vector3.Zero;
        }

        private static bool IsFinite(Vector3 value) =>
            PlayerVitalsSnapshot.IsFiniteValue(value.X) &&
            PlayerVitalsSnapshot.IsFiniteValue(value.Y) &&
            PlayerVitalsSnapshot.IsFiniteValue(value.Z);

        // ================================================================ 抓取 / 写入公共实现

        /// <summary>
        /// 读主机侧角色（本机槽位 / 客户端化身）的 <c>PlayerData.Level</c>（vitals.proto 字段 21）。
        /// 读不到 / 非有限 / 负数 / 超上限 = 抓取失败：计数 + 限流日志，快照按 0（未携带）上报，
        /// **绝不夹紧**（夹紧会把越界值悄悄变成合法等级）。
        /// </summary>
        private float ReadCapturedLevel(PlayerData playerData, PlayerKey key)
        {
            if (playerData == null)
            {
                m_levelCounters.NoteCaptureFailure("no-player-data");
                Problem("level-capture:" + key, "玩家键 " + key + " 没有 PlayerData → 等级按 0" +
                    "（未携带）上报（累计抓取失败 " + m_levelCounters.CaptureFailures + " 次）");
                return 0f;
            }
            float raw = playerData.Level;
            if (!PlayerLevelCapture.TryCapture(raw, out float level, out string captureError))
            {
                string tag = PlayerLevelPolicy.RejectionTag(raw);
                m_levelCounters.NoteCaptureFailure(tag);
                Problem("level-capture:" + key, "玩家键 " + key + " 的 " + captureError +
                    " → 等级按 0（未携带）上报，不夹紧（累计抓取失败 " +
                    m_levelCounters.CaptureFailures + " 次，原因 " +
                    m_levelCounters.CaptureFailuresByText + "）");
                return 0f;
            }
            return level;
        }

        /// <summary>
        /// 主机侧客户端化身的权威等级：先读 <c>PlayerData.Level</c>，死亡时按
        /// <see cref="PlayerLevelPolicy.HalveOnDeath"/> **减一次**并写回。
        ///   · 本机槽位（<c>key.ClientId == 0</c>）不走减半：引擎 <c>PlayerData.PlayerDead</c>
        ///     入场已经减半（<c>PlayerData.cs:299</c>）；
        ///   · 主机侧客户端化身的 <c>PlayerData</c> 不在 <c>SubsystemPlayers.m_playersData</c> 里，
        ///     引擎不会替它跑 <c>PlayerDead</c> → 这里减，写回的是**唯一权威源**；
        ///   · 写回 <c>PlayerData.Level</c> 后，现有记录链
        ///     （<c>NetworkAvatarSpawner.CapturePlayerRecord</c> → <c>PlayerAvatarModule</c>
        ///     每 5 秒 <c>RefreshHostRecords</c>）会把它持久化到身份记录；
        ///   · 写回失败**不登记**"已减半"→ 下一拍重试（绝不假装成功，也不会每拍重复减）。
        /// </summary>
        private float ReadHostAuthorityLevel(PlayerKey key, PlayerData playerData,
            ComponentPlayer player, bool dead)
        {
            float level = ReadCapturedLevel(playerData, key);
            if (!m_hostLevelAuthority.ShouldHalve(key, Token(player), dead, level,
                    out float halvedLevel, out string halveReason))
            {
                if (!string.IsNullOrEmpty(halveReason)) Problem("level-halve:" + key, halveReason);
                // 复活边沿：清掉"本次死亡已减半"标记（下一次死亡可以再减一次）。
                if (!dead) m_hostLevelAuthority.NoteAlive(key);
                return halvedLevel;
            }
            try
            {
                playerData.Level = halvedLevel;
            }
            catch (Exception e)
            {
                m_levelCounters.NoteCaptureFailure("halve-write");
                Error("level-halve-write:" + key,
                    "把减半后的等级写回 PlayerData.Level 失败（下一拍重试，不假装成功）", e);
                return level;
            }
            m_hostLevelAuthority.CommitHalved(key, Token(player));
            m_levelCounters.NoteHostHalved();
            Info(halveReason + " → 已写回 PlayerData.Level；" +
                "NetworkAvatarSpawner.CapturePlayerRecord 会把它持久化到身份记录" +
                "（累计减半死亡期 " + m_levelCounters.HostHalved + " 次）");
            return halvedLevel;
        }

        /// <summary>
        /// 只读观察（客户端 <see cref="TryGetAvatarVitals"/> / 诊断）：不做减半、不改任何状态；
        /// 非法值按 0（未携带）返回，绝不把越界值当成权威。
        /// </summary>
        private static float ReadObservedLevel(PlayerData playerData)
        {
            if (playerData == null) return 0f;
            float level = playerData.Level;
            return PlayerLevelCapture.TryCapture(level, out float captured, out _) ? captured : 0f;
        }

        private PlayerVitalsSnapshot CaptureOne(PlayerKey key, ComponentPlayer player,
            float level)
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
            // 冻结进度 / 阶段（vitals.proto 字段 22/23）：**主机**用自己的 Temperature 算，
            // 客户端只跟随（不再本地从温度推导）。Source: ComponentVitalStats.cs:532-557,579
            float freezeProgress = 0f;
            uint freezeStage = PlayerFreezePolicy.StageNone;
            if (!PlayerFreezePolicy.TryCapture(vital.Temperature, out freezeProgress,
                    out freezeStage))
            {
                m_freezeCaptureFailures++;
                Problem("freeze-capture:" + key, "主机角色 " + key +
                    " 的 Temperature 不是有限数 → 冻结进度按不可观测处理（下发 0/0）");
                freezeProgress = 0f;
                freezeStage = PlayerFreezePolicy.StageNone;
            }
            else
            {
                m_freezeHostValue = freezeProgress;
                // 只读缓存：95 上报的**原值**。第三分屏的冰覆盖只允许读它（实机问题 1），
                // 保证两端是同一个 float、同一时刻；抓取失败时不覆盖（客户端也会保留上一次有效目标）。
                m_freezeHostProgress[key] = freezeProgress;
                if (m_freezeHostStage.TryGetValue(key, out uint previousStage) &&
                    previousStage != freezeStage)
                {
                    m_freezeStageChanges++;
                    Info("主机角色 " + key + " 冻结阶段 " + previousStage + " → " +
                        freezeStage + "（进度 " + freezeProgress.ToString("F3") + "）");
                }
                m_freezeHostStage[key] = freezeStage;
            }
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
                sleepState.ForcedSleep,
                level,
                freezeProgress,
                freezeStage);
        }

        /// <summary>
        /// **只读查询**（实机问题 1）：主机为某个角色算出的**冻结进度 / 阶段** —— 就是
        /// vitals.proto 字段 22/23 的**原值**（与随 95 下发给客户端的是同一个 float）。
        ///
        /// 为什么需要它：第三分屏（<c>Engine/MirrorViewFeedback</c>）以前自己按
        /// <c>ComponentVitalStats.Temperature</c> 算 <c>Saturate(1 - T/6)</c> 并阶跃写入，
        /// 而客户端走的是本条权威值 + <see cref="PlayerPresentationInterpolator"/> 平滑 ——
        /// 两条数据 / 两条时序必然错开（实测"客户端先冻、第三屏晚很多、屏幕占比不一样"）。
        /// 现在第三屏只读这里的值，用**同一个**插值器逼近，两端完全同步。
        ///
        /// 只读、不改任何状态、不产生日志（日志由调用方限流）：
        ///   · false = 还没抓到过这个角色（还没进世界 / 化身未就绪 / 抓取失败）；
        ///     调用方必须明确失败 / 跳过，**不得**拿 0 顶替（0 是"不冷"这个合法值）；
        ///   · 抓取失败时保留上一次成功值（与客户端"保留上一次有效冻结目标"同口径）。
        /// </summary>
        public bool TryGetAuthorityFreeze(PlayerKey key, out float progress, out uint stage,
            out string reason)
        {
            progress = 0f;
            stage = PlayerFreezePolicy.StageNone;
            reason = null;
            if (m_freezeHostProgress.TryGetValue(key, out float capturedProgress) &&
                m_freezeHostStage.TryGetValue(key, out uint capturedStage))
            {
                progress = capturedProgress;
                stage = capturedStage;
                m_freezeAuthorityReads++;
                return true;
            }
            m_freezeAuthorityMisses++;
            reason = "主机还没有抓到玩家键 " + key + " 的冻结进度（还没进世界 / 化身未就绪 / " +
                "抓取失败；已采样冻结值 " + m_freezeHostProgress.Count + " 个角色）";
            return false;
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
        private ActivePlayerState ApplyRemoteState(PlayerKey key, ComponentPlayer player,
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
                    // 死亡本身不补闪红（死亡表现另走 DeathPhase）；同时把"上一拍血量"记成 0，
                    // 复活后的第一份存活快照不会被当成掉血。
                    m_flashHealth[key] = 0f;
                }
                else
                {
                    float safeHealth = MathUtils.Saturate(snapshot.Health);
                    if (previous <= 0f) ResetDeathState(player);
                    WriteField(health, "<Health>k__BackingField", safeHealth,
                        typeof(ComponentHealth));
                    m_parentField.ModifyParentField(health, "m_lastHealth", safeHealth,
                        typeof(ComponentHealth));
                    ApplyRemoteHurtFlash(key, player, safeHealth);
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

        /// <summary>
        /// 远端化身受伤闪红（纯表现，不产生伤害、不改血量）。
        ///
        /// 引擎原生闪红由 <c>ComponentCreatureModel.Update</c> 读
        /// <c>ComponentHealth.HealthChange</c> 驱动（ComponentCreatureModel.cs:185-193），
        /// 而本层的权威写回把 Health 与 m_lastHealth 写成同一个值 ⇒ HealthChange 恒为 0
        /// ⇒ 客户端看到的远端化身永远不会闪红。这里按"上一次已应用的权威血量"判断真实下降，
        /// 直接写引擎私有字段 <c>m_injuryColorFactor = 1f</c>（与 AvatarActionBridge 的
        /// HurtFlash 同一手法），之后由引擎自己按 3f/s 衰减，不需要每帧驱动。
        ///
        /// 第一次应用不闪：新化身 Health 默认 1，若直接比较会在出生那一刻凭空闪一下。
        /// </summary>
        private void ApplyRemoteHurtFlash(PlayerKey key, ComponentPlayer player, float health)
        {
            if (m_flashHealth.TryGetValue(key, out float lastApplied) &&
                health < lastApplied - 1.0e-4f)
            {
                ComponentCreatureModel model = player?.ComponentCreatureModel;
                if (model != null)
                {
                    try
                    {
                        m_parentField.ModifyParentField(model, "m_injuryColorFactor", 1f,
                            typeof(ComponentCreatureModel));
                        m_hurtFlashes++;
                    }
                    catch (Exception e)
                    {
                        Error("hurt-flash:" + key, "远端化身受伤闪红写入失败（" + key +
                            "）：" + e.GetType().Name + " " + e.Message, e);
                    }
                }
            }
            m_flashHealth[key] = health;
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

        /// <summary>
        /// **客户端每帧**：读本机角色的"入睡申请"边沿，并**当场收回**主机未确认的本地睡眠。
        ///
        /// 为什么这样做（根因）：引擎唯一的入睡入口是 <c>ClothingWidget.Update</c>
        /// （Source: Survivalcraft/Game/ClothingWidget.cs:78-90）——它跑在**持有控件的那一端**。
        /// 在客户端上，它调的是客户端自己的 <c>ComponentSleep.Sleep(true)</c>，而主机的那个角色
        /// 根本没睡。下一份权威上报（sleeping=false，<see cref="ApplyLocalSleep"/> 会写回
        /// <c>m_sleepStartTime = null</c>）立刻把角色拉起来 —— 这就是"点击睡觉 → 躺下 → 秒起"。
        ///
        /// 所以客户端的 <c>IsSleeping</c> 只允许由主机确认的 <see cref="LocalSleepState.Sleeping"/>
        /// 驱动：本地一旦出现引擎自己放进来的睡眠，就把它收回（不留任何本地睡眠状态），
        /// 并把这一帧当作一次**入睡申请**边沿上报给模块（→ <c>PLAYER_SLEEP_REQUEST</c>）。
        /// 收回是**权威口径的落实**，不是兜底：客户端从不持有主机没授予的睡眠。
        /// </summary>
        public bool TryPollLocalSleepRequest(PlayerKey key, out PlayerSleepRequestViewState state,
            out bool requested, out string reason)
        {
            state = default;
            requested = false;
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 ComponentSleep（无法判定入睡）";
                return false;
            }
            if (!m_localSleep.TryGetValue(key.Slot, out LocalSleepState cached))
            {
                reason = "本机槽位 " + key.Slot + " 还没有收到主机的睡眠状态（不凭本地状态猜入睡）";
                return false;
            }
            if (cached.EntityToken != Token(player))
            {
                reason = "本机槽位 " + key.Slot + " 的角色实体已换代（旧睡眠状态作废）";
                return false;
            }
            try
            {
                state = new PlayerSleepRequestViewState(cached.Sleeping, cached.Dead);
                if (!sleep.IsSleeping) return true;
                if (cached.Sleeping) return true;   // 主机已确认：本地状态合法，不动它。
                bool allowManualWake = m_parentField.GetParentField<bool>(sleep,
                    "m_allowManualWakeUp", typeof(ComponentSleep));
                if (!allowManualWake)
                {
                    // Sleep(allowManualWakeup: false) 只可能来自主机（昏迷 / Sleep(false)），
                    // 客户端本地的这条路径已被摘掉 —— 出现就是异常，明确计数并收回。
                    m_sleepLocalForcedRejected++;
                    Problem("local-forced-sleep:" + key.Slot,
                        "本机槽位 " + key.Slot + " 出现了引擎本地的强制睡眠（allowManualWake=false）" +
                        "：客户端没有这条路径（ComponentVitalStats.Update 已摘掉）→ 已收回，" +
                        "睡眠只能由主机授予");
                    RevertLocalSleep(sleep);
                    return true;
                }
                // 引擎的睡眠按钮刚在本地把角色放倒，但主机还没授予这一次睡眠。
                requested = true;
                RevertLocalSleep(sleep);
                m_sleepLocalReverts++;
                return true;
            }
            catch (Exception e)
            {
                reason = "读取本机槽位 " + key.Slot + " 的入睡申请异常：" + e.GetType().Name +
                    " " + e.Message;
                Error("local-sleep-request:" + key.Slot, reason, e);
                return false;
            }
        }

        /// <summary>
        /// **无副作用**地判断本机槽位是否可用（模块每帧用它发现"本机槽位表"）。
        /// 与 <see cref="TryPollLocalSleepRequest"/> 的区别：**绝不收回本地睡眠、绝不吃掉
        /// 入睡申请的边沿** —— 用带边沿的读法做发现会把玩家的第一次点击吃掉（实测顾虑）。
        /// </summary>
        public bool TryDescribeLocalSleep(PlayerKey key, out string reason)
        {
            reason = null;
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            if (player.ComponentSleep == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 ComponentSleep（无法判定入睡）";
                return false;
            }
            if (!m_localSleep.TryGetValue(key.Slot, out LocalSleepState cached))
            {
                reason = "本机槽位 " + key.Slot + " 还没有收到主机的睡眠状态（不凭本地状态猜入睡）";
                return false;
            }
            if (cached.EntityToken != Token(player))
            {
                reason = "本机槽位 " + key.Slot + " 的角色实体已换代（旧睡眠状态作废）";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 收回主机未授予的本地睡眠（只清"睡眠本体"三个字段）：<c>m_sleepStartTime</c> 决定
        /// 躺下姿势（Source: ComponentHumanModel.cs:86-94），<c>m_minWetness</c> /
        /// <c>m_messageFactor</c> 是 <c>Sleep()</c> 一并重置的会话字段（ComponentSleep.cs:81-93）。
        /// <c>m_sleepFactor</c> **不在这里清**：它由 <see cref="RefreshSleepPresentation"/> 按
        /// 引擎速度淡出（1/s），直接归零会跳变。
        /// </summary>
        private void RevertLocalSleep(ComponentSleep sleep)
        {
            m_parentField.ModifyParentField(sleep, "m_sleepStartTime", null,
                typeof(ComponentSleep));
            m_parentField.ModifyParentField(sleep, "m_minWetness", float.MaxValue,
                typeof(ComponentSleep));
            m_parentField.ModifyParentField(sleep, "m_messageFactor", 0f, typeof(ComponentSleep));
        }

        /// <summary>确实发出入睡请求时清掉本帧输入（同一个点击不要再触发别的操作）。</summary>
        public bool TryConsumeLocalSleepRequestInput(PlayerKey key, out string reason)
        {
            // 与 TryConsumeLocalSleepInput 同一个动作（引擎 WakeUp 前也是 Input.Clear()）；
            // 分开命名只是让调用点读起来是"入睡请求"而不是"唤醒请求"。
            return TryConsumeLocalSleepInput(key, out reason);
        }

        /// <summary>
        /// 把主机拒绝入睡的原因显示在本机 HUD：与单机 <c>ClothingWidget.Update</c> 的
        /// <c>DisplaySmallMessage(reason, Color.White, blinking: false, playNotificationSound: false)</c>
        /// 逐参数一致（Source: Survivalcraft/Game/ClothingWidget.cs:82-86）。
        /// </summary>
        public bool TryShowLocalSleepRejected(PlayerKey key, string detail, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(detail))
            {
                reason = "主机拒绝了入睡请求但没有给出可显示的细节（不编造一句）";
                return false;
            }
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            ComponentGui gui = player.ComponentGui;
            if (gui == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 ComponentGui（无法显示入睡被拒原因）";
                return false;
            }
            try
            {
                gui.DisplaySmallMessage(detail, Color.White, blinking: false,
                    playNotificationSound: false);
                m_sleepRejectMessages++;
                return true;
            }
            catch (Exception e)
            {
                reason = "显示入睡被拒原因异常：" + e.GetType().Name + " " + e.Message;
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

        /// <summary>
        /// 主机：对主机侧那个角色调用**引擎** <c>ComponentSleep.Sleep(...)</c>
        /// （<c>ComponentSleep.cs:81-94</c>：写 <c>m_sleepStartTime = TotalElapsedGameTime</c>、
        /// <c>m_allowManualWakeUp</c>、<c>m_minWetness = MaxValue</c>、<c>m_messageFactor = 0</c>、
        /// <c>PlayerStats.TimesWentToSleep++</c> —— 这些统计与 SpawnPosition 都只在主机上写）。
        ///
        /// 只由校验通过的 <c>PLAYER_SLEEP_REQUEST</c> 调用：客户端的睡眠按钮跑在客户端进程里，
        /// 主机的这个角色必须由主机自己放倒，否则下一拍权威上报（sleeping=false）会把客户端
        /// 刚躺下的角色立刻拉起来（实测"秒起"）。
        ///
        /// 先跑引擎自己的 <c>CanSleep(out reason)</c>（干地 / 方块舒适度 / 不够困 / 太湿 / 要有
        /// 掩体，Source: ComponentSleep.cs:41-79），失败按 <c>can-sleep:&lt;原句&gt;</c> 明确拒绝。
        /// </summary>
        public bool TrySleepAuthorityPlayer(PlayerKey key, bool allowManualWake,
            out string reasonCode, out string detail, out string reason)
        {
            reasonCode = null;
            detail = null;
            reason = null;
            if (!TryResolveAuthorityPlayer(key, out PlayerData playerData,
                    out ComponentPlayer player, out reason))
            {
                reasonCode = PlayerSleepReasons.UnknownPlayer;
                return false;
            }
            ComponentSleep sleep = player.ComponentSleep;
            if (sleep == null)
            {
                reasonCode = PlayerSleepReasons.SleepFailed;
                reason = "玩家键 " + key + " 没有 ComponentSleep（无法调用引擎 Sleep）";
                return false;
            }
            bool dead = player.ComponentHealth != null && player.ComponentHealth.Health <= 0f;
            if (dead)
            {
                reasonCode = PlayerSleepReasons.Dead;
                reason = "玩家键 " + key + " 已经死亡（尸体不能入睡）";
                return false;
            }
            try
            {
                if (sleep.IsSleeping)
                {
                    // 已经在睡：不是失败路径（可能客户端重发了同一帧的点击）。
                    // 不改状态、不重复写统计，把"已经在睡"作为明确的拒绝码回给客户端。
                    reasonCode = PlayerSleepReasons.AlreadySleeping;
                    reason = "玩家键 " + key + " 已经在睡（sleep_start_time=" +
                        ReadSleepStartTime(sleep).ToString("0.##") + "）→ 不重复 Sleep()";
                    return false;
                }
                if (!sleep.CanSleep(out string canSleepReason))
                {
                    // 引擎原句（"You must be on dry land" / "You are too wet" ...）既进日志
                    // 也回给客户端显示 —— 与单机 ClothingWidget 的显示路径同一个字符串。
                    reasonCode = PlayerSleepReasons.CanSleep + ":" + canSleepReason;
                    detail = canSleepReason;
                    reason = "玩家键 " + key + " 不满足引擎 CanSleep：" + canSleepReason;
                    return false;
                }
                sleep.Sleep(allowManualWake);
                if (!sleep.IsSleeping)
                {
                    // 引擎 Sleep() 只在 !IsSleeping 时生效；上面已排除已睡，走到这里说明
                    // 组件状态与预期不符（例如另一次 WakeUp 插进来）→ 明确失败，不假装成功。
                    reasonCode = PlayerSleepReasons.SleepFailed;
                    reason = "对玩家键 " + key + " 调用 ComponentSleep.Sleep(" +
                        allowManualWake + ") 之后 IsSleeping 仍为 false";
                    m_sleepAuthoritySleepFailures++;
                    return false;
                }
                m_authoritySleeps++;
                return true;
            }
            catch (Exception e)
            {
                reasonCode = PlayerSleepReasons.SleepFailed;
                reason = "对玩家键 " + key + " 调用 ComponentSleep.Sleep() 抛异常：" +
                    e.GetType().Name + " " + e.Message;
                m_sleepAuthoritySleepFailures++;
                Error("authority-sleep:" + key, reason, e);
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

        // ================================================================ 客户端自我伤害（106 / 107）

        /// <summary>
        /// 客户端本地预测回滚守卫（<see cref="IUpdateable"/>，不挂在实体上）。
        ///
        /// 时序是这条链路的关键（引擎事实）：
        ///   ① <c>ScreensManager.Update</c> → 控件树 → <c>VitalStatsWidget.Update</c> 执行
        ///      <c>ComponentHealth.Injure(0.1f, null, ignoreInvulnerability: true, "Choked")</c>
        ///      （<c>Survivalcraft/Game/VitalStatsWidget.cs:176-179</c>）——**纯本地**；
        ///   ② 同一个 <c>GameScreen.Update</c> 才调用 <c>GameManager.UpdateProject()</c>；
        ///   ③ Project 更新里 <c>SubsystemUpdate</c> 按 UpdateOrder 排序；本守卫 =
        ///      <see cref="UpdateOrder.Reset"/>（−100），<c>SubsystemPlayers</c> = −20，
        ///      所以守卫在 <c>PlayerData.Update</c> 看到 <c>Health &lt;= 0</c> 之前把生命写回
        ///      最近一份主机权威值。客户端因此**不会**自己进入 PlayerDead。
        /// </summary>
        private sealed class SelfDamageGuard : IUpdateable
        {
            private readonly PlayerVitalsReplica m_owner;

            public SelfDamageGuard(PlayerVitalsReplica owner)
            {
                m_owner = owner;
            }

            public UpdateOrder UpdateOrder => UpdateOrder.Reset;

            public void Update(float dt) => m_owner.GuardLocalSelfDamage();
        }

        private void EnsureSelfDamageGuard()
        {
            Project project = GameManager.Project;
            if (project == null) return;
            if (m_selfDamageGuardProject == project && m_selfDamageGuard != null) return;
            RemoveSelfDamageGuard();
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (update == null)
            {
                m_selfDamageGuardFailures++;
                Problem("self-damage-guard-subsystem",
                    "找不到 SubsystemUpdate → 无法安装本地自我伤害预测回滚守卫" +
                    "（客户端可能在主机裁决前自己判死）");
                return;
            }
            if (m_selfDamageGuard == null) m_selfDamageGuard = new SelfDamageGuard(this);
            try
            {
                update.AddUpdateable(m_selfDamageGuard);
            }
            catch (Exception e)
            {
                m_selfDamageGuardFailures++;
                Error("self-damage-guard-install", "安装本地自我伤害预测回滚守卫失败", e);
                return;
            }
            m_selfDamageGuardProject = project;
            m_selfDamageGuardInstalls++;
        }

        private void RemoveSelfDamageGuard()
        {
            if (m_selfDamageGuardProject == null || m_selfDamageGuard == null) return;
            try
            {
                SubsystemUpdate update = m_selfDamageGuardProject
                    .FindSubsystem<SubsystemUpdate>(false);
                update?.RemoveUpdateable(m_selfDamageGuard);
                m_selfDamageGuardRemovals++;
            }
            catch (Exception e)
            {
                m_selfDamageGuardFailures++;
                Error("self-damage-guard-remove", "移除本地自我伤害预测回滚守卫失败", e);
            }
            m_selfDamageGuardProject = null;
        }

        /// <summary>
        /// 守卫本体：只在"角色信息界面打开 + 骷髅按钮这一帧被点 + 有主机权威生命"时动作。
        /// 把本地 <c>Health</c> / <c>m_lastHealth</c> / <c>HealthChange</c> / 死因 / DeathTime
        /// 写回最近一份主机权威值；若同一帧之前已经因为本地预测进入了 PlayerDead，再显式
        /// 把 PlayerData 拉回 Playing（主机明确说活着时，本地死亡就是假死亡）。
        /// **绝不**在 authority == 0 时把角色写活：那才是主机裁决的死亡。
        /// </summary>
        private void GuardLocalSelfDamage()
        {
            Project project = m_selfDamageGuardProject;
            if (project == null || GameManager.Project != project) return;
            m_selfDamageGuardFrames++;
            if (!m_spawner.TryCollectLocalPlayers(project, m_selfDamageLocalBuffer,
                    out bool tooMany, out string reason))
            {
                m_selfDamageGuardFailures++;
                Problem("self-damage-guard-players", "读取本机角色失败：" +
                    (reason ?? "端口没有给原因") + " → 本帧不回滚本地自我伤害预测");
                return;
            }
            if (tooMany)
            {
                Problem("self-damage-guard-too-many",
                    "本机角色数超过引擎上限（" + reason + "）→ 只检查前 " +
                    PlayerKey.SlotCount + " 个槽位");
            }
            for (int slot = 0; slot < m_selfDamageLocalBuffer.Count; slot++)
            {
                ComponentPlayer player = m_selfDamageLocalBuffer[slot]?.ComponentPlayer;
                if (player == null) continue;
                ComponentGui gui = player.ComponentGui;
                if (gui == null) continue;
                if (!(gui.ModalPanelWidget is VitalStatsWidget widget)) continue;
                ButtonWidget choke = widget.Children.Find<ButtonWidget>("ChokeButton", false);
                if (choke == null || !choke.IsClicked) continue;
                ComponentHealth health = player.ComponentHealth;
                if (health == null) continue;
                if (!m_localAuthorityHealth.TryGetValue(slot, out float authority))
                {
                    m_selfDamageGuardNoAuthority++;
                    Problem("self-damage-guard-no-authority:" + slot,
                        "本机槽位 " + slot + " 按了骷髅按钮，但还没有主机权威生命 → " +
                        "无法安全回滚本地预测（本次点击不发 106；等下一份 95 上报）");
                    continue;
                }
                try
                {
                    if (MathUtils.Abs(health.Health - authority) >
                        PlayerVitalsLimits.ValueEpsilon)
                    {
                        WriteField(health, "<Health>k__BackingField", authority,
                            typeof(ComponentHealth));
                        m_parentField.ModifyParentField(health, "m_lastHealth", authority,
                            typeof(ComponentHealth));
                        m_parentField.ModifyParentField(health, "<HealthChange>k__BackingField",
                            0f, typeof(ComponentHealth));
                        if (authority > 0f)
                        {
                            m_parentField.ModifyParentField(health,
                                "<CauseOfDeath>k__BackingField", string.Empty,
                                typeof(ComponentHealth));
                            m_parentField.ModifyParentField(health,
                                "<DeathTime>k__BackingField", null, typeof(ComponentHealth));
                        }
                        if (gui.HealthBarWidget != null)
                            gui.HealthBarWidget.Value = authority;
                        m_selfDamageLocalPredictionsCanceled++;
                        m_healthWrites++;
                    }
                    if (authority > 0f && player.PlayerData != null)
                    {
                        object deathTime = m_parentField.GetParentField(player.PlayerData,
                            "m_playerDeathTime", typeof(PlayerData));
                        if (deathTime != null)
                        {
                            m_selfDamageLocalDeathsRepaired++;
                            RestorePlayerDataToPlaying(slot, player);
                        }
                    }
                }
                catch (Exception e)
                {
                    m_selfDamageGuardFailures++;
                    Error("self-damage-guard:" + slot,
                        "回滚本机槽位 " + slot + " 的本地自我伤害预测失败", e);
                }
            }
        }

        // ---------------------------------------------------------------- 端口实现

        public bool TryPollLocalSelfDamageClick(PlayerKey key,
            out PlayerSelfDamagePressState state, out string reason)
        {
            state = default;
            reason = null;
            if (key.ClientId != 0)
            {
                reason = "自我伤害点击只读本机角色（clientId=0），收到玩家键 " + key;
                return false;
            }
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            EnsureSelfDamageGuard();
            ComponentHealth health = player.ComponentHealth;
            bool hasAuthority = m_localAuthorityHealth.TryGetValue(key.Slot,
                out float authority);
            bool dead = hasAuthority
                ? authority <= 0f
                : health != null && health.Health <= 0f;
            ComponentGui gui = player.ComponentGui;
            if (gui == null)
            {
                reason = "本机槽位 " + key.Slot + " 没有 ComponentGui（无法读骷髅按钮）";
                return false;
            }
            bool pressed = false;
            if (gui.ModalPanelWidget is VitalStatsWidget widget)
            {
                ButtonWidget choke = widget.Children.Find<ButtonWidget>("ChokeButton", false);
                if (choke == null)
                {
                    reason = "本机槽位 " + key.Slot +
                        " 的角色信息界面没有 ChokeButton（控件结构变化）";
                    return false;
                }
                pressed = choke.IsClicked;
            }
            state = new PlayerSelfDamagePressState(pressed, dead, hasAuthority, authority);
            return true;
        }

        public PlayerSelfDamageApplyStatus TryApplyAuthoritySelfDamage(PlayerKey key,
            float amount, string cause, out float healthAfter, out string reason)
        {
            healthAfter = 0f;
            reason = null;
            if (!key.IsValid)
            {
                reason = "玩家键 " + key + " 非法";
                return PlayerSelfDamageApplyStatus.Invalid;
            }
            if (key.ClientId <= 0)
            {
                // 主机本机角色走引擎原路径（VitalStatsWidget 直接 Injure），不经过 106。
                reason = "玩家键 " + key + " 不是客户端化身（自我伤害请求只裁决 clientId>0）";
                return PlayerSelfDamageApplyStatus.Invalid;
            }
            if (!PlayerSelfDamageRules.IsAcceptedAmount(amount, out reason))
                return PlayerSelfDamageApplyStatus.Invalid;
            if (!PlayerSelfDamageCauses.TryNormalize(cause, out string causeCode,
                    out string causeReason))
            {
                reason = causeReason;
                return PlayerSelfDamageApplyStatus.Invalid;
            }
            string engineCause = PlayerSelfDamageCauses.EngineDeathCause(causeCode);
            if (engineCause == null)
            {
                reason = "cause=" + causeCode + " 没有对应的引擎死因字符串";
                return PlayerSelfDamageApplyStatus.Invalid;
            }
            if (!TryResolveAvatar(key, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return PlayerSelfDamageApplyStatus.NoAvatar;
            ComponentHealth health = player.ComponentHealth;
            if (health == null)
            {
                reason = "玩家键 " + key + " 的化身缺少 ComponentHealth";
                return PlayerSelfDamageApplyStatus.NoAvatar;
            }
            if (health.Health <= 0f)
            {
                reason = "玩家键 " + key + " 已经死亡（Health=" + health.Health +
                    "）→ 尸体不能再受自我伤害";
                return PlayerSelfDamageApplyStatus.NotAlive;
            }
            float before = health.Health;
            try
            {
                // Source: Survivalcraft/Game/VitalStatsWidget.cs:176-179（与单机同一行）
                health.Injure(amount, null, ignoreInvulnerability: true, engineCause);
            }
            catch (Exception e)
            {
                m_selfDamageAuthorityFailures++;
                reason = "对主机侧角色 " + key + " 调用 ComponentHealth.Injure 抛异常：" +
                    e.GetType().Name + " " + e.Message;
                Error("self-damage-authority:" + key, reason, e);
                return PlayerSelfDamageApplyStatus.ApplyFailed;
            }
            healthAfter = health.Health;
            if (!(healthAfter < before - PlayerVitalsLimits.ValueEpsilon))
            {
                m_selfDamageAuthorityFailures++;
                reason = "主机侧角色 " + key + " 的 Health 没有变化（before=" + before +
                    " after=" + healthAfter + "）→ 不假装施加成功";
                return PlayerSelfDamageApplyStatus.ApplyFailed;
            }
            m_selfDamageAuthorityApplies++;
            return PlayerSelfDamageApplyStatus.Applied;
        }

        public bool TryApplyLocalSelfDamageResult(PlayerKey key,
            in PlayerSelfDamageResultData result, out bool playedSound, out bool redoutArmed,
            out string reason)
        {
            playedSound = false;
            redoutArmed = false;
            reason = null;
            if (!result.Accepted)
            {
                reason = "只应用 accepted 的 107 结果（拒绝走 TryRestoreLocalAuthorityHealth）";
                return false;
            }
            if (key.ClientId != 0 || result.Slot != key.Slot)
            {
                // 模块已校验 result.ClientId == 本端连接编号；这里的 key 是本机角色键
                // （clientId 固定 0），只按槽位对齐。
                reason = "107 结果槽位 " + result.Slot + " 与本机键 " + key + " 不一致";
                return false;
            }
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            ComponentHealth health = player.ComponentHealth;
            if (health == null)
            {
                reason = "本机槽位 " + key.Slot + " 缺少 ComponentHealth";
                return false;
            }
            try
            {
                float healthAfter = MathUtils.Saturate(result.HealthAfter);
                float before = health.Health;
                float loss = before - healthAfter;
                if (loss > PlayerVitalsLimits.ValueEpsilon)
                {
                    // 只在这个分支更新权威缓存：如果 95 上报已经先写入了**更高**的权威值
                    // （治疗 / 回血），不能拿这条更旧的 107 把缓存降回去。
                    m_localAuthorityHealth[key.Slot] = healthAfter;
                    WriteField(health, "<Health>k__BackingField", healthAfter,
                        typeof(ComponentHealth));
                    m_parentField.ModifyParentField(health, "<HealthChange>k__BackingField", 0f,
                        typeof(ComponentHealth));
                    if (healthAfter > 0f)
                    {
                        m_parentField.ModifyParentField(health, "m_lastHealth", healthAfter,
                            typeof(ComponentHealth));
                        m_parentField.ModifyParentField(health,
                            "<CauseOfDeath>k__BackingField", string.Empty,
                            typeof(ComponentHealth));
                        m_parentField.ModifyParentField(health, "<DeathTime>k__BackingField",
                            null, typeof(ComponentHealth));
                    }
                    else
                    {
                        // 主机裁决的 0 血：死因用结果回显的 cause（与单机 "Choked" 同一个字符串）。
                        string engineCause = PlayerSelfDamageCauses.EngineDeathCause(
                            result.Cause);
                        m_parentField.ModifyParentField(health,
                            "<CauseOfDeath>k__BackingField",
                            engineCause ?? PlayerSelfDamageCauses.ChokedEngineDeathCause,
                            typeof(ComponentHealth));
                    }
                    m_healthWrites++;
                    // 客户端 ComponentHealth.Update 已摘掉：受伤音效 / 红屏 / 血条闪烁必须由
                    // 权威结果驱动，否则就是"看得到掉血数字但没有受伤表现"。
                    ShowHurt(player, loss);
                    playedSound = true;
                    redoutArmed = true;
                    m_selfDamageFeedbackSounds++;
                    m_selfDamageRedoutArms++;
                    m_selfDamageRedout[key.Slot] = MathUtils.Max(
                        m_selfDamageRedout.TryGetValue(key.Slot, out float pending)
                            ? pending
                            : 0f,
                        MathUtils.Clamp(loss * 4f, 0f, 1f));
                }
                m_selfDamageLocalResults++;
                return true;
            }
            catch (Exception e)
            {
                m_selfDamageLocalResultFailures++;
                reason = "应用已接受的自我伤害结果失败（玩家键 " + key + "）：" +
                    e.GetType().Name + " " + e.Message;
                Error("self-damage-local-result:" + key, reason, e);
                return false;
            }
        }

        public bool TryRestoreLocalAuthorityHealth(PlayerKey key, out float health,
            out string reason)
        {
            health = 0f;
            reason = null;
            if (!m_localAuthorityHealth.TryGetValue(key.Slot, out health))
            {
                reason = "本机槽位 " + key.Slot +
                    " 还没有主机权威生命缓存（没有可回滚的目标值）";
                return false;
            }
            if (!TryResolveLocal(key.Slot, out PlayerData playerData, out ComponentPlayer player,
                    out reason))
                return false;
            ComponentHealth component = player.ComponentHealth;
            if (component == null)
            {
                reason = "本机槽位 " + key.Slot + " 缺少 ComponentHealth";
                return false;
            }
            try
            {
                if (MathUtils.Abs(component.Health - health) >
                    PlayerVitalsLimits.ValueEpsilon)
                {
                    WriteField(component, "<Health>k__BackingField", health,
                        typeof(ComponentHealth));
                    m_parentField.ModifyParentField(component, "<HealthChange>k__BackingField",
                        0f, typeof(ComponentHealth));
                    if (health > 0f)
                    {
                        m_parentField.ModifyParentField(component, "m_lastHealth", health,
                            typeof(ComponentHealth));
                        m_parentField.ModifyParentField(component,
                            "<CauseOfDeath>k__BackingField", string.Empty,
                            typeof(ComponentHealth));
                        m_parentField.ModifyParentField(component,
                            "<DeathTime>k__BackingField", null, typeof(ComponentHealth));
                    }
                    ComponentGui gui = player.ComponentGui;
                    if (gui?.HealthBarWidget != null) gui.HealthBarWidget.Value = health;
                    m_healthWrites++;
                    m_selfDamageRollbacks++;
                }
                if (health > 0f && player.PlayerData != null)
                {
                    object deathTime = m_parentField.GetParentField(player.PlayerData,
                        "m_playerDeathTime", typeof(PlayerData));
                    if (deathTime != null)
                    {
                        m_selfDamageLocalDeathsRepaired++;
                        RestorePlayerDataToPlaying(key.Slot, player);
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                m_selfDamageRollbackFailures++;
                reason = "回滚本机槽位 " + key.Slot + " 的权威生命失败：" +
                    e.GetType().Name + " " + e.Message;
                Error("self-damage-rollback:" + key, reason, e);
                return false;
            }
        }

        public int RefreshLocalSelfDamagePresentation(float deltaTime, out string reason)
        {
            reason = null;
            if (m_selfDamageRedout.Count == 0) return 0;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（自我伤害红屏无法刷新）";
                return 0;
            }
            if (!m_spawner.TryCollectLocalPlayers(project, m_selfDamageLocalBuffer,
                    out bool tooMany, out string localReason))
            {
                m_selfDamageRedoutFailures++;
                reason = "读取本机角色失败：" + (localReason ?? "端口没有给原因");
                return 0;
            }
            if (tooMany)
            {
                Problem("self-damage-redout-too-many",
                    "本机角色数超过引擎上限（" + localReason + "）→ 红屏只刷新前 " +
                    PlayerKey.SlotCount + " 个槽位");
            }
            float decay = MathUtils.Pow(0.2f, MathUtils.Max(deltaTime, 0f));
            int applied = 0;
            string firstFailure = null;
            m_selfDamageRedoutSlots.Clear();
            foreach (int slot in m_selfDamageRedout.Keys) m_selfDamageRedoutSlots.Add(slot);
            for (int i = 0; i < m_selfDamageRedoutSlots.Count; i++)
            {
                int slot = m_selfDamageRedoutSlots[i];
                float factor = m_selfDamageRedout[slot] * decay;
                if (factor <= 0.01f)
                {
                    m_selfDamageRedout.Remove(slot);
                    continue;
                }
                m_selfDamageRedout[slot] = factor;
                ComponentPlayer player = slot >= 0 && slot < m_selfDamageLocalBuffer.Count
                    ? m_selfDamageLocalBuffer[slot]?.ComponentPlayer
                    : null;
                if (player == null)
                {
                    m_selfDamageRedoutFailures++;
                    if (firstFailure == null)
                        firstFailure = "槽位 " + slot + " 还没有 ComponentPlayer";
                    continue;
                }
                ComponentScreenOverlays overlays = player.ComponentScreenOverlays;
                if (overlays == null)
                {
                    m_selfDamageRedoutFailures++;
                    if (firstFailure == null)
                        firstFailure = "槽位 " + slot + " 缺少 ComponentScreenOverlays";
                    continue;
                }
                overlays.RedoutFactor = MathUtils.Max(overlays.RedoutFactor, factor);
                applied++;
                m_selfDamageRedoutFrames++;
            }
            reason = firstFailure;
            return applied;
        }

        public void DetachSelfDamage()
        {
            RemoveSelfDamageGuard();
            m_selfDamageRedout.Clear();
            m_selfDamageRedoutSlots.Clear();
            m_localAuthorityHealth.Clear();
            m_selfDamageLocalBuffer.Clear();
        }

        public string DescribeSelfDamage()
        {
            return "selfDamageEngine{guardInstalls=" + m_selfDamageGuardInstalls +
                " guardRemovals=" + m_selfDamageGuardRemovals +
                " guardFrames=" + m_selfDamageGuardFrames +
                " localPredictionsCanceled=" + m_selfDamageLocalPredictionsCanceled +
                " localDeathsRepaired=" + m_selfDamageLocalDeathsRepaired +
                " noAuthority=" + m_selfDamageGuardNoAuthority +
                " guardFailures=" + m_selfDamageGuardFailures +
                " authorityApplied=" + m_selfDamageAuthorityApplies +
                " authorityFailures=" + m_selfDamageAuthorityFailures +
                " localResults=" + m_selfDamageLocalResults +
                " localResultFailures=" + m_selfDamageLocalResultFailures +
                " rollbacks=" + m_selfDamageRollbacks +
                " rollbackFailures=" + m_selfDamageRollbackFailures +
                " feedbackSounds=" + m_selfDamageFeedbackSounds +
                " redoutArms=" + m_selfDamageRedoutArms +
                " redoutFrames=" + m_selfDamageRedoutFrames +
                " redoutFailures=" + m_selfDamageRedoutFailures +
                " authorityHealthSlots=" + m_localAuthorityHealth.Count +
                " redoutPending=" + m_selfDamageRedout.Count + "}";
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
                " localRespawns=" + m_localRespawns + " engineDeaths=" +
                m_deathScreensShown + " terminalTaps=" + m_terminalDeathTaps +
                " deathTapLevels=" + m_deathTapLevelsIgnored +
                " playerDataRestores=" + m_localPlayerDataRestores +
                " rejectedUi=" + m_respawnRejectedMessages +
                " gateShown=" + m_gateShown + " gateHidden=" + m_gateHidden +
                " gateFail=" + m_gateFailures + " gateCover=" + m_gateCoverNeutralized +
                " gateCoverMiss=" + m_gateCoverMisses + " gateOpen=" +
                m_localDeathStates.Count + " deathEpochs=" + m_localDeathEpoch.Count +
                " engineRemove=" + m_engineRespawnExecuted + "/" + m_engineRespawnFailures +
                " replay=" + m_replayApplied + "/" + m_replayFailures +
                " replayWait=" + m_replayWaitFrames + " respawnInventoryDroppedByEngine=" +
                m_respawnInventoryDroppedByEngine +
                " staleDeathRejects=" + m_staleDeathRejects +
                " replicaWindowOpened=" + m_replicaWindowOpened +
                " replicaWindowSuppressed=" + m_replicaWindowDeadSuppressed +
                " replicaWindowClosed=" + m_replicaWindowClosed +
                " replicaWindowNewDeath=" + m_replicaWindowNewDeath +
                " replicaWindowCleanupFail=" + m_replicaWindowCleanupFailures +
                " replicaWindowActive=" + CountActiveRespawnWindows() +
                " hudWrites=" + m_hudWrites +
                " hudRefresh=" + m_hudPresentationRefreshes + " tempTex=" +
                m_hudTemperatureTextures + " ice=" + m_hudIceWrites + " hudFail=" +
                m_hudPresentationFailures + " hudNonFinite=" + m_hudNonFinite +
                " freezeHostValue=" + m_freezeHostValue.ToString("F3") +
                " freezeClientApplied=" + m_freezeClientApplied +
                " freezeInterpolatedFrames=" + m_freezeInterpolatedFrames +
                " freezeStageChanges=" + m_freezeStageChanges +
                " freezeLegacy=" + m_freezeLegacy + " freezeInvalid=" + m_freezeInvalid +
                " freezeCaptureFailures=" + m_freezeCaptureFailures +
                " freezeSeeds=" + m_freezeSeeds +
                " freezeAuthorityReads=" + m_freezeAuthorityReads +
                " freezeAuthorityMisses=" + m_freezeAuthorityMisses +
                " freezeAuthorityKeys=" + m_freezeHostProgress.Count +
                " iceFactorWriteFailures=" + m_iceFactorWriteFailures +
                " hurtFeedbackSound=" + m_hurtFeedbackSound +
                " hurtFeedbackRedout=" + m_hurtRedoutHits +
                " hurtFeedbackFrames=" + m_hurtRedoutFrames +
                " hurtFeedbackFailures=" + m_hurtRedoutFailures +
                " remoteHurtFlash=" + m_hurtFlashes +
                " fluEffectApplied=" + m_fluEffectApplied +
                " fluEffectSound=" + m_fluEffectSounds +
                " fluEffectParticles=" + m_fluEffectParticles +
                " fluEffectParticleFail=" + m_fluEffectParticleFailures +
                " fluEffectRejected=" + m_fluEffectRejected +
                " fluEffectDuplicate=" + m_fluEffectDuplicate +
                " fluEffectStale=" + m_fluEffectStale +
                " fluEffectSoundMismatch=" + m_fluEffectSoundMismatch +
                " fluBlackoutFrames=" + m_fluBlackoutFrames +
                " fluGreenoutFrames=" + m_fluGreenoutFrames +
                " fluEffectPolls=" + m_effectPolls +
                " fluEffectEmitted=" + m_effectEventsEmitted +
                " fluEffectPollFailures=" + m_effectPollFailures +
                " fluEffectPositionFailures=" + m_effectPositionFailures +
                " fluEffectSoundReadFailures=" + (m_effectSoundReadFailures +
                    m_effectSoundReadFailuresLocal) +
                " effectStates=" + m_localEffects.Count +
                " localSimOff=" + m_localSimDisabledNew + "/" + m_localSimDisableRuns +
                " localSimOn=" + m_localSimRestores + " avatarReads=" + m_avatarReads +
                " noAvatar=" + m_noAvatar + " applyFail=" + m_applyFailures +
                " avatarRespawns=" + m_avatarRespawns + "/" + m_avatarRespawnFailures +
                " respawnAnchorFail=" + m_respawnAnchorFailures +
                " suppressRuns=" + m_suppressRuns + " suppressedNew=" + m_suppressedNew +
                " suppressed=" + m_suppressedTokens.Count + " missing=" + m_suppressedMissing +
                " deathParticleFail=" + m_deathParticleFailures +
                " sleepStates=" + m_localSleep.Count + " sleepRuns=" +
                m_sleepPresentationRuns + " mirrorViews=" + m_mirrorSleep.Count +
                " sleepMirrorRuns=" + m_sleepMirrorRuns + " sleepMirrorFail=" +
                m_sleepMirrorFailures + " sleepMirrorClears=" + m_sleepMirrorClears +
                " sleepNoteFail=" + m_sleepNoteFailures + " authoritySleepReads=" +
                m_authoritySleepReads + " authorityWakes=" + m_authorityWakes +
                " authoritySleeps=" + m_authoritySleeps + " sleepAuthorityFail=" +
                m_sleepAuthoritySleepFailures + " localSleepReverts=" + m_sleepLocalReverts +
                " localSleepForced=" + m_sleepLocalForcedRejected + " sleepRejectMsg=" +
                m_sleepRejectMessages + " " + m_levelCounters.Describe() +
                " levelHalvedDeaths=" + m_hostLevelAuthority.ActiveDeaths + "} " +
                DescribeSelfDamage();
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
