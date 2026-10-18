using System;
using System.Buffers;
using System.Threading;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 传输层资源计数器（缓冲池 / 连接 / 专属线程）。
    ///
    /// 为什么要有它：联机传输每帧都要借帧缓冲、每条连接都会占一个 socket，
    /// 以前这些数字在日志里完全看不见，只能靠猜。这里把「借了多少 / 还了多少 /
    /// 现在还活着多少」做成进程级只增计数器，**每 10 秒最多写一行**（限流），
    /// 并且池的借还必须配平（<see cref="PoolLive"/> 归零才算没有泄漏）。
    ///
    /// 线程安全：所有计数都用 <see cref="Interlocked"/>；日志用 CAS 抢写权，
    /// 不会因为并发调用刷屏，也不会阻塞收发路径。
    /// 演示页标记：NET03。
    /// </summary>
    public static class TransportDiagnostics
    {
        /// <summary>日志限流间隔（毫秒）。需求是 10 秒；常量公开便于自检核对。</summary>
        public const int LogIntervalMs = 10_000;

        private static long s_poolRented;
        private static long s_poolReturned;
        private static long s_poolBytesRented;

        private static long s_connectionsCreated;
        private static long s_connectionsReused;
        private static long s_connectionsClosed;

        private static long s_threadsCreated;
        private static long s_threadsStopped;

        private static long s_socketsOpened;
        private static long s_socketsClosed;
        private static long s_socketsPeak;
        private static long s_listenersOpened;

        private static long s_lastLogMs = Environment.TickCount64;

        /// <summary>累计借出的缓冲区块数（借 = 从 <see cref="ArrayPool{T}.Shared"/> 取）。</summary>
        public static long PoolRented => Interlocked.Read(ref s_poolRented);

        /// <summary>累计归还的缓冲区块数（每一条借出路径都必须在 finally 里归还）。</summary>
        public static long PoolReturned => Interlocked.Read(ref s_poolReturned);

        /// <summary>当前未归还的缓冲区块数；正常应恒为 0。</summary>
        public static long PoolLive => PoolRented - PoolReturned;

        /// <summary>累计向池借出的字节数（按池实际返回的数组长度计）。</summary>
        public static long PoolBytesRented => Interlocked.Read(ref s_poolBytesRented);

        /// <summary>累计建立的传输连接数（主机 accept + 客户端成功连接）。</summary>
        public static long ConnectionsCreated => Interlocked.Read(ref s_connectionsCreated);

        /// <summary>复用已有连接的次数（不新开 socket，例如自检里的可复用 TCP 通道）。</summary>
        public static long ConnectionsReused => Interlocked.Read(ref s_connectionsReused);

        /// <summary>累计关闭的传输连接数。</summary>
        public static long ConnectionsClosed => Interlocked.Read(ref s_connectionsClosed);

        /// <summary>当前仍然活着的传输连接数。</summary>
        public static long ConnectionsActive => ConnectionsCreated + ConnectionsReused - ConnectionsClosed;

        /// <summary>历史上同时活着的连接数峰值。</summary>
        public static long ConnectionsPeak => Interlocked.Read(ref s_connectionsPeak);

        private static long s_connectionsPeak;

        /// <summary>累计打开的真实 TCP socket 数（listener + client；内存传输不计）。</summary>
        public static long SocketsOpened => Interlocked.Read(ref s_socketsOpened);

        /// <summary>已关闭的真实 TCP socket 数。</summary>
        public static long SocketsActive => SocketsOpened - Interlocked.Read(ref s_socketsClosed);

        /// <summary>同时打开的真实 TCP socket 数峰值。</summary>
        public static long SocketsPeak => Interlocked.Read(ref s_socketsPeak);

        /// <summary>累计打开的 TcpListener 数（一个监听端口 = 一个 socket）。</summary>
        public static long ListenersOpened => Interlocked.Read(ref s_listenersOpened);

        /// <summary>累计创建的传输专属线程数（异步化后应为 0）。</summary>
        public static long ThreadsCreated => Interlocked.Read(ref s_threadsCreated);

        /// <summary>已结束的传输专属线程数。</summary>
        public static long ThreadsActive => ThreadsCreated - Interlocked.Read(ref s_threadsStopped);

        /// <summary>
        /// 从共享池借一个至少 <paramref name="minimumLength"/> 字节的缓冲。
        /// **调用方必须在 finally 里调 <see cref="ReturnBuffer"/>**（所有路径，含异常/队列满/断开）。
        /// </summary>
        public static byte[] RentBuffer(int minimumLength)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(minimumLength);
            Interlocked.Increment(ref s_poolRented);
            Interlocked.Add(ref s_poolBytesRented, buffer.Length);
            MaybeLog();
            return buffer;
        }

        /// <summary>归还 <see cref="RentBuffer"/> 借出的缓冲；null 直接忽略（不算一次归还）。</summary>
        public static void ReturnBuffer(byte[] buffer)
        {
            if (buffer == null) return;
            ArrayPool<byte>.Shared.Return(buffer);
            Interlocked.Increment(ref s_poolReturned);
            MaybeLog();
        }

        /// <summary>登记一条新建立的传输连接（socket 已就绪）。</summary>
        public static void OnConnectionCreated()
        {
            Interlocked.Increment(ref s_connectionsCreated);
            long active = ConnectionsActive;
            long peak = Interlocked.Read(ref s_connectionsPeak);
            while (active > peak)
            {
                long seen = Interlocked.CompareExchange(ref s_connectionsPeak, active, peak);
                if (seen == peak) break;
                peak = seen;
            }
            MaybeLog();
        }

        /// <summary>登记一次连接复用（不新开 socket）。</summary>
        public static void OnConnectionReused()
        {
            Interlocked.Increment(ref s_connectionsReused);
            long active = ConnectionsActive;
            long peak = Interlocked.Read(ref s_connectionsPeak);
            while (active > peak)
            {
                long seen = Interlocked.CompareExchange(ref s_connectionsPeak, active, peak);
                if (seen == peak) break;
                peak = seen;
            }
            MaybeLog();
        }

        /// <summary>登记一条连接关闭（每个连接只能调一次）。</summary>
        public static void OnConnectionClosed()
        {
            Interlocked.Increment(ref s_connectionsClosed);
            MaybeLog();
        }

        /// <summary>登记打开一个真实 TCP socket（TcpListener / TcpClient）。</summary>
        public static void OnSocketOpened()
        {
            Interlocked.Increment(ref s_socketsOpened);
            long active = SocketsActive;
            long peak = Interlocked.Read(ref s_socketsPeak);
            while (active > peak)
            {
                long seen = Interlocked.CompareExchange(ref s_socketsPeak, active, peak);
                if (seen == peak) break;
                peak = seen;
            }
            MaybeLog();
        }

        /// <summary>登记一个真实 TCP socket 已关闭（与 <see cref="OnSocketOpened"/> 配对）。</summary>
        public static void OnSocketClosed()
        {
            Interlocked.Increment(ref s_socketsClosed);
            MaybeLog();
        }

        /// <summary>登记打开一个 TcpListener。</summary>
        public static void OnListenerOpened()
        {
            Interlocked.Increment(ref s_listenersOpened);
        }

        /// <summary>登记一个传输专属线程开始（异步化后传输层不再创建专属线程）。</summary>
        public static void OnTransportThreadStarted()
        {
            Interlocked.Increment(ref s_threadsCreated);
            MaybeLog();
        }

        /// <summary>登记一个传输专属线程结束。</summary>
        public static void OnTransportThreadStopped()
        {
            Interlocked.Increment(ref s_threadsStopped);
            MaybeLog();
        }

        /// <summary>一行计数快照（日志 / 自检报告共用同一份口径）。</summary>
        public static string Snapshot()
        {
            return "transportPoolRented=" + PoolRented +
                   " transportPoolReturned=" + PoolReturned +
                   " transportPoolLive=" + PoolLive +
                   " transportPoolBytes=" + PoolBytesRented +
                   " transportConnectionsCreated=" + ConnectionsCreated +
                   " transportConnectionsReused=" + ConnectionsReused +
                   " transportConnectionsClosed=" + ConnectionsClosed +
                   " transportConnectionsActive=" + ConnectionsActive +
                   " transportConnectionsPeak=" + ConnectionsPeak +
                   " transportThreadsCreated=" + ThreadsCreated +
                   " transportThreads=" + ThreadsActive +
                   " transportSocketsOpened=" + SocketsOpened +
                   " transportSocketsClosed=" + Interlocked.Read(ref s_socketsClosed) +
                   " transportSocketsActive=" + SocketsActive +
                   " transportSocketsPeak=" + SocketsPeak +
                   " transportListenersOpened=" + ListenersOpened;
        }

        private static void MaybeLog()
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref s_lastLogMs);
            if (now - last < LogIntervalMs) return;
            // 抢到写权的那一个调用才写；抢不到说明别的线程刚写过，直接返回。
            if (Interlocked.CompareExchange(ref s_lastLogMs, now, last) != last) return;
            try
            {
                global::Engine.Log.Information("[SuMP] " + Snapshot());
            }
            catch (Exception e)
            {
                // 日志失败本身不能影响传输；用最小代价留下明确原因（不静默）。
                try
                {
                    global::Engine.Log.Warning("[SuMP] transport 统计日志写入失败：" +
                        e.GetType().Name + " " + e.Message);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
