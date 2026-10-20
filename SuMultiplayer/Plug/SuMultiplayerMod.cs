using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;
using SuMultiplayer.Creatures;
using SuMultiplayer.Engine;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.UI;
using SuMultiplayer.Voice;

namespace SuMultiplayer
{
    /// <summary>
    /// 组合根：**只做装配**，不含业务。
    ///   ① 读参数（data:/Mods/SuMultiplayer.json；里面没有 host/client 模式）
    ///   ② 注册引擎注入点（Game.SubsystemTerrain → SuMultiplayer.Engine.SuSubsystemTerrain）
    ///   ③ 建 ModuleHost，注册模块与端口
    ///   ④ 替换 Play 屏（加入房间入口）+ 每帧挂 CR 按钮（建房入口）
    ///   ⑤ 订阅 Frame.Update 驱动 tick；订阅 GameDatabase.GameDatabase 触发注入
    ///
    /// 联机的两个入口都是**玩家操作**，不是启动配置：
    ///   · 建房 = 游戏内 CR 按钮（正在玩的世界）；
    ///   · 加入 = Play 屏世界列表里的房间行。
    ///
    /// 每个模块都可以单独下线而不影响别的模块（宿主负责解析/熔断/隔离）。
    /// </summary>
    public sealed class SuMultiplayerMod : IMod
    {
        public const string ModIdentifier = "SuMultiplayer";
        public const string ModVersionText = "0.1.0";

        private ModuleHost m_moduleHost;
        private TerrainSyncModule m_terrainModule;
        private SessionModule m_sessionModule;
        private WorldTransferModule m_transferModule;
        private PlayerAvatarModule m_avatarModule;
        /// <summary>联机角色引擎适配器（统计模块要按 clientId 找本机 / 联机角色，UI 要定位本机玩家）。</summary>
        private NetworkAvatarSpawner m_avatarSpawner;
        /// <summary>统计 / 成就的引擎侧端口（本机 PlayerStats 读写 + 角色记录落盘）。</summary>
        private PlayerStatsStore m_statsPort;
        /// <summary>统计 / 成就同步模块（主机权威 / 客户端只展示）。</summary>
        private PlayerStatsModule m_statsModule;
        /// <summary>玩家生命 / 体征 / 死亡状态：引擎侧只写远端化身的字段 + 抑制本端对它的模拟。</summary>
        private PlayerVitalsReplica m_vitalsEngine;
        /// <summary>玩家生命 / 体征 / 死亡状态同步模块（谁跑模拟谁上报，另一端只应用）。</summary>
        private PlayerVitalsModule m_vitalsModule;
        /// <summary>自定义皮肤资源（主机权威 + 客户端会话副本缓存 / 贴图应用）。</summary>
        private PlayerSkinStore m_skinStore;
        /// <summary>自定义皮肤协议模块（上传 / 校验 / 广播 / 计数）。</summary>
        private PlayerSkinModule m_skinModule;
        private SkinAssetCounters m_skinCounters;
        /// <summary>
        /// 引擎 Edit Players（PlayerScreen）进入 / 退出钩子：进入抓快照、退出比较并同步名字/皮肤。
        /// 只在屏幕引用变化时动作，不轮询玩家资料。
        /// </summary>
        private PlayerEditWatcher m_editWatcher;
        /// <summary>角色动作 / 手持物 / 背包复制（主机权威）：引擎适配 + 角色记录落盘 + 模块。</summary>
        private PlayerActionModule m_actionModule;
        private AvatarActionBridge m_actionBridge;
        private AvatarInventoryStore m_inventoryStore;
        /// <summary>爆炸同步：引擎适配（主机检测 / 客户端表现 / 请求执行）+ 网络模块（新文件，不碰别人的模块）。</summary>
        private ExplosionReplicaEngine m_explosionEngine;
        private ExplosionSyncModule m_explosionModule;
        /// <summary>烧尽表现同步：主机 = 引擎 5 秒烧尽批次后广播；客户端 = 只放碎屑 / 嘶嘶声。</summary>
        private BurnAwayEffectReplicaEngine m_burnAwayEngine;
        private EffectsSyncModule m_burnAwayModule;
        /// <summary>拾取物 / 投射物复制：引擎适配（主机抓取/执行拾取、客户端副本）+ 网络模块。</summary>
        private PickableReplicaEngine m_entityEngine;
        private EntityReplicationModule m_entityModule;
        /// <summary>方块使用 / 容器同步：引擎适配（客户端输入拦截 + 副本面板 / 主机权威库存）+ 网络模块。</summary>
        private ContainerReplicaEngine m_blockUseEngine;
        private BlockUseSyncModule m_blockUseModule;
        /// <summary>家具设计 / 内容同步：引擎适配（主机权威设计表 + 客户端副本表）+ 网络模块。</summary>
        private FurnitureReplicaEngine m_furnitureEngine;
        private FurnitureSyncModule m_furnitureModule;
        /// <summary>
        /// 可编辑方块内容（真值表 / 内存条）：引擎适配层（客户端对话框提交 → 请求；
        /// 主机校验 + 落地权威内容 + 广播）+ 网络模块。组合根把适配层挂到
        /// TerrainBridge.EditableBlocks，两个注入外壳从 SubsystemTerrain 拿它。
        /// </summary>
        private EditableBlockBridge m_editableBridge;
        private EditableSyncModule m_editableModule;
        /// <summary>远程动作声音中继：主机 = 远端请求执行窗口内捕获音频队列；客户端 = 只播放。</summary>
        private RemoteActionAudioReplica m_remoteAudioEngine;
        private RemoteAudioSyncModule m_remoteAudioModule;
        /// <summary>
        /// 循环环境音（Audio/Fire / Water / Magma）：主机按每个客户端化身位置算音量，
        /// 客户端施加到本机 SubsystemAmbientSounds（引擎仍拥有循环 / 淡入淡出 / Sizzles）。
        /// </summary>
        private AmbientSoundReplicaEngine m_ambientSoundEngine;
        private AmbientSoundSyncModule m_ambientSoundModule;
        /// <summary>水花表现：主机 = 拾取物 / 投射物子系统 Update 窗口内捕获粒子；客户端 = 重建同一粒子 + 声音。</summary>
        private WaterSplashReplicaEngine m_waterSplashEngine;
        private WaterSplashSyncModule m_waterSplashModule;
        /// <summary>活塞运动表现：主机 = 每次运动开始 / 结束广播；客户端 = 建本地只表现集合（不写格）。</summary>
        private PistonReplicaEngine m_pistonEngine;
        private PistonSyncModule m_pistonModule;
        private CreatureSpawnModule m_creatureModule;
        private CreatureWorldAdapter m_creatureWorld;
        /// <summary>
        /// 近战攻击：引擎适配（客户端输入拦截 / 主机裁决 + 一次引擎 Hit / 预测表现与回滚）。
        /// 模块层只做协议 / 计数 / 广播；距离规则在 Net/MeleeNet.cs 的纯逻辑里。
        /// </summary>
        private MeleeReplicaEngine m_meleeEngine;
        private MeleeSyncModule m_meleeModule;
        /// <summary>客户端副本 / 主机权威（编辑审批 + 世界信息 + RNG 状态）；组合根装配，模块只拿端口。</summary>
        private ClientReplicaEngine m_clientAuthority;
        private ClientAuthorityModule m_authorityModule;
        /// <summary>客户端引信表现（只读主机下发的火药桶格子 data；主机路径不会进入）。</summary>
        private FuseReplicaPresentation m_fusePresenter;
        /// <summary>客户端营火表现（只维护 FireParticleSystem；主机路径只做烧尽检测）。</summary>
        private CampfireReplicaPresentation m_campfirePresenter;
        /// <summary>客户端熔炉火焰表现（只维护 FireParticleSystem；由 FURNACE_STATE 与格子通知驱动）。</summary>
        private FurnaceReplicaPresentation m_furnacePresenter;
        /// <summary>营火熄灭火声：主机 = 烧尽后广播；客户端 = 只播 Audio/Sizzles。</summary>
        private CampfireEffectReplicaEngine m_campfireEngine;
        private CampfireSyncModule m_campfireModule;
        private RoomUiAttacher m_uiAttacher;
        /// <summary>多人聊天（主机权威 / 瞬态）：协议模块 + 底部 HUD（消息流 / 输入 / 触摸按钮）。</summary>
        private ChatModule m_chatModule;
        private ChatOverlayAttacher m_chatOverlay;
        /// <summary>实时语音：OpenAL 采集/播放（P/Invoke）+ 纯逻辑模块 + 游戏内 HUD。</summary>
        private VoiceAudioEngine m_voiceAudio;
        private VoiceChatModule m_voiceModule;
        private VoiceHud m_voiceHud;
        /// <summary>客户端 UI 拖出（inventory drag-to-world）拦截：替换 GameWidget 的 DragHost。</summary>
        private UiDropAttacher m_uiDropAttacher;
        /// <summary>
        /// 调试镜像（debug-only 主机端分屏镜像）：纯逻辑控制器（配置闸门 / 目标 / 生命周期 /
        /// 计数）+ 引擎适配器（临时PlayerData + 第 3 个 GameWidget）+ 小 HUD 按钮。
        /// 配置 debugmirrorclient 默认关闭；关闭时这三者都不产生任何引擎对象。
        /// </summary>
        private DebugMirrorController m_debugMirror;
        private DebugMirrorClient m_debugMirrorClient;
        private DebugMirrorHud m_debugMirrorHud;
        /// <summary>
        /// 调试镜像角色（debug-only，默认关闭）：主机本地角色（名字由 mirrorcharacter 配置，产品口径 = H3）
        /// 逐拍复制指定客户端角色的位置/状态/背包/动作。纯逻辑 = Modules/DebugMirrorCharacterPorts.cs，
        /// 引擎动作 = Engine/MirrorCharacterEngine.cs；配置关闭时控制器一帧都不会调用引擎。
        /// </summary>
        private MirrorCharacterController m_mirrorCharacter;
        private MirrorCharacterEngine m_mirrorCharacterEngine;
        /// <summary>
        /// 自动客户端分屏（**默认开启**，配置键 clientview / clientviewclient）：主机在有空槽时
        /// 为每个加入的客户端建一个 camera-only 第一人称分屏（与客户端自己看到的画面一致），
        /// 客户端离开 / 离开房间 / 世界卸载 / 配置关闭时自动拆除。
        /// 纯逻辑 = Modules/ClientViewPorts.cs，引擎动作 = Engine/ClientViewEngine.cs；
        /// 与 H3 replace 模式的优先级：显式 mirrorcharacter 优先（H3 绑定同一客户端时不建自动视图）。
        /// </summary>
        private ClientViewController m_clientView;
        /// <summary>
        /// 镜像宿主（**调试功能，默认开启**；配置键 mirrorcharacterauto / mirrorhostnameprefix）：
        /// 客户端加入时主机为它自动创建**真实本地角色**（引擎第 2/3/4 个分屏，带引擎自带的触控控件 /
        /// HUD / 界面镜像 / 睡眠与受击表现），并把该分屏相机绑到客户端第一人称（replace）。
        /// 池 = 4 − 主机实际本地角色数（PlayersData.Count）；池满明确拒绝，绝不顶掉已有角色。
        /// 纯逻辑 = Modules/MirrorHostPorts.cs，引擎动作 = Engine/MirrorHostEngine.cs；
        /// auto 标记注册表 = MirrorHostEngine.RegistrySubsystem（注入 Game.SubsystemPlayers）。
        /// </summary>
        private MirrorHostController m_mirrorHost;
        private MirrorHostEngine m_mirrorHostEngine;
        /// <summary>镜像宿主当前观察的 Project（换世界时补做一次"清理上一次会话留下的 auto 角色"）。</summary>
        private Project m_mirrorHostProject;
        /// <summary>本会话是否已经在当前世界里补做过清理（每个 Project 只做一次）。</summary>
        private bool m_mirrorHostReconciled;
        private ClientViewEngine m_clientViewEngine;
        /// <summary>
        /// 客户端界面层镜像（CLIENT_UI_STATE，id 100）：客户端只读采集输入形态 / HUD 显隐 /
        /// 当前屏幕 / 打开的对话框与模态面板，主机施加到"相机 Target = 该客户端"的分屏视图。
        /// 纯逻辑 = Net/UiStateNet.cs + Modules/UiStatePorts.cs + Modules/UiStateSyncModule.cs；
        /// 引擎动作 = Engine/ClientUiStateReader.cs（只读）+ Engine/UiMirrorEngine.cs（只写视图）。
        /// </summary>
        private UiStateSyncModule m_uiStateModule;
        private ClientUiStateReader m_uiStateReader;
        private UiMirrorEngine m_uiMirrorEngine;
        private EngineWorldTransfer m_engineWorldTransfer;
        /// <summary>角色创建屏的默认名字摇号（按身份稳定键 + 本次会话盐；计数器见 StatusLine）。
        /// 组合根建一个、随本次会话存活 —— 模块与屏都不持有静态状态。</summary>
        private CharacterNameRoller m_nameRoller;
        private IModEventBus m_eventBus;
        private IModInjector m_injector;
        private SyncConfig m_config;
        /// <summary>
        /// 交互距离上限 + 计数（**唯一实例**）：所有引擎适配器的距离校验共用它，
        /// 规则 = min(请求 reach + 容差, MaxInteractionReach)。无静态状态。
        /// </summary>
        private InteractionReachGate m_reachGate;
        private long m_frameIndex;
        private long m_lastStatusMs;

