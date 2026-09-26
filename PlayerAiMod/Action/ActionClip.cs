using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 一帧动作输入。**刻意与 <see cref="RecordingFrame"/> 同形**（按住键 / 刚按下键 / 鼠标位图 /
    /// 滚轮 / 视角增量 / 目标槽位），于是：
    ///   · 运行时"生成"的动作与"录制"的动作能喂给同一个执行器（§3.3 三种来源共用一条路径）；
    ///   · 想把生成的动作存成 `.scatpak` 时，字段一一对应，不需要第二套格式（bake）。
    ///
    /// 键名用字符串（与 <c>CmdBridgeInput</c> 的键名一致），不用 <see cref="Engine.Input.Key"/>
    /// 枚举 —— 动作层要能在没有游戏的临时工程里自检（§3.3 的"命令语义不依赖游戏"同一条纪律）。
    /// </summary>
    public struct ActionFrame
    {
        /// <summary>
        /// 构造函数：把 <see cref="SelectSlot"/> 初始化成 **-1（不管槽位）**。
        ///
        /// 为什么必须显式写（实测踩过）：`struct` 的字段默认值是 **0**，而 0 会被执行器当成
        /// "槽位 0"（`SelectSlot &gt;= 0` 才生效）—— 于是每个用对象初始化器直接构造的帧
        /// （jump / aim / dig / ui …）都会**顺手按下数字键 0**。
        /// 游戏里的表现：轨迹出现 `refused:slot:0`；真机上就是莫名切换快捷栏。
        /// </summary>
        public ActionFrame()
        {
            DeltaMs = 0;
            KeysHeld = null;
            KeysPressed = null;
            MouseButtons = 0;
            Wheel = 0;
            LookDeltaYaw = 0f;
            LookDeltaPitch = 0f;
            HasAbsoluteLook = false;
            YawDegrees = 0f;
            PitchDegrees = 0f;
            SelectSlot = -1;
            UiClick = null;
            TypeText = null;
            MouseAction = null;
            MouseButtonName = null;
            HasLookAt = false;
            LookAtX = 0f;
            LookAtY = 0f;
            LookAtZ = 0f;
        }

        /// <summary>与上一帧的间隔（毫秒）。动作包回放按它保持这一帧的输入。</summary>
        public int DeltaMs;

        /// <summary>这一帧按住的键（跨帧有效）。</summary>
        public string[] KeysHeld;

        /// <summary>这一帧刚按下的键（引擎的 downOnce 语义：开关类动作靠它）。</summary>
        public string[] KeysPressed;

        /// <summary>鼠标键位图：bit0 左 / bit1 右 / bit2 中。</summary>
        public byte MouseButtons;

        /// <summary>滚轮格数（本帧增量）。</summary>
        public int Wheel;

        /// <summary>视角增量（弧度；与引擎把 PlayerInput.Look 加进 lookAngles 的量同单位）。</summary>
        public float LookDeltaYaw;
        public float LookDeltaPitch;

        /// <summary>绝对朝向（度）—— 只有 look/turn 这类 verb 会设；设了就以它为准（瞬时转向）。</summary>
        public bool HasAbsoluteLook;
        public float YawDegrees;
        public float PitchDegrees;

        /// <summary>本帧要选的快捷栏槽位（1..10）；-1 = 不管。</summary>
        public int SelectSlot;

        /// <summary>本帧要点的 UI 语义目标（可空）。</summary>
        public string UiClick;

        /// <summary>本帧要打的文本（可空）。</summary>
        public string TypeText;

        /// <summary>本帧要按的鼠标动作：null / "down" / "up" / "click"（配合 MouseButtonName）。</summary>
        public string MouseAction;
        public string MouseButtonName;

        /// <summary>本帧要看的绝对世界点（可空；与 LookAt 相关）。</summary>
        public bool HasLookAt;
        public float LookAtX, LookAtY, LookAtZ;

        public static ActionFrame Hold(int deltaMs, params string[] keys)
        {
            return new ActionFrame { DeltaMs = deltaMs, KeysHeld = keys, SelectSlot = -1 };
        }

        public static ActionFrame Idle(int deltaMs)
        {
            return new ActionFrame { DeltaMs = deltaMs, SelectSlot = -1 };
        }

        public bool HasAnyInput
        {
            get
            {
                return (KeysHeld != null && KeysHeld.Length > 0)
                    || (KeysPressed != null && KeysPressed.Length > 0)
                    || MouseButtons != 0
                    || Wheel != 0
                    || LookDeltaYaw != 0f || LookDeltaPitch != 0f
                    || HasAbsoluteLook
                    || SelectSlot >= 0
                    || !string.IsNullOrEmpty(UiClick)
                    || !string.IsNullOrEmpty(TypeText)
                    || !string.IsNullOrEmpty(MouseAction)
                    || HasLookAt;
            }
        }
    }

    /// <summary>
    /// 一个 verb 编译出来的逐帧输入片段。**不落盘也能跑**；想归档时用它生成 `.scatpak`
    /// （字段一一对应，见 <see cref="ActionFrame"/> 的说明）。
    /// </summary>
    public sealed class ActionClip
    {
        private readonly List<ActionFrame> m_frames = new List<ActionFrame>();

        public ActionClip(string name)
        {
            Name = name ?? "clip";
        }

        public string Name { get; }

        public IReadOnlyList<ActionFrame> Frames
        {
            get { return m_frames; }
        }

        public int FrameCount
        {
            get { return m_frames.Count; }
        }

        public double DurationSeconds
        {
            get
            {
                double total = 0.0;
                for (int i = 0; i < m_frames.Count; i++)
                    total += m_frames[i].DeltaMs / 1000.0;
                return total;
            }
        }

        public ActionClip Add(ActionFrame frame)
        {
            m_frames.Add(frame);
            return this;
        }

        /// <summary>连续按住若干帧（每个 verb 的"持续类"动作都走它）。</summary>
        public ActionClip Hold(int totalMs, int frameMs, params string[] keys)
        {
            if (frameMs <= 0)
                frameMs = ActionCompileDefaults.FrameMs;
            int remaining = Math.Max(frameMs, totalMs);
            while (remaining > 0)
            {
                int slice = Math.Min(frameMs, remaining);
                m_frames.Add(ActionFrame.Hold(slice, keys));
                remaining -= slice;
            }
            return this;
        }

        /// <summary>什么都不做，等若干毫秒。</summary>
        public ActionClip Wait(int totalMs, int frameMs = 0)
        {
            if (frameMs <= 0)
                frameMs = ActionCompileDefaults.FrameMs;
            int remaining = Math.Max(frameMs, totalMs);
            while (remaining > 0)
            {
                int slice = Math.Min(frameMs, remaining);
                m_frames.Add(ActionFrame.Idle(slice));
                remaining -= slice;
            }
            return this;
        }

        public string Describe()
        {
            return Name + " frames=" + m_frames.Count + " (" + DurationSeconds.ToString("0.00") + "s)";
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>动作编译的默认参数（集中一处，便于调参与自检）。</summary>
    public static class ActionCompileDefaults
    {
        /// <summary>一帧的时间片（毫秒）。20 Hz：比录制（60 Hz）粗，但足够表达"按住 1.5 秒"。</summary>
        public const int FrameMs = 50;

        /// <summary>按下类按键的默认保持时长（毫秒）。</summary>
        public const int PulseMs = 100;

        /// <summary>转向的默认完成时间（毫秒）。</summary>
        public const int LookMs = 100;

        /// <summary>单步默认超时（毫秒）。0 = 用编译出的时长 × 系数。</summary>
        public const int DefaultStepTimeoutMs = 0;

        /// <summary>单步超时系数：编译时长 × 它 = 超时上限（看门狗）。</summary>
        public const double StepTimeoutFactor = 2.5;

        /// <summary>超时下限（毫秒）—— 很短的步骤也要给足余量。</summary>
        public const int MinimumStepTimeoutMs = 750;

        /// <summary>单个脚本的最大步数（防脚本写成死循环）。</summary>
        public const int MaxSteps = 64;

        /// <summary>单个脚本的最大总时长（毫秒）。</summary>
        public const int MaxTotalMs = 120000;
    }
}
