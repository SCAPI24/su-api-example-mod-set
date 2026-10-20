using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>一次应用到某个角色上的结果（模块据此计数 / 决定是否丢掉该键的状态）。</summary>
    public enum PlayerVitalsApplyStatus
    {
        /// <summary>已写进引擎组件。</summary>
        Applied = 0,

        /// <summary>本机没有这个玩家键的角色（本地槽位不存在 / 联机化身还没建好或已经被移除）。</summary>
        NoAvatar = 1,

        /// <summary>值或引擎状态不允许应用（<c>reason</c> 写清原因）。</summary>
        Rejected = 2
    }

    /// <summary>
    /// 主机侧一个**由主机模拟**的角色的廉价采样（每帧轮询用）：
    ///   · <see cref="Key"/> = 玩家键：主机本机角色 = (0, slot)，客户端化身 = (clientId, slot)；
    ///   · <see cref="HasHealth"/> = 这个角色此刻有 ComponentPlayer + ComponentHealth 可读；
    ///   · <see cref="Dead"/> = <c>ComponentHealth.Health &lt;= 0</c>；
    ///   · <see cref="EntityToken"/> = ComponentPlayer 的对象标识（复活会换实体 → 换 token）；
    ///   · <see cref="Position"/> = <c>ComponentBody.Position</c>（只给复活落点用）；
    ///   · <see cref="CauseOfDeath"/> = 主机引擎写下的死因（死亡边沿事件用）。
    /// 每帧只读这几个字段，不做 satiation 字典拷贝（那是 5 Hz 的完整抓取才做的事）。
    /// </summary>
    public readonly struct PlayerVitalsSlotSample
    {
        public PlayerVitalsSlotSample(int slot, bool hasHealth, bool dead, int entityToken,
            Vector3 position)
            : this(new PlayerKey(0, slot), hasHealth, dead, entityToken, position, null)
        {
        }

        public PlayerVitalsSlotSample(PlayerKey key, bool hasHealth, bool dead, int entityToken,
            Vector3 position, string causeOfDeath)
        {
            Key = key;
            HasHealth = hasHealth;
            Dead = dead;
            EntityToken = entityToken;
            Position = position;
            CauseOfDeath = causeOfDeath ?? string.Empty;
        }

        public PlayerKey Key { get; }

        /// <summary>槽位（本机角色 0..3 / 该客户端自己的槽位 0..3）。</summary>
        public int Slot => Key.Slot;

        /// <summary>false = 这个角色此刻不可观测（没有 ComponentPlayer / ComponentHealth）。</summary>
        public bool HasHealth { get; }

        public bool Dead { get; }
        public int EntityToken { get; }

        /// <summary>不是有限值时 = 这个角色的位置读不到（调用方不得拿它当落点）。</summary>
        public Vector3 Position { get; }

        public string CauseOfDeath { get; }

        public bool HasFinitePosition =>
            PlayerVitalsSnapshot.IsFiniteValue(Position.X) &&
            PlayerVitalsSnapshot.IsFiniteValue(Position.Y) &&
            PlayerVitalsSnapshot.IsFiniteValue(Position.Z);
    }

    /// <summary>
    /// 玩家生命 / 体征 / 死亡状态的**引擎侧端口**（实现 = Engine/PlayerVitalsReplica.cs，
    /// 组合根注入）。模块层（Modules/）不引用 Game，因此方向裁决 / 发送节奏 / 陈旧拒绝 /
    /// 计数全部可以用 P:\tmp\tsverify 直接编译同一份源码（假实现本接口）。
    ///
    /// **权威口径（2026-10 产品要求，不可回退）**：
    ///   · **主机是唯一权威**：主机自己模拟**每一个**角色的生命 / 体征 / 睡眠 ——
    ///     主机本机角色 + 主机侧每个客户端化身；<see cref="CaptureAuthorityVitals"/> /
    ///     <see cref="PollAuthoritySlots"/> 只读这些主机模拟的组件；
    ///   · 客户端**绝不**上报自己的状态；客户端本机角色的
    ///     ComponentHealth / ComponentVitalStats / ComponentSleep / ComponentFlu /
    ///     ComponentSickness / ComponentOnFire 的 Update 被摘掉（<see cref="DisableLocalSimulation"/>），
    ///     只接收主机下发的值（<see cref="ApplyLocalVitals"/> / <see cref="ApplyLocalVitalEvent"/>）；
    ///   · 客户端上的远端化身（主机角色 / 其它客户端角色）同样不跑生命 / 体征模拟
    ///     （<see cref="SuppressRemoteAvatars"/>），只应用主机下发的值
    ///     （<see cref="ApplyRemoteVitals"/> / <see cref="ApplyVitalEvent"/>）；
    ///   · <see cref="CaptureAuthorityVitals"/> 读到的客户端化身状态也是 H3 调试镜像
    ///     （Engine/MirrorCharacterEngine.cs）的来源：主机绝不能再抑制 / 对账回滚这些化身，
    ///     否则 H3 读到的是假状态。
    ///
    /// **死亡 / 复活（2026-10 产品决定：走引擎原生流程，方案 A）**：
    ///   · 死亡 = 主机 Death 事件驱动客户端写 <c>CauseOfDeath</c> + <c>Health = 0</c>，
    ///     之后**引擎自己的** <c>PlayerData</c> 状态机负责死亡相机 / "YOU HAVE DIED" /
    ///     等级减半 / 点击后删实体 / PrepareSpawn / SpawnDialog / WaitForTerrain / SpawnPlayer
    ///     （<c>PlayerData.cs:257-321</c>）；
    ///   · mod 只做三件引擎做不到的事：①挡住引擎"未获批准就删实体"的点击分支
    ///     （<see cref="TryPollLocalDeathTap"/> + 自己挂的挡门对话框）；②在主机批准后执行
    ///     那一行 <c>RemoveEntity</c>（<see cref="TryExecuteLocalRespawn"/>）；③新实体出生后
    ///     用主机权威数据整体重放 + 用死亡 epoch 丢弃陈旧状态
    ///     （<see cref="TryReplayLocalRespawn"/>）；
    ///   · Cruel / Adventure 禁复活的本地引擎分支（<c>PlayerData.cs:308-315</c>）由端口截住，
    ///     绝不静默照搬（见 <see cref="TryHandleTerminalDeathTap"/>）。
    ///
    /// 所有失败（没有 Project / 没有角色 / 值非法）都通过返回值 + <c>reason</c> 明确报告。
    /// </summary>
    public interface IPlayerVitalsPort
    {
        /// <summary>
        /// **主机侧**：抓取主机模拟的**全部**角色的完整生命 / 体征状态：
        ///   · 主机本机角色 = <c>PlayerKey(0, slot)</c>（PlayerIndex 升序 = 槽位 0..3）；
        ///   · 主机侧客户端化身 = <c>PlayerKey(clientId, slot)</c>（NetworkAvatarSpawner 化身表）。
        /// 返回写进 <paramref name="target"/> 的条数；某个角色没有 ComponentPlayer /
        /// ComponentHealth / ComponentVitalStats 就跳过并写清原因（限流日志由调用方做），
        /// 绝不拿别的角色的值顶替。
        /// </summary>
        int CaptureAuthorityVitals(List<PlayerVitalsSnapshot> target, out string reason);

        /// <summary>
        /// **主机侧**：每帧廉价轮询主机模拟的每个角色的死亡 / 实体换代 / 位置 / 死因
        /// （死亡→复活边沿检测 + 请求冷却用的死亡时刻）。主机**不再**据此排自动复活。
        /// 返回写进 <paramref name="target"/> 的条数；没有角色时返回 0 并写明原因。
        /// </summary>
        int PollAuthoritySlots(List<PlayerVitalsSlotSample> target, out string reason);

        /// <summary>
        /// 读某个**联机化身**（客户端角色 / 主机角色在客户端的副本）当前的生命 / 体征。
        /// H3 调试镜像 / 诊断用；false = 没有这个化身（<paramref name="reason"/> 明确）。
        /// </summary>
        bool TryGetAvatarVitals(PlayerKey key, out PlayerVitalsSnapshot snapshot, out string reason);

        /// <summary>
        /// **客户端侧**：把主机下发的权威状态写进**本机玩家**（按 <c>key.Slot</c> 取本机槽位；
        /// key 的 clientId 由模块判定，端口只认槽位）。
        ///   · 只做字段赋值 + HUD / 受伤表现，**绝不**调用 <c>Injure</c> / <c>Heal</c>；
        ///   · <c>Dead = true</c> 时按主机权威写 <c>CauseOfDeath</c> 并把 <c>Health</c> 写成 0
        ///     （**不写** <c>m_lastHealth</c>：引擎要看到"掉到 0"的边沿才播原生死亡表现），
        ///     引擎 <c>PlayerData.Playing</c> 因此进入原生 <c>PlayerDead</c>
        ///     （<c>PlayerData.cs:270/275-321</c>）；
        ///     revision 不新于本槽位死亡 epoch 的死亡状态 = 陈旧，直接拒绝（不写 0）。
        /// </summary>
        PlayerVitalsApplyStatus ApplyLocalVitals(PlayerKey key,
            in PlayerVitalsSnapshot snapshot, out string reason);

        /// <summary>
        /// **客户端侧**：把主机下发的死亡 / 复活边沿应用到本机玩家：
        ///   · 死亡 = 写 <c>CauseOfDeath</c> + <c>Health = 0</c>（引擎原生死亡流程由此启动）
        ///     并按模式挂上"挡门对话框"（TapToRespawn：挡住引擎自动删实体，等主机批准；
        ///     Cruel：不挡，让引擎原生打开 GameMenuDialog；Adventure 禁复活：挡住引擎的
        ///     本地重启冒险）。**不删实体、不掉物品、不本地复活**；
        ///   · 复活 = 只推进死亡 epoch 并记录主机落点；真正的落点 / 数值重放在
        ///     <see cref="TryReplayLocalRespawn"/> 里对新实体执行。
        /// </summary>
        PlayerVitalsApplyStatus ApplyLocalVitalEvent(in PlayerVitalEventSnapshot evt,
            out string reason);

        /// <summary>
        /// **客户端侧 / 每帧**：把主机权威体征派生出的 **HUD 显示属性**刷新到本机角色：
        /// 温度条 <c>BarSubtexture</c>（按目标体温选 Temperature0..6）、冰冻覆盖
        /// <c>ComponentScreenOverlays.IceFactor</c>、血条 / 食物条的值。
        ///
        /// 为什么必须每帧：<c>ComponentVitalStats.Update</c>（含 UpdateTemperature）已被
        /// <see cref="DisableLocalSimulation"/> 摘掉，而 <c>ComponentScreenOverlays.Update</c>
        /// 每帧把 <c>IceFactor</c> 清 0（更新序 Reset，早于 Default）。温度条贴图只在档位变化时
        /// 写（内部去重）。只写**显示属性**，绝不写组件字段、绝不跑模拟。
        /// 返回刷新到的本机角色数；<paramref name="reason"/> 非空 = 有槽位失败（原因可直接写日志）。
        /// </summary>
        int RefreshLocalPresentation(out string reason);

        /// <summary>
        /// **客户端侧**：把主机下发的权威状态写进**远端化身**（不跑任何伤害 / 治疗模拟）。
        /// 死亡（<c>Dead</c>）走显式死亡视觉路径（<c>DeathTime</c> + 死亡粒子）；
        /// 活着（且之前是死的）会清掉死亡状态并把 <c>DeathPhase</c> 归零（复活姿势）。
        /// </summary>
        PlayerVitalsApplyStatus ApplyRemoteVitals(PlayerKey key,
            in PlayerVitalsSnapshot snapshot, out string reason);

        /// <summary>
        /// **客户端侧**：把主机下发的死亡 / 复活边沿应用到远端化身。复活会按事件的落点
        /// 重置位置 / 速度 / 生命 / 体征 / 病程 / 火焰 / 睡眠（照旧实现的
        /// ResetNetworkPlayerAfterRespawn）。
        /// </summary>
        PlayerVitalsApplyStatus ApplyVitalEvent(in PlayerVitalEventSnapshot evt, out string reason);

        /// <summary>
        /// **客户端侧**：把本机玩家（槽位 0..3）的 ComponentHealth / ComponentVitalStats /
        /// ComponentSleep / ComponentFlu / ComponentSickness / ComponentOnFire 从
        /// <c>SubsystemUpdate</c> 摘掉，并把 <c>IsInvulnerable</c> 置 true（挡住其它
        /// 本机系统直接 <c>Injure</c>）。重复调用幂等；角色实体换代（复活）会重新摘。
        /// 返回本次新摘掉的角色数；<paramref name="totalDisabled"/> = 当前被摘掉的角色总数。
        /// </summary>
        int DisableLocalSimulation(out int totalDisabled, out string reason);

        /// <summary>
        /// **客户端侧**：恢复本机玩家的生命 / 体征 / 睡眠 / 病程 / 火焰模拟（离开房间 /
        /// 断开主机后必须恢复，否则本机角色永远不再模拟）。返回是否全部恢复。
        /// </summary>
        bool EnableLocalSimulation(out string reason);

        /// <summary>
        /// **客户端侧**：确保**每个**远端化身的生命 / 体征 / 睡眠模拟都不在本端运行
        /// （摘 updateable + IsInvulnerable）。返回本次新抑制的化身数；
        /// <paramref name="suppressedTotal"/> = 当前被抑制的化身总数。
        /// 重复调用是幂等的（新出现的化身会被补上）。主机侧**不得**调用（主机要自己模拟它们）。
        /// </summary>
        int SuppressRemoteAvatars(out int suppressedTotal, out string reason);

        /// <summary>
        /// **主机侧 / 客户端侧**：读当前世界的复活规则（引擎 PlayerData.PlayerDead 同一口径）：
        /// Cruel → 不能复活；Adventure 且 <c>IsAdventureRespawnAllowed == false</c> → 重启冒险；
        /// 其它 → 点击复活。读不到世界 / 模式时返回 <see cref="PlayerRespawnMode.Unknown"/> + 原因
        /// （调用方不得猜一个模式）。
        /// </summary>
        PlayerRespawnMode GetRespawnMode(out string reason);

        /// <summary>
        /// **主机侧**：把一个死掉的客户端化身按**它自己的出生点**复活（位置 / 速度 / 生命 /
        /// 体征 / 病程 / 火焰 / 睡眠全部复位），返回主机选定的落点（复活事件用）。
        /// 只由 owner 的 PLAYER_RESPAWN_REQUEST 触发（主机不再有自动复活计时器）。
        /// 没有落点 / 没有 Project / 没有化身都明确失败（不拿 Zero 顶替）。
        /// </summary>
        bool TryRespawnAvatar(PlayerKey key, out Vector3 position, out string reason);

        /// <summary>
        /// **客户端侧 / 每帧**：死亡屏上是否出现了**一次新的按下**（按下沿）。
        /// 对话框打开时不判定（与引擎 <c>PlayerData.PlayerDead</c> 的
        /// <c>!DialogsManager.HasDialogs(GuiWidget)</c> 同口径）；但**不能用**
        /// <c>GameWidget.Input.Any</c> 的电平代替按下沿 —— <c>Any</c> 在键盘自动重复时每帧都会重新为真
        /// （<c>WidgetInput.cs:704</c> + <c>Keyboard.cs:170-176</c>），按住方向键会被当成"点击"→
        /// 1.5 s 门槛一过就自动复活。生产实现见
        /// <c>Engine/PlayerVitalsReplica.cs:TryPollLocalDeathTap</c>（口径
        /// <see cref="SuMultiplayer.Net.PlayerDeathInputSample.IsFreshPress"/>）。
        /// 这里只读输入，不发请求、不改状态、不清输入。
        /// </summary>
        bool TryPollLocalDeathTap(PlayerKey key, out bool tapped, out string reason);

        /// <summary>
        /// **客户端侧 / 不可复活模式**：引擎等价的终止路径 ——
        ///   · Cruel：**不再需要本方法**（挡门对话框不挂，引擎自己的
        ///     <c>PlayerData.cs:308-311</c> 会在点击后打开 <c>GameMenuDialog</c>）；调用只会
        ///     明确失败并说明"该模式已交给引擎"；
        ///   · Adventure 禁复活：引擎会 <c>ScreensManager.SwitchScreen("GameLoading", ...,
        ///     "AdventureRestart")</c>；联机客户端不能重启主机世界，所以挡门对话框继续挂着，
        ///     这里打开 <c>GameMenuDialog</c> 并写清原因（返回 true，reason 里给出说明）。
        /// 不发送复活请求。
        /// </summary>
        bool TryHandleTerminalDeathTap(PlayerKey key, PlayerRespawnMode mode, out string reason);

        /// <summary>
        /// **客户端侧**：把主机拒绝复活的原因显示在本机死亡屏上（小消息），不改变请求状态。
        /// </summary>
        bool TryShowLocalRespawnRejected(PlayerKey key, string reasonCode, out string reason);

        /// <summary>
        /// **客户端侧**：主机**批准**复活之后，由 mod 自己把引擎的死亡流程推进到下一步 ——
        ///   ① 关掉挡门对话框（<c>DialogsManager.HideDialog</c>）；
        ///   ② 执行引擎 <c>PlayerData.PlayerDead</c> 点击分支那一行：
        ///      <c>m_project.RemoveEntity(ComponentPlayer.Entity, disposeEntity: true)</c>
        ///      （<c>Survivalcraft/Game/PlayerData.cs:318</c>）。
        /// 之后引擎自己的
        /// <c>PrepareSpawn → SpawnDialog → WaitForTerrain → SpawnPlayer → FppCamera</c>
        /// 全流程自动跑完（<c>PlayerData.cs:163-256 / 404-421</c>）。
        ///
        /// 前置条件必须真实成立，否则明确失败（不猜、不强推状态机）：
        ///   · 本机槽位有 ComponentPlayer；
        ///   · 引擎 PlayerData 真的在 PlayerDead（<c>m_playerDeathTime</c> 有值，
        ///     说明我们已经写过 Health = 0 且引擎自己判了死）。
        /// 重复调用幂等：已经删过（没有 ComponentPlayer）= 返回 true + 说明。
        /// </summary>
        bool TryExecuteLocalRespawn(PlayerKey key, out string reason);

        /// <summary>
        /// **客户端侧 / 每帧**：新实体出生后的**权威重放**（方案 A 的第三个闸门）。
        ///
        /// 引擎 <c>SpawnPlayer</c> 建的是**全新实体**（模板血量 1、空背包、空衣服，
        /// <c>PlayerData.cs:568-640</c> + <c>Database.xml</c> 的 Player 模板），落点还会被
        /// <c>FindNoIntroSpawnPosition</c> 偏移最多 8 格（<c>PlayerData.cs:454-477</c>）。
        /// 所以出生后必须用主机权威数据覆盖：
        ///   · 落点 = <paramref name="request"/> 里的主机落点（同时写进
        ///     <c>PlayerData.SpawnPosition</c>，作为以后复活的锚点）；
        ///   · 背包 / 手持槽 / 工作台配方格 / 衣服 = **死亡那一刻冻结的权威副本**；
        ///   · 重新摘掉新实体的生命 / 体征 / 睡眠 / 病程 / 火焰模拟 + <c>IsInvulnerable</c>；
        ///   · 重新初始化 HUD（新 GUI 的控件是新的）。
        /// 五项体征 / 死亡 epoch 由模块在拿到 <see cref="PlayerRespawnReplayStatus.Applied"/>
        /// 之后用最近一条主机快照补写（模块持有快照，端口不缓存）。
        /// 返回 <see cref="PlayerRespawnReplayStatus"/>；<paramref name="reason"/> 写清失败原因。
        /// 幂等：同一个实体只重放一次（重复调用返回 <c>NoPending</c>）。
        /// </summary>
        PlayerRespawnReplayStatus TryReplayLocalRespawn(PlayerKey key,
            in PlayerRespawnReplayRequest request, out string reason);

        /// <summary>
        /// **客户端侧**：把"已向主机请求复活、正在等裁决"显示在本机死亡屏上（小消息）。
        /// 只做表现，不改请求状态。
        /// </summary>
        bool TryShowLocalRespawnPending(PlayerKey key, out string reason);

        /// <summary>状态行用：引擎侧动作计数（应用 / 死亡视觉 / 抑制 / 本地模拟闸门），不改变行为。</summary>
        string Describe();
    }
}
