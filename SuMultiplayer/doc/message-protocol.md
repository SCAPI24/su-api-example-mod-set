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
| `Protocol/worldcontrol.proto` | 世界控制请求 / 回执（`WORLD_CONTROL_REQUEST = 112` / `WORLD_CONTROL_RESULT = 113`）：客户端创意四按钮（降水 / 雾 / 闪电 / 时间）不在本地执行，发请求由主机在权威世界按引擎同款调用落地；状态变化由既有 WorldInfo 快照广播生效，回执带最新降水/雾状态与时间档位供请求者显示提示。见 `Modules/WorldControlModule.cs` 与 `Engine/WorldControlEngine.cs` |
| `Protocol/authority.proto` | 单元格编辑请求 / 裁决 |
| `Protocol/position.proto` | 玩家位置 / 姿态单条与批 |
| `Protocol/creature.proto` | 生物记录批 / 全量重发请求 |
| `Protocol/entities.proto` | 拾取物 / 投射物复制与拾取裁决（`ENTITY_STATE = 38` / `ENTITY_SNAPSHOT = 39` / 拾取请求与回执），以及纯表现事件：`WEAPON_FIRE_EVENT = 54`、`PICKUP_SOUND_EVENT = 69`、`PROJECTILE_HIT_EVENT = 111`（投射物命中伤害数字：主机按每个客户端的兴趣窗口中继，客户端只建 `HitValueParticleSystem`，不扣血 / 不改格） |
| `Protocol/ambient.proto` | 主机 → 客户端的循环环境音音量（`AMBIENT_SOUNDS_STATE = 93`，每个客户端一条 4 float 小消息） |
| `Protocol/uistate.proto` | 客户端 → 主机的界面层状态（`CLIENT_UI_STATE = 100`：输入形态 / HUD 显隐 / 当前屏幕 / 打开的对话框与模态面板 / 聊天输入行 / 本机视角模式（`camera_mode = 18`：0 不改变 / 1 第一人称 / 2 第三人称 / 3 轨道环绕 / 4 固定视点，>4 拒绝）；变化驱动 + 2 s 心跳，见 `Modules/UiStateSyncModule.cs`） |
| `Protocol/vitals.proto` | 主机 → 客户端权威生命 / 体征 / 死亡边沿（`PLAYER_VITALS_REPORT = 95` / `PLAYER_VITALS_EVENTS = 96`） |
| `Protocol/respawn.proto` | 客户端 → 主机复活请求 `PLAYER_RESPAWN_REQUEST = 101` / 主机回执 `PLAYER_RESPAWN_RESULT = 102`（死亡屏点击后每次死亡最多一次；主机校验 owner / dead / 模式 / 冷却；接受后立即 `TryRespawnAvatar`，回执带落点并沿用 95/96）。110（`PLAYER_RESPAWN_STAGE` 进度握手）已回退并保留编号，禁止复用 |
| `Protocol/Generated/` | 构建时生成并提交的 C#（不参与编译，只作审阅 / 离线参考） |

id 区间：

```
0          非法（帧头校验拒绝）
1..999     内置（protocol.proto 的 MessageType）
1000..1999 第三方（每个模块自己挑一个未占用的 id）
```

**id 只允许追加，绝不复用**。第三方不要用 1..999；重复 id 注册会抛
`InvalidOperationException`（不会静默覆盖）。当前内置 id 最大为 **111**
（`PROJECTILE_HIT_EVENT` = 投射物命中伤害数字，主机按每个客户端的兴趣窗口中继；
110 曾是已回退的复活进度握手，编号保留、禁止复用；109 是 `PLAYER_EFFECT_EVENTS`，
108 是 `PLAYER_PROFILE_SYNC`，101/102 是复活请求 / 回执，100 是 `CLIENT_UI_STATE`，
99 是 `COMBAT_RNG_SYNC`）；新增内置消息请从 112 开始。

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

## 11. 收件人按"客户端兴趣窗口"裁剪（纯表现事件 / 逐角色状态）

**口径唯一**：世界是分区加载的 —— 主机加载全部地形并模拟全部实体，客户端通过
`INTEREST_UPDATE` 上报"中心区块 + 半径"，只加载自己周围那片。兴趣窗口 = 以该中心为圆心、
`radius` 环的**切比雪夫方形**（`|dx| ≤ r && |dz| ≤ r`；1 环 = 1 区块 = 16 m），半径由游戏
`SubsystemSky.VisibilityRange / 16` 推出（`TerrainSyncRange.RadiusForVisibility`，夹 1..32）。
地形下发、编辑距离校验、实体同步、投射物命中、纯表现事件全部用**这一份**窗口，不存在第二套区间。

