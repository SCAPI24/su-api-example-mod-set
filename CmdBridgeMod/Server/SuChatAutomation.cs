using System;
using System.Reflection;
using SuAPI;

namespace CmdBridgeMod
{
    /// <summary>
    /// SuMultiplayer 聊天自动化钩子的**反射发现与调用**（CmdBridge 不引用 SuMultiplayer）。
    ///
    /// 契约（SuMultiplayer 侧，见 <c>UI/ChatAutomation.cs</c>）：
    ///   · 已加载 Mod 的类型全名必须是 <c>SuMultiplayer.SuMultiplayerMod</c>；
    ///   · 它有一个 **public 实例属性** <c>ChatAutomation</c>，值是
    ///     <c>SuMultiplayer.UI.ChatAutomation</c>（组合根创建，无静态可变状态）；
    ///   · 该对象公开：
    ///       bool   IsInSession            { get; }
    ///       bool   IsChatInputOpen        { get; }
    ///       string CurrentChatInputText   { get; }
    ///       void   OpenChatInput()
    ///       void   InjectChatText(string text)
    ///       void   SubmitChatInput()
    ///       void   CancelChatInput()
    ///   · 失败时抛出的异常带 public string Code（chat_not_in_session / chat_not_open /
    ///     chat_unavailable / chat_inject_failed / chat_send_failed），这里原样透传成
    ///     <see cref="BridgeCommandException"/>，绝不静默降级。
    ///
    /// 没有 SuMultiplayer / 没有该属性 / 属性为 null → 一律 <c>chat_unavailable</c>，
    /// 并写清是"没装"还是"装的是没有钩子的旧包"。
    /// </summary>
    internal sealed class SuChatAutomation
    {
        internal const string SuMultiplayerModTypeName = "SuMultiplayer.SuMultiplayerMod";
        internal const string HookPropertyName = "ChatAutomation";

        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

        private readonly object m_hook;

        private SuChatAutomation(object hook)
        {
            m_hook = hook;
        }

        /// <summary>在已加载 Mod 列表里找 SuMultiplayer 并取出钩子；失败必须显式。</summary>
        internal static SuChatAutomation Resolve()
        {
            ModManager manager = ModManager.Instance;
            if (manager == null || manager.ModLoader == null)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "SuAPI ModManager is not initialized, so SuMultiplayer's chat hook cannot be located.");
            }

