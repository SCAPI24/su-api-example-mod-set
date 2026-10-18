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
        /// <summary>方块使用距离上限（格²）；与旧实现 ContainerSync 的 8 格一致。</summary>
        private const float MaxUseDistanceSq = 8f * 8f;
        private const int ProblemLogMs = 5000;

        private readonly NetworkAvatarSpawner m_spawner;
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
        private long m_hostStatesPolled;
        private long m_hostStateBroadcasts;
        private long m_hostKindMismatches;

        private long m_clientViewsOpened;
        private long m_clientViewsClosed;
        private long m_clientStatesApplied;
        private long m_clientStateApplyFailures;
        private long m_clientReplicaEntitiesCreated;
        private long m_clientReplicaKindReplaced;

        public ContainerReplicaEngine(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModParentMethod parentMethod, IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "block-use" : ownerId;
        }

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
            "/" + m_hostEditsRejected + " kindMismatch=" + m_hostKindMismatches +
            " polled=" + m_hostStatesPolled + " broadcast=" + m_hostStateBroadcasts +
            " · client views=" + m_clientViewsOpened + "/" + m_clientViewsClosed +
            " statesApplied=" + m_clientStatesApplied + " applyFailed=" +
            m_clientStateApplyFailures + " replicas=" + m_clientReplicaEntitiesCreated +
            " kindReplaced=" + m_clientReplicaKindReplaced + ")";

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
                direction.Z);
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
            float distanceSq = Vector3.DistanceSquared(player.ComponentBody.Position, center);
            if (distanceSq > MaxUseDistanceSq)
            {
                reason = "距离过远（" + MathUtils.Sqrt(distanceSq).ToString("F1",
                    CultureInfo.InvariantCulture) + " 格 > 8 格）";
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

        public bool TryApplyContainerEdit(int clientId, in ContainerEditRequestData request,
            out ContainerStateData state, out string reason)
        {
            state = null;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (!TryGetHostInventory(request.Key, out ComponentInventoryBase inventory,
                    out reason))
            {
                m_hostEditsRejected++;
                return false;
            }
            ContainerStateData current = CaptureState(request.Key, inventory, false, out _);
            if (request.BaseRevision != current.Revision)
            {
                reason = "版本不一致（请求 base=" + request.BaseRevision + "，主机=" +
                    current.Revision + "）";
                m_hostEditsRejected++;
                return false;
            }
            if (!current.SameValues(request.BaseValues, request.BaseCounts))
            {
                reason = "base 槽位与主机权威值不一致";
                m_hostEditsRejected++;
                return false;
            }
            if (request.DesiredValues.Length != inventory.SlotsCount ||
                request.DesiredCounts.Length != inventory.SlotsCount)
            {
                reason = "desired 槽位数与库存不一致（" + request.DesiredValues.Length + "/" +
                    inventory.SlotsCount + "）";
                m_hostEditsRejected++;
                return false;
            }
            int resultSlot = -1;
            int remainsSlot = -1;
            if (request.ContainerKind == ContainerKinds.CraftingTable)
            {
                var craftingSlots = inventory as ComponentCraftingTable;
                if (craftingSlots == null)
                {
                    reason = "主机容器不是 ComponentCraftingTable";
                    m_hostEditsRejected++;
                    return false;
                }
                resultSlot = craftingSlots.ResultSlotIndex;
                remainsSlot = craftingSlots.RemainsSlotIndex;
            }
            if (!ValidateDesiredSlots(inventory, request.DesiredValues, request.DesiredCounts,
                    resultSlot, remainsSlot, out reason))
            {
                m_hostEditsRejected++;
                return false;
            }

            if (request.ContainerKind == ContainerKinds.CraftingTable)
            {
                var crafting = inventory as ComponentCraftingTable;
                int baseResult = current.Counts[resultSlot];
                int desiredResult = request.DesiredCounts[resultSlot];
                if (desiredResult > baseResult)
                {
                    reason = "客户端试图创建合成产物（result " + baseResult + " → " +
                        desiredResult + "）";
                    m_hostEditsRejected++;
                    return false;
                }
                if (desiredResult < baseResult)
                {
                    // 取走合成产物：主机用引擎的 RemoveSlotItems 原子消费配方格（含 remains），
                    // 然后拿客户端的 desired 做**事后核对**；不一致就恢复到 base 再拒绝
                    // （绝不把一次不合法的合成留在主机世界里）。
                    int take = baseResult - desiredResult;
                    int removed;
                    try
                    {
                        removed = crafting.RemoveSlotItems(resultSlot, take);
                    }
                    catch (Exception e)
                    {
                        reason = "主机 RemoveSlotItems 抛异常：" + e.GetType().Name + " " +
                            e.Message;
                        RestoreCraftingSnapshot(crafting, current);
                        m_hostEditsRejected++;
                        return false;
                    }
                    if (removed != take)
                    {
                        reason = "主机只移除了 " + removed + " 个合成产物（请求 " + take + "）";
                        RestoreCraftingSnapshot(crafting, current);
                        m_hostEditsRejected++;
                        return false;
                    }
                    for (int i = 0; i < inventory.SlotsCount; i++)
                    {
                        if (i == resultSlot) continue;   // 产物数量由主机重算，客户端值仅供参考
                        if (request.DesiredValues[i] == inventory.GetSlotValue(i) &&
                            request.DesiredCounts[i] == inventory.GetSlotCount(i))
                        {
                            continue;
                        }
                        reason = "取走合成产物后主机配方格/remains 与客户端不一致（槽 " + i +
                            "：主机 " + inventory.GetSlotValue(i) + "x" +
                            inventory.GetSlotCount(i) + "，客户端 " + request.DesiredValues[i] +
                            "x" + request.DesiredCounts[i] + "）→ 恢复并拒绝";
                        RestoreCraftingSnapshot(crafting, current);
                        m_hostEditsRejected++;
                        return false;
                    }
                }
                else
                {
                    // 只改配方格 / 让主机重算产物：先在守恒校验里排除 result / remains。
                    if (request.DesiredValues[remainsSlot] != current.Values[remainsSlot] ||
                        request.DesiredCounts[remainsSlot] != current.Counts[remainsSlot])
                    {
                        reason = "没有取走合成产物却改动了 remains 槽 → 拒绝";
                        m_hostEditsRejected++;
                        return false;
                    }
                    if (!ValidateContainerGains(request.Key, current, request, resultSlot,
                            remainsSlot, out reason))
                    {
                        m_hostEditsRejected++;
                        return false;
                    }
                    for (int i = 0; i < inventory.SlotsCount; i++)
                    {
                        if (i == resultSlot || i == remainsSlot) continue;
                        SetSlotDirect(crafting, i, request.DesiredValues[i],
                            request.DesiredCounts[i]);
                    }
                    try
                    {
                        m_parentMethod.InvokeParentMethod(crafting, "UpdateCraftingResult");
                    }
                    catch (Exception e)
                    {
                        reason = "重算合成结果失败：" + e.GetType().Name + " " + e.Message;
                        m_hostEditsRejected++;
                        return false;
                    }
                }
            }
            else
            {
                if (!ValidateContainerGains(request.Key, current, request, -1, -1,
                        out reason))
                {
                    m_hostEditsRejected++;
                    return false;
                }
                for (int i = 0; i < inventory.SlotsCount; i++)
                {
                    if (!SetInventorySlot(inventory, i, request.DesiredValues[i],
                            request.DesiredCounts[i]))
                    {
                        reason = "槽 " + i + " 无法写入（容量校验失败）";
                        m_hostEditsRejected++;
                        return false;
                    }
                }
            }
            state = CaptureState(request.Key, inventory, true, out _);
            m_hostEditsApplied++;
            Log("主机落地 clientId=" + clientId + " 的容器编辑 " + request.Describe() + " → " +
                state.Describe());
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
                if (i == resultSlot || i == remainsSlot) continue;
                int count = counts[i];
                int value = values[i];
                if (count == 0) continue;
                int contents = Game.Terrain.ExtractContents(value);
                if (contents <= 0 || contents >= BlocksManager.Blocks.Length)
                {
                    reason = "槽 " + i + " 的方块内容非法：" + contents;
                    return false;
                }
                int capacity;
                try
                {
                    capacity = inventory.GetSlotCapacity(i, value);
                }
                catch (Exception e)
                {
                    reason = "槽 " + i + " 容量查询失败：" + e.GetType().Name + " " + e.Message;
                    return false;
                }
                if (capacity <= 0 || count > capacity)
                {
                    reason = "槽 " + i + " 数量超容量（" + count + " > " + capacity + "）";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 阻止客户端把物品"变"进容器：每种物品在容器里的增量不得超过它从玩家背包里
        /// 减少的数量。玩家背包本身仍由客户端权威（既有 PlayerInventory 同步），这里只守容器。
        /// </summary>
        private static bool ValidateContainerGains(ContainerKey key, ContainerStateData current,
            in ContainerEditRequestData request, int excludeSlotA, int excludeSlotB,
            out string reason)
        {
            reason = null;
            var baseContainer = new Dictionary<int, long>();
            var desiredContainer = new Dictionary<int, long>();
            for (int i = 0; i < current.SlotsCount; i++)
            {
                if (i == excludeSlotA || i == excludeSlotB) continue;
                if (request.BaseCounts[i] > 0 && request.BaseValues[i] != 0)
                {
                    baseContainer.TryGetValue(request.BaseValues[i], out long baseCount);
                    baseContainer[request.BaseValues[i]] = baseCount + request.BaseCounts[i];
                }
                if (request.DesiredCounts[i] > 0 && request.DesiredValues[i] != 0)
                {
                    desiredContainer.TryGetValue(request.DesiredValues[i], out long desiredCount);
                    desiredContainer[request.DesiredValues[i]] = desiredCount +
                        request.DesiredCounts[i];
                }
            }
            var playerBase = new Dictionary<int, long>();
            var playerDesired = new Dictionary<int, long>();
            BlockUseCodec.AccumulateItems(request.PlayerBaseValues, request.PlayerBaseCounts,
                playerBase);
            BlockUseCodec.AccumulateItems(request.PlayerDesiredValues,
                request.PlayerDesiredCounts, playerDesired);
            foreach (KeyValuePair<int, long> item in desiredContainer)
            {
                baseContainer.TryGetValue(item.Key, out long baseCount);
                long gain = item.Value - baseCount;
                if (gain <= 0) continue;
                playerBase.TryGetValue(item.Key, out long playerBefore);
                playerDesired.TryGetValue(item.Key, out long playerAfter);
                long available = playerBefore - playerAfter;
                if (available < gain)
                {
                    reason = "物品 " + item.Key + " 在容器里增加 " + gain + "，但玩家背包只减少 " +
                        available + "（" + key.Describe() + "）";
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
    }
}
