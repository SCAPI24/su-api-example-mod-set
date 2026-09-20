using Engine;
using System;
using System.Collections.Generic;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// 帧首泵 —— 让"每帧要在帧首做一次的事"变成可复用的能力（任何 Mod 都能用）。
    ///
    /// 为什么必须有它（全部来自源码实测）：
    ///   · <c>Dispatcher.Dispatch</c> 在**主线程调用时会立即执行**（`Engine/Engine/Dispatcher.cs:40-44`），
    ///     只有**后台线程**调用才入队、并在下一帧 `Dispatcher.BeforeFrame()` 执行（同文件 :79-105）。
    ///     所以"想在帧首干活"就必须从一个后台线程 Dispatch。
    ///   · `Mouse.AfterFrame()` / `Keyboard.AfterFrame()` 会在帧末清空 downOnce 数组
    ///     （`Engine/Engine/Input/Mouse.cs:132-138`），因此**按下脉冲只能写在帧首**；
    ///     写在 `Frame.Update`（帧末）会被同帧清掉 —— 这正是"按住键有效、脉冲键无效"的原因。
    ///   · Mod 事件总线只有 `Frame.Update`（帧末，`Survivalcraft/Game/Program.cs:147`）可用，没有帧首回调。
    ///
    /// 闭环：`Frame.Update`（帧末）→ <see cref="SignalFrameEnd"/> 发信号 → 后台线程把队列整体入队
    /// → 下一帧帧首在游戏线程上依次执行。语义与真人输入事件完全一致。
    /// </summary>
    internal sealed class FrameStartPump : IDisposable
    {
        private readonly object m_gate = new object();
        private readonly Queue<Action> m_pending = new Queue<Action>();
        private readonly AutoResetEvent m_frameEnded = new AutoResetEvent(false);

        private Thread m_thread;
        private volatile bool m_running;
        private int m_framesPumped;
        private int m_actionsExecuted;
        private int m_actionsDropped;

        /// <summary>队列上限：防止"游戏没在推进"时无限堆积。</summary>
        public int MaxPendingActions { get; set; } = 256;

        public bool IsRunning
        {
            get { return m_running; }
        }

        public int PendingCount
        {
            get { lock (m_gate) return m_pending.Count; }
        }

        public int FramesPumped
        {
            get { return m_framesPumped; }
        }

        public int ActionsExecuted
        {
            get { return m_actionsExecuted; }
        }

        public int ActionsDropped
        {
            get { return m_actionsDropped; }
        }

        /// <summary>排一个"下一帧帧首执行"的动作。线程安全。</summary>
        public void Enqueue(Action action)
        {
            if (action == null)
                return;

            Start();
            lock (m_gate)
            {
                if (m_pending.Count >= MaxPendingActions)
                {
                    m_actionsDropped++;
                    Log.Warning("[CmdBridge] FrameStartPump queue is full, dropping one action.");
                    return;
                }
                m_pending.Enqueue(action);
            }
        }

        public void Start()
        {
            if (m_running)
                return;
            if (!GameThreadInvoker.IsDispatcherReady())
                return; // 游戏还没进入首帧：下一次 Enqueue 会再试

            m_running = true;
            m_thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "CmdBridge.FrameStartPump"
            };
            m_thread.Start();
        }

        public void Stop()
        {
            if (!m_running)
                return;
            m_running = false;
            m_frameEnded.Set();
            lock (m_gate)
            {
                m_pending.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
            m_frameEnded.Dispose();
        }

        /// <summary>
        /// 由 Mod 的 `Frame.Update` 钩子每帧调用一次（游戏线程，帧末）。
        /// 它只负责"举手"，真正的执行在下一帧帧首。
        /// </summary>
        public void SignalFrameEnd()
        {
            if (!m_running)
                return;
            m_framesPumped++;
            m_frameEnded.Set();
        }

        /// <summary>把当前队列整体入队到下一帧帧首（必须在后台线程调用，否则 Dispatcher 会就地执行）。</summary>
        private void Loop()
        {
            while (m_running)
            {
                if (!m_frameEnded.WaitOne(250))
                    continue; // 游戏没出帧（最小化/暂停）：什么都不做，也不堆积

                if (!m_running)
                    break;

                Action[] batch = null;
                lock (m_gate)
                {
                    if (m_pending.Count > 0)
                    {
                        batch = m_pending.ToArray();
                        m_pending.Clear();
                    }
                }

                if (batch == null)
                    continue;

                try
                {
                    // 注意：这里绝不能传 waitUntilCompleted=true —— 游戏若停止出帧会永久阻塞本线程。
                    Dispatcher.Dispatch(delegate
                    {
                        for (int i = 0; i < batch.Length; i++)
                        {
                            try
                            {
                                batch[i]();
                                m_actionsExecuted++;
                            }
                            catch (Exception exception)
                            {
                                Log.Warning("[CmdBridge] frame-start action failed: "
                                    + exception.GetType().Name + ": " + exception.Message);
                            }
                        }
                    });
                }
                catch (Exception exception)
                {
                    Log.Warning("[CmdBridge] FrameStartPump dispatch failed: " + exception.Message);
                }
            }
        }
    }
}
