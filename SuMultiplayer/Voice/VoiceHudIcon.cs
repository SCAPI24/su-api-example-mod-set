using System;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 语音状态图标（右下角、聊天按钮正上方）的**纯逻辑**：位置规则 / ON·OFF 视觉映射 /
    /// 状态变化计数。不依赖 Engine / Game，可被 P:\tmp\tsverify 直接编译自检。
    ///
    /// 位置来源（只读，不改聊天 HUD）：<c>UI/ChatOverlay.cs</c> 的常驻“聊天”按钮是
    /// <c>HorizontalAlignment=Far / VerticalAlignment=Far / Margin=(0, 236) / Size=(76, 56)</c>。
    /// 图标保持同样的右下角锚点与右边缘，Y 再往上一个按钮高度 + 8 px 间距，
    /// 因此**始终直接位于聊天按钮正上方**，不进入 MoreContents。
    /// </summary>
    public static class VoiceHudLayout
    {
        /// <summary>聊天按钮宽度（与 ChatOverlay.cs 的 m_chatButton 一致）。</summary>
        public const float ChatButtonWidth = 76f;

        /// <summary>聊天按钮高度（与 ChatOverlay.cs 的 m_chatButton 一致）。</summary>
        public const float ChatButtonHeight = 56f;

        /// <summary>聊天按钮距屏幕底部的 Y 边距（与 ChatOverlay.cs 的 m_chatButton 一致）。</summary>
        public const float ChatButtonMarginY = 236f;

        /// <summary>图标宽度：紧凑的图标按钮（比聊天按钮窄）。</summary>
        public const float IconWidth = 64f;

        /// <summary>图标高度：与聊天按钮同高，视觉上属于同一列。</summary>
        public const float IconHeight = 56f;

        /// <summary>图标与聊天按钮之间的间距（像素）。</summary>
        public const float IconGapY = 8f;

        /// <summary>图标右边缘与聊天按钮右边缘对齐。</summary>
        public const float IconMarginX = 0f;

        /// <summary>图标距屏幕底部的 Y 边距 = 聊天按钮上边缘 + 间距。</summary>
        public const float IconMarginY = ChatButtonMarginY + ChatButtonHeight + IconGapY;

        /// <summary>聊天按钮上边缘的 Y 边距。</summary>
        public static float ChatButtonTopY => ChatButtonMarginY + ChatButtonHeight;

        /// <summary>图标是否真的落在聊天按钮正上方（同一右侧锚点 + 一个按钮高度内的间距）。</summary>
        public static bool IsDirectlyAboveChat(float iconMarginY) =>
            iconMarginY >= ChatButtonTopY &&
            iconMarginY - ChatButtonTopY <= 24f;

        public static string Describe() =>
            "icon=" + IconWidth + "x" + IconHeight + " @far/far marginY=" + IconMarginY +
            "（聊天按钮 y=" + ChatButtonMarginY + " h=" + ChatButtonHeight +
            " gap=" + (IconMarginY - ChatButtonTopY) + "）";
    }

    /// <summary>图标某一时刻的视觉状态（纯数据；VoiceHud 再映射成 Engine.Color）。</summary>
    public readonly struct VoiceIconVisual : IEquatable<VoiceIconVisual>
    {
        public readonly bool On;
        public readonly bool ShowSlash;

        /// <summary>按钮底色 0xRRGGBB。</summary>
        public readonly uint CenterRgb;

        /// <summary>按钮斜面高光色 0xRRGGBB。</summary>
        public readonly uint BevelRgb;

        /// <summary>麦克风图标主色 0xRRGGBB。</summary>
        public readonly uint GlyphRgb;

        /// <summary>OFF 斜杠色 0xRRGGBB（ON 时不画）。</summary>
        public readonly uint SlashRgb;

        private VoiceIconVisual(bool on, bool showSlash, uint centerRgb, uint bevelRgb,
            uint glyphRgb, uint slashRgb)
        {
            On = on;
            ShowSlash = showSlash;
            CenterRgb = centerRgb;
            BevelRgb = bevelRgb;
            GlyphRgb = glyphRgb;
            SlashRgb = slashRgb;
        }

        /// <summary>ON = 彩色（绿底白麦）；OFF = 灰色（真灰，无色偏）+ 红色斜杠。</summary>
        public static VoiceIconVisual For(bool on) => on
            ? new VoiceIconVisual(true, false, 0x2E7D4F, 0x9AD6AE, 0xFFFFFF, 0x000000)
            : new VoiceIconVisual(false, true, 0x555555, 0x8A8A8A, 0xA8A8A8, 0xE06A5A);

        /// <summary>供状态行 / 日志使用的文字标签（图标本身不依赖字体）。</summary>
        public string Label => On ? "ON" : "OFF";

        public bool Equals(VoiceIconVisual other) =>
            On == other.On &&
            ShowSlash == other.ShowSlash &&
            CenterRgb == other.CenterRgb &&
            BevelRgb == other.BevelRgb &&
            GlyphRgb == other.GlyphRgb &&
            SlashRgb == other.SlashRgb;

        public override bool Equals(object obj) =>
            obj is VoiceIconVisual other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = On ? 1 : 0;
                hash = (hash * 397) ^ (ShowSlash ? 1 : 0);
                hash = (hash * 397) ^ (int)CenterRgb;
                hash = (hash * 397) ^ (int)BevelRgb;
                hash = (hash * 397) ^ (int)GlyphRgb;
                hash = (hash * 397) ^ (int)SlashRgb;
                return hash;
            }
        }
    }

    /// <summary>
    /// 每个图标实例的状态跟踪：只在实际视觉变化时计数（不是每帧 +1），
    /// 保证“图标更新”计数有意义；无静态状态。
    /// </summary>
    public sealed class VoiceIconStateTracker
    {
        private VoiceIconVisual m_current = VoiceIconVisual.For(false);
        private bool m_initialized;

        public VoiceIconVisual Current => m_current;

        public bool IsInitialized => m_initialized;

        /// <summary>视觉状态真正发生变化的次数（首次应用也算一次）。</summary>
        public long Updates { get; private set; }

        /// <summary>由图标点击触发的切换次数。</summary>
        public long Toggles { get; private set; }

        /// <summary>应用模块的 ON/OFF；返回 true = 视觉需要改写（状态发生了变化）。</summary>
        public bool Apply(bool on)
        {
            VoiceIconVisual next = VoiceIconVisual.For(on);
            if (m_initialized && next.Equals(m_current)) return false;
            m_current = next;
            m_initialized = true;
            Updates++;
            return true;
        }

        public void NoteToggle() => Toggles++;

        public void Reset()
        {
            m_current = VoiceIconVisual.For(false);
            m_initialized = false;
            Updates = 0;
            Toggles = 0;
        }
    }
}
