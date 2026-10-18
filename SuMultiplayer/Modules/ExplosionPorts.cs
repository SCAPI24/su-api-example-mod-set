using System;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 爆炸同步的引擎侧端口（实现方 = Engine/ExplosionReplicaEngine.cs，组合根注入）。
    ///
    /// 模块层（Modules/）保持不依赖 Game：主机检测到的爆炸记录、客户端表现、客户端请求的
    /// 主机执行都通过这里进出；所以编解码 / 排序 / 去重 / 计数可以被 P:\tmp\tsverify
    /// 用同一份源码直接编译（假世界实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：引擎是唯一模拟方，<see cref="HostExplosionExecuted"/> 只上报**已经发生**的爆炸；
    ///   · 客户端：<see cref="Present"/> 只允许粒子 / 声音 / 镜头震动，禁止改地形 / 生成实体 / 伤害；
    ///   · 客户端请求：<see cref="TryExecuteRequest"/> 由主机校验并执行，执行失败返回明确原因。
    /// </summary>
    public interface IExplosionReplicaPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>主机：一个爆炸实例已经由引擎执行完（含链式爆炸）。</summary>
        event Action<ExplosionEventData> HostExplosionExecuted;

        /// <summary>主机：爆炸伤害结算后，某个玩家掉了血（表现用；客户端不得改血量）。</summary>
        event Action<PlayerDamageData> HostPlayerDamaged;

        /// <summary>客户端：本机玩家要投掷 / 点燃爆炸物（发给主机，由主机执行）。</summary>
        event Action<ExplosionRequestData> ClientRequestSubmitted;

        /// <summary>每帧调用：Project 换新 / 注入子系统出现时重新挂接。</summary>
        void Pump();

        /// <summary>
        /// 主机：校验并执行一条客户端爆炸请求。
        /// false = 明确拒绝（<paramref name="reason"/> 可直接写进回执 / 日志），
        /// 调用方必须回一条拒绝结果，绝不放本地爆炸。
        /// </summary>
        bool TryExecuteRequest(int clientId, in ExplosionRequestData request,
            out Vector3 resultPosition, out string reason);

        /// <summary>客户端：主机接受了请求后扣掉本机玩家的道具（失败写原因，不静默）。</summary>
        bool TryConsumeRequestItem(in ExplosionRequestData request, out string reason);

        /// <summary>客户端：只做表现（粒子 / 声音 / 震动）；不模拟、不改地形、不生成实体、不伤害。</summary>
        bool Present(in ExplosionEventData explosion, out string reason);

        /// <summary>客户端：玩家受伤事件的表现（当前只记录，不改血量；血量同步是后续项）。</summary>
        bool PresentPlayerDamage(in PlayerDamageData damage, out string reason);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }
}
