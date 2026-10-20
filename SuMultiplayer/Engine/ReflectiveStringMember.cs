using System;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 反射读取“字符串成员”的安全绑定（无静态状态，自检可直接实例化）：
    ///   · 成员实际类型必须是 System.String；不是 → 首次即禁用（Mismatches=1），
    ///     之后只累计 SkippedReads，绝不重复计数、绝不抛异常；
    ///   · 值为 null 是合法的“未赋值”，**不是类型不匹配**。这正是 2026-10-19 的回归根因：
    ///     <c>ModParentField.GetParentField&lt;string&gt;</c> 用 `value is T` 判定，
    ///     null 对引用类型 T 也返回 false，于是字段还没被赋值时就抛 InvalidCastException；
    ///   · 运行期读到非字符串 / 读取异常 → 同样只禁用一次，之后安静跳过（由调用方限流打印原因）；
    ///   · 本类只记录状态与计数，不写日志，避免在每帧路径里刷屏。
    /// </summary>
    public sealed class ReflectiveStringMember
    {
        public enum BindingState
        {
            Unknown,
            Ready,
            Unsupported
        }

        public ReflectiveStringMember(string memberName)
        {
            if (string.IsNullOrWhiteSpace(memberName))
                throw new ArgumentException("Member name cannot be empty.", nameof(memberName));
            MemberName = memberName;
        }

        public string MemberName { get; }

        public BindingState State { get; private set; }

        /// <summary>绑定成功时的成员实际类型（未绑定 / 找不到时为 n/a）。</summary>
        public string BoundTypeName { get; private set; } = "n/a";

        /// <summary>绑定来源（字段 / 属性 + 声明类型；只用于日志）。</summary>
        public string BoundFrom { get; private set; } = string.Empty;

        /// <summary>禁用原因（首次不匹配 / 读取异常；之后保持不变）。</summary>
        public string UnsupportedReason { get; private set; } = string.Empty;

        /// <summary>类型不匹配次数：**最多 1**（首次即禁用，后续不再重复计）。</summary>
        public long Mismatches { get; private set; }

        /// <summary>禁用后仍被调用而跳过的读取次数（每只生物每帧都会累加；用来证明路径已停用）。</summary>
        public long SkippedReads { get; private set; }

        /// <summary>成功执行的读取次数（含 null = 未赋值）。</summary>
        public long Reads { get; private set; }

        /// <summary>绑定成员的实际类型。memberType 为 null = 成员不存在。</summary>
        public bool TryBind(Type memberType, string boundFrom)
        {
            if (State == BindingState.Ready) return true;
            if (State == BindingState.Unsupported) return false;
            if (memberType == null)
            {
                Disable("成员不存在");
                return false;
            }
            BoundTypeName = memberType.FullName ?? memberType.Name;
            if (memberType != typeof(string))
            {
                Disable("成员实际类型 " + BoundTypeName + " 不是 System.String");
                return false;
            }
            BoundFrom = boundFrom ?? string.Empty;
            State = BindingState.Ready;
            return true;
        }

        /// <summary>
        /// 解释一次反射读取的原始值：
        ///   · null → 成功，value=null（未赋值，不是错误）；
        ///   · string → 成功；
        ///   · 其它 → 禁用并返回 false（disabledNow = 本次调用导致禁用）。
        /// </summary>
        public bool TryInterpret(object rawValue, out string value, out string failure,
            out bool disabledNow)
        {
            value = null;
            failure = string.Empty;
            disabledNow = false;
            if (State == BindingState.Unsupported)
            {
                SkippedReads++;
                failure = UnsupportedReason;
                return false;
            }
            if (State == BindingState.Unknown)
            {
                SkippedReads++;
                failure = "尚未绑定成员类型";
                return false;
            }
            Reads++;
            if (rawValue == null) return true;
            if (rawValue is string text)
            {
                value = text;
                return true;
            }
            Type actual = rawValue.GetType();
            failure = "运行期值类型 " + (actual.FullName ?? actual.Name) + " 不是 System.String";
            disabledNow = Disable(failure);
            return false;
        }

        /// <summary>禁用后仍被调用时记一次跳过（调用方连反射都不该再做）。</summary>
        public void NoteSkippedRead() => SkippedReads++;

        /// <summary>读取过程抛异常：首次禁用并返回 true，之后只累计 SkippedReads。</summary>
        public bool TryReject(string reason)
        {
            if (State == BindingState.Unsupported)
            {
                SkippedReads++;
                return false;
            }
            return Disable(reason);
        }

        private bool Disable(string reason)
        {
            if (State == BindingState.Unsupported) return false;
            State = BindingState.Unsupported;
            UnsupportedReason = reason ?? string.Empty;
            Mismatches++;
            return true;
        }
    }
}
