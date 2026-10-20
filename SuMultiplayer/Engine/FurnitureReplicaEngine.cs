using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Furniture;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using TemplatesDatabase;
using EngineRandom = global::Engine.Random;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 家具同步的**引擎适配层**（新文件）：
    ///
    /// 客户端：
    ///   · 接管世界里已加载的设计为副本（只进副本表；Save 由子系统外壳写空）；
    ///   · 按主机索引把记录装进引擎设计表槽位（引擎 GetDesign 不是虚方法，渲染必须走槽位），
    ///     但**不调用** TryAddDesign / TryAddDesignChain，也不写任何存档；
    ///   · 锤子创建 / 编辑：复刻 ScanDesign 的扫描 + BuildFurnitureDialog，点 Build 只发请求；
    ///   · 颜料桶：家具目标改成主机请求（客户端不本地 Paint / 不新增设计）；
    ///   · 每秒副本完整性 + leak sweep：主机没下发的本地设计明确移除并计数；
    ///   · 内容表应用后刷新本机 ComponentFurnitureInventory。
    ///
    /// 主机：
    ///   · 抓权威设计表指纹 / 完整记录（模块只在指纹变化时调用完整抓取）；
    ///   · 校验并落地客户端的设计 / 上色请求：距离、手持物、源格、设计值来源、
    ///     设计数量上限；安装设计、销毁源方块、生成家具物品、改格上色。
    ///
    /// 无静态状态；全部计数器与缓存都在实例上。所有方法只在主线程调用。
    /// </summary>
    public sealed class FurnitureReplicaEngine : IFurniturePort, IFurnitureEngineEvents,
        IClientFurnitureDesignGuard
    {
        private const int MaxDesigns = FurnitureDesignStore.MaxDesigns;
        private const int ProblemLogMs = 10000;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly InteractionReachGate m_reachGate;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<int, int> m_replicaLinks =
            new Dictionary<int, int>();
        private readonly EngineRandom m_random = new EngineRandom();

        private Project m_project;
        private SubsystemTerrain m_subsystemTerrain;
        private SuSubsystemTerrain m_terrainShell;
        private SubsystemFurnitureBlockBehavior m_behavior;
        private SuSubsystemFurnitureBlockBehavior m_subsystem;
        private FurnitureDesign[] m_designs;
        private SuSubsystemHammerBlockBehavior m_hammerShell;
        private SuSubsystemBucketBlockBehavior m_bucketShell;
        private SuFurnitureBlock m_furnitureBlock;
        private IFurnitureDesignRequestSink m_sink;
        private bool m_clientReplica;
        private bool m_adopted;
        private bool m_shellMissingLogged;
        private bool m_designArrayMissingLogged;
        private bool m_hammerShellMissingLogged;
        private bool m_bucketShellMissingLogged;
        private bool m_blockShellMissingLogged;
        private int m_sessionSerial;
        private int m_blockCount = -1;

        // ---------------------------------------------------------------- 计数
        private long m_replicaInstalled;
        private long m_replicaRemoved;
        private long m_replicaReinstalled;
        private long m_clientLibraryRejected;
        private long m_clientLibraryLeaks;
        private long m_clientSaveSuppressed;
        private long m_clientMissingDesignCells;
        private long m_clientPaintBlocked;
        private long m_clientCraftBlocked;
        private long m_inventoryRefreshed;
        private long m_inventoryRefreshFailed;
        private long m_clientSetsCleared;
        private long m_hostInstalled;
        private long m_hostPaintApplied;
        private long m_hostRenamed;
        private long m_hostRejected;
        private long m_hostSpawnFailures;

        public FurnitureReplicaEngine(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModuleDiagnostics diagnostics, string ownerId, InteractionReachGate reachGate)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_reachGate = reachGate ?? throw new ArgumentNullException(nameof(reachGate));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "furniture-sync" : ownerId;
        }

        // ================================================================= IFurniturePort

        public void SetRequestSink(IFurnitureDesignRequestSink sink) => m_sink = sink;

        public int BlockCount => m_blockCount;

        public bool IsClientReplica => m_clientReplica;

        public bool IsWorldReady =>
            m_clientReplica && m_project != null && m_behavior != null && m_subsystem != null &&
            m_designs != null && m_subsystemTerrain?.Terrain != null;

        public int SessionSerial => m_sessionSerial;

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                DetachShells();
                m_project = project;
                m_behavior = null;
                m_subsystem = null;
                m_designs = null;
                m_subsystemTerrain = null;
                m_terrainShell = null;
                m_hammerShell = null;
                m_bucketShell = null;
                m_furnitureBlock = null;
                m_adopted = false;
                m_blockCount = -1;
                m_sessionSerial++;
                m_shellMissingLogged = false;
                m_designArrayMissingLogged = false;
                m_hammerShellMissingLogged = false;
                m_bucketShellMissingLogged = false;
                m_blockShellMissingLogged = false;
                m_replicaLinks.Clear();
            }
            if (m_project == null)
            {
                DetachShells();
                return;
            }

            if (m_subsystemTerrain == null)
                m_subsystemTerrain = m_project.FindSubsystem<SubsystemTerrain>(false);
            if (m_terrainShell == null)
                m_terrainShell = m_subsystemTerrain as SuSubsystemTerrain;
            if (m_behavior == null)
            {
                m_behavior = m_project.FindSubsystem<SubsystemFurnitureBlockBehavior>(false);
                if (m_behavior != null)
                {
                    m_subsystem = m_behavior as SuSubsystemFurnitureBlockBehavior;
                    try
                    {
                        m_designs = m_parentField.GetParentField<FurnitureDesign[]>(m_behavior,
                            "m_furnitureDesigns", typeof(SubsystemFurnitureBlockBehavior));
                    }
                    catch (Exception error)
                    {
                        m_designs = null;
                        if (!m_designArrayMissingLogged)
                        {
                            m_designArrayMissingLogged = true;
                            LogProblem("design-array", "读不到家具设计表 m_furnitureDesigns（" +
                                error.GetType().Name + " " + error.Message +
                                "）→ 家具同步不启用（明确失败）");
                        }
                    }
                    if (m_subsystem == null && !m_shellMissingLogged)
                    {
                        m_shellMissingLogged = true;
                        LogProblem("shell-missing", "SubsystemFurnitureBlockBehavior 注入未生效（实际类型 " +
                            (m_behavior == null ? "<null>" : m_behavior.GetType().Name) +
                            "）→ 客户端 Save 无法保证不写设计表，家具同步不启用（明确失败）");
                    }
                }
            }
            if (m_subsystem != null) m_subsystem.Runtime = this;

            if (m_hammerShell == null)
                m_hammerShell = m_project.FindSubsystem<SubsystemHammerBlockBehavior>(false)
                    as SuSubsystemHammerBlockBehavior;
            if (m_hammerShell != null) m_hammerShell.Runtime = this;

            if (m_bucketShell == null)
                m_bucketShell = m_project.FindSubsystem<SubsystemBucketBlockBehavior>(false)
                    as SuSubsystemBucketBlockBehavior;
            if (m_bucketShell != null) m_bucketShell.Runtime = this;

            if (m_furnitureBlock == null && BlocksManager.Blocks != null &&
                FurnitureBlock.Index < BlocksManager.Blocks.Length)
            {
                m_furnitureBlock = BlocksManager.Blocks[FurnitureBlock.Index] as SuFurnitureBlock;
            }
            if (m_furnitureBlock != null) m_furnitureBlock.Runtime = this;

            if (m_terrainShell != null) m_terrainShell.ClientFurnitureGuard = this;

            bool client = m_terrainShell != null && m_terrainShell.ClientReplica;
            if (client != m_clientReplica)
            {
                m_clientReplica = client;
                if (!client) m_adopted = false;
                Log(client
                    ? "家具同步角色 = 客户端副本（设计只进会话副本表，Save 写空）"
                    : "家具同步角色 = 权威（主机 / 单机：世界设计表原样保存）");
            }
            if (BlocksManager.Blocks != null && BlocksManager.Blocks.Length > 0)
                m_blockCount = BlocksManager.Blocks.Length;
            // 在角色计算之后写入：客户端存档防护必须在首帧就是正确值，
            // 否则首帧 Save（理论上）会走原生保存。
            if (m_subsystem != null)
                m_subsystem.ClientReplicaSaveGuard = m_clientReplica;

            if (m_clientReplica && m_subsystem == null)
            {
                // 明确失败：没有外壳就无法保证 Save 不污染客户端家具库。
                LogProblem("client-shell-missing", "客户端没有家具子系统外壳 → 不接管 / 不安装设计" +
                    "（否则无法保证不写客户端存档）");
            }
            if (m_clientReplica && m_hammerShell == null && !m_hammerShellMissingLogged)
            {
                m_hammerShellMissingLogged = true;
                LogProblem("hammer-shell-missing", "客户端没有锤子子系统外壳 → 原生 ScanDesign 会在本地" +
                    " TryAddDesign（家具库污染）；leak sweep 会移除并计数，但请先检查数据库注入");
            }
            if (m_clientReplica && m_bucketShell == null && !m_bucketShellMissingLogged)
            {
                m_bucketShellMissingLogged = true;
                LogProblem("bucket-shell-missing", "客户端没有颜料桶子系统外壳 → 家具上色会在本地" +
                    " Paint（TryAddDesignChain 污染）；leak sweep 会移除并计数");
            }
            if (m_clientReplica && m_furnitureBlock == null && !m_blockShellMissingLogged)
            {
                m_blockShellMissingLogged = true;
                LogProblem("block-shell-missing", "客户端没有家具方块外壳（index=" +
                    FurnitureBlock.Index + "）→ 其它调用 Paint / GetAdHocCraftingRecipe 的路径" +
                    " 可能在本地新增设计；leak sweep 会明确移除并计数");
            }
        }

        public void Shutdown()
        {
            if (m_subsystem != null) m_subsystem.Runtime = null;
            if (m_hammerShell != null) m_hammerShell.Runtime = null;
            if (m_bucketShell != null) m_bucketShell.Runtime = null;
            if (m_furnitureBlock != null) m_furnitureBlock.Runtime = null;
            if (m_terrainShell != null) m_terrainShell.ClientFurnitureGuard = null;
            m_sink = null;
            m_project = null;
            m_behavior = null;
            m_subsystem = null;
            m_designs = null;
            m_subsystemTerrain = null;
            m_terrainShell = null;
            m_hammerShell = null;
            m_bucketShell = null;
            m_furnitureBlock = null;
            m_adopted = false;
            m_replicaLinks.Clear();
        }

        private void DetachShells()
        {
            if (m_subsystem != null) m_subsystem.Runtime = null;
            if (m_hammerShell != null) m_hammerShell.Runtime = null;
            if (m_bucketShell != null) m_bucketShell.Runtime = null;
            if (m_furnitureBlock != null) m_furnitureBlock.Runtime = null;
            if (m_terrainShell != null) m_terrainShell.ClientFurnitureGuard = null;
            m_terrainShell = null;
            m_hammerShell = null;
            m_bucketShell = null;
            m_furnitureBlock = null;
        }

        // ================================================================= 主机：抓取

        public bool TryCaptureHostFingerprint(out long fingerprint, out int count,
            out string reason)
        {
            fingerprint = 0L;
            count = 0;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_behavior == null || m_designs == null)
            {
                reason = "furniture-table-unavailable";
                return false;
            }
            if (!TryGetBlockCount(out reason)) return false;
            unchecked
            {
                long hash = 1469598103934665603L;
                for (int i = 0; i < m_designs.Length; i++)
                {
                    FurnitureDesign design = m_designs[i];
                    if (design == null) continue;
                    count++;
                    hash = FurnitureDesignHash.Mix(hash, i);
                    hash = FurnitureDesignHash.Mix(hash, design.Hash);
                    hash = FurnitureDesignHash.MixString(hash, design.Name);
                    hash = FurnitureDesignHash.Mix(hash, (int)design.InteractionMode);
                    hash = FurnitureDesignHash.Mix(hash,
                        design.LinkedDesign == null ? -1 : design.LinkedDesign.Index);
                    hash = FurnitureDesignHash.MixString(hash,
                        design.FurnitureSet == null ? string.Empty : design.FurnitureSet.Name);
                }
                fingerprint = hash == 0L ? 1L : hash;
            }
            return true;
        }

        public int CaptureHostDesigns(List<FurnitureDesignRecord> target, int max,
            out long fingerprint, out string reason)
        {
            fingerprint = 0L;
            reason = null;
            if (target == null)
            {
                reason = "target-null";
                return -1;
            }
            target.Clear();
            if (m_clientReplica)
            {
                reason = "not-host";
                return -1;
            }
            if (m_behavior == null || m_designs == null)
            {
                reason = "furniture-table-unavailable";
                return -1;
            }
            unchecked
            {
                long hash = 1469598103934665603L;
                for (int i = 0; i < m_designs.Length; i++)
                {
                    FurnitureDesign design = m_designs[i];
                    if (design == null) continue;
                    if (target.Count >= max)
                    {
                        reason = "too-many-designs:" + target.Count.ToString(CultureInfo.InvariantCulture) +
                            ">=" + max.ToString(CultureInfo.InvariantCulture);
                        target.Clear();
                        return -1;
                    }
                    var record = ToRecord(design, i);
                    target.Add(record);
                    hash = FurnitureDesignHash.Mix(hash, i);
                    hash = FurnitureDesignHash.Mix(hash, design.Hash);
                    hash = FurnitureDesignHash.MixString(hash, design.Name);
                    hash = FurnitureDesignHash.Mix(hash, (int)design.InteractionMode);
                    hash = FurnitureDesignHash.Mix(hash,
                        design.LinkedDesign == null ? -1 : design.LinkedDesign.Index);
                    hash = FurnitureDesignHash.MixString(hash,
                        design.FurnitureSet == null ? string.Empty : design.FurnitureSet.Name);
                }
                fingerprint = hash == 0L ? 1L : hash;
            }
            return target.Count;
        }

        private FurnitureDesignRecord ToRecord(FurnitureDesign design, int slot)
        {
            int index = design.Index;
            if (!FurnitureDesignStore.IsValidIndex(index)) index = slot;
            int resolution = design.Resolution;
            int count = resolution * resolution * resolution;
            var values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = FurnitureDesignHash.StripLight(design.GetValue(i));
            return new FurnitureDesignRecord(index, resolution, design.Name,
                (int)design.InteractionMode,
                design.LinkedDesign == null ? -1 : design.LinkedDesign.Index,
                design.FurnitureSet == null ? string.Empty : design.FurnitureSet.Name, values);
        }

        // ================================================================= 主机：落地请求

        public bool TryApplyHostDesignRequest(int clientId, in FurnitureDesignRequestData request,
            out FurnitureDesignResultData result, out FurnitureDesignRecord installed,
            out string reason)
        {
            result = default;
            installed = null;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null || m_behavior == null || m_subsystemTerrain?.Terrain == null)
            {
                reason = "world-unavailable";
                return false;
            }
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer?.ComponentBody == null)
            {
                reason = "client-" + clientId.ToString(CultureInfo.InvariantCulture) +
                    "-has-no-avatar";
                m_hostRejected++;
                return false;
            }
            if (request.Op == FurnitureDesignOps.Paint)
                return TryApplyHostPaint(clientId, playerData, in request, out result,
                    out installed, out reason);
            if (request.Op == FurnitureDesignOps.Rename)
                return TryApplyHostRename(clientId, playerData, in request, out result,
                    out installed, out reason);
            return TryApplyHostCreate(clientId, playerData, in request, out result,
                out installed, out reason);
        }

        private bool TryApplyHostCreate(int clientId, PlayerData playerData,
            in FurnitureDesignRequestData request, out FurnitureDesignResultData result,
            out FurnitureDesignRecord installed, out string reason)
        {
            result = default;
            installed = null;
            reason = null;
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentMiner miner = player?.ComponentMiner;
            SubsystemTerrain terrain = m_subsystemTerrain;
            SubsystemFurnitureBlockBehavior behavior = m_behavior;
            if (miner == null || miner.Inventory == null || terrain?.Terrain == null)
            {
                reason = "requester-miner-unavailable";
                return Reject(in request, reason, out result, out reason);
            }
            if (request.InteractionMode != 0)
            {
                reason = "unsupported-interaction-mode:" +
                    request.InteractionMode.ToString(CultureInfo.InvariantCulture);
                return Reject(in request, reason, out result, out reason);
            }
            var startCenter = new Vector3(request.StartX + 0.5f, request.StartY + 0.5f,
                request.StartZ + 0.5f);
            float measured = MathUtils.Sqrt(Vector3.DistanceSquared(
                player.ComponentBody.Position, startCenter));
            if (!m_reachGate.TryCheckDistance(request.HasReach, request.Reach,
                    EngineReach.IsCreative(m_project), measured, out string reachReason))
            {
                reason = reachReason;
                return Reject(in request, reason, out result, out reason);
            }
            int activeSlot = miner.Inventory.ActiveSlotIndex;
            if (activeSlot < 0 || activeSlot >= miner.Inventory.SlotsCount)
            {
                reason = "no-active-slot";
                return Reject(in request, reason, out result, out reason);
            }
            int heldValue = miner.Inventory.GetSlotValue(activeSlot);
            int heldContents = Game.Terrain.ExtractContents(heldValue);
            if (heldContents >= BlocksManager.Blocks.Length ||
                !(BlocksManager.Blocks[heldContents] is HammerBlock))
            {
                reason = "not-holding-hammer:" + heldContents.ToString(CultureInfo.InvariantCulture);
                return Reject(in request, reason, out result, out reason);
            }

            int baseIndex = request.BaseDesignIndex;
            FurnitureDesign baseDesign = null;
            if (baseIndex >= 0)
            {
                baseDesign = behavior.GetDesign(baseIndex);
                if (baseDesign == null)
                {
                    reason = "base-design-missing:" + baseIndex.ToString(CultureInfo.InvariantCulture);
                    return Reject(in request, reason, out result, out reason);
                }
            }

            if (!TryValidateSourceCells(in request, terrain, out reason))
                return Reject(in request, reason, out result, out reason);
            if (!TryValidateDesignProvenance(in request, baseDesign, out reason))
                return Reject(in request, reason, out result, out reason);

            FurnitureDesign design;
            try
            {
                design = new FurnitureDesign(terrain);
                design.Name = request.Name;
                design.InteractionMode = (FurnitureInteractionMode)request.InteractionMode;
                int[] values = new int[request.Values.Length];
                for (int i = 0; i < values.Length; i++)
                    values[i] = FurnitureDesignHash.StripLight(request.Values[i]);
                design.SetValues(request.Resolution, values);
            }
            catch (Exception error)
            {
                reason = "invalid-design:" + error.GetType().Name + ":" + error.Message;
                return Reject(in request, reason, out result, out reason);
            }

            int startValue;
            try
            {
                startValue = terrain.Terrain.GetCellValue(request.StartX, request.StartY,
                    request.StartZ);
            }
            catch (Exception)
            {
                startValue = 0;
            }

            SubsystemGameInfo gameInfo = m_project.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo == null)
            {
                reason = "game-info-missing";
                return Reject(in request, reason, out result, out reason);
            }

            FurnitureDesign added = behavior.TryAddDesign(design);
            if (added == null)
            {
                reason = "design-limit";
                return Reject(in request, reason, out result, out reason);
            }
            bool duplicate = !ReferenceEquals(added, design);
            m_hostInstalled++;

            // Source: ScanDesign —— 非创造模式销毁源方块；创造模式源方块保留。
            if (FurnitureBuildPolicy.DestroysSourceCells(
                    gameInfo.WorldSettings.GameMode == GameMode.Creative))
            {
                for (int i = 0; i < request.SourceCount; i++)
                {
                    try
                    {
                        terrain.DestroyCell(0, request.SourceX[i], request.SourceY[i],
                            request.SourceZ[i], 0, noDrop: true, noParticleSystem: true);
                    }
                    catch (Exception error)
                    {
                        LogProblem("source-destroy:" + error.GetType().Name,
                            "主机销毁设计源方块失败 (" + request.SourceX[i] + "," +
                            request.SourceY[i] + "," + request.SourceZ[i] + ")：" +
                            error.GetType().Name + " " + error.Message);
                    }
                }
            }

            bool pickableSpawned = TrySpawnFurnitureItem(player, miner, added, out string spawnReason);
            if (!pickableSpawned)
            {
                m_hostSpawnFailures++;
                LogProblem("spawn:" + ReasonCode(spawnReason),
                    "主机已保存家具设计 index=" + added.Index + "，但生成家具物品失败（" +
                    spawnReason + "）→ 结果里如实标记 pickable_spawned=false");
            }
            else
            {
                int soundX = request.StartX, soundY = request.StartY, soundZ = request.StartZ;
                for (int i = 0; i < 3; i++)
                {
                    int captured = i;
                    Time.QueueTimeDelayedExecution(Time.FrameStartTime + captured * 0.25,
                        delegate
                        {
                            try
                            {
                                SubsystemSoundMaterials sounds =
                                    m_project?.FindSubsystem<SubsystemSoundMaterials>(false);
                                sounds?.PlayImpactSound(startValue,
                                    new Vector3(soundX, soundY, soundZ), 1f);
                            }
                            catch (Exception)
                            {
                            }
                        });
                }
                if (miner.ComponentCreature?.PlayerStats != null)
                    miner.ComponentCreature.PlayerStats.FurnitureItemsMade +=
                        MathUtils.Clamp(added.Resolution, 4, 8);
            }

            installed = ToRecord(added, added.Index);
            result = new FurnitureDesignResultData(request.Sequence, true, string.Empty,
                added.Index, installed.ContentHash, duplicate, 0, pickableSpawned);
            if (duplicate)
            {
                Log("主机复用了已存在的家具设计 index=" + added.Index + "（内容相同，未重复创建）");
            }
            return true;
        }

        private bool TryApplyHostRename(int clientId, PlayerData playerData,
            in FurnitureDesignRequestData request, out FurnitureDesignResultData result,
            out FurnitureDesignRecord installed, out string reason)
        {
            result = default;
            installed = null;
            reason = null;
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentMiner miner = player?.ComponentMiner;
            SubsystemTerrain terrain = m_subsystemTerrain;
            SubsystemFurnitureBlockBehavior behavior = m_behavior;
            if (miner == null || miner.Inventory == null || terrain?.Terrain == null)
            {
                reason = "requester-miner-unavailable";
                return Reject(in request, reason, out result, out reason);
            }
            int activeSlot = miner.Inventory.ActiveSlotIndex;
            if (activeSlot < 0 || activeSlot >= miner.Inventory.SlotsCount)
            {
                reason = "no-active-slot";
                return Reject(in request, reason, out result, out reason);
            }
            int heldContents = Game.Terrain.ExtractContents(
                miner.Inventory.GetSlotValue(activeSlot));
            if (heldContents >= BlocksManager.Blocks.Length ||
                !(BlocksManager.Blocks[heldContents] is HammerBlock))
            {
                reason = "not-holding-hammer:" + heldContents.ToString(CultureInfo.InvariantCulture);
                return Reject(in request, reason, out result, out reason);
            }
            Vector3 center = new Vector3(request.StartX + 0.5f, request.StartY + 0.5f,
                request.StartZ + 0.5f);
            float measured = MathUtils.Sqrt(Vector3.DistanceSquared(
                player.ComponentBody.Position, center));
            if (!m_reachGate.TryCheckDistance(request.HasReach, request.Reach,
                    EngineReach.IsCreative(m_project), measured, out string reachReason))
            {
                reason = reachReason;
                return Reject(in request, reason, out result, out reason);
            }
            FurnitureDesign design = behavior.GetDesign(request.BaseDesignIndex);
            if (design == null)
            {
                reason = "base-design-missing:" + request.BaseDesignIndex.ToString(
                    CultureInfo.InvariantCulture);
                return Reject(in request, reason, out result, out reason);
            }
            try
            {
                design.Name = request.Name;
            }
            catch (Exception error)
            {
                reason = "invalid-name:" + error.GetType().Name + ":" + error.Message;
                return Reject(in request, reason, out result, out reason);
            }
            m_hostRenamed++;
            installed = ToRecord(design, design.Index);
            result = new FurnitureDesignResultData(request.Sequence, true, string.Empty,
                design.Index, installed.ContentHash, false, 0, false);
            return true;
        }

        private bool TryApplyHostPaint(int clientId, PlayerData playerData,
            in FurnitureDesignRequestData request, out FurnitureDesignResultData result,
            out FurnitureDesignRecord installed, out string reason)
        {
            result = default;
            installed = null;
            reason = null;
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentMiner miner = player?.ComponentMiner;
            SubsystemTerrain terrain = m_subsystemTerrain;
            SubsystemFurnitureBlockBehavior behavior = m_behavior;
            if (miner == null || miner.Inventory == null || terrain?.Terrain == null)
            {
                reason = "requester-miner-unavailable";
                return Reject(in request, reason, out result, out reason);
            }
            int x = request.StartX, y = request.StartY, z = request.StartZ;
            if (!terrain.Terrain.IsCellValid(x, y, z) ||
                terrain.Terrain.GetChunkAtCell(x, z) == null)
            {
                reason = "cell-not-loaded";
                return Reject(in request, reason, out result, out reason);
            }
            float measured = MathUtils.Sqrt(Vector3.DistanceSquared(
                player.ComponentBody.Position, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)));
            if (!m_reachGate.TryCheckDistance(request.HasReach, request.Reach,
                    EngineReach.IsCreative(m_project), measured, out string reachReason))
            {
                reason = reachReason;
                return Reject(in request, reason, out result, out reason);
            }
            int cellValue = terrain.Terrain.GetCellValue(x, y, z);
            if (Game.Terrain.ExtractContents(cellValue) != FurnitureBlock.Index)
            {
                reason = "target-not-furniture";
                return Reject(in request, reason, out result, out reason);
            }
            int data = Game.Terrain.ExtractData(cellValue);
            int designIndex = FurnitureBlock.GetDesignIndex(data);
            if (designIndex != request.BaseDesignIndex)
            {
                reason = "target-design-changed:" + designIndex.ToString(CultureInfo.InvariantCulture) +
                    "!=" + request.BaseDesignIndex.ToString(CultureInfo.InvariantCulture);
                return Reject(in request, reason, out result, out reason);
            }
            FurnitureDesign baseDesign = behavior.GetDesign(designIndex);
            if (baseDesign == null)
            {
                reason = "base-design-missing:" + designIndex.ToString(CultureInfo.InvariantCulture);
                return Reject(in request, reason, out result, out reason);
            }
            int activeSlot = miner.Inventory.ActiveSlotIndex;
            if (activeSlot < 0 || activeSlot >= miner.Inventory.SlotsCount)
            {
                reason = "no-active-slot";
                return Reject(in request, reason, out result, out reason);
            }
            int heldValue = miner.Inventory.GetSlotValue(activeSlot);
            int heldContents = Game.Terrain.ExtractContents(heldValue);
            Block heldBlock = heldContents >= 0 && heldContents < BlocksManager.Blocks.Length
                ? BlocksManager.Blocks[heldContents]
                : null;
            if (!(heldBlock is PaintBucketBlock))
            {
                reason = "not-holding-paint-bucket:" +
                    heldContents.ToString(CultureInfo.InvariantCulture);
                return Reject(in request, reason, out result, out reason);
            }
            int expectedColor = heldContents == 128
                ? -1
                : PaintBucketBlock.GetColor(Game.Terrain.ExtractData(heldValue));
            if (expectedColor != request.PaintColor)
            {
                reason = "paint-color-mismatch:" + request.PaintColor.ToString(CultureInfo.InvariantCulture) +
                    "!=" + expectedColor.ToString(CultureInfo.InvariantCulture);
                return Reject(in request, reason, out result, out reason);
            }

            FurnitureDesign painted;
            try
            {
                List<FurnitureDesign> chain = baseDesign.CloneChain();
                int? color = request.PaintColor < 0 ? (int?)null : request.PaintColor;
                for (int i = 0; i < chain.Count; i++) chain[i].Paint(color);
                painted = behavior.TryAddDesignChain(chain[0], garbageCollectIfNeeded: true);
            }
            catch (Exception error)
            {
                reason = "paint-failed:" + error.GetType().Name + ":" + error.Message;
                return Reject(in request, reason, out result, out reason);
            }
            if (painted == null)
            {
                reason = "design-limit";
                return Reject(in request, reason, out result, out reason);
            }
            m_hostPaintApplied++;
            int newData = FurnitureBlock.SetDesignIndex(data, painted.Index,
                painted.ShadowStrengthFactor, painted.IsLightEmitter);
            int newValue = Game.Terrain.ReplaceData(cellValue, newData);
            if (newValue != cellValue)
            {
                try
                {
                    terrain.ChangeCell(x, y, z, newValue);
                    miner.DamageActiveTool(1);
                }
                catch (Exception error)
                {
                    LogProblem("paint-change:" + error.GetType().Name,
                        "host furniture paint cell change failed (" + x + "," + y + "," + z +
                        "): " + error.GetType().Name + " " + error.Message);
                }
            }
            PlayPaintPresentation(terrain, player, x, y, z, request.Face, request.PaintColor);
            installed = ToRecord(painted, painted.Index);
            result = new FurnitureDesignResultData(request.Sequence, true, string.Empty,
                painted.Index, installed.ContentHash, painted.Index == designIndex, 0, false);
            return true;
        }

        private bool TrySpawnFurnitureItem(ComponentPlayer player, ComponentMiner miner,
            FurnitureDesign design, out string reason)
        {
            reason = null;
            SubsystemPickables pickables = m_project?.FindSubsystem<SubsystemPickables>(false);
            if (pickables == null)
            {
                reason = "no-pickables";
                return false;
            }
            try
            {
                int value = Game.Terrain.MakeBlockValue(FurnitureBlock.Index, 0,
                    FurnitureBlock.SetDesignIndex(0, design.Index,
                        design.ShadowStrengthFactor, design.IsLightEmitter));
                int count = MathUtils.Clamp(design.Resolution, 4, 8);
                Matrix matrix = player.ComponentBody.Matrix;
                Vector3 position = matrix.Translation + matrix.Forward + Vector3.UnitY;
                pickables.AddPickable(value, count, position, null, null);
                miner.DamageActiveTool(1);
                miner.Poke(forceRestart: false);
                return true;
            }
            catch (Exception error)
            {
                reason = error.GetType().Name + ":" + error.Message;
                return false;
            }
        }

        private void PlayPaintPresentation(SubsystemTerrain terrain, ComponentPlayer player,
            int x, int y, int z, int face, int color)
        {
            try
            {
                SubsystemAudio audio = m_project.FindSubsystem<SubsystemAudio>(false);
                audio?.PlayRandomSound("Audio/Paint", 0.4f, m_random.Float(-0.1f, 0.1f),
                    player.ComponentBody.Position, 2f, autoDelay: true);
                SubsystemParticles particles = m_project.FindSubsystem<SubsystemParticles>(false);
                if (particles != null && face >= 0 && face <= 5)
                {
                    int? colorIndex = color < 0 ? (int?)null : color;
                    Color tint = SubsystemPalette.GetColor(terrain, colorIndex);
                    var system = new PaintParticleSystem(terrain,
                        new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                        CellFace.FaceToVector3(face), tint);
                    particles.AddParticleSystem(system);
                }
            }
            catch (Exception error)
            {
                LogProblem("paint-presentation:" + error.GetType().Name,
                    "主机播放上色表现失败：" + error.GetType().Name + " " + error.Message);
            }
        }

        // ================================================================= 主机：校验
        //
        // 规则与客户端锤子扫描共用同一份纯逻辑（Furniture/FurnitureSourceModel.cs）：
        //   · 只接受引擎 ScanDesign 会扫描的格子（白名单 / 顶层流体拒绝 / 区块已加载）；
        //   · 源格值是**去灯值**（contents + data），与主机当前格逐格比对（stale-source）；
        //   · 源格集合必须从 start 出发 6 连通（引擎 flood fill 只沿 6 邻居扩展），
        //     26 连通但非 6 连通（对角接触）给独立的 source-diagonal。
        // 这样"创建设计"不能变成销毁附近任意方块的后门，也不会把合法形状误判成不连通。
        private bool TryValidateSourceCells(in FurnitureDesignRequestData request,
            SubsystemTerrain terrain, out string reason)
        {
            return FurnitureSourceSetValidator.TryValidate(request.SourceX, request.SourceY,
                request.SourceZ, request.SourceValues, request.StartX, request.StartY,
                request.StartZ, request.BaseDesignIndex, new TerrainSourceWorld(terrain),
                out reason);
        }

        /// <summary>
        /// 源格世界视图的引擎实现：主机校验与客户端锤子扫描都通过它读地形，
        /// 保证两侧看到的是同一套坐标 / 区块 / 方块规则。
        /// </summary>
        private sealed class TerrainSourceWorld : IFurnitureSourceWorld
        {
            private readonly SubsystemTerrain m_terrain;

            public TerrainSourceWorld(SubsystemTerrain terrain)
            {
                m_terrain = terrain;
            }

            public bool IsCellInRange(int x, int y, int z) =>
                m_terrain?.Terrain != null && m_terrain.Terrain.IsCellValid(x, y, z);

            public bool TryReadCell(int x, int y, int z, out int value)
            {
                value = 0;
                Game.Terrain terrain = m_terrain?.Terrain;
                if (terrain == null || !terrain.IsCellValid(x, y, z)) return false;
                if (terrain.GetChunkAtCell(x, z) == null) return false;
                value = terrain.GetCellValue(x, y, z);
                return true;
            }

            public bool IsFurnitureContents(int contents) => contents == FurnitureBlock.Index;

            public bool IsAllowedContents(int contents) =>
                FurnitureDesignRules.IsAllowedContents(contents);

            public bool IsDisallowedCell(int contents, int data) =>
                FurnitureDesignRules.IsDisallowedCell(contents, data);

            public int GetFurnitureDesignIndex(int data) => FurnitureBlock.GetDesignIndex(data);
        }

        private bool TryValidateDesignProvenance(in FurnitureDesignRequestData request,
            FurnitureDesign baseDesign, out string reason)
        {
            reason = null;
            var allowed = new Dictionary<int, int>();
            if (baseDesign != null)
            {
                int count = baseDesign.Resolution * baseDesign.Resolution * baseDesign.Resolution;
                for (int i = 0; i < count; i++)
                {
                    int value = FurnitureDesignHash.StripLight(baseDesign.GetValue(i));
                    if ((value & 0x3FF) == 0) continue;
                    allowed.TryGetValue(value, out int existing);
                    allowed[value] = existing + 1;
                }
            }
            else
            {
                for (int i = 0; i < request.SourceCount; i++)
                {
                    int value = FurnitureDesignHash.StripLight(request.SourceValues[i]);
                    if ((value & 0x3FF) == 0) continue;
                    allowed.TryGetValue(value, out int existing);
                    allowed[value] = existing + 1;
                }
            }

            var design = new Dictionary<int, int>();
            int valueCount = request.Resolution * request.Resolution * request.Resolution;
            for (int i = 0; i < valueCount; i++)
            {
                int value = FurnitureDesignHash.StripLight(request.Values[i]);
                int contents = value & 0x3FF;
                if (contents == 0) continue;
                if (!FurnitureDesignRules.IsAllowedContents(contents))
                {
                    reason = "design-block:" + contents.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (!allowed.TryGetValue(value, out int allowedCount))
                {
                    reason = "design-value-not-in-source:" + value.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                design.TryGetValue(value, out int used);
                used++;
                if (used > allowedCount)
                {
                    reason = "design-value-count:" + value.ToString(CultureInfo.InvariantCulture) +
                        " used=" + used.ToString(CultureInfo.InvariantCulture) +
                        " allowed=" + allowedCount.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                design[value] = used;
            }
            return true;
        }

        private bool Reject(in FurnitureDesignRequestData request, string code,
            out FurnitureDesignResultData result, out string reason)
        {
            m_hostRejected++;
            result = new FurnitureDesignResultData(request.Sequence, false, code, 0, 0, false, 0,
                false);
            reason = code;
            return false;
        }

        private bool TryGetBlockCount(out string reason)
        {
            reason = null;
            if (m_blockCount <= 0)
            {
                if (BlocksManager.Blocks != null && BlocksManager.Blocks.Length > 0)
                    m_blockCount = BlocksManager.Blocks.Length;
            }
            if (m_blockCount <= 0)
            {
                reason = "block-count-unavailable";
                return false;
            }
            return true;
        }

        // ================================================================= 客户端：接管 / 安装

        public bool TryAdoptClientLibrary(List<FurnitureDesignRecord> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "target-null";
                return false;
            }
            target.Clear();
            if (!m_clientReplica || m_subsystem == null)
            {
                reason = m_clientReplica ? "furniture-shell-missing" : "not-client";
                return false;
            }
            if (m_behavior == null || m_designs == null)
            {
                reason = "furniture-table-unavailable";
                return false;
            }
            if (m_adopted) return true;
            for (int i = 0; i < m_designs.Length; i++)
            {
                FurnitureDesign design = m_designs[i];
                if (design == null) continue;
                target.Add(ToRecord(design, i));
                ProtectFromGarbageCollection(design);
            }
            // 客户端只保留"未分类"设计：集合来自存档，若保留会被 FurnitureInventoryPanel 的
            // 集合操作 / 导出路径当成客户端自己的库。设计与集合都不落盘（Save 写空）。
            ClearClientFurnitureSets();
            m_adopted = true;
            Log("客户端接管已加载的家具设计 " + target.Count + " 条为会话副本（集合已清空，Save 写空）");
            return true;
        }

        private int CountClientFurnitureSets()
        {
            try
            {
                List<FurnitureSet> sets = m_parentField.GetParentField<List<FurnitureSet>>(
                    m_behavior, "m_furnitureSets", typeof(SubsystemFurnitureBlockBehavior));
                return sets == null ? 0 : sets.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void ClearClientFurnitureSets()
        {
            try
            {
                List<FurnitureSet> sets = m_parentField.GetParentField<List<FurnitureSet>>(
                    m_behavior, "m_furnitureSets", typeof(SubsystemFurnitureBlockBehavior));
                if (sets == null) return;
                var copy = new List<FurnitureSet>(sets);
                for (int i = 0; i < copy.Count; i++) m_behavior.DeleteFurnitureSet(copy[i]);
            }
            catch (Exception error)
            {
                LogProblem("sets-clear:" + error.GetType().Name,
                    "清空客户端家具集合失败：" + error.GetType().Name + " " + error.Message);
            }
        }

        public bool TryInstallReplica(FurnitureDesignRecord record, out string reason)
        {
            reason = null;
            if (record == null)
            {
                reason = "record-null";
                return false;
            }
            if (!m_clientReplica || m_subsystem == null)
            {
                reason = m_clientReplica ? "furniture-shell-missing" : "not-client";
                return false;
            }
            if (m_designs == null || m_subsystemTerrain == null)
            {
                reason = "furniture-table-unavailable";
                return false;
            }
            if (!FurnitureDesignStore.IsValidIndex(record.Index))
            {
                reason = "index:" + record.Index.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            try
            {
                int count = record.Resolution * record.Resolution * record.Resolution;
                var values = new int[count];
                for (int i = 0; i < count; i++) values[i] = record.GetValue(i);
                var design = new FurnitureDesign(m_subsystemTerrain);
                design.Name = record.Name;
                design.InteractionMode = (FurnitureInteractionMode)record.InteractionMode;
                design.SetValues(record.Resolution, values);
                m_parentField.ModifyParentField(design, "Index", record.Index,
                    typeof(FurnitureDesign));
                ProtectFromGarbageCollection(design);
                m_designs[record.Index] = design;
                m_replicaLinks[record.Index] = record.LinkedIndex;
                ResolveReplicaLinks();
                m_replicaInstalled++;
                return true;
            }
            catch (Exception error)
            {
                reason = "install:" + error.GetType().Name + ":" + error.Message;
                return false;
            }
        }

        public bool TryRemoveReplica(int index, out string reason)
        {
            reason = null;
            if (m_designs == null)
            {
                reason = "furniture-table-unavailable";
                return false;
            }
            if (!FurnitureDesignStore.IsValidIndex(index))
            {
                reason = "index:" + index.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            FurnitureDesign design = m_designs[index];
            if (design == null)
            {
                reason = "not-present";
                return false;
            }
            m_designs[index] = null;
            try
            {
                m_parentField.ModifyParentField(design, "Index", -1, typeof(FurnitureDesign));
            }
            catch (Exception error)
            {
                LogProblem("remove-index:" + error.GetType().Name,
                    "把副本设计 Index 置 -1 失败 index=" + index + "：" +
                    error.GetType().Name + " " + error.Message);
            }
            m_replicaLinks.Remove(index);
            ResolveReplicaLinks();
            m_replicaRemoved++;
            return true;
        }

        public int EnsureReplicaIntegrity(IReadOnlyList<FurnitureDesignRecord> records,
            List<int> reinstalled)
        {
            if (records == null || m_designs == null) return 0;
            if (m_clientReplica)
            {
                // 客户端的家具套装不同步（且 Save 写空）：每秒清掉本地套装。
                // 否则玩家可以把副本设计拖进本地套装再导出成
                // FurniturePacksManager 的 .scfpack（那就是“保存到别处”）。
                int sets = CountClientFurnitureSets();
                if (sets > 0)
                {
                    m_clientSetsCleared += sets;
                    ClearClientFurnitureSets();
                    LogProblem("sets-cleared",
                        "客户端家具套装 " + sets +
                        " 个已清空（多人家具套装不同步 / 不写存档 / 不导出）");
                }
                ComponentFurnitureInventory inventory = FindLocalFurnitureInventory(out _);
                if (inventory != null && inventory.FurnitureSet != null)
                    inventory.FurnitureSet = null;
            }
            int count = 0;
            for (int i = 0; i < records.Count; i++)
            {
                FurnitureDesignRecord record = records[i];
                if (record == null || !FurnitureDesignStore.IsValidIndex(record.Index)) continue;
                FurnitureDesign existing = m_designs[record.Index];
                if (existing == null || !SameDesign(existing, record))
                {
                    if (TryInstallReplica(record, out string reason))
                    {
                        count++;
                        m_replicaReinstalled++;
                        reinstalled?.Add(record.Index);
                    }
                    else
                    {
                        LogProblem("reinstall:" + ReasonCode(reason),
                            "副本重装失败 index=" + record.Index + "（" + reason + "）");
                    }
                }
                else
                {
                    ProtectFromGarbageCollection(existing);
                }
            }
            ResolveReplicaLinks();
            return count;
        }

        public int SweepClientLibraryLeaks(IReadOnlyCollection<int> allowedIndices,
            List<int> removed)
        {
            if (m_designs == null || allowedIndices == null) return 0;
            var allowed = new HashSet<int>(allowedIndices);
            int count = 0;
            for (int i = 0; i < m_designs.Length; i++)
            {
                FurnitureDesign design = m_designs[i];
                if (design == null) continue;
                if (allowed.Contains(i) && design.Index == i) continue;
                m_designs[i] = null;
                try
                {
                    m_parentField.ModifyParentField(design, "Index", -1,
                        typeof(FurnitureDesign));
                }
                catch (Exception error)
                {
                    LogProblem("leak-index:" + error.GetType().Name,
                        "移除泄漏家具设计时把 Index 置 -1 失败 index=" + i +
                        "：" + error.GetType().Name + " " + error.Message);
                }
                m_replicaLinks.Remove(i);
                removed?.Add(i);
                count++;
            }
            if (count > 0)
            {
                m_replicaRemoved += count;
                m_clientLibraryRejected += count;
                m_clientLibraryLeaks++;
                ResolveReplicaLinks();
            }
            return count;
        }

        private bool SameDesign(FurnitureDesign design, FurnitureDesignRecord record)
        {
            if (design == null || record == null) return false;
            if (design.Resolution != record.Resolution ||
                design.InteractionMode != (FurnitureInteractionMode)record.InteractionMode ||
                (design.Name ?? string.Empty) != (record.Name ?? string.Empty))
                return false;
            int expected = record.ValueCount;
            if (design.Resolution * design.Resolution * design.Resolution != expected)
                return false;
            for (int i = 0; i < expected; i++)
            {
                if (FurnitureDesignHash.StripLight(design.GetValue(i)) != record.GetValue(i))
                    return false;
            }
            int linked = design.LinkedDesign == null ? -1 : design.LinkedDesign.Index;
            return linked == record.LinkedIndex;
        }

        private void ResolveReplicaLinks()
        {
            if (m_designs == null) return;
            foreach (KeyValuePair<int, int> item in m_replicaLinks)
            {
                if (!FurnitureDesignStore.IsValidIndex(item.Key)) continue;
                FurnitureDesign design = m_designs[item.Key];
                if (design == null) continue;
                FurnitureDesign linked = null;
                if (item.Value >= 0 && item.Value < m_designs.Length)
                    linked = m_designs[item.Value];
                try
                {
                    design.LinkedDesign = linked;
                }
                catch (Exception error)
                {
                    LogProblem("link-set:" + error.GetType().Name,
                        "设置家具副本链接失败 index=" + item.Key + "：" +
                        error.GetType().Name + " " + error.Message);
                }
            }
        }

        private void ProtectFromGarbageCollection(FurnitureDesign design)
        {
            if (design == null) return;
            try
            {
                // FillSlots 会先 GarbageCollectDesigns：terrainUseCount==0 且无 FurnitureSet 的
                // 设计会被删掉。副本设计还没摆放时必须保持"在用"，否则家具面板一开就丢。
                m_parentField.ModifyParentField(design, "m_terrainUseCount", 1,
                    typeof(FurnitureDesign));
            }
            catch (Exception error)
            {
                LogProblem("protect:" + error.GetType().Name,
                    "保护副本设计不被 GC 失败：" + error.GetType().Name + " " + error.Message);
            }
        }

        // ================================================================= 客户端：家具库存 / 消息

        public bool TryRefreshFurnitureInventory(out int slotCount, out string reason)
        {
            slotCount = 0;
            reason = null;
            ComponentFurnitureInventory inventory = FindLocalFurnitureInventory(out reason);
            if (inventory == null) return false;
            try
            {
                inventory.FillSlots();
                slotCount = inventory.SlotsCount;
                m_inventoryRefreshed++;
                return true;
            }
            catch (Exception error)
            {
                m_inventoryRefreshFailed++;
                reason = "fill:" + error.GetType().Name + ":" + error.Message;
                return false;
            }
        }

        public bool TryCaptureFurnitureInventory(List<int> indices, out string reason)
        {
            reason = null;
            if (indices == null)
            {
                reason = "target-null";
                return false;
            }
            indices.Clear();
            ComponentFurnitureInventory inventory = FindLocalFurnitureInventory(out reason);
            if (inventory == null) return false;
            List<int> slots;
            try
            {
                slots = m_parentField.GetParentField<List<int>>(inventory, "m_slots",
                    typeof(ComponentFurnitureInventory));
            }
            catch (Exception error)
            {
                reason = "slots:" + error.GetType().Name + ":" + error.Message;
                return false;
            }
            if (slots == null)
            {
                reason = "slots-null";
                return false;
            }
            var seen = new HashSet<int>();
            for (int i = 0; i < slots.Count; i++)
            {
                int value = slots[i];
                if (Game.Terrain.ExtractContents(value) != FurnitureBlock.Index) continue;
                int designIndex = FurnitureBlock.GetDesignIndex(Game.Terrain.ExtractData(value));
                if (FurnitureDesignStore.IsValidIndex(designIndex) && seen.Add(designIndex))
                    indices.Add(designIndex);
            }
            return true;
        }

        public bool TryShowClientMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            ComponentPlayer player = FindLocalPlayer(out _);
            if (player == null) return false;
            try
            {
                player.ComponentGui?.DisplaySmallMessage(text, Color.White, blinking: true,
                    playNotificationSound: false);
                return true;
            }
            catch (Exception error)
            {
                LogProblem("client-message:" + error.GetType().Name,
                    "显示客户端消息失败：" + error.GetType().Name + " " + error.Message);
                return false;
            }
        }

        private ComponentFurnitureInventory FindLocalFurnitureInventory(out string reason)
        {
            ComponentPlayer player = FindLocalPlayer(out reason);
            if (player == null)
            {
                reason = reason ?? "no-local-player";
                return null;
            }
            ComponentFurnitureInventory inventory =
                player.Entity.FindComponent<ComponentFurnitureInventory>();
            if (inventory == null)
            {
                reason = "no-furniture-inventory-component";
                return null;
            }
            return inventory;
        }

        private ComponentPlayer FindLocalPlayer(out string reason)
        {
            reason = null;
            if (m_project == null)
            {
                reason = "no-project";
                return null;
            }
            if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData data, out reason))
                return null;
            ComponentPlayer player = data?.ComponentPlayer;
            if (player == null)
                reason = "local-player-missing";
            return player;
        }

        // ================================================================= 客户端：锤子创建 / 编辑

        /// <summary>
        /// 联机客户端的锤子流程：复刻引擎 ScanDesign 的扫描 / 对话框，点 Build 后只把
        /// 设计 + 源格发给主机。返回 true = 已接管（调用方不执行 base.OnUse）。
        /// </summary>
        public bool TryStartClientDesign(Ray3 ray, ComponentMiner componentMiner)
        {
            if (!m_clientReplica || m_sink == null)
            {
                return false;
            }
            Project project = GameManager.Project;
            SubsystemTerrain terrain = m_subsystemTerrain;
            SubsystemFurnitureBlockBehavior behavior = m_behavior;
            if (project == null || terrain?.Terrain == null || behavior == null ||
                componentMiner?.ComponentPlayer == null)
            {
                LogProblem("hammer-unavailable", "客户端锤子流程缺少 Project / 地形 / 家具子系统 / 玩家" +
                    " → 本次不扫描（明确失败）");
                return false;
            }
            TerrainRaycastResult? hit = componentMiner.Raycast<TerrainRaycastResult>(ray,
                RaycastMode.Digging);
            if (!hit.HasValue) return true;
            CellFace start = hit.Value.CellFace;
            Vector3 direction = ray.Direction;

            // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:ScanDesign
            // 扫描与主机校验共用 FurnitureSourceScanner：同一套 6 邻居 / 白名单 / 跨度规则，
            // 客户端发出的就是主机校验器认的那一组世界格（不会因遍历差异被判不连通）。
            var scan = new FurnitureSourceScan();
            if (!FurnitureSourceScanner.TryScan(new TerrainSourceWorld(terrain), start.X,
                    start.Y, start.Z, scan, out string scanReason))
            {
                ShowHammerScanFailure(componentMiner, scanReason);
                return true;
            }
            FurnitureDesign design;
            FurnitureDesign sourceDesign = null;
            if (scan.StartIsFurniture)
            {
                sourceDesign = behavior.GetDesign(scan.BaseDesignIndex);
                if (sourceDesign == null)
                {
                    ShowHammerScanFailure(componentMiner, "furniture-design-missing");
                    return true;
                }
                design = sourceDesign.Clone();
                design.LinkedDesign = null;
                design.InteractionMode = FurnitureInteractionMode.None;
            }
            else
            {
                design = new FurnitureDesign(terrain);
                int resolution = MathUtils.Max(MathUtils.Max(scan.MaxX - scan.MinX,
                    scan.MaxY - scan.MinY, scan.MaxZ - scan.MinZ) + 1, 2);
                var values = new int[resolution * resolution * resolution];
                for (int i = 0; i < scan.Count; i++)
                {
                    values[(scan.X[i] - scan.MinX) + (scan.Y[i] - scan.MinY) * resolution +
                        (scan.Z[i] - scan.MinZ) * resolution * resolution] =
                        FurnitureDesignHash.StripLight(scan.Values[i]);
                }
                design.SetValues(resolution, values);
                int steps = start.Face > 3
                    ? CellFace.Vector3ToFace(direction, 3)
                    : CellFace.OppositeFace(start.Face);
                design.Rotate(1, steps);
                Point3 location = design.Box.Location;
                Point3 far = new Point3(design.Resolution) -
                    (design.Box.Location + design.Box.Size);
                Point3 delta = new Point3((far.X - location.X) / 2, -location.Y,
                    (far.Z - location.Z) / 2);
                design.Shift(delta);
            }

            // 源格 = **扫描到的世界坐标**原样发送；值去灯（contents + data），与主机校验同口径。
            int sourceCount = scan.Count;
            var sourceX = new int[sourceCount];
            var sourceY = new int[sourceCount];
            var sourceZ = new int[sourceCount];
            var sourceValues = new int[sourceCount];
            for (int i = 0; i < sourceCount; i++)
            {
                sourceX[i] = scan.X[i];
                sourceY[i] = scan.Y[i];
                sourceZ[i] = scan.Z[i];
                sourceValues[i] = FurnitureDesignHash.StripLight(scan.Values[i]);
            }

            // 对话框的 "Rename Original Furniture" 会改 m_sourceDesign；传一个**克隆**，
            // 避免改到客户端副本表里的引擎对象（否则本地改名会污染副本，且主机不知道）。
            FurnitureDesign dialogSource = sourceDesign?.Clone();
            string sourceName = sourceDesign == null ? string.Empty : sourceDesign.Name;
            var dialog = new BuildFurnitureDialog(design, dialogSource, delegate (bool accepted)
            {
                if (m_sink == null)
                {
                    LogProblem("hammer-detached", "家具设计对话框确认时模块已下线 → 不发送请求");
                    return;
                }
                if (sourceDesign != null && dialogSource != null &&
                    dialogSource.Name != sourceName)
                {
                    var rename = new FurnitureDesignRequestData(0L, FurnitureDesignOps.Rename,
                        start.X, start.Y, start.Z, start.Face, 0, dialogSource.Name, 0,
                        Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(),
                        Array.Empty<int>(), Array.Empty<int>(), sourceDesign.Index, -1,
                        hasReach: true, reach: EngineReach.LocalReach());
                    if (!m_sink.TrySubmitDesignRequest(in rename, out string renameReason))
                    {
                        LogProblem("hammer-rename:" + ReasonCode(renameReason),
                            "家具改名请求提交失败（" + renameReason + "）→ 本地不改名");
                        componentMiner.ComponentPlayer.ComponentGui.DisplaySmallMessage(
                            "Furniture rename request failed: " + renameReason, Color.White,
                            blinking: true, playNotificationSound: false);
                    }
                }
                if (!accepted) return;
                if (!ReferenceEquals(GameManager.Project, project) ||
                    !ReferenceEquals(componentMiner.Project, project))
                {
                    LogProblem("hammer-world-changed",
                        "家具设计对话框确认时世界已更换 → 不发送请求（明确失败）");
                    return;
                }
                int resolution = design.Resolution;
                int count = resolution * resolution * resolution;
                var values = new int[count];
                for (int i = 0; i < count; i++)
                    values[i] = FurnitureDesignHash.StripLight(design.GetValue(i));
                var request = new FurnitureDesignRequestData(0L, FurnitureDesignOps.Create,
                    start.X, start.Y, start.Z, start.Face, resolution, design.Name,
                    (int)design.InteractionMode, values,
                    sourceX, sourceY, sourceZ, sourceValues,
                    sourceDesign == null ? -1 : sourceDesign.Index, -1,
                    hasReach: true, reach: EngineReach.LocalReach());
                if (!m_sink.TrySubmitDesignRequest(in request, out string submitReason))
                {
                    LogProblem("hammer-submit:" + ReasonCode(submitReason),
                        "家具设计请求提交失败（" + submitReason + "）→ 本地没有创建设计");
                    componentMiner.ComponentPlayer.ComponentGui.DisplaySmallMessage(
                        "Furniture design request failed: " + submitReason, Color.White,
                        blinking: true, playNotificationSound: false);
                    return;
                }
                componentMiner.ComponentPlayer.ComponentGui.DisplaySmallMessage(
                    "Furniture design sent to host", Color.White, blinking: false,
                    playNotificationSound: false);
            });
            DialogsManager.ShowDialog(componentMiner.ComponentPlayer.GuiWidget, dialog);
            return true;
        }

        /// <summary>
        /// 锤子扫描的明确失败（不发送请求）：引擎原文案三类（Unsuitable block found /
        /// Furniture design is too large / No suitable blocks found）保持原样，
        /// 其它原因显式显示 reason 并限流记日志（不静默跳过）。
        /// </summary>
        private void ShowHammerScanFailure(ComponentMiner componentMiner, string reason)
        {
            string message;
            switch (reason)
            {
                case "disallowed":
                case "furniture-design-missing":
                    message = "Unsuitable block found";
                    break;
                case "too-large":
                    message = "Furniture design is too large";
                    break;
                case "no-source":
                    message = "No suitable blocks found";
                    break;
                default:
                    message = "Furniture design scan failed: " + reason;
                    LogProblem("hammer-scan:" + ReasonCode(reason),
                        "客户端锤子扫描失败（" + reason + "）→ 不发送设计请求（明确失败）");
                    break;
            }
            componentMiner.ComponentPlayer.ComponentGui.DisplaySmallMessage(message,
                Color.White, blinking: true, playNotificationSound: false);
        }

        // ================================================================= 客户端：颜料桶

        /// <summary>
        /// 联机客户端 + 颜料桶 + 家具目标：改成主机上色请求。
        /// 返回 false = 不是这个组合（调用方走 base）；true = 已接管（成功或明确失败）。
        /// </summary>
        public bool TryInterceptClientFurniturePaint(Ray3 ray, ComponentMiner componentMiner)
        {
            if (!m_clientReplica || componentMiner?.Inventory == null) return false;
            int activeSlot = componentMiner.Inventory.ActiveSlotIndex;
            if (activeSlot < 0 || activeSlot >= componentMiner.Inventory.SlotsCount) return false;
            int activeValue = componentMiner.Inventory.GetSlotValue(activeSlot);
            int activeContents = Game.Terrain.ExtractContents(activeValue);
            if (activeContents != 128 && activeContents != 129) return false;
            TerrainRaycastResult? hit = componentMiner.Raycast<TerrainRaycastResult>(ray,
                RaycastMode.Digging);
            if (!hit.HasValue) return false;
            CellFace face = hit.Value.CellFace;
            int cellValue = m_subsystemTerrain?.Terrain?.GetCellValue(face.X, face.Y, face.Z) ?? 0;
            if (Game.Terrain.ExtractContents(cellValue) != FurnitureBlock.Index) return false;
            Block block = activeContents >= 0 && activeContents < BlocksManager.Blocks.Length
                ? BlocksManager.Blocks[activeContents]
                : null;
            if (!(block is PaintBucketBlock)) return false;
            int color = activeContents == 128
                ? -1
                : PaintBucketBlock.GetColor(Game.Terrain.ExtractData(activeValue));
            int designIndex = FurnitureBlock.GetDesignIndex(Game.Terrain.ExtractData(cellValue));
            var request = new FurnitureDesignRequestData(0L, FurnitureDesignOps.Paint,
                face.X, face.Y, face.Z, face.Face, 0, string.Empty, 0,
                Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(),
                Array.Empty<int>(), Array.Empty<int>(), designIndex, color,
                hasReach: true, reach: EngineReach.LocalReach());
            m_clientPaintBlocked++;
            if (m_sink == null)
            {
                LogProblem("paint-no-sink", "客户端颜料桶请求没有模块入口 → 不执行本地 Paint");
                return true;
            }
            if (!m_sink.TrySubmitDesignRequest(in request, out string reason))
            {
                LogProblem("paint-submit:" + ReasonCode(reason),
                    "家具上色请求提交失败（" + reason + "）→ 本地不执行 Paint（家具设计不会污染客户端库）");
                TryShowClientMessage("Furniture paint request failed: " + reason);
                return true;
            }
            return true;
        }

        // ================================================================= IFurnitureEngineEvents / 闸门

        public bool IsMultiplayerClient => m_clientReplica;

        public void NoteClientSaveSuppressed(int designCount)
        {
            m_clientSaveSuppressed++;
            LogProblem("client-save", "客户端 Save 已把家具设计表写空（原本 " + designCount +
                " 条副本设计）→ 联机设计绝不写进客户端家具库 / 世界存档（累计 " +
                m_clientSaveSuppressed + " 次）");
        }

        public void NoteMissingDesignCell(int designIndex)
        {
            m_clientMissingDesignCells++;
            LogProblem("missing-design-cell",
                "客户端地形里的家具格引用了尚未同步的设计 index=" + designIndex +
                " → 跳过 useCount / 粒子登记（不崩溃；设计到达后正常渲染），累计 " +
                (m_clientMissingDesignCells + (m_subsystem?.MissingDesignCells ?? 0)) +
                " 格·次");
        }

        public void NoteClientCraftBlocked()
        {
            m_clientCraftBlocked++;
            LogProblem("client-craft-blocked",
                "客户端直接调到 FurnitureBlock.GetAdHocCraftingRecipe（会新增设计）→ 返回 null 并计数；" +
                "累计 clientLibraryCraftBlocked=" + m_clientCraftBlocked);
        }

        bool IClientFurnitureDesignGuard.IsReplicaDesign(int designIndex) =>
            m_sink != null && m_sink.IsReplicaDesign(designIndex);

        bool IClientFurnitureDesignGuard.IsSynced => m_sink != null && m_sink.IsSynced;

        // ================================================================= 诊断

        public string Describe()
        {
            return "furnitureEngine(session=" + m_sessionSerial.ToString(CultureInfo.InvariantCulture) +
                " client=" + m_clientReplica +
                " adopted=" + m_adopted +
                " blockCount=" + m_blockCount.ToString(CultureInfo.InvariantCulture) +
                " replicaInstalled=" + m_replicaInstalled.ToString(CultureInfo.InvariantCulture) +
                " replicaRemoved=" + m_replicaRemoved.ToString(CultureInfo.InvariantCulture) +
                " replicaReinstalled=" + m_replicaReinstalled.ToString(CultureInfo.InvariantCulture) +
                " clientLibraryRejected=" + m_clientLibraryRejected.ToString(CultureInfo.InvariantCulture) +
                " clientLibraryLeaks=" + m_clientLibraryLeaks.ToString(CultureInfo.InvariantCulture) +
                " clientLibrarySaveSuppressed=" + m_clientSaveSuppressed.ToString(CultureInfo.InvariantCulture) +
                " clientLibraryMissingDesignCells=" +
                (m_clientMissingDesignCells + (m_subsystem?.MissingDesignCells ?? 0))
                    .ToString(CultureInfo.InvariantCulture) +
                " clientLibraryPaintBlocked=" + m_clientPaintBlocked.ToString(CultureInfo.InvariantCulture) +
                " clientLibraryCraftBlocked=" + m_clientCraftBlocked.ToString(CultureInfo.InvariantCulture) +
                " hostInstalled=" + m_hostInstalled.ToString(CultureInfo.InvariantCulture) +
                " hostPaintApplied=" + m_hostPaintApplied.ToString(CultureInfo.InvariantCulture) +
                " hostRenamed=" + m_hostRenamed.ToString(CultureInfo.InvariantCulture) +
                " hostRejected=" + m_hostRejected.ToString(CultureInfo.InvariantCulture) +
                " hostSpawnFailures=" + m_hostSpawnFailures.ToString(CultureInfo.InvariantCulture) +
                " inventoryRefreshed=" + m_inventoryRefreshed.ToString(CultureInfo.InvariantCulture) +
                " inventoryRefreshFailed=" + m_inventoryRefreshFailed.ToString(CultureInfo.InvariantCulture) +
                " clientSetsCleared=" + m_clientSetsCleared.ToString(CultureInfo.InvariantCulture) +
                " clientFurnitureEditsDropped=" +
                (m_terrainShell == null ? 0 : m_terrainShell.ClientFurnitureEditsDropped)
                    .ToString(CultureInfo.InvariantCulture) +
                ")";
        }

        private static string ReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "none";
            int colon = reason.IndexOf(':');
            string code = colon > 0 ? reason.Substring(0, colon) : reason;
            code = code.Trim();
            if (code.Length == 0) return "none";
            return code.Length > 48 ? code.Substring(0, 48) : code;
        }

        private void Log(string message) =>
            m_diagnostics?.Info(m_ownerId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}
