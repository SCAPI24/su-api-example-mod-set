namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 客户端 Hello 里 <c>alreadyHasWorld</c> 标记的**唯一判定**（纯逻辑，不碰引擎、不碰 IO）。
    ///
    /// 语义（实机故障后定稿）：<c>alreadyHasWorld = 本端此刻正在游玩这个已下载的联机世界，
    /// 并且手里这份世界的会话标识与主机当前会话一致</c>。缺一不可：
    ///   · 在游戏里（当前屏幕是 <c>GameScreen</c>）—— Play 屏 / 世界列表 / GameLoading 都不算；
    ///   · Project 已加载；
    ///   · 当前世界目录就是登记过的"下载来的世界"；
    ///   · 下载世界那边**存有会话标识**（主机下发 WorldBegin 时写入）；
    ///   · 本端最近一次 HelloAck 收到过主机会话标识，且与下载世界存的那个**完全一致**。
    ///
    /// 它**不是**"磁盘上有一个下载来的世界 / 登记表里还有它"。实机故障：客户端在 Play 屏加入
    /// （世界正在下载或刚下载完、还没进世界）却声明 alreadyHasWorld=true，主机于是跳过整图发送，
    /// 客户端 Play 屏永远等不到世界（平板卡在 screen=Play / worldLoaded=false / stage=Requesting）。
    ///
    /// 会话标识缺失 / 不一致（主机重启过）⇒ false ⇒ 首次加入 ⇒ 主机下发整图。
    /// 这保证"主机重启后客户端不会拿着过期世界继续玩"。
    ///
    /// 模块（<c>Modules/TerrainSyncModule.cs</c>）把引擎事实与会话事实折算成入参后只调用本方法，
    /// P:\tmp\tsverify 对**同一份源码**做单元断言。
    /// </summary>
    public static class RejoinFlagPolicy
    {
        /// <summary>
        /// 是否在 Hello 里声明"本端已经有过这个世界"（= 断线重连，主机不要重发整图）。
        /// <paramref name="reason"/> 是写进每握手一行日志的**依据**（不是兜底值）。
        /// <param name="storedSessionId">下载世界登记里存的会话标识（WorldBegin 下发；空 = 没有）。</param>
        /// <param name="lastHostSessionId">本端最近一次 HelloAck 收到的主机会话标识（空 = 还没收到过）。</param>
        /// </summary>
        public static bool ShouldSendAlreadyHasWorld(bool inGame, bool projectLoaded,
            bool currentWorldDirectoryKnown, bool currentWorldIsDownloaded,
            string storedSessionId, string lastHostSessionId, out string reason)
        {
            if (!inGame)
            {
                reason = "不在游戏内（Play 屏 / 世界列表 / 加入或加载流程中）→ 按首次加入处理";
                return false;
            }
            if (!projectLoaded)
            {
                reason = "在游戏内但 Project 尚未加载完 → 按首次加入处理";
                return false;
            }
            if (!currentWorldDirectoryKnown)
            {
                reason = "在游戏内但读不到当前世界目录 → 无法证明这是下载世界，按首次加入处理";
                return false;
            }
            if (!currentWorldIsDownloaded)
            {
                reason = "在游戏内，但当前世界不是下载来的联机世界 → 按首次加入处理";
                return false;
            }
            if (string.IsNullOrEmpty(storedSessionId))
            {
                reason = "下载世界没有会话标识（旧世界 / 主机从未下发 / 已被判定过期）→ 按首次加入处理";
                return false;
            }
            if (string.IsNullOrEmpty(lastHostSessionId))
            {
                reason = "还没收到过主机的会话标识（HelloAck）→ 无法证明是同一会话，按首次加入处理";
                return false;
            }
            if (!string.Equals(storedSessionId, lastHostSessionId, System.StringComparison.Ordinal))
            {
                reason = "下载世界的会话 " + SessionIdentity.Short(storedSessionId) +
                    " 与主机当前会话 " + SessionIdentity.Short(lastHostSessionId) +
                    " 不一致（主机重启 / 换了房间）→ 按首次加入处理（整图重下）";
                return false;
            }
            reason = "正在游玩下载来的联机世界（GameScreen + Project + 下载登记匹配 + 会话一致 " +
                SessionIdentity.Short(storedSessionId) + "）→ 断线重连，不重下整图";
            return true;
        }
    }
}