实现端口：`Modules/WindowBroadcastPorts.cs` 的 `IWindowBroadcastPort`（实现方 =
`Modules/TerrainSyncModule.cs`，它是窗口数据的所有者）：

| 方法 | 语义 |
|---|---|
| `BroadcastToPlayersAt(type, payload, position, chunkMargin, out candidates)` | 同一份**已编码** payload 并发入队给"窗口外扩 `chunkMargin` 环包含该世界坐标"的在线玩家端；一次编码、N 个连接并发入队 |
| `BroadcastToPlayersAtAvatar(type, payload, avatarClientId, chunkMargin, ...)` | 同上，位置取该玩家端**化身实体**的 `ComponentBody.Position`；该化身自己的端**永远包含**（不依赖它是否已上报窗口）；`avatarClientId = 0` = 主机本机玩家（位置取本机玩家口径，只发窗口内观察者） |
| `CaptureClientWindows(target)` + `SendToClient(clientId, ...)` | 需要**逐端过滤消息内容**时枚举"有窗口的端"再逐端发送（例如一批烧尽格子按每个端自己的窗口裁剪） |

窗口未知（端还没上报 `INTEREST_UPDATE`，例如正在下载世界）的端**不会**被当成"要发给它的"：
它收不到观察者数据，只发它自己的状态（若有），并计入 `windowUnknown` 类计数。
**任何路径都不允许回退到"发给所有人"** —— 跳过一次就计数 + 限流日志。

| 消息 | 收件人规则 | 额外余量 |
|---|---|---|
| `PLAYER_POSITIONS = 18` | 每个端一份**按自己窗口裁剪**的批：自己的角色**永远**包含；其他角色仅当窗口包含其位置 | 外扩 **1 环**（16 m，进入可视范围的缓冲；被遮挡也照样同步） |
| `ENTITY_STATE = 38` / `ENTITY_SNAPSHOT = 39` | 每个**稳定用户**一份 mask（位序 = userid 派生的槽位，**不是 clientId**）；窗口进入某区块补该区块实体的 ADD、离开补 REMOVE；快照按该端窗口截取，`complete=true` = "你窗口内完整" | 0（窗口本身） |
| `EXPLOSION_EVENT = 34` | 爆炸点落在窗口内（含外扩）的端；纯表现 | **6 环 = 96 m**（视觉/听觉半径） |
| `PLAYER_DAMAGED = 37` | 只发**被伤害玩家自己**的端（客户端处理器无任何观察者可见表现；受伤 HUD / 音效走 `PLAYER_VITALS_REPORT`） | — |
| `BURN_AWAY_EFFECT = 43` | 逐端过滤批次里的格子：只发窗口包含的格子（过滤后重新编码） | 0 |
| `WEAPON_FIRE_EVENT = 54` | 开火点落在窗口内的端（客户端按声音衰减自己决定播不播 / 多响） | 0 |
| `CAMPFIRE_EXTINGUISH_EVENT = 67` | 营火格落在窗口内的端 | 0 |
| `PLAYER_VITALS_REPORT = 95` | 每个端一份过滤后的批：owner 条目**永远**包含；观察者条目仅窗口内 + 按距离分级降频（≤32 m 每拍、32–64 m 每 2 拍、>64 m 每 4 拍）；死亡 / 强制睡眠等关键边沿不降频 | 0 |
| `PLAYER_INVENTORY = 32` | 所有者端**永远**包含；观察者端仅窗口包含其化身位置时（客户端用它镜像远端化身）；主机本机玩家只发窗口内观察者 | 0 |
| `REMOTE_ACTION_SOUND = 68` | 已按 48 m 距离裁剪（见第 10 节） | 48 m |
| `WATER_SPLASH_EFFECT` / 拾取声音 / 环境音 / 活塞 / 生物 / 地形 | 已按窗口或固定距离裁剪 | — |

**明确不按窗口裁剪**（有状态一致性 / 身份 / 回执语义，裁剪会破坏正确性）：
聊天与语音、`WORLD_INFO = 26`、身份与皮肤（27/28/29/75/76/108）、容器 / 活塞终态等待
一致性消息（62/70/74/79/81）、各类请求回执、`COMBAT_RNG_SYNC = 99`、96/109、91/92。

状态行审计：每个模块的 `StatusLine()` 都会列出"窗口外裁剪 / 窗口未知 / 入队失败"计数
（例如实体复制的 `outOfWindow` / `windowUnknown`、位置的 `posOutOfWindow` /
`posWindowUnknown`），计数只增不清零，便于对照日志定位漏发。

---

## 12. 角色记录 hash 与重连 diff（不常变内容）

