using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Engine;
using Game;
using GameEntitySystem;

namespace SuUndergroundWorld.Core
{
    /// <summary>
    /// 双世界管理器（纯 mod，不改游戏源码）。
    ///
    /// 结构：
    ///   地表世界 = 引擎原本的 Terrain（Regions/），统一坐标 [256,511]
    ///   地下世界 = mod 新建的 Terrain（Regions1/），统一坐标 [0,255]，
    ///              **用引擎自己的世界生成器 + 另一个种子**生成一个完整正常的世界
    ///              （生物群系/树/洞穴都齐全），而不是自制的洞穴地形。
    ///
    /// 接缝（两层相邻，所以换算量是 ±255）：
    ///   向下：地表 localY &lt; 0    → 下界 localY = 254（下界天空），之后继续自由落体到地面
    ///   向上：下界 localY &gt; 255.5 → 地表 localY = 1（地表竖井底部），保留上升动量继续飞
    ///   X/Z 原样保留：地表挖的洞与下界对应列垂直对齐，流体也从这里贯通。
    ///
    /// 实体（动物/掉落物）：两个世界各持一份实体集合。换界时把离开世界的非玩家实体
    /// **移出工程但不销毁**（`Project.RemoveEntity(disposeEntity:false)`）并落盘，进入世界时
    /// 再 `AddEntity` 回来 —— 切换瞬间看到的就是该世界自己的动物与掉落物
    /// （引擎在下界新刷的实体会在下次换界时自动归入下界集合）。
    ///
    /// 复活：持续记录"上一次站得住的位置 + 它属于哪个世界"，死亡复活时回到那里；
    /// 若那个位置在另一个世界，会先把活动世界切回去（复活点跨世界）。
    /// </summary>
    internal sealed class SuWorldManager
    {
        /// <summary>跨接缝换算量：地表 localY=0 与下界 localY=255 是相邻两层 → ±255。</summary>
        public const int CrossOffset = 255;

        /// <summary>下界世界生成器的种子偏移：下界种子 = 地表种子 + 这个值。</summary>
        private const int DeepSeedOffset = 1;

        /// <summary>向下换界后玩家的出现高度（下界天空顶部），之后继续向下掉到地面。</summary>
        /// <summary>
        /// 向下换界的落点（下界侧）。**必须低于向上换界的阈值 UpCrossY**，
        /// 否则玩家传下去之后原地悬停（创造飞行/泡在水里不动）会被立刻判成"越过接缝"再弹回上界
        /// —— 这就是"传下去又被传上来"的原因（实测）。
        /// 取 253.0：比 UpCrossY(253.3) 低 0.3，同时仍在世界最顶上几格，下去后继续自由落体。
        /// </summary>
        private const float DeepEntryY = 253f;

        /// <summary>
        /// 向上换界的高度阈值（下界侧）。
        ///
        /// ⚠️ 这个值必须**低于"在水里能跳到的最高点"**，否则玩家永远回不去：
        ///   · 水帘源在 254 格（MirrorFluidY），水面高度 ≈ 254 + GetLevelHeight(0) ≈ 254.875；
        ///   · 实测：在水里无论怎么跳，脚最多抬到约 **水面 - 0.5 ≈ 254.4**（水里跳不高）。
        /// 取 **253.3**：实测玩家在水帘里**只是浮着**（不跳）时的高度约 253.4~253.6，
        /// 所以这个值保证"浮到最高点就能过"，不需要额外跳一下。
        /// 又比向下换界的落点（DeepEntryY = 253）低 0.3，配合"刚进来 1.5 秒内不判越界"就不会自我触发。
        /// </summary>
        private const float UpCrossY = 253.3f;

        /// <summary>刚掉进下界后的这段时间内不判"向上越界"，避免与向下换界的落点（254）互相触发。</summary>
        private const float EnterGraceSeconds = 1.5f;

        /// <summary>
        /// 越界的运动条件：向上速度超过这个值才算"在往上走"。
        /// 另外"泡在水里"（ImmersionFactor）也算 —— 玩家在水帘里浮着时速度几乎为 0，
        /// 但那是他要的"浮到水面就上去"；而在天空纯悬停（无速度、无水）不应触发（实测 bug）。
        /// </summary>
        private const float UpCrossMinVelocity = 0.3f;

        /// <summary>泡在水里（浸没比例超过它）时，即使速度≈0 也允许向上越界。</summary>
        private const float UpCrossMinImmersion = 0.05f;

        /// <summary>向上换界后的出点（地表竖井底部）。</summary>
        private const float SurfaceExitY = 1f;

        /// <summary>
        /// 是否在下界时**继续给上界补流体模拟**。
        ///
        /// 引擎只模拟"活动世界"的流体（SubsystemFluidBlockBehavior 用的是 SubsystemTerrain.Terrain），
        /// 所以玩家在下界时上界的流体是**冻结**的：你把上界源头堵掉/抽掉以后，井底残留的流动水不会流干，
        /// mod 每帧读到的仍是"那儿有水"，于是下界的水帘与浮力永远停不下来（实测 bug）。
        /// 这里每隔一会儿把活动地形临时指向上界、单跑几拍流体子系统再换回来，让残水按规则流干。
        /// </summary>
        private const bool SimulateInactiveSurfaceFluid = true;
        private const float PhantomFluidInterval = 1.0f;
        private const float PhantomFluidStep = 0.05f;

        /// <summary>返回时允许"自动对准"的最大水平偏差（格）：只差一两格时直接把你挪到井口，而不是硬挡回去。</summary>
        private const int ReturnAlignRadius = 16;

        /// <summary>找不到井时，在多大半径内搜索真正的井口来提示玩家。</summary>
        private const int ReturnHintSearchRadius = 48;

        /// <summary>向上换界保留（不足则补足）的上升速度：换界不清零动量，避免在接缝处被"停住"来回跳图。</summary>
        private const float MinAscendVelocity = 6f;

        /// <summary>换界后等待目标区块就绪的超时（秒）。</summary>
        private const float ChunkReadyTimeout = 8f;

        /// <summary>是否强制下界体温舒适。真世界生成器下温度模型正常，默认关（留作调试开关）。</summary>
        private const bool ForceComfortTemperature = false;

        /// <summary>【调试/自动化】凿井后自动把玩家挪到井口并掉落（正式使用保持 false）。</summary>
        private const bool AutoDemoDrop = false;
        private const float AutoDemoDropDelay = 6f;

        /// <summary>记录"上一次位置"的间隔（秒）。只记录稳稳站住的位置，免得把复活点记在坠落半空。</summary>
        private const bool AutoDemoFluid = false;

        /// <summary>
        /// 【仅演示】上升时若上界同列没有洞，就就地开一个。
        /// **正式版保持 false**：正式版必须"对准地表那口洞才能飞上去"，不允许自动在世界里开新井。
        /// </summary>
        private const bool DemoCarveSurfaceHole = false;

        /// <summary>【调试/自动化】进入下界后自动给一个向上速度，用于验证"向上飞回地表"（正式使用保持 false）。</summary>
        private const bool AutoDemoAscend = false;
        private const float AutoDemoAscendDelay = 6f;
        private const float AutoDemoAscendVelocity = 25f;

        /// <summary>
        /// 下界水帘的生成高度。
        /// ⚠️ 不要用 255（世界最顶层）：那一层放方块时顶面是隐形的、水面不成形，
        /// 实测表现为"下界的水没有浮力/不像真正的水"。放到 254 就一切正常。
        /// </summary>
        private const int MirrorFluidY = 254;

        /// <summary>
        /// 断流方式：true = 用**空气**清掉顶部水源（走 ChangeCell 正常路径，不留方块，推荐）；
        /// false = 照玩家手动的做法改成实心方块（备用，一行即可切换）。
        /// </summary>
        private const bool CutSourceWithAir = true;

        /// <summary>流体连通扫描周期（帧）与列数上限。</summary>
        private const int FluidBridgeIntervalFrames = 10;
        private const int MaxCrossColumns = 64;

        private Project m_project;
        private SubsystemTerrain m_terrain;
        private SubsystemGameInfo m_gameInfo;
        private string m_worldDirectory;

        private SuTerrainWorldSet m_surface;
        private SuTerrainWorldSet m_deep;
        private SuTerrainWorldSet m_active;
        private bool m_initialized;

        private int m_rockIndex;
        private int m_bedrockIndex;
        private int m_waterIndex;
        private bool m_bindFailed;
        private int m_bindAttempts;

        /// <summary>需要做流体连通的列（地表井口与下界同列）。</summary>
        private readonly List<Point2> m_crossColumns = new List<Point2>();

        /// <summary>已在下界顶部镜像过水源的列（列 → 流体 contents），用于地表断流时撤掉。</summary>
        private readonly Dictionary<long, int> m_mirroredFluid = new Dictionary<long, int>();

        /// <summary>已经"通知过流体模拟"的列（避免每帧重复 0→值 唤醒）。</summary>
        private readonly HashSet<long> m_wokenFluid = new HashSet<long>();

        /// <summary>两个世界各自的实体集合：[0]=地表，[1]=下界。换界时整体移出/放回工程。</summary>
        private readonly List<Entity>[] m_worldEntities = { new List<Entity>(), new List<Entity>() };

        /// <summary>
        /// 掉落物与移动方块**不是实体**：它们分别存在 SubsystemPickables.m_pickables 与
        /// SubsystemMovingBlocks.m_movingBlockSets 这两个"世界对象"列表里（引擎自己也不存档它们）。
        /// 所以必须和实体一样按世界分流，否则在下界掉的物品会跟着你回到上界（实测 bug）。
        /// </summary>
        private readonly List<Pickable>[] m_worldPickables = { new List<Pickable>(), new List<Pickable>() };
        private readonly List<object>[] m_worldMovingBlocks = { new List<object>(), new List<object>() };
        private SubsystemWaterBlockBehavior m_waterBehavior;
        private SubsystemMagmaBlockBehavior m_magmaBehavior;
        private double m_nextPhantomFluidAt;
        private bool m_phantomFluidLogged;
        private SubsystemPickables m_subsystemPickables;
        private SubsystemMovingBlocks m_subsystemMovingBlocks;
        private FieldInfo m_pickablesField;
        private FieldInfo m_movingBlockSetsField;

        private bool m_anchorSet;
        private int m_anchorX;
        private int m_anchorZ;
        private float m_surfaceTopAtAnchor;
        private bool m_shaftCarved;
        private double m_demoDropAt;
        private bool m_demoDropDone;
        private double m_demoAscendAt;
        private bool m_demoAscendArmed;
        private bool m_demoAscendDone;
        private bool m_loggedNoAnchor;
        private bool m_loggedSurfaceBlocked;
        private int m_fluidBridgeCounter;

        /// <summary>引擎自己的复活点（初始出生点 / 睡觉用床保存的点）以及它属于哪个世界。mod 跟随并会存盘。</summary>
        private bool m_hasSpawn;
        private bool m_spawnSeeded;
        private bool m_wasSleeping;
        private double m_nextExitHintAt;
        private Vector3 m_spawnPosition;
        private bool m_spawnIsDeep;
        private bool m_wasDead;
        private double m_enterGraceUntil;
        private double m_nextFluidDiagAt;
        /// <summary>每一列井口"上界有没有水"的最后一次读数（上界活动时刷新，存盘跨会话）。</summary>
        private readonly Dictionary<long, int> m_lastSurfaceFluid = new Dictionary<long, int>();



        private int m_solidBlockIndex = -1;
        /// <summary>
        /// 上界只跟踪"切换面所在那一层"的水（上界局部 y=0/1，也就是井底那一层）。
        /// 不然重燃会把上界其它正在流的水也一起"激活"（实测：不该被调用的水流也被调用了）。
        /// </summary>
        private const int SurfaceSeamTrackMaxY = 1;

