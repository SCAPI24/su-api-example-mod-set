using System;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 语音开关快捷键（Windows U）的**边沿判定**（纯逻辑，可被 P:\tmp\tsverify 直接自检）：
    ///   · 一次"按下"只产生一次 toggle —— 按住不会连发；
    ///   · 松开不产生 toggle；
    ///   · suppressed（文本输入 / 对话框 / 软键盘打开）= 丢弃这一次按下边沿，
    ///     且不会在松开后补触发 —— 关掉输入后必须重新按一次。
    /// 无静态状态；HUD 每个实例持有一个。
    /// </summary>
    public sealed class VoiceToggleGate
    {
        private bool m_down;

        /// <summary>最近一帧是否按着（诊断 / 自检用）。</summary>
        public bool IsDown => m_down;

        /// <summary>
        /// 输入一帧的按键状态，返回 true = 恰好发生一次有效的"按下边沿"。
        /// </summary>
        public bool Update(bool down, bool suppressed)
        {
            bool rising = down && !m_down;
            m_down = down;
            return rising && !suppressed;
        }

        public void Reset() => m_down = false;
    }
}
