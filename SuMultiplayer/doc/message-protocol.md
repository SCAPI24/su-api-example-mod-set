# 消息协议（protobuf）与第三方扩展

SuMultiplayer 的联机消息**全部是 protobuf**：`.proto` 在 `Protocol/`，构建时由
`Grpc.Tools` 调 `protoc` 生成 C#，运行期只依赖 `Google.Protobuf.dll`。
旧的逐字段手写 `Encode/Decode` 已删除（live-map 的网页 feed 是唯一的冻结例外）。

---

## 1. 线上格式

每条消息仍然是原来的 16 字节 TCP 帧头 + 负载：

```
offset 0  : int32  payloadLength   （小端，最大 60 KiB）
offset 4  : int64  sequence        （小端，发送方每条连接自增）
offset 12 : uint16 type            （小端，紧凑 id：内置 1..999 / 第三方 1000..1999）
offset 14 : uint16 flags           （小端，当前恒 0；保留）
offset 16 : protobuf payload       （该 type 对应的消息字节）
```

* `type` 从 1 字节扩成 2 字节，才能容纳第三方 id；帧头仍是 16 字节。
* 传输层不变：`TcpTerrainServer` / `TcpTerrainClient` 仍是每连接一个接收线程 +
  有界发送队列 + 发送线程，**允许一次 write 里连续写多帧**（队列里有多少帧就写多少）。
* `TerrainFrame.MaxPayloadBytes = 60 * 1024`；超限在编码时抛异常、在解码时判协议违规，
  不会截断后假装成功。
* 每帧只放**一条**消息；批量语义由具体消息的 `repeated` 字段表达（例如
  `ChunkDigestReport.entries`、`CellChange.entries`、`PlayerPositions.entries`）。

### 1.1 每帧 / 每 tick 预算（没有变）

| 预算 | 默认 | 位置 |
|---|---|---|
| 单条摘要上报最多条目 | 4096 | `TerrainSyncOptions.MaxDigestEntriesPerMessage` |
| 主机每 tick 处理欠裁决上报 | 16 | `TerrainSyncOptions.PendingReportsPerTick` |
| 客户端每帧发摘要条目 | 4 | `ClientReportEntriesPerTick` |
| 客户端每帧落地回传分片 | 1 | `ClientResyncPartsPerTick` |
| 客户端每帧发内存区块消息 | 4 | `ClientChunkMessagesPerTick` |
| 追帧每批条目 / 每 tick 批数 | 64 / 2 | `CatchUpBatchEntries` / `CatchUpBatchesPerTick` |
| 世界传输每 tick 分片 | 8 | `WorldTransferHost.ChunksPerTick` |

这些是"把大消息切成有界批次"的唯一入口；protobuf 只改变单条消息的编码，
**没有**放宽任何预算，也没有把多条消息塞进一帧。

---

## 2. 文件与消息 id

| 文件 | 内容 |
|---|---|
| `Protocol/protocol.proto` | `MessageType` 枚举、`Envelope`（逻辑信封）、Hello / HelloAck / InterestUpdate / Bye |
| `Protocol/terrain.proto` | 摘要上报、裁决、整块回传、格改动、追帧控制、客户端内存区块、重新核对 |
| `Protocol/transfer.proto` | 世界清单 / 分片 / 结束 / 缺片 + 随机数状态表 |
| `Protocol/worldinfo.proto` | 时间 / 天气 / 雾 / 闪电 / WorldSettings |
| `Protocol/authority.proto` | 单元格编辑请求 / 裁决 |
| `Protocol/position.proto` | 玩家位置 / 姿态单条与批 |
| `Protocol/creature.proto` | 生物记录批 / 全量重发请求 |
| `Protocol/ambient.proto` | 主机 → 客户端的循环环境音音量（`AMBIENT_SOUNDS_STATE = 93`，每个客户端一条 4 float 小消息） |
| `Protocol/uistate.proto` | 客户端 → 主机的界面层状态（`CLIENT_UI_STATE = 100`：输入形态 / HUD 显隐 / 当前屏幕 / 打开的对话框与模态面板 / 聊天输入行；变化驱动 + 2 s 心跳，见 `Modules/UiStateSyncModule.cs`） |
| `Protocol/vitals.proto` | 主机 → 客户端权威生命 / 体征 / 死亡边沿（`PLAYER_VITALS_REPORT = 95` / `PLAYER_VITALS_EVENTS = 96`） |
| `Protocol/respawn.proto` | 客户端 → 主机复活请求 `PLAYER_RESPAWN_REQUEST = 101` / 主机回执 `PLAYER_RESPAWN_RESULT = 102`（死亡屏点击后每次死亡最多一次；主机校验 owner / dead / 模式 / 冷却；接受后沿用 95/96） |
| `Protocol/Generated/` | 构建时生成并提交的 C#（不参与编译，只作审阅 / 离线参考） |