        private bool IsTrackedFluidLayer(bool deepWorld, int y)
        {
            return deepWorld || y <= SurfaceSeamTrackMaxY;
        }

        /// <summary>每个世界里玩家最后的位置（重进存档时按它恢复，避免被引擎放到错误的世界）。</summary>
        private readonly Dictionary<string, Vector3> m_savedPos = new Dictionary<string, Vector3>();
        private Vector3 m_pendingRestore;
        private double m_pendingRestoreAt;
        private bool m_pendingRestoreValid;
        private bool m_pendingRestoreDeep;
        private double m_nextPosSaveAt;
        private int m_reigniteFrames;
        private SuTerrainWorldSet m_lastActiveWorld;
        /// <summary>换界前记下的"流体待更新格"（世界名, x, y, z）——回来时按这些坐标重新通知流体模拟。</summary>
        private readonly List<(string world, int x, int y, int z)> m_pendingFluidCells
            = new List<(string, int, int, int)>();

        /// <summary>已经"堵过水源"的列（每列只堵一次）。</summary>
        private readonly HashSet<long> m_sourcePlugged = new HashSet<long>();


        private ComponentBody m_waitBody;
        private Vector3 m_waitPosition;
        private Vector3 m_waitReleaseVelocity;
        private double m_waitDeadline;
        private bool m_waiting;
        private bool m_waitGravity;
        private bool m_deepActive;

        public Project Project => m_project;
        public bool IsReady => m_initialized;

        // ───────────────────────── 绑定 / 初始化 ─────────────────────────

        public bool TryBind(Project project)
        {
            if (m_initialized && m_project == project)
            {
                return true;
            }
            if (m_bindFailed && m_project == project)
            {
                return false;
            }
            if (m_project != null)
            {
                Shutdown();
            }

            if (!SuTerrainBinding.IsValid)
            {
                m_bindFailed = true;
                m_project = project;
                Log.Error("[SuUndergroundWorld] 反射绑定 SubsystemTerrain 失败，缺少字段：{0}", SuTerrainBinding.DescribeMissing());
                return false;
            }

            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(false);
            if (terrain == null || gameInfo == null)
            {
                return false;   // 世界还在加载，下一帧再试
            }

            int rock = FindBlockIndex("GraniteBlock", "CobblestoneBlock", "LimestoneBlock");
            int bedrock = FindBlockIndex("BedrockBlock");
            if (rock < 0 || bedrock < 0)
            {
                m_bindAttempts++;
                if (m_bindAttempts <= 3)
                {
                    Log.Warning("[SuUndergroundWorld] 方块表尚未就绪（rock={0} bedrock={1}），稍后重试", rock, bedrock);
                }
                return false;
            }

            m_project = project;
            m_terrain = terrain;
            m_gameInfo = gameInfo;
            m_worldDirectory = gameInfo.DirectoryName;

            m_surface = SuTerrainBinding.Capture(terrain);
            m_surface.Name = "surface";
            m_active = m_surface;

            m_rockIndex = rock;
            m_bedrockIndex = bedrock;
            m_waterIndex = FindBlockIndex("WaterBlock");

            // 掉落物 / 移动方块：引擎把它们的列表放在子系统私有字段里，用反射直接搬（不新增引擎改动）
            m_waterBehavior = project.FindSubsystem<SubsystemWaterBlockBehavior>(false);
            m_magmaBehavior = project.FindSubsystem<SubsystemMagmaBlockBehavior>(false);
            m_subsystemPickables = project.FindSubsystem<SubsystemPickables>(false);
            m_subsystemMovingBlocks = project.FindSubsystem<SubsystemMovingBlocks>(false);
            m_pickablesField = typeof(SubsystemPickables).GetField(
                "m_pickables", BindingFlags.Instance | BindingFlags.NonPublic);
            m_movingBlockSetsField = typeof(SubsystemMovingBlocks).GetField(
                "m_movingBlockSets", BindingFlags.Instance | BindingFlags.NonPublic);

            m_deep = SuTerrainWorldSet.CreateDeep(
                terrain, project, gameInfo.DirectoryName, CreateDeepWorldGenerator(terrain, gameInfo));

            m_initialized = true;
            m_bindFailed = false;
            Log.Information("[SuUndergroundWorld] 双世界已就绪：地表=Regions/（种子 {0}），下界=Regions1/（真世界生成器，种子 {1}）",
                gameInfo.WorldSeed, gameInfo.WorldSeed + DeepSeedOffset);

            // 上次是在下界退出的：直接恢复到下界（否则引擎会把下界的实体/玩家坐标当成地表的）
            if (ReadActiveWorldMarker() == "deep")
            {
                Log.Information("[SuUndergroundWorld] 上次退出在下界 → 本次直接恢复到下界");
                SwitchTo(m_deep, swapEntities: false);
            }

            // 读回我们自己存的复活点（**带世界**）：引擎的 SpawnPosition 只是个坐标，
            // 重进存档时无法判断它属于哪个世界，必须靠我们存盘的记录。
            TryLoadSpawnPoint();
            TryLoadAnchorPoint();
            LoadSurfaceFluid();      // 井口也要读回：否则每进一次存档就会在旁边再凿一口新井
            LoadPlayerPositions();   // 上次在哪个世界、以及那个世界里的玩家位置（引擎只存一个不带世界的坐标）
            return true;
        }

