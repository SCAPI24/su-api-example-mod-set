using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 调试"镜像角色"的**引擎动作**（<see cref="IMirrorCharacterPort"/> 的实现）。
    ///
    /// 产品口径：主机世界里已有一个本地角色（名字由配置 <c>mirrorcharacter</c> 指定，所有者 = "H3"）。
    /// 客户端加入后，按 <c>mirrorcharactermode</c> 二选一：
    ///
    ///   · **replace（默认）**：**用 H3 的第三个分屏视图显示客户端角色本身**。
    ///       - H3 的身体从世界表现里摘掉：模型不再渲染（从 <c>SubsystemModelsRenderer.m_componentModels</c>
    ///         摘除）、从 <c>SubsystemBodies</c> 摘除（不可碰撞）、停掉会改状态/推挤的 updateable；
    ///         **不改坐标**（坐标会广播给客户端 / 进存档 / 上实时地图）。
    ///       - H3 的 <c>GameWidget</c> 相机绑到客户端角色的 <c>ComponentPlayer</c>
    ///         （<c>GameWidget.Target</c> + <c>ActiveCamera = FppCamera</c>）→ 该分屏 = 客户端第一人称。
    ///         <c>ViewWidget.Draw</c> 只用 H3 自己的 <c>PlayerData.ComponentPlayer / IsReadyForPlaying</c>
    ///         决定画不画（Source: Survivalcraft/Game/ViewWidget.cs:45-51），<c>FppCamera</c> 读
    ///         <c>GameWidget.Target.ComponentCreatureModel.EyePosition/EyeRotation</c>
    ///         （Source: Survivalcraft/Game/FppCamera.cs:21-44）；与 <c>Engine/DebugMirrorClient.cs:275-280</c>
    ///         是同一条已证明路径。
    ///       - **不复制任何状态**（背包 / 生命 / 动作都不动；没有第二份状态、没有第二个模型）。
    ///       - H3 的 PlayerData / GameWidget / 身份 / 统计 / 记录都不动，解绑时恢复模型渲染、碰撞、
    ///         updateable、输入、相机与快照里的位置。
    ///
    ///   · **copy（旧行为，可选）**：把客户端角色的**可见状态**逐拍复制到 H3 身上，让主机在本机世界
    ///     看到客户端的精确替身。复制清单（逐字段来源）：
    ///   · body：<c>ComponentBody.Position / Velocity / Rotation / TargetCrouchFactor / CrouchFactor</c>
    ///     + <c>ComponentLocomotion.LookAngles</c>（身体 + 头部朝向）。
    ///   · vitals：<c>ComponentHealth.Health/Air</c>（私有 backing field + m_lastHealth）、
    ///     <c>ComponentVitalStats</c>（m_food / m_stamina / m_sleep / m_temperature / m_wetness /
    ///     m_targetTemperature + 对应的 m_last* 边沿值）、
    ///     <c>ComponentFlu</c>（m_fluOnset / m_fluDuration / m_coughDuration / m_sneezeDuration）、
    ///     <c>ComponentSickness.m_sicknessDuration</c>、<c>ComponentOnFire.m_fireDuration</c>。
    ///   · inventory：<c>ComponentMiner.Inventory</c> 的槽位值/数量、ActiveSlotIndex，
    ///     以及 <c>ComponentClothing</c> 的四类衣物（装备外观）。创造背包只镜像前
    ///     <see cref="PlayerInventoryMath.MaxSyncSlots"/> 槽（快捷栏；创造仓库其余槽位不是实时同步数据）。
    ///   · actions：<c>ComponentMiner.PokingPhase / DigCellFace / DigProgress</c>（挖掘裂纹 + 抬手/攻击/使用），
    ///     <c>ComponentHumanModel</c> 的 m_punchFactor / m_punchPhase / m_aimHandAngle /
    ///     m_inHandItemOffset / m_inHandItemRotation / m_rowLeft / m_rowRight（这些字段在上一次
    ///     ComponentHumanModel.Update 里已被消费，所以帧末读它们才是真正画出来的姿势），
    ///     <c>ComponentCreatureModel</c> 的 m_injuryColorFactor / MovementAnimationPhase / Bob /
    ///     DeathPhase。客户端化身在主机侧的这些字段由既有 PlayerAction 同步链写入
    ///     （Engine/AvatarActionBridge.cs:TryApplyPresentation），本类只读，不新增协议。
    ///
    /// 碰撞 / 玩法隔离（为什么不是"引擎里有个 IsCollidable"）：
    ///   · 引擎的 <c>ComponentBody</c> **没有**公开的 IsCollidable / 碰撞开关（只有 Block.IsCollidable）；
    ///     唯一受支持的"把某个身体从碰撞集合里摘掉"的入口是 <c>SubsystemBodies.RemoveBody</c>
    ///     （private，组合根注入的 SuAPI <c>IModParentMethod</c> 调用；与 NetworkAvatarSpawner
    ///     调 <c>SubsystemGameWidgets.RemoveGameWidget</c> 同一套做法）。恢复时调 <c>AddBody</c>。
    ///   · 同时用公开 API <c>SubsystemUpdate.RemoveUpdateable(ComponentBody)</c> 停掉 H3 自己的物理攒动：
    ///     否则它仍会以客户端速度移动/推挤（ComponentBody.HandleAxisCollision 会改对方速度），
    ///     即使别的身体看不见它。copy 模式位置每拍由本类直接写入；replace 模式位置不动。
    ///   · replace 模式"看不见"的实现 = 把 H3 的每个 <c>ComponentModel</c> 从
    ///     <c>SubsystemModelsRenderer.m_componentModels</c> 摘掉（这是"画不画"的唯一开关）；
    ///     **不把身体挪到世界外** —— 主机每帧把本机角色的位置广播给客户端
    ///     （<c>NetworkAvatarSpawner.CapturePlayerPositionBatch</c>，键 (0,slot)），挪走会让客户端上的
    ///     H3 也一起瞬移，还会污染存档位置 / 实时地图。
    ///   · 输入 / 玩法：摘掉 <c>ComponentInput</c>（并清空 m_playerInput）、<c>ComponentPlayer</c> 仍会跑但
    ///     读到零输入；再摘掉 <c>ComponentMiner</c>（不会自动交互/挖掘）、<c>ComponentHealth</c>（不受伤/不死亡）、
    ///     <c>ComponentVitalStats</c>（不饿/不耗）、<c>ComponentFlu</c> / <c>ComponentSickness</c>（不咳/不吐/
    ///     不写 PlayerStats）、<c>ComponentLocomotion</c>（不走路、不写 DistanceWalked/Jumps 统计）、
    ///     <c>ComponentSleep</c>（不睡/不黑屏）。解绑时按快照恢复全部 updateable。
    ///   · replace 模式 H3 的 GameWidget / ViewWidget 都不接受命中测试（HUD 按钮 / 拖放不会作用到 H3）。
    ///
    /// 持久化口径：
    ///   · H3 是**真实主机本地角色**，不从 Project.m_entities 摘除、不隐藏、replace 模式也不改坐标，
    ///     所以它照常进存档且存的是自己的值；copy 模式绑定期间存档里会留下"客户端当前状态"
    ///     （位置/生命/背包/衣物），解绑时逐项写回绑定前快照；若进程在绑定期间异常退出，
    ///     下次进世界看到的是快照之前的最后一次落盘值（与任何未保存状态一致）。
    ///   · **绝不写客户端身份记录**：不调用 AvatarInventoryStore / PlayerStatsStore / AvatarRoster /
    ///     PlayerData.Name / 任何 SuMultiplayerPlayers.xml 写入；来源只读，目标只写 H3 引擎字段。
    ///
    /// 本类**无静态可变状态**：反射入口 / 化身表 / 诊断由组合根以实例注入。
    /// </summary>
    public sealed class MirrorCharacterEngine : IMirrorCharacterPort, IDisposable
    {
        private const string OwnerId = "mirror-character";

        /// <summary>只镜像引擎快捷栏（与既有 PlayerInventory 同步范围一致）。</summary>
        private const int CreativeMirrorSlots = PlayerInventoryMath.MaxSyncSlots;

        private readonly NetworkAvatarSpawner m_avatars;
        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly HashSet<string> m_reportedOnce = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 第三分屏的**客户端伤情 / 体征 / 死亡反馈镜像**（只写 H3 的显示层：血条 / 食物条 / 温度档位 /
        /// 等级文字 / 红屏 / 黑屏 / 大消息）。replace 模式下 H3 的 <c>ComponentHealth.Update</c> 被摘掉，
        /// 引擎不会再替视图所有者写这些显示值 —— 由它在每拍按客户端化身的实际值补齐。
        /// 纯逻辑 = Engine/MirrorViewFeedbackPolicy.cs，引擎写入 = Engine/MirrorViewFeedback.cs。
        /// </summary>
        private readonly MirrorViewFeedback m_viewFeedback;

        private MethodInfo m_addBodyMethod;
        private MethodInfo m_removeBodyMethod;
        private MethodInfo m_clothingTexturesMethod;
        private ModFieldRef<ComponentInput, PlayerInput> m_inputRef;

        private Project m_project;
        private PlayerData m_target;
        private PlayerData m_source;
        private ComponentPlayer m_targetPlayer;
        private ComponentPlayer m_sourcePlayer;
        private ComponentBody m_targetBody;
        private ComponentBody m_sourceBody;
        private ComponentHealth m_targetHealth;
        private ComponentHealth m_sourceHealth;
        private ComponentVitalStats m_targetVitals;
        private ComponentVitalStats m_sourceVitals;
        private ComponentLocomotion m_targetLocomotion;
        private ComponentLocomotion m_sourceLocomotion;
        private ComponentMiner m_targetMiner;
        private ComponentMiner m_sourceMiner;
        private ComponentCreatureModel m_targetModel;
        private ComponentCreatureModel m_sourceModel;
        private ComponentHumanModel m_targetHuman;
        private ComponentHumanModel m_sourceHuman;
        private ComponentInput m_targetInput;
        private GameWidget m_targetGameWidget;
        private ComponentFlu m_targetFlu;
        private ComponentFlu m_sourceFlu;
        private ComponentSickness m_targetSickness;
        private ComponentSickness m_sourceSickness;
        private ComponentOnFire m_targetOnFire;
        private ComponentOnFire m_sourceOnFire;
        private ComponentClothing m_targetClothing;
        private ComponentClothing m_sourceClothing;

        private MirrorCharacterSnapshot m_snapshot;
        private readonly List<IUpdateable> m_removedUpdateables = new List<IUpdateable>();
        private bool m_bodyRemoved;
        private bool m_bound;
        private bool m_restoredDuringDispose;
        private MirrorRestoreOutcome m_restoreDuringDisposeOutcome;
        private bool m_projectDisposedSubscribed;
        private string m_restoreFailureReason;
        private string m_characterName = string.Empty;
        private int m_clientId = -1;

        /// <summary>
        /// 本端按配置要用的绑定计划（replace = 摘身体 + 绑相机、不复制状态；copy = 每拍复制）。
        /// 绑定期间只读；解绑恢复按它决定要写回哪些字段。
        /// </summary>
        private MirrorCharacterPlan m_plan;

        /// <summary>replace 模式：H3 的 GameWidget 相机是否已绑到客户端角色的 ComponentPlayer。</summary>
        private bool m_viewBound;

        /// <summary>replace：第三分屏的快捷栏原本绑在宿主自己的背包上；绑定时改绑客户端化身的背包，
        /// 解绑时改回宿主背包（Source: Survivalcraft/Game/ComponentGui.cs:281 ShortInventoryWidget.AssignComponents）。</summary>
        private bool m_inventoryViewBound;

        /// <summary>replace：让宿主 HUD 的蹲伏按钮反映客户端状态（按钮读宿主自己的
        /// ComponentBody.TargetCrouchFactor，Source: ComponentGui.cs:428）。只写表现字段。</summary>
        private bool m_crouchBridged;

        /// <summary>replace：客户端角色死亡 → 镜像分屏切 DeathCamera 的次数。</summary>
        private long m_deathCameraShown;

        /// <summary>replace：客户端复活 → 镜像分屏切回 FppCamera 的次数。</summary>
        private long m_deathCameraRestored;

        /// <summary>replace 模式：H3 的模型是否已从 SubsystemModelsRenderer 的渲染表里摘掉。</summary>
        private bool m_bodyHidden;

        /// <summary>
        /// <c>SubsystemModelsRenderer.m_componentModels</c>（<c>Dictionary&lt;ComponentModel, ModelData&gt;</c>）。
        /// 这是"某个模型画不画"的唯一开关：绘制入口只遍历它（Source: Survivalcraft/Game/
        /// SubsystemModelsRenderer.cs:66-88），实体加入/移除时由 OnEntityAdded/OnEntityRemoved 维护
        /// （:139-159）。ModelData 是私有嵌套类型，所以这里按运行时类型反射调用
        /// TryGetValue / Remove / Add，而不是强类型引用（引擎版本不匹配时明确失败）。
        /// </summary>
        private object m_modelTable;
        private MethodInfo m_modelTableTryGet;
        private MethodInfo m_modelTableRemove;
        private MethodInfo m_modelTableAdd;
        private readonly List<ComponentModel> m_hiddenModels = new List<ComponentModel>();
        private readonly List<object> m_hiddenModelData = new List<object>();

        public MirrorCharacterEngine(NetworkAvatarSpawner avatars, IModParentField parentField,
            IModParentMethod parentMethod, IModuleDiagnostics diagnostics)
        {
            m_avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_diagnostics = diagnostics;
            m_viewFeedback = new MirrorViewFeedback(m_parentField,
                delegate { return Time.RealTime; }, m_diagnostics);
        }

        public bool IsBound => m_bound;

        public int BoundClientId => m_bound ? m_clientId : -1;

        public string BoundCharacterName => m_bound ? m_characterName : string.Empty;

        /// <summary>
        /// 第三分屏客户端反馈镜像的计数器一行（<c>viewDamageFlash</c> / <c>viewHealthBarFlash</c> /
        /// <c>viewHudNumbers</c> / <c>viewDeathOverlay</c> / <c>viewFeedbackFailures</c> /
        /// <c>viewFeedbackSkipped</c> + 原因 + 细项）。供控制器的状态行 / 10 秒限流日志输出。
        /// 未接入（配置关闭 / 已解绑）时是一串 0，不会让状态行缺键。
        /// </summary>
        public string DescribeViewFeedback() =>
            m_viewFeedback.Describe() +
            " deathCameraShown=" + m_deathCameraShown +
            " deathCameraRestored=" + m_deathCameraRestored;

        // ------------------------------------------------------------------ 枚举

        public bool TryListLocalCharacterNames(List<string> names, out string reason)
        {
            reason = null;
            if (names == null)
            {
                reason = "调用方没有给名字列表";
                return false;
            }
            names.Clear();
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进入世界）";
                return false;
            }
            var players = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_avatars.TryCollectLocalPlayers(project, players, out bool tooMany,
                    out string localReason))
            {
                reason = localReason ?? "列不出主机本机角色";
                return false;
            }
            for (int i = 0; i < players.Count; i++)
                names.Add(players[i].Name ?? string.Empty);
            if (tooMany && !string.IsNullOrEmpty(localReason))
            {
                WarnOnce("too-many-local",
                    "主机本机角色超过引擎上限：" + localReason +
                    "（镜像只在列出的前 " + PlayerKey.SlotCount + " 个角色里按名字查找）");
            }
            if (names.Count == 0)
                reason = localReason ?? "主机没有任何本机角色";
            return true;
        }

        public bool TryListClientIds(List<int> clientIds, out string reason)
        {
            reason = null;
            if (clientIds == null)
            {
                reason = "调用方没有给客户端列表";
                return false;
            }
            clientIds.Clear();
            var seen = new HashSet<int>();
            foreach (PlayerKey key in m_avatars.Keys)
            {
                if (key.ClientId <= 0 || !seen.Add(key.ClientId)) continue;
                clientIds.Add(key.ClientId);
            }
            clientIds.Sort();
            if (clientIds.Count == 0) reason = "当前没有已加入的客户端（还没有联机化身）";
            return true;
        }

        // ------------------------------------------------------------------ 绑定 / 解绑

        public bool TryBind(string characterName, int clientId, MirrorCharacterPlan plan,
            out MirrorBindOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (m_bound)
            {
                reason = "已经绑定了一个镜像角色（先解绑）";
                return false;
            }
            if (!string.IsNullOrEmpty(m_restoreFailureReason))
            {
                reason = "上一次解绑写回快照失败，拒绝再次绑定以保护 H3 的存档状态：" +
                    m_restoreFailureReason;
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进入世界）";
                return false;
            }
            if (clientId <= 0)
            {
                reason = "clientId=" + clientId + " 非法（必须是远端连接，主机 0 不是镜像目标）";
                return false;
            }
            if (plan.IsEmpty)
            {
                reason = "绑定计划为空（目标 / 来源未就绪）：" + plan.Describe();
                return false;
            }

            var localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_avatars.TryCollectLocalPlayers(project, localPlayers, out _, out string localReason))
            {
                reason = localReason ?? "列不出主机本机角色";
                return false;
            }
            PlayerData found = null;
            int matches = 0;
            for (int i = 0; i < localPlayers.Count; i++)
            {
                if (!string.Equals(localPlayers[i].Name ?? string.Empty, characterName ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                matches++;
                if (found == null) found = localPlayers[i];
            }
            if (matches == 0)
            {
                reason = "主机本机角色里没有名为「" + characterName + "」的角色";
                return false;
            }
            if (matches > 1)
            {
                reason = "有 " + matches + " 个本机角色都叫「" + characterName + "」（不猜是哪一个）";
                return false;
            }
            if (!m_avatars.TryGet(new PlayerKey(clientId, 0), out PlayerData source) || source == null)
            {
                reason = "客户端 " + clientId + " 的联机角色（槽位 0）还没建好";
                return false;
            }
            if (ReferenceEquals(source, found))
            {
                reason = "镜像目标不能是客户端化身自己（角色「" + characterName + "」）";
                return false;
            }

            ComponentPlayer targetPlayer = found.ComponentPlayer;
            ComponentPlayer sourcePlayer = source.ComponentPlayer;
            ComponentBody targetBody = targetPlayer?.ComponentBody;
            ComponentBody sourceBody = sourcePlayer?.ComponentBody;
            if (targetPlayer == null || targetBody == null)
            {
                reason = "本机角色「" + characterName + "」还没有 ComponentPlayer/ComponentBody";
                return false;
            }
            if (sourcePlayer == null || sourceBody == null)
            {
                reason = "客户端 " + clientId + " 的联机角色还没有 ComponentPlayer/ComponentBody";
                return false;
            }
            // 相机 Target 是 ComponentCreature：FppCamera 每帧读 Target.ComponentCreatureModel 的
            // 眼睛位置 / 朝向（Source: Survivalcraft/Game/FppCamera.cs:21-44），所以两种模式都要求
            // 来源模型就绪（replace = 第三分屏的相机目标；copy = 动作/姿态镜像的来源）。
            ComponentCreatureModel sourceModel = sourcePlayer.ComponentCreatureModel;
            if (sourceModel == null)
            {
                reason = "客户端 " + clientId + " 的联机角色没有 ComponentCreatureModel" +
                    "（第三分屏的相机没有可跟随的眼睛）";
                return false;
            }
            ComponentHealth targetHealth = targetPlayer.ComponentHealth;
            ComponentHealth sourceHealth = sourcePlayer.ComponentHealth;
            ComponentVitalStats targetVitals = targetPlayer.ComponentVitalStats;
            ComponentVitalStats sourceVitals = sourcePlayer.ComponentVitalStats;
            ComponentLocomotion targetLocomotion = targetPlayer.ComponentLocomotion;
            ComponentLocomotion sourceLocomotion = sourcePlayer.ComponentLocomotion;
            ComponentMiner targetMiner = targetPlayer.ComponentMiner;
            ComponentMiner sourceMiner = sourcePlayer.ComponentMiner;
            ComponentCreatureModel targetModel = targetPlayer.ComponentCreatureModel;
            ComponentInput targetInput = targetPlayer.ComponentInput;
            if (targetInput == null)
            {
                reason = "本机角色「" + characterName + "」没有 ComponentInput" +
                    "（无法在绑定期间清空输入，H3 会继续响应主机操作）";
                return false;
            }
            GameWidget targetGameWidget;
            try
            {
                // PlayerData.GameWidget 没有时会抛（第三分屏必须存在，否则 replace 无处显示）。
                targetGameWidget = found.GameWidget;
            }
            catch (Exception e)
            {
                reason = "本机角色「" + characterName + "」没有 GameWidget（第三分屏不存在）：" +
                    e.GetType().Name + " " + e.Message;
                return false;
            }
            if (plan.CopyState)
            {
                if (targetHealth == null || targetVitals == null || targetLocomotion == null ||
                    targetMiner == null || targetModel == null)
                {
                    reason = "本机角色「" + characterName +
                        "」缺少生命/体力/移动/矿工/模型组件（不是完整玩家，无法复制状态）";
                    return false;
                }
                if (sourceHealth == null || sourceVitals == null || sourceLocomotion == null ||
                    sourceMiner == null)
                {
                    reason = "客户端 " + clientId + " 的联机角色缺少生命/体力/移动/矿工组件";
                    return false;
                }
            }

            SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(false);
            SubsystemBodies subsystemBodies = project.FindSubsystem<SubsystemBodies>(false);
            if (subsystemUpdate == null || subsystemBodies == null)
            {
                reason = "世界里找不到 SubsystemUpdate / SubsystemBodies（无法摘掉 H3 的物理与碰撞）";
                return false;
            }
            if (m_removeBodyMethod == null)
                m_removeBodyMethod = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies),
                    "RemoveBody", new[] { typeof(ComponentBody) });
            if (m_addBodyMethod == null)
                m_addBodyMethod = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies),
                    "AddBody", new[] { typeof(ComponentBody) });
            if (m_removeBodyMethod == null || m_addBodyMethod == null)
            {
                reason = "引擎没有 SubsystemBodies.RemoveBody/AddBody（版本不匹配）：" +
                    "无法在不推挤客户端化身的前提下让 H3 退出碰撞";
                return false;
            }
            if (plan.CopyState && m_clothingTexturesMethod == null)
            {
                m_clothingTexturesMethod = m_parentMethod.GetInstanceMethodInfo(
                    typeof(ComponentClothing), "UpdateRenderTargets", Type.EmptyTypes);
                if (m_clothingTexturesMethod == null)
                {
                    reason = "引擎没有 ComponentClothing.UpdateRenderTargets（版本不匹配）：" +
                        "摘掉衣物磨损逻辑后无法刷新镜像衣物贴图";
                    return false;
                }
            }
            try
            {
                m_inputRef = m_parentField.BindFieldRef<ComponentInput, PlayerInput>("m_playerInput");
            }
            catch (Exception e)
            {
                reason = "无法绑定 ComponentInput.m_playerInput：" + e.GetType().Name + " " +
                    e.Message;
                return false;
            }

            MirrorCharacterSnapshot snapshot;
            try
            {
                snapshot = MirrorCharacterSnapshot.Capture(this, found,
                    targetPlayer, targetGameWidget, targetBody, targetHealth, targetVitals,
                    targetLocomotion, targetMiner, targetModel, targetInput,
                    flu: targetPlayer.Entity.FindComponent<ComponentFlu>(),
                    sickness: targetPlayer.Entity.FindComponent<ComponentSickness>(),
                    onFire: targetPlayer.Entity.FindComponent<ComponentOnFire>(),
                    clothing: targetPlayer.Entity.FindComponent<ComponentClothing>());
            }
            catch (Exception e)
            {
                reason = "抓取 H3 绑定前快照失败（拒绝在没有快照的情况下改动引擎状态）：" +
                    e.GetType().Name + " " + e.Message;
                m_diagnostics?.Error(OwnerId, reason, e);
                return false;
            }

            // 从这里起开始改引擎状态：任何失败都必须回滚到 snapshot。
            m_project = project;
            m_target = found;
            m_source = source;
            m_targetPlayer = targetPlayer;
            m_sourcePlayer = sourcePlayer;
            m_targetBody = targetBody;
            m_sourceBody = sourceBody;
            m_targetHealth = targetHealth;
            m_sourceHealth = sourceHealth;
            m_targetVitals = targetVitals;
            m_sourceVitals = sourceVitals;
            m_targetLocomotion = targetLocomotion;
            m_sourceLocomotion = sourceLocomotion;
            m_targetMiner = targetMiner;
            m_sourceMiner = sourceMiner;
            m_targetModel = targetModel;
            m_sourceModel = sourceModel;
            m_targetHuman = targetPlayer.Entity.FindComponent<ComponentHumanModel>();
            m_sourceHuman = sourcePlayer.Entity.FindComponent<ComponentHumanModel>();
            m_targetInput = targetInput;
            m_targetGameWidget = targetGameWidget;
            m_targetFlu = targetPlayer.Entity.FindComponent<ComponentFlu>();
            m_sourceFlu = sourcePlayer.Entity.FindComponent<ComponentFlu>();
            m_targetSickness = targetPlayer.Entity.FindComponent<ComponentSickness>();
            m_sourceSickness = sourcePlayer.Entity.FindComponent<ComponentSickness>();
            m_targetOnFire = targetPlayer.Entity.FindComponent<ComponentOnFire>();
            m_sourceOnFire = sourcePlayer.Entity.FindComponent<ComponentOnFire>();
            m_targetClothing = targetPlayer.Entity.FindComponent<ComponentClothing>();
            m_sourceClothing = sourcePlayer.Entity.FindComponent<ComponentClothing>();
            m_snapshot = snapshot;
            m_plan = plan;
            m_characterName = found.Name ?? characterName ?? string.Empty;
            m_clientId = clientId;
            m_removedUpdateables.Clear();
            m_bodyRemoved = false;
            m_viewBound = false;
            m_bodyHidden = false;
            m_hiddenModels.Clear();
            m_hiddenModelData.Clear();
            m_modelTable = null;
            m_modelTableTryGet = null;
            m_modelTableRemove = null;
            m_modelTableAdd = null;

            try
            {
                // 1) 停掉会推挤 / 改统计 / 受伤 / 消耗的 updateable（两种模式共用）。
                RemoveUpdateable(subsystemUpdate, targetBody);
                RemoveUpdateable(subsystemUpdate, targetInput);
                // replace：两个 HUD 数据源组件**保留 Update**（它们负责把 Health/Food/Temperature
                // 写进 HUD；摘掉会让第三屏数值停在出生默认值并与我们的写入互相覆盖 → 抖动）。
                // 由 TickHudBridge 每拍把客户端化身的权威值灌进宿主组件，并靠 IsInvulnerable
                // 抑制宿主自身受伤/饥饿/受冻的副作用（数值每拍都被拉回客户端值）。
                // copy：仍摘掉（copy 是直接写宿主 HUD，不需要组件自己 Update）。
                if (!plan.ReplaceHudBridge && targetHealth != null)
                    RemoveUpdateable(subsystemUpdate, targetHealth);
                if (!plan.ReplaceHudBridge && targetVitals != null)
                    RemoveUpdateable(subsystemUpdate, targetVitals);
                if (targetLocomotion != null) RemoveUpdateable(subsystemUpdate, targetLocomotion);
                if (targetMiner != null) RemoveUpdateable(subsystemUpdate, targetMiner);
                RemoveUpdateable(subsystemUpdate, m_targetFlu);
                RemoveUpdateable(subsystemUpdate, m_targetSickness);
                // ComponentClothing.Update 会在等级不足时把衣服丢成拾取物、并磨损衣物；
                // ComponentLevel.Update 会写 PlayerStats.HighestLevel / 加经验 —— 两者都不能在镜像期间跑。
                RemoveUpdateable(subsystemUpdate, m_targetClothing);
                RemoveUpdateable(subsystemUpdate, targetPlayer.ComponentLevel);
                // H3 若在绑定前正骑着东西：身体不再由物理驱动，Rider.Update 会判定"离开坐骑"
                // 并自动下马（改 ParentBody / Velocity）。摘掉它，解绑时原样恢复。
                RemoveUpdateable(subsystemUpdate, targetPlayer.ComponentRider);
                RemoveUpdateable(subsystemUpdate, targetPlayer.Entity.FindComponent<ComponentSleep>());

                // 2) 从碰撞集合里摘掉 H3（其它身体不再看见它的 AABB；它自己的物理也已停）。
                bool bodyTracked = false;
                foreach (ComponentBody body in subsystemBodies.Bodies)
                {
                    if (!ReferenceEquals(body, targetBody)) continue;
                    bodyTracked = true;
                    break;
                }
                if (!bodyTracked)
                {
                    throw new InvalidOperationException(
                        "H3 的 ComponentBody 不在 SubsystemBodies 里（世界状态异常），无法确认碰撞已摘除");
                }
                m_removeBodyMethod.Invoke(subsystemBodies, new object[] { targetBody });
                m_bodyRemoved = true;

                // 3) 输入清零 + 关掉瞄准；H3 的 GameWidget 不接受命中测试
                //    （从背包拖到世界 / 点 HUD 按钮都不会发生）。
                SetTargetInput(default(PlayerInput));
                // 清掉绑定瞬间可能还挂着的瞄准（否则 ComponentPlayer.Update 会走 Aim(Completed)
                // 分支，H3 可能替客户端放箭 / 开枪 / 消耗物品）。
                m_parentField.ModifyParentField(targetPlayer, "m_aim", null,
                    typeof(ComponentPlayer));
                if (m_targetGameWidget != null) m_targetGameWidget.IsHitTestVisible = false;

                if (plan.CopyState)
                {
                    // —— copy（旧行为）：把客户端状态逐拍复制到 H3 身上 ——
                    targetBody.IsGravityEnabled = false;
                    m_bound = true;
                    if (!TryCopy(MirrorCopyPlan.Full, out _, out string copyReason))
                    {
                        throw new InvalidOperationException("绑定后首拍复制失败：" + copyReason);
                    }
                }
                else
                {
                    // —— replace（默认）：H3 的身体从世界表现里摘掉（**不改坐标**），
                    //    再把 H3 的 GameWidget 相机绑到客户端角色的 ComponentPlayer ——
                    HideTargetModel(project);
                    BindViewToSource();
                    m_bound = true;
                    // 第三分屏的客户端反馈镜像：只写 H3 的显示层（血条 / 食物条 / 温度档位 /
                    // 等级文字 / 红屏 / 黑屏 / 死亡大消息），不写客户端或 H3 的任何权威字段。
                    // 接入失败不阻断绑定（相机已经对了），但必须有明确日志 + viewFeedbackFailures 计数。
                    if (!m_viewFeedback.Attach(m_targetPlayer, m_clientId, m_source?.Name,
                            out string feedbackReason))
                    {
                        m_diagnostics?.Warn(OwnerId,
                            "第三分屏客户端反馈镜像接入失败（相机已绑定，但第三分屏不会闪红 / 血条不动 / " +
                            "死亡时没有死亡屏）：" + feedbackReason);
                    }
                }
                SubscribeProjectDisposed();
                outcome = new MirrorBindOutcome(plan.Mode, viewBound: m_viewBound,
                    bodyHidden: m_bodyHidden, collisionDisabled: m_bodyRemoved,
                    stateCopied: plan.CopyState);
                Log("mirrorBind：本地角色「" + m_characterName + "」← 客户端 " + clientId +
                    "（槽位 0）· mirrorMode=" +
                    MirrorCharacterModes.Name(plan.Mode) + " · " + outcome.Describe() +
                    " · plan=" + plan.Describe() +
                    " · collisionDisabled=true · 已抓取绑定前快照 · " +
                    "背包槽=" + (snapshot.InventoryValues?.Length ?? 0) +
                    " · updateablesRemoved=" + m_removedUpdateables.Count +
                    (plan.CopyState
                        ? " · 绑定期间 H3 进存档的是镜像值（解绑会写回快照；H3 的 PlayerData/PlayerStats/身份记录不变）"
                        : " · 第三分屏相机=客户端 ComponentPlayer（FppCamera 第一人称）；" +
                          "H3 的模型不渲染、不可碰撞、不复制任何状态（背包/生命/动作保持原值）；" +
                          "H3 仍会照常进存档（坐标未改）"));
                if (plan.CopyState)
                {
                    InfoOnce("save-scope",
                        "绑定期间 H3 的位置/生命/背包/衣物会随主机存档落盘为客户端当前值；" +
                        "解绑时按快照写回。H3 的 PlayerData.Name / PlayerStats / 身份记录从不写入。");
                }
                else
                {
                    InfoOnce("replace-hud",
                        "第三分屏的 HUD 控件属于 H3 自己的 ComponentGui（引擎按玩家实体绑定 HUD，不跟相机" +
                        " Target）：画面/第一人称手与手持物/耳朵位置/方块高亮来自相机 Target = 客户端角色；" +
                        "生命条/食物条/温度档位/等级文字由 Engine/MirrorViewFeedback 每拍镜像客户端化身的" +
                        "显示值（只写显示层），死亡时显示与引擎同文本的黑屏 + 死亡大消息；" +
                        "快捷栏仍是 H3 自己的（replace 模式不复制背包/生命/动作，无第二份状态）。");
                }
                return true;
            }
            catch (Exception e)
            {
                string failure = e.GetType().Name + " " + e.Message;
                try
                {
                    RestoreInternal();
                }
                catch (Exception restoreError)
                {
                    failure += "；回滚也失败：" + restoreError.GetType().Name + " " +
                        restoreError.Message;
                    m_restoreFailureReason = failure;
                }
                ClearBinding();
                m_bound = false;
                reason = failure;
                m_diagnostics?.Error(OwnerId,
                    "绑定镜像角色「" + characterName + "」← client " + clientId +
                    " 失败（已回滚到绑定前快照）：" + failure, e);
                return false;
            }
        }

        public bool ValidateBound(out string reason)
        {
            reason = null;
            if (!m_bound)
            {
                reason = "当前没有绑定的镜像角色";
                return false;
            }
            if (m_project == null || GameManager.Project != m_project)
            {
                reason = "世界已卸载（Project 已不是绑定时的那个）";
                return false;
            }
            if (m_target == null || m_targetPlayer == null || m_targetPlayer.Entity == null ||
                !m_targetPlayer.Entity.IsAddedToProject)
            {
                reason = "本机角色「" + m_characterName + "」已不在世界里";
                return false;
            }
            if (!m_target.SubsystemPlayers.PlayersData.Contains(m_target))
            {
                reason = "本机角色「" + m_characterName + "」已不在 SubsystemPlayers.PlayersData 里";
                return false;
            }
            if (m_source == null ||
                !m_avatars.TryGet(new PlayerKey(m_clientId, 0), out PlayerData currentSource) ||
                !ReferenceEquals(currentSource, m_source))
            {
                reason = "客户端 " + m_clientId + " 的联机角色已离开 / 已被摘除";
                return false;
            }
            if (m_targetBody == null || m_sourceBody == null || m_sourceBody.Entity == null ||
                !m_sourceBody.Entity.IsAddedToProject)
            {
                reason = "来源或目标的 ComponentBody 已失效";
                return false;
            }
            if (m_plan.BindView)
            {
                // replace：相机 Target 必须是"活着的"客户端 ComponentPlayer（FppCamera 每帧读它）。
                ComponentPlayer latestSource = currentSource.ComponentPlayer;
                if (latestSource == null || latestSource.ComponentBody == null ||
                    latestSource.ComponentCreatureModel == null)
                {
                    reason = "客户端 " + m_clientId +
                        " 的 ComponentPlayer / 身体 / 模型已失效（第三分屏相机没有可跟随的目标）";
                    return false;
                }
                if (!ReferenceEquals(latestSource, m_sourcePlayer))
                {
                    // 客户端重生 / 重建了角色实体：相机重新对到新的 ComponentPlayer。
                    // 不重抓快照、不重建绑定（H3 的引擎状态本来就没被复制，无需回滚）。
                    m_sourcePlayer = latestSource;
                    m_sourceBody = latestSource.ComponentBody;
                    m_sourceModel = latestSource.ComponentCreatureModel;
                    if (m_targetGameWidget != null) m_targetGameWidget.Target = latestSource;
                    InfoOnce("view-rebind:" + m_clientId,
                        "客户端 " + m_clientId + " 的 ComponentPlayer 换了（重生 / 重建角色）→ " +
                        "第三分屏相机已重新绑定到新的 ComponentPlayer");
                }
                else if (m_targetGameWidget != null &&
                         !ReferenceEquals(m_targetGameWidget.Target, latestSource))
                {
                    // 引擎里有人把 Target 改回去了（例如 PlayerData.OnEntityAdded 被重放）→ 重新对到客户端。
                    m_targetGameWidget.Target = latestSource;
                    WarnOnce("view-retarget:" + m_clientId,
                        "第三分屏的相机 Target 被引擎改成了别的角色 → 已重新对到客户端 " +
                        m_clientId + " 的 ComponentPlayer");
                }
                // 客户端角色死亡 → 第四/第三分屏切到引擎的 DeathCamera（环绕尸体），
                // 否则 FppCamera 盯着一具死亡模型（眼睛位置失效）会变成黑屏。
                // 复活后自动切回 FppCamera（引擎 PlayerData 的 PlayerDead 就是这两台相机）。
                if (m_targetGameWidget != null && m_sourcePlayer != null)
                {
                    bool sourceDead = m_sourcePlayer.ComponentHealth != null &&
                        m_sourcePlayer.ComponentHealth.Health <= 0f;
                    bool deathCameraActive = m_targetGameWidget.ActiveCamera is DeathCamera;
                    if (sourceDead && !deathCameraActive)
                    {
                        DeathCamera death = m_targetGameWidget.FindCamera<DeathCamera>(false);
                        if (death != null)
                        {
                            try
                            {
                                m_targetGameWidget.Target = m_sourcePlayer;
                                m_targetGameWidget.ActiveCamera = death;
                                m_deathCameraShown++;
                                InfoOnce("view-death-camera:" + m_clientId,
                                    "客户端 " + m_clientId +
                                    " 角色死亡 → 镜像分屏已切到 DeathCamera（不再黑屏）");
                            }
                            catch (Exception e)
                            {
                                m_diagnostics?.Error(OwnerId,
                                    "镜像分屏切 DeathCamera 失败（客户端 " + m_clientId + "）：" +
                                    e.GetType().Name + " " + e.Message, e);
                            }
                        }
                    }
                    else if (!sourceDead && deathCameraActive)
                    {
                        FppCamera restore = m_targetGameWidget.FindCamera<FppCamera>(false);
                        if (restore != null)
                        {
                            try
                            {
                                m_targetGameWidget.ActiveCamera = restore;
                                m_deathCameraRestored++;
                                InfoOnce("view-death-camera-restore:" + m_clientId,
                                    "客户端 " + m_clientId +
                                    " 角色复活 → 镜像分屏已切回 FppCamera");
                            }
                            catch (Exception e)
                            {
                                m_diagnostics?.Error(OwnerId,
                                    "镜像分屏切回 FppCamera 失败（客户端 " + m_clientId + "）：" +
                                    e.GetType().Name + " " + e.Message, e);
                            }
                        }
                    }
                }
                if (m_targetGameWidget != null &&
                    m_targetGameWidget.ActiveCamera is FppCamera == false &&
                    !(m_targetGameWidget.ActiveCamera is DeathCamera))
                {
                    FppCamera fpp = m_targetGameWidget.FindCamera<FppCamera>(false);
                    if (fpp == null)
                    {
                        reason = "第三分屏的 GameWidget 没有 FppCamera（引擎版本不匹配）";
                        return false;
                    }
                    m_targetGameWidget.ActiveCamera = fpp;
                    WarnOnce("view-camera-reset:" + m_clientId,
                        "第三分屏的相机被引擎切成了 " +
                        (m_targetGameWidget.ActiveCamera == null
                            ? "null"
                            : m_targetGameWidget.ActiveCamera.GetType().Name) +
                        " → 已切回 FppCamera（客户端第一人称）");
                }
            }
            if (m_plan.BindView)
            {
                // 第三分屏的 HUD 数据源跟随客户端（蹲伏按钮 / 快捷栏），只写显示层。
                TickHudBridge();
            }
            if (m_plan.BindView && m_viewFeedback.IsLinked)
            {
                // 第三分屏的客户端反馈：每拍只读客户端化身、只写 H3 的显示层。放在相机校验之后，
                // 客户端重生换了 ComponentPlayer 时本拍用的就是新的那个。
                m_viewFeedback.Tick(m_targetPlayer, m_sourcePlayer, m_source?.Name,
                    Time.FrameDuration);
            }
            return true;
        }

        public bool TryCopy(MirrorCopyPlan plan, out MirrorCopyOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (!m_bound)
            {
                reason = "当前没有绑定的镜像角色";
                return false;
            }
            if (!m_plan.CopyState)
            {
                reason = "replace 模式不复制状态（第三分屏只显示客户端第一人称；" +
                    "H3 的背包 / 生命 / 动作保持原值）";
                return false;
            }
            if (!ValidateBound(out reason)) return false;
            try
            {
                bool body = false;
                bool vitals = false;
                bool inventory = false;
                bool actions = false;
                int slotsChanged = 0;
                if (plan.Body)
                {
                    ApplyBody();
                    body = true;
                }
                if (plan.Vitals)
                {
                    ApplyVitals();
                    vitals = true;
                }
                if (plan.Inventory)
                {
                    ApplyInventory(out slotsChanged);
                    inventory = true;
                }
                if (plan.Actions)
                {
                    ApplyActions();
                    actions = true;
                }
                bool riding = m_sourcePlayer?.ComponentRider?.Mount != null ||
                    (m_sourceBody != null && m_sourceBody.ParentBody != null);
                outcome = new MirrorCopyOutcome(body, vitals, inventory, actions,
                    collisionDisabled: m_bodyRemoved, riding, slotsChanged);
                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        public bool TryRestore(out MirrorRestoreOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (!m_bound)
                return true;
            if (m_restoredDuringDispose)
            {
                // ProjectDisposed 已在 Dispose 之前把快照写回（此时 Project 还活着）；这里只收尾计数。
                m_restoredDuringDispose = false;
                m_bound = false;
                ClearBinding();
                outcome = m_restoreDuringDisposeOutcome;
                return true;
            }
            try
            {
                outcome = RestoreInternal();
                m_bound = false;
                ClearBinding();
                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
                m_restoreFailureReason = reason;
                m_bound = false;
                UnsubscribeProjectDisposed();
                // 快照写回失败时 H3 的字段保持原样（便于排查），但反馈镜像必须停：
                // 绑定状态已经结束，继续每拍写显示层只会让第三分屏停在客户端的值上。
                m_viewFeedback.Release("解绑失败（停止客户端反馈镜像）");
                m_diagnostics?.Error(OwnerId,
                    "解绑镜像角色「" + m_characterName + "」并写回快照失败（H3 可能停在半套状态）：" +
                    reason, e);
                return false;
            }
        }

        public void Dispose()
        {
            if (m_bound)
            {
                try
                {
                    TryRestore(out _, out _);
                }
                catch (Exception e)
                {
                    m_diagnostics?.Warn(OwnerId,
                        "卸载镜像角色时写回快照失败：" + e.GetType().Name + " " + e.Message);
                }
            }
            UnsubscribeProjectDisposed();
        }

        // ------------------------------------------------------------------ 每拍复制

        private void ApplyBody()
        {
            Vector3 position = m_sourceBody.Position;
            Vector3 velocity = m_sourceBody.Velocity;
            Quaternion rotation = m_sourceBody.Rotation;
            RequireFinite(position, "来源 Position");
            RequireFinite(velocity, "来源 Velocity");
            RequireFinite(rotation, "来源 Rotation");
            m_targetBody.Position = position;
            m_targetBody.Velocity = velocity;
            m_targetBody.Rotation = rotation;
            m_targetBody.TargetCrouchFactor = ValidCrouch(m_sourceBody.TargetCrouchFactor);
            WriteField(m_targetBody, "m_crouchFactor", ValidCrouch(m_sourceBody.CrouchFactor),
                typeof(ComponentBody));
            WriteField(m_targetLocomotion, "m_lookAngles", m_sourceLocomotion.LookAngles,
                typeof(ComponentLocomotion));
            // 走路动画用 (Velocity - StandingOnVelocity)；物理停掉后这个值不会自己更新。
            WriteField(m_targetBody, "<StandingOnVelocity>k__BackingField",
                m_sourceBody.StandingOnVelocity, typeof(ComponentBody));
            // 物理已停：immersion 不再由 ComponentBody.Update 计算。复制来源的浸没状态，
            // 让 ComponentOnFire（水/岩浆判定）与任何只读 body 状态的显示都跟来源一致。
            WriteField(m_targetBody, "<ImmersionDepth>k__BackingField",
                m_sourceBody.ImmersionDepth, typeof(ComponentBody));
            WriteField(m_targetBody, "<ImmersionFactor>k__BackingField",
                m_sourceBody.ImmersionFactor, typeof(ComponentBody));
            WriteField(m_targetBody, "<ImmersionFluidBlock>k__BackingField",
                m_sourceBody.ImmersionFluidBlock, typeof(ComponentBody));
        }

        private void ApplyVitals()
        {
            float health = m_sourceHealth.Health;
            float air = m_sourceHealth.Air;
            float airCapacity = m_sourceHealth.AirCapacity;
            RequireFinite(health, "来源 Health");
            RequireFinite(air, "来源 Air");
            RequireFinite(airCapacity, "来源 AirCapacity");
            float safeHealth = MathUtils.Saturate(health);
            float safeAir = MathUtils.Saturate(air);
            WriteField(m_targetHealth, "<Health>k__BackingField", safeHealth,
                typeof(ComponentHealth));
            WriteField(m_targetHealth, "<Air>k__BackingField", safeAir, typeof(ComponentHealth));
            m_targetHealth.AirCapacity = MathUtils.Max(0.1f, airCapacity);
            // 不写死亡分支：H3 的 ComponentHealth.Update 已摘掉，Health=0 只会让模型进入倒地姿势。
            WriteField(m_targetHealth, "m_lastHealth", safeHealth, typeof(ComponentHealth));

            float food = RequireFinite(m_sourceVitals.Food, "来源 Food");
            float stamina = RequireFinite(m_sourceVitals.Stamina, "来源 Stamina");
            float sleep = RequireFinite(m_sourceVitals.Sleep, "来源 Sleep");
            float temperature = RequireFinite(m_sourceVitals.Temperature, "来源 Temperature");
            float wetness = RequireFinite(m_sourceVitals.Wetness, "来源 Wetness");
            float targetTemperature = RequireFinite(
                GetField<float>(m_sourceVitals, "m_targetTemperature", typeof(ComponentVitalStats)),
                "来源 TargetTemperature");
            float safeFood = MathUtils.Saturate(food);
            float safeStamina = MathUtils.Saturate(stamina);
            float safeSleep = MathUtils.Saturate(sleep);
            float safeTemperature = MathUtils.Clamp(temperature, 0f, 24f);
            float safeWetness = MathUtils.Saturate(wetness);
            float safeTargetTemperature = MathUtils.Clamp(targetTemperature, 0f, 24f);
            WriteField(m_targetVitals, "m_food", safeFood, typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_stamina", safeStamina, typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_sleep", safeSleep, typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_temperature", safeTemperature,
                typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_wetness", safeWetness, typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_targetTemperature", safeTargetTemperature,
                typeof(ComponentVitalStats));
            // 这些"上一次值"是引擎跨阈值提示的边沿判据；一起写成当前值，HUD 不会出现假提示。
            WriteField(m_targetVitals, "m_lastFood", safeFood, typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_lastStamina", safeStamina, typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_lastSleep", safeSleep, typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_lastTemperature", safeTemperature,
                typeof(ComponentVitalStats));
            WriteField(m_targetVitals, "m_lastWetness", safeWetness, typeof(ComponentVitalStats));

            if (m_sourceFlu != null && m_targetFlu != null)
            {
                WriteField(m_targetFlu, "m_fluOnset",
                    MathUtils.Max(0f, GetField<float>(m_sourceFlu, "m_fluOnset",
                        typeof(ComponentFlu))), typeof(ComponentFlu));
                WriteField(m_targetFlu, "m_fluDuration",
                    MathUtils.Max(0f, GetField<float>(m_sourceFlu, "m_fluDuration",
                        typeof(ComponentFlu))), typeof(ComponentFlu));
                WriteField(m_targetFlu, "m_coughDuration",
                    MathUtils.Max(0f, GetField<float>(m_sourceFlu, "m_coughDuration",
                        typeof(ComponentFlu))), typeof(ComponentFlu));
                WriteField(m_targetFlu, "m_sneezeDuration",
                    MathUtils.Max(0f, GetField<float>(m_sourceFlu, "m_sneezeDuration",
                        typeof(ComponentFlu))), typeof(ComponentFlu));
            }
            if (m_sourceSickness != null && m_targetSickness != null)
            {
                WriteField(m_targetSickness, "m_sicknessDuration",
                    MathUtils.Max(0f, GetField<float>(m_sourceSickness, "m_sicknessDuration",
                        typeof(ComponentSickness))), typeof(ComponentSickness));
            }
            if (m_sourceOnFire != null && m_targetOnFire != null)
            {
                float fireDuration = m_sourceOnFire.IsOnFire
                    ? MathUtils.Max(0f, GetField<float>(m_sourceOnFire, "m_fireDuration",
                        typeof(ComponentOnFire)))
                    : 0f;
                WriteField(m_targetOnFire, "m_fireDuration", fireDuration,
                    typeof(ComponentOnFire));
            }
        }

        private void ApplyInventory(out int slotsChanged)
        {
            slotsChanged = 0;
            IInventory sourceInventory = m_sourceMiner.Inventory;
            IInventory targetInventory = m_targetMiner.Inventory;
            if (sourceInventory == null || targetInventory == null)
                throw new InvalidOperationException("来源/目标没有 ComponentMiner.Inventory");

            int slots = Math.Min(sourceInventory.SlotsCount, targetInventory.SlotsCount);
            bool creative = sourceInventory is ComponentCreativeInventory ||
                targetInventory is ComponentCreativeInventory;
            if (creative)
            {
                slots = Math.Min(slots, CreativeMirrorSlots);
                InfoOnce("creative-inventory-cap",
                    "创造模式背包只镜像前 " + CreativeMirrorSlots +
                    " 槽（快捷栏；与既有 PlayerInventory 同步范围一致，创造仓库其余槽位不是实时数据）");
            }
            for (int i = 0; i < slots; i++)
            {
                int value = sourceInventory.GetSlotValue(i);
                int count = sourceInventory.GetSlotCount(i);
                if (count < 0 || (count > 0 && value == 0))
                {
                    WarnOnce("slot-invalid:" + i,
                        "来源背包槽 " + i + " 的值非法（value=" + value + " count=" + count +
                        "）→ 跳过该槽（其余槽照常镜像）");
                    continue;
                }
                if (targetInventory.GetSlotValue(i) == value &&
                    targetInventory.GetSlotCount(i) == count)
                    continue;
                if (!TrySetSlot(targetInventory, i, value, count, out string slotReason))
                {
                    WarnOnce("slot-refused:" + i,
                        "目标背包槽 " + i + " 无法写入镜像值（" + slotReason +
                        "）→ 跳过该槽（其余槽照常镜像）");
                    continue;
                }
                slotsChanged++;
            }
            int active = sourceInventory.ActiveSlotIndex;
            if (active >= 0 && active < targetInventory.SlotsCount)
                targetInventory.ActiveSlotIndex = active;

            if (m_sourceClothing != null && m_targetClothing != null)
            {
                slotsChanged += CopyClothes(m_sourceClothing, m_targetClothing);
                // 衣物 updateable 已摘掉（避免磨损/掉落），贴图刷新由这里补：方法内部自带 valid 检查。
                m_clothingTexturesMethod.Invoke(m_targetClothing, null);
            }
        }

        private void ApplyActions()
        {
            float poking = m_sourceMiner.PokingPhase;
            RequireFinite(poking, "来源 PokingPhase");
            WriteField(m_targetMiner, "<PokingPhase>k__BackingField",
                MathUtils.Clamp(poking, 0f, 1f), typeof(ComponentMiner));

            object boxedFace = m_parentField.GetParentField(m_sourceMiner,
                "<DigCellFace>k__BackingField", typeof(ComponentMiner));
            if (boxedFace is CellFace face)
            {
                WriteField(m_targetMiner, "<DigCellFace>k__BackingField", face,
                    typeof(ComponentMiner));
                WriteField(m_targetMiner, "m_digProgress",
                    MathUtils.Clamp(m_sourceMiner.DigProgress, 0f, 1f), typeof(ComponentMiner));
                WriteField(m_targetMiner, "m_lastDigFrameIndex", Time.FrameIndex,
                    typeof(ComponentMiner));
                SubsystemTime clock = m_targetMiner.Project?.FindSubsystem<SubsystemTime>(false);
                if (clock == null)
                {
                    WarnOnce("dig-no-clock",
                        "目标世界找不到 SubsystemTime → H3 的挖掘裂纹 DigTime 取不到（其余动作不受影响）");
                }
                else
                {
                    WriteField(m_targetMiner, "m_digStartTime", clock.GameTime - 1.0,
                        typeof(ComponentMiner));
                }
            }
            else
            {
                WriteField(m_targetMiner, "<DigCellFace>k__BackingField", null,
                    typeof(ComponentMiner));
                WriteField(m_targetMiner, "m_digProgress", 0f, typeof(ComponentMiner));
            }

            if (m_sourceHuman != null && m_targetHuman != null)
            {
                WriteField(m_targetHuman, "m_punchFactor", GetField<float>(m_sourceHuman,
                    "m_punchFactor", typeof(ComponentHumanModel)), typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_punchPhase", GetField<float>(m_sourceHuman,
                    "m_punchPhase", typeof(ComponentHumanModel)), typeof(ComponentHumanModel));
                // 源 HumanModel.Update 在本帧已经消费过一次 order 并把结果放进 m_*；
                // 本帧模块 tick 刚写入的新 order 更"新"（否则 H3 的瞄准/划船姿势会慢一帧）。
                float aimHandAngle = GetField<float>(m_sourceHuman, "m_aimHandAngle",
                    typeof(ComponentHumanModel));
                if (MathUtils.Abs(m_sourceModel.AimHandAngleOrder) > 0.0001f)
                    aimHandAngle = m_sourceModel.AimHandAngleOrder;
                Vector3 inHandOffset = GetField<Vector3>(m_sourceHuman, "m_inHandItemOffset",
                    typeof(ComponentHumanModel));
                if (m_sourceModel.InHandItemOffsetOrder != Vector3.Zero)
                    inHandOffset = m_sourceModel.InHandItemOffsetOrder;
                Vector3 inHandRotation = GetField<Vector3>(m_sourceHuman, "m_inHandItemRotation",
                    typeof(ComponentHumanModel));
                if (m_sourceModel.InHandItemRotationOrder != Vector3.Zero)
                    inHandRotation = m_sourceModel.InHandItemRotationOrder;
                bool rowLeft = GetField<bool>(m_sourceHuman, "m_rowLeft",
                    typeof(ComponentHumanModel)) || m_sourceModel.RowLeftOrder;
                bool rowRight = GetField<bool>(m_sourceHuman, "m_rowRight",
                    typeof(ComponentHumanModel)) || m_sourceModel.RowRightOrder;
                WriteField(m_targetHuman, "m_aimHandAngle", aimHandAngle,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_inHandItemOffset", inHandOffset,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_inHandItemRotation", inHandRotation,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_rowLeft", rowLeft, typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_rowRight", rowRight, typeof(ComponentHumanModel));
            }

            WriteField(m_targetModel, "m_injuryColorFactor", GetField<float>(m_sourceModel,
                "m_injuryColorFactor", typeof(ComponentCreatureModel)),
                typeof(ComponentCreatureModel));
            WriteField(m_targetModel, "<MovementAnimationPhase>k__BackingField",
                m_sourceModel.MovementAnimationPhase, typeof(ComponentCreatureModel));
            WriteField(m_targetModel, "<Bob>k__BackingField", m_sourceModel.Bob,
                typeof(ComponentCreatureModel));
            WriteField(m_targetModel, "<DeathPhase>k__BackingField", m_sourceModel.DeathPhase,
                typeof(ComponentCreatureModel));
            m_targetModel.FeedOrder = m_sourceModel.FeedOrder;
            m_targetModel.RowLeftOrder = m_sourceModel.RowLeftOrder;
            m_targetModel.RowRightOrder = m_sourceModel.RowRightOrder;
            m_targetModel.AttackOrder = m_sourceModel.AttackOrder;
            m_targetModel.AimHandAngleOrder = m_sourceModel.AimHandAngleOrder;
            m_targetModel.InHandItemOffsetOrder = m_sourceModel.InHandItemOffsetOrder;
            m_targetModel.InHandItemRotationOrder = m_sourceModel.InHandItemRotationOrder;
            m_targetLocomotion.IsCreativeFlyEnabled = m_sourceLocomotion.IsCreativeFlyEnabled;
        }

        // ------------------------------------------------------------------ 快照 / 恢复

        private MirrorRestoreOutcome RestoreInternal()
        {
            MirrorCharacterSnapshot snapshot = m_snapshot;
            if (snapshot == null)
                throw new InvalidOperationException("没有绑定前快照，无法写回 H3 的状态");

            bool viewRestored = false;
            // 0) replace：相机先回 H3 自己（ActiveCamera / Target 都是视图状态，和引擎字段分开恢复）。
            if (m_plan.BindView) RestoreHudSources();
            if (m_plan.BindView) viewRestored = RestoreView();

            // 1) 引擎字段（位置先写：SubsystemBodies.AddBody 会按当前位置登记）。
            if (m_targetBody != null)
            {
                m_targetBody.Position = snapshot.Position;
                m_targetBody.Velocity = snapshot.Velocity;
                WriteField(m_targetBody, "<StandingOnVelocity>k__BackingField",
                    snapshot.StandingOnVelocity, typeof(ComponentBody));
                m_targetBody.Rotation = snapshot.Rotation;
                m_targetBody.TargetCrouchFactor = snapshot.TargetCrouchFactor;
                WriteField(m_targetBody, "m_crouchFactor", snapshot.CrouchFactor,
                    typeof(ComponentBody));
                m_targetBody.IsGravityEnabled = snapshot.IsGravityEnabled;
                WriteField(m_targetBody, "<ImmersionDepth>k__BackingField",
                    snapshot.ImmersionDepth, typeof(ComponentBody));
                WriteField(m_targetBody, "<ImmersionFactor>k__BackingField",
                    snapshot.ImmersionFactor, typeof(ComponentBody));
                WriteField(m_targetBody, "<ImmersionFluidBlock>k__BackingField",
                    snapshot.ImmersionFluidBlock, typeof(ComponentBody));
            }
            if (m_plan.CopyState && m_targetLocomotion != null)
            {
                WriteField(m_targetLocomotion, "m_lookAngles", snapshot.LookAngles,
                    typeof(ComponentLocomotion));
                m_targetLocomotion.IsCreativeFlyEnabled = snapshot.IsCreativeFlyEnabled;
            }
            if (m_targetInput != null && m_inputRef != null)
                SetTargetInput(snapshot.PlayerInput);
            if (m_targetPlayer != null)
                m_parentField.ModifyParentField(m_targetPlayer, "m_aim", snapshot.PlayerAim,
                    typeof(ComponentPlayer));
            if (m_targetGameWidget != null)
                m_targetGameWidget.IsHitTestVisible = snapshot.GameWidgetHitTestVisible;
            if (!m_plan.CopyState)
            {
                // replace 只改了"身体在世界里的存在方式"（碰撞 / 模型渲染 / updateable / 相机），
                // 背包 / 生命 / 体力 / 衣物 / 动作从来没人动过 —— 不写回这些字段，
                // 避免让 H3 的原值经过一次"快照回写"（那是 copy 模式才需要的事）。
                return FinishRestore(snapshot, viewRestored, bodyRestored: false);
            }
            if (m_targetHealth != null)
            {
                WriteField(m_targetHealth, "<Health>k__BackingField", snapshot.Health,
                    typeof(ComponentHealth));
                WriteField(m_targetHealth, "<Air>k__BackingField", snapshot.Air,
                    typeof(ComponentHealth));
                WriteField(m_targetHealth, "m_lastHealth", snapshot.LastHealth,
                    typeof(ComponentHealth));
                m_targetHealth.AirCapacity = snapshot.AirCapacity;
            }
            if (m_targetVitals != null)
            {
                WriteField(m_targetVitals, "m_food", snapshot.Food, typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_stamina", snapshot.Stamina,
                    typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_sleep", snapshot.Sleep, typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_temperature", snapshot.Temperature,
                    typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_wetness", snapshot.Wetness,
                    typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_targetTemperature", snapshot.TargetTemperature,
                    typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_lastFood", snapshot.LastFood,
                    typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_lastStamina", snapshot.LastStamina,
                    typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_lastSleep", snapshot.LastSleep,
                    typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_lastTemperature", snapshot.LastTemperature,
                    typeof(ComponentVitalStats));
                WriteField(m_targetVitals, "m_lastWetness", snapshot.LastWetness,
                    typeof(ComponentVitalStats));
            }
            if (m_targetFlu != null)
            {
                WriteField(m_targetFlu, "m_fluOnset", snapshot.FluOnset, typeof(ComponentFlu));
                WriteField(m_targetFlu, "m_fluDuration", snapshot.FluDuration, typeof(ComponentFlu));
                WriteField(m_targetFlu, "m_coughDuration", snapshot.CoughDuration,
                    typeof(ComponentFlu));
                WriteField(m_targetFlu, "m_sneezeDuration", snapshot.SneezeDuration,
                    typeof(ComponentFlu));
            }
            if (m_targetSickness != null)
                WriteField(m_targetSickness, "m_sicknessDuration", snapshot.SicknessDuration,
                    typeof(ComponentSickness));
            if (m_targetOnFire != null)
                WriteField(m_targetOnFire, "m_fireDuration", snapshot.FireDuration,
                    typeof(ComponentOnFire));
            if (m_targetMiner != null)
            {
                WriteField(m_targetMiner, "<PokingPhase>k__BackingField", snapshot.PokingPhase,
                    typeof(ComponentMiner));
                WriteField(m_targetMiner, "<DigCellFace>k__BackingField", snapshot.DigCellFace,
                    typeof(ComponentMiner));
                WriteField(m_targetMiner, "m_digProgress", snapshot.DigProgress,
                    typeof(ComponentMiner));
                WriteField(m_targetMiner, "m_digStartTime", snapshot.DigStartTime,
                    typeof(ComponentMiner));
                WriteField(m_targetMiner, "m_lastDigFrameIndex", snapshot.LastDigFrameIndex,
                    typeof(ComponentMiner));
            }
            if (m_targetHuman != null)
            {
                WriteField(m_targetHuman, "m_punchFactor", snapshot.PunchFactor,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_punchPhase", snapshot.PunchPhase,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_aimHandAngle", snapshot.AimHandAngle,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_inHandItemOffset", snapshot.InHandItemOffset,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_inHandItemRotation", snapshot.InHandItemRotation,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_rowLeft", snapshot.RowLeft,
                    typeof(ComponentHumanModel));
                WriteField(m_targetHuman, "m_rowRight", snapshot.RowRight,
                    typeof(ComponentHumanModel));
            }
            if (m_targetModel != null)
            {
                WriteField(m_targetModel, "m_injuryColorFactor", snapshot.InjuryColorFactor,
                    typeof(ComponentCreatureModel));
                WriteField(m_targetModel, "<MovementAnimationPhase>k__BackingField",
                    snapshot.MovementAnimationPhase, typeof(ComponentCreatureModel));
                WriteField(m_targetModel, "<Bob>k__BackingField", snapshot.Bob,
                    typeof(ComponentCreatureModel));
                WriteField(m_targetModel, "<DeathPhase>k__BackingField", snapshot.DeathPhase,
                    typeof(ComponentCreatureModel));
                m_targetModel.FeedOrder = snapshot.FeedOrder;
                m_targetModel.RowLeftOrder = snapshot.RowLeftOrder;
                m_targetModel.RowRightOrder = snapshot.RowRightOrder;
                m_targetModel.AttackOrder = snapshot.AttackOrder;
                m_targetModel.AimHandAngleOrder = snapshot.AimHandAngleOrder;
                m_targetModel.InHandItemOffsetOrder = snapshot.InHandItemOffsetOrder;
                m_targetModel.InHandItemRotationOrder = snapshot.InHandItemRotationOrder;
            }
            RestoreInventory(snapshot);
            RestoreClothes(snapshot);
            if (m_targetClothing != null)
                m_clothingTexturesMethod.Invoke(m_targetClothing, null);
            // 衣物 SetClothes 会按衣物密度差调整 ComponentBody.Density；写在它之后才是快照真值。
            if (m_targetBody != null) m_targetBody.Density = snapshot.Density;
            return FinishRestore(snapshot, viewRestored, bodyRestored: false);
        }

        /// <summary>
        /// 恢复引擎注册（两种模式共用）：先把身体登记回 SubsystemBodies（使用已写回的位置），
        /// 再恢复 updateable；replace 模式额外把 H3 的模型重新加回渲染表。
        /// </summary>
        private MirrorRestoreOutcome FinishRestore(MirrorCharacterSnapshot snapshot, bool viewRestored,
            bool bodyRestored)
        {
            Project project = m_project ?? GameManager.Project;
            SubsystemBodies bodies = project?.FindSubsystem<SubsystemBodies>(false);
            if (m_bodyRemoved && m_targetBody != null)
            {
                if (bodies == null)
                    throw new InvalidOperationException("找不到 SubsystemBodies，无法把 H3 加回碰撞集合");
                m_addBodyMethod.Invoke(bodies, new object[] { m_targetBody });
                m_bodyRemoved = false;
            }
            SubsystemUpdate update = project?.FindSubsystem<SubsystemUpdate>(false);
            if (update != null)
            {
                for (int i = 0; i < m_removedUpdateables.Count; i++)
                    update.AddUpdateable(m_removedUpdateables[i]);
            }
            m_removedUpdateables.Clear();
            // replace：模型重新加回 SubsystemModelsRenderer 的渲染表（下一帧起照常渲染 / 投影）。
            if (m_plan.HideBody) bodyRestored = RestoreTargetModel();
            UnsubscribeProjectDisposed();
            Log("mirrorRestored：已把本地角色「" + m_characterName + "」写回绑定前快照（mirrorMode=" +
                MirrorCharacterModes.Name(m_plan.Mode) + " · 位置 / 输入 / updateable / 碰撞" +
                (m_plan.CopyState ? " / 生命 / 体力 / 背包 / 衣物 / 动作状态" : "") +
                (m_plan.BindView ? " / 相机回 H3 自己 / 模型恢复渲染" : "") + "）");
            return new MirrorRestoreOutcome(restored: true, viewRestored, bodyRestored);
        }

        // ------------------------------------------------------------------ replace：相机 / 模型

        /// <summary>
        /// 把 H3 的模型从渲染表里摘掉 —— 这是"身体留在原地但不参与世界表现"的实现：
        ///   · 模型不渲染（任何视图都看不到），阴影也不会排队；
        ///   · 不从世界挪走坐标 → 主机广播给客户端的主机角色位置 / 存档 / 实时地图都不受影响；
        ///   · 碰撞已在调用方通过 SubsystemBodies.RemoveBody 摘掉，updateable 也已停掉。
        /// 渲染开关的唯一事实来源是 <c>SubsystemModelsRenderer.m_componentModels</c>：
        /// 绘制入口只遍历它（Source: Survivalcraft/Game/SubsystemModelsRenderer.cs:66-88），
        /// 实体加入 / 移除时由 OnEntityAdded / OnEntityRemoved 维护（:139-159）。
        /// </summary>
        private void HideTargetModel(Project project)
        {
            Entity entity = m_targetPlayer?.Entity;
            if (entity == null)
                throw new InvalidOperationException("H3 没有实体，无法隐藏模型");
            SubsystemModelsRenderer renderer = project.FindSubsystem<SubsystemModelsRenderer>(false);
            if (renderer == null)
                throw new InvalidOperationException(
                    "世界里找不到 SubsystemModelsRenderer（无法让 H3 的模型不再渲染）");
            if (m_modelTable == null)
            {
                object table;
                try
                {
                    table = m_parentField.GetParentField(renderer, "m_componentModels",
                        typeof(SubsystemModelsRenderer));
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        "读取 SubsystemModelsRenderer.m_componentModels 失败（引擎版本不匹配）：" +
                        e.GetType().Name + " " + e.Message);
                }
                if (table == null)
                    throw new InvalidOperationException(
                        "SubsystemModelsRenderer.m_componentModels 为 null（引擎状态异常）");
                Type tableType = table.GetType();
                MethodInfo tryGet = FindInstanceMethod(tableType, "TryGetValue", 2);
                MethodInfo remove = FindInstanceMethod(tableType, "Remove", 1);
                MethodInfo add = FindInstanceMethod(tableType, "Add", 2);
                if (tryGet == null || remove == null || add == null)
                {
                    throw new InvalidOperationException(
                        "SubsystemModelsRenderer.m_componentModels 不是预期的 " +
                        "Dictionary<ComponentModel, ModelData>（实际 " + tableType +
                        "）：无法在不动坐标的前提下隐藏 H3 的模型");
                }
                m_modelTable = table;
                m_modelTableTryGet = tryGet;
                m_modelTableRemove = remove;
                m_modelTableAdd = add;
            }
            foreach (ComponentModel model in entity.FindComponents<ComponentModel>())
            {
                if (model == null) continue;
                object[] args = { model, null };
                bool present = (bool)m_modelTableTryGet.Invoke(m_modelTable, args);
                if (!present)
                {
                    throw new InvalidOperationException(
                        "H3 的模型组件 " + model.GetType().Name +
                        " 不在 SubsystemModelsRenderer.m_componentModels 里（引擎状态异常），" +
                        "无法确认它不会渲染");
                }
                m_hiddenModels.Add(model);
                m_hiddenModelData.Add(args[1]);
                m_modelTableRemove.Invoke(m_modelTable, new object[] { model });
            }
            if (m_hiddenModels.Count == 0)
                throw new InvalidOperationException("H3 实体上没有任何 ComponentModel（无从隐藏模型）");
            m_bodyHidden = true;
            Log("H3「" + m_characterName + "」的模型已从渲染表摘除（" + m_hiddenModels.Count +
                " 个 ComponentModel）：身体留在原地、不渲染、不投影、不再参与碰撞");
        }

        /// <summary>把 H3 的模型加回渲染表（可重复调用；引擎已经自行加回时不重复 Add）。</summary>
        private bool RestoreTargetModel()
        {
            if (m_hiddenModels.Count == 0)
            {
                m_bodyHidden = false;
                return false;
            }
            if (m_modelTable != null)
            {
                for (int i = 0; i < m_hiddenModels.Count; i++)
                {
                    object[] probe = { m_hiddenModels[i], null };
                    bool present = (bool)m_modelTableTryGet.Invoke(m_modelTable, probe);
                    if (present) continue;
                    m_modelTableAdd.Invoke(m_modelTable,
                        new object[] { m_hiddenModels[i], m_hiddenModelData[i] });
                }
            }
            m_hiddenModels.Clear();
            m_hiddenModelData.Clear();
            m_bodyHidden = false;
            return true;
        }

        /// <summary>
        /// replace：把 H3 的 GameWidget 相机绑到客户端角色的 ComponentPlayer。
        /// 引擎读的两处事实：
        ///   · <c>ViewWidget.Draw</c> 只用 <c>GameWidget.PlayerData.ComponentPlayer != null &amp;&amp;
        ///     IsReadyForPlaying</c> 决定画不画（Source: Survivalcraft/Game/ViewWidget.cs:45-51）
        ///     —— H3 自己的 PlayerData 一直活着，所以这个视图照常画；
        ///   · <c>FppCamera.Update</c> 每帧读 <c>GameWidget.Target.ComponentCreatureModel.EyePosition /
        ///     EyeRotation</c>（Source: Survivalcraft/Game/FppCamera.cs:21-44）—— Target 换成客户端的
        ///     ComponentPlayer 后，这个视图就是客户端的第一人称。
        /// 与 <c>Engine/DebugMirrorClient.cs:275-280</c> 完全同一条已证明路径。
        /// </summary>
        private void BindViewToSource()
        {
            GameWidget widget = m_targetGameWidget;
            if (widget == null)
                throw new InvalidOperationException("本机角色没有 GameWidget，第三分屏无法显示客户端角色");
            if (m_sourcePlayer == null || m_sourcePlayer.ComponentCreatureModel == null)
                throw new InvalidOperationException(
                    "客户端的 ComponentPlayer / 模型已失效，第三分屏相机无法绑定");
            FppCamera fpp = widget.FindCamera<FppCamera>(false);
            if (fpp == null)
                throw new InvalidOperationException("第三分屏的 GameWidget 没有 FppCamera（引擎版本不匹配）");
            string previous = widget.ActiveCamera == null
                ? "null"
                : widget.ActiveCamera.GetType().Name;
            widget.Target = m_sourcePlayer;
            widget.ActiveCamera = fpp;
            // 立刻更新一次相机，避免本帧 Draw 用到上一台相机留下的朝向。
            widget.ActiveCamera.Update(0f);
            if (widget.ViewWidget != null) widget.ViewWidget.IsHitTestVisible = false;
            m_viewBound = true;
            BindHudSourcesToSource();
            Log("第三分屏相机已绑定客户端 " + m_clientId + "：GameWidget#" + widget.GameWidgetIndex +
                " · Target=客户端 ComponentPlayer · ActiveCamera=FppCamera（原相机 " + previous +
                "）· 视图不接受命中测试");
        }

        /// <summary>
        /// replace：把第三分屏的 **HUD 数据源**从宿主切到客户端化身。引擎的快捷栏是
        /// `ShortInventoryWidget.AssignComponents(m_componentPlayer.ComponentMiner.Inventory)`
        /// （Source: Survivalcraft/Game/ComponentGui.cs:281），蹲伏按钮读
        /// `m_componentPlayer.ComponentBody.TargetCrouchFactor`（Source: ComponentGui.cs:428）——
        /// 这两处都指"视图自己的角色"，只换相机不会跟着客户端。这里只改**显示层**：
        ///   · 快捷栏改绑客户端化身的背包（客户端背包本来就通过 PLAYER_INVENTORY 同步到该化身）；
        ///   · 蹲伏状态每拍从客户端化身复制（Tick 里做）。
        /// 不写任何权威字段、不改宿主存档（解绑时改回宿主背包）。
        /// </summary>
        private void BindHudSourcesToSource()
        {
            if (m_targetPlayer == null || m_sourcePlayer == null) return;
            try
            {
                ComponentGui targetGui = m_targetPlayer.ComponentGui;
                ComponentMiner sourceMiner = m_sourcePlayer.ComponentMiner;
                if (targetGui?.ShortInventoryWidget != null && sourceMiner?.Inventory != null)
                {
                    targetGui.ShortInventoryWidget.AssignComponents(sourceMiner.Inventory);
                    m_inventoryViewBound = true;
                    Log("第三分屏快捷栏已改绑客户端背包（显示层；解绑时改回宿主背包）");
                }
                else
                {
                    m_diagnostics?.Warn(OwnerId,
                        "第三分屏快捷栏改绑失败：宿主 Gui / ShortInventoryWidget 或客户端背包不可用" +
                        "（第三屏快捷栏仍显示宿主自己背包的空槽）");
                }
            }
            catch (Exception e)
            {
                m_diagnostics?.Error(OwnerId, "第三分屏 HUD 数据源桥接异常：" +
                    e.GetType().Name + " " + e.Message, e);
            }
        }

        /// <summary>replace：每拍把客户端化身的蹲伏表现映射到宿主（只写显示相关字段）。</summary>
        private void TickHudBridge()
        {
            if (m_targetPlayer == null || m_sourcePlayer == null) return;
            ComponentBody targetBody = m_targetPlayer.ComponentBody;
            ComponentBody sourceBody = m_sourcePlayer.ComponentBody;
            if (targetBody == null || sourceBody == null) return;
            try
            {
                // 蹲伏按钮 = IsChecked(TargetCrouchFactor > 0)（ComponentGui.cs:428）；宿主身体在绑定期间
                // 不参与更新，所以这里每拍跟随客户端，避免按钮永远不亮。
                // 只写可写属性 TargetCrouchFactor（CrouchFactor 是只读的，由身体自己按目标值推进；
                // 宿主身体在绑定期间不参与更新，但按钮只看 TargetCrouchFactor > 0，Source: ComponentGui.cs:428）。
                float crouch = sourceBody.TargetCrouchFactor;
                targetBody.TargetCrouchFactor = crouch;
                m_crouchBridged = true;

                // —— 生命 / 饱食 / 温度：把客户端化身的权威值灌进宿主组件，让引擎自己写 HUD。
                // 宿主这两个组件的 Update 保留（见绑定处），所以 HUD 只有一个写入方 → 不会抖动。
                // 同时把宿主标记为无敌，避免它自己受伤/饿死/冻死（数值每拍都会被拉回客户端值）。
                ComponentHealth srcHealth = m_sourcePlayer.ComponentHealth;
                ComponentHealth dstHealth = m_targetPlayer.ComponentHealth;
                if (srcHealth != null && dstHealth != null)
                {
                    m_parentField.ModifyParentField(dstHealth, "<IsInvulnerable>k__BackingField", true,
                        typeof(ComponentHealth));
                    WriteHealth(dstHealth, srcHealth.Health, srcHealth.Air);
                }
                ComponentVitalStats srcVitals = m_sourcePlayer.ComponentVitalStats;
                ComponentVitalStats dstVitals = m_targetPlayer.ComponentVitalStats;
                if (srcVitals != null && dstVitals != null)
                {
                    WriteVitals(dstVitals, srcVitals);
                }
            }
            catch (Exception)
            {
                // 表现层失败不影响相机绑定；解绑日志里会体现（不静默：计数在 failures 里）
            }
        }

        /// <summary>把生命/空气写入宿主的 ComponentHealth（私有 backing field；引擎随后自己刷血条）。</summary>
        private void WriteHealth(ComponentHealth target, float health, float air)
        {
            if (target == null) return;
            if (!float.IsFinite(health) || !float.IsFinite(air)) return;
            try
            {
                // Health 是 private set（ComponentHealth.cs:40）→ 写 backing field；
                // 同时同步 m_lastHealth，避免引擎把它当成一次"掉血边沿"而触发红屏/音效。
                m_parentField.ModifyParentField(target, "<Health>k__BackingField", MathUtils.Clamp(health, 0f, 1f), typeof(ComponentHealth));
                m_parentField.ModifyParentField(target, "m_lastHealth", MathUtils.Clamp(health, 0f, 1f), typeof(ComponentHealth));
                m_parentField.ModifyParentField(target, "<Air>k__BackingField", MathUtils.Clamp(air, 0f, 1f), typeof(ComponentHealth));
            }
            catch (Exception)
            {
                // 表现层失败不影响相机；由调用方的计数/日志体现（不静默吞掉权威逻辑）。
            }
        }

        /// <summary>把客户端的饱食/体力/睡眠/体温/湿度写入宿主 ComponentVitalStats（引擎随后自己刷 HUD）。</summary>
        private void WriteVitals(ComponentVitalStats target, ComponentVitalStats source)
        {
            if (target == null || source == null) return;
            try
            {
                m_parentField.ModifyParentField(target, "m_food",
                    MathUtils.Clamp(source.Food, 0f, 1f), typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(target, "m_stamina",
                    MathUtils.Clamp(source.Stamina, 0f, 1f), typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(target, "m_sleep",
                    MathUtils.Clamp(source.Sleep, 0f, 1f), typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(target, "m_temperature",
                    MathUtils.Clamp(source.Temperature, 0f, 24f), typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(target, "m_wetness",
                    MathUtils.Clamp(source.Wetness, 0f, 1f), typeof(ComponentVitalStats));
                // 温度档位贴图由 m_targetTemperature 决定（ComponentVitalStats.cs:589-616）；
                // 该属性是 private（ComponentVitalStats.cs:62），用反射读后再写。
                float targetTemperature;
                try
                {
                    targetTemperature = m_parentField.GetParentField<float>(source,
                        "m_targetTemperature", typeof(ComponentVitalStats));
                }
                catch (Exception)
                {
                    targetTemperature = source.Temperature;
                }
                m_parentField.ModifyParentField(target, "m_targetTemperature",
                    MathUtils.Clamp(targetTemperature, 0f, 24f), typeof(ComponentVitalStats));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>replace：解绑时把快捷栏改回宿主自己的背包 + 把蹲伏按钮的目标值交还给宿主身体。</summary>
        private void RestoreHudSources()
        {
            if (!m_inventoryViewBound && !m_crouchBridged) return;
            try
            {
                ComponentGui targetGui = m_targetPlayer?.ComponentGui;
                ComponentMiner targetMiner = m_targetPlayer?.ComponentMiner;
                if (m_inventoryViewBound && targetGui?.ShortInventoryWidget != null &&
                    targetMiner?.Inventory != null)
                    targetGui.ShortInventoryWidget.AssignComponents(targetMiner.Inventory);
            }
            catch (Exception e)
            {
                m_diagnostics?.Warn(OwnerId, "解绑时恢复第三分屏快捷栏失败（显示层）：" +
                    e.GetType().Name + " " + e.Message);
            }
            m_inventoryViewBound = false;
            if (m_crouchBridged)
            {
                // 绑定期间宿主身体不参与更新，蹲伏目标值被我们每拍写成客户端的状态；
                // 解绑后必须交还（否则按钮永远亮着/灭着，宿主也无法再自己蹲伏）。
                try
                {
                    ComponentBody targetBody = m_targetPlayer?.ComponentBody;
                    if (targetBody != null) targetBody.TargetCrouchFactor = 0f;
                }
                catch (Exception e)
                {
                    m_diagnostics?.Warn(OwnerId, "解绑时恢复第三分屏蹲伏按钮失败（显示层）：" +
                        e.GetType().Name + " " + e.Message);
                }
                m_crouchBridged = false;
            }
        }

        /// <summary>replace：相机回到 H3 自己（Target / ActiveCamera / 命中测试），返回是否真的恢复过。</summary>
        private bool RestoreView()
        {
            if (!m_viewBound) return false;
            GameWidget widget = m_targetGameWidget;
            MirrorCharacterSnapshot snapshot = m_snapshot;
            if (widget == null)
            {
                m_viewBound = false;
                return false;
            }
            Camera camera = snapshot == null ? null : snapshot.ActiveCamera;
            if (camera == null || !ReferenceEquals(camera.GameWidget, widget))
            {
                // ActiveCamera 的 setter 只接受"属于这个 GameWidget"的相机；绑定前的相机已经不可用时
                // 明确用 FppCamera 恢复（H3 自己的第一人称），并在日志里写清原因。
                camera = widget.FindCamera<FppCamera>(false);
                if (snapshot != null && snapshot.ActiveCamera != null)
                {
                    WarnOnce("view-camera-restore",
                        "绑定前的相机已不可用（GameWidget 不匹配 / 已被移除）→ H3 的视图用 FppCamera 恢复");
                }
            }
            if (camera != null) widget.ActiveCamera = camera;
            ComponentCreature target = m_targetPlayer ?? (snapshot == null ? null : snapshot.GameWidgetTarget);
            if (target != null) widget.Target = target;
            if (snapshot != null && widget.ViewWidget != null)
                widget.ViewWidget.IsHitTestVisible = snapshot.ViewWidgetHitTestVisible;
            widget.ActiveCamera?.Update(0f);
            m_viewBound = false;
            Log("第三分屏相机已回到 H3「" + m_characterName + "」自己：Target=H3 的 ComponentPlayer · " +
                "ActiveCamera=" + (widget.ActiveCamera == null
                    ? "null"
                    : widget.ActiveCamera.GetType().Name));
            return true;
        }

        private static MethodInfo FindInstanceMethod(Type type, string name, int parameterCount)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                if (method.GetParameters().Length == parameterCount) return method;
            }
            return null;
        }

        private void RemoveUpdateable(SubsystemUpdate update, IUpdateable updateable)
        {
            if (update == null || updateable == null) return;
            update.RemoveUpdateable(updateable);
            m_removedUpdateables.Add(updateable);
        }

        private void RestoreInventory(MirrorCharacterSnapshot snapshot)
        {
            if (m_targetMiner == null || snapshot.InventoryValues == null ||
                snapshot.InventoryCounts == null)
                return;
            IInventory inventory = m_targetMiner.Inventory;
            if (inventory == null)
                throw new InvalidOperationException("H3 没有 ComponentMiner.Inventory，无法写回背包快照");
            int slots = Math.Min(inventory.SlotsCount,
                Math.Min(snapshot.InventoryValues.Length, snapshot.InventoryCounts.Length));
            for (int i = 0; i < slots; i++)
            {
                if (!TrySetSlot(inventory, i, snapshot.InventoryValues[i],
                        snapshot.InventoryCounts[i], out string slotReason))
                {
                    WarnOnce("restore-slot:" + i,
                        "写回背包快照时槽 " + i + " 失败：" + slotReason +
                        "（该槽保持当前值；其余槽已写回）");
                }
            }
            if (snapshot.ActiveSlotIndex >= 0 && snapshot.ActiveSlotIndex < inventory.SlotsCount)
                inventory.ActiveSlotIndex = snapshot.ActiveSlotIndex;
        }

        private void RestoreClothes(MirrorCharacterSnapshot snapshot)
        {
            if (m_targetClothing == null || snapshot.Clothes == null) return;
            for (int slot = 0; slot < snapshot.Clothes.Length && slot < 4; slot++)
            {
                int[] clothes = snapshot.Clothes[slot] ?? Array.Empty<int>();
                m_targetClothing.SetClothes((ClothingSlot)slot, clothes);
            }
        }

        // ------------------------------------------------------------------ 工具

        private void SetTargetInput(PlayerInput input)
        {
            if (m_targetInput == null || m_inputRef == null)
                throw new InvalidOperationException(
                    "没有绑定 ComponentInput.m_playerInput，拒绝在未清空输入的情况下镜像（H3 可能继续响应主机输入）");
            m_inputRef(m_targetInput) = input;
        }

        private void SubscribeProjectDisposed()
        {
            if (m_projectDisposedSubscribed) return;
            GameManager.ProjectDisposed += OnProjectDisposed;
            m_projectDisposedSubscribed = true;
        }

        private void UnsubscribeProjectDisposed()
        {
            if (!m_projectDisposedSubscribed) return;
            GameManager.ProjectDisposed -= OnProjectDisposed;
            m_projectDisposedSubscribed = false;
        }

        /// <summary>
        /// 世界卸载发生在 <c>Project.Dispose</c> **之前**（GameManager.DisposeProject 先触发事件）。
        /// 在这里写回快照，保证"世界卸载"也能把 H3 恢复成绑定前状态；随后由控制器 Tick 收尾计数。
        /// </summary>
        private void OnProjectDisposed(Project project)
        {
            if (!m_bound || m_restoredDuringDispose || project == null ||
                !ReferenceEquals(project, m_project))
                return;
            try
            {
                m_restoreDuringDisposeOutcome = RestoreInternal();
                m_restoredDuringDispose = true;
                Log("mirrorRestored：世界卸载前已把本地角色「" + m_characterName +
                    "」写回绑定前快照（等待控制器解绑计数）");
            }
            catch (Exception e)
            {
                m_restoreFailureReason = e.GetType().Name + " " + e.Message;
                m_diagnostics?.Error(OwnerId,
                    "世界卸载前写回镜像角色「" + m_characterName + "」快照失败：" +
                    m_restoreFailureReason, e);
            }
        }

        private void ClearBinding()
        {
            // 第三分屏的客户端反馈镜像：写回绑定前的 HUD 显示值、清掉我们写过的覆盖层 / 死亡大消息，
            // 然后停止更新（幂等；正常解绑 / 回滚 / 世界卸载都走这里）。
            m_viewFeedback.Release("解绑 / 回滚 / 世界卸载");
            m_project = null;
            m_target = null;
            m_source = null;
            m_targetPlayer = null;
            m_sourcePlayer = null;
            m_targetBody = null;
            m_sourceBody = null;
            m_targetHealth = null;
            m_sourceHealth = null;
            m_targetVitals = null;
            m_sourceVitals = null;
            m_targetLocomotion = null;
            m_sourceLocomotion = null;
            m_targetMiner = null;
            m_sourceMiner = null;
            m_targetModel = null;
            m_sourceModel = null;
            m_targetHuman = null;
            m_sourceHuman = null;
            m_targetInput = null;
            m_targetGameWidget = null;
            m_targetFlu = null;
            m_sourceFlu = null;
            m_targetSickness = null;
            m_sourceSickness = null;
            m_targetOnFire = null;
            m_sourceOnFire = null;
            m_targetClothing = null;
            m_sourceClothing = null;
            m_snapshot = null;
            m_removedUpdateables.Clear();
            m_bodyRemoved = false;
            m_restoredDuringDispose = false;
            m_restoreDuringDisposeOutcome = default;
            m_plan = MirrorCharacterPlan.None;
            m_viewBound = false;
            m_bodyHidden = false;
            m_hiddenModels.Clear();
            m_hiddenModelData.Clear();
            m_modelTable = null;
            m_modelTableTryGet = null;
            m_modelTableRemove = null;
            m_modelTableAdd = null;
            m_clientId = -1;
            m_characterName = string.Empty;
        }

        private bool TrySetSlot(IInventory inventory, int slot, int value, int count,
            out string reason)
        {
            reason = null;
            if (inventory == null)
            {
                reason = "背包为 null";
                return false;
            }
            if (slot < 0 || slot >= inventory.SlotsCount)
            {
                reason = "槽位 " + slot + " 越界（背包 " + inventory.SlotsCount + " 槽）";
                return false;
            }
            if (count < 0 || (count > 0 && value == 0))
            {
                reason = "槽位值非法（value=" + value + " count=" + count + "）";
                return false;
            }
            if (count == 0) value = 0;
            if (count > 0)
            {
                int capacity;
                try
                {
                    capacity = inventory.GetSlotCapacity(slot, value);
                }
                catch (Exception e)
                {
                    reason = "容量查询失败：" + e.GetType().Name + " " + e.Message;
                    return false;
                }
                if (capacity <= 0)
                {
                    reason = "目标槽不接受物品（capacity=0）";
                    return false;
                }
                if (count > capacity)
                {
                    WarnOnce("slot-capacity:" + slot,
                        "槽位 " + slot + " 的数量 " + count + " 超过目标容量 " + capacity +
                        " → 按容量写入（其余槽照常镜像）");
                    count = capacity;
                }
            }
            if (inventory.GetSlotValue(slot) == value && inventory.GetSlotCount(slot) == count)
                return true;
            inventory.RemoveSlotItems(slot, int.MaxValue);
            if (count > 0) inventory.AddSlotItems(slot, value, count);
            return true;
        }

        private static int CopyClothes(ComponentClothing source, ComponentClothing target)
        {
            int changed = 0;
            for (int i = 0; i < 4; i++)
            {
                var clothes = source.GetClothes((ClothingSlot)i);
                var current = target.GetClothes((ClothingSlot)i);
                bool same = clothes.Count == current.Count;
                if (same)
                {
                    for (int j = 0; j < clothes.Count; j++)
                    {
                        if (clothes[j] == current[j]) continue;
                        same = false;
                        break;
                    }
                }
                if (same) continue;
                target.SetClothes((ClothingSlot)i, clothes);
                changed++;
            }
            return changed;
        }

        private static float ValidCrouch(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return MathUtils.Saturate(value);
        }

        private static float RequireFinite(float value, string what)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException(what + " 不是有限数（nan/inf），拒绝复制半套状态");
            return value;
        }

        private static void RequireFinite(Vector3 value, string what)
        {
            RequireFinite(value.X, what + ".X");
            RequireFinite(value.Y, what + ".Y");
            RequireFinite(value.Z, what + ".Z");
        }

        private static void RequireFinite(Quaternion value, string what)
        {
            RequireFinite(value.X, what + ".X");
            RequireFinite(value.Y, what + ".Y");
            RequireFinite(value.Z, what + ".Z");
            RequireFinite(value.W, what + ".W");
        }

        private void WriteField(object target, string field, object value, Type declaringType)
        {
            if (target == null) throw new InvalidOperationException("写入字段 " + field + " 时目标为 null");
            m_parentField.ModifyParentField(target, field, value, declaringType);
        }

        private T GetField<T>(object target, string field, Type declaringType)
        {
            if (target == null) throw new InvalidOperationException("读取字段 " + field + " 时目标为 null");
            return m_parentField.GetParentField<T>(target, field, declaringType);
        }

        private void Log(string message) => m_diagnostics?.Info(OwnerId, message);

        private void InfoOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            m_diagnostics?.Info(OwnerId, message);
        }

        private void WarnOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            m_diagnostics?.Warn(OwnerId, message);
        }

        /// <summary>
        /// 绑定前快照。**必须在第一次复制之前抓取**；解绑时逐项写回，避免把 H3 永久改成客户端状态。
        /// 捕捉范围 = 本类会写的全部引擎字段（多余字段不影响，少一个就等于永久污染）。
        /// </summary>
        private sealed class MirrorCharacterSnapshot
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3 StandingOnVelocity;
            public Quaternion Rotation;
            public float TargetCrouchFactor;
            public float CrouchFactor;
            public float Density;
            public bool IsGravityEnabled;
            public float ImmersionDepth;
            public float ImmersionFactor;
            public FluidBlock ImmersionFluidBlock;
            public Vector2 LookAngles;
            public bool IsCreativeFlyEnabled;
            public PlayerInput PlayerInput;
            public object PlayerAim;
            public bool GameWidgetHitTestVisible = true;
            public bool ViewWidgetHitTestVisible = true;
            /// <summary>绑定前 GameWidget.Target（正常 = H3 自己的 ComponentPlayer）。</summary>
            public ComponentCreature GameWidgetTarget;
            /// <summary>绑定前 GameWidget.ActiveCamera（正常 = FppCamera）。</summary>
            public Camera ActiveCamera;
            public float Health;
            public float Air;
            public float AirCapacity;
            public float LastHealth;
            public float Food;
            public float Stamina;
            public float Sleep;
            public float Temperature;
            public float Wetness;
            public float TargetTemperature;
            public float LastFood;
            public float LastStamina;
            public float LastSleep;
            public float LastTemperature;
            public float LastWetness;
            public float FluOnset;
            public float FluDuration;
            public float CoughDuration;
            public float SneezeDuration;
            public float SicknessDuration;
            public float FireDuration;
            public int[] InventoryValues;
            public int[] InventoryCounts;
            public int ActiveSlotIndex;
            public int[][] Clothes;
            public float PokingPhase;
            public CellFace? DigCellFace;
            public float DigProgress;
            public double DigStartTime;
            public int LastDigFrameIndex;
            public float PunchFactor;
            public float PunchPhase;
            public float AimHandAngle;
            public Vector3 InHandItemOffset;
            public Vector3 InHandItemRotation;
            public bool RowLeft;
            public bool RowRight;
            public float InjuryColorFactor;
            public float MovementAnimationPhase;
            public float Bob;
            public float DeathPhase;
            public bool FeedOrder;
            public bool RowLeftOrder;
            public bool RowRightOrder;
            public bool AttackOrder;
            public float AimHandAngleOrder;
            public Vector3 InHandItemOffsetOrder;
            public Vector3 InHandItemRotationOrder;

            public static MirrorCharacterSnapshot Capture(MirrorCharacterEngine engine,
                PlayerData target, ComponentPlayer targetPlayer, GameWidget gameWidget,
                ComponentBody body, ComponentHealth health, ComponentVitalStats vitals,
                ComponentLocomotion locomotion, ComponentMiner miner,
                ComponentCreatureModel model, ComponentInput input, ComponentFlu flu,
                ComponentSickness sickness, ComponentOnFire onFire, ComponentClothing clothing)
            {
                var snapshot = new MirrorCharacterSnapshot
                {
                    Position = body.Position,
                    Velocity = body.Velocity,
                    StandingOnVelocity = body.StandingOnVelocity,
                    Rotation = body.Rotation,
                    TargetCrouchFactor = body.TargetCrouchFactor,
                    CrouchFactor = engine.GetField<float>(body, "m_crouchFactor",
                        typeof(ComponentBody)),
                    Density = body.Density,
                    IsGravityEnabled = body.IsGravityEnabled,
                    ImmersionDepth = body.ImmersionDepth,
                    ImmersionFactor = body.ImmersionFactor,
                    ImmersionFluidBlock = body.ImmersionFluidBlock,
                    LookAngles = locomotion != null ? locomotion.LookAngles : default(Vector2),
                    IsCreativeFlyEnabled = locomotion != null && locomotion.IsCreativeFlyEnabled,
                    PlayerInput = engine.m_inputRef != null
                        ? engine.m_inputRef(input)
                        : default(PlayerInput),
                    PlayerAim = engine.m_parentField.GetParentField(targetPlayer, "m_aim",
                        typeof(ComponentPlayer)),
                    GameWidgetHitTestVisible = gameWidget != null && gameWidget.IsHitTestVisible,
                    ViewWidgetHitTestVisible = gameWidget == null || gameWidget.ViewWidget == null ||
                        gameWidget.ViewWidget.IsHitTestVisible,
                    GameWidgetTarget = gameWidget == null ? null : gameWidget.Target,
                    ActiveCamera = gameWidget == null ? null : gameWidget.ActiveCamera,
                    Health = health == null ? 0f : engine.GetField<float>(health,
                        "<Health>k__BackingField", typeof(ComponentHealth)),
                    Air = health == null ? 0f : engine.GetField<float>(health,
                        "<Air>k__BackingField", typeof(ComponentHealth)),
                    AirCapacity = health == null ? 1f : health.AirCapacity,
                    LastHealth = health == null ? 0f : engine.GetField<float>(health,
                        "m_lastHealth", typeof(ComponentHealth)),
                    Food = vitals == null ? 1f : vitals.Food,
                    Stamina = vitals == null ? 1f : vitals.Stamina,
                    Sleep = vitals == null ? 1f : vitals.Sleep,
                    Temperature = vitals == null ? 0f : vitals.Temperature,
                    Wetness = vitals == null ? 0f : vitals.Wetness,
                    TargetTemperature = vitals == null ? 0f : engine.GetField<float>(vitals,
                        "m_targetTemperature", typeof(ComponentVitalStats)),
                    LastFood = engine.GetField<float>(vitals, "m_lastFood",
                        typeof(ComponentVitalStats)),
                    LastStamina = vitals == null ? 0f : engine.GetField<float>(vitals,
                        "m_lastStamina", typeof(ComponentVitalStats)),
                    LastSleep = vitals == null ? 0f : engine.GetField<float>(vitals,
                        "m_lastSleep", typeof(ComponentVitalStats)),
                    LastTemperature = vitals == null ? 0f : engine.GetField<float>(vitals,
                        "m_lastTemperature", typeof(ComponentVitalStats)),
                    LastWetness = vitals == null ? 0f : engine.GetField<float>(vitals,
                        "m_lastWetness", typeof(ComponentVitalStats)),
                    PokingPhase = miner == null ? 0f : miner.PokingPhase,
                    DigCellFace = miner == null ? null : miner.DigCellFace,
                    DigProgress = miner == null ? 0f : miner.DigProgress,
                    DigStartTime = miner == null ? 0.0 : engine.GetField<double>(miner,
                        "m_digStartTime", typeof(ComponentMiner)),
                    LastDigFrameIndex = miner == null ? 0 : engine.GetField<int>(miner,
                        "m_lastDigFrameIndex", typeof(ComponentMiner)),
                    InjuryColorFactor = model == null ? 0f : engine.GetField<float>(model,
                        "m_injuryColorFactor", typeof(ComponentCreatureModel)),
                    MovementAnimationPhase = model == null ? 0f : model.MovementAnimationPhase,
                    Bob = model == null ? 0f : model.Bob,
                    DeathPhase = model == null ? 0f : model.DeathPhase,
                    FeedOrder = model != null && model.FeedOrder,
                    RowLeftOrder = model != null && model.RowLeftOrder,
                    RowRightOrder = model != null && model.RowRightOrder,
                    AttackOrder = model != null && model.AttackOrder,
                    AimHandAngleOrder = model == null ? 0f : model.AimHandAngleOrder,
                    InHandItemOffsetOrder = model == null ? Vector3.Zero : model.InHandItemOffsetOrder,
                    InHandItemRotationOrder = model == null ? Vector3.Zero : model.InHandItemRotationOrder
                };
                if (flu != null)
                {
                    snapshot.FluOnset = engine.GetField<float>(flu, "m_fluOnset",
                        typeof(ComponentFlu));
                    snapshot.FluDuration = engine.GetField<float>(flu, "m_fluDuration",
                        typeof(ComponentFlu));
                    snapshot.CoughDuration = engine.GetField<float>(flu, "m_coughDuration",
                        typeof(ComponentFlu));
                    snapshot.SneezeDuration = engine.GetField<float>(flu, "m_sneezeDuration",
                        typeof(ComponentFlu));
                }
                if (sickness != null)
                    snapshot.SicknessDuration = engine.GetField<float>(sickness,
                        "m_sicknessDuration", typeof(ComponentSickness));
                if (onFire != null)
                    snapshot.FireDuration = engine.GetField<float>(onFire, "m_fireDuration",
                        typeof(ComponentOnFire));
                IInventory inventory = miner == null ? null : miner.Inventory;
                if (inventory != null)
                {
                    int slots = inventory.SlotsCount;
                    snapshot.InventoryValues = new int[slots];
                    snapshot.InventoryCounts = new int[slots];
                    for (int i = 0; i < slots; i++)
                    {
                        snapshot.InventoryValues[i] = inventory.GetSlotValue(i);
                        snapshot.InventoryCounts[i] = inventory.GetSlotCount(i);
                    }
                    snapshot.ActiveSlotIndex = inventory.ActiveSlotIndex;
                }
                if (clothing != null)
                {
                    snapshot.Clothes = new int[4][];
                    for (int i = 0; i < 4; i++)
                    {
                        var clothes = clothing.GetClothes((ClothingSlot)i);
                        snapshot.Clothes[i] = new int[clothes.Count];
                        for (int j = 0; j < clothes.Count; j++)
                            snapshot.Clothes[i][j] = clothes[j];
                    }
                }
                ComponentHumanModel human = targetPlayer.Entity.FindComponent<ComponentHumanModel>();
                if (human != null)
                {
                    snapshot.PunchFactor = engine.GetField<float>(human, "m_punchFactor",
                        typeof(ComponentHumanModel));
                    snapshot.PunchPhase = engine.GetField<float>(human, "m_punchPhase",
                        typeof(ComponentHumanModel));
                    snapshot.AimHandAngle = engine.GetField<float>(human, "m_aimHandAngle",
                        typeof(ComponentHumanModel));
                    snapshot.InHandItemOffset = engine.GetField<Vector3>(human,
                        "m_inHandItemOffset", typeof(ComponentHumanModel));
                    snapshot.InHandItemRotation = engine.GetField<Vector3>(human,
                        "m_inHandItemRotation", typeof(ComponentHumanModel));
                    snapshot.RowLeft = engine.GetField<bool>(human, "m_rowLeft",
                        typeof(ComponentHumanModel));
                    snapshot.RowRight = engine.GetField<bool>(human, "m_rowRight",
                        typeof(ComponentHumanModel));
                }
                _ = target;
                return snapshot;
            }
        }
    }
}