        // 帧驱动分步隔离：任一步抛异常只计数 + 限流一行，本帧其后的步骤照常执行。
        // 之前是整块 try/catch：一个模块每帧抛异常会吞掉它后面的所有 HUD/模块 tick，
        // 并且每帧写一条 ERROR（2026-10-19 的 548 行/秒就是这样来的）。
        private readonly Dictionary<string, long> m_frameStepFailures =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_frameStepLastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private long m_frameStepFailureTotal;
        /// <summary>数据库注入是否已应用（诊断用）。</summary>
        public bool DatabaseApplied { get; private set; }

        /// <summary>
        /// 聊天自动化钩子（CmdBridge 经反射读取 <c>SuMultiplayerMod.ChatAutomation</c>）。
        /// 组合根创建、随模块卸载置 null；无静态可变状态。
        /// </summary>
        public ChatAutomation ChatAutomation { get; private set; }

        public string Name => ModIdentifier;
        public string Version => ModVersionText;
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;
        public IEnumerable<string> Dependencies => Array.Empty<string>();

        // 游戏内**不再有 SK 换肤按钮**：名字 / 皮肤统一走引擎自己的
        // ESC → More → Edit Players → 点角色 → PlayerScreen；组合根只在屏幕切换时驱动
        // PlayerEditWatcher（进入抓快照、退出比较并同步），不再打开自定义换肤屏。

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            m_eventBus = eventBus;
            m_injector = modInjector;

            m_config = SyncConfig.Load(out string note);
            // 交互距离上限（可配置，默认 16 m）：allowed = min(客户端 reach + 0.5 m, 本上限)。
            // 客户端声明 500 m 也只按上限放行并计数 reachClampedToServerMax。
            m_reachGate = new InteractionReachGate(m_config.MaxInteractionReach);
            m_engineWorldTransfer = new EngineWorldTransfer();
            var diagnostics = new GameDiagnostics();
            m_moduleHost = new ModuleHost(diagnostics);
            // 本机资料存储：角色创建界面写、Hello 读、主机分配的记录回填 —— 唯一来源。
            var profileStore = new LocalPlayerProfileStore();
            // 角色创建屏的默认名字：按身份稳定键（UserId.dat）+ 本次会话盐摇**游戏内置名字表**，
            // 不再用共享的引擎 RNG 状态（否则两个客户端会摇到同一个名字）。日志走 [SuMP] 前缀。
            m_nameRoller = new CharacterNameRoller(
                message => Log.Information("[SuMP] " + message),
                message => Log.Warning("[SuMP] " + message),
                CharacterNameRoller.NewSessionSalt());
            m_terrainModule = new TerrainSyncModule(m_config, profileStore: profileStore);
            // 客户端引信表现：挂在桥的 ApplyCells 漏斗上（主机 / 单机不会进入该分支）。
            m_fusePresenter = new FuseReplicaPresentation();
            m_terrainModule.Bridge.FusePresenter = m_fusePresenter;
            // 客户端营火表现：挂在同一个 ApplyCells 漏斗的方块行为通知上（ClientBlockBehaviorNotifier）。
            // 引擎的 SetCellValueFast 绕过 ChangeCell → 不补通知，营火粒子会永远停在燃烧状态。
            m_campfirePresenter = new CampfireReplicaPresentation();
            m_terrainModule.Bridge.CampfirePresenter = m_campfirePresenter;
            // 客户端熔炉火焰：同一个补通知漏斗（行为外壳）+ FURNACE_STATE 直接驱动。
            // 引擎的熔炉火焰是 behavior 自己建的本地粒子，客户端不跑引擎 behavior → 必须单独表现。
            m_furnacePresenter = new FurnaceReplicaPresentation();
            m_terrainModule.Bridge.FurnacePresenter = m_furnacePresenter;
            m_sessionModule = new SessionModule(m_config, profileStore);
            m_transferModule = new WorldTransferModule(m_config, m_engineWorldTransfer, m_engineWorldTransfer,
            m_engineWorldTransfer);