        /// <summary>
        /// 造下界的世界生成器：**和地表同一个生成器类**（跟着地表的世界版本走）+ **不同的种子**。
        /// 生成器在构造时就把种子缓存进 m_seed（TerrainContentsGenerator23.cs:291），
        /// 所以这里临时改 WorldSeed、构造完立刻还原，就得到一个"另一个种子的正常世界"。
        /// </summary>
        private static ITerrainContentsGenerator CreateDeepWorldGenerator(SubsystemTerrain terrain, SubsystemGameInfo gameInfo)
        {
            ITerrainContentsGenerator surfaceGenerator = terrain.TerrainContentsGenerator;
            Type generatorType = surfaceGenerator != null ? surfaceGenerator.GetType() : typeof(TerrainContentsGenerator23);

            FieldInfo seedField = typeof(SubsystemGameInfo).GetField(
                "<WorldSeed>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (seedField == null)
            {
                Log.Warning("[SuUndergroundWorld] 拿不到 WorldSeed 字段，下界将使用与地表相同的种子");
                return (ITerrainContentsGenerator)Activator.CreateInstance(generatorType, terrain);
            }

            object original = seedField.GetValue(gameInfo);
            ITerrainContentsGenerator generator;
            try
            {
                seedField.SetValue(gameInfo, (int)original + DeepSeedOffset);
                generator = (ITerrainContentsGenerator)Activator.CreateInstance(generatorType, terrain);
            }
            finally
            {
                seedField.SetValue(gameInfo, original);
            }
            Log.Information("[SuUndergroundWorld] 下界生成器 = {0}，种子 = 地表({1}) + {2} = {3}",
                generatorType.Name, original, DeepSeedOffset, (int)original + DeepSeedOffset);
            return generator;
        }

        private static int FindBlockIndex(params string[] typeNames)
        {
            Block[] blocks = BlocksManager.Blocks;
            if (blocks == null)
            {
                return -1;
            }
            for (int i = 0; i < blocks.Length; i++)
            {
                Block block = blocks[i];
                if (block == null)
                {
                    continue;
                }
                string name = block.GetType().Name;
                for (int k = 0; k < typeNames.Length; k++)
                {
                    if (name == typeNames[k])
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        // ───────────────────────── 每帧（UpdateOrder=99） ─────────────────────────

        public void Tick()
        {
            if (!m_initialized)
            {
                return;
            }
            ComponentPlayer player = GetPlayer();
            ComponentBody body = player?.ComponentBody;
            if (body == null)
            {
                return;
            }

            ApplyComfortTemperature(player);
            UpdateRespawnTracking(player, body);

            // 重进存档：把玩家放回"上次所在世界 + 那个世界的坐标"。
            // 引擎只保存一个局部坐标、不带世界信息 → 你在下界高空退出、重进会被放到上界高空（实测 bug）。
            if (m_pendingRestoreValid && Time.RealTime >= m_pendingRestoreAt)
            {
                m_pendingRestoreValid = false;
                SwitchTo(m_pendingRestoreDeep ? m_deep : m_surface);
                TeleportAndFreeze(body, m_pendingRestore, Vector3.Zero);
                Log.Information("[SuUndergroundWorld] 恢复到上次所在世界（{0}）与位置：({1:0.0}, {2:0.0}, {3:0.0})",
                    m_pendingRestoreDeep ? "下界" : "地表",
                    m_pendingRestore.X, m_pendingRestore.Y, m_pendingRestore.Z);
            }
            if (Time.RealTime >= m_nextPosSaveAt)
            {
                m_nextPosSaveAt = Time.RealTime + 5.0;
                SavePlayerPositions();
            }


            // 换界后的头几帧：重新点火水帘（流体待更新队列是工程共享的，切世界会把连锁更新弄断）
            if (m_reigniteFrames > 0)
            {
                m_reigniteFrames--;
                ReigniteActiveFluid();
                ReplayPendingFluidUpdates();   // 按换界前记下的坐标把链条接回去
            }

            // 给"当前不活动的那个世界"补流体模拟：**两个方向都要补**。
            // 否则你在上界时，下界那根已经生成的残留水柱会被冻结、永远流不干（实测 bug：
            // 上界水撤了、下界的水柱还挂在那里）。
            if (SimulateInactiveSurfaceFluid && Time.RealTime >= m_nextPhantomFluidAt)
            {
                m_nextPhantomFluidAt = Time.RealTime + PhantomFluidInterval;
                PumpInactiveWorldFluid();
            }


            if (m_waiting)
            {
                HandleWaiting(body);
                return;
            }

            Vector3 position = body.Position;
            if (!m_deepActive)
            {
                if (AutoDemoDrop && m_shaftCarved && !m_demoDropDone && Time.RealTime >= m_demoDropAt)
                {
                    m_demoDropDone = true;
                    body.Velocity = Vector3.Zero;
                    body.Position = new Vector3(m_anchorX + 0.5f, m_surfaceTopAtAnchor + 3f, m_anchorZ + 0.5f);
                    Log.Information("[SuUndergroundWorld] 演示模式：玩家已移到井口，开始下落");
                    return;
                }
                if (position.Y < -0.5f)
                {
                    if (m_anchorSet)
                    {
                        EnterDeep(body, position);
                        return;
                    }
                    if (!m_loggedNoAnchor)
                    {
                        m_loggedNoAnchor = true;
                        Log.Warning("[SuUndergroundWorld] 玩家已掉到 y={0:0.0}（越过 y=0），"
                            + "但锚点尚未就绪（地表锚点区块未加载完成），本帧不换界；稍后仍会生效", position.Y);
                    }
                    return;
                }
            }
            else
            {
                bool risingOrWet = body.Velocity.Y > UpCrossMinVelocity
                    || body.ImmersionFactor > UpCrossMinImmersion;
                if (position.Y > UpCrossY && risingOrWet && Time.RealTime >= m_enterGraceUntil)
                {
                    // "在水里浮到最高点"就算上去（泡在水里时速度≈0 也算）；
                    // 但在下界天空纯悬停（无速度、没碰水）不算 —— 否则传下来原地不动会被弹回上界（实测）。
                    ExitToSurface(body, position);
                    return;
                }
                if (position.Y < -50f)
                {
                    RecoverFromVoid(body);
                    return;
                }
                if (AutoDemoAscend)
                {
                    // 演示顺序：先**落到下界地面**站稳，再等一会儿才上升（否则刚进下界就飞走，看不到地貌）
                    bool landed = body.StandingOnValue.HasValue || body.StandingOnBody != null;
                    if (!m_demoAscendArmed && landed)
                    {
                        m_demoAscendArmed = true;
                        m_demoAscendAt = Time.RealTime + AutoDemoAscendDelay;
                        Log.Information("[SuUndergroundWorld] 演示模式：已落到下界地面 ({0:0.0}, {1:0.0}, {2:0.0})，{3} 秒后开始上升",
                            body.Position.X, body.Position.Y, body.Position.Z, AutoDemoAscendDelay);
                    }
                    if (m_demoAscendArmed && Time.RealTime >= m_demoAscendAt)
                    {
                        body.Velocity = new Vector3(0f, AutoDemoAscendVelocity, 0f);
                        if (!m_demoAscendDone)
                        {
                            m_demoAscendDone = true;
                            Log.Information("[SuUndergroundWorld] 演示模式：开始上升，验证向上飞回地表");
                        }
                    }
                }
            }

            if (!m_anchorSet)
            {
                TrySetupAnchor(body);
                return;
            }
            if (!m_shaftCarved)
            {
                TryCarveShaft();
            }

            m_fluidBridgeCounter++;
            if (m_fluidBridgeCounter >= FluidBridgeIntervalFrames)
            {
                m_fluidBridgeCounter = 0;
                UpdateFluidBridge();
            }
        }

        private void ApplyComfortTemperature(ComponentPlayer player)
        {
            if (!ForceComfortTemperature)
            {
                return;
            }
            ComponentVitalStats vitals = player?.ComponentVitalStats;
            if (vitals == null)
            {
                return;
            }
            Program.ModManager.ModParentField.ModifyParentField(vitals, "m_temperature", 12f, typeof(ComponentVitalStats));
            Program.ModManager.ModParentField.ModifyParentField(vitals, "m_targetTemperature", 12f, typeof(ComponentVitalStats));
        }

        private List<PlayerData> GetPlayersData()
        {
            SubsystemPlayers players = m_project.FindSubsystem<SubsystemPlayers>(false);
            return players == null ? new List<PlayerData>() : players.PlayersData.ToList();
        }

        private PlayerData GetPlayerData()
        {
            return GetPlayersData().FirstOrDefault();
        }

        private ComponentPlayer GetPlayer()
        {
            return GetPlayerData()?.ComponentPlayer;
        }

        // ───────────────────────── 复活点（回到上一次位置，可能跨世界） ─────────────────────────

        private void UpdateRespawnTracking(ComponentPlayer player, ComponentBody body)
        {
            ComponentHealth health = player?.ComponentHealth;
            if (health == null)
            {
                return;
            }
            if (health.Health > 0.01f)
            {
                if (m_wasDead)
                {
                    m_wasDead = false;
            m_enterGraceUntil = 0.0;
            m_nextFluidDiagAt = 0.0;
                    RespawnAtSavedSpawn(body);
                    return;
                }
                // 复活点只在**引擎真正保存它的那一刻**更新：睡醒（ComponentSleep.WakeUp → SpawnPosition）。
                // ⚠️ 不能再"只要 SpawnPosition 变了就重新记录"：引擎出生流程里的
                //    FindNoIntroSpawnPosition（PlayerData.cs:221-227）会按**当前活动世界**的地形微调它，
                //    重进存档时我们恢复成下界 → 引擎拿上界坐标在下界地形上微调 → 数值变了 →
                //    就被我们误记成"下界复活点"（实测 bug）。
                ComponentSleep sleep = player?.ComponentSleep;
                bool sleeping = sleep != null && sleep.IsSleeping;
                if (m_wasSleeping && !sleeping)
                {
                    PlayerData data = GetPlayerData();
                    if (data != null)
                    {
                        m_spawnPosition = data.SpawnPosition;
                        m_spawnIsDeep = m_deepActive;
                        m_spawnSeeded = true;
                        m_hasSpawn = true;
                        SaveSpawnPoint();
                        Log.Information("[SuUndergroundWorld] 复活点已更新（睡醒保存）：({0:0.0}, {1:0.0}, {2:0.0})（{3} 世界）",
                            m_spawnPosition.X, m_spawnPosition.Y, m_spawnPosition.Z, m_spawnIsDeep ? "下界" : "地表");
                    }
                }
                m_wasSleeping = sleeping;

                // 完全没有历史记录时（首次装 mod / 存盘丢了）：把引擎当前复活点按"地表"播种一次
                if (!m_spawnSeeded)
                {
                    PlayerData data = GetPlayerData();
                    if (data != null)
                    {
                        m_spawnPosition = data.SpawnPosition;
                        m_spawnIsDeep = false;
                        m_spawnSeeded = true;
                        m_hasSpawn = true;
                        SaveSpawnPoint();
                        Log.Information("[SuUndergroundWorld] 复活点首次记录（按地表归属）：({0:0.0}, {1:0.0}, {2:0.0})",
                            m_spawnPosition.X, m_spawnPosition.Y, m_spawnPosition.Z);
                    }
                }
            }
            else if (!m_wasDead)
            {
                m_wasDead = true;
                // 死亡时**不切世界、不挪动**：尸体留在死亡的那一侧，等玩家点"复活"再回复活点所在的世界
                Log.Information("[SuUndergroundWorld] 玩家死亡：尸体留在{0}世界；复活点{1}",
                    m_deepActive ? "下界" : "地表",
                    m_hasSpawn ? (m_spawnIsDeep ? "在下界" : "在地表") : "尚未记录");
            }
        }

        /// <summary>
        /// 玩家**点了复活之后**才走到这里：把活动世界切回"复活点所在的世界"，并把玩家放到复活点。
        /// 跨世界复活发生在这个时刻，而不是死亡瞬间（尸体留在原地等复活）。
        /// </summary>
        private void RespawnAtSavedSpawn(ComponentBody body)
        {
            if (!m_hasSpawn)
            {
                Log.Warning("[SuUndergroundWorld] 复活：还没记录到复活点，交给引擎默认处理");
                return;
            }
            if (m_spawnIsDeep != m_deepActive)
            {
                Log.Information("[SuUndergroundWorld] 复活点跨世界：切回{0}世界", m_spawnIsDeep ? "下界" : "地表");
                SwitchTo(m_spawnIsDeep ? m_deep : m_surface);
            }
            Log.Information("[SuUndergroundWorld] 复活：回到{0}世界的复活点 ({1:0.0}, {2:0.0}, {3:0.0})",
                m_spawnIsDeep ? "下界" : "地表", m_spawnPosition.X, m_spawnPosition.Y, m_spawnPosition.Z);
            PlayerData data = GetPlayerData();
            if (data != null)
            {
                data.SpawnPosition = m_spawnPosition;   // 校正引擎重生流程对坐标做的微调，保持在记录的世界里
            }
            TeleportAndFreeze(body, m_spawnPosition, Vector3.Zero);
        }

        // ───────────────────────── 锚点 / 竖井 ─────────────────────────

        private void TrySetupAnchor(ComponentBody body)
        {
            int ax = Terrain.ToCell(body.Position.X) + 6;    // 竖井放在玩家旁边 6 格，避免一进世界就掉下去
            int az = Terrain.ToCell(body.Position.Z);
            TerrainChunk chunk = m_surface.Terrain.GetChunkAtCell(ax, az);
            if (chunk == null || chunk.State != TerrainChunkState.Valid)
            {
                return;
            }
            int groundTop = m_surface.Terrain.CalculateTopmostCellHeight(ax, az);
            if (groundTop <= 0)
            {
                return;
            }

            m_anchorX = ax;
            m_anchorZ = az;
            m_surfaceTopAtAnchor = groundTop;
            RegisterCrossColumns(ax, az);
            m_anchorSet = true;
            m_shaftCarved = false;
            SaveAnchorPoint();
            Log.Information("[SuUndergroundWorld] 锚点已定：竖井中心 ({0},{1})，地表高度 {2}", ax, az, groundTop);
        }

        private void TryCarveShaft()
        {
            TerrainChunk chunk = m_surface.Terrain.GetChunkAtCell(m_anchorX, m_anchorZ);
            if (chunk == null || chunk.State != TerrainChunkState.Valid)
            {
                return;
            }
            int top = m_surface.Terrain.CalculateTopmostCellHeight(m_anchorX, m_anchorZ);
            if (top < 8)
            {
                // 高度图没就绪（实测刚换世界时可能返回 1）→ 等下一帧，别凿出"深 2 格"的假井
                return;
            }

            // 这一列本来就是通的（井已存在 / 玩家自己挖过）→ 什么都不用做，别重复凿
            bool alreadyOpen = true;
            for (int dx = -1; dx <= 1 && alreadyOpen; dx++)
            {
                for (int dz = -1; dz <= 1 && alreadyOpen; dz++)
                {
                    for (int y = 0; y <= 4; y++)
                    {
                        if (m_surface.Terrain.GetCellContents(m_anchorX + dx, y, m_anchorZ + dz) != 0)
                        {
                            alreadyOpen = false;
                            break;
                        }
                    }
                }
            }
            if (alreadyOpen)
            {
                m_shaftCarved = true;
                SaveAnchorPoint();
                Log.Information("[SuUndergroundWorld] 竖井已存在（({0},{1}) 本来通到 y=0），跳过凿井", m_anchorX, m_anchorZ);
                return;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int y = 0; y <= top; y++)
                    {
                        WriteCellDirect(m_surface.Terrain, m_anchorX + dx, y, m_anchorZ + dz, 0);
                    }
                }
            }
            InvalidateAround(m_surface, m_anchorX, m_anchorZ);
            m_shaftCarved = true;
            SaveAnchorPoint();
            m_demoDropAt = Time.RealTime + AutoDemoDropDelay;
            if (AutoDemoFluid && m_waterIndex > 0)
            {
                WriteCellDirect(m_surface.Terrain, m_anchorX, 1, m_anchorZ,
                    Terrain.MakeBlockValue(m_waterIndex));
                InvalidateAround(m_surface, m_anchorX, m_anchorZ);
                Log.Information("[SuUndergroundWorld] 演示模式：在井底 ({0},1,{1}) 放了一格水，用于验证流体连通",
                    m_anchorX, m_anchorZ);
            }
            Log.Information("[SuUndergroundWorld] 竖井已凿通地表→y=0（3x3，深 {0} 格）：({1},{2})；"
                + "掉进去即换界（下界同 X/Z 的 {3:0} 高度），从下界同一列飞到顶也能回来",
                top + 1, m_anchorX, m_anchorZ, DeepEntryY);
        }

        /// <summary>把 (x,z) 3x3 登记为"接缝列"（流体连通用）。</summary>
        private void RegisterCrossColumns(int x, int z)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    int cx = x + dx;
                    int cz = z + dz;
                    if (m_crossColumns.Count < MaxCrossColumns && !ContainsCrossColumn(cx, cz))
                    {
                        m_crossColumns.Add(new Point2(cx, cz));
                    }
                }
            }
        }

