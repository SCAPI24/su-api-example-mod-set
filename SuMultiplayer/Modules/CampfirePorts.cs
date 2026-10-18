using System;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 营火熄灭火声的引擎侧端口（实现方 = Engine/CampfireEffectReplicaEngine.cs，组合根注入）。
    /// 模块层（Modules/）保持不依赖 Game，因此编解码 / 排序去重 / 限速都能被
    /// P:\tmp\tsverify 用同一份源码直接编译（假实现实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：引擎 <c>SubsystemCampfireBlockBehavior</c> 在燃料烧到 0 时**已经在本端**
    ///     播过 Audio/Sizzles；<see cref="HostCampfireExtinguished"/> 只上报这个既成事实；
    ///   · 客户端：<see cref="Present"/> 只允许播放 <c>Audio/Sizzles</c>，禁止写格 /
    ///     生成实体 / 伤害。地形变化仍走既有的 CellChange 流。
    /// </summary>
    public interface ICampfireEffectPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>主机：一格营火刚刚烧尽（格子仍是营火 209、data = 0）。</summary>
        event Action<CampfireExtinguishData> HostCampfireExtinguished;

        /// <summary>每帧调用：Project 换新 / 角色切换后刷新客户端判定。</summary>
        void Pump();

        /// <summary>
        /// 客户端：只播放 Audio/Sizzles（不写格、不生成实体、不伤害）。
        /// false = 明确失败（<paramref name="reason"/> 可直接写日志 / 计数）。
        /// </summary>
        bool Present(in CampfireExtinguishData data, out string reason);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }
}
