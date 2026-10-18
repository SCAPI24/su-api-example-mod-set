using System;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 一批烧尽表现的实际结果（由引擎适配层的表现端口填写；模块只累加计数）。
    /// </summary>
    public readonly struct BurnAwayPresentationResult
    {
        public BurnAwayPresentationResult(int presented, int debris, int sounds,
            int skippedDistance, int skippedCap, int missingAudio)
        {
            Presented = presented;
            Debris = debris;
            Sounds = sounds;
            SkippedDistance = skippedDistance;
            SkippedCap = skippedCap;
            MissingAudio = missingAudio;
        }

        /// <summary>至少得到一种表现（碎屑 / 声音）的格数。</summary>
        public int Presented { get; }
        public int Debris { get; }
        public int Sounds { get; }
        public int SkippedDistance { get; }
        public int SkippedCap { get; }
        /// <summary>该格本来该有声音但本端没有 SubsystemAudio（明确计数，不静默）。</summary>
        public int MissingAudio { get; }
    }

    /// <summary>
    /// 火外壳 → 引擎适配层的上报点（主机侧）。火外壳在每次 Update 后最多调用一次，
    /// 参数已经是**去重并封顶**的一批烧尽格；<paramref name="droppedCells"/> 是超出
    /// 单条消息上限 / 快照上限而没有带上的格数（调用方必须计数和限流记录）。
    /// </summary>
    public interface IBurnAwayEffectSink
    {
        void OnHostBurnAwayBatch(BurnAwayCellData[] cells, int droppedCells);
    }

    /// <summary>
    /// 烧尽表现同步的引擎侧端口（实现方 = Engine/BurnAwayEffectReplicaEngine.cs，
    /// 组合根注入）。模块层（Modules/）保持不依赖 Game，因此编解码 / 排序 / 去重 /
    /// 距离与上限 / 限速都能被 P:\tmp\tsverify 用同一份源码直接编译（假世界实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：引擎的 5 秒烧尽分支是唯一事实来源，<see cref="HostBurnAwayBatch"/>
    ///     只上报**已经发生**的烧尽批次；
    ///   · 客户端：<see cref="Present"/> 只允许 <c>BurntDebrisParticleSystem</c> 和
    ///     <c>Audio/Sizzles</c>，禁止写格 / 生成实体 / 伤害。
    /// </summary>
    public interface IBurnAwayEffectPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>主机：一批烧尽格已经由引擎处理完（已去重、已封顶）。</summary>
        event Action<BurnAwayEffectData> HostBurnAwayBatch;

        /// <summary>每帧调用：Project 换新 / 注入子系统出现时重新挂接火外壳。</summary>
        void Pump();

        /// <summary>
        /// 客户端：只做表现（碎屑粒子 / 嘶嘶声）；不写格、不生成实体、不伤害。
        /// false = 明确失败（<paramref name="reason"/> 可直接写日志 / 计数），本次没有画面。
        /// </summary>
        bool Present(in BurnAwayEffectData effect, out BurnAwayPresentationResult result,
            out string reason);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }
}
