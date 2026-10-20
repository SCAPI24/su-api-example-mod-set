using System;
using Game;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎 <c>SubsystemAudio</c> 的注入外壳（数据库 Class 参数替换，与 SuSubsystemTerrain 同一机制）。
    ///
    /// 为什么必须替换子系统而不是派生后“隐藏”方法：
    ///   · <c>SubsystemAudio.PlaySound / PlayRandomSound</c> 全部**非虚**（Source:
    ///     Survivalcraft/Game/SubsystemAudio.cs:98-166），用 <c>new</c> 隐藏拦不到调用；
    ///   · 但它们最终都把条目排进 <c>m_queuedSounds</c>，而**排空**发生在
    ///     <c>SubsystemAudio.Update</c> 里。`SubsystemUpdate` 通过 <c>IUpdateable</c> 派发，
    ///     所以本类**重新实现** <c>IUpdateable</c>（显式接口方法），在 <c>base.Update</c>
    ///     排空队列**之前**把新增条目交给捕获端 —— 这是唯一能同时看到"立即播放（delay 0）"
    ///     与"延迟播放"声音、又不改变主机播放的时点。
    ///
    /// 捕获端（Engine/RemoteActionAudioReplica.cs）自己保证任何异常不冒泡：这里再包一层，
    /// 引擎的音频更新绝不会因为中继失败而中断。
    ///
    /// 无静态状态；不写格 / 不生成实体 / 不调 AudioManager。
    /// </summary>
    public sealed class SuSubsystemAudio : SubsystemAudio, IUpdateable
    {
        private IHostAudioCaptureSink m_sink;

        public void AttachCaptureSink(IHostAudioCaptureSink sink) => m_sink = sink;

        public void DetachCaptureSink(IHostAudioCaptureSink sink)
        {
            if (ReferenceEquals(m_sink, sink)) m_sink = null;
        }

        void IUpdateable.Update(float dt)
        {
            IHostAudioCaptureSink sink = m_sink;
            if (sink != null)
            {
                try
                {
                    sink.CaptureBeforeDrain(this);
                }
                catch (Exception e)
                {
                    try { sink.NoteCaptureFault(e); } catch (Exception) { }
                }
            }
            try
            {
                base.Update(dt);
            }
            finally
            {
                // 即使 base.Update 抛异常（引擎其它更新者出错被吞掉），也要把无头主机的
                // 虚拟监听者补回去；AfterAudioUpdate 自己保证不抛。
                if (sink != null)
                {
                    try
                    {
                        sink.AfterAudioUpdate(this);
                    }
                    catch (Exception e)
                    {
                        try { sink.NoteCaptureFault(e); } catch (Exception) { }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 音频队列捕获端（实现 = Engine/RemoteActionAudioReplica.cs）。
    /// 在 <see cref="SuSubsystemAudio"/> 的 <c>base.Update</c> 之前调用；必须不抛异常，
    /// 不得改变队列内容（主机仍按原样播放）。
    /// </summary>
    public interface IHostAudioCaptureSink
    {
        void CaptureBeforeDrain(SubsystemAudio audio);

        /// <summary>
        /// <c>base.Update</c> 之后调用（监听者表刚被引擎清空 / 重填）。无头主机没有任何
        /// GameWidget 监听者时，适配层可以在这里装入"客户端化身位置"作为虚拟监听者，
        /// 让引擎的 <c>CalculateListenerDistance / CalculateVolume</c> 得到真实距离，
        /// 从而让全局捕获能反解出音量与距离（否则队列里全是 0 音量，什么都听不到）。
        /// 主机有真实监听者 / 客户端 / 单机时必须是空操作。
        /// </summary>
        void AfterAudioUpdate(SubsystemAudio audio);

        void NoteCaptureFault(Exception exception);
    }
}