id 区间：

```
0          非法（帧头校验拒绝）
1..999     内置（protocol.proto 的 MessageType）
1000..1999 第三方（每个模块自己挑一个未占用的 id）
```

**id 只允许追加，绝不复用**。第三方不要用 1..999；重复 id 注册会抛
`InvalidOperationException`（不会静默覆盖）。当前内置 id 最大为 **102**
（`PLAYER_RESPAWN_RESULT`；100 是 `CLIENT_UI_STATE`，99 是 `COMBAT_RNG_SYNC`）；
新增内置消息请从 103 开始。

---

## 3. 代码生成

```bash
# 正常构建：Grpc.Tools 自动调 protoc，产物进 obj/，不污染源码树
dotnet build Mod/SuMultiplayer/SuMultiplayer.csproj -c Release

# 改了 .proto 之后，把新的生成物同步回仓库（提交用）
dotnet build Mod/SuMultiplayer/SuMultiplayer.csproj -c Release -p:RegenerateProtocol=true
```

生成文件在 `Protocol/Generated/`（`Authority.cs` / `Creature.cs` / `Position.cs` /
`Protocol.cs` / `Terrain.cs` / `Transfer.cs` / `Worldinfo.cs`）。
运行期只加载 `Lib/Google.Protobuf.dll`，**不需要** protoc / Grpc.Tools。

生成代码全部是强类型、AOT 安全的：没有反射序列化器，没有动态代码生成。

---

## 4. 注册表与分发（扩展性核心）

`Net/MessageChannel.cs`：

```csharp
public sealed class MessageRegistry
{
    public const int BuiltInMin = 1, BuiltInMax = 999;
    public const int ThirdPartyMin = 1000, ThirdPartyMax = 1999;

    // 强类型注册：id + 生成的 Parser + 处理器 + 归属
    public void Register<T>(int typeId, MessageParser<T> parser,
        IMessageHandler<T> handler, string owner) where T : class, IMessage<T>;

    // 冻结格式（live-map）用的原始注册
    public void RegisterRaw(MessageType type, Action<MessageContext, byte[]> handler, string owner);

    public void RegisterModule(ISuMPMessageModule module);
    public void UnregisterOwner(string owner);
    public bool TryGetTypeId(Type messageType, out int typeId);
    public bool Dispatch(in ReceivedFrame frame);

    public long HandledMessages, UnhandledMessages, MalformedMessages, HandlerFaults;
    public event Action<string> Problem;
}

public readonly struct MessageContext
{
    public int ClientId { get; }       // 来源连接（客户端侧 = 0）
    public long Sequence { get; }      // 帧头序号
    public MessageType Type { get; }
    public int PayloadBytes { get; }
    public byte[] Payload { get; }     // 只有 RegisterRaw 需要
}

public interface IMessageHandler<in T> where T : class, IMessage<T>
{
    void Handle(in MessageContext context, T message);
}

public interface IMessageSender
{
    // 主机：clientId > 0 = 只发给该玩家端；0 = 明确失败（主机不能猜目标）
    // 客户端：clientId = 0 = 发给主机
    bool Send<T>(int clientId, T message) where T : class, IMessage<T>;
}

public interface ISuMPMessageModule
{
    string ModuleId { get; }
    void Register(MessageRegistry registry);
}

public interface IMessageRegistryPort
{
    MessageRegistry Registry { get; }
    IMessageSender Sender { get; }
}
```

