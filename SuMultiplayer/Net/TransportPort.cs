using System;
using SuMultiplayer.Transfer;

using SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 传输端口：**持有 TCP 连接的模块**（地形模块）把它注册进 ModuleHost，
    /// 别的模块（世界传输、会话）只通过端口用它，不自己建连接。
    /// 端口拿不到 = 能力缺失 → 调用方降级，不崩。
    /// 演示页标记：PORT01。
    /// </summary>
    public interface ITransportPort : ITransferChannel
    {
        /// <summary>入站消息（**已在主线程**回调）。</summary>
        event Action<ReceivedFrame> FrameArrived;
        /// <summary>
        /// 有**玩家端**完成加入握手（clientId, 名字, 身份, 是否重连）。
        /// 身份 = 对端 UserId.dat（Hello 末尾追加字段）；旧构建 / 无法取得时为空串。
        /// <c>rejoin</c> = 对端在 Hello 里声明"我已经有过这个世界"（断线重连）：
        /// 世界传输据此**不再重发整图**，联机角色仍照常建（旧角色在 ClientLeft 时已经摘掉了）。
        /// Debug 只读端不会触发（过滤在 TerrainSyncModule 里）—— 调试端不建角色。
        /// </summary>
        event Action<int, string, string, bool> ClientJoined;
        /// <summary>
        /// 一个玩家端的连接**已经不可达**（对端关闭 / 链路失效 / 协议违规）：(clientId, 原因)。
        /// 与 <see cref="ClientJoined"/> 成对：提供方据此清掉它的联机角色（否则角色实体会停在
        /// 最后一个已应用的位置上，重连后还会多出一个）。同一端只抛一次。
        /// </summary>
        event Action<int, string> ClientLeft;
        /// <summary>
        /// 主机端：把消息**只发给**指定的玩家端（clientId）。多客户端时世界传输必须能定位自己的目标：
        /// 用 <see cref="ITransferChannel.TrySend"/> 只会发给“最后加入的那个”，两条传输会互相串流
        /// （实机缺陷：两个客户端加入，第一个的世界分片被后加入的连接抢走）。
        /// 连接不可达 / 不是玩家端 → 返回 false（**明确失败**，绝不改投别的客户端）。
        /// </summary>
        bool TrySendTo(int clientId, MessageType type, byte[] payload);
        /// <summary>
        /// 主机端：这个玩家端此刻**还活着、可以尝试发**（Connected / 短暂背压 Backpressured）。
        /// 背压 = 发送队列满、这一帧发不进去，连接仍然可用（下一帧重试），**绝不能当成断线**。
        /// 客户端端恒为 false。
        /// </summary>
        bool IsClientSendable(int clientId);
        /// <summary>
        /// 客户端端：HelloAck 里收到的主机身份（UserId.dat）。未握手 / 旧构建 = 空串。
        /// **建"主机角色"必须用它按身份绑定**，不允许退回 index 键。
        /// </summary>
        string HostIdentity { get; }
        /// <summary>客户端端：HelloAck 里收到的主机名字。未握手 / 旧构建 = 空串。</summary>
        string HostName { get; }
        /// <summary>
        /// 客户端端：主机在 HelloAck 里给本端的传输层编号（旧 <c>client.ClientID</c>）。未握手 = -1。
        /// 位置快照批里"本机玩家自己那一条"靠它分辨（它**不是**身份）。
        /// </summary>
        int LocalClientId { get; }
    }
}