            // 联机角色：引擎动作需要 SuAPI 的父类反射入口。组合根从引擎拿（Game.Program.ModManager），
            // **以实例注入**给模块 —— 模块内部没有任何静态单例。
            ModManager engineModManager = Game.Program.ModManager;
            if (engineModManager == null)
            {
                throw new InvalidOperationException(
                    "引擎 ModManager 尚未初始化（Game.Program.ModManager 为空）：无法装配联机角色创建所需的父类反射入口");
            }
            var avatarSpawner = new NetworkAvatarSpawner(engineModManager.ModParentField,
                engineModManager.ModParentMethod, diagnostics, "player-avatar");
            // 自定义皮肤资源：引擎侧缓存 / 贴图 / 只读本机皮肤文件；模块只拿端口。
            m_skinCounters = new SkinAssetCounters();
            m_skinStore = new PlayerSkinStore(avatarSpawner, engineModManager.ModParentField,
                diagnostics, m_skinCounters);
            avatarSpawner.SkinMaterializer = m_skinStore;
            m_avatarSpawner = avatarSpawner;
            m_avatarModule = new PlayerAvatarModule(avatarSpawner,
                new SuMultiplayerPlayersXml("player-avatar", diagnostics), profileStore, m_skinStore);
            // 统计 / 成就：统计只有玩家自己的那一端算得准 —— 每个玩家端把自己的 PlayerStats
            // 周期上报给主机，主机按**稳定身份**写进世界作用域角色记录（SuMultiplayerPlayers.xml）
            // 并判定成就；加入 / 重连时只把**本机身份**记录里的值写回本机 PlayerStats，
            // 引擎统计面板（ESC → Game Menu → Statistics）显示的就是自己的数据。
            // 客户端进世界时还会先把下载世界自带的主机统计从本机槽位分出去（旧实现语义）。
            m_statsPort = new PlayerStatsStore(avatarSpawner, m_avatarModule.Records, diagnostics,
                delegate (int clientId)
                {
                    return m_avatarModule.TryGetPeerIdentity(clientId, out string identity)
                        ? identity
                        : null;
                },
                "player-stats");
            m_statsModule = new PlayerStatsModule(m_statsPort, delegate { return Time.RealTime; });
            // 玩家生命 / 体征 / 死亡状态（审计 F14/F7，2026-10 改为**主机权威**）：
            // 主机模拟每个角色（本机 + 客户端化身）并把权威状态下发给各端；客户端只应用，
            // 本机角色的生命 / 体征 / 睡眠 / 病程 / 火焰模拟被摘掉，客户端不再上报。
            // 引擎动作全在 PlayerVitalsReplica 里（抓取 / 字段写入 / 本机模拟闸门 / 复活），
            // 模块层不引用 Game。
            m_vitalsEngine = new PlayerVitalsReplica(avatarSpawner, engineModManager.ModParentField,
                diagnostics, "player-vitals");
            m_vitalsModule = new PlayerVitalsModule(delegate { return Time.RealTime; });
            // 角色动作 / 手持物 / 背包：引擎适配层只读写远端化身与角色背包，
            // 纯逻辑（动作边沿 / 动画状态 / 背包增量）在 Modules/PlayerActionModule.cs 与 Net/ 里，
            // 因此自检可以直接编译同一份逻辑。背包落盘写角色记录（SuMultiplayerPlayers.xml）。
            m_actionBridge = new AvatarActionBridge(avatarSpawner, engineModManager.ModParentField,
                m_terrainModule.TransportPort, m_terrainModule.ControlPort, diagnostics, "player-action");
            m_inventoryStore = new AvatarInventoryStore(avatarSpawner, m_avatarModule.Records,
                diagnostics, "player-action");
            m_actionModule = new PlayerActionModule(m_actionBridge, m_inventoryStore,
                delegate { return Time.RealTime; });
            // 开火复位广播：实体复制模块通过桥→模块回读权威背包并广播手持物。
            m_actionBridge.ActionModule = m_actionModule;
            // 爆炸同步：主机 = 引擎执行后广播（含链式 / 投掷物 / 烟花）；客户端 = 只做表现。
            // 引擎适配器持有子系统挂接与请求执行，网络模块只做协议 / 排序 / 去重 / 计数。
            m_explosionEngine = new ExplosionReplicaEngine(avatarSpawner,
                engineModManager.ModParentField, engineModManager.ModParentMethod, diagnostics,
                "explosion-sync", m_reachGate);
            m_explosionModule = new ExplosionSyncModule(m_explosionEngine);
            // 烧尽表现：引擎 5 秒烧尽分支（SubsystemFireBlockBehavior.cs:227-256）只在本端生成
            // 碎屑/嘶嘶声，不随 CellChange 下发。火外壳上报已处理完的一批格，本模块负责广播；
            // 客户端只做表现（24 m 碎屑 / 16 m 声音，每批 15 / 4，限速）。
            m_burnAwayEngine = new BurnAwayEffectReplicaEngine(avatarSpawner, diagnostics, "effects-sync");
            m_burnAwayModule = new EffectsSyncModule(m_burnAwayEngine);
            // 营火熄灭火声：主机 = 营火外壳检测到"刚烧尽"后广播；客户端 = 限速后只播 Sizzles。
            m_campfireEngine = new CampfireEffectReplicaEngine(m_campfirePresenter,
                diagnostics, "campfire-sync");
            m_campfireModule = new CampfireSyncModule(m_campfireEngine);
            // 拾取物 / 投射物：主机是唯一创建/删除方；客户端只建副本，拾取走请求。
            // 引擎适配器负责对象 → 网络 id、主机拾取执行（引擎 AcquireItems）、客户端插值。
            m_entityEngine = new PickableReplicaEngine(avatarSpawner,
                engineModManager.ModParentField, diagnostics, "entity-replication", m_reachGate);
            // 客户端编辑销毁方块 → 主机补生成掉落：地形桥在 ChangeCell 前后调用这个引擎端口
            // （先 GetDropValues + OnItemHarvested，再 AddPickable），掉落者拿到 1.5s 拾取免疫窗口。
            m_terrainModule.Bridge.HostBlockDrops = m_entityEngine;
            m_entityModule = new EntityReplicationModule(m_entityEngine, m_actionBridge,
                m_inventoryStore);
            // 方块使用 / 容器：客户端在 ComponentPlayer.Update 之前拦下 Interact（不本地执行），
            // 主机校验后执行引擎方块行为 / 提供权威方块实体库存；容器面板在客户端显示副本，
            // 槽位变化走 ContainerEditRequest，主机落地后广播权威 ContainerState。
            m_blockUseEngine = new ContainerReplicaEngine(avatarSpawner,
                engineModManager.ModParentField, engineModManager.ModParentMethod, diagnostics,
                "block-use", m_reachGate);
            m_blockUseModule = new BlockUseSyncModule(m_blockUseEngine);
            // 家具设计 / 内容同步：主机的世界设计表是唯一权威；客户端的锤子 / 颜料桶只发请求，
            // 主机校验后安装设计并广播。客户端把记录装进**副本表**（不写存档、不进家具库）。
            m_furnitureEngine = new FurnitureReplicaEngine(avatarSpawner,
                engineModManager.ModParentField, diagnostics, "furniture-sync", m_reachGate);
            m_furnitureModule = new FurnitureSyncModule(m_furnitureEngine);
            // 可编辑方块内容（真值表 188 / 内存条 186）：对话框 OK 的提交在客户端只改本地副本
            // （引擎事实），主机永远看不到 —— 外壳接管 OnEditBlock，提交变成 EDITABLE_EDIT_REQUEST，
            // 主机校验并落地权威内容后广播 EDITABLE_STATE；客户端只把权威内容写进副本。
            // Source: Survivalcraft/Game/SubsystemEditableItemBehavior.cs:23-39
            // Source: Survivalcraft/Game/SubsystemTruthTableCircuitBlockBehavior.cs:38-53
            // Source: Survivalcraft/Game/SubsystemMemoryBankBlockBehavior.cs:38-53
            m_editableBridge = new EditableBlockBridge(avatarSpawner, diagnostics, "editable-sync",
                m_reachGate);
            m_editableModule = new EditableSyncModule(m_editableBridge);
            // 外壳通过 SubsystemTerrain → Bridge 找适配层（与营火 / 活塞表现同一注入方式）。
            m_terrainModule.Bridge.EditableBlocks = m_editableBridge;
            // 远程动作声音：客户端点开关 / 按钮 / 拉杆时，方块行为由主机用远端玩家的
            // ComponentMiner 执行，SubsystemAudio 只在主机播放（SwitchElectricElement.cs:27,
            // SubsystemDoorBlockBehavior.cs:24, SubsystemCampfireBlockBehavior.cs:209/213）。
            // 适配器在 BlockUseSyncModule 的同步执行窗口内读取音频队列新增条目；模块合并成
            // 一条 RemoteActionSound，发给请求者 + 附近客户端（纯表现，不复制粒子）。
            // 非虚的 SubsystemAudio API 无法用派生外壳截获，原因见 Engine/RemoteActionAudioReplica.cs。
            m_remoteAudioEngine = new RemoteActionAudioReplica(avatarSpawner, diagnostics,
                "remote-audio");
            m_remoteAudioModule = new RemoteAudioSyncModule(m_remoteAudioEngine);
            // 循环环境音：三条 loop 由 SubsystemAudio.CreateSound 创建、从不进入音频队列，
            // 全局中继（队列差集）覆盖不到；主机按每个客户端化身位置算 4 个音量，
            // 客户端施加到本机 SubsystemAmbientSounds。
            m_ambientSoundEngine = new AmbientSoundReplicaEngine(avatarSpawner, diagnostics,
                "ambient-sound");
            m_ambientSoundModule = new AmbientSoundSyncModule(m_ambientSoundEngine);
            // 已经有定向事件在客户端表现同一条声音的适配器：主机本端播完后登记给全局捕获，
            // 下一次队列差集按 (名字 / 目录, 主机侧衰减音量) 精确跳过 → 不会双播。
            // 远端动作窗口由 RemoteActionAudioReplica.EndCapture 自己登记，不在这里接线。
            m_explosionEngine.SoundCover = m_remoteAudioEngine;
            m_burnAwayEngine.SoundCover = m_remoteAudioEngine;
            m_campfireEngine.SoundCover = m_remoteAudioEngine;
            m_entityEngine.SoundCover = m_remoteAudioEngine;
            // 水花表现：引擎在主机本端为“拾取物 / 投射物落水”创建 WaterSplashParticleSystem 并
            // 播放 Audio/Splashes（SubsystemPickables.cs:325-330 / SubsystemProjectiles.cs:383-390），
            // 这条表现不在 EntityState 里 → 客户端看不到也听不到。适配器在主机两个子系统外壳的
            // base.Update 前后做粒子表差集（非虚 AddParticleSystem 无法用派生外壳截获），模块按
            // 客户端距离 48 m 分批下发；客户端只重建同一粒子系统 + 同一声音，绝不写格 / 生成实体。
            m_waterSplashEngine = new WaterSplashReplicaEngine(avatarSpawner, diagnostics,
                "water-splash");
            m_waterSplashEngine.SoundCover = m_remoteAudioEngine;
            m_waterSplashModule = new WaterSplashSyncModule(m_waterSplashEngine);
            // 活塞运动表现：引擎把推 / 拉的中间过程放在 SubsystemMovingBlocks 里（不是格子数据），
            // 客户端不跑活塞逻辑 ⇒ 中间过程看不到。适配器在主机检测每次运动开始 / 结束（含提前结束），
            // 客户端按主机参数在本地 SubsystemMovingBlocks 建只表现集合；最终格仍走既有 CellChange。
            // 适配器同时提供"玩家端位置 / 兴趣窗口"视图：只发给看得到这些活塞的端。
            m_pistonEngine = new PistonReplicaEngine(avatarSpawner, diagnostics,
                delegate (int clientId, out int centerChunkX, out int centerChunkZ,
                    out int radiusChunks)
                {
                    return m_terrainModule.TryGetClientRange(clientId, out centerChunkX,
                        out centerChunkZ, out radiusChunks);
                }, "piston-sync");
            m_pistonModule = new PistonSyncModule(m_pistonEngine);
            // 客户端移动组表现：挂在桥的 ApplyCells 漏斗上（主机 / 单机不会进入该分支）。
            // 权威格变化落在本地只表现集合（活塞 / 塌落）的影响区内 → 立刻取消该集合，
            // 避免本地继续插值到原目标位置（穿过被推方块约半格；实机缺陷）。
            m_terrainModule.Bridge.PistonPresenter = m_pistonEngine;
            // 主机 Hello 资料闸门 = 联机角色模块（它拥有角色记录与本地玩家）：
            // 没有记录且 Hello 没带完整资料 → profile_required，不下发世界。
            m_terrainModule.SetHelloProfileGate(m_avatarModule);

            // 生物生成/同步：主机权威生成 + 按客户端区域分配；客户端只做复制。
            // 适配器同时是"客户端区域来源"（联机角色位置）与引擎侧生成子系统注入的持有者。
            m_creatureWorld = new CreatureWorldAdapter(engineModManager.ModParentField,
                engineModManager.ModParentMethod, avatarSpawner, diagnostics);
            m_creatureModule = new CreatureSpawnModule(m_creatureWorld, m_creatureWorld);
            // 近战攻击（主机权威）：主机决定命中 / 伤害 / 剩余生命并广播给攻击者 + 观测端；
            // 客户端只做预测表现（挥手 / 挥击声 / 红色命中标记），结果到达后确认或**显式回滚**。
            // 距离与交互 / 挖掘同一套语义：allowed = min(请求 reach + 0.5 m, meleereachmax, 引擎 reach)，
            // 客户端预检与主机裁决调用同一个 MeleeReachRule（结果里回传主机上限）。
            // Source: Survivalcraft/Game/ComponentMiner.cs:293（Hit）/ :376（Raycast）/ :472（AttackBody）
            m_meleeEngine = new MeleeReplicaEngine(avatarSpawner, m_creatureModule, m_creatureWorld,
                engineModManager.ModParentField, diagnostics, m_terrainModule.TransportPort,
                m_config.MeleeMaxReach, "melee-sync");
            m_meleeEngine.PredictionEnabled = m_config.MeleePrediction;
            m_meleeModule = new MeleeSyncModule(m_meleeEngine, delegate { return Time.RealTime; });

            // 客户端副本 / 主机权威：引擎侧服务（编辑捕获、主机权威落地、世界信息、RNG 状态）
            // 只在这里构造一次，模块通过端口访问 —— 模块层不引用 Game。
            m_clientAuthority = new ClientReplicaEngine(m_terrainModule.Bridge, m_reachGate);
            // place / dig 编辑的玩家相对距离校验：请求者权威化身位置来自联机角色表。
            m_clientAuthority.SetPlayerPositionResolver(
                delegate (int clientId, out Vector3 position)
                {
                    return avatarSpawner.TryGetAvatarCell(clientId, out position, out _);
                });
            // 主机落地客户端家具格编辑前的设计索引校验：data 里的设计必须在主机设计表里。
            m_clientAuthority.SetFurnitureValueValidator(delegate (int contents, int data)
            {
                if (contents != FurnitureBlock.Index) return true;
                var project = GameManager.Project;
                var behavior = project?.FindSubsystem<SubsystemFurnitureBlockBehavior>(false);
                return behavior != null &&
                    behavior.GetDesign(FurnitureBlock.GetDesignIndex(data)) != null;
            });
            // 方块销毁掉落的 ToolLevel：主机按联机化身的权威背包镜像读手持方块（与 ComponentMiner.Dig 同源）。
            m_clientAuthority.SetToolLevelResolver(
                delegate (int clientId, out int toolLevel)
                {
                    return avatarSpawner.TryGetHeldToolLevel(clientId, out toolLevel);
                });
            m_authorityModule = new ClientAuthorityModule();