分发流程（主线程泵内，一次字典查找 + 一次 protobuf 解析 + 顺序调用处理器）：

```
Tcp 接收线程 → TerrainBridge.PostFrame → 主线程 Pump → MessageChannel.Dispatch(frame)
  → MessageRegistry 按 type 找 Registration<T>
  → Registration<T>.Dispatch: parser.ParseFrom(payload) → 每个 IMessageHandler<T>.Handle
```

* 未注册类型：`UnhandledMessages++` + 一行 `Problem`，整帧丢弃；
* 载荷不是合法 protobuf：`MalformedMessages++` + 一行 `Problem`，整帧丢弃；
* 处理器异常：`HandlerFaults++` + 一行 `Problem`，本次该处理器跳过（其它处理器继续）；
* 一个 type 可以挂多个处理器（按注册顺序）；同一 `owner` 重复注册会抛异常；
* `UnregisterOwner` 在通道关闭 / 模块下线时移除该模块的处理器（解析器保留，发送仍可用）。

### 4.1 组合根里的注册

`TerrainSyncModule` 创建唯一的 `MessageChannel`，把它交给 `TerrainSyncHost` /
`TerrainSyncClient`，并通过 `IMessageRegistryPort` 端口暴露给模块：

```csharp
context.TryGetPort(out IMessageRegistryPort messages);
messages.Registry.RegisterModule(this);       // 模块实现 ISuMPMessageModule
```

内置模块都在 `Attach` 里注册自己；第三方模块两种接入方式：

```csharp
// A. 提前加载：订阅 SuMultiplayer 加载完成时触发的事件
eventBus.SubscribeEvent("SuMultiplayer.MessageRegistry", args =>
{
    var port = args[0] as IMessageRegistryPort;
    port?.Registry.RegisterModule(new MyPingModule());
    return args;
}, EventPriority.NORMAL);

// B. 后加载：主动请求一次
eventBus.TriggerEvent("SuMultiplayer.MessageRegistry.Request",
    new object[] { (Action<IMessageRegistryPort>)(port => port.Registry.RegisterModule(new MyPingModule())) });
```

> 说明：B 里的 `Action<IMessageRegistryPort>` 由 SuMultiplayer 在事件处理里调用，
> 参数就是它的 `IMessageRegistryPort`；第三方只需要在 `TriggerEvent` 里传入自己的回调。

---

## 5. 增加一条消息（内置 / 第三方同一套步骤）

### 5.1 写 `.proto`

```proto
// Protocol/myfeature.proto（第三方放在自己的程序集里即可）
syntax = "proto3";
package mymod.feature;
option csharp_namespace = "MyMod.Feature";
import "google/protobuf/any.proto";

message MyChat {
  string text = 1;
  int32 color = 2;
  bytes ext = 100;                    // 业务自定义字节
  google.protobuf.Any custom = 101;   // 整条自定义消息
}
```

第三方不需要改 `protocol.proto` 的 `MessageType`；用数字 id 注册即可。
内置消息才需要把 id 加进 `MessageType`（保持只追加）。

### 5.2 生成

第三方在自己的 csproj 加：

```xml
<PackageReference Include="Google.Protobuf" Version="3.28.3" />
<PackageReference Include="Grpc.Tools" Version="2.68.1" PrivateAssets="all" />
<Protobuf Include="Proto\*.proto" ProtoRoot="Proto" GrpcServices="None" />
```

