using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>主机侧一个玩家端的身份 + 主机侧联机化身位置。</summary>
    public readonly struct AmbientSoundPeer
    {
        public AmbientSoundPeer(int clientId, bool hasPosition, Vector3 position)
        {
            ClientId = clientId;
            HasPosition = hasPosition;
            Position = position;
        }

        public int ClientId { get; }
        public bool HasPosition { get; }
        public Vector3 Position { get; }
    }

    /// <summary>
    /// 循环环境音同步的**引擎侧端口**（实现 = Engine/AmbientSoundReplicaEngine.cs，组合根注入）。
    /// 模块层（Modules/）保持不引用 Game，因此边界 / 编解码 / 扫描纯逻辑都能被
    /// P:\tmp\tsverify 用同一份源码直接编译（假实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：只读地形格子 + 主机侧化身位置，算出每个客户端应该听到的 4 个音量；
    ///     **不修改** 主机自己的 <c>SubsystemAmbientSounds</c> 字段，也不写格 / 生成实体；
    ///   · 客户端：只把主机下发的最终音量写进本机引擎端口，
    ///     <c>SuSubsystemAmbientSounds</c> 在自己的 Update 里施加（引擎仍拥有 loop / fade /
    ///     Sizzles 逻辑）；客户端不跑任何被闸门挡住的方块行为。
    /// </summary>
    public interface IAmbientSoundPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>每帧调用：Project 换新 / 卸载时清掉可能残留的客户端音量与引擎挂接。</summary>
        void Pump();

        /// <summary>
        /// 主机：收集当前在线玩家端（clientId + 主机侧化身位置）。读不到位置的端仍然返回
        /// （HasPosition=false），由模块明确计入 NoAvatar。false = 没有玩家端 / 引擎不可用
        /// （<paramref name="reason"/> 明确，例如 no-clients / no-project）。
        /// </summary>
        bool TryCollectClients(List<AmbientSoundPeer> target, out string reason);

        /// <summary>
        /// 主机：以某个客户端化身位置做一次有界扫描，返回该端应听到的最终音量。
        /// false = 明确失败（<paramref name="reason"/> 可直接写日志 / 计数）。
        /// 绝不写主机音频字段；只读格子。
        /// </summary>
        bool TryComputeVolumes(int clientId, Vector3 position, out AmbientVolumes volumes,
            out string reason);

        /// <summary>
        /// 客户端：接受一条主机下发的最终音量。false = 本端不是客户端副本 / 值非法 /
        /// revision 不新（<paramref name="reason"/> 明确）。成功后下一帧
        /// <c>SuSubsystemAmbientSounds.Update</c> 施加。
        /// </summary>
        bool ApplyClientVolumes(uint revision, in AmbientVolumes volumes, out string reason);

        /// <summary>客户端：断线 / 重连 / 退房时清空已下发音量（循环下一帧暂停）。</summary>
        void ResetClientVolumes(string reason);

        /// <summary>扫描因火焰格 / 格子总数达到硬上界而截断的次数（明确计数，不静默）。</summary>
        long ScanCapped { get; }

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }

    /// <summary>
    /// 引擎外壳 <c>SuSubsystemAmbientSounds</c> 读取网络音量的最小表面。
    /// 由同一个 <see cref="IAmbientSoundPort"/> 实现提供（组合根在 Attach 后挂上去）。
    ///
    /// <see cref="IsClientReplica"/> = false 时外壳**完全不碰**三个字段（主机 / 单机原样）；
    /// = true 且 <see cref="TryGetClientVolumes"/> = false（尚未收到 / 已断线）时外壳把三个
    /// 字段清零，确保客户端的循环不会卡在旧音量上。
    /// </summary>
    public interface IAmbientSoundVolumeSource
    {
        bool IsClientReplica { get; }

        bool TryGetClientVolumes(out AmbientVolumes volumes, out string reason);
    }
}