            // 端口先就位再解析：会话模块要在 Attach 里拿到"通道控制器"，
            // 世界传输模块要有 transport 能力才会启动。
            m_moduleHost.Register(m_terrainModule);
            m_moduleHost.RegisterPort<ITransportPort>(m_terrainModule.TransportPort);
            // 握手资料视图（主机/分配资料/是否要求先建角色 + 重新握手）：联机角色模块用它。
            m_moduleHost.RegisterPort<IPlayerProfileHandshakePort>(m_terrainModule.ProfileHandshakePort);
            // 实时地图 v9：主机端动物的只读位置来源（只枚举主机世界里的非玩家生物，
            // 不扰动生物同步的 50 Hz 扫描；客户端不贡献动物）。
            m_moduleHost.RegisterPort<IHostCreatureCellSource>(new HostCreatureCellSource());
            // 世界清单的会话标识 / 导出基线由地形模块提供（世界传输模块的硬依赖：
            // 没有它就不发世界，避免客户端拿到没有会话标签的世界、每次加入都整图重下）。
            m_moduleHost.RegisterPort<IWorldSessionPort>(m_terrainModule.WorldSessionPort);
            m_moduleHost.RegisterPort<ITransportControlPort>(m_terrainModule.ControlPort);
            // 客户端权威模块的端口：世界信息广播 / 编辑超时回滚 / 编辑捕获与主机权威落地 / RNG 状态。
            m_moduleHost.RegisterPort<IPeerBroadcastPort>(m_terrainModule);
            m_moduleHost.RegisterPort<IChunkReverifyPort>(m_terrainModule);
            m_moduleHost.RegisterPort<IClientEditPort>(m_clientAuthority);
            m_moduleHost.RegisterPort<IWorldInfoPort>(m_clientAuthority);
            m_moduleHost.RegisterPort<IRandomStatePort>(m_clientAuthority);
            m_moduleHost.RegisterPort<IRoomBrowsePort>(m_sessionModule);
            // 消息层（类型 id ↔ 解析器/处理器 + 第三方发送器）：内置模块与第三方模块共用同一个注册表。
            m_moduleHost.RegisterPort<IMessageRegistryPort>(m_terrainModule.MessagePort);
            // 活塞运动的"玩家端在哪 / 兴趣半径多大"只读视图（远处端不发，避免无用的本地集合）。
            m_moduleHost.RegisterPort<IPistonPeerViewportPort>(m_pistonEngine);
            // 远程动作声音：引擎适配（捕获 / 播放）与中继模块（协议 / 距离 / 限速）各一个端口；
            // 方块使用模块只拿中继端口开 / 关窗口，不直接接触引擎音频队列。
            m_moduleHost.RegisterPort<IRemoteAudioPort>(m_remoteAudioEngine);
            m_moduleHost.RegisterPort<IRemoteAudioRelayPort>(m_remoteAudioModule);
            // 循环环境音：模块只拿引擎端口（逐端扫描 / 客户端施加），不直接接触地形或引擎音频字段。
            m_moduleHost.RegisterPort<IAmbientSoundPort>(m_ambientSoundEngine);
            // 水花表现：模块只拿端口（捕获开关 / 玩家端位置 / 客户端表现），不直接接触引擎粒子表。
            m_moduleHost.RegisterPort<IWaterSplashPort>(m_waterSplashEngine);
            // 可编辑方块内容：模块只拿端口（内容读写 / 身份与距离校验 / 广播目标 / 副本应用）。
            m_moduleHost.RegisterPort<IEditableBlockPort>(m_editableBridge);
            m_moduleHost.ProvideCapability("transport");
            m_moduleHost.Register(m_sessionModule);
            m_moduleHost.Register(m_transferModule);
            // 联机角色模块要 transport + world.transfer 两个硬依赖，所以最后注册。
            m_moduleHost.Register(m_avatarModule);
            // 自定义皮肤：记录端口由联机角色模块提供，引擎资源端口由 PlayerSkinStore 提供。
            // 模块硬依赖 transport + player.avatars，所以必须排在联机角色模块之后。
            m_moduleHost.RegisterPort<IPlayerSkinPort>(m_skinStore);
            m_moduleHost.RegisterPort<IPlayerSkinRecordPort>(m_avatarModule);
            // 统计 / 成就：模块只拿引擎端口（本机 PlayerStats / 记录），不直接引用 Game。
            m_moduleHost.RegisterPort<IPlayerStatsPort>(m_statsPort);
            m_skinModule = new PlayerSkinModule(m_skinCounters, profileStore);
            m_moduleHost.Register(m_skinModule);
            // 编辑玩家屏退出时，自定义皮肤必须走皮肤模块的资源链（读取/校验/上传/主机广播）；
            // 联机角色模块只负责名字/性别/内置皮肤/衣服的资料消息与记录。
            m_avatarModule.EditSkinSink = m_skinModule;
            // 统计 / 成就同步：transport 硬依赖；端口（IPlayerStatsPort）已在上方注册。
            m_moduleHost.Register(m_statsModule);
            // 玩家生命 / 体征 / 死亡状态：transport 硬依赖；引擎端口（IPlayerVitalsPort）必须先注册。
            // 下发走 ITransportPort.TrySendTo（按 clientId 定向，含观察者转发）。
            m_moduleHost.RegisterPort<IPlayerVitalsPort>(m_vitalsEngine);
            // 睡眠表现 + 点击唤醒（黑屏淡入 / Zzz / 提示 / 控件隐藏 / 主机 WakeUp）：同一个
            // PlayerVitalsReplica 实现，必须与 IPlayerVitalsPort 一起注册（模块硬依赖两个端口）。
            m_moduleHost.RegisterPort<IPlayerSleepPort>(m_vitalsEngine);
            m_moduleHost.Register(m_vitalsModule);
            // 动作/背包模块要 transport + player.avatars（由联机角色模块提供）→ 必须在它之后注册。
            m_moduleHost.Register(m_actionModule);
            // 爆炸同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_explosionModule);
            // 烧尽表现同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_burnAwayModule);
            m_moduleHost.Register(m_campfireModule);
            // 实体复制：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_entityModule);
            // 远程动作声音中继：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_remoteAudioModule);
            // 循环环境音同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_ambientSoundModule);
            // 水花表现同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_waterSplashModule);
            // 方块使用 / 容器同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_blockUseModule);
            // 家具设计 / 内容同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_furnitureModule);
            // 可编辑方块内容同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_editableModule);
            // 活塞运动表现同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_pistonModule);
            m_moduleHost.Register(m_creatureModule);
            // 近战攻击：transport 硬依赖；引擎端口（IMeleeWorldPort）必须先于模块注册。
            m_moduleHost.RegisterPort<IMeleeWorldPort>(m_meleeEngine);
            m_moduleHost.Register(m_meleeModule);
            // 客户端权威模块：transport 硬依赖；端口缺失时 Attach 会写明确原因（不假装同步）。
            m_moduleHost.Register(m_authorityModule);
            // 聊天：主机权威（客户端只发 ChatSendRequest，主机净化 / 限速 / 广播）。
            // 本机显示名 / 身份走引擎的唯一来源（PlayerIdentity），模块本身不引用 Game。
            m_chatModule = new ChatModule(
                delegate { return PlayerIdentity.LocalName(); },
                delegate { return PlayerIdentity.LocalIdentity(); });
            m_moduleHost.Register(m_chatModule);
            // 实时语音：引擎侧 OpenAL 采集/播放端口 + 纯逻辑模块（VAD / 可配置 ADPCM /
            // 1-bit CVSD / 主机中继 / 静音表 / jitter）。端口必须先于模块注册
            // （Attach 里就要拿到）。档位与 VAD 挂起来自 SuMultiplayer.json。
            m_voiceAudio = new VoiceAudioEngine();
            m_voiceModule = new VoiceChatModule(m_voiceAudio,
                new VoiceOptions
                {
                    Enabled = m_config.VoiceEnabled,
                    PushToTalk = m_config.VoicePushToTalk,
                    PlaybackVolume = m_config.VoiceVolume,
                    Quality = m_config.VoiceQuality,
                    VadHangoverMs = m_config.VoiceVadHangoverMs
                },
                delegate { return profileStore.Profile?.Name; });
            m_moduleHost.RegisterPort<IVoiceAudioPort>(m_voiceAudio);
            m_moduleHost.Register(m_voiceModule);
            m_clientAuthority.SetRangeResolver(delegate (int clientId, out int centerChunkX,
                out int centerChunkZ, out int radiusChunks)
            {
                return m_terrainModule.TryGetClientRange(clientId, out centerChunkX,
                    out centerChunkZ, out radiusChunks);
            });
            m_moduleHost.RegisterPort<IWorldReadyPort>(m_transferModule.ReadyPort);
            // 下载世界端口也要注册：联机角色模块靠它判断"当前玩的是不是下载来的联机世界"
            // （客户端建主机角色的前提），拿不到就会明确拒绝建角色（不做 index 兜底）。
            m_moduleHost.RegisterPort<IDownloadedWorldPort>(m_transferModule.DownloadedWorldPort);
            // 客户端界面层镜像（CLIENT_UI_STATE，id 100）：
            //   客户端 = Engine/ClientUiStateReader.cs 只读采集（输入形态 / HUD 显隐 / 当前屏幕 /
            //            打开的对话框与模态面板 / 聊天输入行），变化才发 + 2 s 心跳；
            //   主机   = Engine/UiMirrorEngine.cs 施加到"相机 Target = 该客户端 ComponentPlayer"的
            //            分屏视图（自动客户端视图 / H3 replace 模式同一条绑定），只写视图自己的
            //            HUD 与输入开关，绝不写客户端化身。
            //   容器面板的方块坐标来自 block-use 模块（它已按端跟踪打开中的容器），不猜坐标。
            // 聊天输入行状态由组合根用委托注入（UI 类型不反向进入模块）。
            m_uiMirrorEngine = new UiMirrorEngine(avatarSpawner, m_blockUseModule,
                engineModManager.ModParentField, diagnostics);
            m_uiStateReader = new ClientUiStateReader(avatarSpawner,
                delegate { return m_chatOverlay != null && m_chatOverlay.HasOpenInput; });
            m_uiStateModule = new UiStateSyncModule(m_uiStateReader, m_uiMirrorEngine);
            m_moduleHost.Register(m_uiStateModule);
            ModuleResolution resolution = m_moduleHost.Resolve("OnLoad");

