using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端"使用 / 交互"输入的接收方（实现 = <see cref="ContainerReplicaEngine"/>）。
    /// 返回 true = 这次点击已由本适配器接管（请求已提交 / 明确失败并计数）；
    /// 调用方随后必须吞掉 <c>PlayerInput.Interact</c>，客户端**绝不**本地执行交互。
    /// </summary>
    public interface IEntityUseInputSink
    {
        bool TryAcceptInteract(ComponentInput input, ComponentPlayer player);
    }

    /// <summary>
    /// 方块使用 / 容器同步的**引擎适配层**（新文件）：
    ///
    /// 客户端：
    ///   · 在 <c>ComponentInput.Update</c> 之后、<c>ComponentPlayer.Update</c> 之前拦下
    ///     <c>PlayerInput.Interact</c>（射线命中且方块 <c>IsInteractive</c>），提交
    ///     <c>BlockUseRequest</c> 并清空输入 —— 原生 Use/Interact/Place 整段不执行；
    ///   · 主机授权后按 ContainerState 打开**副本**面板（副本方块实体 + 权威槽位），
    ///     槽位变化由模块轮询上报；拒绝 / 超时回滚到权威状态与编辑前背包；
    ///   · 客户端副本实体的 IUpdateable（熔炉烧炼等）被摘掉，副本实体也不进存档实体表。
    ///
    /// 主机：
    ///   · 校验玩家端 / 距离 / 方块内容 / 方块行为；
    ///   · 容器：找到或**创建**权威方块实体库存（客户端放置的工作台在主机侧必有记录），
    ///     返回权威快照；容器编辑在主机按 base 校验后落地，并自增 revision；
    ///   · 门 / 按钮 / 家具等：用远端玩家自己的 ComponentMiner 调引擎方块行为，
    ///     结果随既有 CellChange 广播（不另造地形消息）；
    ///   · 轮询被客户端打开的容器（主机自己的面板 / 熔炉烧炼）并广播状态。
    ///
    /// 无静态状态；计数器与状态都挂在实例上。
    /// </summary>
    public sealed class ContainerReplicaEngine : IBlockUsePort, IEntityUseInputSink
    {
        /// <summary>主机把"附近燃烧熔炉"纳入炉况下发的距离（格²）：关闭但可见也有正确外观。</summary>
        private const float NearFurnaceDistanceSq = 32f * 32f;
        /// <summary>主机 / 客户端熔炉状态表上限（超出清空；只影响"变化才发"的缓存，不丢消息）。</summary>
        private const int MaxTrackedFurnaceStates = 256;
        private const int ProblemLogMs = 5000;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly InteractionReachGate m_reachGate;
        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<ContainerKey, int> m_hostRevisions =
            new Dictionary<ContainerKey, int>();
        private readonly Dictionary<ContainerKey, ContainerStateData> m_hostLastStates =
            new Dictionary<ContainerKey, ContainerStateData>();
        /// <summary>主机：每个熔炉的炉况 revision（数值变化才前进；打开时无条件下发当前值）。</summary>
        private readonly Dictionary<ContainerKey, int> m_hostFurnaceRevisions =
            new Dictionary<ContainerKey, int>();
        /// <summary>主机：每个熔炉最近一次抓取的炉况（判断"数值是否变化" / "是不是刚熄灭"）。</summary>
        private readonly Dictionary<ContainerKey, FurnaceStateData> m_hostLastFurnaceStates =
            new Dictionary<ContainerKey, FurnaceStateData>();
        /// <summary>主机：本拍附近扫描用的联机角色位置（复用，避免每次分配）。</summary>
        private readonly List<KeyValuePair<int, Vector3>> m_furnaceClients =
            new List<KeyValuePair<int, Vector3>>(8);
        private readonly HashSet<Entity> m_replicaEntities = new HashSet<Entity>();
        private readonly HashSet<Entity> m_hostCreatedEntities = new HashSet<Entity>();
        /// <summary>contents → 该方块是否实现 OnUse（缓存；无静态状态）。</summary>
        private readonly Dictionary<int, bool> m_onUseByContents = new Dictionary<int, bool>();

        private Project m_project;
        private bool m_clientReplica;
        private IBlockUseRequestSink m_requestSink;
        private SuComponentInput m_useInput;
        private ModFieldRef<ComponentInput, PlayerInput> m_inputRef;

        private ContainerKey? m_viewKey;
        private Widget m_viewWidget;
        private ComponentInventoryBase m_viewInventory;
        private int m_viewKind;

        private FieldInfo m_slotValueField;
        private FieldInfo m_slotCountField;
        private bool m_inputInjectionLogged;
        private long m_nextReplicaSweepMs;
        private readonly List<Entity> m_sweepScratch = new List<Entity>(256);

        // ---------------------------------------------------------------- 计数
        private long m_useIntents;
        private long m_useIntentSubmitted;
        private long m_useIntentFailures;
        private long m_useIntentIgnoredNotInteractive;
        private long m_useIntentIgnoredPresentation;
        private long m_inputInjectionMissing;

        private long m_hostUseAccepted;
        private long m_hostUseRejected;
        private long m_hostNativeInteractions;
        private long m_hostContainerOpens;
        private long m_hostBlockEntitiesCreated;
        private long m_hostEditsApplied;
        private long m_hostEditsRejected;
        private long m_hostCraftGridApplied;
        private long m_hostResultTaken;
        private long m_hostCraftPredictionMismatch;
        private long m_hostPlayerMirrorRemoved;
        private long m_hostPlayerMirrorAdded;
        private long m_hostPlayerMirrorLeftover;
        private long m_hostPlayerMirrorDiverged;
        private long m_hostPlayerMirrorSlotFailures;
        private long m_hostStatesPolled;
        private long m_hostStateBroadcasts;
        private long m_hostKindMismatches;
        private long m_hostFurnaceStateCaptures;
        private long m_hostFurnaceNearScanFailures;
        private long m_hostFurnaceTargetCapHits;
        private long m_clientFurnaceLitWrites;
        private long m_clientFurnaceProgressWrites;
        private long m_clientFurnaceComponentMissing;
        private long m_clientFurnaceApplyFailures;
        private long m_clientFurnaceFlamePresenterMissing;
        private long m_clientFurnaceFlameFailures;
        /// <summary>请求者的主机侧背包是创造仓库（ComponentCreativeInventory）的容器编辑次数。</summary>
        private long m_hostCreativeInventoryDetected;
        /// <summary>创造请求者的编辑被接受（守恒被创造模式规则替代）的次数。</summary>
        private long m_hostCreativeTransferAllowed;
        /// <summary>创造请求者的编辑里跳过的"玩家实时镜像背包"写入次数（背包由客户端权威上报）。</summary>
        private long m_hostCreativePlayerMirrorSkipped;
        /// <summary>
        /// 创造模式放行的 **data 变体** 件数：物品完整值不在创造仓库，但 contents 位由创造仓库提供
        /// （内存条地图 / 告示牌文字 / 家具设计 id）。这是**允许**计数，不是拒绝原因。
        /// </summary>
        private long m_hostCreativeDataVariantAllowed;
        /// <summary>创造模式放行的"请求者权威背包里已有该物品"件数（移动，不是创造）。</summary>
        private long m_hostCreativeInventoryMoveAllowed;

        private long m_clientViewsOpened;
        private long m_clientViewsClosed;
        private long m_clientStatesApplied;
        private long m_clientStateApplyFailures;
        private long m_clientReplicaEntitiesCreated;
        private long m_clientReplicaKindReplaced;

        public ContainerReplicaEngine(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModParentMethod parentMethod, IModuleDiagnostics diagnostics, string ownerId,
            InteractionReachGate reachGate)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_reachGate = reachGate ?? throw new ArgumentNullException(nameof(reachGate));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "block-use" : ownerId;
        }

        /// <summary>客户端本机有效交互距离（引擎 reach），由 BlockUseRequest 携带给主机。</summary>
        public float LocalReach => EngineReach.LocalReach();

        // ================================================================= IBlockUsePort（挂接）

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                ResetEngineState("project-change");
                m_project = project;
            }
            if (project == null)
            {
                DetachUseInput();
                return;
            }
            bool client = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                shell.ClientReplica;
            if (client != m_clientReplica)
            {
                ResetEngineState("role-change");
                m_clientReplica = client;
            }
            if (client)
            {
                UpdateUseInput(project);
                SweepClientReplicaEntities(project);
            }
            else
            {
                DetachUseInput();
            }
        }

        public void SetRequestSink(IBlockUseRequestSink sink) => m_requestSink = sink;

        public string Describe() =>
            "blockUse(intents=" + m_useIntents + " submitted=" + m_useIntentSubmitted +
            " failed=" + m_useIntentFailures + " ignoredNotInteractive=" +
            m_useIntentIgnoredNotInteractive + " ignoredPresentation=" +
            m_useIntentIgnoredPresentation + " inputMissing=" + m_inputInjectionMissing +
            " · host accepted=" + m_hostUseAccepted + " rejected=" + m_hostUseRejected +
            " native=" + m_hostNativeInteractions + " containerOpens=" + m_hostContainerOpens +
            " entitiesCreated=" + m_hostBlockEntitiesCreated + " edits=" + m_hostEditsApplied +
            "/" + m_hostEditsRejected + " craftGrid=" + m_hostCraftGridApplied +
            " resultTaken=" + m_hostResultTaken + " predMismatch=" +
            m_hostCraftPredictionMismatch + " playerMirror=" + m_hostPlayerMirrorRemoved +
            "/" + m_hostPlayerMirrorAdded + " mirrorLeftover=" + m_hostPlayerMirrorLeftover +
            " mirrorDiverged=" + m_hostPlayerMirrorDiverged + " mirrorFail=" +
            m_hostPlayerMirrorSlotFailures + " kindMismatch=" + m_hostKindMismatches +
            " creativeInventoryDetected=" + m_hostCreativeInventoryDetected +
            " creativeTransferAllowed=" + m_hostCreativeTransferAllowed +
            " creativeMirrorSkipped=" + m_hostCreativePlayerMirrorSkipped +
            " creativeDataVariantAllowed=" + m_hostCreativeDataVariantAllowed +
            " creativeInventoryMoveAllowed=" + m_hostCreativeInventoryMoveAllowed +
            " polled=" + m_hostStatesPolled + " broadcast=" + m_hostStateBroadcasts +
            " furnaceCaptured=" + m_hostFurnaceStateCaptures + " furnaceScanFailed=" +
            m_hostFurnaceNearScanFailures + " furnaceTargetCap=" + m_hostFurnaceTargetCapHits +
            " · client views=" + m_clientViewsOpened + "/" + m_clientViewsClosed +
            " statesApplied=" + m_clientStatesApplied + " applyFailed=" +
            m_clientStateApplyFailures + " replicas=" + m_clientReplicaEntitiesCreated +
            " kindReplaced=" + m_clientReplicaKindReplaced +
            " furnaceLitWrites=" + m_clientFurnaceLitWrites + " furnaceProgressWrites=" +
            m_clientFurnaceProgressWrites + " furnaceComponentMissing=" +
            m_clientFurnaceComponentMissing + " furnaceApplyFailed=" +
            m_clientFurnaceApplyFailures + " furnaceFlamePresenterMissing=" +
            m_clientFurnaceFlamePresenterMissing + " furnaceFlameFailed=" +
            m_clientFurnaceFlameFailures + ")";

        // ================================================================= 客户端：输入拦截

        private void UpdateUseInput(Project project)
        {
            if (m_spawner.TryGetLocalPlayer(project, out PlayerData local, out _))
            {
                ComponentPlayer player = local?.ComponentPlayer;
                ComponentInput input = player?.Entity?.FindComponent<ComponentInput>();
                if (input is SuComponentInput shell)
                {
                    if (!ReferenceEquals(shell, m_useInput))
                    {
                        DetachUseInput();
                        m_useInput = shell;
                        m_inputRef = m_parentField.BindFieldRef<ComponentInput, PlayerInput>(
                            "m_playerInput");
                    }
                    shell.UseSink = this;
                    return;
                }
                if (input != null && !m_inputInjectionLogged)
                {
                    m_inputInjectionLogged = true;
                    m_inputInjectionMissing++;
                    LogProblem("use-input-injection",
                        "客户端没有注入 SuComponentInput（当前：" + input.GetType().Name +
                        "）→ 无法在本地交互之前拦截（请检查 Game.ComponentInput 注入）");
                }
            }
            DetachUseInput();
        }

        /// <summary>
        /// 客户端：每 ~2s 把世界里所有方块实体库存（包括引擎在客户端放置时创建的本地实体）
        /// 标记为**副本**——摘掉 IUpdateable（熔炉不再本地烧炼 / 不再本地改槽位），并从存档实体表移除。
        /// 不这样做的话，客户端在打开面板之前就会自己跑熔炉逻辑，与主机权威库存冲突。
        /// </summary>
        private void SweepClientReplicaEntities(Project project)
        {
            long now = Environment.TickCount64;
            if (now < m_nextReplicaSweepMs) return;
            m_nextReplicaSweepMs = now + 2000;
            m_sweepScratch.Clear();
            foreach (Entity entity in project.Entities) m_sweepScratch.Add(entity);
            int disabled = 0;
            for (int i = 0; i < m_sweepScratch.Count; i++)
            {
                Entity entity = m_sweepScratch[i];
                if (entity == null || entity.FindComponent<ComponentBlockEntity>() == null) continue;
                bool isContainer = entity.FindComponent<ComponentCraftingTable>() != null ||
                    entity.FindComponent<ComponentChest>() != null ||
                    entity.FindComponent<ComponentFurnace>() != null ||
                    entity.FindComponent<ComponentDispenser>() != null;
                if (!isContainer) continue;
                if (m_replicaEntities.Contains(entity)) continue;
                PrepareReplicaEntity(entity);
                disabled++;
            }
            if (disabled > 0)
            {
                Log("客户端把 " + disabled + " 个方块实体库存标记为副本（摘掉 IUpdateable + 不写存档）；" +
                    "累计 " + m_replicaEntities.Count);
            }
        }

        private void DetachUseInput()
        {
            if (m_useInput == null) return;
            if (ReferenceEquals(m_useInput.UseSink, this)) m_useInput.UseSink = null;
            m_useInput = null;
        }

        bool IEntityUseInputSink.TryAcceptInteract(ComponentInput input, ComponentPlayer player)
        {
            if (!m_clientReplica || input == null || player == null || m_project == null) return false;
            IBlockUseRequestSink sink = m_requestSink;
            if (sink == null) return false;   // 模块未装配：保持原生行为（不是新功能路径）
            if (m_spawner == null ||
                !m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _) ||
                !ReferenceEquals(local?.ComponentPlayer, player))
            {
                return false;
            }
            ComponentMiner miner = player.ComponentMiner;
            if (miner == null) return false;
            Ray3? ray = input.PlayerInput.Interact;
            if (!ray.HasValue) return false;
            TerrainRaycastResult? hit;
            try
            {
                hit = miner.Raycast<TerrainRaycastResult>(ray.Value, RaycastMode.Interaction);
            }
            catch (Exception e)
            {
                m_useIntentFailures++;
                LogProblem("use-raycast", "客户端交互射线查询失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 本帧按原生行为处理（只记录一次/5s）");
                return false;
            }
            if (!hit.HasValue) return false;
            m_useIntents++;
            int contents = Game.Terrain.ExtractContents(hit.Value.Value);
            SubsystemTerrain terrain = m_project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null || contents < 0 || contents >= BlocksManager.Blocks.Length)
                return false;
            Block block = BlocksManager.Blocks[contents];
            bool interactive = false;
            try
            {
                interactive = block != null && block.IsInteractive(terrain, hit.Value.Value);
            }
            catch (Exception)
            {
                interactive = false;
            }
            if (!interactive)
            {
                m_useIntentIgnoredNotInteractive++;
                return false;   // 放置 / 其它 native 路径：既有 CellEditRequest 照旧
            }
            if (IsPresentationOnlyInteractive(contents))
            {
                m_useIntentIgnoredPresentation++;
                return false;   // 告示牌编辑等纯本地 UI：不接管
            }
            int activeContents = Game.Terrain.ExtractContents(miner.ActiveBlockValue);
            if (ActiveItemHasOnUse(terrain, activeContents))
            {
                // 手持物自身要消费这次右击（水桶 / 肥料 / 锤子 / 火柴 / 马鞍 / 哨子 ...）：
                // 保持原生 Use 路径（既有地形/背包同步照旧），不把它换成"打开方块"。
                m_useIntentIgnoredPresentation++;
                return false;
            }
            CellFace cellFace = hit.Value.CellFace;
            Vector3 direction = ray.Value.Direction;
            if (direction.LengthSquared() < 1e-6f) direction = new Vector3(0f, 0f, 1f);
            var request = new BlockUseRequestData(0L, cellFace.X, cellFace.Y, cellFace.Z,
                cellFace.Face, contents, BlockUseActions.Interact, ray.Value.Position.X,
                ray.Value.Position.Y, ray.Value.Position.Z, direction.X, direction.Y,
                direction.Z, hasReach: true, reach: EngineReach.LocalReach());
            bool submitted;
            string reason;
            try
            {
                submitted = sink.TrySubmitUse(in request, out reason);
            }
            catch (Exception e)
            {
                submitted = false;
                reason = "submit-exception-" + e.GetType().Name;
            }
            // 无论请求有没有发出去，都吞掉原生输入：客户端不产生本地交互效果。
            if (m_inputRef != null)
            {
                try { m_inputRef(input).Interact = null; }
                catch (Exception e)
                {
                    LogProblem("use-input-clear", "清空 Interact 输入失败（" + e.GetType().Name +
                        " " + e.Message + "）→ 引擎可能仍执行一次本地交互（明确记录）");
                }
            }
            if (submitted)
            {
                m_useIntentSubmitted++;
            }
            else
            {
                m_useIntentFailures++;
                LogProblem("use-intent:" + reason, "客户端方块使用意图没有发出去（" + reason +
                    "）→ 本次点击无效果（不本地执行交互），累计失败 " + m_useIntentFailures);
            }
            return true;
        }

        /// <summary>
        /// 手持方块是否实现了 <c>OnUse</c>（引擎的 Use 在 Interact 之前执行）。
        /// 用反射看最派生的 OnUse 是否不是 SubsystemBlockBehavior 的空实现；按 contents 缓存。
        /// </summary>
        private bool ActiveItemHasOnUse(SubsystemTerrain terrain, int contents)
        {
            if (contents <= 0 || contents >= BlocksManager.Blocks.Length) return false;
            if (m_onUseByContents.TryGetValue(contents, out bool known)) return known;
            bool has = false;
            SubsystemBlockBehaviors behaviors =
                terrain?.Project?.FindSubsystem<SubsystemBlockBehaviors>(false);
            if (behaviors != null)
            {
                SubsystemBlockBehavior[] list = behaviors.GetBlockBehaviors(contents);
                for (int i = 0; i < list.Length; i++)
                {
                    MethodInfo method = list[i].GetType().GetMethod("OnUse",
                        BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    if (method != null && method.DeclaringType != typeof(SubsystemBlockBehavior))
                    {
                        has = true;
                        break;
                    }
                }
            }
            m_onUseByContents[contents] = has;
            return has;
        }

        /// <summary>
        /// 不在本次"方块使用"范围内的交互：
        ///   · 告示牌（97/98/210/211）：交互是打开本地编辑框，文本走"可编辑数据"域；
        ///   · 营火（209）：交互会消耗手持燃料（组件 RemoveActiveTool），而客户端背包仍由
        ///     客户端权威上报 —— 若在这里接管，主机会替客户端扣燃料、客户端随后又把它同步回来，
        ///     等于凭空生成燃料。保持原生本地路径（格子 data 变化仍走既有 CellEditRequest）。
        /// </summary>
        private static bool IsPresentationOnlyInteractive(int contents) =>
            contents == 97 || contents == 98 || contents == 210 || contents == 211 ||
            contents == 209;

        // ================================================================= 客户端：副本面板

        public bool TryCapturePlayerInventory(out PlayerInventorySnapshot snapshot,
            out string reason)
        {
            snapshot = null;
            reason = null;
            if (m_project == null)
            {
                reason = "没有加载中的 Project";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out reason))
                return false;
            IInventory inventory = local?.ComponentPlayer?.ComponentMiner?.Inventory;
            if (inventory == null)
            {
                reason = "本机玩家没有可用背包（ComponentMiner.Inventory 为空）";
                return false;
            }
            int slots = inventory.SlotsCount;
            if (slots <= 0 || slots > ContainerStateData.MaxSlots)
            {
                reason = "背包槽位数非法：" + slots;
                return false;
            }
            var values = new int[slots];
            var counts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                values[i] = inventory.GetSlotValue(i);
                counts[i] = inventory.GetSlotCount(i);
            }
            snapshot = new PlayerInventorySnapshot(values, counts);
            return true;
        }

        public bool IsContainerViewOpen
        {
            get
            {
                if (m_viewWidget == null || m_viewInventory == null || m_project == null)
                    return false;
                if (!m_viewInventory.IsAddedToProject) return false;
                if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _))
                    return false;
                ComponentGui gui = local?.ComponentPlayer?.ComponentGui;
                return gui != null && ReferenceEquals(gui.ModalPanelWidget, m_viewWidget);
            }
        }

        public bool TryCaptureOpenContainer(out int[] values, out int[] counts, out string reason)
        {
            values = null;
            counts = null;
            reason = null;
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            if (!m_viewKey.HasValue || m_viewInventory == null ||
                !m_viewInventory.IsAddedToProject)
            {
                reason = "没有打开中的容器副本";
                return false;
            }
            if (m_viewInventory.SlotsCount <= 0 ||
                m_viewInventory.SlotsCount > ContainerStateData.MaxSlots)
            {
                reason = "副本库存槽位数非法：" + m_viewInventory.SlotsCount;
                return false;
            }
            values = new int[m_viewInventory.SlotsCount];
            counts = new int[m_viewInventory.SlotsCount];
            for (int i = 0; i < m_viewInventory.SlotsCount; i++)
            {
                values[i] = m_viewInventory.GetSlotValue(i);
                counts[i] = m_viewInventory.GetSlotCount(i);
            }
            return true;
        }

        public bool TryOpenContainerView(ContainerStateData state, out string reason)
        {
            reason = null;
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            if (state == null || !state.Key.IsValid)
            {
                reason = "容器状态为空 / 种类非法";
                return false;
            }
            if (m_project == null)
            {
                reason = "没有加载中的 Project";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out reason))
                return false;
            ComponentPlayer player = local?.ComponentPlayer;
            ComponentMiner miner = player?.ComponentMiner;
            IInventory playerInventory = miner?.Inventory;
            ComponentGui gui = player?.ComponentGui;
            if (playerInventory == null || gui == null)
            {
                reason = "本机玩家没有背包 / GUI（还不能打开容器面板）";
                return false;
            }
            if (!TryEnsureReplicaInventory(state.Key, out ComponentInventoryBase inventory,
                    out reason))
            {
                return false;
            }
            Widget widget;
            switch (state.ContainerKind)
            {
                case ContainerKinds.CraftingTable:
                    if (!(inventory is ComponentCraftingTable crafting))
                    {
                        reason = "副本实体不是 ComponentCraftingTable（实际 " +
                            inventory.GetType().Name + "）";
                        return false;
                    }
                    widget = new CraftingTableWidget(playerInventory, crafting);
                    break;
                case ContainerKinds.Chest:
                    if (!(inventory is ComponentChest chest))
                    {
                        reason = "副本实体不是 ComponentChest（实际 " +
                            inventory.GetType().Name + "）";
                        return false;
                    }
                    widget = new ChestWidget(playerInventory, chest);
                    break;
                case ContainerKinds.Furnace:
                    if (!(inventory is ComponentFurnace furnace))
                    {
                        reason = "副本实体不是 ComponentFurnace（实际 " +
                            inventory.GetType().Name + "）";
                        return false;
                    }
                    widget = new FurnaceWidget(playerInventory, furnace);
                    break;
                case ContainerKinds.Dispenser:
                    if (!(inventory is ComponentDispenser dispenser))
                    {
                        reason = "副本实体不是 ComponentDispenser（实际 " +
                            inventory.GetType().Name + "）";
                        return false;
                    }
                    widget = new DispenserWidget(playerInventory, dispenser);
                    break;
                default:
                    reason = "容器种类非法：" + state.ContainerKind;
                    return false;
            }
            // 先把权威槽位写进副本，再挂面板：第一帧显示的就是主机库存。
            if (!ApplyStateToInventory(inventory, state, out reason)) return false;
            gui.ModalPanelWidget = widget;
            m_viewKey = state.Key;
            m_viewWidget = widget;
            m_viewInventory = inventory;
            m_viewKind = state.ContainerKind;
            m_clientViewsOpened++;
            Log("客户端打开容器面板 " + state.Describe() + "（" +
                ContainerKinds.Name(state.ContainerKind) + "）· 内容来自主机权威库存");
            return true;
        }

        public void ApplyContainerStateView(ContainerStateData state)
        {
            if (state == null || m_viewInventory == null)
            {
                m_clientStateApplyFailures++;
                return;
            }
            if (!m_viewKey.HasValue || !m_viewKey.Value.Equals(state.Key) ||
                state.ContainerKind != m_viewKind)
            {
                m_clientStateApplyFailures++;
                LogProblem("state-view-key", "ContainerState 与当前面板种类/坐标不符（" +
                    state.Describe() + "）→ 不应用");
                return;
            }
            if (!ApplyStateToInventory(m_viewInventory, state, out string reason))
            {
                m_clientStateApplyFailures++;
                LogProblem("state-apply:" + reason, "应用容器权威状态失败（" + reason + "）→ " +
                    state.Describe());
                return;
            }
            m_clientStatesApplied++;
        }

        public void CloseContainerView(string reason)
        {
            if (m_viewWidget != null && m_project != null &&
                m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _))
            {
                ComponentGui gui = local?.ComponentPlayer?.ComponentGui;
                if (gui != null && ReferenceEquals(gui.ModalPanelWidget, m_viewWidget))
                {
                    gui.ModalPanelWidget = null;
                    m_clientViewsClosed++;
                }
            }
            m_viewKey = null;
            m_viewWidget = null;
            m_viewInventory = null;
            m_viewKind = ContainerKinds.None;
        }

        // ================================================================= 客户端：熔炉炉况

        /// <summary>
        /// 客户端把权威炉况变成**表现**：
        ///   ① 燃烧外观：把格子的 contents 落成 64/65（data 保朝向），走
        ///      <see cref="TerrainBridge.ApplyCells"/>（SetCellValueFast，不进引擎改动表，
        ///      不会变成一次本地编辑）；
        ///   ② 副本组件：把 heat / smelting progress / fire time 写进
        ///      <c>ComponentFurnace</c> 的私有字段 —— 面板的火焰（HeatLevel &gt; 0）与进度条
        ///      （SmeltingProgress）随下一帧 Update 显示，与主机完全一致；
        ///   ③ 世界火焰（presentation-only）：heat/fireTime 交给
        ///      <see cref="FurnaceReplicaPresentation"/> 建 / 收本地 FireParticleSystem ——
        ///      面板关着也可见，重连时主机重发炉况会重建。
        /// 客户端摘掉了副本实体的 IUpdateable，所以这些字段**不会**驱动本地烧炼 / 燃料消耗。
        /// </summary>
        public bool ApplyFurnacePresentation(FurnaceStateData state, out bool litApplied,
            out bool progressApplied, out string reason)
        {
            litApplied = false;
            progressApplied = false;
            reason = null;
            if (state == null)
            {
                reason = "state-null";
                return false;
            }
            if (!m_clientReplica)
            {
                m_clientFurnaceApplyFailures++;
                reason = "not-client";
                return false;
            }
            if (m_project == null)
            {
                m_clientFurnaceApplyFailures++;
                reason = "no-project";
                return false;
            }
            SubsystemTerrain terrain = m_project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
            {
                m_clientFurnaceApplyFailures++;
                reason = "no-terrain";
                return false;
            }
            string litReason = null;
            litApplied = TryApplyFurnaceLitCell(terrain, state, out litReason);
            if (litApplied) m_clientFurnaceLitWrites++;
            string componentReason = null;
            progressApplied = TryApplyFurnaceComponent(state, out componentReason);
            if (progressApplied)
            {
                m_clientFurnaceProgressWrites++;
            }
            else if (componentReason == "no-replica-entity" ||
                componentReason == "not-a-furnace-replica")
            {
                m_clientFurnaceComponentMissing++;
                LogThrottled("furnace-component-missing:" + state.Key.Describe(),
                    "客户端还没有熔炉副本组件 @" + state.Key.Describe() + "（" + componentReason +
                    "）→ 热量/进度暂存到面板打开时重放；燃烧外观照常落地（" +
                    state.Describe() + "）");
            }
            // ③ 世界火焰：由权威炉况直接驱动（heat > 0 / fireTime > 0）。
            //    与行为通知共用同一个表现层状态机 → 同一格重复状态只会 Keep，不会出现两个粒子。
            FurnaceReplicaPresentation flamePresenter =
                (terrain as SuSubsystemTerrain)?.Bridge?.FurnacePresenter;
            if (flamePresenter == null)
            {
                m_clientFurnaceFlamePresenterMissing++;
                LogProblem("furnace-flame-presenter:" + state.Key.Describe(),
                    "客户端没有挂接熔炉火焰表现层（TerrainBridge.FurnacePresenter == null）@" +
                    state.Key.Describe() + " → 面板状态照常应用，但世界里不会有火焰（累计 " +
                    m_clientFurnaceFlamePresenterMissing + " 次）");
            }
            else if (!flamePresenter.Present(state, out string flameReason))
            {
                m_clientFurnaceFlameFailures++;
                LogProblem("furnace-flame-present:" + state.Key.Describe(),
                    "熔炉火焰表现应用失败（" + flameReason + "）→ " + state.Describe() +
                    "（累计 " + m_clientFurnaceFlameFailures + " 次）");
            }
            if (!litApplied || !progressApplied)
            {
                reason = (litApplied ? string.Empty : "lit:" + (litReason ?? "failed")) +
                    (!litApplied && !progressApplied ? "; " : string.Empty) +
                    (progressApplied ? string.Empty : "component:" +
                        (componentReason ?? "failed"));
            }
            if (!litApplied && !progressApplied)
            {
                m_clientFurnaceApplyFailures++;
                return false;
            }
            return true;
        }

        private bool TryApplyFurnaceLitCell(SubsystemTerrain terrain, FurnaceStateData state,
            out string reason)
        {
            reason = null;
            if (!terrain.Terrain.IsCellValid(state.X, state.Y, state.Z))
            {
                reason = "cell-invalid";
                return false;
            }
            TerrainChunk chunk = terrain.Terrain.GetChunkAtCell(state.X, state.Z);
            if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
            {
                reason = "chunk-not-loaded";
                return false;
            }
            int current = terrain.Terrain.GetCellValue(state.X, state.Y, state.Z);
            if (Game.Terrain.ExtractContents(current) == state.Contents &&
                Game.Terrain.ExtractData(current) == state.Data)
            {
                return true;   // 已经是主机权威外观：幂等确认，不重复写
            }
            if (KindForContents(Game.Terrain.ExtractContents(current)) != ContainerKinds.Furnace)
            {
                reason = "block-changed: contents=" + Game.Terrain.ExtractContents(current);
                return false;
            }
            TerrainBridge bridge = (terrain as SuSubsystemTerrain)?.Bridge;
            if (bridge == null)
            {
                reason = "no-terrain-bridge";
                return false;
            }
            var key = new ChunkKey(state.X >> 4, state.Z >> 4);
            bridge.ApplyCells(key, new[]
            {
                new CellEdit(state.X & 15, state.Y, state.Z & 15, state.Contents, state.Data)
            });
            return true;
        }

        private bool TryApplyFurnaceComponent(FurnaceStateData state, out string reason)
        {
            reason = null;
            SubsystemBlockEntities entities =
                m_project.FindSubsystem<SubsystemBlockEntities>(false);
            ComponentBlockEntity blockEntity =
                entities?.GetBlockEntity(state.X, state.Y, state.Z);
            if (blockEntity?.Entity == null)
            {
                reason = "no-replica-entity";
                return false;
            }
            ComponentFurnace furnace = blockEntity.Entity.FindComponent<ComponentFurnace>();
            if (furnace == null)
            {
                reason = "not-a-furnace-replica";
                return false;
            }
            // 副本实体不许跑引擎 Update（否则客户端会自己烧炼 / 改格子）；这里再确认一次。
            PrepareReplicaEntity(blockEntity.Entity);
            try
            {
                m_parentField.ModifyParentField(furnace, "m_heatLevel", state.HeatLevel,
                    typeof(ComponentFurnace));
                m_parentField.ModifyParentField(furnace, "m_smeltingProgress",
                    state.SmeltingProgress, typeof(ComponentFurnace));
                m_parentField.ModifyParentField(furnace, "m_fireTimeRemaining",
                    state.FireTimeRemaining, typeof(ComponentFurnace));
                return true;
            }
            catch (Exception e)
            {
                reason = "field-write-failed: " + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        public void RestorePlayerInventory(PlayerInventorySnapshot snapshot, string reason)
        {
            if (snapshot == null || m_project == null) return;
            if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out string lookup))
            {
                LogProblem("restore-player-lookup", "回滚玩家背包时找不到本机玩家（" + lookup +
                    "）→ 背包保持当前显示（明确记录）");
                return;
            }
            IInventory inventory = local?.ComponentPlayer?.ComponentMiner?.Inventory;
            if (inventory == null) return;
            int slots = Math.Min(inventory.SlotsCount,
                Math.Min(snapshot.Values.Length, snapshot.Counts.Length));
            int changed = 0;
            for (int i = 0; i < slots; i++)
            {
                if (inventory.GetSlotValue(i) == snapshot.Values[i] &&
                    inventory.GetSlotCount(i) == snapshot.Counts[i])
                {
                    continue;
                }
                if (SetInventorySlot(inventory, i, snapshot.Values[i], snapshot.Counts[i]))
                    changed++;
            }
            if (changed > 0)
            {
                Log("容器编辑回滚：玩家背包恢复 " + changed + " 槽（" + reason +
                    "；随既有 PlayerInventory 同步上报主机）");
            }
        }

        // ================================================================= 主机：执行使用

        public bool TryExecuteUse(int clientId, in BlockUseRequestData request,
            out BlockUseHostOutcome outcome, out string reason)
        {
            outcome = null;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null)
            {
                reason = "没有加载中的 Project";
                return false;
            }
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer?.ComponentBody == null)
            {
                reason = "clientId=" + clientId + " 没有联机角色（未加入 / 已离开）";
                m_hostUseRejected++;
                return false;
            }
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentMiner miner = player.ComponentMiner;
            if (miner == null)
            {
                reason = "clientId=" + clientId + " 的角色没有 ComponentMiner";
                m_hostUseRejected++;
                return false;
            }
            SubsystemTerrain terrain = m_project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
            {
                reason = "主机地形子系统不可用";
                m_hostUseRejected++;
                return false;
            }
            int x = request.X, y = request.Y, z = request.Z;
            if (!terrain.Terrain.IsCellValid(x, y, z))
            {
                reason = "坐标越界";
                m_hostUseRejected++;
                return false;
            }
            // IsCellValid 只校验 y；x/z 必须先确认块已加载，否则 GetCellValueFast 可能空引用
            // （恶意/过期请求不能把主机打崩）。
            if (terrain.Terrain.GetChunkAtCell(x, z) == null)
            {
                reason = "目标区块未加载";
                m_hostUseRejected++;
                return false;
            }
            Vector3 center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            float measured = MathUtils.Sqrt(Vector3.DistanceSquared(
                player.ComponentBody.Position, center));
            if (!m_reachGate.TryCheckDistance(request.HasReach, request.Reach,
                    EngineReach.IsCreative(m_project), measured, out string reachReason))
            {
                reason = reachReason;
                m_hostUseRejected++;
                return false;
            }
            int cellValue = terrain.Terrain.GetCellValue(x, y, z);
            int contents = Game.Terrain.ExtractContents(cellValue);
            if (contents != request.Contents)
            {
                reason = "方块已变化（请求 contents=" + request.Contents + "，主机=" + contents +
                    "）";
                m_hostUseRejected++;
                return false;
            }
            if (contents <= 0 || contents >= BlocksManager.Blocks.Length)
            {
                reason = "contents=" + contents + " 不是有效方块";
                m_hostUseRejected++;
                return false;
            }
            Block block = BlocksManager.Blocks[contents];
            if (block == null || !block.IsInteractive(terrain, cellValue))
            {
                reason = "contents=" + contents + " 不是可交互方块";
                m_hostUseRejected++;
                return false;
            }

            int containerKind = KindForContents(contents);
            if (containerKind == ContainerKinds.Dispenser)
            {
                SubsystemGameInfo gameInfo = m_project.FindSubsystem<SubsystemGameInfo>(false);
                if (gameInfo != null && gameInfo.WorldSettings.GameMode == GameMode.Adventure)
                {
                    reason = "分配器在冒险模式下不可打开（引擎 SubsystemDispenserBlockBehavior 同口径）";
                    m_hostUseRejected++;
                    return false;
                }
            }
            if (containerKind != ContainerKinds.None)
            {
                if (!TryGetOrCreateHostInventory(x, y, z, containerKind,
                        out ComponentInventoryBase inventory, out reason))
                {
                    m_hostUseRejected++;
                    return false;
                }
                var key = new ContainerKey(x, y, z, containerKind);
                ContainerStateData state = CaptureState(key, inventory, true, out _);
                m_hostUseAccepted++;
                m_hostContainerOpens++;
                outcome = new BlockUseHostOutcome(BlockUseTargetKind.Container, containerKind,
                    state);
                return true;
            }

            // 原生交互：门 / 陷阱门 / 栅栏门 / 家具多状态 / 按钮开关 / ... —— 用远端角色的
            // ComponentMiner 调引擎自己的方块行为；地形结果走既有 CellChange 广播。
            SubsystemBlockBehaviors behaviors =
                m_project.FindSubsystem<SubsystemBlockBehaviors>(false);
            if (behaviors == null)
            {
                reason = "主机没有 SubsystemBlockBehaviors";
                m_hostUseRejected++;
                return false;
            }
            var raycast = BuildRaycastResult(in request, cellValue, x, y, z, center);
            bool handled = false;
            SubsystemBlockBehavior[] list = behaviors.GetBlockBehaviors(contents);
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    if (!list[i].OnInteract(raycast, miner)) continue;
                    handled = true;
                    break;
                }
                catch (Exception e)
                {
                    reason = "方块行为抛异常：" + list[i].GetType().Name + " " + e.GetType().Name +
                        " " + e.Message;
                    m_hostUseRejected++;
                    LogProblem("native-exception:" + list[i].GetType().Name, reason);
                    return false;
                }
            }
            if (!handled)
            {
                reason = "没有方块行为处理该交互（contents=" + contents + "）";
                m_hostUseRejected++;
                return false;
            }
            try
            {
                miner.Poke(false);
            }
            catch (Exception e)
            {
                LogProblem("native-poke", "主机交互后补播抬手动画失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 交互本身已生效，只少一次手臂动画");
            }
            m_hostUseAccepted++;
            m_hostNativeInteractions++;
            outcome = BlockUseHostOutcome.Native;
            return true;
        }

        private static TerrainRaycastResult BuildRaycastResult(in BlockUseRequestData request,
            int cellValue, int x, int y, int z, Vector3 center)
        {
            Vector3 position = new Vector3(request.RayX, request.RayY, request.RayZ);
            Vector3 direction = new Vector3(request.DirectionX, request.DirectionY,
                request.DirectionZ);
            if (direction.LengthSquared() < 1e-6f) direction = Vector3.Normalize(center - position);
            if (direction.LengthSquared() < 1e-6f) direction = new Vector3(0f, 0f, 1f);
            direction = Vector3.Normalize(direction);
            float distance = Vector3.Distance(position, center);
            return new TerrainRaycastResult
            {
                Ray = new Ray3(position, direction),
                Value = cellValue,
                CellFace = new CellFace(x, y, z, request.Face),
                CollisionBoxIndex = 0,
                Distance = MathUtils.Max(distance, 0.001f)
            };
        }

        // ================================================================= 主机：容器编辑

        /// <summary>
        /// 主机落地一次容器编辑。**先校验后落地**：base 必须等于主机权威快照；
        /// 普通槽的净增物品必须由玩家背包的净减覆盖（按种类守恒）；工作台的 result / remains
        /// 由主机推导 —— 放料时客户端预测的产物只当参考（主机 UpdateCraftingResult 重算），
        /// 取产物时主机 RemoveSlotItems(result, n) 原子消费配方格。
        /// 玩家背包在主机侧的实时镜像范围按 <see cref="PlayerInventoryMath.SyncSlotsFor"/> 决定：
        /// 普通背包（26 槽）全部槽位；创造仓库仍只有前 <see cref="PlayerInventoryMath.MaxSyncSlots"/> 槽。
        /// 台账只动这段实时镜像，背包部分由客户端权威上报。
        ///
        /// 请求者背包是创造仓库（<c>ComponentCreativeInventory</c>）时：跳过物品守恒，改按
        /// <see cref="ContainerEditPolicy.TryBuildPlan"/> 的创造规则校验（物品值必须由创造仓库
        /// 提供或来自容器原有存货；槽容量 / base / 打开者 / 距离 / 方块未变照旧），也见
        /// <see cref="ContainerEditRequester"/>。
        /// </summary>
        public bool TryApplyContainerEdit(int clientId, in ContainerEditRequestData request,
            out ContainerEditOutcome outcome, out string reason)
        {
            outcome = null;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host: 客户端副本不能落地容器编辑";
                return false;
            }
            if (m_project == null)
            {
                reason = "no-project: 没有加载中的 Project";
                return false;
            }
            if (!TryGetHostUseTarget(clientId, request.X, request.Y, request.Z,
                    request.ContainerKind, request.HasReach, request.Reach,
                    out ComponentPlayer player, out IInventory playerInventory,
                    out string targetReason))
            {
                reason = targetReason;
                m_hostEditsRejected++;
                return false;
            }
            // 请求者背包类型就是主机权威的"是不是创造模式"判据：创造模式角色的
            // ComponentMiner.Inventory 是 ComponentCreativeInventory（普通背包是 ComponentInventory）。
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Load（GameMode.Creative → 创造仓库）
            ComponentCreativeInventory creativeInventory =
                playerInventory as ComponentCreativeInventory;
            var requester = creativeInventory != null
                ? new ContainerEditRequester(true, CaptureCreativeOfferedValues(creativeInventory),
                    request.PlayerBaseValues, request.PlayerBaseCounts)
                : ContainerEditRequester.Survival;
            if (requester.CreativeInventory)
            {
                m_hostCreativeInventoryDetected++;
                LogThrottled("creative-inventory:" + clientId,
                    "clientId=" + clientId + " 的容器编辑请求者使用创造仓库（" +
                    "ComponentCreativeInventory）→ 本次编辑跳过" +
                    "\"容器增加 ⇒ 玩家背包减少\"的物品守恒（创造仓库 GetSlotCount 恒定 9999、" +
                    "RemoveSlotItems 不扣减可复制物品，玩家侧快照本来就不反映进出）；" +
                    "改按创造模式规则校验：物品值必须由创造仓库提供或来自容器原有存货、" +
                    "槽容量/堆叠、base/revision、打开者、距离、方块未变。累计检测 " +
                    m_hostCreativeInventoryDetected + " 次 / 已允许 " +
                    m_hostCreativeTransferAllowed + " 次");
            }
            if (!TryGetHostInventory(request.Key, out ComponentInventoryBase inventory,
                    out string lookupReason))
            {
                reason = "container-missing: " + lookupReason;
                m_hostEditsRejected++;
                return false;
            }
            ContainerStateData current = CaptureState(request.Key, inventory, false, out _);
            if (request.BaseRevision != current.Revision)
            {
                reason = "stale-base: 请求 base rev=" + request.BaseRevision + "，主机 rev=" +
                    current.Revision + "（客户端 base 过期，需要回滚后按权威状态重试）";
                m_hostEditsRejected++;
                return false;
            }
            if (!current.SameValues(request.BaseValues, request.BaseCounts))
            {
                reason = "base-mismatch: base 槽位与主机权威值不一致（" + current.Describe() + "）";
                m_hostEditsRejected++;
                return false;
            }
            if (request.DesiredValues.Length != inventory.SlotsCount ||
                request.DesiredCounts.Length != inventory.SlotsCount)
            {
                reason = "desired-shape: desired 槽位数与库存不一致（" +
                    request.DesiredValues.Length + "/" + inventory.SlotsCount + "）";
                m_hostEditsRejected++;
                return false;
            }
            ComponentCraftingTable crafting = null;
            int resultSlot = -1;
            int remainsSlot = -1;
            CraftingRecipeRules? craftingRules = null;
            if (request.ContainerKind == ContainerKinds.CraftingTable)
            {
                crafting = inventory as ComponentCraftingTable;
                if (crafting == null)
                {
                    reason = "layout-invalid: 主机坐标 " + request.Key.Describe() +
                        " 的容器不是 ComponentCraftingTable（实际 " +
                        inventory.GetType().Name + "）";
                    m_hostEditsRejected++;
                    return false;
                }
                resultSlot = crafting.ResultSlotIndex;
                remainsSlot = crafting.RemainsSlotIndex;
                craftingRules = TryReadCraftingRules(crafting, resultSlot, remainsSlot);
                if (craftingRules == null && current.Counts[resultSlot] > 0)
                {
                    LogProblem("craft-rules-missing:" + request.Key.Describe(),
                        "主机工作台 " + request.Key.Describe() + " 的权威产物槽 " +
                        current.Counts[resultSlot] + "x" + current.Values[resultSlot] +
                        " 非空，但读不到匹配配方（m_matchedRecipe / m_matchedIngredients）→ " +
                        "本次只接受不取产物的编辑（取产物会因不守恒被明确拒绝）");
                }
            }
            if (!ValidateDesiredSlots(inventory, request.DesiredValues, request.DesiredCounts,
                    resultSlot, remainsSlot, out string slotReason))
            {
                reason = slotReason;
                m_hostEditsRejected++;
                return false;
            }
            if (!TryCaptureHostPlayerInventory(playerInventory, out int[] hostPlayerValues,
                    out int[] hostPlayerCounts, out string playerReason))
            {
                reason = "player-inventory: clientId=" + clientId + " 的背包读不出来（" +
                    playerReason + "）→ 不能原子校验/落地玩家侧变化";
                m_hostEditsRejected++;
                return false;
            }
            if (!ContainerEditPolicy.TryBuildPlan(inventory.SlotsCount, current.Values,
                    current.Counts, request.DesiredValues, request.DesiredCounts, resultSlot,
                    remainsSlot, craftingRules, request.PlayerBaseValues,
                    request.PlayerBaseCounts, request.PlayerDesiredValues,
                    request.PlayerDesiredCounts,
                    PlayerInventoryMath.SyncSlotsFor(playerInventory.SlotsCount),
                    in requester, out ContainerEditPlan plan, out reason))
            {
                m_hostEditsRejected++;
                return false;
            }

            // ---------------------------------------------------------------- 落地（容器）
            int removed = 0;
            if (plan.ResultTakeCount > 0)
            {
                // 取产物 = 主机 RemoveSlotItems(result, n) 的原子语义：按批消费配方格、
                // 填 remains、重算产物。客户端 desired 的 result 只当预测，绝不按它写。
                try
                {
                    removed = crafting.RemoveSlotItems(resultSlot, plan.ResultTakeCount);
                }
                catch (Exception e)
                {
                    RestoreCraftingSnapshot(crafting, current);
                    reason = "craft-take-failed: 主机 RemoveSlotItems 抛异常：" +
                        e.GetType().Name + " " + e.Message;
                    m_hostEditsRejected++;
                    return false;
                }
                if (removed != plan.ResultTakeCount)
                {
                    RestoreCraftingSnapshot(crafting, current);
                    reason = "craft-take-mismatch: 主机只移除了 " + removed + " 个合成产物（请求 " +
                        plan.ResultTakeCount + " 个，产物值 " + plan.ResultValue + "）";
                    m_hostEditsRejected++;
                    return false;
                }
                if (!TryCaptureSlots(inventory, out int[] postTakeValues,
                        out int[] postTakeCounts))
                {
                    RestoreCraftingSnapshot(crafting, current);
                    reason = "craft-grid-mismatch: 取产物后读不到主机容器槽位（" +
                        request.Key.Describe() + "）";
                    m_hostEditsRejected++;
                    return false;
                }
                // 主机**实际** replay 后的容器必须与客户端 desired 一致。
                //   生存：按物品种类守恒（容器 + 玩家背包，产物记到玩家名下）；
                //   创造：不要求守恒，只要求主机 replay 出的配方格 / remains 与客户端 desired 一致
                //         （判据不同：创造仓库的玩家侧快照不反映取出 / 放入）。
                bool replayMatches;
                string balanceReason;
                if (plan.CreativeInventory)
                {
                    replayMatches = ContainerEditPolicy.TryCheckCreativeTakeGrid(postTakeValues,
                        postTakeCounts, request.DesiredValues, request.DesiredCounts, resultSlot,
                        out balanceReason);
                }
                else
                {
                    replayMatches = ContainerEditPolicy.TryCheckConservation(postTakeValues,
                        postTakeCounts, request.DesiredValues, request.DesiredCounts, resultSlot,
                        request.PlayerBaseValues, request.PlayerBaseCounts,
                        request.PlayerDesiredValues, request.PlayerDesiredCounts,
                        plan.ResultValue, removed, checkCraftedClaim: false,
                        out balanceReason);
                }
                if (!replayMatches)
                {
                    RestoreCraftingSnapshot(crafting, current);
                    reason = "craft-grid-mismatch: 主机 replay 取产物后的容器与客户端 desired " +
                        "不一致 @" + request.Key.Describe() + "（" + balanceReason + "）";
                    m_hostEditsRejected++;
                    return false;
                }
            }
            bool craftGridApplied = crafting != null && plan.GridChanged;
            // 落地客户端 desired 的其余槽位（取产物后剩下的移动 / 拆分 / 放入容器）。
            // result 槽永远由主机 UpdateCraftingResult 重算，不接受客户端值。
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                if (i == resultSlot) continue;
                if (request.DesiredValues[i] == inventory.GetSlotValue(i) &&
                    request.DesiredCounts[i] == inventory.GetSlotCount(i))
                {
                    continue;
                }
                bool written = crafting != null
                    ? SetSlotDirect(crafting, i, request.DesiredValues[i],
                        request.DesiredCounts[i])
                    : SetInventorySlot(inventory, i, request.DesiredValues[i],
                        request.DesiredCounts[i]);
                if (!written)
                {
                    if (crafting != null) RestoreCraftingSnapshot(crafting, current);
                    else RestoreContainerSnapshot(inventory, current);
                    reason = "slot-capacity: 槽 " + i + " 无法写入（value=" +
                        request.DesiredValues[i] + " count=" + request.DesiredCounts[i] + "）";
                    m_hostEditsRejected++;
                    return false;
                }
            }
            if (crafting != null)
            {
                try
                {
                    m_parentMethod.InvokeParentMethod(crafting, "UpdateCraftingResult");
                }
                catch (Exception e)
                {
                    RestoreCraftingSnapshot(crafting, current);
                    reason = "craft-recompute: 重算合成结果失败：" + e.GetType().Name + " " +
                        e.Message;
                    m_hostEditsRejected++;
                    return false;
                }
            }

            // 客户端本地预测的产物只当参考：主机重算后若不同，以主机为准（下发的权威状态会纠正）。
            bool predictionMismatch = false;
            if (crafting != null)
            {
                int hostResultValue = crafting.GetSlotValue(resultSlot);
                int hostResultCount = crafting.GetSlotCount(resultSlot);
                predictionMismatch = hostResultValue != request.DesiredValues[resultSlot] ||
                    hostResultCount != request.DesiredCounts[resultSlot];
                if (predictionMismatch)
                {
                    m_hostCraftPredictionMismatch++;
                    LogProblem("craft-predict:" + request.Key.Describe(),
                        "客户端预测的合成产物与主机重算不一致 @" + request.Key.Describe() +
                        "（客户端 " + request.DesiredValues[resultSlot] + "x" +
                        request.DesiredCounts[resultSlot] + "，主机 " + hostResultValue + "x" +
                        hostResultCount + "）→ 以主机为准并下发权威状态");
                }
            }

            // ---------------------------------------------------------------- 落地（玩家实时镜像背包）
            // 创造请求者**不写**玩家实时镜像：创造仓库的 GetSlotCount 恒 9999、RemoveSlotItems 对可复制
            // 物品不扣减（清空一个可复制槽会变成空操作），写镜像既无守恒意义又会和客户端权威的
            // PlayerInventory 同步打架。背包由客户端权威上报（与既有分工一致）。
            int playerRemoved = 0;
            int playerAdded = 0;
            int playerLeftover = 0;
            if (plan.CreativeInventory)
            {
                m_hostCreativePlayerMirrorSkipped++;
            }
            else
            {
                ApplyPlayerMirror(playerInventory, in plan, clientId, hostPlayerValues,
                    hostPlayerCounts, request.PlayerBaseValues, request.PlayerBaseCounts,
                    out playerRemoved, out playerAdded, out playerLeftover);
            }

            ContainerStateData state = CaptureState(request.Key, inventory, true, out _);
            m_hostEditsApplied++;
            if (craftGridApplied) m_hostCraftGridApplied++;
            if (removed > 0) m_hostResultTaken += removed;
            if (plan.CreativeInventory)
            {
                // 创造模式的"守恒被创造规则替代"是**允许**而不是降级：计数器 + 每一条落地日志都写明。
                m_hostCreativeTransferAllowed++;
                m_hostCreativeDataVariantAllowed += plan.CreativeDataVariantAllowed;
                m_hostCreativeInventoryMoveAllowed += plan.CreativeInventoryMoveAllowed;
                if (plan.CreativeDataVariantAllowed > 0 ||
                    plan.CreativeInventoryMoveAllowed > 0)
                {
                    LogThrottled("creative-variant:" + clientId,
                        "creative-data-variant-allowed: 创造模式放行 clientId=" + clientId +
                        " @" + request.Key.Describe() +
                        "：data 变体 " + plan.CreativeDataVariantAllowed + " 件（contents 由创造仓库" +
                        "提供：地图 / 告示牌 / 家具设计等）+ 请求者背包已有物品移动 " +
                        plan.CreativeInventoryMoveAllowed + " 件（累计 dataVariant=" +
                        m_hostCreativeDataVariantAllowed + " inventoryMove=" +
                        m_hostCreativeInventoryMoveAllowed + "）");
                }
            }
            outcome = new ContainerEditOutcome(state, craftGridApplied, removed,
                plan.ResultValue, predictionMismatch, playerRemoved, playerAdded,
                playerLeftover, plan.SplitApplied, plan.MoveApplied,
                plan.TakeAndPlaceApplied, plan.CreativeInventory,
                plan.CreativeDataVariantAllowed, plan.CreativeInventoryMoveAllowed);
            Log("主机落地 clientId=" + clientId + " 的容器编辑 " + request.Describe() + " → " +
                state.Describe() + " · " + outcome.Describe() +
                (plan.CreativeInventory
                    ? " · creativeTransferAllowed=" + m_hostCreativeTransferAllowed +
                      "（创造模式：跳过物品守恒；物品值已按创造仓库可提供 / 容器原有存货校验）"
                    : string.Empty));
            return true;
        }

        /// <summary>
        /// 主机校验一次容器编辑的"人 + 距离 + 方块"上下文：
        /// 请求者必须是已加入的联机角色、在 min(reach + 容差, 主机上限) 内、
        /// 目标格仍是同一种容器方块且区块已加载。
        /// </summary>
        private bool TryGetHostUseTarget(int clientId, int x, int y, int z, int kind,
            bool hasReach, float reach,
            out ComponentPlayer player, out IInventory playerInventory, out string reason)
        {
            player = null;
            playerInventory = null;
            reason = null;
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer?.ComponentBody == null)
            {
                reason = "requester-missing: clientId=" + clientId +
                    " 没有联机角色（未加入 / 已离开）";
                return false;
            }
            player = playerData.ComponentPlayer;
            playerInventory = player.ComponentMiner?.Inventory;
            if (playerInventory == null)
            {
                reason = "requester-inventory: clientId=" + clientId +
                    " 的角色没有 ComponentMiner.Inventory";
                return false;
            }
            SubsystemTerrain terrain = m_project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
            {
                reason = "no-terrain: 主机地形子系统不可用";
                return false;
            }
            if (!terrain.Terrain.IsCellValid(x, y, z))
            {
                reason = "block-changed: 坐标越界 " + x + "," + y + "," + z;
                return false;
            }
            if (terrain.Terrain.GetChunkAtCell(x, z) == null)
            {
                reason = "block-changed: 目标区块未加载 " + x + "," + y + "," + z;
                return false;
            }
            Vector3 center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            float measured = MathUtils.Sqrt(Vector3.DistanceSquared(
                player.ComponentBody.Position, center));
            if (!m_reachGate.TryCheckDistance(hasReach, reach,
                    EngineReach.IsCreative(m_project), measured, out string reachReason))
            {
                reason = reachReason;
                return false;
            }
            int contents = Game.Terrain.ExtractContents(terrain.Terrain.GetCellValue(x, y, z));
            if (KindForContents(contents) != kind)
            {
                reason = "block-changed: 主机方块 @" + x + "," + y + "," + z + " contents=" +
                    contents + " 不是 " + ContainerKinds.Name(kind) + "（请求 kind=" + kind + "）";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 抓创造仓库能提供的物品值（= 创造仓库组件自己的槽值）：前 OpenSlotsCount 槽是快捷栏
        /// 当前物品，其余是 <c>GetCreativeValues()</c> 生成的全部分类页物品 —— 后者就是"创造模式
        /// 允许凭空取用"的集合。主机用它校验创造请求者写进容器的物品值。
        /// Source: Survivalcraft/Game/ComponentCreativeInventory.cs:Load（m_slots = OpenSlotsCount 个
        ///         个人槽 + 每个方块的 GetCreativeValues()）
        /// </summary>
        private static int[] CaptureCreativeOfferedValues(ComponentCreativeInventory inventory)
        {
            if (inventory == null) return Array.Empty<int>();
            var values = new List<int>(inventory.SlotsCount);
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                int value = inventory.GetSlotValue(i);
                if (value != 0) values.Add(value);
            }
            return values.ToArray();
        }

        private static bool TryCaptureHostPlayerInventory(IInventory inventory,
            out int[] values, out int[] counts, out string reason)
        {
            values = null;
            counts = null;
            reason = null;
            if (inventory == null)
            {
                reason = "背包为空";
                return false;
            }
            int slots = inventory.SlotsCount;
            if (slots <= 0 || slots > ContainerStateData.MaxSlots)
            {
                reason = "背包槽位数非法：" + slots;
                return false;
            }
            values = new int[slots];
            counts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                values[i] = inventory.GetSlotValue(i);
                counts[i] = inventory.GetSlotCount(i);
            }
            return true;
        }

        /// <summary>抓容器槽位（取产物后做"实际 replay vs 客户端 desired"守恒校验）。</summary>
        private static bool TryCaptureSlots(ComponentInventoryBase inventory,
            out int[] values, out int[] counts)
        {
            values = null;
            counts = null;
            if (inventory == null) return false;
            int slots = inventory.SlotsCount;
            if (slots <= 0 || slots > ContainerStateData.MaxSlots) return false;
            values = new int[slots];
            counts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                values[i] = inventory.GetSlotValue(i);
                counts[i] = inventory.GetSlotCount(i);
            }
            return true;
        }

        /// <summary>
        /// 读主机工作台的匹配配方（m_matchedRecipe / m_matchedIngredients），转成纯值规则。
        /// 读不到 / 字段布局不符 → 返回 null，调用点按"不能取产物"明确记录（不猜配方）。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerContainerHandlers.cs:
        ///         TryGetCraftingResultTransfer（同样从这两个私有字段读 host 权威配方）
        /// </summary>
        private CraftingRecipeRules? TryReadCraftingRules(ComponentCraftingTable crafting,
            int resultSlot, int remainsSlot)
        {
            if (crafting == null) return null;
            try
            {
                CraftingRecipe recipe = m_parentField.GetParentField<CraftingRecipe>(crafting,
                    "m_matchedRecipe", typeof(ComponentCraftingTable));
                string[] matched = m_parentField.GetParentField<string[]>(crafting,
                    "m_matchedIngredients", typeof(ComponentCraftingTable));
                if (recipe == null || recipe.ResultValue == 0 || recipe.ResultCount <= 0 ||
                    matched == null || matched.Length < 9)
                {
                    return null;
                }
                if (!ContainerEditPolicy.TryGetCraftingLayout(crafting.SlotsCount,
                        out int gridSize, out int layoutResult, out int layoutRemains) ||
                    layoutResult != resultSlot || layoutRemains != remainsSlot)
                {
                    return null;
                }
                var matchedSlots = new List<int>(9);
                for (int y = 0; y < gridSize; y++)
                {
                    for (int x = 0; x < gridSize; x++)
                    {
                        int ingredientIndex = x + y * 3;
                        if (string.IsNullOrEmpty(matched[ingredientIndex])) continue;
                        matchedSlots.Add(x + y * gridSize);
                    }
                }
                if (matchedSlots.Count == 0) return null;
                return new CraftingRecipeRules(recipe.ResultValue, recipe.ResultCount,
                    recipe.RemainsValue, recipe.RemainsCount, matchedSlots.ToArray());
            }
            catch (Exception e)
            {
                LogProblem("craft-rules-read:" + crafting.GetType().Name,
                    "读取工作台匹配配方失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 本次编辑不能取产物（明确拒绝而不是猜配方）");
                return null;
            }
        }

        /// <summary>
        /// 把计划里的玩家背包**实时镜像区**（<see cref="PlayerInventoryMath.SyncSlotsFor"/>：
        /// 普通背包 26 槽全量 / 创造仓库前 <see cref="PlayerInventoryMath.MaxSyncSlots"/> 槽）
        /// 写成客户端 desired。只有当主机实时镜像等于客户端 base 或 desired 时才写
        /// （说明客户端快照没有超过镜像）；否则明确计数 + 限流日志，容器编辑照常落地，
        /// 玩家背包由客户端权威上报对账。
        /// </summary>
        private void ApplyPlayerMirror(IInventory inventory, in ContainerEditPlan plan,
            int clientId, int[] hostValues, int[] hostCounts, int[] playerBaseValues,
            int[] playerBaseCounts, out int removed, out int added, out int leftover)
        {
            removed = 0;
            added = 0;
            leftover = 0;
            if (inventory == null || plan?.LivePlayerValues == null) return;
            int live = Math.Min(plan.LivePlayerValues.Length, inventory.SlotsCount);
            live = Math.Min(live, PlayerInventoryMath.SyncSlotsFor(inventory.SlotsCount));
            if (live <= 0) return;
            bool matchesBase = LiveRangeEquals(hostValues, hostCounts, playerBaseValues,
                playerBaseCounts, live);
            bool matchesDesired = LiveRangeEquals(hostValues, hostCounts,
                plan.LivePlayerValues, plan.LivePlayerCounts, live);
            if (!matchesBase && !matchesDesired)
            {
                m_hostPlayerMirrorDiverged++;
                LogProblem("player-mirror-diverged:" + clientId,
                    "主机实时镜像背包既不等于 clientId=" + clientId +
                    " 的 base 也不等于 desired → 本次不改玩家背包" +
                    "（容器编辑照常落地；背包由客户端权威上报对账）");
                return;
            }
            for (int i = 0; i < live; i++)
            {
                int wantCount = plan.LivePlayerCounts[i];
                int wantValue = wantCount > 0 ? plan.LivePlayerValues[i] : 0;
                int haveValue = inventory.GetSlotValue(i);
                int haveCount = inventory.GetSlotCount(i);
                if (haveValue == wantValue && haveCount == wantCount) continue;
                if (haveValue != wantValue)
                {
                    removed += haveCount;
                    added += wantCount;
                }
                else if (haveCount > wantCount) removed += haveCount - wantCount;
                else added += wantCount - haveCount;
                if (!SetInventorySlot(inventory, i, wantValue, wantCount))
                {
                    leftover++;
                    m_hostPlayerMirrorSlotFailures++;
                    LogProblem("player-mirror-slot:" + clientId + ":" + i,
                        "把 clientId=" + clientId + " 的实时镜像槽 " + i + "写成 " +
                        wantValue + "x" + wantCount + " 失败（容量）→ 计入 leftover，" +
                        "背包由客户端下一次上报对账");
                }
            }
            m_hostPlayerMirrorRemoved += removed;
            m_hostPlayerMirrorAdded += added;
            m_hostPlayerMirrorLeftover += leftover;
        }

        private static bool LiveRangeEquals(int[] leftValues, int[] leftCounts,
            int[] rightValues, int[] rightCounts, int live)
        {
            if (leftValues == null || leftCounts == null || rightValues == null ||
                rightCounts == null) return false;
            if (live > leftValues.Length || live > leftCounts.Length ||
                live > rightValues.Length || live > rightCounts.Length) return false;
            for (int i = 0; i < live; i++)
            {
                if (leftValues[i] != rightValues[i] || leftCounts[i] != rightCounts[i])
                    return false;
            }
            return true;
        }

        public int PollContainerStates(IReadOnlyList<ContainerKey> keys,
            List<ContainerStateData> states)
        {
            if (keys == null || states == null) return 0;
            int changed = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                ContainerKey key = keys[i];
                if (!TryGetHostInventory(key, out ComponentInventoryBase inventory, out _))
                    continue;
                ContainerStateData state = CaptureState(key, inventory, true, out bool didChange);
                m_hostStatesPolled++;
                if (!didChange) continue;
                states.Add(state);
                changed++;
            }
            m_hostStateBroadcasts += changed;
            return changed;
        }

        public bool TryCaptureContainerState(ContainerKey key, out ContainerStateData state,
            out string reason)
        {
            state = null;
            if (!TryGetHostInventory(key, out ComponentInventoryBase inventory, out reason))
                return false;
            state = CaptureState(key, inventory, false, out _);
            return true;
        }

        // ================================================================= 主机：熔炉炉况

        public int AddNearbyFurnaceTargets(List<FurnacePollTarget> targets, int maxTargets)
        {
            if (m_clientReplica || m_project == null || targets == null) return 0;
            if (maxTargets <= 0) maxTargets = 64;
            SubsystemBlockEntities entities =
                m_project.FindSubsystem<SubsystemBlockEntities>(false);
            if (entities == null)
            {
                m_hostFurnaceNearScanFailures++;
                LogProblem("furnace-near-entities",
                    "主机没有 SubsystemBlockEntities → 附近燃烧熔炉不能纳入炉况目标" +
                    "（被客户端打开的熔炉仍会正常下发）");
                return 0;
            }
            Dictionary<Point3, ComponentBlockEntity> table;
            try
            {
                // SubsystemBlockEntities 没有公开枚举（只有 GetBlockEntity）：读它的私有坐标表
                // 是**只读**发现路径；读不到就明确失败并计数，不猜世界里的熔炉位置。
                // Source: Survivalcraft/Game/SubsystemBlockEntities.cs:m_blockEntities
                table = m_parentField.GetParentField<Dictionary<Point3, ComponentBlockEntity>>(
                    entities, "m_blockEntities", typeof(SubsystemBlockEntities));
            }
            catch (Exception e)
            {
                m_hostFurnaceNearScanFailures++;
                LogProblem("furnace-near-read:" + e.GetType().Name,
                    "读取 SubsystemBlockEntities.m_blockEntities 失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 附近熔炉不纳入本拍炉况（被打开的熔炉仍会下发）");
                return 0;
            }
            if (table == null)
            {
                m_hostFurnaceNearScanFailures++;
                LogProblem("furnace-near-null",
                    "SubsystemBlockEntities.m_blockEntities 读出来是空 → 附近熔炉不纳入本拍炉况" +
                    "（被打开的熔炉仍会下发）");
                return 0;
            }
            m_furnaceClients.Clear();
            foreach (int clientId in m_spawner.ClientIds)
            {
                if (!m_spawner.TryGetAvatarCell(clientId, out Vector3 position, out _)) continue;
                m_furnaceClients.Add(new KeyValuePair<int, Vector3>(clientId, position));
            }
            if (m_furnaceClients.Count == 0) return 0;
            int skippedDistance = 0;
            foreach (KeyValuePair<Point3, ComponentBlockEntity> pair in table)
            {
                ComponentBlockEntity blockEntity = pair.Value;
                Entity entity = blockEntity?.Entity;
                if (entity == null) continue;
                ComponentFurnace furnace = entity.FindComponent<ComponentFurnace>();
                if (furnace == null) continue;
                Point3 coordinates = blockEntity.Coordinates;
                var key = new ContainerKey(coordinates.X, coordinates.Y, coordinates.Z,
                    ContainerKinds.Furnace);
                FurnacePollTarget target = FurnacePollTarget.Find(targets, key);
                bool opened = target != null;
                if (!opened)
                {
                    // 关闭且没在烧的熔炉：客户端默认值（heat=0 / progress=0 / contents=64）就是
                    // 正确表现，不需要流量。刚熄灭的那一拍仍要发（由"上一拍还在烧"判定保留）。
                    bool wasBurning = m_hostLastFurnaceStates.TryGetValue(key,
                        out FurnaceStateData last) && (last.HeatLevel > 0f || last.Lit);
                    if (furnace.HeatLevel <= 0f && !wasBurning) continue;
                }
                var center = new Vector3(coordinates.X + 0.5f, coordinates.Y + 0.5f,
                    coordinates.Z + 0.5f);
                bool anyNear = false;
                for (int i = 0; i < m_furnaceClients.Count; i++)
                {
                    KeyValuePair<int, Vector3> client = m_furnaceClients[i];
                    if (Vector3.DistanceSquared(client.Value, center) >
                        NearFurnaceDistanceSq)
                    {
                        continue;
                    }
                    anyNear = true;
                    if (target == null)
                    {
                        if (targets.Count >= maxTargets)
                        {
                            m_hostFurnaceTargetCapHits++;
                            LogProblem("furnace-target-cap",
                                "主机本拍熔炉轮询目标已达上限 " + maxTargets + " → " +
                                key.Describe() + " 的附近客户端本轮收不到炉况（累计 " +
                                m_hostFurnaceTargetCapHits + " 次；被打开的熔炉优先）");
                            break;
                        }
                        target = new FurnacePollTarget(key);
                        targets.Add(target);
                    }
                    target.AddClient(client.Key);
                }
                if (!anyNear && !opened) skippedDistance++;
            }
            return skippedDistance;
        }

        /// <summary>
        /// 主机抓一个熔炉的权威运行状态。热量 / 进度用公开属性；剩余燃烧时间读
        /// <c>m_fireTimeRemaining</c>（引擎没有公开访问器）。燃烧外观按引擎同一规则生成：
        /// <c>m_heatLevel &gt; 0f ? 65 : 64</c>（Source: ComponentFurnace.cs:161）。
        /// </summary>
        public bool TryCaptureFurnaceState(ContainerKey key, out FurnaceStateData state,
            out string reason)
        {
            state = null;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (key.Kind != ContainerKinds.Furnace)
            {
                reason = "not-furnace: kind=" + ContainerKinds.Name(key.Kind);
                return false;
            }
            if (!TryGetHostInventory(key, out ComponentInventoryBase inventory, out reason))
                return false;
            if (!(inventory is ComponentFurnace furnace))
            {
                reason = "furnace-component-missing: " + inventory.GetType().Name;
                return false;
            }
            SubsystemTerrain terrain = m_project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
            {
                reason = "no-terrain";
                return false;
            }
            if (!terrain.Terrain.IsCellValid(key.X, key.Y, key.Z))
            {
                reason = "cell-invalid";
                return false;
            }
            if (terrain.Terrain.GetChunkAtCell(key.X, key.Z) == null)
            {
                reason = "chunk-unloaded";
                return false;
            }
            int cellValue = terrain.Terrain.GetCellValue(key.X, key.Y, key.Z);
            int contents = Game.Terrain.ExtractContents(cellValue);
            if (KindForContents(contents) != ContainerKinds.Furnace)
            {
                reason = "block-changed: contents=" + contents;
                return false;
            }
            float heat = furnace.HeatLevel;
            float progress = furnace.SmeltingProgress;
            float fireTime;
            try
            {
                fireTime = m_parentField.GetParentField<float>(furnace,
                    "m_fireTimeRemaining", typeof(ComponentFurnace));
            }
            catch (Exception e)
            {
                reason = "fire-time-unreadable: " + e.GetType().Name + " " + e.Message;
                return false;
            }
            if (!IsFinite(heat) || heat < 0f || !IsFinite(progress) || progress < 0f ||
                progress > 1f || !IsFinite(fireTime) || fireTime < 0f)
            {
                reason = "field-not-finite: heat=" + heat + " progress=" + progress +
                    " fire=" + fireTime;
                return false;
            }
            int litContents = heat > 0f ? FurnaceStateData.LitContents
                : FurnaceStateData.UnlitContents;
            int authoritativeCell = Game.Terrain.ReplaceContents(cellValue, litContents);
            int revision = m_hostFurnaceRevisions.TryGetValue(key, out int known) ? known : 0;
            bool changed = !m_hostLastFurnaceStates.TryGetValue(key,
                out FurnaceStateData last) ||
                !last.SameValues(heat, progress, fireTime, authoritativeCell);
            if (revision <= 0) revision = 1;
            else if (changed) revision++;
            m_hostFurnaceRevisions[key] = revision;
            state = new FurnaceStateData(key.X, key.Y, key.Z, revision, heat, progress,
                fireTime, authoritativeCell);
            m_hostLastFurnaceStates[key] = state;
            m_hostFurnaceStateCaptures++;
            if (m_hostLastFurnaceStates.Count > MaxTrackedFurnaceStates)
            {
                m_hostLastFurnaceStates.Clear();
                m_hostFurnaceRevisions.Clear();
                Log("主机熔炉炉况快照超过 " + MaxTrackedFurnaceStates +
                    " 项 → 清空（下一拍按当前状态重新下发一次，不丢内容）");
            }
            return true;
        }

        // ================================================================= 主机：方块实体库存

        private bool TryGetHostInventory(ContainerKey key, out ComponentInventoryBase inventory,
            out string reason)
        {
            inventory = null;
            reason = null;
            if (m_project == null)
            {
                reason = "没有加载中的 Project";
                return false;
            }
            SubsystemBlockEntities entities =
                m_project.FindSubsystem<SubsystemBlockEntities>(false);
            ComponentBlockEntity blockEntity = entities?.GetBlockEntity(key.X, key.Y, key.Z);
            if (blockEntity == null)
            {
                reason = "方块实体不存在 @" + key.Describe();
                return false;
            }
            inventory = FindInventory(blockEntity.Entity, key.Kind);
            if (inventory == null)
            {
                reason = "方块实体没有 " + ContainerKinds.Name(key.Kind) + " 库存组件";
                return false;
            }
            return true;
        }

        private bool TryGetOrCreateHostInventory(int x, int y, int z, int kind,
            out ComponentInventoryBase inventory, out string reason)
        {
            inventory = null;
            reason = null;
            SubsystemBlockEntities entities =
                m_project.FindSubsystem<SubsystemBlockEntities>(false);
            if (entities == null)
            {
                reason = "主机没有 SubsystemBlockEntities";
                return false;
            }
            ComponentBlockEntity blockEntity = entities.GetBlockEntity(x, y, z);
            if (blockEntity == null)
            {
                // 放置路径修复：客户端放置的工作台由主机 OnBlockAdded 建实体；若那一步没跑到
                // （例如世界导入 / 直接写格），这里按同一条数据库路径补建权威库存。
                Entity created = CreateBlockEntity(m_project, kind, x, y, z, persist: true,
                    out reason);
                if (created == null) return false;
                m_hostCreatedEntities.Add(created);
                m_hostBlockEntitiesCreated++;
                blockEntity = created.FindComponent<ComponentBlockEntity>();
                Log("主机为坐标 " + x + "," + y + "," + z + " 补建方块实体库存（" +
                    ContainerKinds.Name(kind) + "）；累计 " + m_hostBlockEntitiesCreated);
            }
            inventory = FindInventory(blockEntity.Entity, kind);
            if (inventory == null)
            {
                reason = "坐标 " + x + "," + y + "," + z + " 的方块实体不是 " +
                    ContainerKinds.Name(kind) + " 库存";
                m_hostKindMismatches++;
                return false;
            }
            return true;
        }

        // ================================================================= 客户端：副本实体

        private bool TryEnsureReplicaInventory(ContainerKey key, out ComponentInventoryBase inventory,
            out string reason)
        {
            inventory = null;
            reason = null;
            SubsystemBlockEntities entities =
                m_project.FindSubsystem<SubsystemBlockEntities>(false);
            if (entities == null)
            {
                reason = "客户端没有 SubsystemBlockEntities";
                return false;
            }
            ComponentBlockEntity blockEntity = entities.GetBlockEntity(key.X, key.Y, key.Z);
            if (blockEntity != null)
            {
                inventory = FindInventory(blockEntity.Entity, key.Kind);
                if (inventory != null)
                {
                    PrepareReplicaEntity(blockEntity.Entity);
                    return true;
                }
                // 本地方块实体种类与主机权威不一致：替换成本端副本（只影响客户端表现）。
                Entity old = blockEntity.Entity;
                try
                {
                    m_project.RemoveEntity(old, disposeEntity: true);
                    m_replicaEntities.Remove(old);
                    m_clientReplicaKindReplaced++;
                    Log("客户端副本方块实体种类不符 @" + key.Describe() + " → 移除旧实体（" +
                        old.GetType().Name + "）并按主机种类重建");
                }
                catch (Exception e)
                {
                    reason = "移除种类不符的副本实体失败：" + e.GetType().Name + " " +
                        e.Message;
                    return false;
                }
            }
            Entity created = CreateBlockEntity(m_project, key.Kind, key.X, key.Y, key.Z,
                persist: false, out reason);
            if (created == null) return false;
            m_replicaEntities.Add(created);
            m_clientReplicaEntitiesCreated++;
            blockEntity = created.FindComponent<ComponentBlockEntity>();
            inventory = FindInventory(created, key.Kind);
            if (inventory == null)
            {
                reason = "新建的副本实体没有 " + ContainerKinds.Name(key.Kind) + " 库存组件";
                return false;
            }
            Log("客户端创建容器副本实体 @" + key.Describe() + "（" +
                ContainerKinds.Name(key.Kind) + "）；累计 " + m_clientReplicaEntitiesCreated);
            return true;
        }

        /// <summary>
        /// 客户端副本实体只做表现：摘掉 IUpdateable（熔炉本地烧炼 / 会改槽位的更新）并把它
        /// 从 Project 的存档实体表移出（IsAddedToProject 仍为 true，SubsystemBlockEntities 照常能找到）。
        /// 对引擎在客户端放置时创建的本地方块实体同样生效。
        /// </summary>
        private void PrepareReplicaEntity(Entity entity)
        {
            if (entity == null || m_project == null) return;
            if (!m_replicaEntities.Add(entity)) return;   // 已经处理过
            SubsystemUpdate update = m_project.FindSubsystem<SubsystemUpdate>(false);
            if (update != null)
            {
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                    update.RemoveUpdateable(updateable);
            }
            Dictionary<Entity, bool> saveMap = m_parentField.GetParentField<
                Dictionary<Entity, bool>>(m_project, "m_entities", typeof(Project));
            saveMap?.Remove(entity);
        }

        /// <summary>
        /// 与引擎 <c>SubsystemCraftingTableBlockBehavior.OnBlockAdded</c> 完全同一条数据库路径：
        /// 找到 EntityTemplate、填 BlockEntity.Coordinates、创建实体。
        /// </summary>
        private Entity CreateBlockEntity(Project project, int kind, int x, int y, int z,
            bool persist, out string reason)
        {
            reason = null;
            string template = TemplateForKind(kind);
            if (template == null)
            {
                reason = "没有 " + ContainerKinds.Name(kind) + " 的实体模板";
                return null;
            }
            try
            {
                var overrides = new ValuesDictionary
                {
                    {
                        "BlockEntity",
                        new ValuesDictionary { { "Coordinates", new Point3(x, y, z) } }
                    }
                };
                Entity entity = DatabaseManager.CreateEntity(project, template, overrides,
                    throwIfNotFound: true);
                if (entity == null)
                {
                    reason = "数据库找不到实体模板 " + template;
                    return null;
                }
                project.AddEntity(entity);
                if (!persist) PrepareReplicaEntity(entity);
                return entity;
            }
            catch (Exception e)
            {
                reason = "创建方块实体 " + template + " 失败：" + e.GetType().Name + " " +
                    e.Message;
                return null;
            }
        }

        private static string TemplateForKind(int kind)
        {
            switch (kind)
            {
                case ContainerKinds.CraftingTable: return "CraftingTable";
                case ContainerKinds.Chest: return "Chest";
                case ContainerKinds.Furnace: return "Furnace";
                case ContainerKinds.Dispenser: return "Dispenser";
                default: return null;
            }
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static int KindForContents(int contents)
        {
            switch (contents)
            {
                // Source: Pak/Database.xml（CraftingTableBlock / ChestBlock / FurnaceBlock /
                //         LitFurnaceBlock / DispenserBlock 的 BlockIndex）
                case 27: return ContainerKinds.CraftingTable;
                case 45: return ContainerKinds.Chest;
                case 64:
                case 65: return ContainerKinds.Furnace;
                case 216: return ContainerKinds.Dispenser;
                default: return ContainerKinds.None;
            }
        }

        private static ComponentInventoryBase FindInventory(Entity entity, int kind)
        {
            if (entity == null) return null;
            switch (kind)
            {
                case ContainerKinds.CraftingTable: return entity.FindComponent<ComponentCraftingTable>();
                case ContainerKinds.Chest: return entity.FindComponent<ComponentChest>();
                case ContainerKinds.Furnace: return entity.FindComponent<ComponentFurnace>();
                case ContainerKinds.Dispenser: return entity.FindComponent<ComponentDispenser>();
                default: return null;
            }
        }

        // ================================================================= 状态捕获 / 应用

        private ContainerStateData CaptureState(ContainerKey key, ComponentInventoryBase inventory,
            bool advanceIfChanged, out bool changed)
        {
            int slots = inventory.SlotsCount;
            var values = new int[slots];
            var counts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                values[i] = inventory.GetSlotValue(i);
                counts[i] = inventory.GetSlotCount(i);
            }
            int revision = m_hostRevisions.TryGetValue(key, out int known) ? known : 0;
            changed = !m_hostLastStates.TryGetValue(key, out ContainerStateData last) ||
                !last.SameValues(values, counts);
            if (revision <= 0) revision = 1;
            else if (changed && advanceIfChanged) revision++;
            m_hostRevisions[key] = revision;
            var state = new ContainerStateData(key.X, key.Y, key.Z, key.Kind, revision, values,
                counts);
            m_hostLastStates[key] = state;
            return state;
        }

        private bool ApplyStateToInventory(ComponentInventoryBase inventory,
            ContainerStateData state, out string reason)
        {
            reason = null;
            if (inventory.SlotsCount != state.SlotsCount)
            {
                reason = "槽位数不一致（副本 " + inventory.SlotsCount + " / 主机 " +
                    state.SlotsCount + "）";
                return false;
            }
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                if (!SetSlotDirect(inventory, i, state.Values[i], state.Counts[i]))
                {
                    reason = "槽 " + i + " 写入失败（value=" + state.Values[i] + " count=" +
                        state.Counts[i] + "）";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 直接写槽（权威落地 / 副本镜像）：不跑 Add/Remove 的行为链。
        /// 走 <c>ComponentInventoryBase.m_slots</c> 的 <c>Slot.Value/Count</c> 字段
        /// （与 NetworkAvatarSpawner.SetCraftingSlotDirect 同一手法）。
        /// </summary>
        private bool SetSlotDirect(ComponentInventoryBase inventory, int slotIndex, int value,
            int count)
        {
            if (inventory == null || slotIndex < 0 || slotIndex >= inventory.SlotsCount ||
                count < 0 || (count > 0 && value == 0))
            {
                return false;
            }
            if (count > 0)
            {
                int contents = Game.Terrain.ExtractContents(value);
                if (contents < 0 || contents >= BlocksManager.Blocks.Length) return false;
                count = Math.Min(count, BlocksManager.Blocks[contents].MaxStacking);
            }
            IList slots = m_parentField.GetParentField(inventory, "m_slots",
                typeof(ComponentInventoryBase)) as IList;
            object slot = slots?[slotIndex];
            if (slot == null) return false;
            if (m_slotValueField == null || m_slotCountField == null)
            {
                Type slotType = slot.GetType();
                m_slotValueField = slotType.GetField("Value",
                    BindingFlags.Instance | BindingFlags.Public);
                m_slotCountField = slotType.GetField("Count",
                    BindingFlags.Instance | BindingFlags.Public);
            }
            if (m_slotValueField == null || m_slotCountField == null) return false;
            m_slotValueField.SetValue(slot, count > 0 ? value : 0);
            m_slotCountField.SetValue(slot, count);
            return true;
        }

        /// <summary>通用槽写入（玩家背包回滚 / 普通容器）：走引擎的 Remove+Add 容量校验。</summary>
        private static bool SetInventorySlot(IInventory inventory, int slotIndex, int value,
            int count)
        {
            if (inventory == null || slotIndex < 0 || slotIndex >= inventory.SlotsCount ||
                count < 0 || (count > 0 && value == 0))
            {
                return false;
            }
            try
            {
                if (count > 0)
                {
                    int capacity = inventory.GetSlotCapacity(slotIndex, value);
                    if (capacity <= 0) return false;
                    count = Math.Min(count, capacity);
                }
                if (inventory.GetSlotValue(slotIndex) == (count > 0 ? value : 0) &&
                    inventory.GetSlotCount(slotIndex) == count)
                {
                    return true;
                }
                inventory.RemoveSlotItems(slotIndex, int.MaxValue);
                if (count > 0) inventory.AddSlotItems(slotIndex, value, count);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>非工作台容器的原子回滚：直接写回权威快照（槽位已校验，不会失败）。</summary>
        private void RestoreContainerSnapshot(ComponentInventoryBase inventory,
            ContainerStateData snapshot)
        {
            if (inventory == null || snapshot == null) return;
            int slots = Math.Min(inventory.SlotsCount, snapshot.SlotsCount);
            for (int i = 0; i < slots; i++)
            {
                SetSlotDirect(inventory, i, snapshot.Values[i], snapshot.Counts[i]);
            }
        }

        private void RestoreCraftingSnapshot(ComponentCraftingTable crafting,
            ContainerStateData snapshot)
        {
            if (crafting == null || snapshot == null) return;
            for (int i = 0; i < crafting.SlotsCount &&
                    i < snapshot.SlotsCount; i++)
            {
                SetSlotDirect(crafting, i, snapshot.Values[i], snapshot.Counts[i]);
            }
            try
            {
                m_parentMethod.InvokeParentMethod(crafting, "UpdateCraftingResult");
            }
            catch (Exception e)
            {
                LogProblem("crafting-restore", "恢复合成台权威快照后重算结果失败（" +
                    e.GetType().Name + " " + e.Message + "）→ 该容器状态可能短期不一致，下一次轮询会再广播");
            }
        }

        private bool ValidateDesiredSlots(ComponentInventoryBase inventory, int[] values,
            int[] counts, int resultSlot, int remainsSlot, out string reason)
        {
            reason = null;
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                if (i == resultSlot) continue;   // result 由主机 UpdateCraftingResult 重算
                int count = counts[i];
                int value = values[i];
                if (count == 0) continue;
                int contents = Game.Terrain.ExtractContents(value);
                if (contents <= 0 || contents >= BlocksManager.Blocks.Length)
                {
                    reason = "slot-content: 槽 " + i + " 的方块内容非法：" + contents;
                    return false;
                }
                int capacity;
                if (i == remainsSlot)
                {
                    // ComponentCraftingTable.GetSlotCapacity 对 result/remains 返回 0；
                    // remains 的容量按方块自身的堆叠上限校验（与引擎 AddSlotItems 一致）。
                    capacity = BlocksManager.Blocks[contents].MaxStacking;
                }
                else
                {
                    try
                    {
                        capacity = inventory.GetSlotCapacity(i, value);
                    }
                    catch (Exception e)
                    {
                        reason = "slot-content: 槽 " + i + " 容量查询失败：" +
                            e.GetType().Name + " " + e.Message;
                        return false;
                    }
                }
                if (capacity <= 0 || count > capacity)
                {
                    reason = "slot-capacity: 槽 " + i + " 数量超容量（" + count + " > " +
                        capacity + "）";
                    return false;
                }
            }
            return true;
        }

        // ================================================================= 生命周期 / 诊断

        private void ResetEngineState(string reason)
        {
            DetachUseInput();
            CloseContainerView(reason);
            m_hostRevisions.Clear();
            m_hostLastStates.Clear();
            m_hostFurnaceRevisions.Clear();
            m_hostLastFurnaceStates.Clear();
            m_furnaceClients.Clear();
            m_replicaEntities.Clear();
            m_hostCreatedEntities.Clear();
            m_onUseByContents.Clear();
            m_nextReplicaSweepMs = 0;
            m_sweepScratch.Clear();
            m_inputInjectionLogged = false;
        }

        private void Log(string message) =>
            m_diagnostics?.Info(m_ownerId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = Environment.TickCount64;
            if (m_lastProblemMs.TryGetValue(key, out long last) &&
                nowMs - last < ProblemLogMs)
            {
                return;
            }
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        /// <summary>
        /// 限流的信息级日志（与 <see cref="LogProblem"/> 共用限流表）：用于"为什么某项校验被
        /// 跳过"这类必须可见、但每帧都会发生的事实（例如创造模式检测）。计数仍在 Describe()。
        /// </summary>
        private void LogThrottled(string key, string message)
        {
            long nowMs = Environment.TickCount64;
            if (m_lastProblemMs.TryGetValue(key, out long last) &&
                nowMs - last < ProblemLogMs)
            {
                return;
            }
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Info(m_ownerId, message);
        }
    }
}
