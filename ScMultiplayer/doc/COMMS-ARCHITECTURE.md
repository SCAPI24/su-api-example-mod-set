# Comms 网络库架构文档

> 版本: 基于 ScMultiplayer 项目内置 Comms.dll
> 最后更新: 2026-05-17

---

## 一、层次结构

| 层 | 类 | 职责 |
|----|-----|------|
| 物理层 | UdpTransmitter | UDP Socket (IPv4+IPv6), 单线程接收循环 |
| 物理层（可靠流） | TcpTransmitter | TCP 监听/拨号 + 长度前缀成帧 + 握手把 `UDP ip:port ↔ TCP 连接` 绑定 |
| 物理层（组合） | HybridTransmitter | 按 Comm 包类型分流：可靠包走 TCP，Raw/Unreliable/ACK 走 UDP；对外身份仍是 UDP 端点 |
| 传输层 | Comm | 包收发、可靠/不可靠投递、包排序、分片、连接状态管理 |
| 会话层 | Peer | 节点发现、连接/断开握手、KeepAlive、数据消息路由 |
| 框架层 | Comms.Drt | Server/Client/Explorer -- Tick 驱动游戏网络框架 |

### 可靠流（`ITransmitter.IsReliableStream`）

TCP 自带投递保证、顺序与去重，因此 `HybridTransmitter` 声明 `IsReliableStream = true`，Comm 相应跳过自己的
ACK/重传簿记（`Comm.SendDataPacket` 不登记 `UnackedPackets`，`Comm.ProcessReceivedPacket` 不回 ACK）。
包类型保持不变（`ReliableData = 3`），分流就是按它做的，握手不需要额外协商：两端都是同一版本才互通。

- **端口方案**：TCP 复用与 UDP **相同的端口号**（TCP/UDP 端口空间独立），服务端 `BindFirstAvailableServerPort`
  要求 UDP 与 TCP 同时绑定成功才采用该端口，否则顺延下一个候选端口。
- **身份**：`TcpTransmitter.Address` 是该通道的 UDP 端点，收到的包也以对端 UDP 端点作为 `Packet.Address`，
  所以消息里携带的发送者端口语义不变。
- **握手**：连接建立后第一条记录是 8 字节（magic + 版本 + 保留 + 宣告的 UDP 端口）；IP 取自 socket 远端地址
  （dual-mode 监听会把 IPv4 映射地址归一化回来），因此同时适配回环、多网卡与 IPv4/IPv6。
- **不阻塞游戏线程**：发送只入队，由每连接的写线程落 socket；队列深度经
  `ITransmitter.GetPendingSendCount` 汇入 `Comm.GetUnackedPacketsCount`，应用层窗口与自适应限速仍然有效。

---

## 二、传输模式 (DeliveryMode)

| 模式 | 确认 | 排序 | 重发 | 适用场景 |
|------|------|------|------|----------|
| Raw | 无 | 无 | 无 | 局域网发现广播 |
| Unreliable | 无 | 无 | 无 | 高频位置更新 |
| UnreliableSequenced | 无 | 有序去旧 | 无 | 状态快照流 |
| Reliable | ACK确认 | 无序 | 有 | 聊天/重要控制消息 |
| ReliableSequenced | ACK确认 | 严格有序 | 有 | 方块修改/加入请求 |

### CommSettings 默认值
- MaxResends = 30 (最大重发次数)
- ResendPeriods = [0.1f, 0.15f] (重发间隔秒；**旧文档写的 [0.5, 1.0] 已过期**)
- MinimumResendPeriod = 0.08f
- ResendBackoffFactor = 1.25f，MaximumResendBackoffSteps = 4，MaximumBackoffPeriod = 1.0f
  （退避上限独立于基准上限：以前用 `MaximumResendPeriod=0.15` 夹结果，把指数退避压平成了 ~150ms 恒速重传）
- MessagePartsTimeout = 10f (半截消息多久后丢弃；替代旧的 `MaxResends × ResendPeriods[last] = 4.5s`)
- ReliableSequencedStallTimeout = 2f (可靠有序流卡在缺口多久后跳到最旧的已缓冲消息继续，避免永久停顿)
- DuplicatePacketsDetectionTime = 20f (重复包检测窗口)
- IdleTime = 120f (空闲连接超时)

### 单包上限
- UDP: `UdpTransmitter.DefaultMaxPacketSize = 1200`（IPv6 最小 MTU 1280 − 40 − 8，隧道下也不会被 IP 分片）
- TCP: `TcpTransmitter.DefaultMaxPacketSize = 64K`；`HybridTransmitter.MaxPacketSize` 取两者较小值，
  所以 Comm 的分片仍然按数据报安全值来，可靠包只是改走流

