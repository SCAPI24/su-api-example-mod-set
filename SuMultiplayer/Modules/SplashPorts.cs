using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 一批水花的实际表现结果（由引擎适配层填写；模块只累加计数）。
    /// </summary>
    public readonly struct WaterSplashPresentationResult
    {
        public WaterSplashPresentationResult(int presented, int particlesCreated,
            int soundsPlayed, int skippedDistance, int missingAudio)
        {
            Presented = presented;
            ParticlesCreated = particlesCreated;
            SoundsPlayed = soundsPlayed;
            SkippedDistance = skippedDistance;
            MissingAudio = missingAudio;
        }

        /// <summary>至少生成一个粒子的水花数。</summary>
        public int Presented { get; }
        public int ParticlesCreated { get; }
        public int SoundsPlayed { get; }
        public int SkippedDistance { get; }
        /// <summary>本该有声音但本端没有 SubsystemAudio（明确计数，不静默）。</summary>
        public int MissingAudio { get; }
    }

    /// <summary>
    /// 水花子系统外壳 → 引擎适配层的捕获窗口（主机侧）。窗口只包住一次
    /// <c>SubsystemPickables.Update</c> / <c>SubsystemProjectiles.Update</c>；窗口内新增的
    /// <c>WaterSplashParticleSystem</c> 就是引擎刚刚为本批拾取物 / 投射物创建的水花。
    ///
    /// 为什么用窗口捕获而不是复制引擎判定：
    ///   · 引擎的落水判定依赖 <c>SubsystemFluidBlockBehavior.CalculateFlowSpeed</c> 与每帧
    ///     位置，复制一遍（阈值 / 顺序 / 浮点）必然偏离；
    ///   · <c>SubsystemParticles.AddParticleSystem</c> 是非虚方法（Source:
    ///     Survivalcraft/Game/SubsystemParticles.cs:32），派生外壳无法截获；但粒子表
    ///     <c>m_particleSystems</c> 是同一实例上的可观察容器，窗口前后差集就是精确的
    ///     “本端刚创建了什么”。
    /// </summary>
    public interface IWaterSplashHostSink
    {
        /// <summary>
        /// 主机：开始一次捕获窗口。返回 false = 本帧不捕获（<paramref name="reason"/> 可定位）；
        /// 调用方必须仍执行 base.Update（主机原生表现不受影响）。
        /// </summary>
        bool BeginSplashCapture(out string reason);

        /// <summary>主机：结束捕获窗口并把窗口内的水花交给模块（窗口跨 Project 变化时明确丢弃）。</summary>
        void EndSplashCapture();
    }

    /// <summary>
    /// 水花表现同步的引擎侧端口（实现方 = Engine/WaterSplashReplicaEngine.cs，组合根注入）。
    /// 模块层（Modules/）保持不依赖 Game，因此编解码 / 距离 / 上限 / 排序去重 / 限速都能被
    /// P:\tmp\tsverify 用同一份源码直接编译（假实现实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：引擎在自己 Update 里**已经创建**粒子 + 播放声音；本端口只上报既成事实；
    ///   · 客户端：<see cref="Present"/> 只允许 <c>WaterSplashParticleSystem</c> 与
    ///     <c>Audio/Splashes</c>，禁止写格 / 生成实体 / 伤害。
    /// </summary>
    public interface IWaterSplashPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>主机：引擎刚创建了一个水花（窗口内差集，位置 / large / 声音已确定）。</summary>
        event Action<WaterSplashData> HostSplash;

        /// <summary>每帧调用：Project 换新 / 注入子系统出现时重新挂接两个外壳。</summary>
        void Pump();

        /// <summary>
        /// 主机：打开 / 关闭捕获窗口。只有“本端是主机且至少有一个在线玩家端”时才需要捕获
        /// （单机 / 无客户端时不产生任何网络表现）。false = 明确失败，模块计数并限流写日志。
        /// </summary>
        bool SetHostCaptureEnabled(bool enabled, out string reason);

        /// <summary>
        /// 主机：收集在线玩家端（clientId + 主机侧角色位置）。读不到位置的端仍然返回
        /// （HasPosition=false），由收件人策略明确计数。
        /// </summary>
        bool TryCollectPeers(List<RemoteSoundPeer> target, out string reason);

        /// <summary>
        /// 客户端：本机音频监听者位置（活动相机 → 本机角色）；读不到 false + reason
        /// （调用方明确计数，不猜原点）。
        /// </summary>
        bool TryGetListenerPosition(out Vector3 position, out string reason);

        /// <summary>
        /// 客户端：只做表现（粒子 + 可选声音）；不写格、不生成实体、不伤害。
        /// false = 明确失败（<paramref name="reason"/> 可直接写日志 / 计数）。
        /// </summary>
        bool Present(in WaterSplashEventData data,
            out WaterSplashPresentationResult result, out string reason);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }
}
