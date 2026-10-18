using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using Engine;
using Game;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎桥：**整个 mod 里唯一碰引擎地形的地方**。
    ///
    /// 线程规约（唯一线程边界）：
    ///   · 引擎读写只在主线程（<c>Game</c> 的地形不是线程安全的）；
    ///   · 网络线程只允许 <see cref="PostFrame"/> 入队，绝不碰 Terrain；
    ///   · 主线程 <see cref="Pump"/> 里派发消息 → 同步服务 → 于是同步服务对引擎的
    ///     读（算摘要）与写（落地）**天然都发生在主线程**。
    ///
    /// 角色与业务逻辑都不在这里：这里只有"把引擎事实翻译成事件"和"把事件落回引擎"。
    /// 演示页标记：BRG01。
    /// </summary>
    public sealed class TerrainBridge : ITerrainWorld
    {
        private readonly ConcurrentQueue<ReceivedFrame> m_inbox = new ConcurrentQueue<ReceivedFrame>();
        /// <summary>
        /// 内存 / 常驻 / 上报集合与"超时未定稿"的唯一状态机（纯托管，见 <see cref="ChunkResidencyTracker"/>）。
        /// 曾经这里是 <c>Dictionary&lt;ChunkKey, TerrainChunk&gt; m_resident</c> + <c>HashSet m_memory</c>
        /// 两套集合，且常驻判据多叠了一个 <c>chunk.IsLoaded</c> —— 那是灰框根因（见 <see cref="HasContents"/>）。
        /// </summary>
        private readonly ChunkResidencyTracker m_residency = new ChunkResidencyTracker();
        private readonly int m_mainThreadId;
        private SubsystemTerrain m_subsystem;
        private bool m_reportedWrongThread;

        // ---- 按需单块加载（主机：客户端上报的边缘块，逐块临时地形更新位置）----
        //
        // 为什么要临时更新位置：TerrainUpdater 没有"加载某一块"的公开入口 —— 块的分配 / 推进只由
        // 更新位置的**内容距离**驱动（Survivalcraft/Game/TerrainUpdater.cs:AllocateAndFreeChunks /
        // FindBestChunkToUpdate）。所以"只加载这一块"的正确做法就是**为这一块挂一条临时更新位置**：
        //   · 中心 = 该块中心 ((x+0.5)*16, (z+0.5)*16)；
        //   · 可见 / 内容距离 = 8 格 → IsChunkInRange 的命中集合恒为该块本身（邻块中心相距 16 > 8）；
        //   · 槽位号从 OnDemandSlotBase 起（引擎按 PlayerIndex 索引玩家的更新位置，0..MaxGameWidgets-1，
        //     临时槽位一律 ≥ 1000，绝不与玩家槽位相撞）；
        //   · 每次请求（含重试）都用**新的槽位号**：SetUpdateLocation 对同一 index 的同值调用会被引擎的
        //     8 格滞回吞掉（TerrainUpdater.cs:258），换新 index 才能强制重新登记 / 让
        //     AllocateAndFreeChunks 跑一次。
        // 这条临时位置**只覆盖一块**：本类不碰任何角色的兴趣半径 / 内容距离，也不改
        // Terrain/HostTerrainLocations.cs 的账本（状态行里的 hostUpdateLocations 不受影响）。
        private readonly Dictionary<ChunkKey, int> m_onDemandSlots = new Dictionary<ChunkKey, int>();
        private readonly HashSet<int> m_onDemandSlotIds = new HashSet<int>();
        private const int OnDemandSlotBase = 1000;
        private const int OnDemandSlotCount = 4096;
        /// <summary>只覆盖目标块本身：中心到块边界 8 格，邻块中心相距 16 格。</summary>
        private const float OnDemandContentDistance = 8f;
        private int m_nextOnDemandSlot = OnDemandSlotBase;
        private long m_lastOnDemandSlotLogMs;
        /// <summary>当前挂着的临时单块加载位置数（状态行 / 设备核对用）。</summary>
        public int OnDemandLoadLocations => m_onDemandSlots.Count;
        /// <summary>累计创建过的临时单块加载位置数（每次重试都算一次）。</summary>
        public long OnDemandLoadLocationsCreated { get; private set; }

        // ---- 更新位置覆盖判定（"这块引擎还会不会推进它"）----
        private FieldInfo m_updateParametersField;
        private FieldInfo m_updateLocationsField;
        private FieldInfo m_locationCenterField;
        private FieldInfo m_locationContentDistanceField;
        private bool m_coverageUnavailableLogged;
        private readonly List<Vector2> m_locationCenters = new List<Vector2>(8);
        private readonly List<float> m_locationDistances = new List<float>(8);
        private readonly List<ChunkStallRecord> m_stallRecords = new List<ChunkStallRecord>(64);
        private long m_lastStallWarningMs;
        private const long StallWarningIntervalMs = 10000;
        private const int StallRecordsPerWarning = 6;

        public TerrainBridge()
        {
            m_mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// 客户端**引信表现**（组合根挂；主机 / 单机为 null）。只在 <see cref="ApplyCells"/> 把
        /// 主机 CellChange / 整块下发落到客户端之后调用 —— 这是客户端格值写入的唯一漏斗。
        /// 详见 <see cref="FuseReplicaPresentation"/>：只读格子 data，不倒计时、不爆炸。
        /// </summary>
        public FuseReplicaPresentation FusePresenter { get; set; }

        /// <summary>
        /// 客户端**营火表现**（组合根挂；主机 / 单机为 null）。只维护 FireParticleSystem：
        ///   · <c>SuSubsystemCampfireBlockBehavior</c> 的行为回调（区块生成 / 本桥补发的通知）；
        ///   · 1 秒一次的有界越界扫除（campfireStaleSweep）。
        /// 由 <see cref="BlockBehaviorNotify"/> 在 <see cref="ApplyCells"/> 落地后触发，不单独写格。
        /// </summary>
        public CampfireReplicaPresentation CampfirePresenter { get; set; }

        /// <summary>
        /// 客户端**移动组表现**（活塞 / 塌落；组合根挂；主机 / 单机为 null）。
        /// 只在 <see cref="ApplyCells"/> 把主机 CellChange / 整块下发落到客户端之后逐格通知：
        /// 权威格变化落在某个本地只表现集合的影响区内 → **立刻取消该集合**，接受主机写入的值。
        /// 不通知的后果（实机缺陷）：主机在碰撞 / 落地瞬间写下权威格，本地集合仍继续插值到原目标位置，
        /// 表现为活塞 / 塌落列穿过被推方块约半格，等 PistonMoveEnd 到了才回跳。
        /// 本类只移除 <c>SubsystemMovingBlocks</c> 里的表现对象，绝不写格（最终值就是主机的值）。
        /// 详见 <see cref="PistonReplicaEngine.OnAuthoritativeCellsApplied"/>。
        /// </summary>
        public PistonReplicaEngine PistonPresenter { get; set; }

        /// <summary>
        /// 客户端方块行为通知的单一漏斗（presentation-only；镜像 ChangeCell 的
        /// OnBlockRemoved / OnBlockAdded / OnBlockModified 派发规则）。
        /// 只派发到纯表现行为（营火外壳 / 火把）；其余行为被拒绝并计数
        /// clientBehaviorDeniedWrites（客户端保持 mutation-denied）。
        /// </summary>
        public ClientBlockBehaviorNotifier BlockBehaviorNotify { get; } =
            new ClientBlockBehaviorNotifier();

        // ---- 主线程上触发的事件（模块订阅这些，模块不直接看引擎）----
        /// <summary>区块内容可用（新鲜加载 / 重载）。**摘要 / 校验只认这个信号**。</summary>
        public event Action<ChunkKey> ChunkLoaded;
        /// <summary>区块内容不再可用（卸载，或掉回未定稿状态）。常驻集合移除。</summary>
        public event Action<ChunkKey> ChunkUnloaded;
        /// <summary>区块被引擎**分配**（进入内存；内容可能还没定稿 —— 海洋 / 水面就是这种）。</summary>
        public event Action<ChunkKey> ChunkAllocated;
        /// <summary>区块被引擎**释放**（离开内存）。</summary>
        public event Action<ChunkKey> ChunkFreed;
        /// <summary>
        /// 区块在内存里超过 <see cref="ChunkResidencyTracker.GraceMs"/> 仍未定稿 → **明确不再作为
        /// "内存区块"上报**（实时地图不再挂一个永远等不到裁决的灰框）。第二个参数 = 可直接写进日志的原因。
        /// 与 <see cref="ChunkFreed"/> 的区别：块**还在引擎内存里**，只是没有可核对的内容。
        /// </summary>
        public event Action<ChunkKey, string> ChunkWireExcluded;
        /// <summary>之前被 <see cref="ChunkWireExcluded"/> 排除的块后来定稿了 → 重新纳回上报集合。</summary>
        public event Action<ChunkKey> ChunkWireIncluded;
        /// <summary>网络消息（已切到主线程）。</summary>
        public event Action<ReceivedFrame> FrameArrived;
        /// <summary>主机侧权威改动（已捕获，尚未下发）。</summary>
        public event Action<ChunkKey, CellEdit> HostCellChanged;
        /// <summary>
        /// 客户端侧捕获到的**本地地形写入**（主线程）。架构上客户端只允许主机改地形，
        /// 所以这些写入必须变成 CellEditRequest 交给主机审批（Part B）。
        /// </summary>
        public event Action<ClientCellEdit> ClientCellEdited;

        /// <summary>
        /// 本端是不是"联机客户端副本"。模块在角色确定后写入；写入前桥保持主机行为
        /// （不捕获、不下发 —— 没有会话时两端都不该有网络行为）。
        /// 角色切换时把客户端**待捕获**的本地写入明确作废并计数：旧角色下攒下的格子
        /// 不能在切换后冒充新角色的编辑发出去。
        /// </summary>
        public bool ClientReplica
        {
            get => m_clientReplica;
            set
            {
                if (m_clientReplica == value) return;
                m_clientReplica = value;
                int dropped = m_clientCapture.Reset(ClientCaptureDropReason.RoleChanged);
                if (dropped > 0)
                {
                    m_clientCaptureDroppedTotal += dropped;
                    Log.Warning("[SuMP] clientCapture: 角色切换清掉 " + dropped +
                        " 格待捕获的本地写入（已计入 captureDropped[roleChanged]，不静默）");
                }
            }
        }

        private bool m_clientReplica;
        /// <summary>客户端捕获暂存（跨帧重试；世界卸载 / 角色切换 / 超限都按原因计数）。</summary>
        private readonly ClientEditCaptureQueue m_clientCapture = new ClientEditCaptureQueue();
        /// <summary>捕获重试的帧号（只增不减；用于判定"连续多少帧读不到"）。</summary>
        private long m_captureFrame;
        /// <summary>扫描待捕获格时的复用缓冲（避免每帧分配）。</summary>
        private readonly List<Point3> m_clientCaptureScan = new List<Point3>(64);
        private long m_clientCaptureDroppedTotal;
        private long m_clientCaptureApplyFailures;
        private long m_lastClientCaptureLogMs;
        private long m_lastClientCaptureDropLogMs;
        private const long ClientCaptureLogIntervalMs = 5000;

        /// <summary>客户端捕获各级计数 + 每种丢弃原因（状态行 / 自检用）。</summary>
        public string ClientCaptureStats =>
            m_clientCapture.Describe() + " captureApplyFailed=" + m_clientCaptureApplyFailures;
        /// <summary>客户端捕获累计丢弃格数（含世界卸载 / 角色切换 / 超限 / 过期）。</summary>
        public long ClientCaptureDropped => m_clientCaptureDroppedTotal;

        /// <summary>常驻（内容可用）区块数。校验 / 摘要口径。</summary>
        public long ResidentCount => m_residency.ResidentCount;
        /// <summary>引擎已分配（内存中）区块数。实时地图客户端线框口径。</summary>
        public long AllocatedCount => m_residency.MemoryCount;
        /// <summary>
        /// 已在内存、但内容还没定稿的区块数（= 实时地图上"客户端上报过内存、却永远没有裁决"的灰框数）。
        /// 修复后长期为 0；<see cref="DescribeResidency"/> 里有 per-reason 的停滞账。
        /// </summary>
        public int MemoryNotResident => m_residency.MemoryNotResident;
        /// <summary>累计"内容定稿"次数（含卸载后重载的每一次 —— 重载必须再报一次）。</summary>
        public long ResidentFinalized => m_residency.ResidentFinalized;
        /// <summary>内存 / 常驻 / 未定稿 / 停滞 / 上报集合的可审计一行（状态行用它）。</summary>
        public string DescribeResidency() => m_residency.Describe();
        public long AppliedCells;
        public long PumpedFrames;
        public long AppliedBatches;
        /// <summary>主机：真正交给广播管线的格子数（= 每一格一次；漏格/重复分别见下面两个计数器）。</summary>
        public long HostCellsCaptured;
        /// <summary>主机：捕获时区块已不在内存（读不到权威值）而**明确跳过**的格子数（绝不猜值）。</summary>
        public long HostCellMisses;
        /// <summary>主机：被去重掉的格子数（同一格既在引擎改动表、又被行为登记 / 接住）。</summary>
        public long HostCellDuplicates;
        private long m_lastHostMissLogMs;
        private const long HostMissLogIntervalMs = 10000;

        // ---- 网络线程 → 主线程 ----
        /// <summary>网络线程调用：只入队（不能用 in 参数，否则匹配不上 Action&lt;ReceivedFrame&gt;）。</summary>
        public void PostFrame(ReceivedFrame frame) => m_inbox.Enqueue(frame);

        // ---- ITerrainWorld：全部在主线程被调用 ----
        public bool HasContents(ChunkKey key)
        {
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null) return false;
            TerrainChunk chunk = terrain.GetChunkAtCoords(key.X, key.Z);
            // 与引擎序列化器**同一判据**：内容可用 = State 超过 InvalidContents4
            // （TerrainSerializer23.SaveChunk / TerrainSerializer22.SaveChunk 用的就是这一句）。
            //
            // **绝不能再叠加 TerrainChunk.IsLoaded**（灰框根因，2026-10-18 实机定位）：
            //   · IsLoaded 只在 TerrainUpdater.UpdateChunkSingleStep 里 TerrainSerializer.LoadChunk
            //     成功时置位（TerrainUpdater.cs:735）—— 它表示"这块来自存档文件"，不是"内容已定稿"；
            //   · 引擎**自己生成**的块从不写盘（SaveChunk 只在 ModificationCounter > 0 时保存），
            //     所以生成出来的块永远是 IsLoaded == false，哪怕 State == Valid、玩家正站在上面；
            //   · 旧判据把它们全部挡在常驻集合外 ⇒ 不触发 ChunkLoaded ⇒ 不算摘要、不上报 ⇒
            //     主机永远没有裁决 ⇒ 实时地图 100% 灰框（实机：客户端 已分配 201 / 常驻 93，
            //     它脚下 (34,9) 有真实 Dirt/Grass + 天光 14）。
            // 修复前的老实现（Mod/ScMultiplayer）用 TerrainUpdater.ChunkInitialized 判定，
            // 根本没有 IsLoaded 这一层，所以没有这个 bug。
            return chunk != null && ChunkContentStages.IsContentValid((int)chunk.State);
        }

        public ChunkCellSource GetCellSource(ChunkKey key)
        {
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null)
                return (int x, int y, int z, out int contents, out int data) => { contents = 0; data = 0; };
            int baseX = key.X * 16;
            int baseZ = key.Z * 16;
            return (int x, int y, int z, out int contents, out int data) =>
            {
                int value = terrain.GetCellValueFast(baseX + x, y, baseZ + z);
                // 光照不进摘要：只取 contents 与 data
                contents = Game.Terrain.ExtractContents(value);
                data = Game.Terrain.ExtractData(value);
            };
        }

        /// <summary>
        /// 落地一批格。**主线程调用**（来自同步服务处理重传/改动消息时）。
        /// 用 SetCellValueFast + 邻域降级，绕过 ChangeCell —— 否则回传内容会把块重新标脏，
        /// 又触发一轮同步（旧实现踩过的坑）。
        /// </summary>
        public void ApplyCells(ChunkKey key, IReadOnlyList<CellEdit> edits)
        {
            EnsureMainThread("ApplyCells");
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null || edits == null || edits.Count == 0) return;
            int baseX = key.X * 16;
            int baseZ = key.Z * 16;
            TerrainChunk chunk = terrain.GetChunkAtCell(baseX + 8, baseZ + 8);
            if (chunk == null || chunk.State < TerrainChunkState.InvalidLight) return;   // 块没加载：丢弃（上层会重问）
            SubsystemTerrain subsystem = m_subsystem ??
                GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            for (int i = 0; i < edits.Count; i++)
            {
                CellEdit edit = edits[i];
                if (edit.Y < 0 || edit.Y > 255) continue;
                int x = baseX + edit.X;
                int z = baseZ + edit.Z;
                int value = Game.Terrain.MakeBlockValue(edit.Contents, 0, edit.Data);
                int oldValue = terrain.GetCellValueFast(x, edit.Y, z);
                terrain.SetCellValueFast(x, edit.Y, z, value);
                // 客户端补发方块行为通知（单一漏斗）：SetCellValueFast 绕过 ChangeCell，不补就
                // 没有 OnBlockAdded/Removed/Modified → 营火/火把等 behavior 自己维护的粒子永远陈旧。
                // 只派发纯表现行为；会写格 / 生成实体的行为被拒绝并计数（mutation-denied）。
                // 与 ChangeCell 同一比较口径：光照位不参与"是否变化"（ChangeCell 先 ReplaceLight(0)）。
                if (ClientReplica && Game.Terrain.ReplaceLight(oldValue, 0) != value)
                    BlockBehaviorNotify.Notify(subsystem, oldValue, value, x, edit.Y, z);
            }
            AppliedCells += edits.Count;
            AppliedBatches++;
            // 客户端引信表现：主机把"剩余引信 tick"写进火药桶格子的 data（Terrain/FuseCellState.cs），
            // 这里落地后立即按同一份权威值更新粒子（不重建在烧的引信；熄灭/换方块则收掉）。
            // 只在客户端副本调用：主机的引信粒子由引擎 SubsystemExplosivesBlockBehavior 自己维护。
            if (ClientReplica) FusePresenter?.Apply(key, edits);
            // 客户端移动组表现（活塞 / 塌落）：权威格变化落在本地只表现集合（起点格 / 方块扫掠区）
            // 的影响区内 → 立刻移除该本地集合，接受主机格值。主机在碰撞 / 落地瞬间写格，
            // 本地集合若继续插值就会穿过被推方块约半格（实机缺陷）；这里只移除表现对象，绝不写格。
            if (ClientReplica) PistonPresenter?.OnAuthoritativeCellsApplied(key, edits);
            // 与 SubsystemTerrain.ChangeCell 同源的"降级邻域"，让几何/光照在正常流程里重算。
            // DowngradeChunkNeighborhoodState 是**实例**方法（旧实现能直接写，是因为它本身派生自
            // SubsystemTerrain），所以这里借子系统实例来调。
            // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
            m_subsystem?.TerrainUpdater?.DowngradeChunkNeighborhoodState(chunk.Coords, 1,
                TerrainChunkState.InvalidLight, forceGeometryRegeneration: false);
        }

        /// <summary>
        /// 主机：请求引擎**只加载这一块** —— 为它挂一条临时地形更新位置（口径见本类顶部的注释）。
        /// 返回 false = 引擎当前明确不接受（原因写 <paramref name="reason"/>），**绝不假装已请求**。
        /// 同一块再次请求（重试）= 先摘旧槽位、再换一个新槽位重新登记（引擎的 8 格滞回会吞掉同值调用，
        /// 只有换 index 才能强制 AllocateAndFreeChunks 重跑）。
        /// </summary>
        public bool TryRequestSingleChunkLoad(ChunkKey key, out string reason)
        {
            reason = null;
            EnsureMainThread("TryRequestSingleChunkLoad");
            SubsystemTerrain subsystem = m_subsystem ?? GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            TerrainUpdater updater = subsystem?.TerrainUpdater;
            if (updater == null || subsystem.Terrain == null)
            {
                reason = "找不到 SubsystemTerrain.TerrainUpdater（世界未加载 / 正在卸载）";
                return false;
            }
            if (HasContents(key)) return true;       // 已经有内容：不需要临时位置（调用方下一拍就会看到）
            if (m_onDemandSlots.TryGetValue(key, out int previous))
            {
                updater.RemoveUpdateLocation(previous);
                m_onDemandSlots.Remove(key);
                m_onDemandSlotIds.Remove(previous);
            }
            if (!TryAllocateOnDemandSlot(out int slot))
            {
                reason = "临时单块加载位置已满（上限 " + OnDemandSlotCount + "）";
                return false;
            }
            // 中心 = 块中心；距离 8 格 → 引擎的 IsChunkInRange 只命中这一块（邻块中心相距 16 格）。
            var center = new Vector2(((float)key.X + 0.5f) * 16f, ((float)key.Z + 0.5f) * 16f);
            updater.SetUpdateLocation(slot, center, OnDemandContentDistance, OnDemandContentDistance);
            m_onDemandSlots[key] = slot;
            m_onDemandSlotIds.Add(slot);
            OnDemandLoadLocationsCreated++;
            return true;
        }

        /// <summary>
        /// 主机：撤销 <see cref="TryRequestSingleChunkLoad"/> 的临时位置（块交回引擎按它自己的更新位置决定去留；
        /// 下一帧 TerrainUpdater.Update 应用这条移除，必要时回收该块）。没有登记过就什么也不做。
        /// </summary>
        public void ReleaseSingleChunkLoad(ChunkKey key)
        {
            EnsureMainThread("ReleaseSingleChunkLoad");
            if (!m_onDemandSlots.TryGetValue(key, out int slot)) return;
            m_onDemandSlots.Remove(key);
            m_onDemandSlotIds.Remove(slot);
            SubsystemTerrain subsystem = m_subsystem ?? GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            subsystem?.TerrainUpdater?.RemoveUpdateLocation(slot);
        }

        /// <summary>
        /// 世界没了（卸载 / 换世界）→ 引擎里那些临时更新位置随世界一起消失：账本**同步清空**并限流写一行，
        /// 免得把上一个世界的槽位当成还挂着（那些槽位在新世界里已经不存在，继续留着就是假状态）。
        /// </summary>
        private void DropOnDemandSlots(string reason)
        {
            int count = m_onDemandSlots.Count;
            m_onDemandSlots.Clear();
            m_onDemandSlotIds.Clear();
            long now = Environment.TickCount64;
            if (m_lastOnDemandSlotLogMs != 0 && now - m_lastOnDemandSlotLogMs < StallWarningIntervalMs) return;
            m_lastOnDemandSlotLogMs = now;
            Log.Warning("[SuMP] onDemandLoad: " + count + " 条临时单块地形更新位置随世界一起丢失（" + reason +
                "）→ 账本已同步清空（不当作还挂着）；累计创建 " + OnDemandLoadLocationsCreated + " 条");
        }

        private bool TryAllocateOnDemandSlot(out int slot)
        {
            for (int i = 0; i < OnDemandSlotCount; i++)
            {
                int candidate = m_nextOnDemandSlot;
                m_nextOnDemandSlot = OnDemandSlotBase +
                    (candidate - OnDemandSlotBase + 1) % OnDemandSlotCount;
                if (m_onDemandSlotIds.Contains(candidate)) continue;
                slot = candidate;
                return true;
            }
            slot = 0;
            return false;
        }

        // ---------------------------------------------------------------- 客户端副本 / 主机权威

        /// <summary>
        /// 客户端：把一帧里被本地写入的格子**捕获**成编辑请求（主线程，ProcessModifiedCells 之前）。
        /// 不在这里直接发网络包：模块在同一线程里把每一格变成 CellEditRequest 并通过 TCP 发出去。
        ///
        /// 关键（实机丢编辑根因）：格子先**进暂存**再读值。Project/Terrain 瞬时读不到、区块内容还没定稿、
        /// 模块订阅者暂时不在 —— 这些都不丢弃，保留到下一帧重试；只有世界真的卸载 / 角色切换 /
        /// 超过重试上限 / 超过容量才**明确丢弃并计数**（<see cref="ClientCaptureStats"/> 每级可见）。
        /// 旧实现在 terrain == null 时直接 return，调用方随后照常 ProcessModifiedCells 清表 ⇒ 永久丢失。
        /// </summary>
        public void CaptureClientEdits(ICollection<Point3> cells)
        {
            EnsureMainThread("CaptureClientEdits");
            if (cells == null || cells.Count == 0) return;
            m_captureFrame++;
            m_clientCapture.Stage(cells, m_captureFrame);
            FlushClientCapture();
        }

        /// <summary>
        /// 客户端每帧一次：推进**上一帧遗留**的待捕获格（读得到就交付，读不到继续等）。
        /// 只在暂时读不到地形 / 区块未定稿 / 没有订阅者时才有待捕获格；平时是空队列、零开销。
        /// </summary>
        public void RetryDeferredClientEdits()
        {
            EnsureMainThread("RetryDeferredClientEdits");
            if (m_clientCapture.PendingCount == 0) return;
            m_captureFrame++;
            FlushClientCapture();
        }

        /// <summary>推进客户端捕获暂存：能读的格子立刻交给模块，读不到的保留重试。</summary>
        private void FlushClientCapture()
        {
            if (m_clientCapture.PendingCount == 0) return;
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null)
            {
                // Project 还在 = 引用瞬时不可用 → 保留重试；世界已卸载 = 这些格子属于旧世界 → 明确丢弃。
                if (GameManager.Project == null)
                {
                    int dropped = m_clientCapture.Reset(ClientCaptureDropReason.WorldUnloaded);
                    NoteClientCaptureDrop("世界已卸载（Project == null）", dropped);
                }
                else
                {
                    m_clientCapture.NoteDeferredFrame();
                    LogClientCaptureDeferred("读不到当前地形（Project 还在 → 下一帧重试）");
                }
                m_clientCapture.Expire(m_captureFrame);
                return;
            }
            Action<ClientCellEdit> handler = ClientCellEdited;
            if (handler == null)
            {
                m_clientCapture.NoteDeferredFrame();
                LogClientCaptureDeferred("没有本地编辑订阅者（模块未装配 / 正在下线 → 下一帧重试）");
                NoteClientCaptureExpired(m_clientCapture.Expire(m_captureFrame));
                return;
            }
            m_clientCaptureScan.Clear();
            m_clientCapture.CopyPending(m_clientCaptureScan);
            bool deferred = false;
            for (int i = 0; i < m_clientCaptureScan.Count; i++)
            {
                Point3 cell = m_clientCaptureScan[i];
                TerrainChunk chunk = terrain.GetChunkAtCell(cell.X, cell.Z);
                // 与 PublishHostCells / ApplyCells 同一判据：内容未定稿时读出来的是 0（空气），
                // 绝不能把"读不到"当成本地写入的最终值发出去 → 保留重试。
                if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                {
                    deferred = true;
                    continue;
                }
                int value = terrain.GetCellValueFast(cell.X, cell.Y, cell.Z);
                try
                {
                    handler(new ClientCellEdit(cell.X, cell.Y, cell.Z,
                        Game.Terrain.ExtractContents(value), Game.Terrain.ExtractData(value)));
                }
                catch (Exception e)
                {
                    // 模块回调抛异常：**保留该格重试**并计数。异常绝不能让这一帧的捕获静默消失
                    //（SubsystemUpdate 会吞掉 Update 抛出的异常，日志里什么都看不到）。
                    m_clientCaptureApplyFailures++;
                    long now = Environment.TickCount64;
                    if (m_lastClientCaptureDropLogMs == 0 ||
                        now - m_lastClientCaptureDropLogMs >= ClientCaptureLogIntervalMs)
                    {
                        m_lastClientCaptureDropLogMs = now;
                        Log.Warning("[SuMP] clientCaptureApplyFailed: " + cell.X + "," + cell.Y + "," +
                            cell.Z + " 交付模块异常（" + e.GetType().Name + " " + e.Message +
                            "）→ 该格保留重试；累计 " + m_clientCaptureApplyFailures + " 次");
                    }
                    continue;
                }
                m_clientCapture.Complete(cell);
            }
            if (deferred)
            {
                m_clientCapture.NoteDeferredFrame();
                LogClientCaptureDeferred("区块内容未定稿（保留重试，不猜 0=空气）");
            }
            NoteClientCaptureExpired(m_clientCapture.Expire(m_captureFrame));
        }

        private void NoteClientCaptureExpired(int expired)
        {
            if (expired <= 0) return;
            NoteClientCaptureDrop("连续 " + ClientEditCaptureQueue.MaxRetryFrames +
                " 帧无法交付（读不到地形 / 区块未定稿 / 无订阅者）", expired);
        }

        private void NoteClientCaptureDrop(string reason, int count)
        {
            if (count <= 0) return;
            m_clientCaptureDroppedTotal += count;
            long now = Environment.TickCount64;
            if (m_lastClientCaptureDropLogMs != 0 &&
                now - m_lastClientCaptureDropLogMs < ClientCaptureLogIntervalMs)
            {
                return;
            }
            m_lastClientCaptureDropLogMs = now;
            Log.Warning("[SuMP] clientCaptureDropped: " + count + " 格本地写入没能上报（" + reason +
                "）→ 累计丢弃 " + m_clientCaptureDroppedTotal + " 格；" + m_clientCapture.Describe());
        }

        private void LogClientCaptureDeferred(string reason)
        {
            // 帧级重试是热路径：只在第一次 / 每 ~5s 写一行，计数始终在 ClientCaptureStats 里。
            long now = Environment.TickCount64;
            if (m_lastClientCaptureLogMs != 0 &&
                now - m_lastClientCaptureLogMs < ClientCaptureLogIntervalMs)
            {
                return;
            }
            m_lastClientCaptureLogMs = now;
            Log.Warning("[SuMP] clientCaptureDeferred: " + reason + "；" + m_clientCapture.Describe());
        }

        /// <summary>主机：读一格当前值（编辑校验 / already 判定）。</summary>
        public bool TryReadCell(int x, int y, int z, out int contents, out int data)
        {
            contents = 0;
            data = 0;
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null || y < 0 || y > 255) return false;
            if (!terrain.IsCellValid(x, y, z)) return false;
            int value = terrain.GetCellValueFast(x, y, z);
            contents = Game.Terrain.ExtractContents(value);
            data = Game.Terrain.ExtractData(value);
            return true;
        }

        /// <summary>主机：这一块内容是否可用（编辑请求的落地前提）。</summary>
        public bool IsChunkLoaded(int chunkX, int chunkZ) => HasContents(new ChunkKey(chunkX, chunkZ));

        /// <summary>
        /// 主机："客户端编辑造成的方块销毁 → 掉落"的引擎端口（组合根注入 = PickableReplicaEngine）。
        /// null = 还没装配：编辑照常落地，但**明确**记 no-host-drops 并告警一次（绝不假装生成）。
        /// </summary>
        public IHostBlockDropSink HostBlockDrops { get; set; }

        private bool m_hostDropSinkMissingLogged;
        private long m_lastDropPrepareLogMs;
        private const long DropPrepareLogIntervalMs = 5000;

        /// <summary>
        /// 主机：按**权威路径**落地一条客户端编辑（<c>SubsystemTerrain.ChangeCell</c>）。
        /// 特意不用 SetCellValueFast：ChangeCell 会跑方块行为、进 m_modifiedCells，
        /// 因此既有 CellChange 广播会把最终结果（可能被行为改写）发给所有客户端。
        ///
        /// 方块销毁的掉落（本轮新增）：请求把非空气方块改成别的值（挖掉 / 放置挤掉）时，
        /// 按引擎 <c>SubsystemTerrain.DestroyCell</c> 的**同一顺序**补生成掉落：
        ///   ① 改格前：<c>block.GetDropValues(terrain, oldValue, requestedValue, toolLevel, ...)</c>
        ///      + 逐条 <c>OnItemHarvested</c>（可改写要写入的值 / 掉落本身）；
        ///   ② <c>ChangeCell</c>；
        ///   ③ 改格后：<c>SubsystemPickables.AddPickable(..., position = 格心, velocity = null, ...)</c>
        ///      （引擎默认随机上抛）。若引擎自己的落地路径（方块行为的 OnBlockRemoved 等）已经在
        ///      该格生成过拾取物，就不再生成（<see cref="EditApplyOutcome.EngineSpawnedPickables"/>），
        ///      绝不双份。
        /// Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.DestroyCell
        /// Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        /// </summary>
        public bool TryApplyHostChange(int clientId, int x, int y, int z, int contents, int data,
            int toolLevel, bool hasToolLevel,
            out EditApplyOutcome outcome, out string reason)
        {
            EnsureMainThread("TryApplyHostChange");
            outcome = default;
            reason = null;
            SubsystemTerrain subsystem = m_subsystem ??
                GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (subsystem == null || subsystem.Terrain == null)
            {
                reason = "主机地形子系统不可用";
                return false;
            }
            if (!subsystem.Terrain.IsCellValid(x, y, z))
            {
                reason = "坐标越界";
                return false;
            }
            int oldValue;
            try
            {
                oldValue = subsystem.Terrain.GetCellValueFast(x, y, z);
            }
            catch (Exception e)
            {
                reason = "读改格前值失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            int oldContents = Game.Terrain.ExtractContents(oldValue);
            int requestedValue = Game.Terrain.MakeBlockValue(contents, 0, data);
            // "销毁"= 旧格非空气、且请求要把 contents 改成别的。data-only 改动不算销毁。
            bool destroying = EditDropPolicy.DestroysOldBlock(oldContents, contents);
            HostBlockDropPlan plan = null;
            if (destroying)
            {
                if (!hasToolLevel)
                {
                    outcome.SkipReason = EditDropSkipReason.NoToolLevel;
                }
                else
                {
                    IHostBlockDropSink sink = HostBlockDrops;
                    if (sink == null)
                    {
                        outcome.SkipReason = EditDropSkipReason.NoHostDrops;
                        NoteHostDropSinkMissing();
                    }
                    else if (!sink.TryPrepareBlockDrops(clientId, x, y, z, oldValue,
                                 requestedValue, toolLevel, out plan, out string prepReason) ||
                             plan == null)
                    {
                        plan = null;
                        outcome.SkipReason = EditDropSkipReason.SpawnFailed;
                        long nowMs = Environment.TickCount64;
                        if (m_lastDropPrepareLogMs == 0 ||
                            nowMs - m_lastDropPrepareLogMs >= DropPrepareLogIntervalMs)
                        {
                            m_lastDropPrepareLogMs = nowMs;
                            Log.Warning("[SuMP] editDropPrepareFailed: cell=" + x + "," + y + "," + z +
                                " value=" + oldValue + " → " + (prepReason ?? "unknown") +
                                "（编辑照常落地，但这一格没有补生成掉落；已计入 " +
                                "dropFromEditSkipped[spawn-failed]，同原因限流 " +
                                (DropPrepareLogIntervalMs / 1000) + "s）");
                        }
                    }
                }
            }
            int changeValue = plan != null ? plan.NewValue : requestedValue;
            try
            {
                // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
                subsystem.ChangeCell(x, y, z, changeValue);
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
                return false;
            }
            int value = subsystem.Terrain.GetCellValueFast(x, y, z);
            outcome.ResultContents = Game.Terrain.ExtractContents(value);
            outcome.ResultData = Game.Terrain.ExtractData(value);
            outcome.ResultDiffersFromRequest = value != requestedValue;
            outcome.DestroyedOldBlock = oldContents != 0 && outcome.ResultContents != oldContents;
            if (!destroying)
            {
                outcome.SkipReason = EditDropPolicy.NoDestructionReason(oldContents);
                return true;
            }
            if (!outcome.DestroyedOldBlock)
            {
                // 请求要改掉一个非空气方块，落地后旧方块还在（被行为改回去了）：不生成掉落。
                outcome.SkipReason = EditDropSkipReason.NotDestroyed;
                return true;
            }
            if (plan == null)
            {
                if (outcome.SkipReason == EditDropSkipReason.None)
                    outcome.SkipReason = EditDropSkipReason.SpawnFailed;
                return true;
            }
            IHostBlockDropSink spawnSink = HostBlockDrops;
            if (spawnSink == null)
            {
                outcome.SkipReason = EditDropSkipReason.NoHostDrops;
                NoteHostDropSinkMissing();
                return true;
            }
            if (spawnSink.TrySpawnPreparedDrops(plan, out int spawned, out bool engineSpawned,
                    out string spawnReason))
            {
                outcome.HostSpawnedDrops = spawned > 0;
                outcome.SpawnedPickables = spawned;
                outcome.SkipReason = spawned > 0
                    ? EditDropSkipReason.None
                    : EditDropSkipReason.NoDropValues;
            }
            else
            {
                outcome.EngineSpawnedPickables = engineSpawned;
                outcome.SkipReason = engineSpawned
                    ? EditDropSkipReason.EngineSpawned
                    : (spawnReason == "no-drop-values"
                        ? EditDropSkipReason.NoDropValues
                        : EditDropSkipReason.SpawnFailed);
            }
            return true;
        }

        private void NoteHostDropSinkMissing()
        {
            if (m_hostDropSinkMissingLogged) return;
            m_hostDropSinkMissingLogged = true;
            Log.Warning("[SuMP] 主机没有注入方块掉落的引擎端口（TerrainBridge.HostBlockDrops == null）→ " +
                "客户端编辑造成的方块销毁不会补生成拾取物（已计入 dropFromEditSkipped[no-host-drops]）；" +
                "请检查组合根装配");
        }

        /// <summary>
        /// 客户端：把一格恢复为主机权威值（拒绝 / 超时回滚）。
        /// 走"不标脏"的路径（SetCellValueFast + 邻域降级）：回滚不是一次新的玩家编辑，
        /// 不能再次被捕获、也不能触发方块行为。
        /// </summary>
        public bool TryApplyReplicaValue(int x, int y, int z, int contents, int data)
        {
            EnsureMainThread("TryApplyReplicaValue");
            if (y <= 0 || y >= 255) return false;
            ApplyCells(new ChunkKey(x >> 4, z >> 4),
                new[] { new CellEdit(x & 15, y, z & 15, contents, data) });
            return true;
        }

        /// <summary>内容可用（常驻）的区块：摘要 / 裁决 / 回传唯一认这一份。</summary>
        public IEnumerable<ChunkKey> LoadedChunks => m_residency.ResidentChunks;

        /// <summary>
        /// 参与"内存区块"上报的区块键（v8 实时地图客户端线框口径）。
        /// 正常 = 引擎已分配的全部块（用户口径："方框应该是整个客户端的内存中所有的 chunk"）；
        /// 唯一例外：在内存里超过 <see cref="ChunkResidencyTracker.GraceMs"/> 仍未定稿的块会被
        /// **明确排除**（<see cref="ChunkWireExcluded"/> + 限流 Warning + per-reason 计数）——
        /// 否则地图上会永远挂着一块无法核对、也永远等不到裁决的灰框。
        /// </summary>
        public IEnumerable<ChunkKey> MemoryChunks => m_residency.WireChunks;

        /// <summary>
        /// 枚举引擎里**所有已分配**的 chunk（= 引擎手上真实存在的整片区域）。
        /// 与 <see cref="LoadedChunks"/> 的区别：那是同步用的严格集合（内容已定稿）；
        /// 实时地图要"整个地图区域"，所以把分配的块都拿出来看一遍 ——
        /// 还没有地表的块 → 列全空 → <c>IsEmpty()</c> 会被上层跳过，不会画成假地形。
        /// </summary>
        public List<ChunkKey> EnumerateAllocatedChunks()
        {
            EnsureMainThread("EnumerateAllocatedChunks");
            var result = new List<ChunkKey>();
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null) return result;
            TerrainChunk[] chunks = terrain.AllocatedChunks;
            if (chunks == null) return result;
            for (int i = 0; i < chunks.Length; i++)
            {
                TerrainChunk chunk = chunks[i];
                if (chunk == null) continue;
                result.Add(new ChunkKey(chunk.Coords.X, chunk.Coords.Y));
            }
            return result;
        }

        /// <summary>
        /// 取一块的**俯视地表**（实时地图用）：每列用引擎缓存的 <c>GetTopHeight</c> 定位地表，
        /// 再读那一格的 contents —— 一列两次读取。
        /// （逐格从 255 往下扫要一百多次读取/列，200 块就是几百万次，会在主机上顶出卡顿。）
        /// </summary>
        public SuMultiplayer.Terrain.LiveMapChunk BuildLiveMapChunk(ChunkKey key)
        {
            EnsureMainThread("BuildLiveMapChunk");
            var chunk = new SuMultiplayer.Terrain.LiveMapChunk { X = key.X, Z = key.Z };
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null) return chunk;
            int baseX = key.X * 16;
            int baseZ = key.Z * 16;
            for (int x = 0; x < 16; x++)
            {
                for (int z = 0; z < 16; z++)
                {
                    int worldX = baseX + x;
                    int worldZ = baseZ + z;
                    int topY = terrain.GetTopHeight(worldX, worldZ);
                    int contents = 0;
                    if (topY > 0 && topY <= 255)
                    {
                        contents = terrain.GetCellContentsFast(worldX, topY, worldZ);
                        if (contents == 0)
                        {
                            // 缓存的顶高偶尔会和实际不一致（刚被改过的列）：兜一次真实计算，不猜。
                            topY = terrain.CalculateTopmostCellHeight(worldX, worldZ);
                            contents = topY > 0 ? terrain.GetCellContentsFast(worldX, topY, worldZ) : 0;
                        }
                    }
                    int index = x + 16 * z;
                    chunk.Contents[index] = (ushort)Math.Max(0, Math.Min(contents, 65535));
                    chunk.Top[index] = (byte)Math.Max(0, Math.Min(topY, 255));
                }
            }
            return chunk;
        }

        // ---- 主线程：每帧一次 ----
        /// <summary>
        /// 主线程泵：① 派发网络消息 ② 比对已分配 / 常驻集合产生分配 / 释放 + 加载 / 卸载事件。
        /// 由 <c>SuSubsystemTerrain.Update</c> 调用（那里是引擎每帧必经之地）。
        /// </summary>
        public void Pump(SubsystemTerrain subsystem = null)
        {
            EnsureMainThread("Pump");
            if (subsystem != null) m_subsystem = subsystem;
            Shot();
            int budget = 256;
            while (budget-- > 0 && m_inbox.TryDequeue(out ReceivedFrame frame))
            {
                PumpedFrames++;
                FrameArrived?.Invoke(frame);
            }
        }

        /// <summary>
        /// 比对引擎的**已分配**区块集合，把"进入 / 离开内存""内容定稿 / 掉回未定稿""超时未定稿 →
        /// 不再上报"翻译成事件。**这是本 mod 唯一的"块加载完"信号**（引擎只暴露
        /// <c>TerrainUpdater.ChunkInitialized</c>，没有卸载事件，所以只能这样比对）。
        ///
        /// 状态机在 <see cref="ChunkResidencyTracker"/>（纯托管、可被 P:\tmp\tsverify 直接编译）；
        /// 这里只负责：① 取引擎事实（AllocatedChunks + State）；② 把变化发成事件；
        /// ③ 对"超过宽限仍未定稿"的块写限流 Warning（含原因），绝不静默。
        ///
        /// 复杂度（第 4 项自查）：每帧 O(已分配块) 次字典读取 + 每个更新位置一次距离平方，**n 有上界**——
        /// 引擎的已分配块集合就是"地形更新位置内容距离内的方块"（TerrainUpdater.AllocateAndFreeChunks），
        /// 也就是客户端声明的可见范围（radius×16，最大 32 区块 ≈ 65×65）。实测平板 201 块 / 1~2 个
        /// 更新位置，合计 ~400 次读取（微秒级），相对 16.7 ms 的帧预算可以忽略；
        /// 真正的单帧尖峰来自摘要计算，已按每帧预算搬到 <c>TerrainSyncClient.Tick</c>。
        /// </summary>
        private void Shot()
        {
            long nowMs = Environment.TickCount64;
            m_residency.BeginFrame(nowMs);
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null && m_onDemandSlots.Count > 0) DropOnDemandSlots("当前没有地形（世界卸载 / 正在换世界）");
            TerrainChunk[] chunks = terrain?.AllocatedChunks;
            bool coveredKnown = chunks != null && CaptureUpdateLocations();
            if (chunks != null)
            {
                for (int i = 0; i < chunks.Length; i++)
                {
                    TerrainChunk chunk = chunks[i];
                    if (chunk == null) continue;
                    var key = new ChunkKey(chunk.Coords.X, chunk.Coords.Y);
                    ChunkContentStage stage = ChunkContentStages.OfEngineState((int)chunk.State);
                    // coveredByUpdateLocation:
                    //   true  = 有更新位置覆盖它（引擎会推进它，停滞只是排队）；
                    //   false = 没有任何更新位置覆盖它 ⇒ 引擎**永远**不会再推进（真正的"无法定稿"）；
                    //   null  = 读不到更新位置（已单独告警；不拿"猜"当理由）。
                    bool? covered = !coveredKnown ? (bool?)null : CoversChunk(chunk.Center);
                    m_residency.Observe(key, stage, covered);
                }
            }
            m_residency.EndFrame();      // 本帧没看到的块 = 引擎已释放 / 世界已卸载
            DispatchResidencyChanges();
            WarnAboutStalledChunks(nowMs);
        }

        /// <summary>把本帧的状态变化发成事件（顺序：进入内存 → 定稿 → 掉回未定稿 → 上报集合增减 → 释放）。</summary>
        private void DispatchResidencyChanges()
        {
            IReadOnlyList<ChunkResidencyChange> changes = m_residency.Changes;
            for (int i = 0; i < changes.Count; i++)
            {
                ChunkResidencyChange change = changes[i];
                switch (change.Event)
                {
                    case ChunkResidencyEvent.Allocated:
                        ChunkAllocated?.Invoke(change.Key);
                        break;
                    case ChunkResidencyEvent.BecameResident:
                        ChunkLoaded?.Invoke(change.Key);
                        break;
                    case ChunkResidencyEvent.BecameNotResident:
                        ChunkUnloaded?.Invoke(change.Key);
                        break;
                    case ChunkResidencyEvent.WireExcluded:
                        ChunkWireExcluded?.Invoke(change.Key, DescribeStall(change));
                        break;
                    case ChunkResidencyEvent.WireRestored:
                        ChunkWireIncluded?.Invoke(change.Key);
                        break;
                    case ChunkResidencyEvent.Freed:
                        ChunkFreed?.Invoke(change.Key);
                        break;
                }
            }
        }

        private static string DescribeStall(in ChunkResidencyChange change)
        {
            return "已在内存 " + (change.AgeMs / 1000.0).ToString("0.0") + "s 仍未定稿（State=" +
                ChunkContentStages.DescribeEngineState((int)change.Stage) + "，原因=" +
                ChunkStallReasonNames.Of(change.Reason) + "）";
        }

        /// <summary>
        /// 超过宽限仍未定稿的块：每块一行（首次）+ 全局限流，说明**为什么不报、是谁的问题**。
        /// 这是"实时地图上为什么没有这块的框 / 裁决"的唯一证据行，绝不静默。
        /// </summary>
        private void WarnAboutStalledChunks(long nowMs)
        {
            // 先看限流窗口：窗口内的停滞记录**留在跟踪器里**（不取走、不丢），下一条日志一起点名。
            if (m_lastStallWarningMs != 0 && nowMs - m_lastStallWarningMs < StallWarningIntervalMs) return;
            m_stallRecords.Clear();
            int count = m_residency.TakeNewlyStalled(m_stallRecords);
            if (count == 0) return;
            m_lastStallWarningMs = nowMs;
            var builder = new StringBuilder(256);
            builder.Append("[SuMP] memoryChunkNotResident: ").Append(count)
                .Append(" 块在内存里超过 ").Append(m_residency.GraceMs / 1000)
                .Append("s 仍未定稿 → 不再作为\"内存区块\"上报（实时地图不再挂无法裁决的灰框）；累计 ")
                .Append(m_residency.WireExcludedTotal).Append(" · 当前 memoryNotResident ")
                .Append(m_residency.MemoryNotResident).Append('（').Append(m_residency.Describe())
                .Append("）· 明细：");
            int listed = Math.Min(count, StallRecordsPerWarning);
            for (int i = 0; i < listed; i++)
            {
                ChunkStallRecord record = m_stallRecords[i];
                if (i > 0) builder.Append("；");
                builder.Append(record.Key).Append(' ')
                    .Append(ChunkStallReasonNames.Of(record.Reason))
                    .Append(" state=")
                    .Append(ChunkContentStages.DescribeEngineState((int)record.Stage))
                    .Append(" age=")
                    .Append((record.AgeMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture))
                    .Append('s');
            }
            if (count > listed) builder.Append("；还有 ").Append(count - listed).Append(" 块（同原因限流）");
            Log.Warning(builder.ToString());
        }

        /// <summary>
        /// 读 <c>TerrainUpdater</c> 当前生效的更新位置（可见范围 / 内容距离）—— 用来判断
        /// "引擎还会不会推进这块"。只在主线程调用（那里同一份字典也只由主线程写）。
        /// 读不到就返回 false 并**只告警一次**（原因写清楚，不拿"猜"当"没有"）。
        ///
        /// 为什么用反射：TerrainUpdater 没有公开"当前更新位置"的入口（只有 SetUpdateLocation /
        /// RemoveUpdateLocation / GetUpdateProgress(按 index)）。这里读的是
        /// <c>m_updateParameters.Locations</c>（主线程那份，和 AllocateAndFreeChunks 用的是同一个）。
        /// </summary>
        private bool CaptureUpdateLocations()
        {
            m_locationCenters.Clear();
            m_locationDistances.Clear();
            SubsystemTerrain subsystem = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false)
                ?? m_subsystem;
            TerrainUpdater updater = subsystem?.TerrainUpdater;
            if (updater == null) return false;
            try
            {
                if (m_updateParametersField == null)
                {
                    // 实测（P:\tmp\reflcheck，直接对真实 Survivalcraft.dll 反射）：
                    //   m_updateParameters → Game.TerrainUpdater+UpdateParameters（私有嵌套结构）
                    //   它的字段 Locations 是 **public** ⇒ 查找必须同时带 Public 标志，否则永远 NULL。
                    m_updateParametersField = typeof(TerrainUpdater).GetField("m_updateParameters",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                object boxed = m_updateParametersField?.GetValue(updater);
                if (boxed == null) return CoverageUnavailable("TerrainUpdater.m_updateParameters 读不到");
                Type parametersType = boxed.GetType();
                if (m_updateLocationsField == null)
                {
                    m_updateLocationsField = parametersType.GetField("Locations",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (m_updateLocationsField?.GetValue(boxed) is not IDictionary table)
                    return CoverageUnavailable("TerrainUpdater.m_updateParameters.Locations 不是字典");
                if (m_locationCenterField == null || m_locationContentDistanceField == null)
                {
                    // 不能靠 table.Values 去"顺手发现"字段类型：世界刚加载、还没挂任何更新位置时
                    // 字典是**空的**（实机 18:33:07 / 18:41:12 两次告警就是这么来的），
                    // 空表会让这里永远报"UpdateLocation.Center / ContentDistance 字段找不到"。
                    // 直接按类型名取嵌套结构（Game.TerrainUpdater+UpdateLocation），与字典有没有内容无关。
                    Type locationType = typeof(TerrainUpdater).GetNestedType("UpdateLocation",
                        BindingFlags.Public | BindingFlags.NonPublic);
                    if (locationType == null)
                        return CoverageUnavailable("TerrainUpdater+UpdateLocation 嵌套类型找不到");
                    // 这两个字段在嵌套结构里是 public；查找仍带 NonPublic，避免引擎改可见性后再坏一次。
                    m_locationCenterField = locationType.GetField("Center",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    m_locationContentDistanceField = locationType.GetField("ContentDistance",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (m_locationCenterField == null || m_locationContentDistanceField == null)
                    return CoverageUnavailable("UpdateLocation.Center / ContentDistance 字段找不到");
                foreach (object value in table.Values)
                {
                    if (value == null) continue;
                    m_locationCenters.Add((Vector2)m_locationCenterField.GetValue(value));
                    m_locationDistances.Add((float)m_locationContentDistanceField.GetValue(value));
                }
                return true;
            }
            catch (Exception e)
            {
                return CoverageUnavailable(e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>读不到更新位置：**只告警一次**，并让停滞原因如实记 locations-unknown（不猜"没有"）。</summary>
        private bool CoverageUnavailable(string reason)
        {
            if (m_coverageUnavailableLogged) return false;
            m_coverageUnavailableLogged = true;
            Log.Warning("[SuMP] 读不到 TerrainUpdater 的地形更新位置（" + reason + "）→ 未定稿块的停滞原因" +
                "只能记 locations-unknown（无法判定\"引擎还会不会推进它\"；不拿猜测当原因）");
            return false;
        }

        /// <summary>这块的中心是否落在任一更新位置的内容距离内（与引擎 AllocateAndFreeChunks 同一判据）。</summary>
        private bool CoversChunk(Vector2 chunkCenter)
        {
            for (int i = 0; i < m_locationCenters.Count; i++)
            {
                float distance = m_locationDistances[i];
                if (Vector2.DistanceSquared(chunkCenter, m_locationCenters[i]) <= distance * distance)
                    return true;
            }
            return false;
        }

        /// <summary>主机侧：把捕获到的改动交给模块下发（每格一次事件，模块自己批量）。</summary>
        public void PublishHostCells(ICollection<Point3> cells)
        {
            if (cells == null || cells.Count == 0) return;
            Action<ChunkKey, CellEdit> handler = HostCellChanged;
            if (handler == null)
            {
                // 没有订阅者 = 抓到也发不出去（装配未就绪 / 模块下线）：如实计数，不静默。
                NoteHostCaptureMiss(cells.Count, "没有主机广播订阅者（地形模块未装配 / 正在下线）");
                return;
            }
            Game.Terrain terrain = CurrentTerrain;
            if (terrain == null)
            {
                // 世界正好被卸载：这些格子**没有**下发 → 如实计数（不发一个猜出来的 0=空气）。
                NoteHostCaptureMiss(cells.Count, "当前没有地形（世界卸载中）");
                return;
            }
            foreach (Point3 cell in cells)
            {
                if (cell.Y < 0 || cell.Y > 255) continue;
                // 区块不在内存 / 内容未定稿：GetCellValueFast 会返回 0（= 空气）。绝不把"读不到"
                // 当成"改成了空气"下发 —— 明确跳过并计数，上层加载后的摘要核对会收敛。
                // 判据与 HasContents 同一句（**不看 IsLoaded**：生成出来、还没写盘的块内容同样有效）。
                TerrainChunk chunk = terrain.GetChunkAtCell(cell.X, cell.Z);
                if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
                {
                    NoteHostCaptureMiss(1, "区块内容未定稿 (" + (chunk?.Coords.X ?? (cell.X >> 4)) + "," +
                        (chunk?.Coords.Y ?? (cell.Z >> 4)) + ") state=" +
                        ChunkContentStages.DescribeEngineState(chunk == null ? 0 : (int)chunk.State));
                    continue;
                }
                int value = terrain.GetCellValueFast(cell.X, cell.Y, cell.Z);
                var key = new ChunkKey(cell.X >> 4, cell.Z >> 4);
                HostCellsCaptured++;
                handler(key, new CellEdit(cell.X & 15, cell.Y, cell.Z & 15,
                    Game.Terrain.ExtractContents(value), Game.Terrain.ExtractData(value)));
            }
        }

        /// <summary>主机：本次合并去重掉的格数（同一格既在引擎表、又被行为登记 / 接住）。</summary>
        public void NoteHostCaptureDuplicates(int duplicates)
        {
            if (duplicates > 0) HostCellDuplicates += duplicates;
        }

        /// <summary>
        /// 主机：捕获到的格子**没能**进入广播（区块不在内存 / 世界卸载）。限流写一行明确原因，
        /// 绝不静默：这些格只能靠客户端加载后的摘要核对 + 整块回传收敛。
        /// </summary>
        private void NoteHostCaptureMiss(int count, string reason)
        {
            HostCellMisses += count;
            long now = Environment.TickCount64;
            if (m_lastHostMissLogMs != 0 && now - m_lastHostMissLogMs < HostMissLogIntervalMs) return;
            m_lastHostMissLogMs = now;
            Log.Warning("[SuMP] hostCellMissed: 主机有 " + count + " 格改动没能捕获（" + reason +
                "）→ 跳过（不猜值）；累计 " + HostCellMisses + " 格（同原因限流 " +
                (HostMissLogIntervalMs / 1000) + "s；加载后由摘要核对 / 整块回传收敛）");
        }

        private static Game.Terrain CurrentTerrain => GameManager.Project?.FindSubsystem<SubsystemTerrain>(false)?.Terrain;

        private void EnsureMainThread(string what)
        {
            if (m_reportedWrongThread || Thread.CurrentThread.ManagedThreadId == m_mainThreadId) return;
            m_reportedWrongThread = true;
            Log.Warning("[SuMP] TerrainBridge." + what + " 从非主线程调用（应为引擎线程）");
        }
    }
}
