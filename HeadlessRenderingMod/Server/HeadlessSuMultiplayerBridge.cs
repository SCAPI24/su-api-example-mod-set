using SuAPI;
using System;
using System.Reflection;

namespace HeadlessRenderingMod
{
    /// <summary>
    /// 无头服务器控制台 ↔ SuMultiplayer 的桥（**反射调用**，本 mod 不引用 SuMultiplayer）。
    ///
    /// 背景：无头服务器（远端主机）没有人手去点游戏内界面，但运维需要在控制台里
    /// 直接切换联机模块的日志模式 —— 这正是 <c>SuMultiplayerMod.SetModuleLogModeRuntime</c>
    /// 存在的意义（主机不关房、不改配置即可切换，并自动下发给所有玩家端）。
    ///
    /// 为什么走反射而不是引用程序集：两个 mod 各自独立部署、各自独立升级，
    /// 谁也不能假设对方一定在场或有某个版本（与 `CmdBridgeMod` 的做法一致）。
    ///
    /// 定位纪律：找不到就**明确报错**，绝不静默降级成"什么都没发生"。
    /// </summary>
    internal static class HeadlessSuMultiplayerBridge
    {
        private const string ModTypeName = "SuMultiplayer.SuMultiplayerMod";
        private const string HookMethodName = "SetModuleLogModeRuntime";

        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

        /// <summary>
        /// 运行时切换模块日志模式。
        /// </summary>
        /// <param name="persistToFile">true = 调试模式（写盘）/ false = 上线模式（只进内存）。</param>
        /// <param name="note">SuMultiplayer 返回的说明（含是否生效的原因）。</param>
        /// <returns>是否切换成功。false 时 <paramref name="note"/> 说明原因（例如本端是客户端）。</returns>
        /// <exception cref="InvalidOperationException">
        /// 找不到 SuMultiplayer / 版本过旧没有该接口 —— 这时是**部署问题**，必须让人看见。
        /// </exception>
        internal static bool TrySetModuleLogMode(bool persistToFile, out string note)
        {
            object instance = ResolveModInstance();
            MethodInfo method = instance.GetType().GetMethod(HookMethodName, PublicInstance);
            if (method == null)
            {
                throw new InvalidOperationException(
                    "SuMultiplayer is installed but has no " + HookMethodName
                    + " hook — the deployed package is older than the log-mode feature.");
            }

            // 签名是 (bool persistToFile, out string note)，反射用 object[] 传 out 参数。
            object[] arguments = new object[] { persistToFile, null };
            object result;
            try
            {
                result = method.Invoke(instance, arguments);
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                throw new InvalidOperationException(
                    "SuMultiplayer rejected the log-mode switch: "
                    + inner.GetType().Name + ": " + inner.Message, inner);
            }

            note = arguments[1] as string ?? string.Empty;
            return result is bool applied && applied;
        }

        /// <summary>
        /// 当前模块日志模式（true = 调试 / 落盘）。取不到就报错 —— 绝不猜一个值糊弄控制台。
        /// </summary>
        internal static bool ReadModuleLogMode()
        {
            object instance = ResolveModInstance();
            // 权威来源是日志出口自己的状态（SuMultiplayer.Engine.ModLog.Verbose）：
            // 它就是"当前是否落盘"的唯一判定，切换后立即反映，不必让联机模块多开一个属性。
            Type modLog = instance.GetType().Assembly.GetType(
                "SuMultiplayer.Engine.ModLog", throwOnError: false);
            PropertyInfo verbose = modLog?.GetProperty("Verbose",
                BindingFlags.Static | BindingFlags.Public);
            if (verbose != null && verbose.PropertyType == typeof(bool))
            {
                return verbose.GetValue(null) is bool value && value;
            }

            throw new InvalidOperationException(
                "SuMultiplayer is installed but has no ModLog.Verbose property — "
                + "cannot report the current log mode.");
        }

        /// <summary>在已加载 Mod 列表里找 SuMultiplayer 实例。</summary>
        private static object ResolveModInstance()
        {
            ModManager manager = ModManager.Instance;
            if (manager == null || manager.ModLoader == null)
            {
                throw new InvalidOperationException(
                    "SuAPI ModManager is not initialized, so SuMultiplayer cannot be located.");
            }

            object instance = null;
            int matches = 0;
            foreach (IMod loaded in manager.ModLoader.GetLoadedMods())
            {
                if (loaded == null) continue;
                if (!string.Equals(loaded.GetType().FullName, ModTypeName, StringComparison.Ordinal))
                    continue;
                instance = loaded;
                matches++;
            }

            if (matches == 0)
            {
                throw new InvalidOperationException(
                    "SuMultiplayer is not loaded. Put [SuAPI]SuMultiplayer-*.scmod in the "
                    + "server's Mods/ directory to use multiplayer log-mode commands.");
            }
            if (matches > 1)
            {
                throw new InvalidOperationException(
                    "Multiple SuMultiplayer mod instances are loaded (" + matches
                    + "); refusing to pick one.");
            }
            return instance;
        }
    }
}
