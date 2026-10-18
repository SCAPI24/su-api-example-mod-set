using System;
using System.Collections.Generic;

namespace SuMultiplayer.Modules
{
    /// <summary>一次依赖解析的结果。</summary>
    public readonly struct ModuleResolution
    {
        public ModuleResolution(int active, int disabled, int unavailable, int faulted,
            int attached, int detached, string[] essentialMissing)
        {
            Active = active;
            Disabled = disabled;
            Unavailable = unavailable;
            Faulted = faulted;
            Attached = attached;
            Detached = detached;
            EssentialMissing = essentialMissing;
        }

        public int Active { get; }
        public int Disabled { get; }
        public int Unavailable { get; }
        public int Faulted { get; }
        public int Attached { get; }
        public int Detached { get; }
        /// <summary>缺失的"必需模块"（由 Essential 模块提供的能力）。为空 = 联机功能可以启动。</summary>
        public string[] EssentialMissing { get; }
        public bool EssentialComplete => EssentialMissing.Length == 0;
    }

    /// <summary>
    /// 模块宿主：负责依赖解析、装配/卸载、能力表、异常隔离与熔断。
    /// 设计原则（对应"模块下线不影响其余系统"）：
    /// <list type="number">
    /// <item>模块之间**没有任何直接引用**——只通过能力与端口通信；</item>
    /// <item>硬依赖只允许在同一条业务链内部，跨域一律软依赖（缺失即降级）；</item>
    /// <item>任何模块抛异常只影响自己：计数 → 熔断 → 撤回能力 → 触发重新解析；</item>
    /// <item>下线是**一等操作**：卸载时按逆序 Detach，并让依赖方走降级路径而不是崩溃；</item>
    /// <item>必需能力缺失时如实报告 <see cref="ModuleResolution.EssentialMissing"/>，由上层决定是否拒绝开局。</item>
    /// </list>
    /// 演示页标记：DEC04。
    /// </summary>
    public sealed class ModuleHost : IDisposable
    {
        private sealed class Entry
        {
            public IMultiplayerModule Module;
            public ModuleDescriptor Descriptor;
            public ModuleOptions Options;
            public ModuleState State = ModuleState.Registered;
            public string Reason = string.Empty;
            public bool Degraded;
            public int ConsecutiveFaults;
            public long LastFaultMs;
            public int Order;
        }

        private readonly IModuleDiagnostics m_diagnostics;
        private readonly List<Entry> m_entries = new List<Entry>();
        private readonly Dictionary<string, object> m_ports = new Dictionary<string, object>();
        private readonly HashSet<string> m_infrastructureCapabilities = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_activeCapabilities = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> m_byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly CapabilityRegistry m_capabilities;
        private int m_orderCounter;
        private bool m_disposed;

        public ModuleHost(IModuleDiagnostics diagnostics)
        {
            m_diagnostics = diagnostics ?? new NullModuleDiagnostics();
            m_capabilities = new CapabilityRegistry(m_activeCapabilities);
            m_capabilities.CapabilityChanged += (capability, added) =>
            {
                CapabilityChanged?.Invoke(capability, added);
            };
        }

        public ICapabilityRegistry Capabilities => m_capabilities;

        public event Action<ModuleStatusChange> StatusChanged;

        public event Action<string, bool> CapabilityChanged;

        // ------------------------------------------------------------------
        // 端口：模块唯一的外部能力入口。缺失时 TryGetPort 返回 false。
        // ------------------------------------------------------------------
        public void RegisterPort<T>(T port) where T : class
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            m_ports[typeof(T).FullName] = port;
        }

        public void RemovePort<T>() where T : class => m_ports.Remove(typeof(T).FullName);

        // ------------------------------------------------------------------
        // 基础设施能力：由宿主/上层提供（例如 session.core、net.transport）
        // ------------------------------------------------------------------
        public void ProvideCapability(string capability)
        {
            if (string.IsNullOrWhiteSpace(capability)) return;
            if (m_infrastructureCapabilities.Add(capability)) RefreshCapabilities();
        }