            m_uiAttacher = new RoomUiAttacher(m_sessionModule, m_transferModule.ReadyPort);
            // 引擎 Edit Players（PlayerScreen）进入/退出钩子：名字/皮肤统一走引擎自己的编辑屏。
            // 进入抓快照、退出比较；主机广播改名后的本机角色，客户端按 (clientId, slot) 提交资料。
            m_editWatcher = new PlayerEditWatcher(avatarSpawner, engineModManager.ModParentField,
                m_avatarModule.EditCounters,
                delegate (PlayerEditSnapshot enter, PlayerEditSnapshot exit)
                {
                    m_avatarModule.OnEditScreenExited(enter, exit);
                },
                message => Log.Information("[SuMP] " + message),
                message => Log.Warning("[SuMP] " + message),
                message => Log.Error("[SuMP] " + message));
            // UI 拖出到世界：引擎 ViewWidget.DragDrop 会先扣本地背包再本地 AddPickable
            // （客户端物品直接消失）。这里每帧把本机 GameWidget 的 DragHost 换成外壳，
            // 在 DragDrop 之前把意图交给同一条 PickableDropRequest 链 —— 本地不扣、不生成。
            m_uiDropAttacher = new UiDropAttacher(m_entityEngine,
                engineModManager.ModParentField,
                message => Log.Information(message),
                message => Log.Warning(message));
            // 调试镜像（debug-only，默认关闭；配置键 debugmirrorclient）：
            //   纯逻辑 = Modules/DebugMirrorPorts.cs（配置闸门 / 目标选择 / 生命周期 / 限流日志 / 计数），
            //   引擎动作 = Engine/DebugMirrorClient.cs（临时 camera-only PlayerData + 第 3 个 GameWidget），
            //   HUD = UI/DebugMirrorHud.cs（MoreContents 里的 MIR 小按钮，只在配置开启 + 主持房间时出现）。
            // 目标从 NetworkAvatarSpawner 的公开 API 读，不碰它的文件；客户端侧永远不创建。
            m_debugMirrorClient = new DebugMirrorClient(avatarSpawner,
                engineModManager.ModParentField, engineModManager.ModParentMethod, diagnostics,
                "debug-mirror");
            m_debugMirror = new DebugMirrorController(m_config.DebugMirrorClient,
                m_debugMirrorClient,
                message => Log.Information(message),
                message => Log.Warning(message),
                delegate { return Time.RealTime; });
            m_debugMirrorHud = new DebugMirrorHud(m_debugMirror,
                delegate { return m_sessionModule != null && m_sessionModule.IsHostingRoom; },
                message => Log.Information(message),
                message => Log.Warning(message));
            // 调试镜像角色（debug-only，默认关闭；配置键 mirrorcharacter / mirrorcharacterclient /
            //   mirrorcharactermode）：
            //   纯逻辑 = Modules/DebugMirrorCharacterPorts.cs（配置闸门 / 名字查找 / 目标选择 / 计划 / 生命周期 / 计数），
            //   引擎动作 = Engine/MirrorCharacterEngine.cs（只读客户端化身，只写主机本地角色 H3 + 快照恢复）。
            //   默认 replace = 用 H3 的第三个分屏显示客户端角色本身（H3 的身体从世界里摘掉、不复制状态）；
            //   copy = 旧行为（把客户端状态逐拍复制到 H3 身上）。
            // 配置关闭时控制器 Tick 直接 return，不产生任何引擎调用 / 对象。
            m_mirrorCharacterEngine = new MirrorCharacterEngine(avatarSpawner,
                engineModManager.ModParentField, engineModManager.ModParentMethod, diagnostics);
            m_mirrorCharacter = new MirrorCharacterController(m_config.MirrorCharacter,
                m_config.MirrorCharacterClient, m_config.MirrorCharacterMode,
                m_mirrorCharacterEngine,
                message => Log.Information(message),
                message => Log.Warning(message),
                delegate { return Time.RealTime; });
            // 镜像宿主（**调试功能，默认开启**；配置键 mirrorcharacterauto / mirrorhostnameprefix /
            //   mirrorcharacter + mirrorcharactermode 复用）：
            //   纯逻辑 = Modules/MirrorHostPorts.cs（配置 / 池计算 / 闸门 / 命名 / 复用 / 生命周期 / 计数），
            //   引擎动作 = Engine/MirrorHostEngine.cs（只用公开链路 new PlayerData → AddPlayerData /
            //   RemovePlayerData；绑定复用上面的 MirrorCharacterEngine）。
            //   与显式 mirrorcharacter 的关系：WorldDatabase 里已有同名的 auto 角色 → 复用它；
            //   手动创建的角色（h1/h2/H3）绝不被归入 auto 账本，离开时绝不被删除。
            m_mirrorHostEngine = new MirrorHostEngine(avatarSpawner, m_mirrorCharacterEngine, diagnostics)
            {
                NamePrefix = m_config.MirrorHostNamePrefix
            };
            m_mirrorHost = new MirrorHostController(m_config.MirrorCharacterAuto,
                m_config.MirrorHostNamePrefix, m_config.MirrorCharacterMode, m_config.MirrorCharacter,
                m_mirrorHostEngine,
                message => Log.Information(message),
                message => Log.Warning(message),
                delegate { return Time.RealTime; });
            // 自动客户端分屏（默认开启；配置键 clientview / clientviewclient）：
            //   纯逻辑 = Modules/ClientViewPorts.cs（配置闸门 / 候选筛选 / 槽位账本 / 生命周期 / 计数），
            //   引擎动作 = Engine/ClientViewEngine.cs（camera-only 临时 PlayerData + GameWidget，
            //   实体不加入 Project：不存盘 / 不碰撞 / 不可攻击，与 DebugMirrorClient 同一配方）。
            // 槽位账本 = 本机角色 + 联机化身 + 视图 ≤ 4；没空槽只记 clientViewNoSlot，不崩溃。
            // 优先级：显式 mirrorcharacter（H3 replace）已绑定同一客户端时，自动视图让位；
            // 已被 debugmirrorclient 实际镜像的客户端同理（显式配置优先）。
            m_clientViewEngine = new ClientViewEngine(avatarSpawner,
                engineModManager.ModParentField, engineModManager.ModParentMethod, diagnostics);
            m_clientView = new ClientViewController(m_config.ClientView, m_config.ClientViewClient,
                m_clientViewEngine,
                message => Log.Information(message),
                message => Log.Warning(message),
                delegate { return Time.RealTime; },
                delegate (int clientId)
                {
                    if (m_mirrorCharacterEngine != null && m_mirrorCharacterEngine.IsBound &&
                        m_mirrorCharacterEngine.BoundClientId == clientId)
                        return true;                    // 镜像宿主（mirrorcharacterauto）已经接管这个客户端 → 自动视图让位（显式镜像优先）。
                    if (m_mirrorHost != null && m_mirrorHost.HasHostFor(clientId))
                    {
                        if (m_clientView != null) m_clientView.Counters.NoteYieldToMirrorHost();
                        return true;
                    }

                    if (m_debugMirror != null && m_debugMirror.IsEnabled &&
                        m_debugMirror.TryGetActiveTarget(out DebugMirrorTarget debugTarget) &&
                        debugTarget.IsResolved && debugTarget.ClientId == clientId)
                        return true;
                    return false;
                });
            // 聊天 HUD：底部消息流 + 暖色半透明面板（无蓝色）+ 回车/T 打开输入 +
            // 触摸端常驻“聊天”按钮。关闭输入时不拦截任何游戏输入。
            m_chatOverlay = new ChatOverlayAttacher(m_chatModule,
                message => Log.Information(message),
                message => Log.Warning(message));
            // 自动化钩子：CmdBridge 通过反射读这个属性（本 Mod 不引用 CmdBridge）。
            // 与聊天 HUD 是同一个实例；输入 / 提交走 ChatModule 的同一条发送链。
            ChatAutomation = new ChatAutomation(m_chatOverlay);
            // 语音 HUD：右下角语音状态图标（聊天按钮正上方，唯一的开关；点按 / U 键）+
            // 右上状态行 / TALK（可选 PTT，无键盘绑定）/ MUTE ALL + 每个远端的静音按钮。
            // U 键的文本输入抑制由聊天 HUD 的"输入行是否打开"提供（组合根是唯一同时知道
            // 两个 UI 的地方；VoiceHud 不反向依赖 ChatOverlay 类型）。
            m_voiceHud = new VoiceHud(m_voiceModule,
                delegate { return m_chatOverlay != null && m_chatOverlay.HasOpenInput; });

            // 第三方模块接入点：在 OnLoad 里订阅这个事件即可拿到 MessageRegistry + IMessageSender
            // （见 doc/message-protocol.md）。事件只在 SuMultiplayer 加载完成时触发一次；
            // 组合根自己的模块则通过 IMessageRegistryPort 端口在 Attach 里注册。
            try
            {
                eventBus.TriggerEvent("SuMultiplayer.MessageRegistry",
                    new object[] { m_terrainModule.MessagePort });
            }
            catch (Exception e)
            {
                Log.Warning("[SuMP] 触发 SuMultiplayer.MessageRegistry 失败（第三方消息模块可能收不到注册表）：" +
                    e.GetType().Name + " " + e.Message);
            }
            // 加载顺序兜底（不是功能兜底）：后加载的第三方模块可以主动请求一次注册表。
            eventBus.SubscribeEvent("SuMultiplayer.MessageRegistry.Request", args =>
            {
                if (args != null && args.Length > 0 && args[0] is Action<IMessageRegistryPort> callback)
                    callback(m_terrainModule.MessagePort);
                return args;
            }, EventPriority.NORMAL);

            Log.Information("[SuMP] " + ModIdentifier + " " + ModVersionText + " 已加载 · " + note);
            Log.Information("[SuMP] " + m_config.Describe() + " · 模块 Active=" + resolution.Active +
                " Unavailable=" + resolution.Unavailable + " Faulted=" + resolution.Faulted +
                (resolution.EssentialComplete ? "" : " 必需能力缺失=" + string.Join(",", resolution.EssentialMissing)));
            Log.Information("[SuMP] " + m_sessionModule.StatusLine());
            Log.Information("[SuMP] " + m_transferModule.StatusLine());
            Log.Information("[SuMP] " + m_terrainModule.StatusLine());
            Log.Information("[SuMP] " + m_avatarModule.StatusLine());
            Log.Information("[SuMP] " + m_actionModule.StatusLine());
            Log.Information("[SuMP] " + m_statsModule.StatusLine());
            Log.Information("[SuMP] " + m_vitalsModule.StatusLine());
            Log.Information("[SuMP] " + m_meleeModule?.StatusLine());
            Log.Information("[SuMP] " + m_statsPort.Describe());
            Log.Information("[SuMP] " + m_inventoryStore.StatusLine());
            Log.Information("[SuMP] " + m_explosionModule.StatusLine());
            Log.Information("[SuMP] " + m_burnAwayModule.StatusLine());
            Log.Information("[SuMP] " + m_campfireModule.StatusLine());
            Log.Information("[SuMP] " + m_campfirePresenter.Describe() + " · " +
                m_terrainModule.Bridge.BlockBehaviorNotify.Describe());
            Log.Information("[SuMP] " + m_furnacePresenter.Describe());
            Log.Information("[SuMP] " + m_entityModule.StatusLine());
            Log.Information("[SuMP] " + m_blockUseModule.StatusLine());
            Log.Information("[SuMP] " + m_furnitureModule.StatusLine());
            Log.Information("[SuMP] " + m_editableModule.StatusLine());
            Log.Information("[SuMP] " + m_remoteAudioModule.StatusLine());
            Log.Information("[SuMP] " + m_ambientSoundModule.StatusLine());
            Log.Information("[SuMP] " + m_ambientSoundEngine.Describe());
            Log.Information("[SuMP] " + m_waterSplashModule.StatusLine());
            Log.Information("[SuMP] " + m_pistonModule.StatusLine());
            Log.Information("[SuMP] " + m_entityEngine.Describe());
            Log.Information("[SuMP] " + m_creatureModule.StatusLine());
            Log.Information("[SuMP] " + m_creatureWorld.StatusLine());
            Log.Information("[SuMP] " + m_authorityModule.StatusLine());
            Log.Information("[SuMP] " + m_chatModule.StatusLine());
            Log.Information("[SuMP] " + m_voiceModule.StatusLine());
            Log.Information("[SuMP] 交互距离：serverMax=" +
                m_reachGate.ServerMaxMeters.ToString("0.##", CultureInfo.InvariantCulture) +
                "m tolerance=" + InteractionReachGate.ToleranceMeters.ToString("0.##",
                    CultureInfo.InvariantCulture) + "m · " + m_reachGate.Counters.Describe());
            Log.Information("[SuMP] " + m_nameRoller.StatusLine());
            Log.Information("[SuMP] " + m_fusePresenter.Describe());
            Log.Information("[SuMP] " + m_debugMirror.StatusLine() +
                (m_debugMirror.IsConfigured
                    ? ""
                    : "（debugmirrorclient 默认关闭；启用见 Mods/SuMultiplayer.json）"));
            Log.Information("[SuMP] " + m_mirrorCharacter.StatusLine() +
                (m_mirrorCharacter.IsConfigured
                    ? ""
                    : "（mirrorcharacter 默认关闭；启用见 Mods/SuMultiplayer.json）"));
            Log.Information("[SuMP] " + m_mirrorHost.StatusLine() +
                (m_mirrorHost.IsConfigured
                    ? ""
                    : "（mirrorcharacterauto 已关闭 / 配置写错）"));
            Log.Information("[SuMP] " + m_clientView.StatusLine() +
                (m_clientView.IsConfigured
                    ? ""
                    : "（clientview 已关闭 / 配置写错；默认 true）"));
            Log.Information("[SuMP] " + m_uiStateModule.StatusLine() +
                " · unsupportedTypes=" + m_uiStateModule.DescribeUnsupported());