### PeerSettings 默认值
- KeepAlivePeriod = 10f
- KeepAliveResendPeriod = 1f
- ConnectionLostPeriod = 30f
- ConnectTimeOut = 8f
- SendPeerConnectDisconnectNotifications = true

---

## 三、Comms.Drt -- Tick 驱动游戏网络框架

### 核心模型
Server 按 TickDuration 间隔推进, Client 按 SafetyLag 缓冲后推进,
所有客户端共享同一 Tick 流。Explorer 通过 UDP 广播发现局域网 Server。

### Server
- 管理 ServerGame 列表 (多房间隔离)
- 收集客户端 Input -> 打包 ServerTickMessage -> 广播所有客户端
- 处理加入/离开握手流程
- 支持 DesyncDetection (状态哈希校验模式)

### Client
- CreateGame(addr, descBytes, name) -> 房主 (ClientID=0)
- JoinGame(addr, gameID, joinBytes, name) -> 加入已有游戏
- SendInput(byte[]) -> Reliable 模式发送
- SendState(step, byte[]) -> 状态快照
- AcceptJoinGame / RefuseJoinGame -> 批准/拒绝加入请求

### GameStep 事件 (每 Tick)
struct GameStepData {
    int Step;                    // 当前步数
    JoinData[] Joins;           // 新加入: { ClientID, Address, JoinRequestBytes }
    LeaveData[] Leaves;         // 离开: { ClientID }
    InputData[] Inputs;         // 输入: { ClientID, InputBytes }
}

### Explorer
- 局域网广播发现 (UDP Broadcast 到 serverPort)
- DiscoveredServers -> IReadOnlyList<ServerDescription>
- ServerDescription: Name, Priority, Ping, IsLocal, GameDescriptions[]

---

## 四、ScMultiplayer 使用的 Comms 接口

### Peer 层
| 方法/事件 | 用途 | 调用位置 |
|-----------|------|----------|
| Peer.Start() | 启动对等通信 | OnLoad (内部) |
| Peer.ConnectedTo | 检查是否已连接 | Update() 状态判断 |
| Peer.Address | 本地 IPEndPoint | 消息发送者标识 |
| Peer.Disconnect() | 断开连接 | LeaveGame() |
| Peer.PeerDiscoveryRequest | 发现请求 | Server 处理 |
| Peer.ConnectRequest | 连接请求 | Server 处理 |
| Peer.ConnectAccepted | 连接接受 | Client 处理 |
| Peer.ConnectRefused | 连接拒绝 | Client 处理 |
| Peer.PeerDisconnected | 对端断开 | Server/Client 内部 |

### Client 层 (Drt)
| 方法 | 用途 |
|------|------|
| Start() | 启动 |
| CreateGame() | 创建房间 |
| JoinGame() | 加入房间 |
| LeaveGame() | 离开房间 |
| SendInput(byte[]) | 发送输入 (Reliable) |
| SendState(int, byte[]) | 发送状态 |
| SendGameDescription(byte[]) | 发送房间描述 |
| AcceptJoinGame(int) | 接受加入 |
| RefuseJoinGame(int, string) | 拒绝加入 |
| IsConnected / ClientID / Step | 状态属性 |

### Server 层 (Drt)
| 方法 | 用途 |
|------|------|
| Start() | 启动 |
| Games | 游戏列表 |
| Address | 地址 |
| Information 事件 | 日志 |

### Explorer 层 (Drt)
| 方法 | 用途 |
|------|------|
| StartDiscovery() | 开始局域网发现 |
| StopDiscovery() | 停止发现 |
| DiscoveredServers | 已发现的服务器列表 |
| Error 事件 | 错误回调 |

---

## 五、关键规则与限制

1. **线程安全**: Comms 非线程安全, 所有操作需在 Lock 下执行, Drt 内部已处理
2. **包大小**: UDP 单包 1200 字节（原 1024），超大数据自动分片 (MessagePart 机制)；可靠包走 TCP 流，不再逐片 ACK/重传
3. **广播必须用 Raw**: 否则抛 InvalidOperationException
4. **SendInput 是 Reliable**: 当前位置同步用 Reliable, 高频场景应改 Unreliable
5. **Server 静默失败**: OnLoad 中 catch(Exception){} 可能隐藏错误
6. **无断线重连**: Disconnected 后需手动重新 JoinGame
7. **端口确定性**: "SuSCMP".ToDynamicPort() -> 49152-65535, 同字符串得同端口
8. **序列化小端序**: Reader/Writer 均为 Little Endian
9. **.scmod 依赖**: `IsMergeLib=true`，Comms.dll 必须打包进 `.scmod` 的 `Lib/`
10. **ModInfo.xml Dependencies**: 声明 Comms 依赖

