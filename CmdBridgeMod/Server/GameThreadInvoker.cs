using Engine;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CmdBridgeMod
{
    /// <summary>
    /// 把动作送到游戏线程并在"下一帧帧首"执行。
    ///
    /// 为什么必须是帧首：
    ///   Window.RenderFrameHandler → BeforeFrameAll() → Window.Frame()（帧体）→ AfterFrameAll()
    ///   BeforeFrameAll 依次调用 Dispatcher.BeforeFrame()、Keyboard.BeforeFrame()、Mouse.BeforeFrame()。
    ///   而 Keyboard.AfterFrame()/Mouse.AfterFrame() 会在帧末清空 downOnce 数组，
    ///   因此在帧体末尾的 Frame.Update 里注入脉冲会被同帧清掉。
    ///   从后台线程调用 Dispatcher.Dispatch 会入队，并在下一帧 Dispatcher.BeforeFrame() 执行，
    ///   位置早于键盘/鼠标设备读取与整个帧体，语义与真实输入事件完全一致。
    ///
    /// Source: Engine/Engine/Dispatcher.cs:Dispatcher.Dispatch / Dispatcher.BeforeFrame
    /// Source: Engine/Engine/Window.cs:BeforeFrameAll / AfterFrameAll
    /// </summary>
    internal sealed class GameThreadInvoker
    {
        private readonly int m_timeoutMilliseconds;

        public GameThreadInvoker(int timeoutSeconds)
        {
            m_timeoutMilliseconds = Math.Max(1, timeoutSeconds) * 1000;
        }

        /// <summary>
        /// 在游戏线程（下一帧帧首）执行 action 并等待其完成。返回 action 的返回值。
        /// </summary>
        public object Invoke(Func<object> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            // Mod 的监听在 OnLoad 就绪，早于 Window.Run() 里的 Dispatcher.Initialize()，
            // 因此启动瞬间可能收到请求。此时给一个"稍后重试"的明确错误，而不是让它抛
            // InvalidOperationException（客户端会当成未知失败）。
            if (!IsDispatcherReady())
            {
                throw new BridgeCommandException(
                    "not_ready",
                    "The game is still starting up (the frame dispatcher is not ready yet).");
            }

            // 已在游戏线程（例如 Mod 卸载阶段）时直接执行，避免死等帧首。
            if (IsGameThread())
                return action();

            var completion = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Dispatcher.Dispatch(() =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });

            try
            {
                completion.Task.Wait(m_timeoutMilliseconds);
            }
            catch (AggregateException)
            {
                // 故意吞掉：下面用 GetAwaiter().GetResult() 保留原始异常类型。
                // 客户端依赖 BridgeCommandException 的错误码区分 element_missing 与 element_occluded。
            }

            if (!completion.Task.IsCompleted)
                throw new TimeoutException("The game thread did not run the request in time.");

            return completion.Task.GetAwaiter().GetResult();
        }

        /// <summary>Dispatcher 是否已初始化（可在任意线程安全调用，不抛异常）。</summary>
        public static bool IsDispatcherReady()
        {
            try
            {
                int ignored = Dispatcher.MainThreadId;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// 当前线程是不是游戏主线程。UI 服务用它决定"就地解析+就地注入"还是"排到帧首"：
        /// 行为树 tick 本来就在帧首批次里跑，就地写输入层当帧就能被引擎读到
        /// （于是能如实回答"点到了没有"，而不是只回"已受理"）。
        /// </summary>
        public static bool IsGameThread()
        {
            try
            {
                return Dispatcher.MainThreadId == Environment.CurrentManagedThreadId;
            }
            catch (InvalidOperationException)
            {
                // Dispatcher 尚未初始化（游戏还没进入首帧）。
                return false;
            }
        }
    }
}
