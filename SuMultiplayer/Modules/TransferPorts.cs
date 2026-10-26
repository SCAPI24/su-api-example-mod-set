namespace SuMultiplayer.Modules
{
    // 本文件只放"世界传输 / 通道控制"需要的端口：它们**不依赖 Engine/Game**，
    // 因此世界传输模块（Modules/WorldTransferModule.cs）可以连同端口定义一起被
    // P:\tmp\tsverify 直接编译，用真实 TCP 验证"重连不下发整图 / 首次加入恰好发一次"。
    // 依赖引擎类型的端口（房间浏览 / 下载世界生命周期 / 联机角色）仍在 Modules/SessionPorts.cs。

    /// <summary>
    /// 传输通道控制端口：**会话模块按用户操作去起/停通道**，而不是启动时按配置自己连。
    ///
    /// 为什么要有它：旧实现的正确入口只有两个 ——
    ///   · 游戏内 CR 按钮建房（主机开始监听）；
    ///   · Play 屏点房间行加入（客户端连到那个房间）。
    /// 所以"什么时候开通道"必须由这两个操作决定，不能由配置文件决定。
    /// </summary>
    public interface ITransportControlPort
    {
        /// <summary>主机开始监听（建房）。端口被占用等失败原因必须回给调用方。</summary>
        bool StartHostServer(int port, out string reason);

        /// <summary>连到某个房间。连不上必须回明确原因，不静默重试成"看起来连上了"。</summary>
        bool ConnectTo(string host, int port, out string reason);

        /// <summary>关闭本端持有的通道（退房 / 离开）。</summary>
        void StopChannel();

        /// <summary>
        /// 客户端**显式退房**时用：先向主机发 BYE（明确"我要走"，主机立即摘角色），
        /// 等发送队列排空（最多约 150ms）再关通道。普通断线/闪退主机按宽限处理；
        /// 只有这里发过 BYE 的才零宽限立即清理（2026-10-26 退房后角色留在地图修复）。
        /// 主机端 / 未连接时等价于 <see cref="StopChannel"/>。
        /// </summary>
        void StopChannelWithBye(string reason);

        bool IsChannelRunning { get; }

        bool IsHosting { get; }

        /// <summary>已连上的玩家端数量（主机侧用于房间广播的在线人数）。</summary>
        int ConnectedPlayerCount { get; }

        string ChannelStateText { get; }
    }

    /// <summary>
    /// 下载世界的生命周期端口（Play 屏在进入世界列表时调用；实现方 = 世界传输模块）。
    /// 对应旧实现 <c>SuPlayScreen.Enter → CleanupDownloadedWorldsBeforeWorldList()</c>。
    /// </summary>
    public interface IDownloadedWorldPort
    {
        /// <summary>删掉上一次下载来的世界（下载世界是临时的，回到列表就该清掉）。</summary>
        bool TryCleanup(out string note);

        /// <summary>
        /// 这个世界目录是不是"下载来的联机世界"。
        /// 客户端建"主机角色"的前提就是它（旧实现 <c>m_shouldCreateHostAvatar</c> 只在世界导入并进入后为真）：
        /// 不能只看"连着通道 + 有 Project"，否则玩家在自己的单机世界里连上房间就会凭空多出一个主机角色。
        /// </summary>
        bool IsDownloadedWorld(string worldDirectory);

        /// <summary>
        /// 读这个下载世界登记在一起的**主机会话标识 + 导出基线序号**（WorldBegin 下发）。
        /// 返回 false = 没有登记 / 没有会话标识（旧世界、旧主机）——调用方必须按首次加入处理，
        /// 绝不猜一个默认会话（猜错会让主机跳过整图、客户端永远停在过期世界上）。
        /// </summary>
        bool TryGetWorldSession(string worldDirectory, out string sessionId, out long baseSequence);

        /// <summary>
        /// 明确把某个下载世界的会话标识作废（主机判定历史已裁剪 / 会话不一致；客户端随后以首次加入身份重连）。
        /// 这是"显式失败"：不是静默清空，调用方必须写一行原因。
        /// </summary>
        void ForgetWorldSession(string worldDirectory);
    }

    /// <summary>
    /// 主机**会话标识 / 改动水位**端口（实现方 = 地形同步模块；世界传输模块通过它给世界清单打标）。
    ///
    /// 为什么单独一个端口（而不是塞进 ITransportPort）：传输通道是"怎么发"，会话标识是"这次建房是谁"，
    /// 两者生命周期不同（会话随房间开始生成，通道可能重连），分开后别的传输实现不必被迫实现它。
    /// </summary>
    public interface IWorldSessionPort
    {
        /// <summary>主机当前房间/会话标识（32 位十六进制；不是主机 = 空串）。**绝不落盘**。</summary>
        string SessionId { get; }

        /// <summary>主机当前已广播的改动序号（世界清单的导出基线 / 追帧范围都基于它；不是主机 = 0）。</summary>
        long CurrentChangeSequence { get; }

        /// <summary>
        /// 客户端：下载世界**已导入完成**（世界传输模块在 Install 成功后调用）。
        /// 地形同步模块据此把"上次应用序号"对齐到世界清单的 baseSequence，
        /// 并把该世界的会话标识记为"最近一次 HelloAck 的主机会话"（下一次重连的判定依据）。
        /// </summary>
        void OnClientWorldInstalled(string worldDirectory, string sessionId, long baseSequence);
    }

    /// <summary>
    /// 下载世界就绪端口：世界传输模块**导入完就发布**，由 Play 屏消费后切 GameLoading。
    /// 传输模块自己绝不切屏（切屏是界面的事，旧实现也是由 SuPlayScreen.Play 完成的）。
    /// </summary>
    public interface IWorldReadyPort
    {
        void Publish(string directory, string worldName);

        bool TryTake(out string directory, out string worldName);
    }
}
