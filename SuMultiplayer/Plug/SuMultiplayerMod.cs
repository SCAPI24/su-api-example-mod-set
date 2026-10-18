using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuAPI;
using TemplatesDatabase;
using SuMultiplayer.Creatures;
using SuMultiplayer.Engine;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.UI;

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
        private CreatureSpawnModule m_creatureModule;
        private CreatureWorldAdapter m_creatureWorld;
        /// <summary>客户端副本 / 主机权威（编辑审批 + 世界信息 + RNG 状态）；组合根装配，模块只拿端口。</summary>
        private ClientReplicaEngine m_clientAuthority;
        private ClientAuthorityModule m_authorityModule;
        /// <summary>客户端引信表现（只读主机下发的火药桶格子 data；主机路径不会进入）。</summary>
        private FuseReplicaPresentation m_fusePresenter;
        private RoomUiAttacher m_uiAttacher;
        private EngineWorldTransfer m_engineWorldTransfer;
        /// <summary>角色创建屏的默认名字摇号（按身份稳定键 + 本次会话盐；计数器见 StatusLine）。
        /// 组合根建一个、随本次会话存活 —— 模块与屏都不持有静态状态。</summary>
        private CharacterNameRoller m_nameRoller;
        private IModEventBus m_eventBus;
        private IModInjector m_injector;
        private SyncConfig m_config;
        private long m_frameIndex;
        private long m_lastStatusMs;
        /// <summary>数据库注入是否已应用（诊断用）。</summary>
        public bool DatabaseApplied { get; private set; }

        public string Name => ModIdentifier;
        public string Version => ModVersionText;
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;
        public IEnumerable<string> Dependencies => Array.Empty<string>();

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            m_eventBus = eventBus;
            m_injector = modInjector;

            m_config = SyncConfig.Load(out string note);
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
            m_avatarModule = new PlayerAvatarModule(avatarSpawner,
                new SuMultiplayerPlayersXml("player-avatar", diagnostics), profileStore);
            // 角色动作 / 手持物 / 背包：引擎适配层只读写远端化身与角色背包，
            // 纯逻辑（动作边沿 / 动画状态 / 背包增量）在 Modules/PlayerActionModule.cs 与 Net/ 里，
            // 因此自检可以直接编译同一份逻辑。背包落盘写角色记录（SuMultiplayerPlayers.xml）。
            m_actionBridge = new AvatarActionBridge(avatarSpawner, engineModManager.ModParentField,
                m_terrainModule.TransportPort, m_terrainModule.ControlPort, diagnostics, "player-action");
            m_inventoryStore = new AvatarInventoryStore(avatarSpawner, m_avatarModule.Records,
                diagnostics, "player-action");
            m_actionModule = new PlayerActionModule(m_actionBridge, m_inventoryStore,
                delegate { return Time.RealTime; });
            // 爆炸同步：主机 = 引擎执行后广播（含链式 / 投掷物 / 烟花）；客户端 = 只做表现。
            // 引擎适配器持有子系统挂接与请求执行，网络模块只做协议 / 排序 / 去重 / 计数。
            m_explosionEngine = new ExplosionReplicaEngine(avatarSpawner,
                engineModManager.ModParentField, engineModManager.ModParentMethod, diagnostics,
                "explosion-sync");
            m_explosionModule = new ExplosionSyncModule(m_explosionEngine);
            // 烧尽表现：引擎 5 秒烧尽分支（SubsystemFireBlockBehavior.cs:227-256）只在本端生成
            // 碎屑/嘶嘶声，不随 CellChange 下发。火外壳上报已处理完的一批格，本模块负责广播；
            // 客户端只做表现（24 m 碎屑 / 16 m 声音，每批 15 / 4，限速）。
            m_burnAwayEngine = new BurnAwayEffectReplicaEngine(avatarSpawner, diagnostics, "effects-sync");
            m_burnAwayModule = new EffectsSyncModule(m_burnAwayEngine);
            // 拾取物 / 投射物：主机是唯一创建/删除方；客户端只建副本，拾取走请求。
            // 引擎适配器负责对象 → 网络 id、主机拾取执行（引擎 AcquireItems）、客户端插值。
            m_entityEngine = new PickableReplicaEngine(avatarSpawner,
                engineModManager.ModParentField, diagnostics, "entity-replication");
            m_entityModule = new EntityReplicationModule(m_entityEngine, m_actionBridge,
                m_inventoryStore);
            // 方块使用 / 容器：客户端在 ComponentPlayer.Update 之前拦下 Interact（不本地执行），
            // 主机校验后执行引擎方块行为 / 提供权威方块实体库存；容器面板在客户端显示副本，
            // 槽位变化走 ContainerEditRequest，主机落地后广播权威 ContainerState。
            m_blockUseEngine = new ContainerReplicaEngine(avatarSpawner,
                engineModManager.ModParentField, engineModManager.ModParentMethod, diagnostics,
                "block-use");
            m_blockUseModule = new BlockUseSyncModule(m_blockUseEngine);
            // 主机 Hello 资料闸门 = 联机角色模块（它拥有角色记录与本地玩家）：
            // 没有记录且 Hello 没带完整资料 → profile_required，不下发世界。
            m_terrainModule.SetHelloProfileGate(m_avatarModule);

            // 生物生成/同步：主机权威生成 + 按客户端区域分配；客户端只做复制。
            // 适配器同时是"客户端区域来源"（联机角色位置）与引擎侧生成子系统注入的持有者。
            m_creatureWorld = new CreatureWorldAdapter(engineModManager.ModParentField,
                engineModManager.ModParentMethod, avatarSpawner, diagnostics);
            m_creatureModule = new CreatureSpawnModule(m_creatureWorld, m_creatureWorld);

            // 客户端副本 / 主机权威：引擎侧服务（编辑捕获、主机权威落地、世界信息、RNG 状态）
            // 只在这里构造一次，模块通过端口访问 —— 模块层不引用 Game。
            m_clientAuthority = new ClientReplicaEngine(m_terrainModule.Bridge);
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
            m_moduleHost.ProvideCapability("transport");
            m_moduleHost.Register(m_sessionModule);
            m_moduleHost.Register(m_transferModule);
            // 联机角色模块要 transport + world.transfer 两个硬依赖，所以最后注册。
            m_moduleHost.Register(m_avatarModule);
            // 动作/背包模块要 transport + player.avatars（由联机角色模块提供）→ 必须在它之后注册。
            m_moduleHost.Register(m_actionModule);
            // 爆炸同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_explosionModule);
            // 烧尽表现同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_burnAwayModule);
            // 实体复制：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_entityModule);
            // 方块使用 / 容器同步：transport 硬依赖；消息处理在 Attach 里注册。
            m_moduleHost.Register(m_blockUseModule);
            m_moduleHost.Register(m_creatureModule);
            // 客户端权威模块：transport 硬依赖；端口缺失时 Attach 会写明确原因（不假装同步）。
            m_moduleHost.Register(m_authorityModule);
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
            ModuleResolution resolution = m_moduleHost.Resolve("OnLoad");

            m_uiAttacher = new RoomUiAttacher(m_sessionModule, m_transferModule.ReadyPort);

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
            Log.Information("[SuMP] " + m_inventoryStore.StatusLine());
            Log.Information("[SuMP] " + m_explosionModule.StatusLine());
            Log.Information("[SuMP] " + m_burnAwayModule.StatusLine());
            Log.Information("[SuMP] " + m_entityModule.StatusLine());
            Log.Information("[SuMP] " + m_blockUseModule.StatusLine());
            Log.Information("[SuMP] " + m_entityEngine.Describe());
            Log.Information("[SuMP] " + m_creatureModule.StatusLine());
            Log.Information("[SuMP] " + m_creatureWorld.StatusLine());
            Log.Information("[SuMP] " + m_authorityModule.StatusLine());
            Log.Information("[SuMP] " + m_nameRoller.StatusLine());
            Log.Information("[SuMP] " + m_fusePresenter.Describe());

            // 引擎注入：把 Game.SubsystemTerrain 换成我们的薄外壳
            modInjector.Register("Game.SubsystemTerrain", "SuMultiplayer.Engine.SuSubsystemTerrain");
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
            modInjector.Register("Game.SubsystemElectricity",
                "SuMultiplayer.Engine.SuSubsystemElectricity");
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
                Log.Information("[SuMP] 引擎注入已应用（SubsystemTerrain 外壳 + 客户端副本闸门：水/岩浆/水下/水草/海参 + 火/草/植物/…）");
                return new object[] { false, database };
            }, EventPriority.HIGHEST);

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
                                delegate { m_sessionModule.PreserveJoinOnNextPlayEnter(); }));
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
                    m_moduleHost?.Tick(new ModuleTickContext(m_frameIndex++, Time.RealTime,
                        Time.FrameDuration, m_sessionModule.IsHostingRoom, inWorld));
                    m_uiAttacher?.Tick();
                    // 周期性打一行各模块状态（组合根是唯一同时知道两个模块的地方，
                    // 模块之间不互相读状态）。实机排查全靠这几行。
                    long nowMs = (long)(Time.RealTime * 1000.0);
                    if (nowMs - m_lastStatusMs >= 5000)
                    {
                        m_lastStatusMs = nowMs;
                        Log.Information("[SuMP] " + m_sessionModule?.StatusLine());
                        Log.Information("[SuMP] " + m_terrainModule?.StatusLine());
                        Log.Information("[SuMP] " + m_avatarModule?.StatusLine());
                        Log.Information("[SuMP] " + m_actionModule?.StatusLine());
                        Log.Information("[SuMP] " + m_inventoryStore?.StatusLine());
                        Log.Information("[SuMP] " + m_explosionModule?.StatusLine());
                        Log.Information("[SuMP] " + m_burnAwayModule?.StatusLine());
                        Log.Information("[SuMP] " + m_entityModule?.StatusLine());
                        Log.Information("[SuMP] " + m_blockUseModule?.StatusLine());
                        Log.Information("[SuMP] " + m_creatureModule?.StatusLine());
                        Log.Information("[SuMP] " + m_creatureWorld?.StatusLine());
                        Log.Information("[SuMP] " + m_nameRoller?.StatusLine());
                        Log.Information("[SuMP] " + m_fusePresenter?.Describe());
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[SuMP] Frame 驱动异常：" + e.GetType().Name + " " + e.Message);
                }
                return args;
            }, EventPriority.LOWEST);
        }

        public void OnUnload()
        {
            try
            {
                m_avatarModule?.Detach(ModuleDetachReason.HostShutdown);
                m_actionModule?.Detach(ModuleDetachReason.HostShutdown);
                m_explosionModule?.Detach(ModuleDetachReason.HostShutdown);
                m_burnAwayModule?.Detach(ModuleDetachReason.HostShutdown);
                m_entityModule?.Detach(ModuleDetachReason.HostShutdown);
                m_blockUseModule?.Detach(ModuleDetachReason.HostShutdown);
                m_blockUseEngine?.SetRequestSink(null);
                m_explosionEngine?.Shutdown();
                m_burnAwayEngine?.Shutdown();
                m_creatureModule?.Detach(ModuleDetachReason.HostShutdown);
                m_authorityModule?.Detach(ModuleDetachReason.HostShutdown);
                m_transferModule?.Detach(ModuleDetachReason.HostShutdown);
                m_sessionModule?.Detach(ModuleDetachReason.HostShutdown);
                m_terrainModule?.Detach(ModuleDetachReason.HostShutdown);
                m_fusePresenter?.Reset();
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