            object modInstance = null;
            int matches = 0;
            foreach (IMod loaded in manager.ModLoader.GetLoadedMods())
            {
                if (loaded == null)
                    continue;
                if (!string.Equals(loaded.GetType().FullName, SuMultiplayerModTypeName,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                modInstance = loaded;
                matches++;
            }

            if (matches == 0)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "SuMultiplayer is not loaded. Chat injection requires SuMultiplayer "
                    + "(put [SuAPI]SuMultiplayer-*.scmod in Mods/ next to CmdBridgeMod).");
            }
            if (matches > 1)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "Multiple SuMultiplayer mod instances are loaded (" + matches
                    + "); refusing to pick one for chat injection.");
            }

            PropertyInfo hookProperty = modInstance.GetType().GetProperty(HookPropertyName, PublicInstance);
            if (hookProperty == null)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "Loaded SuMultiplayer has no public " + HookPropertyName
                    + " property. The installed SuMultiplayer build predates the chat-injection hook.");
            }

            object hook;
            try
            {
                hook = hookProperty.GetValue(modInstance, null);
            }
            catch (TargetInvocationException exception)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "Reading SuMultiplayer." + HookPropertyName + " failed: "
                    + Describe(exception.InnerException ?? exception));
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "Reading SuMultiplayer." + HookPropertyName + " failed: " + Describe(exception));
            }

            if (hook == null)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "SuMultiplayer." + HookPropertyName
                    + " is null (mod not initialized yet or already unloaded).");
            }
            return new SuChatAutomation(hook);
        }

        internal bool IsInSession => ReadBoolean(nameof(IsInSession));

        internal bool IsChatInputOpen => ReadBoolean(nameof(IsChatInputOpen));

        internal string CurrentChatInputText => ReadString(nameof(CurrentChatInputText));

        internal void OpenChatInput() => Call("chat_unavailable", nameof(OpenChatInput));

        internal void InjectChatText(string text) =>
            Call("chat_inject_failed", nameof(InjectChatText), text);

        internal void SubmitChatInput() => Call("chat_inject_failed", nameof(SubmitChatInput));

        internal void CancelChatInput() => Call("chat_not_open", nameof(CancelChatInput));

        private object Call(string fallbackCode, string methodName, params object[] arguments)
        {
            MethodInfo method = Method(methodName);
            try
            {
                return method.Invoke(m_hook, arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw Translate(exception.InnerException ?? exception, fallbackCode);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException(fallbackCode,
                    "Calling SuMultiplayer.ChatAutomation." + methodName + " failed: "
                    + Describe(exception));
            }
        }

        private bool ReadBoolean(string propertyName)
        {
            object value = Read(propertyName);
            if (value is bool boolean)
                return boolean;
            throw new BridgeCommandException("chat_unavailable",
                "SuMultiplayer.ChatAutomation." + propertyName + " returned "
                + (value == null ? "null" : value.GetType().FullName) + ", expected bool.");
        }

        private string ReadString(string propertyName)
        {
            object value = Read(propertyName);
            if (value is string text)
                return text;
            if (value == null)
                return string.Empty;
            throw new BridgeCommandException("chat_unavailable",
                "SuMultiplayer.ChatAutomation." + propertyName + " returned "
                + value.GetType().FullName + ", expected string.");
        }

        private object Read(string propertyName)
        {
            PropertyInfo property = m_hook.GetType().GetProperty(propertyName, PublicInstance);
            if (property == null)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "SuMultiplayer.ChatAutomation." + propertyName
                    + " was not found (hook version mismatch).");
            }
            try
            {
                return property.GetValue(m_hook, null);
            }
            catch (TargetInvocationException exception)
            {
                throw Translate(exception.InnerException ?? exception, "chat_unavailable");
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "Reading SuMultiplayer.ChatAutomation." + propertyName + " failed: "
                    + Describe(exception));
            }
        }

        private MethodInfo Method(string methodName)
        {
            MethodInfo method = m_hook.GetType().GetMethod(methodName, PublicInstance);
            if (method == null)
            {
                throw new BridgeCommandException("chat_unavailable",
                    "SuMultiplayer.ChatAutomation." + methodName
                    + " was not found (hook version mismatch).");
            }
            return method;
        }

        /// <summary>
        /// 把 SuMultiplayer 抛出的 <c>ChatAutomationException.Code</c> 原样透传。
        /// 没有 Code 的异常是钩子内部故障 → 用调用点的兜底码，并附类型/消息。
        /// </summary>
        private static BridgeCommandException Translate(Exception exception, string fallbackCode)
        {
            PropertyInfo codeProperty = exception.GetType().GetProperty("Code", PublicInstance);
            string code = null;
            if (codeProperty != null && codeProperty.PropertyType == typeof(string))
            {
                try
                {
                    code = codeProperty.GetValue(exception, null) as string;
                }
                catch (Exception)
                {
                    code = null;
                }
            }
            if (!string.IsNullOrEmpty(code))
            {
                return new BridgeCommandException(code,
                    "SuMultiplayer chat hook: " + exception.Message);
            }
            return new BridgeCommandException(fallbackCode,
                "SuMultiplayer chat hook failed: " + Describe(exception));
        }

        private static string Describe(Exception exception)
        {
            if (exception == null)
                return "unknown error";
            return exception.GetType().Name + ": " + exception.Message;
        }
    }
}
