using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using TemplatesDatabase;
using SuMultiplayer.Creatures;
using SuMultiplayer.Engine;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.Session;
using SuMultiplayer.Terrain;
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
        /// <summary>主机权威复活点表同步（PLAYER_SPAWN=116，方案 B）。</summary>
        private SpawnPointSyncModule m_spawnSyncModule;
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
        private MountReplicaEngine m_mountEngine;
        private MountSyncModule m_mountModule;
        /// <summary>烧尽表现同步：主机 = 引擎 5 秒烧尽批次后广播；客户端 = 只放碎屑 / 嘶嘶声。</summary>
        private BurnAwayEffectReplicaEngine m_burnAwayEngine;
        private EffectsSyncModule m_burnAwayModule;
        /// <summary>方块破坏表现：主机观察器按兴趣窗口广播碎屑/落叶粒子；客户端只重建粒子。</summary>
        private BlockEffectReplicaEngine m_blockEffectEngine;
        private BlockEffectSyncModule m_blockEffectModule;
        /// <summary>拾取物 / 投射物复制：引擎适配（主机抓取/执行拾取、客户端副本）+ 网络模块。</summary>
        private PickableReplicaEngine m_entityEngine;
        private EntityReplicationModule m_entityModule;
        /// <summary>方块使用 / 容器同步：引擎适配（客户端输入拦截 + 副本面板 / 主机权威库存）+ 网络模块。</summary>
        private ContainerReplicaEngine m_blockUseEngine;
        private BlockUseSyncModule m_blockUseModule;
        /// <summary>世界控制：引擎适配（客户端四按钮拦截 + 主机权威执行 + 客户端提示）+ 网络模块。</summary>
        private WorldControlEngine m_worldControlEngine;
        private WorldControlModule m_worldControlModule;
        /// <summary>主机丢失退出：客户端在世界内通道死亡时退房回世界列表（引擎侧端口）。</summary>
        private ClientSessionExit m_clientSessionExit;
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
        /// <summary>
        /// 投射物命中反馈：主机 = <c>SuSubsystemProjectiles.Update</c> 窗口内做血量差捕获，
        /// 按每个客户端的兴趣窗口中继；客户端 = 可见范围 + 区块已载入后只建伤害数字。
        /// </summary>
        private ProjectileHitReplicaEngine m_projectileHitEngine;
        /// <summary>
        /// 逐端兴趣窗口的**唯一只读适配器**：实体复制（逐用户投递）与投射物命中都用它。
        /// 之前它只作为构造参数传给投射物模块、没注册成端口 → EntityReplicationModule 拿不到，
        /// 主机每拍直接返回、一条实体状态都不发（客户端看不到任何掉落物）。现在注册成端口。
        /// </summary>
        private ClientInterestWindowAdapter m_clientInterestWindow;
        private ProjectileHitModule m_projectileHitModule;
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
        /// <summary>MoreContents 的单只 VO 按钮 + VOICE 静音表格（唯一静音入口）。</summary>
        private VoiceMuteTableAttacher m_voiceMuteTable;
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

        /// <summary>
        /// 当前世界的**调试镜像配置**（世界目录里的 SuMultiplayerDebug.json；换世界自动重读）。
        /// 全局 SuMultiplayer.json 不再保存调试镜像开关（用户口径：调试只跟随一张地图）。
        /// </summary>
        private DebugMirrorWorldSettings m_debugMirrorWorldSettings =
            DebugMirrorWorldSettings.None;
        private Project m_debugMirrorSettingsProject;

        /// <summary>
        /// 当前世界的**联机行为设置**（世界目录里的 SuMultiplayerWorld.json；换世界自动重读）：
        /// 目前只有 autoapprovejoins（是否自动同意加入）。默认手动审批。
        /// </summary>
        private volatile WorldJoinSettings m_worldJoinSettings = new WorldJoinSettings();
        private Project m_worldJoinSettingsProject;
        /// <summary>加入审批决定器（主机：手动弹 Allow/Reject，或按世界设置自动同意）。</summary>
        private JoinApprovalUi m_joinApprovalUi;
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
        /// <summary>上一次帧循环看到的联机会话状态：用于在角色日志里写开始/结束边界。</summary>
        private bool m_lastMultiplayerSessionActive;

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

            // 个人服务器（链接添加）入口：与旧 Mod 同一 SuAPI 内容提供商。
            try
            {
                SuAPICore.SuFromLinkProviders.Subscribe(eventBus,
                    new NetWorldFromLinkProvider());
            }
            catch (Exception error)
            {
                Log.Warning("[SuMP][personal] 注册 Add Net World From Link 失败：" +
                    error.GetType().Name + " " + error.Message);
            }
            m_config = SyncConfig.Load(out string note);
            ModLog.Configure(m_config.VerboseLog);
            ProxySettings.Load(out string proxyNote);
            ModLog.Info("[SuMP][proxy] " + proxyNote);
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
                message => ModLog.Info("[SuMP] " + message),
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
            m_sessionModule = new SessionModule(m_config, profileStore, delegate
            {
                // 旧 SuPlayScreen 行字段：大小 / 最后保存时间 / 环境模式 / 序列化版本。
                // 主机/客户端都可能调用（只有主机建房时会真正发布），拿不到就返回未知。
                SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
                if (info == null) return default(RoomWorldDetails);
                WorldInfo world = WorldsManager.WorldInfos.FirstOrDefault(candidate =>
                    string.Equals(candidate.DirectoryName, info.DirectoryName,
                        StringComparison.OrdinalIgnoreCase));
                return new RoomWorldDetails(
                    world?.Size ?? 0L,
                    world?.LastSaveTime ?? DateTime.MinValue,
                    info.WorldSettings.EnvironmentBehaviorMode.ToString(),
                    world?.SerializationVersion ?? VersionsManager.SerializationVersion);
            });
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
            // 世界导出前先把联机角色记录（客户端复活点）落盘，保证中途加入客户端下载到的
            // SuMultiplayerPlayers.xml 带其他端最新 SpawnPosition；不影响原版 Project.xml 路径。
            m_engineWorldTransfer.BeforeExport = delegate
            {
                bool saved = m_avatarModule.Records.SaveIfDirty();
                if (!saved)
                    Log.Warning("[SuMP] 世界导出前保存 SuMultiplayerPlayers.xml 失败（角色记录保持 dirty，后续重试）");
            };
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
            // 2026-10-27：主机上某角色睡醒（引擎 ComponentSleep.WakeUp 已写入最后睡觉位置）时，
            // 立刻让联机角色模块把该角色记录（含复活点 SpawnPosition）落盘到
            // SuMultiplayerPlayers.xml —— 不等 5 秒周期刷新，也不写 Project.xml。
            m_vitalsEngine.AuthoritySpawnPointChanged = delegate (SuMultiplayer.Players.PlayerKey key)
            {
                m_avatarModule.NoteAuthoritySpawnPointChanged(key);
            };
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
            // 方块破坏表现（碎屑 / 秋季落叶粒子）：引擎 DestroyCell 的粒子不随 CellChange 下发，
            // 观察器只上报表现，模块按每个客户端的兴趣窗口逐端投递；客户端只建粒子。
            m_blockEffectEngine = new BlockEffectReplicaEngine();
            m_terrainModule.Bridge.BlockEffects = m_blockEffectEngine;
            m_blockEffectModule = new BlockEffectSyncModule(m_blockEffectEngine);
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
            // 世界控制（创意四按钮：降水/雾/闪电/时间）：客户端把点击变成 WORLD_CONTROL_REQUEST，
            // 主机在权威世界上按引擎同款调用执行；状态变化由既有 WorldInfo 快照（~2 Hz）广播生效，
            // 回执让请求者显示与引擎一致的提示。客户端不本地执行（会被快照拉回）。
            m_worldControlEngine = new WorldControlEngine(avatarSpawner, diagnostics,
                "world-control");
            m_worldControlModule = new WorldControlModule(m_worldControlEngine);
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
            // 投射物命中反馈：引擎的 "-N" 伤害数字只在 ComponentMiner.AttackBody 里创建
            // （ComponentMiner.cs:506-526，投射物命中走 SubsystemProjectiles.cs:221-240），
            // 客户端副本不跑投射物 Update ⇒ 开火者与其他客户端都没有数字。适配器在主机
            // SuSubsystemProjectiles 的 base.Update 前后做"血量差"捕获，模块按每个客户端的
            // 兴趣窗口逐个下发（世界分区：没载入那片地形的端收不到），客户端只建同一个粒子。
            // 受伤闪红是同一份权威血量的表现（PlayerVitalsReplica.ApplyRemoteState），不新增流量。
            m_projectileHitEngine = new ProjectileHitReplicaEngine(avatarSpawner, diagnostics,
                "projectile-hit");
            m_clientInterestWindow = new ClientInterestWindowAdapter(
                delegate (int clientId, out int centerChunkX, out int centerChunkZ,
                    out int radiusChunks)
                {
                    return m_terrainModule.TryGetClientRange(clientId, out centerChunkX,
                        out centerChunkZ, out radiusChunks);
                });
            m_projectileHitModule = new ProjectileHitModule(m_projectileHitEngine,
                m_clientInterestWindow);
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
            // 加入审批（旧联机 mod 的 join request）：玩家端必须先被主机同意（或世界设置自动同意）
            // 才继续握手。拒绝时连接在"建角色记录/建角色实体/世界传输"之前就被移除。
            m_joinApprovalUi = new JoinApprovalUi(this);
            m_terrainModule.SetJoinApproval(m_joinApprovalUi);
            m_terrainModule.JoinRejected += HandleJoinRejected;
            // 加入审批中的客户端断开（Bye / 链路失效）：清掉它在请求队列里的待决条目。
            m_terrainModule.ClientJoinConnectionAborted += HandleClientJoinConnectionAborted;
            m_terrainModule.PendingApprovalAborted += clientId =>
            {
                m_joinApprovalUi?.HandlePendingApprovalAborted(clientId);
            };

            // 生物生成/同步：主机权威生成 + 按客户端区域分配；客户端只做复制。
            // 适配器同时是"客户端区域来源"（联机角色位置）与引擎侧生成子系统注入的持有者。
            m_creatureWorld = new CreatureWorldAdapter(engineModManager.ModParentField,
                engineModManager.ModParentMethod, avatarSpawner, diagnostics);
            // 乘骑控制桥：主机的化身骑乘状态（32Hz motion 的 Riding 位）驱动坐骑 Order。
            avatarSpawner.RidingInputResolver = delegate (int mountClientId, out float turn,
                out float forward, out bool jump)
            {
                turn = 0f;
                forward = 0f;
                jump = false;
                RemoteAnimationAdapter ridingAdapter = m_actionModule?.AnimationAdapter;
                return ridingAdapter != null &&
                    ridingAdapter.TryGetRidingInput(mountClientId, out turn, out forward,
                        out jump);
            };
            m_creatureModule = new CreatureSpawnModule(m_creatureWorld, m_creatureWorld);
            // 乘骑（2026-10-26）：客户端拦骑乘按钮边沿发 MountRequest；主机跑引擎原生
            // FindNearestMount / StartMounting / StartDismounting（拒绝=Rejected），MountState
            // 广播后所有端的化身镜像挂载。AvatarRiderResolver 用化身表解析 clientId→化身。
            m_mountEngine = new MountReplicaEngine(engineModManager.ModParentField,
                m_creatureWorld, diagnostics, "mount-sync")
            {
                AvatarRiderResolver = delegate (int clientId)
                {
                    if (avatarSpawner == null) return null;
                    if (avatarSpawner.TryGet(clientId, out PlayerData data)) return data?.ComponentPlayer;
                    return null;
                }
            };
            m_mountModule = new MountSyncModule(m_mountEngine);
            // 主机：MountState 的 mount_key 必须用主机分配的复制键（生物/船），客户端拿它解析坐骑副本。
            // 旧实现在主机侧只查客户端复制表 → 键=0 → 其他客户端解析不到坐骑（实机：B 看不到 A 骑马）。
            // Source: CreatureSpawnModule.TryGetHostCreatureKey（主机 m_keyByToken 键表）
            m_mountEngine.HostKeyResolver = delegate (Entity entity)
            {
                if (m_creatureModule == null || entity == null) return 0;
                return m_creatureModule.TryGetHostCreatureKey(entity, out int hostKey) ? hostKey : 0;
            };
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
            // 主机丢失退出端口：客户端在世界内通道死亡时退房回世界列表（2026-10-26 产品口径）。
            m_clientSessionExit = new ClientSessionExit();
            m_moduleHost.RegisterPort<IClientSessionExitPort>(m_clientSessionExit);
            // 握手资料视图（主机/分配资料/是否要求先建角色 + 重新握手）：联机角色模块用它。
            m_moduleHost.RegisterPort<IPlayerProfileHandshakePort>(m_terrainModule.ProfileHandshakePort);
            // 实时地图 v9：主机端动物的只读位置来源（只枚举主机世界里的非玩家生物，
            // 不扰动生物同步的 50 Hz 扫描；客户端不贡献动物）。
            m_moduleHost.RegisterPort<IHostCreatureCellSource>(new HostCreatureCellSource());
            // 世界清单的会话标识 / 导出基线由地形模块提供（世界传输模块的硬依赖：
            // 没有它就不发世界，避免客户端拿到没有会话标签的世界、每次加入都整图重下）。
            m_moduleHost.RegisterPort<IWorldSessionPort>(m_terrainModule.WorldSessionPort);
            m_moduleHost.RegisterPort<IWorldTransferBandwidthPort>(
                m_terrainModule.WorldTransferBandwidthPort);
            m_moduleHost.RegisterPort<ITransportControlPort>(m_terrainModule.ControlPort);
            // 客户端权威模块的端口：世界信息广播 / 编辑超时回滚 / 编辑捕获与主机权威落地 / RNG 状态。
            m_moduleHost.RegisterPort<IPeerBroadcastPort>(m_terrainModule);
            // 纯表现类事件（爆炸 / 烧尽 / 营火 / 武器声 …）的"按兴趣窗口投递"：窗口只有地形模块
            // 这一处真相，其它模块不再各自拼广播目标，也不再无脑发给全体客户端。
            m_moduleHost.RegisterPort<IWindowBroadcastPort>(m_terrainModule);
            // 逐端兴趣窗口只读视图（实体复制的逐用户投递 + 投射物命中共用同一个适配器）。
            // 数据源 = 地形模块（客户端 INTEREST_UPDATE），没有第二套窗口口径。
            m_moduleHost.RegisterPort<IClientInterestWindowPort>(m_clientInterestWindow);
            // 每角色记录 hash（Hello 上报 / 加入补发 diff）：世界 pak 里带着角色记录文件，
            // 重连时只重发"不常变内容变了"的角色记录（自定义皮肤资源这类大块数据）。
            m_moduleHost.RegisterPort<IKnownRecordHashPort>(m_terrainModule);
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
            // 投射物命中反馈：模块拿引擎端口（捕获上报 / 客户端建数字）与兴趣窗口只读视图。
            m_moduleHost.RegisterPort<IProjectileHitPort>(m_projectileHitEngine);
            // 可编辑方块内容：模块只拿端口（内容读写 / 身份与距离校验 / 广播目标 / 副本应用）。
            m_moduleHost.RegisterPort<IEditableBlockPort>(m_editableBridge);
            m_moduleHost.ProvideCapability("transport");
            m_moduleHost.Register(m_sessionModule);
            m_moduleHost.Register(m_transferModule);
            // 联机角色模块要 transport + world.transfer 两个硬依赖，所以最后注册。
            m_moduleHost.Register(m_avatarModule);
            // 复活点表：客户端角色记录（SuMultiplayerPlayers.xml）→ 主机权威表 → 全端镜像。
            // 主机本地角色仍以 Project.xml 为权威，不写进联机角色文件。
            m_spawnSyncModule = new SpawnPointSyncModule(m_avatarModule, delegate { return Time.RealTime; });
            m_moduleHost.Register(m_spawnSyncModule);
            m_moduleHost.Register(new PlayerProfileModule(m_avatarModule, m_avatarModule, delegate { return Time.RealTime; })); // 资料运行时同步：PLAYER_PROFILE_SYNC=108（主机权威）
            // 自定义皮肤：记录端口由联机角色模块提供，引擎资源端口由 PlayerSkinStore 提供。
            // 模块硬依赖 transport + player.avatars，所以必须排在联机角色模块之后。
            m_moduleHost.RegisterPort<IPlayerSkinPort>(m_skinStore);
            m_moduleHost.RegisterPort<IPlayerSkinRecordPort>(m_avatarModule);
            // 统计 / 成就：模块只拿引擎端口（本机 PlayerStats / 记录），不直接引用 Game。
            m_moduleHost.RegisterPort<IPlayerStatsPort>(m_statsPort);
            m_skinModule = new PlayerSkinModule(m_skinCounters, profileStore);
            m_moduleHost.RegisterPort<IProfileSkinSyncPort>(m_skinModule); // 资料同步（108）检测到本机换肤 → 同一皮肤资源链（客户端上传 / 主机广播）
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
            m_moduleHost.Register(new PlayerSelfDamageModule(m_vitalsEngine, delegate { return Time.RealTime; })); // 自我伤害（106/107）：客户端只发请求，主机在主机侧化身执行引擎 Injure，107 结果驱动受伤音效 / 红屏
            // 动作/背包模块要 transport + player.avatars（由联机角色模块提供）→ 必须在它之后注册。
            m_moduleHost.Register(m_actionModule);
            // 爆炸同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_explosionModule);
            // 乘骑同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_mountModule);
            // 烧尽表现同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_burnAwayModule);
            m_moduleHost.Register(m_blockEffectModule);
            m_moduleHost.Register(m_campfireModule);
            // 实体复制：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_entityModule);
            // 远程动作声音中继：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_remoteAudioModule);
            // 循环环境音同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_ambientSoundModule);
            // 水花表现同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_waterSplashModule);
            // 投射物命中反馈：transport 硬依赖；主机按兴趣窗口逐个发，客户端只建伤害数字。
            m_moduleHost.Register(m_projectileHitModule);
            // 方块使用 / 容器同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_blockUseModule);
            // 世界控制（创意四按钮）：transport 硬依赖；引擎端口先于模块注册
            //（Attach 里就要拿到；客户端输入外壳的挂接由模块 Tick 驱动引擎 Pump）。
            m_moduleHost.RegisterPort<IWorldControlPort>(m_worldControlEngine);
            m_moduleHost.Register(m_worldControlModule);
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
            m_moduleHost.RegisterPort<IWorldSnapshotInvalidatorPort>(m_transferModule);
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
                engineModManager.ModParentField,
                delegate { return m_chatOverlay != null && m_chatOverlay.HasOpenInput; });
            m_uiStateModule = new UiStateSyncModule(m_uiStateReader, m_uiMirrorEngine);
            m_moduleHost.Register(m_uiStateModule);
            ModuleResolution resolution = m_moduleHost.Resolve("OnLoad");
            // 单机 / 主菜单不跑联机模块的 Tick（不跟踪同步、不写周期状态日志）：
            // session 仍要 Tick 以处理房间发现/创建/加入；terrain 仍要 Tick 以解除旧地形桥。
            m_moduleHost.TickFilter = id =>
                id == "session" || id == "terrain-sync" ||
                (m_sessionModule != null && m_sessionModule.IsMultiplayerActive);

            m_uiAttacher = new RoomUiAttacher(m_sessionModule, m_transferModule.ReadyPort,
                m_joinApprovalUi?.Queue,
                m_joinApprovalUi == null ? (Action)null : m_joinApprovalUi.ShowLatestJoinDialog,
                delegate { return m_worldJoinSettings == null ? 0 : m_worldJoinSettings.HostPort; },
                delegate { return m_worldJoinSettings != null &&
                    m_worldJoinSettings.AutoHostCurrentWorld; },
                delegate { return CaptureHostPeersForUi(); },
                delegate { return CaptureBannedIdsForUi(); },
                delegate (string identity, bool kick) { return BanIdentityForUi(identity, kick); },
                delegate (string identity) { return UnbanIdentityForUi(identity); },
                delegate (int clientId) { return KickPeerForUi(clientId); },
                delegate (int port) { return ChangeHostPortForUi(port); },
                delegate { return m_worldJoinSettings != null &&
                    m_worldJoinSettings.AutoHostCurrentWorld; },
                delegate { return m_worldJoinSettings != null &&
                    m_worldJoinSettings.AutoApproveJoins; },
                delegate (bool value) { return SetAutoHostForUi(value); },
                delegate (bool value) { return SetAutoApproveForUi(value); },
                delegate { return SaveWorldJoinSettingsForUi(); });
            // 引擎 Edit Players（PlayerScreen）进入/退出钩子：名字/皮肤统一走引擎自己的编辑屏。
            // 进入抓快照、退出比较；主机广播改名后的本机角色，客户端按 (clientId, slot) 提交资料。
            m_editWatcher = new PlayerEditWatcher(avatarSpawner, engineModManager.ModParentField,
                m_avatarModule.EditCounters,
                delegate (PlayerEditSnapshot enter, PlayerEditSnapshot exit)
                {
                    m_avatarModule.OnEditScreenExited(enter, exit);
                },
                message => ModLog.Info("[SuMP] " + message),
                message => Log.Warning("[SuMP] " + message),
                message => Log.Error("[SuMP] " + message));
            // UI 拖出到世界：引擎 ViewWidget.DragDrop 会先扣本地背包再本地 AddPickable
            // （客户端物品直接消失）。这里每帧把本机 GameWidget 的 DragHost 换成外壳，
            // 在 DragDrop 之前把意图交给同一条 PickableDropRequest 链 —— 本地不扣、不生成。
            m_uiDropAttacher = new UiDropAttacher(m_entityEngine,
                engineModManager.ModParentField,
                message => ModLog.Info(message),
                message => Log.Warning(message));
            // 调试镜像（debug-only，默认关闭；配置键 debugmirrorclient）：
            //   纯逻辑 = Modules/DebugMirrorPorts.cs（配置闸门 / 目标选择 / 生命周期 / 限流日志 / 计数），
            //   引擎动作 = Engine/DebugMirrorClient.cs（临时 camera-only PlayerData + 第 3 个 GameWidget），
            //   HUD = UI/DebugMirrorHud.cs（MoreContents 里的 MIR 小按钮，只在配置开启 + 主持房间时出现）。
            // 目标从 NetworkAvatarSpawner 的公开 API 读，不碰它的文件；客户端侧永远不创建。
            m_debugMirrorClient = new DebugMirrorClient(avatarSpawner,
                engineModManager.ModParentField, engineModManager.ModParentMethod, diagnostics,
                "debug-mirror");
            // 世界作用域：先按"全关"构造，进世界时读 <世界目录>/SuMultiplayerDebug.json 再应用。
            m_debugMirror = new DebugMirrorController(string.Empty,
                m_debugMirrorClient,
                message => ModLog.Info(message),
                message => Log.Warning(message),
                delegate { return Time.RealTime; });
            m_debugMirrorHud = new DebugMirrorHud(m_debugMirror,
                delegate { return m_sessionModule != null && m_sessionModule.IsHostingRoom; },
                message => ModLog.Info(message),
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
            m_mirrorCharacterEngine.ViewFreezeProbe = m_vitalsEngine.TryGetAuthorityFreeze;
            m_moduleHost.RegisterPort<IViewCameraModePort>(m_mirrorCharacterEngine);
            m_mirrorCharacter = new MirrorCharacterController(string.Empty, string.Empty,
                string.Empty,
                m_mirrorCharacterEngine,
                message => ModLog.Info(message),
                message => Log.Warning(message),
                delegate { return Time.RealTime; });
            // 镜像宿主（**调试功能，默认开启**；配置键 mirrorcharacterauto / mirrorhostnameprefix /
            //   mirrorcharacter + mirrorcharactermode 复用）：
            //   纯逻辑 = Modules/MirrorHostPorts.cs（配置 / 池计算 / 闸门 / 命名 / 复用 / 生命周期 / 计数），
            //   引擎动作 = Engine/MirrorHostEngine.cs（只用公开链路 new PlayerData → AddPlayerData /
            //   RemovePlayerData；绑定复用上面的 MirrorCharacterEngine）。
            //   与显式 mirrorcharacter 的关系：WorldDatabase 里已有同名的 auto 角色 → 复用它；
            //   手动创建的角色（h1/h2/H3）绝不被归入 auto 账本，离开时绝不被删除。
            m_mirrorHostEngine = new MirrorHostEngine(avatarSpawner, m_mirrorCharacterEngine,
                // 免疫写入（免伤 / 免病 / 着火 / 体征归位）走 SuAPI 的反射入口，单独成类注入：
                // 宿主生命周期那条链路保持"只用公开 API"（源码守卫按文件检查）。
                new MirrorHostImmunityEngine(engineModManager.ModParentField, diagnostics),
                diagnostics)
            {
                NamePrefix = DebugMirrorWorldSettings.DefaultNamePrefix
            };
            m_mirrorHost = new MirrorHostController(false,
                DebugMirrorWorldSettings.DefaultNamePrefix, string.Empty, string.Empty,
                m_mirrorHostEngine,
                message => ModLog.Info(message),
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
            // 自动客户端分屏是**调试功能**：默认关闭，只有 &lt;世界目录&gt;/SuMultiplayerDebug.json 里
            // 显式写 clientview 才启用（用户口径 2026-10-21：正常加入时主机端不该默认多出屏幕）。
            m_clientView = new ClientViewController(false, string.Empty,
                m_clientViewEngine,
                message => ModLog.Info(message),
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
                message => ModLog.Info(message),
                message => Log.Warning(message));
            // 自动化钩子：CmdBridge 通过反射读这个属性（本 Mod 不引用 CmdBridge）。
            // 与聊天 HUD 是同一个实例；输入 / 提交走 ChatModule 的同一条发送链。
            ChatAutomation = new ChatAutomation(m_chatOverlay);
            // 语音 HUD：右下角语音状态图标（聊天按钮正上方，唯一的开关；点按 / U 键）+
            // 右上状态行 / TALK（可选 PTT，无键盘绑定）；静音入口是 MoreContents 的 VO 按钮，
            // 点开 VOICE 静音表格（VoiceMuteTableAttacher）。
            // U 键的文本输入抑制由聊天 HUD 的"输入行是否打开"提供（组合根是唯一同时知道
            // 两个 UI 的地方；VoiceHud 不反向依赖 ChatOverlay 类型）。
            m_voiceHud = new VoiceHud(m_voiceModule,
                delegate { return m_chatOverlay != null && m_chatOverlay.HasOpenInput; });
            m_voiceMuteTable = new VoiceMuteTableAttacher(m_voiceModule);

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

            ModLog.Info("[SuMP] " + ModIdentifier + " " + ModVersionText + " 已加载 · " + note);
            ModLog.Info("[SuMP] " + m_config.Describe() + " · 模块 Active=" + resolution.Active +
                " Unavailable=" + resolution.Unavailable + " Faulted=" + resolution.Faulted +
                (resolution.EssentialComplete ? "" : " 必需能力缺失=" + string.Join(",", resolution.EssentialMissing)));
            ModLog.Info("[SuMP] " + m_sessionModule.StatusLine());
            ModLog.Info("[SuMP] " + m_transferModule.StatusLine());
            ModLog.Info("[SuMP] " + m_terrainModule.StatusLine());
            ModLog.Info("[SuMP] " + m_avatarModule.StatusLine());
            ModLog.Info("[SuMP] " + m_actionModule.StatusLine());
            ModLog.Info("[SuMP] " + m_statsModule.StatusLine());
            ModLog.Info("[SuMP] " + m_vitalsModule.StatusLine());
            ModLog.Info("[SuMP] " + m_meleeModule?.StatusLine());
            // 2026-10-26：乘骑同步之前完全没有周期状态行（整轮测试只有 4 条日志，看不到
            // present/applied/stale/MountKey 解析失败），骑马问题无法离线复盘。
            ModLog.Info("[SuMP] " + m_mountModule?.StatusLine());
            ModLog.Info("[SuMP] " + m_statsPort.Describe());
            ModLog.Info("[SuMP] " + m_inventoryStore.StatusLine());
            ModLog.Info("[SuMP] " + m_explosionModule.StatusLine());
            ModLog.Info("[SuMP] " + m_burnAwayModule.StatusLine());
            ModLog.Info("[SuMP] " + m_campfireModule.StatusLine());
            ModLog.Info("[SuMP] " + m_campfirePresenter.Describe() + " · " +
                m_terrainModule.Bridge.BlockBehaviorNotify.Describe());
            ModLog.Info("[SuMP] " + m_furnacePresenter.Describe());
            ModLog.Info("[SuMP] " + m_entityModule.StatusLine());
            ModLog.Info("[SuMP] " + m_blockUseModule.StatusLine());
            ModLog.Info("[SuMP] " + m_furnitureModule.StatusLine());
            ModLog.Info("[SuMP] " + m_editableModule.StatusLine());
            ModLog.Info("[SuMP] " + m_remoteAudioModule.StatusLine());
            ModLog.Info("[SuMP] " + m_ambientSoundModule.StatusLine());
            ModLog.Info("[SuMP] " + m_ambientSoundEngine.Describe());
            ModLog.Info("[SuMP] " + m_waterSplashModule.StatusLine());
            ModLog.Info("[SuMP] " + m_projectileHitModule.StatusLine());
            ModLog.Info("[SuMP] " + m_pistonModule.StatusLine());
            ModLog.Info("[SuMP] " + m_entityEngine.Describe());
            ModLog.Info("[SuMP] " + m_creatureModule.StatusLine());
            ModLog.Info("[SuMP] " + m_creatureWorld.StatusLine());
            ModLog.Info("[SuMP] " + m_authorityModule.StatusLine());
            ModLog.Info("[SuMP] " + m_chatModule.StatusLine());
            ModLog.Info("[SuMP] " + m_voiceModule.StatusLine());
            ModLog.Info("[SuMP] 交互距离：serverMax=" +
                m_reachGate.ServerMaxMeters.ToString("0.##", CultureInfo.InvariantCulture) +
                "m tolerance=" + InteractionReachGate.ToleranceMeters.ToString("0.##",
                    CultureInfo.InvariantCulture) + "m · " + m_reachGate.Counters.Describe());
            ModLog.Info("[SuMP] " + m_nameRoller.StatusLine());
            ModLog.Info("[SuMP] " + m_fusePresenter.Describe());
            ModLog.Info("[SuMP] " + m_debugMirror.StatusLine() +
                (m_debugMirror.IsConfigured
                    ? ""
                    : "（调试镜像默认关闭；在<世界目录>/SuMultiplayerDebug.json里显式打开）"));
            ModLog.Info("[SuMP] " + m_mirrorCharacter.StatusLine() +
                (m_mirrorCharacter.IsConfigured
                    ? ""
                    : "（调试镜像角色默认关闭；在<世界目录>/SuMultiplayerDebug.json里显式打开）"));
            ModLog.Info("[SuMP] " + m_mirrorHost.StatusLine() +
                (m_mirrorHost.IsConfigured
                    ? ""
                    : "（镜像宿主默认关闭；在<世界目录>/SuMultiplayerDebug.json里显式打开）"));
            ModLog.Info("[SuMP] " + m_clientView.StatusLine() +
                (m_clientView.IsConfigured
                    ? ""
                    : "（clientview 已关闭 / 配置写错；默认 true）"));
            ModLog.Info("[SuMP] " + m_uiStateModule.StatusLine() +
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
            // 指南针：无磁铁时按主机权威复活点表 + 最近手部位置选目标（三端同一点）。
            modInjector.RegisterBlock(SuMultiplayer.Engine.CompassBlock.Index, typeof(SuMultiplayer.Engine.CompassBlock), ModIdentifier);
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
            // 世界控制（创意四按钮：降水/雾/闪电/时间）：替换 ComponentGui，在 base.Update
            // （HandleInput 读四按钮）之前**同帧**采集意图发请求并消费标志。不能在
            // ComponentInput(-10) 抢点击：widget 的 IsClicked 每帧由 input.Click 现算
            //（ClickableWidget.Update），-10 读到的是上一帧残留 → 晚一帧且消费不掉当帧置位，
            // 实测客户端本地会先执行一次（本地爆炸 + 复制事件 = 双份特效，2026-10-26）。
            // 主机 / 非客户端没有 sink，纯粹透传 base.Update，行为与原版一致。
            modInjector.Register("Game.ComponentGui",
                "SuMultiplayer.Engine.SuComponentGui");
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
                ModLog.Info("[SuMP] 引擎注入已应用（SubsystemTerrain 外壳 + 客户端副本闸门：水/岩浆/水下/水草/海参 + 火/草/植物/… + 活塞/电路）");
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
                if (!Game.LoadingManager.ReplaceItem("Initialize SettingsCompatibilityScreen", delegate
                    {
                        ScreensManager.AddScreen("SettingsCompatibility",
                            new SuSettingsCompatibilityScreen());
                    }))
                {
                    throw new InvalidOperationException(
                        "没有找到 Loading 项 'Initialize SettingsCompatibilityScreen'：" +
                        "兼容性设置屏无法追加 Network Proxy 行");
                }
                ModLog.Info("[SuMP] Play 屏已替换；兼容性设置屏已追加 Network Proxy 行" +
                    "（代理配置 " + ProxySettings.SettingsPath + "）");
                return args;
            }, EventPriority.HIGHEST);

            // 每帧驱动：模块 tick（宿主负责异常隔离）+ 游戏内 CR 按钮挂载
            eventBus.SubscribeEvent("Frame.Update", args =>
            {
                try
                {
                    // 主机丢失退出：模块 tick 只登记请求（避免在 tick 中途 DisposeProject），
                    // 这里在帧头部真正执行（存盘 → 卸载世界 → 回 Play 屏 → LeaveRoom）。
                    try { m_clientSessionExit?.DrainPendingExit(); }
                    catch (Exception e) { NoteFrameStepFailure("client-session-exit", e); }
                    // 注意：**不能**在 Project == null 时跳过 tick —— 加入房间发生在主菜单。
                    // 需要世界的部分（地形泵）由 SuSubsystemTerrain.Update 自己驱动。
                    bool inWorld = GameManager.Project != null;
                    ModLog.SetRole(m_sessionModule != null && m_sessionModule.IsHostingRoom
                        ? "Server" : "Client");
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

                    // 乘骑：客户端把 MountSink 挂到本机玩家的 SuComponentGui（Project 换新重挂）。
                    try { m_mountEngine?.Pump(m_sessionModule != null &&
                        !m_sessionModule.IsHostingRoom && inWorld); }
                    catch (Exception e) { NoteFrameStepFailure("mount-pump", e); }

                    try { m_uiAttacher?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("room-ui", e); }

                    try { m_chatOverlay?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("chat-ui", e); }

                    try { m_voiceHud?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("voice-ui", e); }

                    try { m_voiceMuteTable?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("voice-table-ui", e); }

                    // UI 拖出安全网的"拖出前"背包快照：必须放在所有模块 tick 之后
                    // （到下一帧控件输入之间背包不会再变），否则会把上一帧的扣除误当成拖出。
                    try { m_uiDropAttacher?.Tick(); }
                    catch (Exception e) { NoteFrameStepFailure("ui-drop", e); }

                    try { m_entityEngine?.CaptureUiDropInventorySnapshot(); }
                    catch (Exception e) { NoteFrameStepFailure("ui-drop-snapshot", e); }

                    // 世界作用域的调试镜像配置：进世界 / 换世界时重读 <世界目录>/SuMultiplayerDebug.json，
                    // 再把结果应用到三个调试镜像控制器（全局配置里不再有这些开关）。
                    try { RefreshDebugMirrorWorldSettings(inWorld); }
                    catch (Exception e) { NoteFrameStepFailure("debug-mirror-config", e); }

                    // 世界作用域的联机设置（加入审批）：换世界重读；文件不存在 = 手动审批。
                    try { RefreshWorldJoinSettings(inWorld); }
                    catch (Exception e) { NoteFrameStepFailure("world-join-settings", e); }

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
                    bool mirrorHostOwnsEngine = m_debugMirrorWorldSettings.MirrorHostAuto;
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
                    // 单机 / 未建房 / 未加入时模块全在空转：周期状态行没有诊断价值，
                    // 只会把 Game.log 和角色日志刷满（用户口径：没开房间不要有输出）。
                    // 只有存在联机会话时才写；需要离线看空转状态时用 verbose 配置。
                    bool multiplayerSessionActive = m_sessionModule != null &&
                        m_sessionModule.IsMultiplayerActive;
                    if (multiplayerSessionActive != m_lastMultiplayerSessionActive)
                    {
                        m_lastMultiplayerSessionActive = multiplayerSessionActive;
                        // 边界标记必须落在正确的 host/client 角色日志里：此处再同步一次角色，
                        // 避免建房发生在 SetRole 之后导致开始标记落进 Client 文件。
                        ModLog.SetRole(m_sessionModule?.IsHostingRoom == true ? "Server" : "Client");
                        ModLog.SessionMarker(multiplayerSessionActive
                            ? "==== multiplayer session start (role=" +
                                (m_sessionModule?.IsHostingRoom == true ? "host" : "client") + ") ===="
                            : "==== multiplayer session end ====");
                    }
                    if (nowMs - m_lastStatusMs >= 5000 &&
                        (multiplayerSessionActive || m_config.VerboseLog))
                    {
                        m_lastStatusMs = nowMs;
                        ModLog.Info("[SuMP] " + m_sessionModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_terrainModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_avatarModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_skinModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_actionModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_statsModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_statsPort?.Describe());
                        ModLog.Info("[SuMP] " + m_inventoryStore?.StatusLine());
                        ModLog.Info("[SuMP] " + m_explosionModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_burnAwayModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_campfireModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_campfirePresenter?.Describe() + " · " +
                            m_terrainModule?.Bridge.BlockBehaviorNotify.Describe());
                        ModLog.Info("[SuMP] " + m_furnacePresenter?.Describe());
                        ModLog.Info("[SuMP] " + m_entityModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_blockUseModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_furnitureModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_editableModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_remoteAudioModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_ambientSoundModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_voiceModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_waterSplashModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_projectileHitModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_pistonModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_creatureModule?.StatusLine());
                        ModLog.Info("[SuMP] " + m_creatureWorld?.StatusLine());
                        ModLog.Info("[SuMP] " + m_nameRoller?.StatusLine());
                        ModLog.Info("[SuMP] 交互距离：serverMax=" +
                            m_reachGate.ServerMaxMeters.ToString("0.##",
                                CultureInfo.InvariantCulture) + "m · " +
                            m_reachGate.Counters.Describe());
                        ModLog.Info("[SuMP] " + m_fusePresenter?.Describe());
                        ModLog.Info("[SuMP] ui-drop: installedDragHosts=" +
                            (m_uiDropAttacher?.InstalledCount ?? 0));
                        ModLog.Info("[SuMP] HUD: " +
                            (m_voiceHud?.DescribeCounters() ?? "voice-hud=n/a") + " · " +
                            (m_uiAttacher?.DescribeCounters() ?? "room-ui=n/a") + " · " +
                            (m_voiceMuteTable?.DescribeCounters() ?? "vo=n/a") +
                            " · frameSteps=" + m_frameStepFailureTotal + " [" +
                            DescribeFrameStepCounters() + "]");
                        if (m_debugMirror != null && m_debugMirror.IsConfigured)
                            ModLog.Info("[SuMP] " + m_debugMirror.StatusLine() + " · " +
                                (m_debugMirrorHud?.DescribeCounters() ?? "debug-mirror-ui=n/a"));
                        if (m_mirrorCharacter != null && m_mirrorCharacter.IsConfigured)
                            ModLog.Info("[SuMP] " + m_mirrorCharacter.StatusLine());
                        if (m_mirrorHost != null && m_mirrorHost.IsConfigured)
                            ModLog.Info("[SuMP] " + m_mirrorHost.StatusLine());
                        if (m_clientView != null && m_clientView.IsConfigured)
                            ModLog.Info("[SuMP] " + m_clientView.StatusLine());
                        if (m_uiStateModule != null)
                            ModLog.Info("[SuMP] " + m_uiStateModule.StatusLine() +
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
        /// <summary>
        /// 进世界 / 换世界时重读**该世界目录**的调试镜像配置（SuMultiplayerDebug.json），
        /// 并应用到三个调试镜像控制器。换世界 = 换文件（不同世界互不沿用调试开关）。
        /// </summary>
        private void RefreshDebugMirrorWorldSettings(bool inWorld)
        {
            Project project = inWorld ? GameManager.Project : null;
            if (ReferenceEquals(project, m_debugMirrorSettingsProject)) return;
            m_debugMirrorSettingsProject = project;
            if (project == null)
            {
                m_debugMirrorWorldSettings = DebugMirrorWorldSettings.None;
                ApplyDebugMirrorWorldSettings("世界已卸载 → 调试镜像全关");
                return;
            }
            string directory = project.FindSubsystem<SubsystemGameInfo>(false)?.DirectoryName ??
                string.Empty;
            m_debugMirrorWorldSettings = DebugMirrorWorldSettings.Load(directory, out string note);
            ApplyDebugMirrorWorldSettings(note);
        }

        private void ApplyDebugMirrorWorldSettings(string note)
        {
            m_debugMirror?.ApplyConfiguration(m_debugMirrorWorldSettings.DebugMirrorClient);
            m_mirrorCharacter?.ApplyConfiguration(m_debugMirrorWorldSettings.MirrorCharacter,
                m_debugMirrorWorldSettings.MirrorCharacterClient,
                m_debugMirrorWorldSettings.MirrorCharacterMode);
            m_mirrorHost?.ApplyConfiguration(m_debugMirrorWorldSettings.MirrorHostAuto,
                m_debugMirrorWorldSettings.MirrorHostNamePrefix,
                m_debugMirrorWorldSettings.MirrorCharacterMode,
                m_debugMirrorWorldSettings.MirrorCharacter);
            if (m_mirrorHostEngine != null)
                m_mirrorHostEngine.NamePrefix = m_debugMirrorWorldSettings.MirrorHostNamePrefix;
            // 自动客户端分屏同属调试开关（世界文件），默认关闭；关闭时立刻拆掉已建的视图。
            m_clientView?.ApplyConfiguration(m_debugMirrorWorldSettings.ClientView,
                m_debugMirrorWorldSettings.ClientViewClient);
            ModLog.Info("[SuMP] 世界调试配置（镜像/自动分屏）：" + note);
        }

        /// <summary>换世界时重读 &lt;世界目录&gt;/SuMultiplayerWorld.json（加入审批设置）。</summary>
        private void RefreshWorldJoinSettings(bool inWorld)
        {
            Project project = inWorld ? GameManager.Project : null;
            if (ReferenceEquals(project, m_worldJoinSettingsProject)) return;
            m_worldJoinSettingsProject = project;
            if (project == null)
            {
                m_worldJoinSettings = new WorldJoinSettings();
                ModLog.Info("[SuMP] 加入审批：世界已卸载 → 回到默认（手动审批）");
                return;
            }
            string directory = project.FindSubsystem<SubsystemGameInfo>(false)?.DirectoryName ??
                string.Empty;
            m_worldJoinSettings = WorldJoinSettings.Load(directory, out string note);
            ModLog.Info("[SuMP] 加入审批配置：" + note);
        }

        private IReadOnlyList<HostPeerInfo> CaptureHostPeersForUi() =>
            m_avatarModule?.CaptureHostPeers() ?? (IReadOnlyList<HostPeerInfo>)Array.Empty<HostPeerInfo>();

        private IReadOnlyList<string> CaptureBannedIdsForUi() =>
            m_worldJoinSettings?.BannedIdentities?.ToArray() ?? Array.Empty<string>();

        private string SaveWorldJoinSettingsForUi()
        {
            string directory = m_worldJoinSettingsProject?
                .FindSubsystem<SubsystemGameInfo>(false)?.DirectoryName ?? string.Empty;
            if (string.IsNullOrEmpty(directory))
                return "No world directory; multiplayer settings were not saved.";
            bool saved = m_worldJoinSettings.Save(directory, out string note);
            ModLog.Info("[SuMP][world-settings] " + note);
            return saved ? "World multiplayer settings saved." :
                "Saving world multiplayer settings failed; see the log.";
        }

        private string BanIdentityForUi(string identity, bool kick)
        {
            if (string.IsNullOrWhiteSpace(identity)) return "User ID is empty; cannot ban.";
            string id = identity.Trim();
            if (!m_worldJoinSettings.IsBanned(id))
            {
                m_worldJoinSettings.BannedIdentities.Add(id);
                m_worldJoinSettings.Save(m_worldJoinSettingsProject?
                    .FindSubsystem<SubsystemGameInfo>(false)?.DirectoryName ?? string.Empty,
                    out _);
            }
            string kickNote = "Added to the ban list (the player is offline; not kicked).";
            if (kick)
            {
                HostPeerInfo peer = CaptureHostPeersForUi()
                    .FirstOrDefault(item => string.Equals(item.Identity, id,
                        StringComparison.OrdinalIgnoreCase));
                if (peer != null)
                {
                    string reason = "Session module is unavailable.";
                    bool kicked = m_sessionModule != null &&
                        m_sessionModule.TryKickClient(peer.ClientId, out reason);
                    kickNote = kicked
                        ? "Banned and kicked clientId=" + peer.ClientId
                        : "Banned, but kicking failed: " + reason;
                }
            }
            string saveNote = SaveWorldJoinSettingsForUi();
            return kickNote + " · " + saveNote;
        }

        private string UnbanIdentityForUi(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) return "User ID is empty.";
            int removed = m_worldJoinSettings.BannedIdentities.RemoveAll(item =>
                string.Equals(item, identity.Trim(), StringComparison.OrdinalIgnoreCase));
            return (removed > 0 ? "Removed from the ban list: " + identity :
                "This user ID is not in the ban list: " + identity) +
                " | " + SaveWorldJoinSettingsForUi();
        }

        private string KickPeerForUi(int clientId)
        {
            if (m_sessionModule == null) return "Session module is unavailable.";
            return m_sessionModule.TryKickClient(clientId, out string reason)
                ? "Kicked clientId=" + clientId + " (not banned)."
                : "Kick failed: " + reason;
        }

        private string ChangeHostPortForUi(int port)
        {
            m_worldJoinSettings.HostPort = port;
            string saveNote = SaveWorldJoinSettingsForUi();
            if (m_sessionModule == null || !m_sessionModule.IsHostingRoom)
                return "Port saved: " + port + " (applies on the next room creation). | " + saveNote;
            SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            string worldName = info?.WorldSettings.Name ?? string.Empty;
            string gameMode = info?.WorldSettings.GameMode.ToString() ?? string.Empty;
            m_sessionModule.CloseRoom();
            if (m_sessionModule.CreateRoom(worldName, gameMode, port, out string reason))
                return "Port changed and room recreated: " + port + ". | " + saveNote;
            return "Port saved, but recreating the room failed: " + reason + " | " + saveNote;
        }

        private string SetAutoHostForUi(bool value)
        {
            m_worldJoinSettings.AutoHostCurrentWorld = value;
            return "Auto host = " + (value ? "on" : "off") + ". | " + SaveWorldJoinSettingsForUi();
        }

        private string SetAutoApproveForUi(bool value)
        {
            m_worldJoinSettings.AutoApproveJoins = value;
            return "Auto approve joins = " + (value ? "on" : "off") + ". | " +
                SaveWorldJoinSettingsForUi();
        }

        /// <summary>
        /// 客户端：主机拒绝加入（HelloAck.rejected / 审批超时）。关闭通道、回 Play 屏并显示原因，
        /// 不再自动重连（重连只会被再拒一次）。主机侧此时没有建任何角色/记录/世界传输。
        /// </summary>
        /// <summary>主机在角色创建阶段踢人 / Ban：关闭角色创建屏并回 Play，不自动重连。</summary>
        private void HandleClientJoinConnectionAborted(string reason)
        {
            ModLog.Warn("[SuMP][join] 主机在角色创建阶段关闭连接：" + (reason ?? string.Empty) +
                " → 取消本次加入");
            try { m_sessionModule?.LeaveRoom(); } catch (Exception) { }
            try
            {
                DialogsManager.HideAllDialogs();
                if (ScreensManager.CurrentScreen is SuNetworkPlayerScreen)
                    ScreensManager.SwitchScreen("Play");
                DialogsManager.ShowDialog(null, new MessageDialog("Multiplayer",
                    "The host closed the connection. Character creation was canceled.",
                    "OK", null, null));
            }
            catch (Exception e)
            {
                ModLog.Warn("[SuMP][join] 退出角色创建屏失败：" + e.GetType().Name + " " + e.Message);
            }
        }

        private void HandleJoinRejected(string reason)
        {
            string text = string.IsNullOrEmpty(reason) ? "(host gave no reason)" : reason;
            // 被 Ban 时把**本机用户 ID** 显示给玩家，便于让主机解 Ban（客户端没有主机菜单入口）。
            if (text.IndexOf("ban", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string own = PlayerIdentity.LocalIdentity() ?? string.Empty;
                text += " | Your user ID: " + (own.Length == 0 ? "(unavailable)" : own);
            }
            Log.Warning("[SuMP][join-approval] 加入被主机拒绝：" + text + " → 回 Play 屏（不再自动重连）");
            try { m_sessionModule?.NotifyJoinRejected(reason); }
            catch (Exception e)
            {
                Log.Warning("[SuMP][join-approval] 重置加入流程失败：" + e.GetType().Name + " " +
                    e.Message + "（通道可能仍在运行；请从 Play 屏重新加入）");
            }
            try
            {
                DialogsManager.HideAllDialogs();
                DialogsManager.ShowDialog(null, new MessageDialog("Join Room",
                    "The host declined your join request: " + text, "OK", null, null));
            }
            catch (Exception e)
            {
                Log.Warning("[SuMP][join-approval] 显示拒绝提示失败：" + e.GetType().Name + " " +
                    e.Message + "（原因已写入日志：加入被拒绝）");
            }
        }

        /// <summary>
        /// 主机加入审批 UI：世界设置 autoapprovejoins=true 时直接放行；否则弹 Allow / Reject。
        /// 审批决议由 Terrain 层按 clientId 落地（同意 = 继续握手；拒绝/超时 = HelloAck.rejected）。
        /// </summary>
        private sealed class JoinApprovalUi : IJoinApprovalDecider
        {
            private readonly SuMultiplayerMod m_owner;
            /// <summary>通用客户端事件请求队列（2026-10-26）：加入审批是第一个用户；
            /// IF 按钮的队列页只认这个组件。加入请求不直接弹窗，先入队。</summary>
            internal readonly Requests.HostRequestQueue Queue = new Requests.HostRequestQueue();
            /// <summary>当前唯一打开的审批对话框；同一时刻最多一个，避免重连/排队时叠出多个同名框。</summary>
            private UI.JoinRequestDialog m_activeDialog;
            private long m_activeEntryId = -1;
            private int m_activeClientId = -1;

            public JoinApprovalUi(SuMultiplayerMod owner) { m_owner = owner; }

            public bool AutoApprove => m_owner.m_worldJoinSettings.AutoApproveJoins;

            public void RequestDecision(int clientId, string name, string identity,
                Action<bool, string> decided)
            {
                string label = string.IsNullOrEmpty(name) ? ("client#" + clientId) : name;
                // Ban 在自动同意/入队之前裁决：被 Ban 的身份即使 autoApprove 打开也必须拒绝。
                if (m_owner.m_worldJoinSettings != null &&
                    m_owner.m_worldJoinSettings.IsBanned(identity))
                {
                    string bannedText = "Banned user ID: " + (identity ?? string.Empty);
                    ModLog.Info("[SuMP][join-approval] 拒绝被 Ban 的用户：clientId=" + clientId +
                        " · " + label + " · 用户ID=" + identity);
                    decided(false, bannedText);
                    return;
                }
                string shortIdentity = string.IsNullOrEmpty(identity)
                    ? "(no identity)"
                    : identity.Substring(0, Math.Min(8, identity.Length));
                // 入队（通用请求队列口径：同端重复 = 旧条目过期、新条目按新时间入队；
                // 满 8 条 = 忽略新请求并立即按 busy 拒绝，客户端可稍后再发）。
                bool enqueued = Queue.TryEnqueue(Requests.HostRequestKind.Join, clientId,
                    label, shortIdentity, Time.RealTime, decided,
                    out Requests.HostRequestEntry entry);
                ModLog.Info("[SuMP][join-approval] 加入请求入队：" + clientId + " · " + label +
                    " · 身份=" + shortIdentity + " → " + (enqueued
                        ? "队列 " + Queue.Describe()
                        : "队列已满（容量 " + Requests.HostRequestQueue.Capacity + "）→ 按 busy 拒绝"));
                if (!enqueued)
                {
                    decided(false, "The host request queue is full; try again later.");
                    return;
                }
                // 队列里的最新一条自动弹出对话框（同一时刻最多一个加入对话框；
                // Later / Esc = 条目留队列，由 IF 按钮的队列页再处理）。
                ShowLatestJoinDialog();
            }

            /// <summary>弹出队列里最新一条加入请求的三按钮对话框（Allow / Later / Reject）。</summary>
            internal void ShowLatestJoinDialog()
            {
                // 同一时刻只允许一个审批框：旧代码每次新请求都 ShowDialog，
                // 客户端断线重连时会留下旧框，再叠一个新框（同一玩家两个提示）。
                if (m_activeDialog != null && DialogsManager.Dialogs.Contains(m_activeDialog)) return;
                m_activeDialog = null;
                m_activeEntryId = -1;
                m_activeClientId = -1;
                Requests.HostRequestEntry entry = Queue.PeekLatest();
                if (entry == null || entry.Kind != Requests.HostRequestKind.Join) return;
                Dispatcher.Dispatch(delegate
                {
                    if (m_activeDialog != null && DialogsManager.Dialogs.Contains(m_activeDialog)) return;
                    var dialog = new UI.JoinRequestDialog(
                        entry.Name + " (" + entry.Identity + ") wants to join this world.",
                        delegate (string kind)
                        {
                            ClearActiveDialog();
                            if (kind == "Allow")
                            {
                                ModLog.Info("[SuMP][join-approval] 主机同意加入：clientId=" +
                                    entry.ClientId + " · " + entry.Name);
                                Queue.Approve(entry.Id, string.Empty);
                                Dispatcher.Dispatch(ShowLatestJoinDialog);
                            }
                            else if (kind == "Reject")
                            {
                                ModLog.Info("[SuMP][join-approval] 主机拒绝加入：clientId=" +
                                    entry.ClientId + " · " + entry.Name);
                                Queue.Reject(entry.Id, "The host rejected the join request.");
                                Dispatcher.Dispatch(ShowLatestJoinDialog);
                            }
                            else
                            {
                                // Later：条目留在队列；不自动重开同一个框，由 IF 队列页再处理。
                                ModLog.Info("[SuMP][join-approval] 主机稍后处理：clientId=" +
                                    entry.ClientId + " · " + entry.Name + "（条目留在队列）");
                                Queue.Hold(entry.Id);
                            }
                        });
                    m_activeDialog = dialog;
                    m_activeEntryId = entry.Id;
                    m_activeClientId = entry.ClientId;
                    DialogsManager.ShowDialog(null, dialog);
                });
            }

            /// <summary>
            /// 加入审批中的连接断开：移除队列条目；如果当前打开的正是这个客户端的框，
            /// 立即关掉它，避免旧框留在屏幕上、随后重连又弹出第二个同名框。
            /// </summary>
            internal void HandlePendingApprovalAborted(int clientId)
            {
                int removed = Queue.RemoveAllFor(clientId);
                bool active = m_activeClientId == clientId &&
                    m_activeDialog != null && DialogsManager.Dialogs.Contains(m_activeDialog);
                if (active)
                {
                    Dispatcher.Dispatch(delegate
                    {
                        if (m_activeDialog != null && DialogsManager.Dialogs.Contains(m_activeDialog))
                            DialogsManager.HideDialog(m_activeDialog);
                        ClearActiveDialog();
                        ShowLatestJoinDialog();
                    });
                }
                if (removed > 0 || active)
                    ModLog.Info("[SuMP][join-approval] clientId=" + clientId +
                        " 断开 → 请求队列移除 " + removed + " 条待决条目" +
                        (active ? "；已关闭对应的审批框" : string.Empty));
            }

            private void ClearActiveDialog()
            {
                m_activeDialog = null;
                m_activeEntryId = -1;
                m_activeClientId = -1;
            }
        }

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
                m_projectileHitModule?.Detach(ModuleDetachReason.HostShutdown);
                m_projectileHitEngine?.Shutdown();
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
                m_voiceMuteTable?.Detach();
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
            m_voiceMuteTable = null;
            ModLog.FlushAll();
            ModLog.Info("[SuMP] " + ModIdentifier + " 已卸载");
        }

        /// <summary>模块日志出口：模块不直接依赖引擎日志。</summary>
        private sealed class GameDiagnostics : IModuleDiagnostics
        {
            public void Info(string moduleId, string message) =>
                ModLog.Info("[" + moduleId + "] " + message);
            public void Warn(string moduleId, string message) =>
                ModLog.Warn("[" + moduleId + "] " + message);
            public void Error(string moduleId, string message, Exception exception) =>
                ModLog.Error("[" + moduleId + "] " + message, exception);
        }
    }
}