---

## 六、未使用但可用的接口

| 接口 | 潜在用途 |
|------|----------|
| Peer.DiscoverPeer(IPEndPoint) | 直连指定地址 |
| Peer.DisconnectPeer(PeerData) | 踢出玩家 |
| Peer.DisconnectAllPeers() | 关闭房间 |
| Peer.SendDataMessages() | 批量消息优化 |
| Peer.RespondToDiscovery() | 自定义发现响应 |
| client.SendDesyncState() | 不同步状态上报 |
| client.SendStateHashes() | 快速一致性校验 |
| server.SendResource() | 大型资源分发 |
| server.DisconnectAllClients() | 关闭全部连接 |
| DeliveryMode.Unreliable | 高频位置 (减少带宽) |
| DeliveryMode.UnreliableSequenced | 状态快照流 |

---

## 七、数据流图

[本机玩家操作]
  |
  v
SuComponentInput.Update()
  每帧读取 PlayerInput
  |
  v
ScMultiplayer.Update() (30fps)
  |
  +--> SendGamePlayerPositionMessage()
  |      |
  |      v
  |    Message.Write() -> client.SendInput() -> Server -> 广播所有 Client
  |
  +--> SendGameWorldInfoMessage() (仅 Host)
         |
         v
       Message.Write() -> client.SendInput() -> Server -> 广播

Client.GameStep 事件 (接收)
  |
  +--> Inputs[]: Message.Read() -> HandleXxxMessage()
  |       GamePlayerPosition -> 更新远程玩家位置
  |       GameModifiedCells -> 执行方块修改
  |       GameWorldInfo1 -> 同步游戏时间
  |       GamePakWorld -> 导入世界存档
  |       ChatMessage -> 显示聊天
  |
  +--> Joins[]: 分配 PlayerIndex -> AcceptJoinGame / RefuseJoinGame
  +--> Leaves[]: 释放 PlayerIndex

---

## 八、Comms.Drt Tick 驱动详解

### 实时模式 (TickDuration > 0)
Server Tick 循环:
  NextTickTime = floor(time/TickDuration+1) * TickDuration
  每隔 TickDuration 推进一次
  CreateTickMessage -> 打包所有客户端 Input
  SendDataMessageToAllClients (ReliableSequenced)

Client 接收:
  收到 ServerTickMessage -> 入队
  SafetyLag 缓冲 -> 按 StepsPerTick 推进 GameStep

### Client 缓冲公式
MaxAllowedStep = (LastTick + 1) * StepsPerTick
WaitTime = SafetyLag - (MaxAllowedStep - Step) * StepDuration + (NextTickExpectedTime - time)
WaitTime <= 0 时立即执行下一步

---

## 九、本机双端联调（同一台机器跑两个实例）

**同机双端可以互相看到房间，直接加入即可。** 已验证（2026-09-22）：

- 主持端日志：`[ScMP] CreateGame attempt 1/5, local=127.0.0.1:51459, advertised=192.168.31.28:51459` + `GameCreated`
- 加入端 Play 屏出现该房间，条目详情带在线标记：
  `Stanva Yethern | 144KB | … | 1 名玩家 | 生存 | 动态 | 在线（延迟 1ms）`
- `在线（延迟 x ms）` = 局域网广播发现的在线房间；`服务（延迟 x ms）` = DNS/显式端点发现的服务房间。

⚠️ **排查时的坑**：同机两端的存档常常同名，Play 列表里显示的是**房间条目**而不是加入端自己的本地存档，
不要只看条目名就判断"没发现房间" —— 要看条目详情里的 `在线（延迟 …）` 标记。

### 本机服务器的过滤条件（只跳过"自己"）

`ScMultiplayer.IsLocalServerEndpoint(endpoint)` 要求**端口与本机服务器相同**且地址是本机 IPv4：

```csharp
endpoint.Port == server.Address.Port && UdpTransmitter.IsLocalIPv4Address(endpoint.Address)
```

所以它只会跳过**本进程自己的**服务器；同机另一个实例用的是另一个端口（51459 / 51460 各占一个），
不会被跳过。若两端分别显示各自的空服务器，属于正常。

### 发现链路自检（不启动游戏也能查）