        public void WithdrawCapability(string capability)
        {
            if (m_infrastructureCapabilities.Remove(capability)) RefreshCapabilities();
        }

        public bool HasCapability(string capability) => m_activeCapabilities.Contains(capability);

        // ------------------------------------------------------------------
        // 注册与解析
        // ------------------------------------------------------------------
        public void Register(IMultiplayerModule module, ModuleOptions options = null)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            ModuleDescriptor descriptor = module.Descriptor
                ?? throw new InvalidOperationException("模块缺少 Descriptor");
            if (m_byId.ContainsKey(descriptor.Id))
                throw new InvalidOperationException("模块 id 重复：" + descriptor.Id);

            var entry = new Entry
            {
                Module = module,
                Descriptor = descriptor,
                Options = options ?? new ModuleOptions { Enabled = descriptor.EnabledByDefault },
                Order = m_orderCounter++
            };
            m_entries.Add(entry);
            m_byId.Add(descriptor.Id, entry);
        }

        /// <summary>
        /// 依赖解析 + 装配差值。可在启动、热插拔、熔断、能力变化后反复调用（幂等）。
        /// </summary>
        public ModuleResolution Resolve(string reason = "resolve")
        {
            ThrowIfDisposed();
            ModuleResolution resolution = Compute();
            if (resolution.Attached > 0 || resolution.Detached > 0)
            {
                m_diagnostics.Info("module-host", "解析(" + reason + ")：Active=" + resolution.Active +
                    " 装配=" + resolution.Attached + " 卸载=" + resolution.Detached +
                    " Unavailable=" + resolution.Unavailable + " Disabled=" + resolution.Disabled +
                    " Faulted=" + resolution.Faulted +
                    (resolution.EssentialComplete
                        ? string.Empty
                        : " 必需能力缺失=" + string.Join(",", resolution.EssentialMissing)));
            }
            return resolution;
        }

        private ModuleResolution Compute()
        {
            long now = Environment.TickCount64;
            var wasActive = new HashSet<Entry>();
            foreach (Entry entry in m_entries)
            {
                if (entry.State == ModuleState.Active) wasActive.Add(entry);
            }

            // 1) 候选集合：显式启用 + 冷却期已过的熔断模块
            var candidates = new List<Entry>();
            var disabled = new List<Entry>();
            var heldFaulted = new List<Entry>();
            foreach (Entry entry in m_entries)
            {
                if (!entry.Options.Enabled)
                {
                    disabled.Add(entry);
                    continue;
                }
                if (entry.State == ModuleState.Faulted)
                {
                    bool retry = entry.Options.RetryCooldownMs > 0 &&
                        now - entry.LastFaultMs >= entry.Options.RetryCooldownMs;
                    if (!retry)
                    {
                        // 熔断保持：等冷却或人工恢复，不能当成"配置关闭"
                        heldFaulted.Add(entry);
                        continue;
                    }
                    entry.ConsecutiveFaults = 0;
                }
                candidates.Add(entry);
            }

            // 2) 不动点：能力从基础设施开始，逐轮把"硬依赖已满足"的模块加进来
            var available = new HashSet<string>(m_infrastructureCapabilities, StringComparer.Ordinal);
            var activated = new HashSet<Entry>();
            bool progressed = true;
            while (progressed)
            {
                progressed = false;
                foreach (Entry entry in candidates)
                {
                    if (activated.Contains(entry)) continue;
                    if (!HardRequiresSatisfied(entry, available)) continue;
                    activated.Add(entry);
                    foreach (string capability in entry.Descriptor.ProvidesCapabilities)
                        available.Add(capability);
                    progressed = true;
                }
            }

            // 3) 未激活的候选 = 硬依赖缺失（可能是依赖方刚被下线）
            var unavailable = new List<Entry>();
            foreach (Entry entry in candidates)
            {
                if (!activated.Contains(entry)) unavailable.Add(entry);
            }

            // 4) 装配顺序：保持已有 Active 的稳定顺序，再按解析波次追加新模块
            var ordered = new List<Entry>(activated.Count);
            foreach (Entry entry in m_entries)
            {
                if (wasActive.Contains(entry) && activated.Contains(entry)) ordered.Add(entry);
            }
            foreach (Entry entry in m_entries)
            {
                if (!wasActive.Contains(entry) && activated.Contains(entry)) ordered.Add(entry);
            }

            // 5) 需要卸载的：曾经 Active 但本轮不在装配列表里（按注册逆序 = 依赖方先下线）
            var toDetach = new List<Entry>();
            foreach (Entry entry in m_entries)
            {
                if (wasActive.Contains(entry) && !activated.Contains(entry)) toDetach.Add(entry);
            }
            toDetach.Sort((a, b) => b.Order.CompareTo(a.Order));

            // 6) 目标能力集合：只有本轮 Active 的模块提供的能力才算上线
            var targetCapabilities = new HashSet<string>(m_infrastructureCapabilities, StringComparer.Ordinal);
            foreach (Entry entry in ordered)
            {
                foreach (string capability in entry.Descriptor.ProvidesCapabilities)
                    targetCapabilities.Add(capability);
            }

            // 7) 先卸载旧模块，再发布新能力表，最后装配新模块
            int detached = 0;
            foreach (Entry entry in toDetach)
            {
                DetachEntry(entry, entry.Options.Enabled
                    ? ModuleDetachReason.DependencyLost
                    : ModuleDetachReason.DisabledByUser);
                detached++;
            }
            ReplaceActiveCapabilities(targetCapabilities);

            int attached = 0;
            foreach (Entry entry in ordered)
            {
                entry.Degraded = MissingPreferred(entry, targetCapabilities).Length > 0;
                if (!wasActive.Contains(entry))
                {
                    AttachEntry(entry);
                    attached++;
                }
                else if (entry.Degraded)
                {
                    m_diagnostics.Warn(entry.Descriptor.Id,
                        "软依赖缺失，已降级运行：" + string.Join(", ", MissingPreferred(entry, targetCapabilities)));
                }
            }

            // 8) 终态收敛：Disabled / Unavailable / Faulted 只在"确实不在装配列表里"时落地
            foreach (Entry entry in disabled)
            {
                if (activated.Contains(entry)) continue;
                SetState(entry, ModuleState.Disabled, "配置关闭", "disabled");
            }
            foreach (Entry entry in heldFaulted)
            {
                SetState(entry, ModuleState.Faulted,
                    entry.Reason.Length > 0 ? entry.Reason : "已熔断，等待冷却或人工恢复", "faulted-held");
            }
            foreach (Entry entry in unavailable)
            {
                string[] missing = MissingHardRequires(entry, targetCapabilities);
                SetState(entry, ModuleState.Unavailable,
                    "缺少能力：" + string.Join(", ", missing), "unavailable");
            }

            // 9) 必需能力检查
            var essentialMissing = new List<string>();
            foreach (string capability in m_requiredEssentials)
            {
                if (!targetCapabilities.Contains(capability)) essentialMissing.Add(capability);
            }

            return new ModuleResolution(ordered.Count, disabled.Count, unavailable.Count,
                CountState(ModuleState.Faulted), attached, detached, essentialMissing.ToArray());
        }

        private readonly List<string> m_requiredEssentials = new List<string>();

        /// <summary>声明"没有它就不能开局"的能力（由上层在启动时登记）。</summary>
        public void RequireEssential(string capability)
        {
            if (!string.IsNullOrWhiteSpace(capability) && !m_requiredEssentials.Contains(capability))
                m_requiredEssentials.Add(capability);
        }

        private static bool HardRequiresSatisfied(Entry entry, HashSet<string> available)
        {
            foreach (string capability in entry.Descriptor.RequiresCapabilities)
            {
                if (!available.Contains(capability)) return false;
            }
            return true;
        }

        private static string[] MissingHardRequires(Entry entry, HashSet<string> available)
        {
            var missing = new List<string>();
            foreach (string capability in entry.Descriptor.RequiresCapabilities)
            {
                if (!available.Contains(capability)) missing.Add(capability);
            }
            return missing.ToArray();
        }

        private static string[] MissingPreferred(Entry entry, HashSet<string> available)
        {
            var missing = new List<string>();
            foreach (string capability in entry.Descriptor.PreferredCapabilities)
            {
                if (!available.Contains(capability)) missing.Add(capability);
            }
            return missing.ToArray();
        }

        private int CountState(ModuleState state)
        {
            int count = 0;
            foreach (Entry entry in m_entries) if (entry.State == state) count++;
            return count;
        }

        private void AttachEntry(Entry entry)
        {
            var context = new ModuleContext(entry.Descriptor.Id, m_capabilities, m_diagnostics, m_ports);
            try
            {
                entry.Module.Attach(context);
                SetState(entry, ModuleState.Active, entry.Reason, "attach");
            }
            catch (Exception error)
            {
                entry.LastFaultMs = Environment.TickCount64;
                entry.ConsecutiveFaults++;
                m_diagnostics.Error(entry.Descriptor.Id, "装配失败，模块已被隔离", error);
                SetState(entry, ModuleState.Faulted, "Attach 抛异常：" + error.GetType().Name, "faulted");
            }
        }

        private void DetachEntry(Entry entry, ModuleDetachReason reason)
        {
            try
            {
                entry.Module.Detach(reason);
            }
            catch (Exception error)
            {
                m_diagnostics.Error(entry.Descriptor.Id, "卸载时抛异常（已忽略）", error);
            }
            SetState(entry, reason == ModuleDetachReason.Faulted ? ModuleState.Faulted : ModuleState.Detached,
                reason.ToString(), "detach");
        }

        private void SetState(Entry entry, ModuleState state, string reason, string reasonKind)
        {
            ModuleState previous = entry.State;
            entry.State = state;
            entry.Reason = reason ?? string.Empty;
            if (previous == state) return;
            m_diagnostics.Info(entry.Descriptor.Id, "模块状态 " + previous + " → " + state + "（" + reasonKind + "）");
            StatusChanged?.Invoke(new ModuleStatusChange(entry.Descriptor.Id, previous, state, reason));
        }

        private void ReplaceActiveCapabilities(HashSet<string> target)
        {
            var removed = new List<string>();
            foreach (string capability in m_activeCapabilities) if (!target.Contains(capability)) removed.Add(capability);
            var added = new List<string>();
            foreach (string capability in target) if (!m_activeCapabilities.Contains(capability)) added.Add(capability);
            foreach (string capability in removed) m_activeCapabilities.Remove(capability);
            foreach (string capability in added) m_activeCapabilities.Add(capability);
            foreach (string capability in removed) m_capabilities.RaiseChanged(capability, false);
            foreach (string capability in added) m_capabilities.RaiseChanged(capability, true);
        }

        private void RefreshCapabilities() => Resolve("capability-change");

        // ------------------------------------------------------------------
        // 帧调度：逐模块隔离
        // ------------------------------------------------------------------
        public void Tick(in ModuleTickContext tick)
        {
            ThrowIfDisposed();
            List<Entry> faulted = null;
            foreach (Entry entry in m_entries)
            {
                if (entry.State != ModuleState.Active) continue;
                try
                {
                    entry.Module.Tick(in tick);
                    entry.ConsecutiveFaults = 0;
                }
                catch (Exception error)
                {
                    entry.ConsecutiveFaults++;
                    entry.LastFaultMs = Environment.TickCount64;
                    m_diagnostics.Error(entry.Descriptor.Id,
                        "Tick 抛异常（第 " + entry.ConsecutiveFaults + " 次）", error);
                    if (entry.Options.FaultThreshold > 0 &&
                        entry.ConsecutiveFaults >= entry.Options.FaultThreshold)
                    {
                        (faulted ?? (faulted = new List<Entry>())).Add(entry);
                    }
                }
            }
            if (faulted == null) return;
            // 先遍历完再改状态：保证本帧其它模块照常运行
            foreach (Entry entry in faulted)
            {
                SetState(entry, ModuleState.Faulted,
                    "连续失败 " + entry.ConsecutiveFaults + " 次，已熔断下线", "faulted");
                DetachEntry(entry, ModuleDetachReason.Faulted);
            }
            Resolve("fault-cascade");   // 依赖它的模块会在这一轮变成 Unavailable / 降级
        }

        // ------------------------------------------------------------------
        // 热插拔：运行中下线 / 上线单个模块
        // ------------------------------------------------------------------
        public bool SetEnabled(string moduleId, bool enabled)
        {
            if (!m_byId.TryGetValue(moduleId, out Entry entry)) return false;
            if (!entry.Descriptor.HotPluggable && !enabled)
            {
                m_diagnostics.Warn(moduleId, "该模块不支持热插拔下线");
                return false;
            }
            entry.Options.Enabled = enabled;
            Resolve(enabled ? "hot-enable" : "hot-disable");
            return true;
        }

        public IReadOnlyList<ModuleStatus> Statuses
        {
            get
            {
                var list = new List<ModuleStatus>(m_entries.Count);
                foreach (Entry entry in m_entries)
                {
                    list.Add(new ModuleStatus(entry.Descriptor.Id, entry.Descriptor.DisplayName,
                        entry.State, entry.Degraded, entry.Reason, entry.ConsecutiveFaults,
                        entry.Descriptor.ProvidesCapabilities));
                }
                return list;
            }
        }

        public ModuleState GetState(string moduleId) =>
            m_byId.TryGetValue(moduleId, out Entry entry) ? entry.State : ModuleState.Registered;

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            for (int i = m_entries.Count - 1; i >= 0; i--)
            {
                Entry entry = m_entries[i];
                if (entry.State != ModuleState.Active) continue;
                DetachEntry(entry, ModuleDetachReason.HostShutdown);
            }
            ReplaceActiveCapabilities(new HashSet<string>(m_infrastructureCapabilities, StringComparer.Ordinal));
        }

        private void ThrowIfDisposed()
        {
            if (m_disposed) throw new ObjectDisposedException(nameof(ModuleHost));
        }

        private sealed class ModuleContext : IModuleContext
        {
            private readonly Dictionary<string, object> m_ports;

            public ModuleContext(string moduleId, ICapabilityRegistry capabilities,
                IModuleDiagnostics diagnostics, Dictionary<string, object> ports)
            {
                ModuleId = moduleId;
                Capabilities = capabilities;
                Diagnostics = diagnostics;
                m_ports = ports;
            }

            public string ModuleId { get; }
            public ICapabilityRegistry Capabilities { get; }
            public IModuleDiagnostics Diagnostics { get; }

            public bool TryGetPort<T>(out T port) where T : class
            {
                if (m_ports.TryGetValue(typeof(T).FullName, out object value) && value is T typed)
                {
                    port = typed;
                    return true;
                }
                port = null;
                return false;
            }
        }
    }

    internal sealed class CapabilityRegistry : ICapabilityRegistry
    {
        private readonly HashSet<string> m_active;

        public CapabilityRegistry(HashSet<string> active) => m_active = active;

        public event Action<string, bool> CapabilityChanged;

        public bool Has(string capability) => capability != null && m_active.Contains(capability);

        public IReadOnlyCollection<string> Active => m_active;

        public void RaiseChanged(string capability, bool added) => CapabilityChanged?.Invoke(capability, added);
    }

    internal sealed class NullModuleDiagnostics : IModuleDiagnostics
    {
        public void Info(string moduleId, string message) { }
        public void Warn(string moduleId, string message) { }
        public void Error(string moduleId, string message, Exception exception) { }
    }
}
