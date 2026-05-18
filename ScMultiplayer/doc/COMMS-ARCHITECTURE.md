# Comms 网络库架构文档

> 版本: 基于 ScMultiplayer 项目内置 Comms.dll
> 最后更新: 2026-05-17

---

## 一、层次结构

| 层 | 类 | 职责 |
|----|-----|------|
| 物理层 | UdpTransmitter | UDP Socket (IPv4+IPv6), 单线程接收循环 |
| 传输层 | Comm | 包收发、可靠/不可靠投递、包排序、分片、连接状态管理 |
| 会话层 | Peer | 节点发现、连接/断开握手、KeepAlive、数据消息路由 |
| 框架层 | Comms.Drt | Server/Client/Explorer -- Tick 驱动游戏网络框架 |

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
- ResendPeriods = [0.5f, 1.0f] (重发间隔秒)
- DuplicatePacketsDetectionTime = 20f (重复包检测窗口)
- IdleTime = 120f (空闲连接超时)

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
2. **包大小**: MaxPacketSize = 1024 字节, 超大数据自动分片 (MessagePart 机制)
3. **广播必须用 Raw**: 否则抛 InvalidOperationException
4. **SendInput 是 Reliable**: 当前位置同步用 Reliable, 高频场景应改 Unreliable
5. **Server 静默失败**: OnLoad 中 catch(Exception){} 可能隐藏错误
6. **无断线重连**: Disconnected 后需手动重新 JoinGame
7. **端口确定性**: "SuSCMP".ToDynamicPort() -> 49152-65535, 同字符串得同端口
8. **序列化小端序**: Reader/Writer 均为 Little Endian
9. **.scmod 依赖**: Comms.dll 必须打包进 .scmod 的 Lib/X64/
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
