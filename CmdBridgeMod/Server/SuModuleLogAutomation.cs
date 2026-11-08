using SuAPI;
using System;
using System.Reflection;

namespace CmdBridgeMod
{
    /// <summary>
    /// 运行时切换 SuMultiplayer 的**模块日志模式**（反射调用，本 Mod 不引用 SuMultiplayer）。
    ///
    /// 背景（用户 2026-11-08 定）：SuMultiplayer 的模块日志有两种模式 ——
    ///   · 上线（默认）：只进内存缓冲，缓冲满即丢弃，**不写盘**；
    ///   · Debug：缓冲满把这一轮写进角色日志。
    /// 模式**由主机调度**：主机切换后广播给所有玩家端（它们不读本地配置）。
    /// 这个钩子让主机在不关房、不改 `SuMultiplayer.json` 的情况下随时切换。
    ///
    /// 对应 SuMultiplayer 侧公开接口：
    ///   <c>SuMultiplayer.SuMultiplayerMod.SetModuleLogModeRuntime(bool persistToFile, out string note)</c>
    ///
    /// 与 <see cref="SuChatAutomation"/> 同一套定位纪律：找不到就明确报错，绝不静默降级。
    /// </summary>
    internal sealed class SuModuleLogAutomation
    {
        internal const string SuMultiplayerModTypeName = "SuMultiplayer.SuMultiplayerMod";
        internal const string HookMethodName = "SetModuleLogModeRuntime";

        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

        private readonly object m_modInstance;
        private readonly MethodInfo m_hook;

        private SuModuleLogAutomation(object modInstance, MethodInfo hook)
        {
            m_modInstance = modInstance;
            m_hook = hook;
        }

        /// <summary>在已加载 Mod 列表里找 SuMultiplayer 并取出钩子；失败必须显式。</summary>
        internal static SuModuleLogAutomation Resolve()
        {
            const string code = "logmode_unavailable";
            ModManager manager = ModManager.Instance;
            if (manager == null || manager.ModLoader == null)
            {
                throw new BridgeCommandException(code,
                    "SuAPI ModManager is not initialized, so SuMultiplayer's log-mode hook "
                    + "cannot be located.");
            }

            object modInstance = null;
            int matches = 0;
            foreach (IMod loaded in manager.ModLoader.GetLoadedMods())
            {
                if (loaded == null) continue;
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
                throw new BridgeCommandException(code,
                    "SuMultiplayer is not loaded. Log-mode switching requires SuMultiplayer "
                    + "(put [SuAPI]SuMultiplayer-*.scmod in Mods/ next to CmdBridgeMod).");
            }
            if (matches > 1)
            {
                throw new BridgeCommandException(code,
                    "Multiple SuMultiplayer mod instances are loaded (" + matches
                    + "); refusing to pick one for log-mode switching.");
            }

            MethodInfo method = modInstance.GetType().GetMethod(HookMethodName, PublicInstance);
            if (method == null)
            {
                throw new BridgeCommandException(code,
                    "SuMultiplayer is loaded but has no " + HookMethodName
                    + " hook — the installed package is older than the log-mode feature.");
            }
            return new SuModuleLogAutomation(modInstance, method);
        }

        /// <summary>
        /// 切换日志模式。
        /// <paramref name="persistToFile"/>：true = Debug（落盘）/ false = 上线（不落盘）。
        /// </summary>
        /// <returns>SuMultiplayer 返回的说明文本（已含是否生效的原因）。</returns>
        internal string SetMode(bool persistToFile)
        {
            // 签名是 (bool persistToFile, out string note)，反射用 object[] 传 out 参数。
            object[] arguments = new object[] { persistToFile, null };
            object result;
            try
            {
                result = m_hook.Invoke(m_modInstance, arguments);
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                throw new BridgeCommandException("logmode_failed",
                    "SuMultiplayer rejected the log-mode switch: "
                    + inner.GetType().Name + ": " + inner.Message);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("logmode_failed",
                    "Invoking SuMultiplayer." + HookMethodName + " failed: "
                    + exception.GetType().Name + ": " + exception.Message);
            }

            bool applied = result is bool value && value;
            string note = arguments[1] as string ?? string.Empty;
            return (applied ? "mode switched: " : "mode NOT switched: ")
                + (persistToFile ? "debug (persist to disk)" : "online (memory only)")
                + (string.IsNullOrEmpty(note) ? string.Empty : " · " + note);
        }
    }
}