生成物随第三方 DLL 一起发布；运行期同样只需要 `Google.Protobuf.dll`。

### 5.3 注册 + 处理 + 发送

```csharp
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using MyMod.Feature;

public sealed class MyChatModule : ISuMPMessageModule
{
    public const int MyChatId = 1000;            // 第三方区间，自己保证不冲突
    private IMessageSender m_sender;

    public string ModuleId => "my-chat";
    public void Bind(IMessageSender sender) => m_sender = sender;

    public void Register(MessageRegistry registry)
    {
        registry.Register(MyChatId, MyChat.Parser, new ChatHandler(this), ModuleId);
    }

    private sealed class ChatHandler : IMessageHandler<MyChat>
    {
        private readonly MyChatModule m_owner;
        public ChatHandler(MyChatModule owner) { m_owner = owner; }

        public void Handle(in MessageContext context, MyChat message)
        {
            // context.ClientId = 发送方连接；message 已经解析好，不需要再碰 byte[]
            Console.WriteLine("chat from " + context.ClientId + ": " + message.Text);
            // 回一条：主机侧 context.ClientId 就是要回的目标；客户端侧用 0（发给主机）
            m_owner.m_sender?.Send(context.ClientId, new MyChat { Text = "ack", Color = message.Color });
        }
    }
}
```

组合根接线：

```csharp
var module = new MyChatModule();
registry.RegisterModule(module);
module.Bind(sender);             // sender = IMessageRegistryPort.Sender
```

发送接口就是 `IMessageSender.Send<T>(int clientId, T message)`：
主机 `clientId > 0` 定向，客户端 `0` 发主机；返回 `false` = 未注册 / 不可用 / 背压，
调用方必须处理（不会静默丢）。

---

## 6. 两个扩展字段

每个核心消息都预留：

```proto
bytes ext = 100;
google.protobuf.Any custom = 101;
```

### 6.1 `bytes ext`（推荐给热路径的小块自定义数据）

```proto
message MyChat {
  string text = 1;
  bytes ext = 100;
}
```

```csharp
// 发送
var chat = new MyChat { Text = "hi", Ext = ByteString.CopyFrom(myBytes) };

// 接收
byte[] mine = message.Ext.ToByteArray();
```

核心模块**从不解释** `ext`；它只是原样保留在消息里（protobuf 也会保留未知字段）。

### 6.2 `google.protobuf.Any custom`（推荐整条自定义消息）

```csharp
// 发送：AOT 安全，不需要反射注册表
var custom = new Any
{
    TypeUrl = "type.googleapis.com/mymod.feature.MyPayload",
    Value = new MyPayload { A = 1 }.ToByteString()
};
var chat = new MyChat { Text = "hi", Custom = custom };

// 接收：按 TypeUrl 判断 + 用生成的 Parser 解析
if (message.Custom != null &&
    message.Custom.TypeUrl == "type.googleapis.com/mymod.feature.MyPayload")
{
    MyPayload payload = MyPayload.Parser.ParseFrom(message.Custom.Value);
}
```

不要用 `Any.Unpack<T>()` 的反射重载做运行期类型发现；上面的显式 `Parser.ParseFrom`
是 AOT 安全的。也不要把 `Any` 用作"核心消息的包装层"——核心消息本身就是 protobuf。

---

## 7. 最小第三方示例：ping / pong

`pingpong.proto`：

```proto
syntax = "proto3";
package thirdparty.ping;
option csharp_namespace = "ThirdParty.Ping";
import "google/protobuf/any.proto";

message Ping { int32 value = 1; string text = 2; bytes ext = 100; google.protobuf.Any custom = 101; }
message Pong { int32 value = 1; string text = 2; bytes ext = 100; google.protobuf.Any custom = 101; }
```

模块：

