using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 远程动作声音中继的**引擎侧端口**（实现 = Engine/RemoteActionAudioReplica.cs，组合根注入）。
    /// 模块层（Modules/）保持不依赖 Game，因此边界 / 距离 / 去重 / 限速都能被
    /// P:\tmp\tsverify 用同一份源码直接编译（假实现本接口）。
    ///
    /// 权威口径（不可回退）：
    ///   · 主机：只在执行**远端客户端请求**的同步窗口内采集 <c>SubsystemAudio</c> 排进本地
    ///     音频队列的声音；窗口外的本机动作一律不采集（不会产生回声）；
    ///   · 客户端：<see cref="Present"/> 只调 <c>SubsystemAudio.PlaySound</c>，不写格 /
    ///     不生成实体 / 不造成伤害；地形与库存仍走既有 CellChange / ContainerState。
    /// </summary>
    public interface IRemoteAudioPort
    {
        /// <summary>本端是否是联机客户端副本（引擎侧同一事实：SuSubsystemTerrain.ClientReplica）。</summary>
        bool IsClientReplica { get; }

        /// <summary>每帧调用：Project 换新 / 卸载时清掉可能残留的捕获窗口（明确计数）。</summary>
        void Pump();

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
