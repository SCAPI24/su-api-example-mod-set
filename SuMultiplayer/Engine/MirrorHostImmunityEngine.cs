using System;
using System.Collections.Generic;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 自动镜像宿主的**免疫写入引擎动作**（<see cref="MirrorHostImmunityFields"/> 的执行者）。
    ///
    /// 为什么单独一个类（而不是写进 <c>MirrorHostEngine</c>）：
    ///   · 宿主生命周期（创建 / 收编 / 移除 / 绑定）只用**公开引擎链路**，那条源码守卫要求
    ///     <c>MirrorHostEngine.cs</c> 里不出现反射入口；本类必须用 SuAPI <c>IModParentField</c>
    ///     去写引擎的 private setter / 私有字段，所以单独成类，由组合根注入实例。
    ///   · 读 <c>PlayerData</c> 状态机（判断是否卡在 <c>PlayerDead</c>）同样需要私有字段
    ///     <c>m_stateMachine</c>（Source: Survivalcraft/Game/PlayerData.cs:29；公开类型
    ///     <c>StateMachine.CurrentState</c> 读当前状态，Source: Survivalcraft/Game/StateMachine.cs:37）。
    ///
    /// 写入方式：<c>IModParentField.ModifyParentField(组件实例, 成员名, 值, 组件类型)</c> ——
    /// 先找字段再找属性（Source: EntitySystem/SuAPI/ModParentField.cs:59-97），因此
    /// public 属性（<c>ComponentHealth.Health</c> 的 private setter）与私有字段
    /// （<c>ComponentFlu.m_fluDuration</c>）走同一条链路。
    ///
    /// 硬约束：
    ///   · **一个字段写失败也要显式计数 + 写清字段名**（绝不静默兜底、绝不"写不进去就当成功"）；
    ///   · 只接受调用方给的 <c>ComponentPlayer</c>；"它是不是自动宿主"由
    ///     <c>MirrorHostEngine</c> 用 <see cref="MirrorHostImmunityTarget"/> 判定后再调用本类；
    ///   · 本类**无静态可变状态**：诊断 / 反射入口由组合根注入。
    /// </summary>
    public sealed class MirrorHostImmunityEngine
    {
        private const string OwnerId = "mirror-host-immunity";

        /// <summary>
        /// 同一失败键的日志最小间隔（秒）。热路径**每帧**都会调用 <see cref="TryApply"/>，
        /// 引擎版本变化导致某个字段写不进去时必须仍然每帧计数、但日志按 10 秒一行（与控制器状态行同口径）。
        /// </summary>
        private const double FailureLogIntervalSeconds = 10.0;

        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;

        /// <summary>失败键 → 上次写日志的时刻（<c>Engine.Time.RealTime</c>，10 秒限流）。</summary>
        private readonly Dictionary<string, double> m_lastFailureLog =
            new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>累计写入成功的字段数（诊断用；每拍清零的增量由调用方统计）。</summary>
        private long m_writes;

        /// <summary>累计写入失败的字段数（诊断用：字段名 + 异常写在日志里）。</summary>
        private long m_writeFailures;

        /// <summary>累计读 <c>PlayerData.m_stateMachine</c> 失败的次数。</summary>
        private long m_stateReadFailures;

        /// <summary>累计清掉的症状粒子（呕吐 / 着火）次数。</summary>
        private long m_particlesStopped;

        /// <summary>累计停症状粒子失败的次数（拿不到粒子句柄 / 停粒子抛异常）。</summary>
        private long m_particleFailures;

        public MirrorHostImmunityEngine(IModParentField parentField, IModuleDiagnostics diagnostics)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_diagnostics = diagnostics;
        }

        /// <summary>累计写入成功的字段数。</summary>
        public long Writes => m_writes;

        /// <summary>累计写入失败的字段数。</summary>
        public long WriteFailures => m_writeFailures;

        /// <summary>累计读状态机失败次数。</summary>
        public long StateReadFailures => m_stateReadFailures;

        /// <summary>累计停掉的症状粒子数（呕吐 / 着火）。</summary>
        public long ParticlesStopped => m_particlesStopped;

        /// <summary>累计停症状粒子失败的次数（每次都计数，日志 10 秒限流）。</summary>
        public long ParticleFailures => m_particleFailures;

        /// <summary>一行诊断摘要（10 秒状态行用）。</summary>
        public string Describe() =>
            "writes=" + m_writes + " writeFailures=" + m_writeFailures +
            " stateReadFailures=" + m_stateReadFailures +
            " particlesStopped=" + m_particlesStopped +
            " particleFailures=" + m_particleFailures;

        /// <summary>
        /// 失败日志（**每次都计数、日志 10 秒限流**）：热路径每帧都会走到这里，
        /// 同一个失败键 10 秒内只写一行；消息里带累计次数与可直接定位的原因（绝不静默）。
        /// </summary>
        private void LogFailure(string key, bool error, string message, Exception exception)
        {
            // Source: Engine/Engine/Time.cs:24 的静态时钟（与控制器注入的 Time.RealTime 同一个值；
            // 纯 Stopwatch 读取，不会抛异常，因此这里没有兜底分支）。
            // 必须写 global:: —— 本文件在 SuMultiplayer.Engine 命名空间里，Engine.Time 会被解析成该命名空间。
            double now = global::Engine.Time.RealTime;
            if (m_lastFailureLog.TryGetValue(key, out double last) &&
                now - last < FailureLogIntervalSeconds)
                return;
            m_lastFailureLog[key] = now;
            if (error) m_diagnostics?.Error(OwnerId, message, exception);
            else m_diagnostics?.Warn(OwnerId, message);
        }

        /// <summary>
        /// 读宿主 <c>PlayerData</c> 是否处于 <c>PlayerDead</c> 状态（该状态只有在
        /// <c>GameWidget.Input.Any</c> 时自己复活；自动宿主 <c>InputDevice=None</c> ⇒ 不修就卡死）。
        /// 读不到 = false + <paramref name="failure"/>（调用方必须把它记成失败，不能当成"没死"）。
        /// </summary>
        public bool TryReadPlayerDead(PlayerData playerData, out bool dead, out string failure)
        {
            dead = false;
            failure = null;
            if (playerData == null)
            {
                failure = "PlayerData 为空";
                return false;
            }
            StateMachine machine;
            try
            {
                machine = m_parentField.GetParentField<StateMachine>(playerData, "m_stateMachine",
                    typeof(PlayerData));
            }
            catch (Exception e)
            {
                m_stateReadFailures++;
                failure = "读不到 PlayerData.m_stateMachine（引擎版本不匹配）：" +
                    e.GetType().Name + " " + e.Message;
                return false;
            }
            if (machine == null)
            {
                m_stateReadFailures++;
                failure = "PlayerData.m_stateMachine 为 null（引擎版本不匹配）";
                return false;
            }
            string state;
            try
            {
                state = machine.CurrentState;
            }
            catch (Exception e)
            {
                m_stateReadFailures++;
                failure = "读 PlayerData 状态机当前状态失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            if (state == null)
            {
                m_stateReadFailures++;
                failure = "PlayerData 状态机没有当前状态（还没进 FirstUpdate）";
                return false;
            }
            dead = string.Equals(state, "PlayerDead", StringComparison.Ordinal);
            return true;
        }

        /// <summary>
        /// 把免疫字段清单整套写进这个宿主：<paramref name="applied"/> = 成功写入的字段数。
        /// 返回 false = 至少有一个字段没写进去（<paramref name="failure"/> 写清第一个失败原因，
        /// 后续失败同样计入 <see cref="WriteFailures"/>；日志按失败键 10 秒限流，消息里带累计次数）。
        /// </summary>
        public bool TryApply(ComponentPlayer player, out int applied, out string failure)
        {
            applied = 0;
            failure = null;
            if (player == null)
            {
                failure = "ComponentPlayer 为空（宿主还没出生）";
                return false;
            }
            Entity entity = player.Entity;
            if (entity == null)
            {
                failure = "宿主 ComponentPlayer 没有实体（正在被移除）";
                return false;
            }
            ComponentOnFire onFire;
            try
            {
                // 玩家实体模板保证有 ComponentOnFire（Source: ComponentHealth.cs:295 用
                // throwOnError:true 找它）；找不到就是引擎变了 → 明确失败，不静默跳过。
                onFire = entity.FindComponent<ComponentOnFire>(throwOnError: true);
            }
            catch (Exception e)
            {
                failure = "宿主实体没有 ComponentOnFire（引擎模板变了）：" +
                    e.GetType().Name + " " + e.Message;
                m_writeFailures++;
                LogFailure("component:onfire", true,
                    failure + " · 累计字段失败=" + m_writeFailures, e);
                return false;
            }
            // 先停症状粒子（呕吐 / 着火）：只清持续时间的话，已经生成的粒子会继续挂在世界里。
            string particleFailure = NeutralizeSymptomParticles(player, onFire);
            if (particleFailure != null && failure == null) failure = particleFailure;
            MirrorHostImmuneField[] fields = MirrorHostImmunityFields.Fields;
            for (int i = 0; i < fields.Length; i++)
            {
                MirrorHostImmuneField field = fields[i];
                Component target;
                string resolveFailure;
                bool resolved = TryResolveComponent(player, onFire, field.Component, out target,
                    out resolveFailure);
                if (!resolved)
                {
                    m_writeFailures++;
                    if (failure == null) failure = resolveFailure;
                    LogFailure("resolve:" + field.Key, false,
                        resolveFailure + " · 累计字段失败=" + m_writeFailures, null);
                    continue;
                }
                try
                {
                    m_parentField.ModifyParentField(target, field.Member, field.Value,
                        target.GetType());
                    applied++;
                    m_writes++;
                }
                catch (Exception e)
                {
                    m_writeFailures++;
                    string message = "写免疫字段 " + field.Key + " = " + field.Describe() +
                        " 失败：" + e.GetType().Name + " " + e.Message;
                    if (failure == null) failure = message;
                    LogFailure("write:" + field.Key, true,
                        message + "（" + field.Role + "）· 累计字段失败=" + m_writeFailures, e);
                }
            }
            return failure == null;
        }

        /// <summary>
        /// 停掉已生成的症状粒子：呕吐（<c>ComponentSickness.m_pukeParticleSystem</c>）与
        /// 着火（<c>ComponentOnFire.m_onFireParticleSystem</c>）。
        /// 引擎自己的停止逻辑在本拍来不及（引擎 Update 已经跑过），所以这里显式停：
        /// <c>IsStopped = true</c> → <c>SubsystemParticles.RemoveParticleSystem</c> →
        /// 组件字段置 null（否则下一拍 <c>ComponentSickness.Update</c> 仍会把它当活粒子用）。
        /// </summary>
        private string NeutralizeSymptomParticles(ComponentPlayer player, ComponentOnFire onFire)
        {
            string failure = null;
            ComponentSickness sickness = player.ComponentSickness;
            if (sickness != null)
            {
                try
                {
                    // 非泛型重载：泛型版本在成员为 null 时会抛 InvalidCastException（null is T = false）。
                    if (m_parentField.GetParentField(sickness, "m_pukeParticleSystem",
                            typeof(ComponentSickness)) is PukeParticleSystem puke)
                    {
                        puke.IsStopped = true;
                        if (m_parentField.GetParentField(sickness, "m_subsystemParticles",
                                typeof(ComponentSickness)) is SubsystemParticles particles)
                            particles.RemoveParticleSystem(puke);
                        m_parentField.ModifyParentField(sickness, "m_pukeParticleSystem", null,
                            typeof(ComponentSickness));
                        m_particlesStopped++;
                    }
                }
                catch (Exception e)
                {
                    m_particleFailures++;
                    failure = "停掉宿主的呕吐粒子失败：" + e.GetType().Name + " " + e.Message;
                    LogFailure("particle:puke", true, failure + "（" +
                        MirrorHostImmunityFields.OwnerSickness + ".m_pukeParticleSystem）· 累计粒子失败=" +
                        m_particleFailures, e);
                }
            }
            if (onFire != null && onFire.IsOnFire)
            {
                try
                {
                    // 公开 API：清 Attacker + 清 m_fireDuration；粒子由下一拍 Update 自己收，
                    // 这里同时也把粒子停掉（本拍引擎 Update 已经过了）。
                    onFire.Extinguish();
                    if (m_parentField.GetParentField(onFire, "m_onFireParticleSystem",
                            typeof(ComponentOnFire)) is OnFireParticleSystem fire)
                    {
                        fire.IsStopped = true;
                        m_parentField.ModifyParentField(onFire, "m_onFireParticleSystem", null,
                            typeof(ComponentOnFire));
                        m_particlesStopped++;
                    }
                }
                catch (Exception e)
                {
                    m_particleFailures++;
                    string message = "息灭宿主身上的火失败：" + e.GetType().Name + " " + e.Message;
                    if (failure == null) failure = message;
                    LogFailure("particle:onfire", true,
                        message + " · 累计粒子失败=" + m_particleFailures, e);
                }
            }
            return failure;
        }

        /// <summary>
        /// 清单里的组件名 → 组件实例。未知名字 = 明确失败（绝不"跳过这一条"）。
        /// </summary>
        private static bool TryResolveComponent(ComponentPlayer player, ComponentOnFire onFire,
            string component, out Component target, out string failure)
        {
            target = null;
            failure = null;
            switch (component)
            {
                case MirrorHostImmunityFields.OwnerHealth:
                    target = player.ComponentHealth;
                    break;
                case MirrorHostImmunityFields.OwnerVitalStats:
                    target = player.ComponentVitalStats;
                    break;
                case MirrorHostImmunityFields.OwnerFlu:
                    target = player.ComponentFlu;
                    break;
                case MirrorHostImmunityFields.OwnerSickness:
                    target = player.ComponentSickness;
                    break;
                case MirrorHostImmunityFields.OwnerOnFire:
                    target = onFire;
                    break;
                default:
                    failure = "免疫字段清单里出现未知组件「" + component + "」→ 拒绝写入（清单与引擎不匹配）";
                    return false;
            }
            if (target != null) return true;
            failure = "宿主缺少组件「" + component + "」→ 免疫字段无法写入";
            return false;
        }
    }
}
