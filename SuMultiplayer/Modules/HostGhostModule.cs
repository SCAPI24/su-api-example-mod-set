using System;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 主机角色"幽灵身体"：**身体留在原地，但别人看不到、打不到他、他也打不到别人**。
    ///
    /// 为什么需要它（用户 2026-11-08 定）：无头服务器的主机没有人在操作，那个本机角色
    /// 会永久站在世界里 —— 客户端看到的是"一具不动的尸体"，而且它会一直参与碰撞与战斗。
    /// 幽灵化之后主机角色退出世界表现，但**PlayerData / 背包 / 坐标全部保留**，
    /// 随时可以复原。
    ///
    /// 实现照搬旧版联机 Mod 里"客户端映射到主机第三屏"的做法
    /// （Source: Mod/SuMultiplayer 历史提交 22c6468 · Engine/MirrorCharacterEngine.cs:HideTargetModel），
    /// 那套做法是当时验证过可行的：
    ///
    ///   1. <c>SubsystemBodies.RemoveBody</c>（private，反射）—— 摘掉**碰撞**：
    ///      别人打不到他（攻击判定走 <c>m_subsystemBodies.Raycast</c>），他也推不动别人；
    ///   2. 从 <c>SubsystemModelsRenderer.m_componentModels</c>（private 字段，反射）移除模型
    ///      —— **任何视图都看不到**，阴影也不再排队（绘制入口只遍历这张表）；
    ///   3. <c>SubsystemUpdate.RemoveUpdateable</c> 停掉本机角色的 updateable —— 不再跑物理/输入；
    ///   4. **坐标完全不动** —— 广播给客户端的位置、存档、实时地图都不受影响；
    ///   5. 复原时反向操作（AddBody + 模型加回渲染表 + updateable 加回）。
    ///
    /// 另外把 <c>ComponentSleep.SleepFactor</c> 置 1（睡眠）：否则客户端全部睡觉时，
    /// 主机这个"清醒"角色会让世界**永远不加速**（判据见
    /// Survivalcraft/Game/SubsystemTime.cs:87-100：睡眠+死亡人数必须等于总人数且至少一人睡觉）。
    /// </summary>
    public sealed class HostGhostModule : IMultiplayerModule
    {
        private readonly ModuleDescriptor m_descriptor;
        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly Action<string> m_info;
        private readonly Action<string> m_warn;

        // 已隐藏的模型与它们的渲染数据（复原时按原样加回）。
        private readonly List<ComponentModel> m_hiddenModels = new List<ComponentModel>();
        private readonly List<object> m_hiddenModelData = new List<object>();
        // 已停掉的 updateable（复原时加回）。
        private readonly List<IUpdateable> m_removedUpdateables = new List<IUpdateable>();

        private object m_modelTable;
        private MethodInfo m_modelTableTryGet;
        private MethodInfo m_modelTableRemove;
        private MethodInfo m_modelTableAdd;
        private MethodInfo m_removeBodyMethod;
        private MethodInfo m_addBodyMethod;
        // ComponentSleep.SleepFactor 是只读属性（=> m_sleepFactor），且 Update() 在幽灵状态下不跑，
        // 所以必须直接写字段；否则 m_sleepFactor 永远涨不到 1，世界不会加速。
        private FieldInfo m_sleepFactorField;

        private ComponentBody m_hiddenBody;
        private ComponentPlayer m_hiddenPlayer;
        private float m_originalSleepFactor;
        private bool m_isGhost;

        public HostGhostModule(IModParentField parentField, IModParentMethod parentMethod,
            Action<string> info = null, Action<string> warn = null)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_info = info;
            m_warn = warn;
            m_descriptor = new ModuleDescriptor("host-ghost", "主机角色幽灵身体（不可见 / 不可攻击 / 睡眠）")
            {
                ProvidesCapabilities = new string[0],
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        /// <summary>当前是否处于幽灵状态。</summary>
        public bool IsGhost => m_isGhost;

        public string StatusLine() => "host-ghost: ghost=" + (m_isGhost ? "1" : "0") +
            " hiddenModels=" + m_hiddenModels.Count +
            " hiddenBody=" + (m_hiddenBody != null ? "1" : "0");

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            // 本模块无自有端口依赖：全部通过引擎 Project 反射操作。
        }

        public void Detach(ModuleDetachReason reason)
        {
            // 下线前尽量把身体还回去（不能留下一个看不见又打不到的角色）。
            if (m_isGhost)
            {
                try
                {
                    ExitGhost(out _);
                }
                catch (Exception exception)
                {
                    m_warn?.Invoke("[SuMP][ghost] 模块下线时复原身体失败：" +
                        exception.GetType().Name + " " + exception.Message);
                }
            }
        }

        /// <summary>幽灵状态没有队列要推进；保留给宿主调用（接口要求）。</summary>
        public void Tick(in ModuleTickContext tick) { }

        // ================================================================= 进出幽灵

        /// <summary>
        /// 进入幽灵状态：隐藏主机本机角色的**全部**本机角色（分屏时可能有多个，
        /// 但无头服务器通常只有一个）。已是幽灵时直接返回成功（幂等）。
        /// </summary>
        public bool EnterGhost(out string reason)
        {
            reason = null;
            if (m_isGhost)
            {
                reason = "Already in ghost mode.";
                return true;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "No world is loaded.";
                return false;
            }
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(false);
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            SubsystemModelsRenderer renderer =
                project.FindSubsystem<SubsystemModelsRenderer>(false);
            if (players == null || bodies == null || update == null || renderer == null)
            {
                reason = "World subsystems are unavailable (players/bodies/update/renderer).";
                return false;
            }

            // 主机自己的角色 = 本机 PlayerData（不是远端化身）。
            List<ComponentPlayer> targets = new List<ComponentPlayer>(players.ComponentPlayers);
            if (targets.Count == 0)
            {
                reason = "There is no local player character to hide.";
                return false;
            }

            if (!EnsureEngineBindings(renderer, bodies, out reason)) return false;

            try
            {
                foreach (ComponentPlayer player in targets)
                {
                    if (player?.ComponentBody == null) continue;
                    m_hiddenPlayer = player;
                    m_hiddenBody = player.ComponentBody;
                    m_originalSleepFactor = GetSleepFactor(player.ComponentSleep);

                    // 1) 摘掉碰撞：别人打不到他，他也推不动别人。
                    m_removeBodyMethod.Invoke(bodies, new object[] { player.ComponentBody });

                    // 2) 摘掉渲染：从 SubsystemModelsRenderer.m_componentModels 移除。
                    foreach (ComponentModel model in player.Entity.FindComponents<ComponentModel>())
                        HideModel(model);

                    // 3) 停掉 updateable（输入 / 物理不再跑）。
                    foreach (IUpdateable updateable in player.Entity.FindComponents<IUpdateable>())
                        RemoveUpdateable(update, updateable);

                    // 4) 睡眠：客户端全睡时世界才会加速（至少一人必须是"睡眠"而不是"死亡"）。
                    SetSleepFactor(player.ComponentSleep, 1f);
                    break;
                }

                if (m_hiddenBody == null)
                {
                    reason = "Could not hide the host body (no ComponentBody).";
                    return false;
                }
                m_isGhost = true;
                m_info?.Invoke("[SuMP][ghost] 主机角色已幽灵化：碰撞/渲染/更新已摘除，" +
                    "睡眠置 1（客户端全睡时世界可加速）；坐标保留 " +
                    m_hiddenBody.Position.ToString() + "；模型 " + m_hiddenModels.Count + " 个");
                return true;
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                reason = "Ghost mode failed: " + inner.GetType().Name + ": " + inner.Message;
                TryRollback();
                return false;
            }
            catch (Exception exception)
            {
                reason = "Ghost mode failed: " + exception.GetType().Name + ": " + exception.Message;
                TryRollback();
                return false;
            }
        }

        /// <summary>复原主机角色（幂等：不是幽灵时直接返回成功）。</summary>
        public bool ExitGhost(out string reason)
        {
            reason = null;
            if (!m_isGhost)
            {
                reason = "Not in ghost mode.";
                return true;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "No world is loaded; restart the world to restore the host body.";
                return false;
            }
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(false);
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (bodies == null || update == null)
            {
                reason = "World subsystems are unavailable (bodies/update).";
                return false;
            }

            try
            {
                if (m_hiddenBody != null && m_addBodyMethod != null)
                    m_addBodyMethod.Invoke(bodies, new object[] { m_hiddenBody });

                foreach (IUpdateable updateable in m_removedUpdateables)
                    update.AddUpdateable(updateable);
                m_removedUpdateables.Clear();

                RestoreModels();

                SetSleepFactor(m_hiddenPlayer?.ComponentSleep, m_originalSleepFactor);

                m_hiddenBody = null;
                m_hiddenPlayer = null;
                m_isGhost = false;
                m_info?.Invoke("[SuMP][ghost] 主机角色已复原：碰撞/渲染/更新已加回，睡眠已还原");
                return true;
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                reason = "Restoring the host body failed: " +
                    inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            catch (Exception exception)
            {
                reason = "Restoring the host body failed: " +
                    exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        // ================================================================= 引擎反射

        private bool EnsureEngineBindings(SubsystemModelsRenderer renderer, SubsystemBodies bodies,
            out string reason)
        {
            reason = null;
            if (m_removeBodyMethod == null)
            {
                m_removeBodyMethod = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies),
                    "RemoveBody", new[] { typeof(ComponentBody) });
            }
            if (m_addBodyMethod == null)
            {
                m_addBodyMethod = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies),
                    "AddBody", new[] { typeof(ComponentBody) });
            }
            if (m_removeBodyMethod == null || m_addBodyMethod == null)
            {
                reason = "This engine build has no SubsystemBodies.RemoveBody/AddBody.";
                return false;
            }

            if (m_modelTable == null)
            {
                object table;
                try
                {
                    table = m_parentField.GetParentField(renderer, "m_componentModels",
                        typeof(SubsystemModelsRenderer));
                }
                catch (Exception exception)
                {
                    reason = "Cannot read SubsystemModelsRenderer.m_componentModels: " +
                        exception.GetType().Name + " " + exception.Message;
                    return false;
                }
                if (table == null)
                {
                    reason = "SubsystemModelsRenderer.m_componentModels is null.";
                    return false;
                }
                Type tableType = table.GetType();
                m_modelTableTryGet = FindInstanceMethod(tableType, "TryGetValue", 2);
                m_modelTableRemove = FindInstanceMethod(tableType, "Remove", 1);
                m_modelTableAdd = FindInstanceMethod(tableType, "Add", 2);
                if (m_modelTableTryGet == null || m_modelTableRemove == null ||
                    m_modelTableAdd == null)
                {
                    reason = "m_componentModels is not Dictionary<ComponentModel, ModelData> ("
                        + tableType + ").";
                    return false;
                }
                m_modelTable = table;
            }
            return true;
        }

        /// <summary>把模型从渲染表摘掉（不渲染、不投影）。</summary>
        private void HideModel(ComponentModel model)
        {
            if (model == null || m_modelTable == null) return;
            object[] args = { model, null };
            bool present = (bool)m_modelTableTryGet.Invoke(m_modelTable, args);
            if (!present) return;
            m_hiddenModels.Add(model);
            m_hiddenModelData.Add(args[1]);
            m_modelTableRemove.Invoke(m_modelTable, new object[] { model });
        }

        /// <summary>把模型加回渲染表（引擎已自行加回时不重复 Add）。</summary>
        private void RestoreModels()
        {
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
        }

        private void RemoveUpdateable(SubsystemUpdate update, IUpdateable updateable)
        {
            if (update == null || updateable == null) return;
            update.RemoveUpdateable(updateable);
            m_removedUpdateables.Add(updateable);
        }

        /// <summary>中途失败时尽力回滚，避免留下半隐藏状态。</summary>
        private void TryRollback()
        {
            try
            {
                Project project = GameManager.Project;
                SubsystemBodies bodies = project?.FindSubsystem<SubsystemBodies>(false);
                SubsystemUpdate update = project?.FindSubsystem<SubsystemUpdate>(false);
                if (m_hiddenBody != null && m_addBodyMethod != null && bodies != null)
                    m_addBodyMethod.Invoke(bodies, new object[] { m_hiddenBody });
                if (update != null)
                {
                    foreach (IUpdateable updateable in m_removedUpdateables)
                        update.AddUpdateable(updateable);
                }
                m_removedUpdateables.Clear();
                RestoreModels();
                SetSleepFactor(m_hiddenPlayer?.ComponentSleep, m_originalSleepFactor);
            }
            catch (Exception exception)
            {
                m_warn?.Invoke("[SuMP][ghost] 回滚失败：" +
                    exception.GetType().Name + " " + exception.Message);
            }
            finally
            {
                m_hiddenBody = null;
                m_hiddenPlayer = null;
                m_isGhost = false;
            }
        }

        /// <summary>读 ComponentSleep.m_sleepFactor（只读属性背后的字段）。</summary>
        private float GetSleepFactor(ComponentSleep sleep)
        {
            if (sleep == null) return 0f;
            FieldInfo field = EnsureSleepField(sleep.GetType());
            return field != null && field.GetValue(sleep) is float value ? value : 0f;
        }

        /// <summary>
        /// 直接写 ComponentSleep.m_sleepFactor。
        ///
        /// 为什么不用 Sleep()/属性：`Sleep()` 只设标志，`m_sleepFactor` 要靠 Update() 每帧 +0.33
        /// 才涨到 1；幽灵模式下 updateable 已被摘掉，Update 不跑 → 世界永远不会认为"全员睡觉"。
        /// 因此这里直接写字段（与引擎 Update 的终态一致）。
        /// </summary>
        private void SetSleepFactor(ComponentSleep sleep, float value)
        {
            if (sleep == null) return;
            FieldInfo field = EnsureSleepField(sleep.GetType());
            if (field == null)
            {
                m_warn?.Invoke("[SuMP][ghost] 找不到 ComponentSleep.m_sleepFactor（引擎版本不匹配）："
                    + "主机角色无法置为睡眠，客户端全睡时世界不会加速");
                return;
            }
            field.SetValue(sleep, value);
        }

        private FieldInfo EnsureSleepField(Type sleepType)
        {
            if (m_sleepFactorField != null) return m_sleepFactorField;
            m_sleepFactorField = sleepType?.GetField("m_sleepFactor",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return m_sleepFactorField;
        }

        private static MethodInfo FindInstanceMethod(Type type, string name, int parameterCount)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance |
                BindingFlags.Public))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                if (method.GetParameters().Length == parameterCount) return method;
            }
            return null;
        }
    }
}
