using System;
using System.Collections.Generic;

namespace SuMultiplayer.Modules
{
    /// <summary>模块在宿主里的状态。演示页标记：DEC01。</summary>
    public enum ModuleState
    {
        /// <summary>已注册但尚未解析/装配。</summary>
        Registered = 0,

        /// <summary>正在运行，能力已上线。</summary>
        Active = 1,

        /// <summary>被显式关闭（配置 / 运行时下线）。</summary>
        Disabled = 2,

        /// <summary>依赖的能力缺失，无法启动（不影响别的模块）。</summary>
        Unavailable = 3,

        /// <summary>自身持续抛异常，已被熔断下线；能力同时撤回。</summary>
        Faulted = 4,

        /// <summary>已 Detach，等待重新装配（热插拔用）。</summary>
        Detached = 5
    }

    public enum ModuleDetachReason
    {
        HostShutdown = 0,
        DisabledByUser = 1,
        DependencyLost = 2,
        Faulted = 3,
        SessionReset = 4
    }

    /// <summary>
    /// 模块描述符。**依赖写成"能力"而不是"模块名"**——这是解耦的关键：
    /// 模块只关心"世界上有没有这个能力"，不关心是谁提供的、由几个模块提供。
    /// 演示页标记：DEC02。
    /// </summary>
    public sealed class ModuleDescriptor
    {
        public ModuleDescriptor(string id, string displayName = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("模块 id 不能为空", nameof(id));
            Id = id;
            DisplayName = displayName ?? id;
        }

        /// <summary>稳定 id（用于诊断、控制台、能力协商，不用于协议字段）。</summary>
        public string Id { get; }

        public string DisplayName { get; }

        /// <summary>硬依赖：缺失时本模块直接 Unavailable（只允许同一条业务链内部使用）。</summary>
        public string[] RequiresCapabilities { get; set; } = Array.Empty<string>();

        /// <summary>软依赖：缺失时本模块仍然运行，但必须走降级路径并标记 Degraded。</summary>
        public string[] PreferredCapabilities { get; set; } = Array.Empty<string>();

        /// <summary>本模块上线后对外提供的能力。</summary>
        public string[] ProvidesCapabilities { get; set; } = Array.Empty<string>();

        /// <summary>默认是否启用（可被配置或运行时关闭）。</summary>
        public bool EnabledByDefault { get; set; } = true;

        /// <summary>整个联机功能的最小必需集合；缺失就完全不可用（极少，通常是会话与传输）。</summary>
        public bool Essential { get; set; }

        /// <summary>热插拔：允许运行中上下线。</summary>
        public bool HotPluggable { get; set; } = true;
    }

    /// <summary>模块可用的运行参数。</summary>
    public sealed class ModuleOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>连续失败多少次后熔断下线。0 = 不熔断（仅记录）。</summary>
        public int FaultThreshold { get; set; } = 3;

        /// <summary>熔断后多少毫秒允许尝试自动恢复（0 = 不自动恢复）。</summary>
        public int RetryCooldownMs { get; set; }
    }

    /// <summary>一帧的调度上下文（模块只读）。</summary>
    public readonly struct ModuleTickContext
    {
        public ModuleTickContext(long frameIndex, double now, float deltaTime, bool isHost, bool isConnected)
        {
            FrameIndex = frameIndex;
            Now = now;
            DeltaTime = deltaTime;
            IsHost = isHost;
            IsConnected = isConnected;
        }

        public long FrameIndex { get; }
        public double Now { get; }
        public float DeltaTime { get; }
        public bool IsHost { get; }
        public bool IsConnected { get; }
    }

    /// <summary>能力查询：模块用它判断"我要的功能现在在不在"。</summary>
    public interface ICapabilityRegistry
    {
        bool Has(string capability);

        IReadOnlyCollection<string> Active { get; }

        /// <summary>上线/下线变化（用于让模块自己重算降级路径，而不是靠调用别的模块）。</summary>
        event Action<string, bool> CapabilityChanged;
    }

    /// <summary>诊断出口：模块只允许通过它写日志，不允许直接引用引擎日志或别的模块。</summary>
    public interface IModuleDiagnostics
    {
        void Info(string moduleId, string message);
        void Warn(string moduleId, string message);
        void Error(string moduleId, string message, Exception exception);
    }

    /// <summary>
    /// 模块能看到的"整个世界"。除了端口与能力查询，看不到任何别的模块——
    /// 因此任何模块下线都不会让别的模块编译不过或崩在空引用上。
    /// 演示页标记：DEC03。
    /// </summary>
    public interface IModuleContext
    {
        string ModuleId { get; }

        ICapabilityRegistry Capabilities { get; }

        IModuleDiagnostics Diagnostics { get; }

        /// <summary>
        /// 取端口。**必须用 TryGet 并处理 false**：端口可能因为提供方下线而消失，
        /// 模块要降级而不是崩溃（例如传输端口没了就只写队列不发送）。
        /// </summary>
        bool TryGetPort<T>(out T port) where T : class;
    }

    public interface IMultiplayerModule
    {
        ModuleDescriptor Descriptor { get; }

        /// <summary>装配：只拿上下文；不要在这里假设任何端口存在。</summary>
        void Attach(IModuleContext context);

        /// <summary>推进本模块拥有的有界队列（宿主已隔离异常）。</summary>
        void Tick(in ModuleTickContext tick);

        /// <summary>下线：只清理本模块自己的状态，禁止清空全局或别人的东西。</summary>
        void Detach(ModuleDetachReason reason);
    }

    /// <summary>模块状态快照（给 UI / 控制台 / 诊断）。</summary>
    public readonly struct ModuleStatus
    {
        public ModuleStatus(string id, string displayName, ModuleState state, bool degraded,
            string reason, int consecutiveFaults, string[] provides)
        {
            Id = id;
            DisplayName = displayName;
            State = state;
            Degraded = degraded;
            Reason = reason;
            ConsecutiveFaults = consecutiveFaults;
            Provides = provides;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public ModuleState State { get; }
        /// <summary>软依赖缺失时仍在运行，但功能受限。</summary>
        public bool Degraded { get; }
        public string Reason { get; }
        public int ConsecutiveFaults { get; }
        public string[] Provides { get; }
    }

    /// <summary>
    /// 实时地图 v9 的**主机端动物位置**来源。实现方只枚举主机自己世界里的非玩家生物，
    /// 写入的条目**只含位置**（<see cref="SuMultiplayer.Terrain.LiveMapAnimal"/> 的 Key 仅用于排序），
    /// 不提供名字 / 网络 id / 生命值；客户端不实现 / 不贡献任何条目。
    /// 返回 -1 = 明确失败（<paramref name="reason"/> 写清原因，调用方不得当成"没有动物"）；
    /// 返回 0 = 世界里确实没有动物（合法结果）。
    /// </summary>
    public interface IHostCreatureCellSource
    {
        int CaptureHostCreatureCells(List<SuMultiplayer.Terrain.LiveMapAnimal> target, out string reason);
    }

    public readonly struct ModuleStatusChange
    {
        public ModuleStatusChange(string moduleId, ModuleState from, ModuleState to, string reason)
        {
            ModuleId = moduleId;
            From = from;
            To = to;
            Reason = reason;
        }

        public string ModuleId { get; }
        public ModuleState From { get; }
        public ModuleState To { get; }
        public string Reason { get; }
    }
}
