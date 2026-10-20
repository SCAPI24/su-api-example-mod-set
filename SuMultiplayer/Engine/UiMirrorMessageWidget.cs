using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using SuAPI;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 宿主 <c>MessageWidget</c> 里当前显示的一条冒泡消息（引擎私有嵌套类型
    /// <c>MessageWidget.Message</c> 的只读投影）。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/MessageWidget.cs）：
    ///   · 消息对象只有 <c>LabelWidget / StartTime / Duration / Color / Blinking</c> 五个字段
    ///     （:12-39），显示 / 过期完全由它们决定；
    ///   · <c>RemoveOldMessages</c> 在 <c>Time.FrameStartTime &gt;= StartTime + Duration</c>、超过 3 条、
    ///     或"非 Blinking 且不是最新一条"时移除（:100-111）；
    ///   · <c>DisplayMessage</c> 用**当前帧**作 StartTime（:70-77）——所以镜像必须能在加入后
    ///     把 StartTime 前移客户端的 age，否则同一条消息会在第三屏上多留 age 那么久。
    ///
    /// 本类只读 <c>m_messageWidget</c> / <c>m_messages</c> 与消息字段；唯一写入是
    ///   · 通过公开的 <c>DisplayMessage</c> 追加，
    ///   · 通过引擎自己的私有 <c>RemoveMessage</c> 移除我们追加过的那几条，
    ///   · 调整这些消息的 StartTime（让过期时刻与客户端一致）。
    /// 绝不碰宿主自己产生的消息（按对象引用跟踪），也不构造第二个 MessageWidget。
    /// </summary>
    public readonly struct UiMirrorHostMessage
    {
        public UiMirrorHostMessage(object message, string text, uint packedColor, bool blinking,
            double startTime, double duration)
        {
            Message = message;
            Text = text ?? string.Empty;
            PackedColor = packedColor;
            Blinking = blinking;
            StartTime = startTime;
            Duration = duration;
        }

        /// <summary>The engine <c>MessageWidget.Message</c> instance (opaque reference).</summary>
        public object Message { get; }

        public string Text { get; }
        public uint PackedColor { get; }
        public bool Blinking { get; }
        public double StartTime { get; }
        public double Duration { get; }

        /// <summary>Host clock at which the engine will drop this entry.</summary>
        public double ExpiresAtHostSeconds => StartTime + Duration;

        public bool SameKey(string text, uint packedColor, bool blinking) =>
            PackedColor == packedColor && Blinking == blinking &&
            string.Equals(Text, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>MessageWidget</c> 的反射访问器（无静态可变状态；字段缓存只在本实例内）。
    ///
    /// 为什么必须反射：<c>ComponentGui.m_messageWidget</c> 是私有字段，
    /// <c>MessageWidget.m_messages</c> 与嵌套消息类型也都是私有的，而引擎没有任何
    /// "读取当前可见消息 / 移除某一条 / 设定开始时间"的公开入口。
    /// 反射入口缺失时**显式失败**并给出字段名（不静默降级成"少显示一条"）。
    /// </summary>
    public sealed class UiMirrorMessageAccess
    {
        private readonly IModParentField m_parentField;
        private Type m_messageType;
        private FieldInfo m_startTimeField;
        private FieldInfo m_durationField;
        private FieldInfo m_colorField;
        private FieldInfo m_blinkingField;
        private FieldInfo m_labelWidgetField;
        private MethodInfo m_removeMessageMethod;
        private string m_fieldError;

        public UiMirrorMessageAccess(IModParentField parentField)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
        }

        /// <summary>视图 HUD 的 <c>ComponentGui.m_messageWidget</c>。失败 = 引擎版本不匹配。</summary>
        public bool TryGetWidget(ComponentGui gui, out MessageWidget widget, out string reason)
        {
            widget = null;
            reason = null;
            if (gui == null)
            {
                reason = "gui-null";
                return false;
            }
            try
            {
                widget = m_parentField.GetParentField<MessageWidget>(gui, "m_messageWidget",
                    typeof(ComponentGui));
            }
            catch (Exception e)
            {
                reason = "m_messageWidget:" + Explain(e);
                return false;
            }
            if (widget == null)
            {
                reason = "m_messageWidget-null";
                return false;
            }
            return true;
        }

        /// <summary>按显示顺序（旧 → 新）读宿主当前的消息列表。</summary>
        public bool TryRead(MessageWidget widget, List<UiMirrorHostMessage> output, out string reason)
        {
            reason = null;
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();
            if (widget == null)
            {
                reason = "widget-null";
                return false;
            }
            object list;
            try
            {
                list = m_parentField.GetParentField(widget, "m_messages", typeof(MessageWidget));
            }
            catch (Exception e)
            {
                reason = "m_messages:" + Explain(e);
                return false;
            }
            if (list == null)
            {
                reason = "m_messages-null";
                return false;
            }
            if (!(list is IEnumerable enumerable))
            {
                reason = "m_messages-not-enumerable:" + list.GetType().Name;
                return false;
            }
            try
            {
                foreach (object message in enumerable)
                {
                    if (message == null)
                    {
                        reason = "message-null";
                        return false;
                    }
                    if (!EnsureMessageFields(message, out reason)) return false;
                    var label = m_labelWidgetField.GetValue(message) as LabelWidget;
                    if (label == null)
                    {
                        reason = "label-widget-null";
                        return false;
                    }
                    output.Add(new UiMirrorHostMessage(message, label.Text,
                        ((Color)m_colorField.GetValue(message)).PackedValue,
                        (bool)m_blinkingField.GetValue(message),
                        (double)m_startTimeField.GetValue(message),
                        (double)m_durationField.GetValue(message)));
                }
            }
            catch (Exception e)
            {
                reason = "read:" + Explain(e);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 客户端当前可见的小消息（按显示顺序），<c>AgeMs</c> = 该条已经在客户端显示了多久。
        /// 超过引擎的 <see cref="MessageWidget"/> 上限（3）说明客户端引擎不变量被破坏 → 明确失败。
        /// </summary>
        public bool TryCapture(ComponentGui gui, double nowSeconds,
            out UiSmallMessageData[] messages, out string reason)
        {
            messages = Array.Empty<UiSmallMessageData>();
            reason = null;
            if (!TryGetWidget(gui, out MessageWidget widget, out reason)) return false;
            var host = new List<UiMirrorHostMessage>(4);
            if (!TryRead(widget, host, out reason)) return false;
            var captured = new List<UiSmallMessageData>(host.Count);
            for (int i = 0; i < host.Count; i++)
            {
                UiMirrorHostMessage entry = host[i];
                double age = nowSeconds - entry.StartTime;
                if (age < 0.0) age = 0.0;
                // 已经到期的条目引擎会在同一帧的 MessageWidget.Update 里移除；这里不再上报。
                if (age >= entry.Duration) continue;
                if (captured.Count >= UiStatePolicy.MaxSmallMessages)
                {
                    reason = "more-than-" + UiStatePolicy.MaxSmallMessages + "-messages(" +
                        host.Count + ")";
                    return false;
                }
                double ageMs = Math.Min(age * 1000.0, uint.MaxValue);
                captured.Add(new UiSmallMessageData(entry.Text, entry.PackedColor,
                    entry.Blinking, (uint)Math.Round(ageMs)));
            }
            messages = captured.ToArray();
            return true;
        }

        /// <summary>
        /// 追加一条宿主消息并把 StartTime 前移到 <paramref name="expiresAtHostSeconds"/> 对应的
        /// 时刻（引擎按 <c>StartTime + Duration</c> 过期，Duration 由文本 / blinking 决定）。
        /// </summary>
        public bool TryAdd(MessageWidget widget, string text, uint packedColor, bool blinking,
            double expiresAtHostSeconds, out object message, out string reason)
        {
            message = null;
            reason = null;
            if (widget == null)
            {
                reason = "widget-null";
                return false;
            }
            if (string.IsNullOrEmpty(text))
            {
                reason = "text-empty";
                return false;
            }
            try
            {
                widget.DisplayMessage(text, new Color(packedColor), blinking);
                var list = new List<UiMirrorHostMessage>(4);
                if (!TryRead(widget, list, out reason)) return false;
                if (list.Count == 0)
                {
                    reason = "add-not-visible";
                    return false;
                }
                UiMirrorHostMessage last = list[list.Count - 1];
                if (!last.SameKey(text, packedColor, blinking))
                {
                    reason = "add-last-mismatch";
                    return false;
                }
                m_startTimeField.SetValue(last.Message, expiresAtHostSeconds - last.Duration);
                message = last.Message;
                return true;
            }
            catch (Exception e)
            {
                reason = "add:" + Explain(e);
                return false;
            }
        }

        /// <summary>只移除先前由 <see cref="TryAdd"/> 返回的对象（调用方保证）。</summary>
        public bool TryRemove(MessageWidget widget, object message, out string reason)
        {
            reason = null;
            if (widget == null)
            {
                reason = "widget-null";
                return false;
            }
            if (message == null)
            {
                reason = "message-null";
                return false;
            }
            if (m_removeMessageMethod == null)
            {
                m_removeMessageMethod = typeof(MessageWidget).GetMethod("RemoveMessage",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (m_removeMessageMethod == null)
                {
                    reason = "MessageWidget.RemoveMessage-missing";
                    return false;
                }
            }
            try
            {
                m_removeMessageMethod.Invoke(widget, new[] { message });
                return true;
            }
            catch (Exception e)
            {
                reason = "remove:" + Explain(e);
                return false;
            }
        }

        private bool EnsureMessageFields(object message, out string reason)
        {
            reason = null;
            Type type = message.GetType();
            if (ReferenceEquals(type, m_messageType) && m_fieldError == null) return true;
            if (!ReferenceEquals(type, m_messageType))
            {
                m_messageType = type;
                m_fieldError = null;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
                m_labelWidgetField = type.GetField("LabelWidget", flags);
                m_startTimeField = type.GetField("StartTime", flags);
                m_durationField = type.GetField("Duration", flags);
                m_colorField = type.GetField("Color", flags);
                m_blinkingField = type.GetField("Blinking", flags);
                var missing = new List<string>(5);
                if (m_labelWidgetField == null) missing.Add("LabelWidget");
                if (m_startTimeField == null) missing.Add("StartTime");
                if (m_durationField == null) missing.Add("Duration");
                if (m_colorField == null) missing.Add("Color");
                if (m_blinkingField == null) missing.Add("Blinking");
                if (missing.Count > 0)
                    m_fieldError = "message-fields-missing(" + string.Join(",", missing) + ")";
            }
            if (m_fieldError != null)
            {
                reason = m_fieldError;
                return false;
            }
            return true;
        }

        private static string Explain(Exception e) =>
            e == null ? "null" : e.GetType().Name + " " + e.Message;
    }
}