用 `Comms.Drt.Explorer` 直接探测主持端（Comms.dll 可独立引用）：

- 广播：`explorer.StartDiscovery(true, Array.Empty<string>())`，应收到 `ServerDiscovered`，
  并含 `房间地址 games=1`（空服务器为 `games=0`）。
- ⚠️ `Explorer.StopDiscovery()` 会**清空** `ServersList`，所以读 `DiscoveredServers` 必须在停止之前，
  否则会看到 0 条而误判为"发现失败"（本次排查就踩过）。

### 验证 TCP 真的在用

- `Get-NetTCPConnection`：两端进程之间在房间端口上有 `Established` 连接。
- 加入端日志：`World download complete: Transfer=1, Transport=TCP, …, RepairRounds=0`。


### 防火墙 / UAC

- 只有联机 Mod 会 `Bind` 端口（主程序只有出站 socket），所以防火墙提示只由"装了联机 Mod 并首次联网"触发。
- TCP 复用与 UDP **相同的端口号**，因此针对 `Survivalcraft.exe` 的程序级入站规则同时覆盖两种协议，
  不需要为 TCP 再开端口。
- 规则要覆盖**当前网络档案**：本机实测规则只建在 `Public`；若网络被标成 `Private` 需重新放行，
  否则跨机发现与入站连接都会被静默丢弃。
- 本机双实例不需要防火墙放行（回环不受影响），跨机才需要。

---

## 十、IPv4 / IPv6 共存

- **两族同端口**：`UdpTransmitter` 在 `localPort = 0` 时先绑 IPv4 拿到系统分配的临时端口，再让 IPv6 绑
  **同一个端口**（该端口在 v6 空间已被占用时才退回各拿一个临时端口）。端口就是消息里携带的 peer 身份，
  两族用不同端口会让**同一个客户端在服务端变成两个身份**：可靠流绑在一族、UDP 包来自另一族，
  表现为 InitAck 串台、可靠包因"没有对应流"被丢弃。
- **IPv6Only = true**：v6 socket 不会重复收到 v4 流量，因此不存在重复投递。
- **TCP 双栈监听**：`TcpTransmitter` 用 dual-mode 监听；v4 客户端以 v4-mapped 地址到达时归一化回 IPv4，
  与 UDP 侧身份一致。若平台不接受 `DualMode`，回退 IPv4-only 监听（此时 IPv6 对端只有 UDP 通路）。
- **实测（双栈服务器 + v4 客户端 + v6 客户端同时接入）**：可靠（TCP）与不可靠（UDP）在两族都送达；
  v4 保持 v4、v6 保持 v6（没有 v4-mapped 泄漏）；两族是**互不相同的身份**、互不干扰；
  v6 客户端 24 个包、v4 客户端 12 个包各自只产生**一个**身份；0 重传、0 传输错误；
  v4 对端退出后 v6 流仍然可用。
- **路由粘性（防地址族来回跳）**：房间条目记住当前可用路由（`SuPlayScreen.m_remoteChosenRoute`），
  另一族在同一房间上应答时不会顶掉它；只有当前路由静默超过 `RemoteRouteHoldSeconds = 15s` 才**单向**回退
  到另一族。explicit/DNS 端点每 `InternetDiscoveryPeriod = 3s` 会把 A 与 AAAA 都探一遍，
  没有这条规则条目会按 3 秒节奏在两族间翻面。
- **会话内不切族**：`Peer.Connect` 之后整个会话固定在同一个端点，重连使用保存的 `ServerAddress`，
  不会中途改用另一族 —— 所以不存在"1 秒 v6、1 秒 v4"的跳变；允许的是断开后按另一族重新加入。
- 仍只认 IPv4 的位置（仅 IPv6-only 主机才会暴露）：`DetectLanAddress`（只用于日志）、
  `IsLocalIPv4Address` / `IsLocalServerEndpoint`（后者还要求端口与本机服务器相同，因此只跳过"自己"；
  本机自己的服务器若只以 IPv6 出现则不会被识别为本地）、
  `HandleReverseDiscoveryRequest`（忽略 v6 来源）、`GetLocalServerConnectionAddress()`（返回 IPv4 回环）。
- **跨族发现**：局域网广播是单族的（按自身 `Address` 族选择 IPv4 广播或 `ff08::1`）；
  要连另一种地址族的服务器，走 DNS / 个人网络世界 / 显式端点 —— 该路径会同时解析 A 与 AAAA 并逐族探测
  （`RemoteServerDirectory.cs` 的 `ResolveDirectoryEntries`）。