            // 引擎注入
            // 镜像宿主的 auto 标记注册表：注册在 SubsystemPlayers 模板下（作为它的子节点），
            // 数据写在它自己的 ValuesDictionary 里 —— Project.SaveProject 会把每个子系统的
            // ValuesDictionary 原样写进世界存档，下次进世界原样读回；PlayerData 自己的 Save
            // 只写引擎固定字段，写不下自定义标记。
            // Source: EntitySystem/GameEntitySystem/Project.cs:Project(GameDatabase, ProjectData)
            // Source: TemplatesDatabase/ValuesDictionary.cs:Save(XElement)（整张表写存档）
            // 引擎注入：SubsystemPlayers 的**完整子类**（只追加镜像宿主注册表，其余继承引擎语义）。
            // 注意：必须是真的继承 SubsystemPlayers；替换成"新子系统"会让引擎报
            // 「Required subsystem Game.SubsystemPlayers does not exist in project.」
            modInjector.Register("Game.SubsystemPlayers", "SuMultiplayer.Engine.SuSubsystemPlayers");
            // 引擎注入：把 Game.SubsystemTerrain 换成我们的薄外壳
            modInjector.Register("Game.SubsystemTerrain", "SuMultiplayer.Engine.SuSubsystemTerrain");
            // 家具：世界设计表外壳（客户端 Save 写空 / 缺失设计不崩溃）、锤子（创建/编辑只发请求）、
            // 颜料桶（家具上色改成主机请求）。方块 227 替换成 SuFurnitureBlock 作为最后一道闸门。
            modInjector.Register("Game.SubsystemFurnitureBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemFurnitureBlockBehavior");
            modInjector.Register("Game.SubsystemHammerBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemHammerBlockBehavior");
            modInjector.Register("Game.SubsystemBucketBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemBucketBlockBehavior");
            modInjector.RegisterBlock(FurnitureBlock.Index, typeof(SuFurnitureBlock),
                ModIdentifier);
            // 生物生成：客户端 = 不跑原生随机生成（只做主机记录的复制）；主机 = 原生模型照常跑。
            // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemSpawn.cs
            // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemCreatureSpawn.cs
            modInjector.Register("Game.SubsystemSpawn", "SuMultiplayer.Engine.SuSpawnSubsystem");
            modInjector.Register("Game.SubsystemCreatureSpawn",
                "SuMultiplayer.Engine.SuCreatureSpawnSubsystem");
            // 客户端副本：所有会改地形的"世界自发演化"在客户端都不推进（见 Engine/ClientReplicaSubsystems.cs）。
            // 这些 Class 参数与 Pak/Database.xml 的 Value 完全一致；主机 / 单机照常走 base。
            // Source: Pak/Database.xml（各 Subsystem 的 Class Parameter）
            modInjector.Register("Game.SubsystemWeather", "SuMultiplayer.Engine.SuSubsystemWeather");
            modInjector.Register("Game.SubsystemWaterBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemWaterBlockBehavior");
            modInjector.Register("Game.SubsystemMagmaBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemMagmaBlockBehavior");
            // 水族三个子类也继承 SubsystemWaterBlockBehavior.Update → SpreadFluid →
            // 每 0.25 game-sec 两次 ProcessModifiedCells（清空引擎改动表）。不注入它们，
            // 客户端/主机都会在"玩家挖/放 → 清表 → 地形捕获"之间丢掉本地写入（实机丢编辑根因）。
            // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:SpreadFluid (:312)
            modInjector.Register("Game.SubsystemInWaterBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemInWaterBlockBehavior");
            modInjector.Register("Game.SubsystemWaterPlantBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemWaterPlantBlockBehavior");
            modInjector.Register("Game.SubsystemBottomSuckerBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemBottomSuckerBlockBehavior");
            modInjector.Register("Game.SubsystemFireBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemFireBlockBehavior");
            modInjector.Register("Game.SubsystemGrassBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemGrassBlockBehavior");
            modInjector.Register("Game.SubsystemPlantBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemPlantBlockBehavior");
            modInjector.Register("Game.SubsystemDeciduousLeavesBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemDeciduousLeavesBlockBehavior");
            modInjector.Register("Game.SubsystemCactusBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemCactusBlockBehavior");
            modInjector.Register("Game.SubsystemRotBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemRotBlockBehavior");
            modInjector.Register("Game.SubsystemCarpetBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemCarpetBlockBehavior");
            modInjector.Register("Game.SubsystemFallenLeavesBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemFallenLeavesBlockBehavior");
            modInjector.Register("Game.SubsystemSoilBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemSoilBlockBehavior");
            modInjector.Register("Game.SubsystemIvyBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemIvyBlockBehavior");
            modInjector.Register("Game.SubsystemSaplingBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemSaplingBlockBehavior");
            // 塌落物理（实机：客户端放一格沙子，落地后主机变成两格）：
            //   · SubsystemCollapsingBlockBehavior.cs:22-28 OnNeighborBlockChanged → TryCollapseColumn；
            //   · :86-108 把整列登记成 "CollapsingBlock" 移动组后 ChangeCell(源,0)（:107）；
            //   · :40-73 MovingBlocks 的 CollidedWithTerrain / Stopped 里 DestroyCell（:51/:66），
            //     落地时把目标格放到本地；客户端照跑 = 源删除 + 目标放置都被 CaptureClientEdits
            //     当成玩家编辑提交，主机再自己塌一次 ⇒ 目标格叠加成两格。
            // 客户端外壳摘掉 Stopped/CollidedWithTerrain 订阅（只判停、不写格），最终格只收主机 CellChange。
            // 客户端不跑塌落 ⇒ 中间的下落过程只能在主机上产生（Id=="CollapsingBlock" 的移动组）：
            // 外壳在主机上检测集合开始 / 结束，交给 Engine/PistonReplicaEngine.cs 广播
            // （PistonMoveEvent/PistonMoveEnd，kind=COLLAPSING）；客户端按消息建
            // "CollapsingBlockReplica" 只表现集合（不写格），最终格仍只收主机 CellChange。
            modInjector.Register("Game.SubsystemCollapsingBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemCollapsingBlockBehavior");
            // 营火燃料烧尽：SubsystemCampfireBlockBehavior.cs:44-67（Update → ResizeCampfire →
            // ChangeCell data）、:69-75（邻居 → DestroyCell）、:107-124（区块丢弃 → ChangeCell）。
            modInjector.Register("Game.SubsystemCampfireBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemCampfireBlockBehavior");
            // 熔炉火焰：SubsystemFurnaceBlockBehavior.cs:30-49（OnBlockAdded → CreateEntity/AddEntity +
            // AddFire）、:51-70（OnBlockRemoved → DropAllItems/RemoveEntity）、:72-94（生成 / 区块丢弃）。
            // 客户端 base 会建/删方块实体，所以换成只维护 FireParticleSystem 的外壳 ——
            // 火焰由 FURNACE_STATE（heat/fireTime）与已下发格子值驱动。
            modInjector.Register("Game.SubsystemFurnaceBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemFurnaceBlockBehavior");
            // 草陷阱：SubsystemGrassTrapBlockBehavior.cs:20-32 累计伤害、:34-59（DestroyCell :47）。
            modInjector.Register("Game.SubsystemGrassTrapBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemGrassTrapBlockBehavior");
            // 树叶腐烂：SubsystemWoodBlockBehavior.cs:20-51 登记树叶、:87-106 Update 采样、
            // :110-176 DecayLeavesIfNeeded（ChangeCell :158）、:53-78 区块丢弃时同样写格。
            modInjector.Register("Game.SubsystemWoodBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemWoodBlockBehavior");
            // 宝藏生成器：SubsystemTreasureGeneratorBlockBehavior.cs:399-455 邻居触发后
            // ChangeCell(self,0)（:401）+ AddPickable 大批宝藏（:430-450）。
            modInjector.Register("Game.SubsystemTreasureGeneratorBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemTreasureGeneratorBlockBehavior");
            // 延迟写格总入口：SubsystemCellChangeQueue.cs:45-51 Update → ApplyCellChanges:55-69
            // （ChangeCell）。现有生产者都已被闸门，客户端统一不推进并清 m_toChange。
            modInjector.Register("Game.SubsystemCellChangeQueue",
                "SuMultiplayer.Engine.SuSubsystemCellChangeQueue");
            // 轮询驱动：SubsystemBlocksScanner.cs:35-99 调所有 pollable OnPoll（世界演化定时器入口）。
            // 目标行为已各自有闸门；客户端再关掉驱动，作为未来新增 pollable 的总闸。
            modInjector.Register("Game.SubsystemBlocksScanner",
                "SuMultiplayer.Engine.SuSubsystemBlocksScanner");
            modInjector.Register("Game.SubsystemExplosions",
                "SuMultiplayer.Engine.SuSubsystemExplosions");
            // 爆炸物引信：引擎的引信是**主机私有表**（SubsystemExplosivesBlockBehavior.m_explosiveDataByPoint），
            // 不写格、也不随 CellChange 下发 → 客户端在爆炸前看不到引信在烧（实机问题）。
            // 外壳在主机把"剩余引信 tick"写进火药桶格子 data（Terrain/FuseCellState.cs）走既有下发，
            // 客户端只做表现、不跑 base.Update（不倒计时、不爆炸）；起爆器 data 0..5 是挂载面、
            // 写高位会让方块不渲染，因此起爆器只显式计数+限流日志（见外壳注释）。
            modInjector.Register("Game.SubsystemExplosivesBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemExplosivesBlockBehavior");
            // 拾取物 / 投射物：客户端不跑引擎的拾取结算 / 投射物模拟（它们会改地形、伤人、
            // 生成掉落物、触发爆炸）；主机保留原生模拟，只摘掉远端化身的“本地拾取”。
            // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemPickables.cs
            // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemProjectiles.cs
            modInjector.Register("Game.SubsystemPickables",
                "SuMultiplayer.Engine.SuSubsystemPickables");
            modInjector.Register("Game.SubsystemProjectiles",
                "SuMultiplayer.Engine.SuSubsystemProjectiles");
            // 客户端用爆炸物：投掷 / 火柴点引信改成主机请求；烟花爆开由主机广播 cosmetic 事件。
            // 只有这三个方块行为被替换，其它方块行为不动。
            // Source: Pak/Database.xml（ThrowableBlockBehavior / MatchBlockBehavior / FireworksBlockBehavior 的 Class）
            modInjector.Register("Game.SubsystemThrowableBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemThrowableBlockBehavior");
            modInjector.Register("Game.SubsystemMatchBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemMatchBlockBehavior");
            modInjector.Register("Game.SubsystemFireworksBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemFireworksBlockBehavior");
            // 客户端投掷 / 弓 / 弩 / 火枪：Completed 分支改成主机请求（主机用引擎自己的
            // FireProjectile 生成权威投射物并消费道具 / 耐久）；客户端不本地预测、不本地扣除。
            // Source: Survivalcraft/Game/SubsystemThrowableBlockBehavior.cs:OnAim
            // Source: Survivalcraft/Game/SubsystemBowBlockBehavior.cs:OnAim
            // Source: Survivalcraft/Game/SubsystemCrossbowBlockBehavior.cs:OnAim
            // Source: Survivalcraft/Game/SubsystemMusketBlockBehavior.cs:OnAim
            modInjector.Register("Game.SubsystemBowBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemBowBlockBehavior");
            modInjector.Register("Game.SubsystemCrossbowBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemCrossbowBlockBehavior");
            modInjector.Register("Game.SubsystemMusketBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemMusketBlockBehavior");
            // 客户端丢弃（Q / 手柄 B）：引擎的丢弃分支在 ComponentPlayer.Update 里先扣物品再
            // AddPickable（本地扣除 + 本地实体）。这里替换输入组件，在 ComponentInput.Update
            // 之后、ComponentPlayer.Update 之前把 Drop 意图交给实体复制模块发 PickableDropRequest，
            // 并清掉 Drop 标志 → 客户端不扣物品、不生成会被清理的幽灵拾取物。
            // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.Update（UpdateOrder.Input）
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update（Drop 分支）
            modInjector.Register("Game.ComponentInput",
                "SuMultiplayer.Engine.SuComponentInput");
            // 全局主机声音中继：替换 SubsystemAudio，在 base.Update 排空 m_queuedSounds
            // 之前捕获新增条目（非虚 PlaySound 无法用派生外壳截获，见 Engine/RemoteActionAudioReplica.cs）。
            modInjector.Register("Game.SubsystemAudio",
                "SuMultiplayer.Engine.SuSubsystemAudio");
            // 循环环境音：替换 SubsystemAmbientSounds，客户端在 base.Update 之前施加主机音量；
            // 主机 / 单机外壳直接 base.Update（主机听感与改前完全一致）。
            modInjector.Register("Game.SubsystemAmbientSounds",
                "SuMultiplayer.Engine.SuSubsystemAmbientSounds");
            modInjector.Register("Game.SubsystemElectricity",
                "SuMultiplayer.Engine.SuSubsystemElectricity");
            // 活塞：主机是唯一模拟方（推 / 拉 / 移动方块 / 落地写格）；客户端**不跑活塞逻辑**
            // （ProcessQueuedActions / StopPiston 会 ChangeCell / DestroyCell），只按主机下发的
            // PistonMoveEvent 在本地 SubsystemMovingBlocks 里建只表现集合。
            // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:Update/ProcessQueuedActions/StopPiston
            modInjector.Register("Game.SubsystemPistonBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemPistonBlockBehavior");
            // 移动组防御性兜底（item 3）：活塞 / 塌落的私有碰撞回调直接调**非虚**的
            // SubsystemTerrain.DestroyCell/ChangeCell，外壳 override 拦不住。正常路径由两个外壳把
            // 委托从 SubsystemMovingBlocks 的事件上摘掉；这里再替换驱动本身，客户端在 base.Update
            // 前后用 ClientMovingBlockWriteGuard 快照/恢复"移动组会碰到的格子"并计数
            // （clientShellBaseWriteAttempts / clientBasePistonWriteAttempt）。主机 / 单机直接 base.Update。
            // Source: Survivalcraft/Game/SubsystemMovingBlocks.cs:Update:277-353 TerrainCollision:508-535
            modInjector.Register("Game.SubsystemMovingBlocks",
                "SuMultiplayer.Engine.SuSubsystemMovingBlocks");
            // 可编辑方块内容：引擎在对话框 OK 的回调里直接 SetBlockData（非虚调用、不是可注入的方法），
            // 唯一能同时保留原生对话框并拦住提交的入口是替换行为本身。两个外壳在客户端把提交变成
            // EDITABLE_EDIT_REQUEST（对话框改副本复制品，绝不 SetBlockData），内容只收主机
            // EDITABLE_STATE；主机 / 单机照旧落地权威内容（并广播）。见 Engine/EditableBlockSubsystems.cs。
            // Source: Survivalcraft/Game/SubsystemTruthTableCircuitBlockBehavior.cs:38-53
            // Source: Survivalcraft/Game/SubsystemMemoryBankBlockBehavior.cs:38-53
            modInjector.Register("Game.SubsystemTruthTableCircuitBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemTruthTableCircuitBlockBehavior");
            modInjector.Register("Game.SubsystemMemoryBankBlockBehavior",
                "SuMultiplayer.Engine.SuSubsystemMemoryBankBlockBehavior");
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                // 引擎契约：每个处理器的返回值必须是 [bool 是否替换数据库, Database]。
                // Source: EntitySystem/GameEntitySystem/GameDatabase.cs:GameDatabase ctor
                //     foreach (object[] item in TriggerEvent("GameDatabase.GameDatabase", ...))
                //         if ((bool)item[0]) Database = (Database)item[1];
                // 返回 args（只有 Database 一项）会让 (bool)item[0] 抛 InvalidCastException，
                // 世界加载直接中止 —— 这就是"scmod 加载报错"的真实原因。
                // Source: Mod/GmMod/GmMod.cs:173  return new object[] { false, database };
                if (args == null || args.Length == 0 || !(args[0] is Database database))
                    return new object[] { false, null };
                modInjector.Apply(database, "SuMultiplayer");
                DatabaseApplied = true;
                Log.Information("[SuMP] 引擎注入已应用（SubsystemTerrain 外壳 + 客户端副本闸门：水/岩浆/水下/水草/海参 + 火/草/植物/… + 活塞/电路）");
                return new object[] { false, database };
            }, EventPriority.HIGHEST);

            // Block 替换必须在 BlocksManager.Initialize 事件里 ApplyBlocks（与 Channel 同一模式）。
            eventBus.SubscribeEvent("BlocksManager.Initialize", args =>
            {
                // BlocksManager 会直接索引 item[0]，绝不能返回 null。
                if (args == null || args.Length != 1 || !(args[0] is Block[] blocks))
                    return new object[] { false };
                modInjector.ApplyBlocks(blocks, ModIdentifier);
                return new object[] { false, blocks };
            }, EventPriority.NORMAL);

            // 加入房间入口：替换 Play 屏（旧实现 ScMultiplayerLifecycle.HandleLoading 的同一条路径）。
            eventBus.SubscribeEvent("Loading.Initialize", args =>
            {
                if (args == null || args.Length == 0 || !(args[0] is Type type) ||
                    type.Name != "LoadingManager")
                    return args;
                if (!Game.LoadingManager.ReplaceItem("Initialize PlayScreen", delegate
                    {
                        ScreensManager.AddScreen("Play", new SuPlayScreen(m_sessionModule,
                            m_transferModule.ReadyPort, m_transferModule.DownloadedWorldPort));
                        // 首次加入没有角色时的创建屏（旧 SuNetworkPlayerScreen 流程）。
                        // 取消 = 放弃本次加入（停通道回 Play），不留下半个角色。
                        ScreensManager.AddScreen("SuMultiplayerPlayerProfile",
                            new SuNetworkPlayerScreen(engineModManager.ModParentField, m_nameRoller,
                                delegate { m_sessionModule.LeaveRoom(); },
                                delegate { m_sessionModule.PreserveJoinOnNextPlayEnter(); },
                                m_avatarModule.OnCharacterCreated));
                    }))
                {
                    throw new InvalidOperationException(
                        "没有找到 Loading 项 'Initialize PlayScreen'：Play 屏无法替换，加入房间入口缺失");
                }
                Log.Information("[SuMP] Play 屏已替换：局域网房间会作为世界列表里的行出现（加入房间入口）");
                return args;
            }, EventPriority.HIGHEST);

            // 每帧驱动：模块 tick（宿主负责异常隔离）+ 游戏内 CR 按钮挂载
            eventBus.SubscribeEvent("Frame.Update", args =>
            {
                try
                {
                    // 注意：**不能**在 Project == null 时跳过 tick —— 加入房间发生在主菜单。
                    // 需要世界的部分（地形泵）由 SuSubsystemTerrain.Update 自己驱动。
                    bool inWorld = GameManager.Project != null;
                    try
                    {
                        m_moduleHost?.Tick(new ModuleTickContext(m_frameIndex++, Time.RealTime,
                            Time.FrameDuration, m_sessionModule.IsHostingRoom, inWorld));
                    }
                    catch (Exception e) { NoteFrameStepFailure("module-host", e); }

                    // 编辑玩家屏（PlayerScreen）进入/退出钩子：只比较 ScreensManager.CurrentScreen
                    // 的引用；退出那一帧抓快照并同步名字/皮肤（不轮询玩家资料）。
                    try { m_editWatcher?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("edit-player", e); }

                    try { m_uiAttacher?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("room-ui", e); }

                    try { m_chatOverlay?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("chat-ui", e); }

                    try { m_voiceHud?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("voice-ui", e); }

                    // UI 拖出安全网的"拖出前"背包快照：必须放在所有模块 tick 之后
                    // （到下一帧控件输入之间背包不会再变），否则会把上一帧的扣除误当成拖出。
                    try { m_uiDropAttacher?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("ui-drop", e); }

                    try { m_entityEngine?.CaptureUiDropInventorySnapshot(); }
                    catch (Exception e) { NoteFrameStepFailure("ui-drop-snapshot", e); }

                    // 调试镜像（debug-only）：先让控制器按"配置 / 主机 / 世界"状态创建或拆除
                    // 第 3 个分屏视图，再挂 MIR 小按钮。配置关闭时两者都不产生任何引擎对象；
                    // 离开房间 / 世界卸载 / 关闭开关都在控制器 Tick 里同步拆除。
                    try
                    {
                        m_debugMirror?.Tick(GameManager.Project != null,
                            m_sessionModule != null && m_sessionModule.IsHostingRoom);
                    }
                    catch (Exception e) { NoteFrameStepFailure("debug-mirror", e); }

                    try { m_debugMirrorHud?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("debug-mirror-ui", e); }

                    // 调试镜像角色（debug-only）：主机 + 世界内 + 配置开启时才工作；
                    // 每拍把客户端角色的位置/状态/背包/动作复制到主机本地角色 H3（只显示、不可操作）。
                    // 离开房间 / 世界卸载 / 客户端离开 / 配置关闭都在控制器 Tick 里自动解绑并写回快照。
                    // 仲裁：mirrorcharacterauto 开启时，引擎独占权交给 MirrorHost（它自己按 auto 宿主名绑定）。
                    // 否则两个控制器会抢同一个 MirrorCharacterEngine：显式控制器先绑定 → MirrorHost 侧
                    // TryBind 命中"幂等返回" → replace 的绑视图/隐藏身体动作从未执行（viewBound=0，
                    // 第三屏还是宿主自己的视角）。auto 关闭时才由显式 mirrorcharacter 驱动。
                    bool mirrorHostOwnsEngine = m_config != null && m_config.MirrorCharacterAuto;
                    if (!mirrorHostOwnsEngine)
                    {
                        try
                        {
                            m_mirrorCharacter?.Tick(GameManager.Project != null,
                                m_sessionModule != null && m_sessionModule.IsHostingRoom);
                        }
                        catch (Exception e) { NoteFrameStepFailure("mirror-character", e); }
                    }

                                        // 镜像宿主（默认开启；配置键 mirrorcharacterauto）：客户端加入时主机为它自动创建
                    // 真实本地角色（引擎第 2/3/4 个分屏）并绑定到客户端第一人称；离开时只删 auto 角色。
                    // 必须在 client-view 之前：本拍已经建 / 绑上的宿主会让同客户端的自动视图让位。
                    try
                    {
                        TickMirrorHost(inWorld);
                    }
                    catch (Exception e) { NoteFrameStepFailure("mirror-host", e); }

// 自动客户端分屏（默认开启）：主机在有空槽时为每个加入的客户端建 camera-only
                    // 第一人称视图；客户端离开 / 离开房间 / 世界卸载 / 配置关闭都在控制器 Tick 里拆除。
                    // 放在 mirror-character 之后：本拍已绑定的 H3（replace）会让同客户端的自动视图让位。
                    try
                    {
                        m_clientView?.Tick(GameManager.Project != null,
                            m_sessionModule != null && m_sessionModule.IsHostingRoom);
                    }
                    catch (Exception e) { NoteFrameStepFailure("client-view", e); }
                    // 周期性打一行各模块状态（组合根是唯一同时知道两个模块的地方，
                    // 模块之间不互相读状态）。实机排查全靠这几行。
                    long nowMs = (long)(Time.RealTime * 1000.0);
                    if (nowMs - m_lastStatusMs >= 5000)
                    {
                        m_lastStatusMs = nowMs;
                        Log.Information("[SuMP] " + m_sessionModule?.StatusLine());
                        Log.Information("[SuMP] " + m_terrainModule?.StatusLine());
                        Log.Information("[SuMP] " + m_avatarModule?.StatusLine());
                        Log.Information("[SuMP] " + m_skinModule?.StatusLine());
                        Log.Information("[SuMP] " + m_actionModule?.StatusLine());
                        Log.Information("[SuMP] " + m_statsModule?.StatusLine());
                        Log.Information("[SuMP] " + m_statsPort?.Describe());
                        Log.Information("[SuMP] " + m_inventoryStore?.StatusLine());
                        Log.Information("[SuMP] " + m_explosionModule?.StatusLine());
                        Log.Information("[SuMP] " + m_burnAwayModule?.StatusLine());
                        Log.Information("[SuMP] " + m_campfireModule?.StatusLine());
                        Log.Information("[SuMP] " + m_campfirePresenter?.Describe() + " · " +
                            m_terrainModule?.Bridge.BlockBehaviorNotify.Describe());
                        Log.Information("[SuMP] " + m_furnacePresenter?.Describe());
                        Log.Information("[SuMP] " + m_entityModule?.StatusLine());
                        Log.Information("[SuMP] " + m_blockUseModule?.StatusLine());
                        Log.Information("[SuMP] " + m_furnitureModule?.StatusLine());
                        Log.Information("[SuMP] " + m_editableModule?.StatusLine());
                        Log.Information("[SuMP] " + m_remoteAudioModule?.StatusLine());
                        Log.Information("[SuMP] " + m_ambientSoundModule?.StatusLine());
                        Log.Information("[SuMP] " + m_voiceModule?.StatusLine());
                        Log.Information("[SuMP] " + m_waterSplashModule?.StatusLine());
                        Log.Information("[SuMP] " + m_pistonModule?.StatusLine());
                        Log.Information("[SuMP] " + m_creatureModule?.StatusLine());
                        Log.Information("[SuMP] " + m_creatureWorld?.StatusLine());
                        Log.Information("[SuMP] " + m_nameRoller?.StatusLine());
                        Log.Information("[SuMP] 交互距离：serverMax=" +
                            m_reachGate.ServerMaxMeters.ToString("0.##",
                                CultureInfo.InvariantCulture) + "m · " +
                            m_reachGate.Counters.Describe());
                        Log.Information("[SuMP] " + m_fusePresenter?.Describe());
                        Log.Information("[SuMP] ui-drop: installedDragHosts=" +
                            (m_uiDropAttacher?.InstalledCount ?? 0));
                        Log.Information("[SuMP] HUD: " +
                            (m_voiceHud?.DescribeCounters() ?? "voice-hud=n/a") + " · " +
                            (m_uiAttacher?.DescribeCounters() ?? "room-ui=n/a") +
                            " · frameSteps=" + m_frameStepFailureTotal + " [" +
                            DescribeFrameStepCounters() + "]");
                        if (m_debugMirror != null && m_debugMirror.IsConfigured)
                            Log.Information("[SuMP] " + m_debugMirror.StatusLine() + " · " +
                                (m_debugMirrorHud?.DescribeCounters() ?? "debug-mirror-ui=n/a"));
                        if (m_mirrorCharacter != null && m_mirrorCharacter.IsConfigured)
                            Log.Information("[SuMP] " + m_mirrorCharacter.StatusLine());
                        if (m_mirrorHost != null && m_mirrorHost.IsConfigured)
                            Log.Information("[SuMP] " + m_mirrorHost.StatusLine());
                        if (m_clientView != null && m_clientView.IsConfigured)
                            Log.Information("[SuMP] " + m_clientView.StatusLine());
                        if (m_uiStateModule != null)
                            Log.Information("[SuMP] " + m_uiStateModule.StatusLine() +
                                " · unsupportedTypes=" + m_uiStateModule.DescribeUnsupported());
                    }
                }
                catch (Exception e)
                {
                    NoteFrameStepFailure("frame-driver", e);
                }
                return args;
            }, EventPriority.LOWEST);
        }

        /// <summary>
        /// <summary>
        /// 镜像宿主的每帧驱动（组合根是唯一知道"世界 / 主机"状态的地方）。
        ///   ① 换了 Project → 第一拍补做"清理上一次会话留下的 auto 角色"（只删带标记的前缀命名角色）；
        ///   ② 控制器按池计算 / 就绪 / 离开做创建、绑定、解绑、只删 auto 的清理。
        /// 同一帧只跑一次：ClientJoined 在模块 tick 里到达，控制器下一拍就能看到新客户端。
        /// </summary>
        private void TickMirrorHost(bool inWorld)
        {
            if (m_mirrorHost == null) return;
            Project project = inWorld ? GameManager.Project : null;
            if (!ReferenceEquals(project, m_mirrorHostProject))
            {
                m_mirrorHostProject = project;
                m_mirrorHostReconciled = false;
            }
            if (project != null && !m_mirrorHostReconciled && m_mirrorHostEngine != null)
            {
                m_mirrorHostReconciled = true;
                m_mirrorHostEngine.ReconcileOrphans(Log.Information, m_mirrorHost.IsConfigured);
            }
            m_mirrorHost.Tick(inWorld, m_sessionModule != null && m_sessionModule.IsHostingRoom);
        }

        /// 帧子步骤异常隔离：计数器每帧真实累加，日志按 5 秒限流（同一步骤只写一行）。
        /// 不是静默吞异常：日志里有步骤名、累计次数、异常类型与消息。
        /// </summary>
        private void NoteFrameStepFailure(string step, Exception e)
        {
            if (e == null) return;
            m_frameStepFailures.TryGetValue(step, out long count);
            count++;
            m_frameStepFailures[step] = count;
            m_frameStepFailureTotal++;
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_frameStepLastLogMs.TryGetValue(step, out long last) && nowMs - last < 5000)
                return;
            m_frameStepLastLogMs[step] = nowMs;
            Log.Error("[SuMP] Frame 步骤异常（已隔离，本帧其后的步骤继续）：" + step +
                " 累计=" + count + " 总=" + m_frameStepFailureTotal + "：" +
                e.GetType().Name + " " + e.Message);
        }

        /// <summary>帧子步骤失败计数摘要（5 秒状态行用；无失败时就是 0）。</summary>
        private string DescribeFrameStepCounters()
        {
            if (m_frameStepFailures.Count == 0) return "0";
            var text = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, long> pair in m_frameStepFailures)
            {
                if (text.Length > 0) text.Append(',');
                text.Append(pair.Key).Append('=').Append(pair.Value);
            }
            return text.ToString();
        }

        public void OnUnload()
        {
            try
            {
                ChatAutomation = null;
                m_editWatcher = null;
                // 客户端界面层镜像先下线：趁分屏视图还在把挂上去的面板摘掉、恢复视图原值
                // （输入形态 / HUD 可见），随后再拆视图本身。
                try
                {
                    m_uiStateModule?.Detach(ModuleDetachReason.HostShutdown);
                }
                catch (Exception e)
                {
                    Log.Warning("[SuMP] 卸载客户端界面层镜像失败：" + e.GetType().Name + " " +
                        e.Message);
                }
                m_uiStateModule = null;
                m_uiStateReader = null;
                m_uiMirrorEngine = null;
                // 镜像宿主先下线：趁客户端化身 / 宿主都还活着解绑镜像并**只移除 auto 角色**
                // （手动角色绝不动）。它绑定的是 mirror-character 的引擎，所以必须先于下面那段。
                try
                {
                    m_mirrorHost?.Shutdown("SuMultiplayer 卸载");
                }
                catch (Exception e)
                {
                    Log.Warning("[SuMP] 卸载镜像宿主失败：" + e.GetType().Name + " " + e.Message);
                }
                try
                {
                    m_mirrorHostEngine?.Detach("SuMultiplayer 卸载");
                }
                catch (Exception e)
                {
                    Log.Warning("[SuMP] 卸载镜像宿主引擎失败：" + e.GetType().Name + " " + e.Message);
                }
                m_mirrorHost = null;
                m_mirrorHostEngine = null;
                // 自动客户端分屏先下线：趁客户端化身还在把临时玩家 / 视图拆干净（槽位立即释放）。
                try
                {
                    m_clientView?.Shutdown("SuMultiplayer 卸载");
                }
                catch (Exception e)
                {
                    Log.Warning("[SuMP] 卸载自动客户端分屏失败：" + e.GetType().Name + " " + e.Message);
                }
                try
                {
                    m_clientViewEngine?.Dispose();
                }
                catch (Exception e)
                {
                    Log.Warning("[SuMP] 释放自动客户端分屏引擎失败：" + e.GetType().Name + " " +
                        e.Message);
                }
                m_clientView = null;
                m_clientViewEngine = null;
                // 调试镜像角色先下线：它要在客户端化身 / 主机本地角色都还活着时写回 H3 的绑定前快照。
                try
                {
                    m_mirrorCharacter?.Shutdown("SuMultiplayer 卸载");
                }
                catch (Exception e)
                {
                    Log.Warning("[SuMP] 卸载调试镜像角色失败：" + e.GetType().Name + " " + e.Message);
                }
                try
                {
                    m_mirrorCharacterEngine?.Dispose();
                }
                catch (Exception e)
                {
                    Log.Warning("[SuMP] 释放调试镜像角色引擎失败：" + e.GetType().Name + " " +
                        e.Message);
                }
                m_mirrorCharacter = null;
                m_mirrorCharacterEngine = null;
                // 调试镜像先下线：它要在联机角色还在时把临时玩家 / 第 3 个视图拆干净。
                try
                {
                    m_debugMirror?.Shutdown("SuMultiplayer 卸载");
                }
                catch (Exception e)
                {
                    Log.Warning("[SuMP] 卸载调试镜像失败：" + e.GetType().Name + " " + e.Message);
                }
                m_debugMirrorHud = null;
                m_debugMirror = null;
                m_debugMirrorClient = null;
                m_chatOverlay?.Dispose();
                m_chatOverlay = null;
                // 统计模块先下线：它要在联机角色 / 记录表还活着时把最后的统计 / 成就落盘。
                m_statsModule?.Detach(ModuleDetachReason.HostShutdown);
                m_avatarModule?.Detach(ModuleDetachReason.HostShutdown);
                m_actionModule?.Detach(ModuleDetachReason.HostShutdown);
                m_explosionModule?.Detach(ModuleDetachReason.HostShutdown);
                m_burnAwayModule?.Detach(ModuleDetachReason.HostShutdown);
                m_campfireModule?.Detach(ModuleDetachReason.HostShutdown);
                m_campfireEngine?.Shutdown();
                m_entityModule?.Detach(ModuleDetachReason.HostShutdown);
                m_blockUseModule?.Detach(ModuleDetachReason.HostShutdown);
                m_furnitureModule?.Detach(ModuleDetachReason.HostShutdown);
                m_furnitureEngine?.Shutdown();
                m_remoteAudioModule?.Detach(ModuleDetachReason.HostShutdown);
                m_ambientSoundModule?.Detach(ModuleDetachReason.HostShutdown);
                m_ambientSoundEngine?.Shutdown();
                m_waterSplashModule?.Detach(ModuleDetachReason.HostShutdown);
                m_waterSplashEngine?.Shutdown();
                m_blockUseEngine?.SetRequestSink(null);
                m_editableModule?.Detach(ModuleDetachReason.HostShutdown);
                m_editableBridge?.SetRequestSink(null);
                m_pistonModule?.Detach(ModuleDetachReason.HostShutdown);
                m_pistonEngine?.Shutdown();
                m_explosionEngine?.Shutdown();
                m_burnAwayEngine?.Shutdown();
                m_creatureModule?.Detach(ModuleDetachReason.HostShutdown);
                m_meleeModule?.Detach(ModuleDetachReason.HostShutdown);
                m_authorityModule?.Detach(ModuleDetachReason.HostShutdown);
                m_chatModule?.Detach(ModuleDetachReason.HostShutdown);
                m_voiceHud?.Detach();
                m_voiceModule?.Detach(ModuleDetachReason.HostShutdown);
                m_voiceAudio?.Shutdown();
                m_transferModule?.Detach(ModuleDetachReason.HostShutdown);
                m_sessionModule?.Detach(ModuleDetachReason.HostShutdown);
                m_terrainModule?.Detach(ModuleDetachReason.HostShutdown);
                m_fusePresenter?.Reset();
                m_campfirePresenter?.Reset();
                m_furnacePresenter?.Reset();
                if (m_terrainModule != null)
                {
                    m_terrainModule.Bridge.CampfirePresenter = null;
                    m_terrainModule.Bridge.FurnacePresenter = null;
                    m_terrainModule.Bridge.PistonPresenter = null;
                    m_terrainModule.Bridge.EditableBlocks = null;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                m_moduleHost?.Dispose();
            }
            catch (Exception)
            {
            }
            m_moduleHost = null;
            m_uiAttacher = null;
            m_chatModule = null;
            m_voiceModule = null;
            m_voiceAudio = null;
            m_voiceHud = null;
            Log.Information("[SuMP] " + ModIdentifier + " 已卸载");
        }

        /// <summary>模块日志出口：模块不直接依赖引擎日志。</summary>
        private sealed class GameDiagnostics : IModuleDiagnostics
        {
            public void Info(string moduleId, string message) => Log.Information("[SuMP][" + moduleId + "] " + message);
            public void Warn(string moduleId, string message) => Log.Warning("[SuMP][" + moduleId + "] " + message);
            public void Error(string moduleId, string message, Exception exception) =>
                Log.Error("[SuMP][" + moduleId + "] " + message + (exception == null ? "" : " :: " + exception));
        }
    }
}