        private bool ContainsCrossColumn(int x, int z)
        {
            for (int i = 0; i < m_crossColumns.Count; i++)
            {
                if (m_crossColumns[i].X == x && m_crossColumns[i].Y == z)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>世界索引：0 = 地表，1 = 下界（实体/掉落物/移动方块的分流用）。</summary>
        private int IndexOf(SuTerrainWorldSet world)
        {
            return world == m_deep ? 1 : 0;
        }

        private static string WorldName(int index)
        {
            return index == 0 ? "地表" : "下界";
        }

        /// <summary>流体镜像用的列键（把 (x,z) 压成一个 long）。</summary>
        private static long MirrorKey(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }

        /// <summary>是不是流体（contents）。用引擎自己的 FluidBlocks 表判断，和引擎口径一致。</summary>
        private static bool IsFluid(int contents)
        {
            return contents >= 0 && contents < BlocksManager.FluidBlocks.Length
                && BlocksManager.FluidBlocks[contents] != null;
        }

        private static bool WriteCellDirect(Terrain terrain, int x, int y, int z, int value)
        {
            if (y < 0 || y > 255)
            {
                return false;
            }
            TerrainChunk chunk = terrain.GetChunkAtCoords(x >> 4, z >> 4);
            if (chunk == null)
            {
                return false;   // 目标区块尚未加载：交给下一轮，避免反复重试与刷日志
            }
            chunk.SetCellValueFast(x & 0xF, y, z & 0xF, value);
            chunk.ModificationCounter++;
            return true;
        }

        private static void InvalidateAround(SuTerrainWorldSet world, int x, int z)
        {
            world.Updater.DowngradeChunkNeighborhoodState(
                new Point2(x >> 4, z >> 4), 1, TerrainChunkState.InvalidLight, forceGeometryRegeneration: true);
        }

        // ───────────────────────── 换界（坐标映射 ±255） ─────────────────────────

        private void EnterDeep(ComponentBody body, Vector3 position)
        {
            int bx = Terrain.ToCell(position.X);
            int bz = Terrain.ToCell(position.Z);
            RegisterCrossColumns(bx, bz);

            // 出现在下界天空顶部，之后继续自由落体到下界地面（X/Z 与地表完全一致）
            var entry = new Vector3(position.X, DeepEntryY, position.Z);
            SwitchTo(m_deep);
            m_enterGraceUntil = Time.RealTime + EnterGraceSeconds;
            m_demoAscendAt = Time.RealTime + AutoDemoAscendDelay;
            m_demoAscendDone = false;
            m_demoAscendArmed = false;
            Log.Information("[SuUndergroundWorld] 进入地下世界：地表 localY={0:0.0} → 下界 localY={1:0.0}"
                + "（同 X/Z ({2},{3})；从天上继续往下掉到下界地面）", position.Y, DeepEntryY, bx, bz);
            TeleportAndFreeze(body, entry, Vector3.Zero);
        }

        private void ExitToSurface(ComponentBody body, Vector3 position)
        {
            int bx = Terrain.ToCell(position.X);
            int bz = Terrain.ToCell(position.Z);
            // 正式版：必须"对准地表那口洞"才能上去（DemoCarveSurfaceHole=false）。
            bool open = IsSurfaceColumnOpen(bx, bz);
            if (!open && DemoCarveSurfaceHole)
            {
                open = TryCarveSurfaceHole(bx, bz);
            }
            if (!open)
            {
                // 只差一两格就自动对准（否则玩家会觉得"通道关了"）：真正找的是地表那口井，不是凭空开洞
                if (TryFindSurfaceHole(bx, bz, ReturnAlignRadius, out int hx, out int hz))
                {
                    float nudge = (float)Math.Sqrt((hx - bx) * (hx - bx) + (hz - bz) * (hz - bz));
                    position = new Vector3(hx + 0.5f, position.Y, hz + 0.5f);
                    body.Position = position;
                    Log.Information("[SuUndergroundWorld] 自动对准井口：({0},{1}) → ({2},{3})（水平纠偏 {4:0.0} 格）",
                        bx, bz, hx, hz, nudge);
                    bx = hx;
                    bz = hz;
                    open = true;
                }
            }
            if (!open)
            {
                // 地表那一列的区块**根本没加载**（在下界时总是如此）且我们也没有已知井口：
                // 这时"按回下界"会把玩家永久困在下界，所以给一条明确记录的逃生路径 —— 回到上界的复活点。
                TerrainChunk surfaceChunk = m_surface.Terrain.GetChunkAtCell(bx, bz);
                bool surfaceKnown = surfaceChunk != null && surfaceChunk.State >= TerrainChunkState.InvalidLight;
                if (!surfaceKnown && m_hasSpawn && !m_spawnIsDeep)
                {
                    Log.Information("[SuUndergroundWorld] 返回通道：地表 ({0},{1}) 区块未加载且无已知井口 → "
                        + "直接送回上界复活点 ({2:0.0}, {3:0.0}, {4:0.0})",
                        bx, bz, m_spawnPosition.X, m_spawnPosition.Y, m_spawnPosition.Z);
                    SwitchTo(m_surface);
                    TeleportAndFreeze(body, m_spawnPosition, Vector3.Zero);
                    return;
                }

                // 软天花板：只把上升速度清零、压回 255 以下，不要把人猛摔下去（否则像"反复被弹回 255"）
                body.Velocity = new Vector3(body.Velocity.X, 0f, body.Velocity.Z);
                body.Position = new Vector3(position.X, UpCrossY - 0.5f, position.Z);
                ShowExitBlockedHint(bx, bz);
                return;
            }

            // 保留上升动量（不足则补足）：越过接缝后继续向上飞（井里灌了水时正好借它往上游）
            float ascend = MathUtils.Max(body.Velocity.Y, MinAscendVelocity);

            // 落点就是**井底**（地表 localY = SurfaceExitY = 1），X/Z 用（可能已自动对准过的）井口列 —— 坐标映射本来的语义。
            SwitchTo(m_surface);
            Log.Information("[SuUndergroundWorld] 回到地表（坐标映射）：下界 localY={0:0.0} → 地表井底 localY={1:0.0}"
                + "（同 X/Z ({2},{3})；保留上升速度 {4:0.0}）", position.Y, SurfaceExitY, bx, bz, ascend);
            TeleportAndFreeze(body, new Vector3(position.X, SurfaceExitY, position.Z), new Vector3(0f, ascend, 0f));
        }

        /// <summary>在上界对应列凿一个 3x3 竖洞（从地表直通 y=0），并登记为流体连通列。</summary>
        private bool TryCarveSurfaceHole(int x, int z)
        {
            TerrainChunk chunk = m_surface.Terrain.GetChunkAtCell(x, z);
            if (chunk == null || chunk.State < TerrainChunkState.InvalidLight)
            {
                return false;   // 区块没加载，挖不了
            }
            int top = m_surface.Terrain.CalculateTopmostCellHeight(x, z);
            if (top <= 0)
            {
                return false;
            }
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int y = 0; y <= top; y++)
                    {
                        WriteCellDirect(m_surface.Terrain, x + dx, y, z + dz, 0);
                    }
                }
            }
            InvalidateAround(m_surface, x, z);
            RegisterCrossColumns(x, z);
            Log.Information("[SuUndergroundWorld] 上界 ({0},{1}) 本来没有洞 → 就地开一个（3x3，深 {2} 格）；"
                + "流体也会从这一列贯通", x, z, top + 1);
            return true;
        }

        /// <summary>返回通道被挡时，在屏幕上（不只是日志）告诉玩家最近的井口在哪。</summary>
        private void ShowExitBlockedHint(int x, int z)
        {
            if (Time.RealTime < m_nextExitHintAt)
            {
                return;
            }
            m_nextExitHintAt = Time.RealTime + 3.0;
            bool found = TryFindSurfaceHole(x, z, ReturnHintSearchRadius, out int hx, out int hz);
            string where = found
                ? string.Format("最近的井口在 ({0}, {1})，水平距离 {2:0} 格", hx, hz,
                    (float)Math.Sqrt((hx - x) * (hx - x) + (hz - z) * (hz - z)))
                : "请先在地表挖一口通到 y=0 的井（或在井口那几列向上飞）";
            ComponentPlayer player = GetPlayer();
            if (player?.ComponentGui != null)
            {
                player.ComponentGui.DisplaySmallMessage("上界这一列没有洞：" + where, Color.White, blinking: true, playNotificationSound: false);
            }
            Log.Information("[SuUndergroundWorld] 返回被挡（({0},{1})）：{2}", x, z, where);
        }

        /// <summary>
        /// 给"不活动的上界"补几拍流体模拟：只把活动地形临时指向上界、跑流体子系统，再换回来。
        /// 目的：井底残留的流动水（上游源头被堵掉以后）能真的流干，下界的水帘随之断流。
        /// </summary>
        private void PumpInactiveWorldFluid()
        {
            SuTerrainWorldSet inactive = m_active == m_deep ? m_surface : m_deep;
            if (inactive?.Terrain == null || inactive == m_active
                || (m_waterBehavior == null && m_magmaBehavior == null))
            {
                return;
            }
            SuTerrainWorldSet leaving = m_active;
            leaving.Updater.UpdateEvent.WaitOne();
            inactive.Updater.UpdateEvent.WaitOne();
            try
            {
                SuTerrainBinding.SetTerrain(m_terrain, inactive.Terrain);
                for (int i = 0; i < 6; i++)
                {
                    m_waterBehavior?.Update(PhantomFluidStep);
                    m_magmaBehavior?.Update(PhantomFluidStep);
                }
                if (!m_phantomFluidLogged)
                {
                    m_phantomFluidLogged = true;
                    Log.Information("[SuUndergroundWorld] 开始为不活动的那个世界补流体模拟（{0:0.0}s 一拍，当前补{1}）"
                        + "—— 否则对面的水在被堵住/断掉源头后会冻结、流不干", PhantomFluidInterval, inactive.Name);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 上界流体补拍失败：{0}", e.Message);
            }
            finally
            {
                SuTerrainBinding.SetTerrain(m_terrain, leaving.Terrain);
                leaving.Updater.UpdateEvent.Set();
                inactive.Updater.UpdateEvent.Set();
            }
        }

        /// <summary>在 (x,z) 周围 radius 格内找一口"真的通到 y=0 的井"（地表 y=0/1 是空的或流体）。</summary>
        private bool TryFindSurfaceHole(int x, int z, int radius, out int holeX, out int holeZ)
        {
            holeX = x;
            holeZ = z;
            float best = float.MaxValue;
            bool found = false;

            // 地表区块没加载时，"我们记录过的井口"是唯一可靠来源；加载了就直接扫真实格子（玩家可能把井填了）
            if (m_shaftCarved && !IsSurfaceColumnLoaded(m_anchorX, m_anchorZ))
            {
                int ax = m_anchorX - x;
                int az = m_anchorZ - z;
                if (ax * ax + az * az <= radius * radius)
                {
                    holeX = m_anchorX;
                    holeZ = m_anchorZ;
                    return true;
                }
            }
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int cx = x + dx;
                    int cz = z + dz;
                    TerrainChunk chunk = m_surface.Terrain.GetChunkAtCell(cx, cz);
                    if (chunk == null || chunk.State < TerrainChunkState.InvalidLight)
                    {
                        continue;
                    }
                    if (!IsOpenOrFluid(m_surface.Terrain.GetCellContents(cx, 0, cz))
                        || !IsOpenOrFluid(m_surface.Terrain.GetCellContents(cx, 1, cz)))
                    {
                        continue;
                    }
                    float d = dx * dx + dz * dz;
                    if (d < best)
                    {
                        best = d;
                        holeX = cx;
                        holeZ = cz;
                        found = true;
                    }
                }
            }
            return found;
        }

        private bool IsSurfaceColumnLoaded(int x, int z)
        {
            TerrainChunk chunk = m_surface.Terrain.GetChunkAtCell(x, z);
            return chunk != null && chunk.State >= TerrainChunkState.InvalidLight;
        }

        private bool IsSurfaceColumnOpen(int x, int z)
        {
            if (!IsSurfaceColumnLoaded(x, z))
            {
                // ⚠️ 玩家在下界时，上界世界的区块会被引擎整个卸载（实测：扫描 9409 列，0 列有区块）。
                // 这时只能靠"我们记录过的井口"判断：井是我们按构造凿通的，所以那一列按通处理。
                return m_shaftCarved
                    && MathUtils.Abs(x - m_anchorX) <= 1
                    && MathUtils.Abs(z - m_anchorZ) <= 1;
            }
            // 空气或流体都算"通"（井里有水/岩浆时照样能从下界飞上来）
            return IsOpenOrFluid(m_surface.Terrain.GetCellContents(x, 0, z))
                && IsOpenOrFluid(m_surface.Terrain.GetCellContents(x, 1, z));
        }

        private static bool IsOpenOrFluid(int contents)
        {
            if (contents == 0)
            {
                return true;
            }
            Block block = BlocksManager.Blocks[contents];
            return block is FluidBlock;
        }

        private void RecoverFromVoid(ComponentBody body)
        {
            int x = m_anchorSet ? m_anchorX : 0;
            int z = m_anchorSet ? m_anchorZ : 0;
            Log.Warning("[SuUndergroundWorld] 下界 y={0:0.0} 异常（区块未就绪？），把玩家放回下界", body.Position.Y);
            body.Velocity = Vector3.Zero;
            body.Position = new Vector3(x + 0.5f, DeepEntryY, z + 0.5f);
        }

        // ───────────────────────── 流体连通 ─────────────────────────

        /// <summary>
        /// 接缝流体镜像（**只做 上界 → 下界**，避免两边互相镜像形成永动水）：
        ///
        /// 地表井里（y=0..4）只要有水/岩浆，就在下界**同一列**的顶部 y=255 放一个
        /// **源方块**（level 0 = 源，见 FluidBlock.GetLevel —— 不是"流动格"），
        /// 下界就会从同一个天上位置挂下一道水帘/岩浆帘，可以借它浮上去。
        ///
        /// 两个坑（都实测过）：
        ///   1) 直接复制地表的"流动格"（level&gt;0）：掉 190 格就散失，下界什么都看不到 → 必须放**源**；
        ///   2) 用 chunk.SetCellValueFast 直写会绕过 SubsystemTerrain.ChangeCell 的通知，
        ///      流体模拟（SubsystemFluidBlockBehavior 的 m_toUpdate 队列）压根不知道这格变了 → 水帘不会流动。
        ///      所以下界处于活动状态时要用引擎的 ChangeCell 放/唤醒（直写只在非活动时做持久化）。
        /// </summary>
        private void UpdateFluidBridge()
        {
            if (m_crossColumns.Count == 0)
            {
                return;
            }
            // ⚠️ 核心事实：玩家在下界时，**上界世界的区块会被引擎整个卸载**（实测读数永远是"未知"）。
            // 所以不能"到下面再去读上界"——那样永远读不到"上界已经把水撤了"。
            // 做法：上界活动时持续把每一列的读数**记录下来（并存盘）**；下界时就用这份记录判断。
            for (int i = 0; i < m_crossColumns.Count; i++)
            {
                int x = m_crossColumns[i].X;
                int z = m_crossColumns[i].Y;
                long key = MirrorKey(x, z);

                int want;
                if (m_deepActive)
                {
                    want = m_lastSurfaceFluid.TryGetValue(key, out int last) ? last : -1;
                }
                else
                {
                    want = FindSurfaceFluidContents(x, z);
                    int wantTop;
                    if (m_deepActive)
                    {
                        want = m_lastSurfaceFluid.TryGetValue(key, out int last) ? last : -1;
                        wantTop = -1;
                    }
                    else
                    {
                        want = FindSurfaceFluidContents(x, z, out wantTop);
                    }
                    if (!m_deepActive && want >= 0
                        && (!m_lastSurfaceFluid.TryGetValue(key, out int old) || old != want))
                    {
                        m_lastSurfaceFluid[key] = want;
                        SaveSurfaceFluid();
                        Log.Information("[SuUndergroundWorld] 记录地表井口列 ({0},{1}) 的读数：{2}（水在 y={3}）",
                            x, z, want == 0 ? "无水" : "有水", want == 0 ? -1 : wantTop);
                    }
                }

                // ⚠️ 相邻的湿列只保留一个代表（键最小的那列）：
                //    上界井底的水常常是**一摊、同时占 2 列**（实测两列读数都是"水在 y=4"），
                //    而玩家看到的是一根下落水柱 → 逐列镜像就会在下界变成两根 ✗。
                if (want != 0 && MirrorNeighborIsRepresentative(x, z, key))
                {
                    want = 0;   // 这一列交给相邻的代表列 → 账上当作"无水"，已生成的会被撤掉
                }
                bool deepLoaded = m_deep.Terrain.GetChunkAtCell(x, z) != null;
                int current = Terrain.ExtractContents(m_deep.Terrain.GetCellValue(x, MirrorFluidY, z));
                bool mirrored = m_mirroredFluid.ContainsKey(key);

                // 读数未知（还没记录过）：什么都不改，只把已有水帘重新通知流体模拟
                if (want < 0)
                {
                    if (current != 0 && IsFluid(current) && m_deepActive && deepLoaded && m_wokenFluid.Add(key))
                    {
                        SetMirrorCell(x, z, MirrorFluidY, 0);
                        SetMirrorCell(x, z, MirrorFluidY,
                            Terrain.MakeBlockValue(current, 0,
                                Terrain.ExtractData(m_deep.Terrain.GetCellValue(x, MirrorFluidY, z))));
                        m_mirroredFluid[key] = current;
                        Log.Information("[SuUndergroundWorld] 流体镜像（唤醒）：下界 {0} 层 ({1},{0},{2}) 的流体"
                            + "重新通知流体模拟", MirrorFluidY, x, z);
                    }
                    continue;
                }

                if (want != 0)
                {
                    m_sourcePlugged.Remove(key);   // 上界又供水了 → 这一列以后还可以再堵一次
                    // 这一列顶上已经是"实心方块"（玩家自己堵的、或洞口脚手架）→ 不覆盖、不再放水源
                    if (current != 0 && !IsFluid(current))
                    {
                        m_mirroredFluid.Remove(key);
                        continue;
                    }
                    if (current != want)
                    {
                        SetMirrorCell(x, z, MirrorFluidY, 0);
                        if (SetMirrorCell(x, z, MirrorFluidY, Terrain.MakeBlockValue(want)))
                        {
                            m_wokenFluid.Add(key);
                            Log.Information("[SuUndergroundWorld] 流体镜像（下泄）：地表 ({0}) 有水 → 下界 {1} 层放{2}源",
                                x + "," + z, MirrorFluidY, want == m_waterIndex ? "水" : "流体");
                        }
                    }
                    else if (m_deepActive && deepLoaded)
                    {
                        // 按**状态**判断要不要重新通知：顶部有源、但正下方没有同种流体
                        //   → 说明这条水帘没被流体模拟接管（多半是之前直写进去的）
                        //   → 用"先清零再写回"制造一次真实的格子变化，把水帘"点着"。
                        // 以前只在 m_wokenFluid 第一次加入时才做，标记一旦加过就再也不管了
                        //   → 出现"顶上一格水永远不往下流"（实测 bug）。
                        int below = Terrain.ExtractContents(m_deep.Terrain.GetCellValue(x, MirrorFluidY - 1, z));
                        if (!IsFluid(below))
                        {
                            SetMirrorCell(x, z, MirrorFluidY, 0);
                            SetMirrorCell(x, z, MirrorFluidY, Terrain.MakeBlockValue(want));
                            Log.Information("[SuUndergroundWorld] 流体镜像（重新点火）：下界 {0} 层 ({1},{0},{2}) "
                                + "有源但下方无水 → 重新通知流体模拟让它往下流", MirrorFluidY, x, z);
                        }
                        m_wokenFluid.Add(key);
                    }
                    m_mirroredFluid[key] = want;
                }
                else
                {
                    // 断流 = **和玩家手动堵水源完全一样**：在"顶部那一格水"放一格实心方块，然后就不管了。
                    //   · 放完由引擎自己把整根无源水柱流干（玩家这样堵，水柱正常流干 —— 实测）；
                    //   · 每列只堵一次（m_sourcePlugged），绝不重复、绝不清格子、绝不往下填
                    //     （"先清成空气再找顶部"会把顶部越推越低 → 变成依次向下填，那是我踩过的坑）。
                    m_mirroredFluid.Remove(key);
                    m_wokenFluid.Remove(key);
                    if (!m_sourcePlugged.Contains(key))
                    {
                        int topY = FindTopCurtainCellY(x, z);
                        if (topY >= 80 && m_deepActive && m_deep.Terrain.GetChunkAtCell(x, z) != null)
                        {
                            // 引擎自己删水就是这么做的：Set(x,y,z,0) → SubsystemTerrain.ChangeCell（SpreadFluid 308 行）。
                            // 走这条正常路径会通知流体子系统 → SpreadFluid（231-246 行）判定"非源且上方无水" →
                            // 变浅/删除 → 连锁把整根无源水柱流干。**每列只做一次**（不要反复清，那会变成依次往下）。
                            m_terrain.ChangeCell(x, topY, z, 0);                       // 空气：清掉顶部水源
                            m_sourcePlugged.Add(key);
                            Log.Information("[SuUndergroundWorld] 断流水源：({0},{1},{2}) 用空气清掉顶部水源"
                                + "（走 ChangeCell 正常路径）→ 引擎自己把整根流干", x, topY, z);
                            if (!CutSourceWithAir)
                            {
                                // 备用方案（照玩家手动做法）：同一格改成实心方块
                                m_terrain.ChangeCell(x, topY, z, Terrain.MakeBlockValue(FindSolidBlockIndex()));
                            }
                        }
                    }
                }
            }

            // 低频状态日志：一眼看出"记录里上界有没有水 / 下界哪层还有水"
            if (Time.RealTime >= m_nextFluidDiagAt)
            {
                m_nextFluidDiagAt = Time.RealTime + 15.0;
                int dx = m_anchorSet ? m_anchorX : m_crossColumns[0].X;
                int dz = m_anchorSet ? m_anchorZ : m_crossColumns[0].Y;
                int recorded = m_lastSurfaceFluid.TryGetValue(MirrorKey(dx, dz), out int rec) ? rec : -1;
                var stillWet = new System.Text.StringBuilder();
                for (int i = 0; i < m_crossColumns.Count; i++)
                {
                    int cx = m_crossColumns[i].X;
                    int cz = m_crossColumns[i].Y;
                    int count = 0;
                    int top = -1;
                    for (int y = 255; y >= 0; y--)
                    {
                        if (IsFluid(Terrain.ExtractContents(m_deep.Terrain.GetCellValue(cx, y, cz))))
                        {
                            count++;
                            if (top < 0)
                            {
                                top = y;
                            }
                        }
                    }
                    if (count > 0)
                    {
                        stillWet.Append('(').Append(cx).Append(',').Append(cz).Append(")顶=").Append(top)
                            .Append('/').Append(count).Append("格 ");
                    }
                }
                Log.Information("[SuUndergroundWorld] 流体状态：地表井口列 ({0},{1}) 记录={2}；当前活动世界={3}；"
                    + "下界残留水柱：{4}", dx, dz,
                    recorded < 0 ? "未记录" : (recorded == 0 ? "无水" : "有水"),
                    m_active?.Name,
                    stillWet.Length == 0 ? "无" : stillWet.ToString());
            }
        }



        private string PlayerPosPath()
        {
            return Storage.CombinePaths(ModDataDirectory(), "player-pos.xml");
        }

        /// <summary>
        /// 落盘"当前世界 + 每个世界里的玩家位置"（XML）。
        /// 引擎的存档只保存一个**不带世界信息**的局部坐标，于是"在下界高空退出 → 重进落在上界高空"（实测）。
        /// 形如：
        ///   &lt;PlayerPositions&gt;
        ///     &lt;ActiveWorld&gt;deep&lt;/ActiveWorld&gt;
        ///     &lt;Position world="surface" x="…" y="…" z="…" /&gt;
        ///     &lt;Position world="deep" x="…" y="…" z="…" /&gt;
        ///   &lt;/PlayerPositions&gt;
        /// </summary>
        private void SavePlayerPositions()
        {
            try
            {
                ComponentBody body = GetPlayer()?.ComponentBody;
                if (body != null)
                {
                    m_savedPos[m_deepActive ? "deep" : "surface"] = body.Position;
                }
                if (m_savedPos.Count == 0)
                {
                    return;
                }
                Storage.CreateDirectory(ModDataDirectory());
                var root = new System.Xml.Linq.XElement("PlayerPositions");
                root.Add(new System.Xml.Linq.XElement("ActiveWorld", m_deepActive ? "deep" : "surface"));
                foreach (KeyValuePair<string, Vector3> pair in m_savedPos)
                {
                    root.Add(new System.Xml.Linq.XElement("Position",
                        new System.Xml.Linq.XAttribute("world", pair.Key),
                        new System.Xml.Linq.XAttribute("x", pair.Value.X),
                        new System.Xml.Linq.XAttribute("y", pair.Value.Y),
                        new System.Xml.Linq.XAttribute("z", pair.Value.Z)));
                }
                Storage.WriteAllText(PlayerPosPath(), root.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 保存玩家位置失败：{0}", e.Message);
            }
        }

        /// <summary>读回"上次在哪个世界 + 那个世界的玩家位置"，并在 1 秒后（等出生流程结束）恢复。</summary>
        private void LoadPlayerPositions()
        {
            try
            {
                m_savedPos.Clear();
                string path = PlayerPosPath();
                if (!Storage.FileExists(path))
                {
                    return;
                }
                System.Xml.Linq.XElement root = System.Xml.Linq.XElement.Parse(Storage.ReadAllText(path));
                foreach (System.Xml.Linq.XElement element in root.Elements("Position"))
                {
                    string world = (string)element.Attribute("world");
                    if (string.IsNullOrEmpty(world))
                    {
                        continue;
                    }
                    m_savedPos[world] = new Vector3(
                        (float)element.Attribute("x"), (float)element.Attribute("y"), (float)element.Attribute("z"));
                }
                string activeWorld = (string)root.Element("ActiveWorld") ?? "surface";
                bool deep = activeWorld.Trim() == "deep";
                if (m_savedPos.TryGetValue(deep ? "deep" : "surface", out Vector3 pos))
                {
                    m_pendingRestore = pos;
                    m_pendingRestoreDeep = deep;
                    m_pendingRestoreAt = Time.RealTime + 1.0;
                    m_pendingRestoreValid = true;
                    Log.Information("[SuUndergroundWorld] 上次退出时在{0}（{1:0.0}, {2:0.0}, {3:0.0}）→ 进图后恢复",
                        deep ? "下界" : "地表", pos.X, pos.Y, pos.Z);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 读取玩家位置失败：{0}", e.Message);
            }
        }

        private string WaterUpdatesPath()
        {
            return Storage.CombinePaths(ModDataDirectory(), "water-updates.xml");
        }

        /// <summary>这一列在不在"我们的水帘列"里。</summary>
        private bool IsOurCurtainColumn(int x, int z)
        {
            for (int i = 0; i < m_crossColumns.Count; i++)
            {
                if (m_crossColumns[i].X == x && m_crossColumns[i].Y == z)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 换界**之前**（离开的世界还活着）把流体子系统的待更新队列读出来，
        /// 只保留属于"我们水帘那些列"的格子，记到内存 + XML。
        ///
        /// 为什么要这么做：流体待更新队列（m_toUpdate）是**整个工程共享、不区分世界**的。
        /// 切世界后，属于另一个世界的待更新格会被错误的地形处理掉 →
        /// 下落中水柱"一格推一格"的链条就断了，回来时冻在半空（实测）。
        /// 记下这些坐标，回来时逐个重新通知一次，链条就能接上 —— **不需要改任何方块**。
        /// </summary>
        private void CapturePendingFluidUpdates(SuTerrainWorldSet leaving)
        {
            try
            {
                m_pendingFluidCells.Clear();
                if (m_waterBehavior == null || m_crossColumns.Count == 0 || leaving == null)
                {
                    return;
                }
                FieldInfo field = typeof(SubsystemFluidBlockBehavior).GetField(
                    "m_toUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(m_waterBehavior) is System.Collections.IDictionary queue)
                {
                    string world = leaving == m_deep ? "deep" : "surface";
                    foreach (object key in queue.Keys)
                    {
                        var p = (Point3)key;
                        if (IsOurCurtainColumn(p.X, p.Z)
                            && IsTrackedFluidLayer(leaving == m_deep, p.Y))
                        {
                            m_pendingFluidCells.Add((world, p.X, p.Y, p.Z));
                        }
                    }
                }
                SavePendingFluidUpdates();
                if (m_pendingFluidCells.Count > 0)
                {
                    Log.Information("[SuUndergroundWorld] 换界前记下流体待更新格 {0} 个（回来时接着更新）",
                        m_pendingFluidCells.Count);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 记录流体待更新格失败：{0}", e.Message);
            }
        }

        /// <summary>按记下的坐标重新通知流体模拟（只处理属于当前世界的那些格，处理完就移除）。</summary>
        private void ReplayPendingFluidUpdates()
        {
            if (m_pendingFluidCells.Count == 0)
            {
                return;
            }
            try
            {
                string world = m_deepActive ? "deep" : "surface";
                int n = 0;
                for (int i = m_pendingFluidCells.Count - 1; i >= 0; i--)
                {
                    (string w, int x, int y, int z) = m_pendingFluidCells[i];
                    if (!IsTrackedFluidLayer(w == "deep", y))
                    {
                        m_pendingFluidCells.RemoveAt(i);   // 上界非"切换面那层"的记录直接丢掉
                        continue;
                    }
                    if (w != world || m_active.Terrain.GetChunkAtCell(x, z) == null)
                    {
                        continue;
                    }
                    ReigniteCell(x, z, y);
                    m_pendingFluidCells.RemoveAt(i);
                    n++;
                }
                if (n > 0)
                {
                    SavePendingFluidUpdates();
                    Log.Information("[SuUndergroundWorld] 接回流体链条：重新通知 {0} 格（水柱继续落/继续缩）", n);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 重放流体待更新格失败：{0}", e.Message);
            }
        }

        private void SavePendingFluidUpdates()
        {
            try
            {
                Storage.CreateDirectory(ModDataDirectory());
                var root = new System.Xml.Linq.XElement("WaterUpdates");
                foreach ((string w, int x, int y, int z) in m_pendingFluidCells)
                {
                    root.Add(new System.Xml.Linq.XElement("Cell",
                        new System.Xml.Linq.XAttribute("world", w),
                        new System.Xml.Linq.XAttribute("x", x),
                        new System.Xml.Linq.XAttribute("y", y),
                        new System.Xml.Linq.XAttribute("z", z)));
                }
                Storage.WriteAllText(WaterUpdatesPath(), root.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 保存流体待更新格失败：{0}", e.Message);
            }
        }

        /// <summary>把某一格流体"重新通知"给流体模拟。
        ///
        /// 为什么需要：流体子系统的待更新队列（m_toUpdate）是**整个工程共享**的（不区分世界），
        /// 切世界时属于另一边的格子会被拿到错误的地形上处理并丢掉 →
        /// 下落中水柱的"一格接一格"连锁更新就断了，回到那个世界时水停在半空（实测）。
        /// 重新通知顶部一格即可让连锁继续往下跑；每列只发两次改格，开销很小。
        /// </summary>
        private void ReigniteActiveFluid()
        {
            if (m_crossColumns.Count == 0 || m_active?.Terrain == null)
            {
                return;
            }
            int reignited = 0;
            for (int i = 0; i < m_crossColumns.Count; i++)
            {
                int x = m_crossColumns[i].X;
                int z = m_crossColumns[i].Y;
                // 找出这一列**最低的那格水（下落前沿）**与**最高的那格水**，两个都重新通知：
                //   · 下落前沿被更新，水柱才会继续往地面长；
                //   · 顶部被更新，断流时才会继续往下缩。
                // 只点一个方向不够（实测：换界回来水柱停在一半、底部不再增加）。
                int tipY = -1;
                int topY = -1;
                bool deepHere = m_active == m_deep;
                for (int y = 0; y <= 255; y++)
                {
                    if (!IsTrackedFluidLayer(deepHere, y))
                    {
                        continue;   // 上界只跟踪切换面那一层
                    }
                    int contents = Terrain.ExtractContents(m_active.Terrain.GetCellValue(x, y, z));
                    if (!IsFluid(contents))
                    {
                        continue;
                    }
                    if (tipY < 0)
                    {
                        tipY = y;
                    }
                    topY = y;
                }
                if (topY < 0 || m_active.Terrain.GetChunkAtCell(x, z) == null)
                {
                    continue;
                }
                ReigniteCell(x, z, topY);
                if (tipY >= 0 && tipY != topY)
                {
                    ReigniteCell(x, z, tipY);
                }
                reignited++;
            }
            if (reignited > 0)
            {
                Log.Information("[SuUndergroundWorld] 水帘重新点火：{0} 列（换界后让下落中的水接着流）", reignited);
            }
        }

        /// <summary>
        /// 把某一格流体"重新通知"给流体模拟：ChangeCell(0) → 原样写回（保留 data）。
        /// ⚠️ 必须保留 data：data=0 对流体的含义是 **level 0 = 水源**，
        ///    只写 contents 会把流动水格变成永久水源（实测 bug）。
        /// </summary>
        private void ReigniteCell(int x, int z, int y)
        {
            int cellValue = m_active.Terrain.GetCellValue(x, y, z);
            int contents = Terrain.ExtractContents(cellValue);
            if (!IsFluid(contents))
            {
                return;
            }
            int value = Terrain.MakeBlockValue(contents, 0, Terrain.ExtractData(cellValue));
            m_terrain.ChangeCell(x, y, z, 0);
            m_terrain.ChangeCell(x, y, z, value);
        }

        /// <summary>读一列井口在上界到底有没有水（下界活动时用记录，上界活动时读实况）。</summary>
        private int ReadSurfaceWant(int x, int z)
        {
            if (m_deepActive)
            {
                return m_lastSurfaceFluid.TryGetValue(MirrorKey(x, z), out int last) ? last : -1;
            }
            return FindSurfaceFluidContents(x, z);
        }

        /// <summary>
        /// 这一列是不是"该交给邻居处理"（相邻的井口列里存在**键更小、也有水**的列）。
        /// 用于把"一摊水占了两列"合并成一根水柱（上下左右 4 邻接）。
        /// </summary>
        private bool MirrorNeighborIsRepresentative(int x, int z, long key)
        {
            for (int i = 0; i < m_crossColumns.Count; i++)
            {
                int nx = m_crossColumns[i].X;
                int nz = m_crossColumns[i].Y;
                if (MathUtils.Abs(nx - x) + MathUtils.Abs(nz - z) != 1)
                {
                    continue;   // 只看上下左右相邻
                }
                if (MirrorKey(nx, nz) >= key)
                {
                    continue;   // 邻居键更大 → 由本列负责
                }
                if (ReadSurfaceWant(nx, nz) > 0)
                {
                    return true;   // 邻居键更小且也有水 → 交给它
                }
            }
            return false;
        }

        /// <summary>从世界顶部往下找这一列**顶部水源那一格**（先撞到固体就返回 -1）。</summary>
        private int FindTopCurtainCellY(int x, int z)
        {
            for (int y = 255; y >= 0; y--)
            {
                int contents = Terrain.ExtractContents(m_deep.Terrain.GetCellValue(x, y, z));
                if (contents == 0)
                {
                    continue;
                }
                return IsFluid(contents) ? y : -1;
            }
            return -1;
        }

        /// <summary>找一种实心方块当脚手架（泥土）。</summary>
        private int FindSolidBlockIndex()
        {
            if (m_solidBlockIndex < 0)
            {
                m_solidBlockIndex = 0;
                for (int i = 1; i < BlocksManager.Blocks.Length; i++)
                {
                    if (BlocksManager.Blocks[i] is DirtBlock)
                    {
                        m_solidBlockIndex = i;
                        break;
                    }
                }
            }
            return m_solidBlockIndex;
        }

        /// <summary>写一格"水帘水位"（活动世界时走引擎 API 以便通知流体模拟；否则直写持久化）。</summary>
        private bool SetMirrorCell(int x, int z, int y, int value)
        {
            if (m_deepActive && m_deep.Terrain.GetChunkAtCell(x, z) != null)
            {
                m_terrain.ChangeCell(x, y, z, value);
                return true;
            }
            if (WriteCellDirect(m_deep.Terrain, x, y, z, value))
            {
                InvalidateAround(m_deep, x, z);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 上界井口列里到底还有没有流体？**扫整列（y=0..255）**。
        /// 返回：-1 = 地表区块没加载（状态未知）、0 = 没有流体、&gt;0 = 流体 contents。
        /// out topY：找到的水在哪个高度（排查"水到底还在哪儿"用）。
        ///
        /// ⚠️ 以前只扫 y=0..4：上界水没流到井底（或残留在中段）时读成"无水"，
        /// 而真正残留的水又扫不到 → 判断完全不可靠（实测踩过）。
        /// </summary>
        private int FindSurfaceFluidContents(int x, int z, out int topY)
        {
            topY = -1;
            TerrainChunk chunk = m_surface.Terrain.GetChunkAtCell(x, z);
            if (chunk == null || chunk.State < TerrainChunkState.InvalidLight)
            {
                return -1;
            }
            for (int y = 255; y >= 0; y--)
            {
                int contents = Terrain.ExtractContents(m_surface.Terrain.GetCellValue(x, y, z));
                if (IsFluid(contents))
                {
                    topY = y;
                    return contents;
                }
            }
            return 0;
        }

        private int FindSurfaceFluidContents(int x, int z)
        {
            return FindSurfaceFluidContents(x, z, out int _);
        }

        /// <summary>把当前活动的非玩家实体整体移出工程（不销毁）存进该世界的集合，并落盘。</summary>
        private void StashWorldEntities(int index)
        {
            if (m_project == null)
            {
                return;
            }
            var playerEntities = new HashSet<Entity>();
            foreach (PlayerData data in GetPlayersData())
            {
                if (data.ComponentPlayer != null)
                {
                    playerEntities.Add(data.ComponentPlayer.Entity);
                }
            }

            List<Entity> stash = m_worldEntities[index];
            stash.Clear();
            foreach (Entity entity in m_project.Entities.ToList())
            {
                if (entity == null || playerEntities.Contains(entity))
                {
                    continue;
                }
                stash.Add(entity);
            }
            foreach (Entity entity in stash)
            {
                try
                {
                    m_project.RemoveEntity(entity, disposeEntity: false);
                }
                catch (Exception e)
                {
                    Log.Warning("[SuUndergroundWorld] 移出实体失败：{0}", e.Message);
                }
            }
            SaveWorldEntities(index);
            Log.Information("[SuUndergroundWorld] 实体切换：收起{0}世界 {1} 个实体（动物/掉落物）", WorldName(index), stash.Count);
        }

        /// <summary>把该世界的实体放回工程；本会话第一次进这个世界时从文件恢复。</summary>
        private void RestoreWorldEntities(int index)
        {
            if (m_project == null)
            {
                return;
            }
            List<Entity> stash = m_worldEntities[index];
            if (stash.Count == 0)
            {
                LoadWorldEntities(index);
            }
            int count = 0;
            foreach (Entity entity in stash)
            {
                try
                {
                    if (!entity.IsAddedToProject)
                    {
                        m_project.AddEntity(entity);
                        count++;
                    }
                }
                catch (Exception e)
                {
                    Log.Warning("[SuUndergroundWorld] 放回实体失败：{0}", e.Message);
                }
            }
            if (count > 0)
            {
                Log.Information("[SuUndergroundWorld] 实体切换：放出{0}世界 {1} 个实体", WorldName(index), count);
            }
        }

        /// <summary>把当前活动世界的掉落物/移动方块收进该世界的存储（直接清列表，避免在新世界闪一帧）。</summary>
        private void StashWorldItems(int index)
        {
            try
            {
                List<Pickable> pickables = GetRawPickables();
                if (pickables != null)
                {
                    m_worldPickables[index].Clear();
                    m_worldPickables[index].AddRange(pickables);
                    pickables.Clear();
                    if (m_worldPickables[index].Count > 0)
                    {
                        Log.Information("[SuUndergroundWorld] 物品切换：收起{0}世界 {1} 个掉落物",
                            WorldName(index), m_worldPickables[index].Count);
                    }
                }

                IList moving = GetRawMovingBlockSets();
                if (moving != null)
                {
                    m_worldMovingBlocks[index].Clear();
                    foreach (object item in moving)
                    {
                        m_worldMovingBlocks[index].Add(item);
                    }
                    moving.Clear();
                    if (m_worldMovingBlocks[index].Count > 0)
                    {
                        Log.Information("[SuUndergroundWorld] 物品切换：收起{0}世界 {1} 组移动方块",
                            WorldName(index), m_worldMovingBlocks[index].Count);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 收起掉落物/移动方块失败：{0}", e.Message);
            }
        }

        /// <summary>把该世界的掉落物/移动方块放回子系统列表。</summary>
        private void RestoreWorldItems(int index)
        {
            try
            {
                List<Pickable> pickables = GetRawPickables();
                if (pickables != null)
                {
                    foreach (Pickable pickable in m_worldPickables[index])
                    {
                        if (pickable != null && !pickables.Contains(pickable))
                        {
                            pickables.Add(pickable);
                        }
                    }
                    if (m_worldPickables[index].Count > 0)
                    {
                        Log.Information("[SuUndergroundWorld] 物品切换：放出{0}世界 {1} 个掉落物",
                            WorldName(index), m_worldPickables[index].Count);
                    }
                }

                IList moving = GetRawMovingBlockSets();
                if (moving != null)
                {
                    foreach (object item in m_worldMovingBlocks[index])
                    {
                        if (item != null && !moving.Contains(item))
                        {
                            moving.Add(item);
                        }
                    }
                    if (m_worldMovingBlocks[index].Count > 0)
                    {
                        Log.Information("[SuUndergroundWorld] 物品切换：放出{0}世界 {1} 组移动方块",
                            WorldName(index), m_worldMovingBlocks[index].Count);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 放回掉落物/移动方块失败：{0}", e.Message);
            }
        }

        private List<Pickable> GetRawPickables()
        {
            if (m_subsystemPickables == null || m_pickablesField == null)
            {
                return null;
            }
            return m_pickablesField.GetValue(m_subsystemPickables) as List<Pickable>;
        }

        private IList GetRawMovingBlockSets()
        {
            if (m_subsystemMovingBlocks == null || m_movingBlockSetsField == null)
            {
                return null;
            }
            return m_movingBlockSetsField.GetValue(m_subsystemMovingBlocks) as IList;
        }

        private string SurfaceFluidPath()
        {
            return Storage.CombinePaths(ModDataDirectory(), "surface-fluid.xml");
        }

        /// <summary>
        /// 把"每一列井口在上界到底有没有水"存盘：上界的区块在下界是不可读的，
        /// 所以这份读数必须跨会话保留，否则重进存档后又会变成"未知"→ 水帘撤不掉。
        /// </summary>
        private void SaveSurfaceFluid()
        {
            try
            {
                Storage.CreateDirectory(ModDataDirectory());
                var root = new System.Xml.Linq.XElement("SurfaceFluid");
                foreach (KeyValuePair<long, int> pair in m_lastSurfaceFluid)
                {
                    root.Add(new System.Xml.Linq.XElement("Column",
                        new System.Xml.Linq.XAttribute("x", (int)(pair.Key >> 32)),
                        new System.Xml.Linq.XAttribute("z", (int)(pair.Key & 0xFFFFFFFF)),
                        new System.Xml.Linq.XAttribute("contents", pair.Value)));
                }
                Storage.WriteAllText(SurfaceFluidPath(), root.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 保存地表流体读数失败：{0}", e.Message);
            }
        }

        private void LoadSurfaceFluid()
        {
            try
            {
                m_lastSurfaceFluid.Clear();
                if (Storage.FileExists(SurfaceFluidPath()))
                {
                    System.Xml.Linq.XElement root =
                        System.Xml.Linq.XElement.Parse(Storage.ReadAllText(SurfaceFluidPath()));
                    foreach (System.Xml.Linq.XElement element in root.Elements("Column"))
                    {
                        m_lastSurfaceFluid[MirrorKey((int)element.Attribute("x"), (int)element.Attribute("z"))] =
                            (int)element.Attribute("contents");
                    }
                }
                Log.Information("[SuUndergroundWorld] 地表流体读数（XML）：{0} 列", m_lastSurfaceFluid.Count);
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 读取地表流体读数失败：{0}", e.Message);
            }
        }

        private string SpawnFilePath()
        {
            return Storage.CombinePaths(ModDataDirectory(), "spawn.xml");
        }

        /// <summary>把"复活点 + 它属于哪个世界"存到 mod 自己的文件（引擎的存档只有坐标，没有世界信息）。</summary>
        private void SaveSpawnPoint()
        {
            try
            {
                Storage.CreateDirectory(ModDataDirectory());
                var root = new System.Xml.Linq.XElement("SpawnPoint",
                    new System.Xml.Linq.XElement("World", m_spawnIsDeep ? "deep" : "surface"),
                    new System.Xml.Linq.XElement("Position",
                        new System.Xml.Linq.XAttribute("x", m_spawnPosition.X),
                        new System.Xml.Linq.XAttribute("y", m_spawnPosition.Y),
                        new System.Xml.Linq.XAttribute("z", m_spawnPosition.Z)));
                Storage.WriteAllText(SpawnFilePath(), root.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 保存复活点失败：{0}", e.Message);
            }
        }

        private bool TryLoadSpawnPoint()
        {
            try
            {
                if (!Storage.FileExists(SpawnFilePath()))
                {
                    return false;
                }
                System.Xml.Linq.XElement root =
                    System.Xml.Linq.XElement.Parse(Storage.ReadAllText(SpawnFilePath()));
                m_spawnIsDeep = ((string)root.Element("World") ?? "surface").Trim() == "deep";
                System.Xml.Linq.XElement pos = root.Element("Position");
                if (pos == null)
                {
                    return false;
                }
                m_spawnPosition = new Vector3(
                    (float)pos.Attribute("x"), (float)pos.Attribute("y"), (float)pos.Attribute("z"));
                m_hasSpawn = true;
                m_spawnSeeded = true;
                // ⚠️ 这里**不要**写回 PlayerData.SpawnPosition：它可能属于另一个世界，
                //    写回后引擎会在当前世界的地形上微调它，把坐标改歪（实测 bug）。
                //    真正需要时（复活那一刻）我们已经切到正确的世界再写（见 RespawnAtSavedSpawn）。
                Log.Information("[SuUndergroundWorld] 复活点（XML 读回）：({0:0.0}, {1:0.0}, {2:0.0})（{3} 世界）",
                    m_spawnPosition.X, m_spawnPosition.Y, m_spawnPosition.Z, m_spawnIsDeep ? "下界" : "地表");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 读取复活点失败：{0}", e.Message);
                return false;
            }
        }

        private string AnchorFilePath()
        {
            return Storage.CombinePaths(ModDataDirectory(), "anchor.xml");
        }

        /// <summary>把井口位置与"是否已凿通"存盘：重进存档后不再乱凿新井，返回提示也能指出真正的井口。</summary>
        private void SaveAnchorPoint()
        {
            try
            {
                Storage.CreateDirectory(ModDataDirectory());
                var root = new System.Xml.Linq.XElement("ShaftAnchor",
                    new System.Xml.Linq.XElement("X", m_anchorX),
                    new System.Xml.Linq.XElement("Z", m_anchorZ),
                    new System.Xml.Linq.XElement("Carved", m_shaftCarved));
                Storage.WriteAllText(AnchorFilePath(), root.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 保存井口失败：{0}", e.Message);
            }
        }

        private bool TryLoadAnchorPoint()
        {
            try
            {
                if (!Storage.FileExists(AnchorFilePath()))
                {
                    return false;
                }
                System.Xml.Linq.XElement root =
                    System.Xml.Linq.XElement.Parse(Storage.ReadAllText(AnchorFilePath()));
                m_anchorX = (int)root.Element("X");
                m_anchorZ = (int)root.Element("Z");
                m_shaftCarved = (bool?)root.Element("Carved") ?? true;
                m_anchorSet = true;
                RegisterCrossColumns(m_anchorX, m_anchorZ);
                Log.Information("[SuUndergroundWorld] 井口（XML 读回）：({0},{1})，已凿通={2}",
                    m_anchorX, m_anchorZ, m_shaftCarved);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 读取井口失败：{0}", e.Message);
                return false;
            }
        }

        private string EntityFilePath(int index)
        {
            // ⚠️ m_worldDirectory 是引擎的虚拟路径（形如 `data:/Worlds/World2`），必须用引擎的 Storage API，
            // 用 System.IO.Path 会拼出 `data:\Worlds\...` 这种非法路径。
            return Storage.CombinePaths(ModDataDirectory(), index == 1 ? "entities.deep.xml" : "entities.surface.xml");
        }

        private string ModDataDirectory()
        {
            return Storage.CombinePaths(m_worldDirectory ?? ".", "SuUndergroundWorld");
        }

        private void SaveWorldEntities(int index)
        {
            try
            {
                EntityDataList data = m_project.SaveEntities(m_worldEntities[index]);
                var root = new XElement("Entities");
                data.Save(root);
                Storage.CreateDirectory(ModDataDirectory());
                Storage.WriteAllText(EntityFilePath(index), new XDocument(root).ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 保存{0}世界实体失败：{1}", WorldName(index), e.Message);
            }
        }

        private void LoadWorldEntities(int index)
        {
            try
            {
                string path = EntityFilePath(index);
                if (!Storage.FileExists(path))
                {
                    return;
                }
                XDocument document = XDocument.Parse(Storage.ReadAllText(path));
                var data = new EntityDataList(m_project.GameDatabase, document.Root, ignoreInvalidEntities: true);
                List<Entity> loaded = m_project.LoadEntities(data);
                m_worldEntities[index].AddRange(loaded);
                Log.Information("[SuUndergroundWorld] 从文件恢复{0}世界实体 {1} 个", WorldName(index), loaded.Count);
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 读取{0}世界实体失败：{1}", WorldName(index), e.Message);
            }
        }

        // ───────────────────────── 换世界（原子 + 线程保护 + 实体切换）─────────────────────────

        /// <summary>
        /// 原子切换活动世界：
        ///   1) 按住"离开的世界"和"进入的世界"两把 UpdateEvent（后台地形线程会用 SubsystemTerrain 的
        ///      Serializer / Generator / GeoGen，切换瞬间不停线程会串世界）；
        ///   2) 用离开世界的序列化器落盘（引擎自己的 Save 只保存活动世界）；
        ///   3) 实体切换：离开世界的非玩家实体收起，进入世界的放回；
        ///   4) 反射写 6 个字段；
        ///   5) 放锁并请求一次同步更新，加速目标区块就绪。
        /// </summary>
        private void SwitchTo(SuTerrainWorldSet target, bool swapEntities = true)
        {
            if (target == null || target == m_active)
            {
                return;
            }
            SuTerrainWorldSet leaving = m_active;
            CapturePendingFluidUpdates(leaving);   // 离开前把流体待更新队列里"我们水帘那些列"的格子记下来
            leaving.Updater.UpdateEvent.WaitOne();
            target.Updater.UpdateEvent.WaitOne();
            try
            {
                SaveWorld(leaving);
                if (swapEntities)
                {
                    StashWorldEntities(IndexOf(leaving));
                    StashWorldItems(IndexOf(leaving));     // 掉落物 / 移动方块也要跟着世界走
                }
                SuTerrainBinding.Apply(m_terrain, target);
                m_active = target;
                m_deepActive = target == m_deep;
                if (swapEntities)
                {
                    RestoreWorldEntities(IndexOf(target));
                    RestoreWorldItems(IndexOf(target));
                }
                WriteActiveWorldMarker(m_deepActive ? "deep" : "surface");
            }
            finally
            {
                leaving.Updater.UpdateEvent.Set();
                target.Updater.UpdateEvent.Set();
            }
            target.Updater.RequestSynchronousUpdate();
            m_reigniteFrames = 3;   // 换界后头几帧重新点火水帘（队列是工程共享的，切世界会把连锁更新弄断）
        }

        private string ActiveWorldMarkerPath()
        {
            return Storage.CombinePaths(ModDataDirectory(), "active-world.txt");
        }

        private void WriteActiveWorldMarker(string value)
        {
            try
            {
                Storage.CreateDirectory(ModDataDirectory());
                Storage.WriteAllText(ActiveWorldMarkerPath(), value);
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 写入活动世界标记失败：{0}", e.Message);
            }
        }

        private string ReadActiveWorldMarker()
        {
            try
            {
                string path = ActiveWorldMarkerPath();
                return Storage.FileExists(path) ? Storage.ReadAllText(path).Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void TeleportAndFreeze(ComponentBody body, Vector3 position, Vector3 releaseVelocity)
        {
            m_waitGravity = body.IsGravityEnabled;
            body.IsGravityEnabled = false;
            body.Velocity = Vector3.Zero;
            body.Position = position;
            m_waitBody = body;
            m_waitPosition = position;
            m_waitReleaseVelocity = releaseVelocity;   // 放行时恢复：向下=0（软着陆），向上=保留上升动量
            m_waitDeadline = Time.RealTime + ChunkReadyTimeout;
            m_waiting = true;
        }

        private void HandleWaiting(ComponentBody body)
        {
            body.Velocity = Vector3.Zero;
            body.Position = m_waitPosition;
            TerrainChunk chunk = m_active.Terrain.GetChunkAtCell(
                Terrain.ToCell(m_waitPosition.X), Terrain.ToCell(m_waitPosition.Z));
            bool ready = chunk != null && chunk.State == TerrainChunkState.Valid;
            if (!ready && Time.RealTime < m_waitDeadline)
            {
                return;
            }
            body.IsGravityEnabled = m_waitGravity;
            body.Velocity = m_waitReleaseVelocity;
            m_waiting = false;
            m_waitBody = null;
            if (ready)
            {
                int bx = Terrain.ToCell(m_waitPosition.X);
                int bz = Terrain.ToCell(m_waitPosition.Z);
                int by = MathUtils.Max(Terrain.ToCell(m_waitPosition.Y) - 1, 0);
                int contents = m_active.Terrain.GetCellContents(bx, by, bz);
                Log.Information("[SuUndergroundWorld] 目标区块就绪（{0} 世界）：脚下 ({1},{2},{3}) contents={4}",
                    m_active.Name, bx, by, bz, contents);
            }
            else
            {
                Log.Warning("[SuUndergroundWorld] 等待目标区块超过 {0} 秒，已放行", ChunkReadyTimeout);
            }
        }

        private static void SaveWorld(SuTerrainWorldSet world)
        {
            if (world?.Terrain == null || world.Serializer == null)
            {
                return;
            }
            TerrainChunk[] chunks = world.Terrain.AllocatedChunks;
            for (int i = 0; i < chunks.Length; i++)
            {
                TrySaveChunk(world.Serializer, chunks[i]);
            }
        }

        private static void TrySaveChunk(TerrainSerializer23 serializer, TerrainChunk chunk)
        {
            try
            {
                serializer.SaveChunk(chunk);
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 保存区块 {0} 失败：{1}", chunk.Coords, e.Message);
            }
        }

        // ───────────────────────── 收尾 ─────────────────────────

        public void OnProjectDisposed(Project project)
        {
            if (project != m_project)
            {
                return;
            }
            Shutdown();
        }

        public void Shutdown()
        {
            try
            {
                if (m_initialized)
                {
                    // ⚠️ 关键：先把 SubsystemTerrain 的 6 个字段切回**地表**世界再收尾。
                    // 否则 Project.Dispose() 会去 dispose 已经属于 mod 的下界对象（重复 dispose / 渲染与
                    // GL 资源被提前释放）→ 退出世界后黑屏。
                    if (m_active == m_deep && m_surface != null)
                    {
                        SuTerrainBinding.Apply(m_terrain, m_surface);
                        m_active = m_surface;
                        m_deepActive = false;
                    }
                    // 两个世界的实体都各自落盘，避免"在某个世界退出"导致另一个世界的动物/掉落物丢失
                    if (m_project != null)
                    {
                        SaveWorldEntities(0);
                        SaveWorldEntities(1);
                    }
                    SaveWorld(m_deep);
                    SaveWorld(m_surface);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[SuUndergroundWorld] 退出前保存失败：{0}", e.Message);
            }
            m_deep?.DisposeAll();

            m_project = null;
            m_terrain = null;
            m_gameInfo = null;
            m_worldDirectory = null;
            m_surface = null;
            m_deep = null;
            m_active = null;
            m_initialized = false;
            m_anchorSet = false;
            m_shaftCarved = false;
            m_demoDropDone = false;
            m_demoDropAt = 0.0;
            m_demoAscendDone = false;
            m_demoAscendArmed = false;
            m_demoAscendAt = 0.0;
            m_loggedNoAnchor = false;
            m_loggedSurfaceBlocked = false;
            m_fluidBridgeCounter = 0;
            m_crossColumns.Clear();
            SavePlayerPositions();
            m_mirroredFluid.Clear();
            m_wokenFluid.Clear();
            m_sourcePlugged.Clear();
            m_lastSurfaceFluid.Clear();
            m_worldEntities[0].Clear();
            m_worldEntities[1].Clear();
            m_worldPickables[0].Clear();
            m_worldPickables[1].Clear();
            m_worldMovingBlocks[0].Clear();
            m_worldMovingBlocks[1].Clear();
            m_subsystemPickables = null;
            m_waterBehavior = null;
            m_magmaBehavior = null;
            m_nextPhantomFluidAt = 0.0;
            m_phantomFluidLogged = false;
            m_subsystemMovingBlocks = null;
            m_hasSpawn = false;
            m_spawnSeeded = false;
            m_wasSleeping = false;
            m_nextExitHintAt = 0.0;
            m_spawnPosition = Vector3.Zero;
            m_spawnIsDeep = false;
            m_wasDead = false;
            m_waiting = false;
            m_waitBody = null;
            m_deepActive = false;
            m_bindFailed = false;
            m_bindAttempts = 0;
        }
    }
}