```csharp
public sealed class PingModule : ISuMPMessageModule
{
    public const int PingId = 1000;
    public const int PongId = 1001;

    public IMessageSender Sender;
    public string ModuleId => "thirdparty-ping";

    public void Register(MessageRegistry registry)
    {
        registry.Register(PingId, Ping.Parser, new DelegateMessageHandler<Ping>(OnPing), ModuleId);
        registry.Register(PongId, Pong.Parser, new DelegateMessageHandler<Pong>(OnPong), ModuleId);
    }

    private void OnPing(MessageContext context, Ping message)
    {
        // 服务端：回 pong；客户端也可以收到别人发来的 ping。
        Sender?.Send(context.ClientId, new Pong { Value = message.Value + 1, Text = message.Text });
    }

    private void OnPong(MessageContext context, Pong message)
    {
        Console.WriteLine("pong " + message.Value + " from " + context.ClientId);
    }
}

// 客户端发送
clientSender.Send(0, new Ping { Value = 41, Text = "hello" });
```

`P:\tmp\tsverify\ThirdPartyChecks.cs` 里的 `TP` 用例就是这条链路的真实 TCP 回归
（独立模块、id 1000/1001、`ext` + `Any custom` 往返、未注册类型显式失败、id 冲突显式失败）。

---

## 8. 冻结的例外：live-map

`MapRequest` / `MapState`（id 15/16）的负载**不是** protobuf：
`Terrain/LiveMap.cs` 的网页 feed 格式被 `doc/live-map.html` 与外部网页直接消费，
改动会破坏实时地图。它们通过 `MessageRegistry.RegisterRaw` 注册（`MapRequest`），
`MapState` 只发送、不解析。新消息一律不要走这条原始路径。

---

## 9. 交互距离（reach）：请求者有效 reach + 主机上限

引擎事实（`Survivalcraft/Game/ComponentMiner.cs:378`）：

```text
reach = (WorldSettings.GameMode == Creative) ? SettingsManager.CreativeReach : 5f
```

`SettingsManager.CreativeReach` 是**客户端本地可调**设置（默认 7.5）。所以主机不能用
`8 m / 6 m / 1.75 m` 之类的固定常量：客户端把 reach 调到 12 m 后本地能点到的方块，
固定 8 m 的主机会判"too-far"；反过来，固定常量也会让主机接受客户端射线根本够不到的目标。

协议：所有玩家交互请求都追加了一个 `optional float reach = N` 字段
（`BlockUseRequest` / `ContainerEditRequest` / `EditableEditRequest` / `CellEditRequest` /
`PickableAcquireRequest` / `PickableDropRequest` / `ProjectileFireRequest` /
`FurnitureDesignRequest` / `ExplosionRequest`）。客户端填**此刻**的引擎 reach
（创造 = `SettingsManager.CreativeReach`，生存 = 5，夹到可发送范围）。

主机校验只有一条规则（实现：`Net/InteractionReach.cs` 的 `InteractionReachGate`）：

```text
allowed = min(requesterReach + 0.5 m, serverMax)
```

| 项 | 值 | 说明 |
|---|---|---|
| `tolerance` | 0.5 m | 覆盖一个 RTT 的位移 + 身体中心 / 眼睛位置差异 |
| `serverMax` | 默认 16 m，可配置 5..64 | `SuMultiplayer.json` 的 `maxinteractionreach`；防作弊上限 |
| 旧客户端 | 缺字段 → 生存 5.0 / 创造 7.5 | 与引擎 `ComponentMiner.Raycast` 同源 |
| 合法声明 | `[0.5, 1024]` m 的有限数 | 否则整条请求 `reach-invalid` 拒绝 |
| 超上限声明 | 按 `serverMax` 放行 | 计数 `reachClampedToServerMax`（不是拒绝） |

拒绝原因固定为 `too-far: <实测> m > <允许> m（reach=...）` 与
`reach-invalid: <值> m（合法范围 ...）`，日志由各模块限流。计数
`reachAwareChecks / reachClampedToServerMax / reachInvalid` 随 5 s 状态行输出。
非玩家相对的距离检查（区块加载 / 世界边界 / 家具源格跨度）不套用本规则。

