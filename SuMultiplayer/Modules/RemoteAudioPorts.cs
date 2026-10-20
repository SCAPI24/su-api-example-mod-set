using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// "这条声音已经由另一个定向事件中继过"的登记口（实现 = RemoteActionAudioReplica）。
    /// 营火 / 烧尽 / 拾取 / 水花 / 爆炸 / 武器开火等适配器在**主机本端**播放同一条声音后调用，
    /// 全局连续捕获下一次读到它时按 (名字 / 目录前缀, 主机侧衰减音量) 精确跳过，避免双播。
    ///
    /// <paramref name="count"/> 必须等于"这次定向事件会覆盖的、主机本端实际排进音频队列的声音数"；
    /// 到期没匹配到的会在实现里计入 coverUnused 并限流告警（不静默）。
    /// </summary>
    public interface IHostSoundCoverPort
    {
        /// <summary>精确声音名（PlaySound 的 name）。</summary>
        void CoverPlayedSound(string sound, Vector3 position, float volume, float minDistance,
            int count, string owner);

        /// <summary>PlayRandomSound 的目录前缀（引擎选中的具体 clip 名无法预知）。</summary>
        void CoverPlayedSoundDirectory(string directory, Vector3 position, float volume,
            float minDistance, int count, string owner);

        /// <summary>
        /// 音量在 [<paramref name="minVolume"/>, <paramref name="maxVolume"/>] 区间内（例如
        /// <c>ProjectileFirePresentation.PlayThrow</c> 的随机音量 0.2..0.3）；
        /// 覆盖账本按区间中心 + 半宽匹配。
        /// </summary>
        void CoverPlayedSoundRange(string sound, Vector3 position, float minVolume,
            float maxVolume, float minDistance, int count, string owner);
    }

    /// <summary>
    /// 远程 / 全局声音中继的**引擎侧端口**（实现 = Engine/RemoteActionAudioReplica.cs，组合根注入）。
    /// 模块层（Modules/）保持不依赖 Game，因此边界 / 距离 / 去重 / 限速都能被
    /// P:\tmp\tsverify 用同一份源码直接编译（假实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：连续捕获引擎音频队列（SuSubsystemAudio 在 base.Update 排空前回调），把每帧
    ///     实际排入的声音交给模块批量中继；**不删队列、不静音、不改变主机播放**；
    ///   · 远端动作窗口：仍只覆盖"主机替远端请求执行"的同步区间，锚点精确；
    ///   · 客户端：<see cref="Present"/> 只调 <c>SubsystemAudio.PlaySound</c>，不写格 /
    ///     不生成实体 / 不造成伤害；地形与库存仍走既有 CellChange / ContainerState。
    /// </summary>
    public interface IRemoteAudioPort : IHostSoundCoverPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>每帧调用：Project 换新 / 卸载时清掉可能残留的捕获状态（明确计数）。</summary>
        void Pump();

        /// <summary>
        /// 模块决定是否开启全局连续捕获（主机 + 已连接 + 至少一个可定位玩家端）。
        /// 关闭时引擎侧只维护客户端本机回声账本，不做队列差集（单机零成本）。
        /// </summary>
        void SetHostCaptureEnabled(bool enabled);

        /// <summary>
        /// 排空引擎侧已捕获、尚未交给模块的全局声音（只读；引擎不负责发送）。
        /// false = 全局捕获当前不可用（<paramref name="reason"/> 明确）。
        /// <paramref name="droppedCap"/> = 自上次排空以来因环缓冲满被丢的最旧声音数。
        /// </summary>
        bool TryDrainHostSounds(List<RemoteSoundClipData> target, out int droppedCap,
            out string reason);

        /// <summary>取走最近一次捕获故障（限流告警用）；没有故障 = false。</summary>
        bool TryTakeHostCaptureFault(out string reason);

        /// <summary>
        /// 客户端：本机是否刚播过同名声音（自己的方块动作 / UI / 本地生物 / 预测表现）。
        /// true = 这一次主机中继应跳过（LocalEchoSkipped），客户端绝不再重复播一遍。
        /// </summary>
        bool TryConsumeLocalEcho(string sound);

        /// <summary>
        /// 主机：打开一个捕获窗口。返回 false = 明确失败（<paramref name="reason"/> 可直接
        /// 写日志 / 计数）；调用方仍然执行动作，只是这次声音不会中继。
        /// <paramref name="anchor"/> = 本次交互的方块格（动作锚点）。
        /// </summary>
        bool TryBeginCapture(int requesterClientId, Vector3 anchor, out string reason);

        /// <summary>
        /// 主机：关闭捕获窗口并把窗口内新排进音频队列的声音取出来。
        /// **无论动作是否抛异常，调用方都必须在 finally 里调用它**（不允许泄漏窗口）。
        /// false = 数据不可用（原因明确），此时 <paramref name="sounds"/> 为空。
        /// </summary>
        bool EndCapture(out RemoteSoundClipData[] sounds, out string reason);

        /// <summary>
        /// 主机：收集当前在线玩家端（clientId + 主机侧角色位置）。
        /// 读不到位置的端仍然返回（HasPosition=false），由收件人策略明确计数。
        /// </summary>
        bool TryCollectPeers(List<RemoteSoundPeer> target, out string reason);

        /// <summary>客户端：本机监听者位置（活动相机 / 本机角色）；读不到 = false + reason。</summary>
        bool TryGetLocalListenerPosition(out Vector3 position, out string reason);

        /// <summary>
        /// 客户端：**本机所有本地角色**的听音位置（分裂屏 / 多角色逐条；远端化身不算）。
        /// 表现计划按"离声源最近的那条"做距离裁剪 / 音量重投影；一条都读不到 = false + reason
        /// （绝不拿原点猜）。客户端收到主机槽位声音（<c>SlotSource</c>）时用它做多角色感知。
        /// </summary>
        bool TryGetLocalListenerPositions(List<Vector3> target, out string reason);

        /// <summary>
        /// 客户端：按记录到的名字 / 位置 / 音量 / 音高 / 最小距离 / autoDelay 播放。
        /// 返回实际播放条数（0..sounds.Length）；播放失败必须写 <paramref name="reason"/>。
        /// </summary>
        int Present(RemoteSoundClipData[] sounds, out string reason);

        /// <summary>状态行用：引擎侧计数 / 诊断文本（不改变行为）。</summary>
        string Describe();
    }

    /// <summary>
    /// 方块使用 / 容器编辑等**主机侧远端动作执行点**用的中继入口（实现 = RemoteAudioSyncModule）。
    /// 模块只在这里开 / 关窗口，音频捕获与网络发送都在中继模块内部；
    /// 调用方不得因为中继失败而改变动作本身的执行结果。
    /// </summary>
    public interface IRemoteAudioRelayPort
    {
        /// <summary>是否可用（已装配 + 本端是主机 + 引擎端口就绪）。</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 动作执行前调用：打开捕获窗口。<paramref name="action"/> 只用于日志
        /// （如 "block-use" / "container-edit"）。绝不抛异常。
        /// </summary>
        void BeginRemoteAction(int requesterClientId, Vector3 anchor, string action);

        /// <summary>
        /// 动作执行后调用（**必须在 finally 里**）：关闭窗口并把采集到的声音中继出去。
        /// 绝不抛异常。
        /// </summary>
        void EndRemoteAction(int requesterClientId, string action);
    }
}