联机角色的记录（名字 / 性别 / 皮肤 / 衣服 / 背包 / 等级 …）只存在于世界目录的
`SuMultiplayerPlayers.xml`（角色实体被从 `Project.m_entities` 摘除，**永远不进 `Project.xml`**）。
主机导出世界时整个世界目录被复制进 pak，所以这份记录文件**跟着世界 pak 一起到加入端**。
重连时 pak 不重发，于是用"每角色一个 hash"把**不常变内容变了的角色**挑出来，只重发这些角色
（自定义皮肤资源这类大块数据），常变部分继续走各自的增量流。

### 12.1 口径

| 项 | 规则 |
|---|---|
| 粒度 | **每个角色记录独立一个 hash**（不是整表一个） |
| 算法 | FNV-1a 64，固定字段顺序 + `Version="rec1"` 前缀（改字段集合 = 改版本 = 旧 hash 自然失效） |
| 存放 | `SuMultiplayerPlayers.xml` 的 `<Player RecordHash="…">`（v9）；**绝不写进 `Project.xml`** |
| 载入校验 | 属性缺失（v8 及更早）/ 与内容重算不一致 → 按内容重算 + 记一行日志 + 计数 + 随下次落盘修正 |
| 落盘 | `Put()` 与 `SaveIfDirty()` 都重算，保证文件里的 hash 永远等于文件里的内容 |

**不常变（进 hash）**：名字、名字已确认、性别、皮肤名、皮肤 SHA-256、皮肤资源长度、
衣服 4 槽、背包槽位值/数量、活跃槽、手工制造槽（仅真正装着东西的）、等级、
初始物品标志、创造背包标志、创造分类/页。

**常变（不进 hash）**：生命 / 空气 / 食物 / 体力 / 睡眠 / 温度 / 目标温度 / 湿度 /
疾病时长 / 火焰时长 / 位置 / 身体朝向 / 视角 / 饱食度 / 游戏统计 / 成就账本。
这些要么每拍都在变（进 hash 会让"记录没变"永远不成立），要么由别的增量流负责。

### 12.2 线上字段（append-only）

| 消息 | 字段 | 方向 | 语义 |
|---|---|---|---|
| `PlayerProfile` | `uint64 record_hash = 8` | 主机 → 客户端 | 该角色的当前记录 hash（名册 / 资料广播都带）；0 = 旧主机 / 未知 |
| `Hello` | `repeated KnownRecordHash known_records = 11` | 客户端 → 主机 | 客户端**本地已有**的角色记录 hash：`record_key` + `hash`；空 = 首次加入 / 旧客户端 |

`record_key` 一律是 `PlayerIdentity.RecordKey(identity, name, slot)`（槽位 0 与旧键完全一致）。
客户端的数据源：它下载的 world pak 副本里的 `SuMultiplayerPlayers.xml`
（只读解析 `RecordHash` 属性，`SuMultiplayerPlayersXml.TryReadRecordHashes`），
再叠加本次进程内从名册 / 资料广播收到的 hash（更新的优先）。上限 64 条；空键 / hash=0 一律拒绝。

### 12.3 主机 diff 行为

端口 `IKnownRecordHashPort`（实现方 = `Modules/TerrainSyncModule.cs`，数据源 = 这次连接的 Hello）。
`Modules/PlayerSkinModule.cs` 的加入补发在**三条路径**上按 hash 跳过：

1. 主机自己的自定义皮肤 → 对端申报的 hash 与主机记录相同则跳过；
2. 房间里已有玩家的自定义皮肤 → 逐人比对，相同则跳过；
3. 加入者自己记录里的资源（回传它自己 + 二次广播给其他端）→ 逐端比对，相同则跳过。

跳过只发生在"对端明确申报且 hash 完全相等"时，计入 `BackfillSkippedUnchanged` 并写一行日志；
没申报（首次加入 / 旧客户端）/ hash 不同 / 记录没有 hash → **照旧全量重发**（绝不误跳过）。
旧主机不认 `known_records` → 客户端照旧上传 / 接收资源；旧客户端不带 `known_records`
→ 主机全量补发。两个方向的旧构建行为都与改动前完全一致。

---

## 13. 检查清单（提 PR 前）

- [ ] `.proto` 改了 → `-p:RegenerateProtocol=true` 并提交 `Protocol/Generated/`。
- [ ] 新 id 在正确区间；没有复用已删除的 id。
- [ ] 新消息有 `bytes ext = 100;`（以及需要时的 `Any custom = 101;`）。
- [ ] 处理器只做有界工作；大数组走 `repeated` 且受既有预算约束。
- [ ] 发送失败（`Send` 返回 false）有计数 / 限流日志，不静默。
- [ ] 第三方模块在 `Detach` 里 `UnregisterOwner(ModuleId)`（或由宿主代做）。
- [ ] 文件行尾 CRLF（仓库根 `.gitattributes` 是 `* -text`）。