---

## 10. 声音中继（`REMOTE_ACTION_SOUND = 68`）

同一 id 68 承载两类**纯表现**声音，靠 `source_client_id` 区分：

| 来源 | `source_client_id` | 触发 | 位置语义 |
|---|---|---|---|
| 远端动作窗口 | 请求者 clientId（>0） | 主机替远端玩家执行交互的同步窗口 | `position` = 交互锚点（精确） |
| 全局主机声音 | `0` | 主机每帧音频队列新增条目 | `position` = 主机监听者；`source_distance` = 反解距离 |

全局中继口径：

* 捕获点 = `SuSubsystemAudio`（数据库 `Game.SubsystemAudio` 注入）。它在
  `base.Update` **排空 `m_queuedSounds` 之前**把新增条目交给
  `Engine/RemoteActionAudioReplica.cs`；队列只读，不静音 / 不删条目。
* 每条消息 ≤ 16 条（`MaxHostSoundsPerMessage`），每端每秒 ≤ 30 条
  （`MaxHostSoundsPerSecondPerPeer`），待发上限 256、保留 2 s，
  每 100 ms 一轮；超出全部有计数（`CapDropped` / `Expired` / `SkippedRate`）。
* 只发给客户端角色到（推断）声源 ≤ 48 m 的端；读不到位置的端不发。
* `attenuation_mode` / `source_distance` 是 append-only 字段：
  `0 ABSOLUTE`（窗口）/ `1 RELATIVE_TO_HOST`（全局：客户端按自己的距离重投影）/
  `2 NON_POSITIONAL`（引擎里没有位置的 2D 声音）。
* 防双播：远端动作窗口与营火 / 烧尽 / 拾取 / 水花 / 爆炸 / 武器开火在主机播完后
  登记 `HostSoundCoveredLedger`，全局捕获在**排空时**按（名字 / 目录前缀, 衰减音量）
  消费；客户端本机播过的同名声音由 `ClientLocalSoundLedger` 跳过
  （`audioLocalEchoSkipped`）。客户端绝不产生声音事件。
* 客户端顺序：`(tick,event_id)` 重放保护 → 严格解码 → `event_id` 排序去重 →
  事件限速 → 本机距离裁剪 → 本机回声过滤 → 只调 `SubsystemAudio.PlaySound`。
  autoDelay 的声音按客户端距离重算延迟（`min(d/120, 3)`）。

已知 bypass（明确不覆盖）：`SubsystemAmbientSounds` 的 `Audio/Fire` / `Audio/Water` /
`Audio/Magma` 是 `CreateSound` 的**循环 Sound**，从不进入 `m_queuedSounds`；
本中继不复制连续低鸣。岩浆 / 火焰 / 营火的随机 `Audio/Sizzles` 仍走队列，会中继。
无头主机没有本机监听者时，适配层用客户端化身位置作为虚拟监听者（显式计数
`virtualListenerFrames`），单客户端会话完全正确；多客户端按最近客户端衰减 +
同一距离重投影模型。

---

## 11. 检查清单（提 PR 前）

- [ ] `.proto` 改了 → `-p:RegenerateProtocol=true` 并提交 `Protocol/Generated/`。
- [ ] 新 id 在正确区间；没有复用已删除的 id。
- [ ] 新消息有 `bytes ext = 100;`（以及需要时的 `Any custom = 101;`）。
- [ ] 处理器只做有界工作；大数组走 `repeated` 且受既有预算约束。
- [ ] 发送失败（`Send` 返回 false）有计数 / 限流日志，不静默。
- [ ] 第三方模块在 `Detach` 里 `UnregisterOwner(ModuleId)`（或由宿主代做）。
- [ ] 文件行尾 CRLF（仓库根 `.gitattributes` 是 `* -text`）。
