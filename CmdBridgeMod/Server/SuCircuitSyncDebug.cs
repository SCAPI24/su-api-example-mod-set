using System;
using System.Collections.Generic;
using System.Reflection;
using SuAPI;

namespace CmdBridgeMod
{
    /// <summary>通过稳定公开钩子调用 SuMultiplayer 的电路同步调试暂停信号。</summary>
    internal sealed class SuCircuitSyncDebug
    {
        private const string ModTypeName = "SuMultiplayer.SuMultiplayerMod";
        private const string HookPropertyName = "CircuitSyncDebugAutomation";
        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;
        private readonly object m_hook;

        private SuCircuitSyncDebug(object hook) { m_hook = hook; }

        internal static SuCircuitSyncDebug Resolve()
        {
            ModManager manager = ModManager.Instance;
            if (manager == null || manager.ModLoader == null)
                throw new BridgeCommandException("circuit_debug_unavailable",
                    "SuAPI ModManager is not initialized.");
            object found = null;
            int matches = 0;
            foreach (IMod loaded in manager.ModLoader.GetLoadedMods())
            {
                if (loaded != null && string.Equals(loaded.GetType().FullName, ModTypeName,
                        StringComparison.Ordinal))
                {
                    found = loaded;
                    matches++;
                }
            }
            if (matches == 0)
                throw new BridgeCommandException("circuit_debug_unavailable",
                    "SuMultiplayer is not loaded.");
            if (matches > 1)
                throw new BridgeCommandException("circuit_debug_unavailable",
                    "Multiple SuMultiplayer mod instances are loaded.");
            PropertyInfo property = found.GetType().GetProperty(HookPropertyName, PublicInstance);
            if (property == null)
                throw new BridgeCommandException("circuit_debug_unavailable",
                    "Loaded SuMultiplayer has no circuit debug hook.");
            object hook;
            try { hook = property.GetValue(found, null); }
            catch (Exception e)
            {
                throw new BridgeCommandException("circuit_debug_unavailable",
                    "Reading SuMultiplayer circuit debug hook failed: " + Describe(e));
            }
            if (hook == null)
                throw new BridgeCommandException("circuit_debug_unavailable",
                    "SuMultiplayer circuit debug hook is unavailable.");
            return new SuCircuitSyncDebug(hook);
        }

        internal Dictionary<string, object> Status()
        {
            object result = Call(nameof(Status));
            if (result is Dictionary<string, object> dictionary) return dictionary;
            throw new BridgeCommandException("circuit_debug_unavailable",
                "SuMultiplayer circuit debug status returned an invalid value.");
        }

        internal void SetPaused(bool paused)
        {
            MethodInfo method = Method(nameof(SetPaused));
            try { method.Invoke(m_hook, new object[] { paused }); }
            catch (TargetInvocationException e) { throw Translate(e.InnerException ?? e); }
            catch (Exception e)
            {
                throw new BridgeCommandException("circuit_debug_failed",
                    "Calling SuMultiplayer circuit debug pause failed: " + Describe(e));
            }
        }

        private object Call(string methodName)
        {
            MethodInfo method = Method(methodName);
            try { return method.Invoke(m_hook, null); }
            catch (TargetInvocationException e) { throw Translate(e.InnerException ?? e); }
            catch (Exception e)
            {
                throw new BridgeCommandException("circuit_debug_failed",
                    "Calling SuMultiplayer circuit debug status failed: " + Describe(e));
            }
        }

        private MethodInfo Method(string name)
        {
            MethodInfo method = m_hook.GetType().GetMethod(name, PublicInstance);
            if (method == null)
                throw new BridgeCommandException("circuit_debug_unavailable",
                    "SuMultiplayer circuit debug method " + name + " was not found.");
            return method;
        }

        private static BridgeCommandException Translate(Exception exception)
        {
            PropertyInfo code = exception.GetType().GetProperty("Code", PublicInstance);
            string value = code?.GetValue(exception, null) as string;
            return new BridgeCommandException(string.IsNullOrEmpty(value)
                ? "circuit_debug_failed" : value, exception.Message);
        }

        private static string Describe(Exception exception) => exception == null
            ? "unknown error" : exception.GetType().Name + ": " + exception.Message;
    }
}
