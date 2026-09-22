# DM 权威数据调整通道

DM（Data Modification）是 ScMultiplayer 给其它 Mod 使用的可选数据调整通道。通用 DM 核心只负责身份、权限、传输、限流和主线程调度，不解释第三方业务数据，也不直接写客户端本地角色、地形或其它游戏对象。`ScMP.Player.*` 是单独分层的内置主机执行器：客户端仍然只提交申请，审批通过后仅由主机解析、校验并修改权威对象。

## 权限

服务器配置 `dataModificationMode` 有三档：

- `reject`：拒绝所有客户端 DM 请求，不显示审批提示。
- `default`：每个请求进入主机审批队列，一次只显示一条包含 Mod、操作、来源和数据规模的提示；主机选择同意后才进入应用处理器，选择拒绝或 120 秒未处理则终止请求。
- `allow`：不显示审批提示，直接允许请求进入主机目标 Mod 的应用处理器；应用处理器仍必须存在并明确返回 `DataModificationApplyResult.Success()`。

默认值为 `default`。没有安装对应主机 Mod、授权处理器或应用处理器时，不会发生任何游戏数据修改。

传输层身份永远以 Comms 的 `sourceClientId` 为准，不能由 payload 内的玩家编号覆盖。主机处理器必须使用 `SourceClientId` / `SourcePlayerIndex` 定位角色，并在自己的业务层再次校验距离、模式、权限、版本和数据守恒。

远程客户端只能申请修改自己的角色。payload 不填 `TargetClientId` 时，主机使用传输层 `sourceClientId`；远程客户端填写其它目标会被拒绝。主机本地 Mod 的 `SourceClientId` 为 `0`，可以指定在线目标；封印角色只接受主机本地申请。客户端提供的坐标和偏移只是提示，最终坐标由主机使用原版安全出生搜索计算。

## Mod API

客户端 Mod 可以调用：

```csharp
DataModificationTool.SubmitFast("MyMod", "set-value", payload);
DataModificationTool.SubmitBulk("MyMod", "terrain-batch", batches);
DataModificationTool.RequestPlayerModification(
    "MyMod",
    DataModificationOperationNames.HealPlayer,
    new PlayerDataModificationRequest { Amount = 0.5f });
```

主机 Mod 订阅 `ScMultiplayer.DataModification.Apply`：接收 `DataModificationApplyContext`，在主机游戏线程执行一个快速请求或一个大数据逻辑分片；返回 `DataModificationApplyResult.Success()` 才算应用成功。`default` 模式的人工审批只决定请求是否可以进入该处理器，不能代替处理器自身的业务校验。

结果通过 `ScMultiplayer.DataModification.Result` 和 `DataModificationTool.ResultReceived` 返回请求方。ScMultiplayer 不把 payload 转发给其它客户端；主机 Mod 修改权威对象后，由既有角色、地形、实体同步链将结果发送给可见客户端。

## 通用世界设置操作（联机 mod 自己落地，主机无需装第三方 mod）

`ScMP.Data.WorldSettings` 是**自定义 operation**（不属于内置 `ScMP.Player.*`），但**由 ScMultiplayer 自己
在主机侧落地**，因此主机端不需要安装任何第三方 mod（例如第三方 GM 工具）——主机只负责审批、落地与分发。

- 载荷：**纯文本，每行 `字段名<TAB>值`**（UTF-8），例：`TimeOfYear	0.525`。
  ⚠ 不要用 JSON：联机 mod 会被 Obfuscar 改名，JSON 依赖属性名会静默解析成默认值（实测把季节写成了夏至）。
- 可改字段：`WorldSettings` 的**任意简单字段**（float / int / bool / string / Vector2 / enum），
  与引擎读 Project.xml 的口径一致；联机 mod **不认识业务语义**（例如它不知道"季节"是什么，
  季节只是客户端通过 `TimeOfYear` 字段表达的一个值）。
- 落地：主机在游戏线程写 `SubsystemGameInfo.WorldSettings`，立刻生效。
- 分发：主机在既有的 2Hz 世界信息广播（`GameWorldInfoMessage1.WorldSettings`）里带上
  **整份世界设置快照**，所有客户端在 `ApplyRemoteWeatherState` 中自行应用 ——
  **分发完全在主机侧**，任何 mod 都不做同步。
- 载荷转发：仍然不转发 payload；审批弹窗会显示解析出的摘要（`DataModificationApprovalRequest.Summary`）。

## 通用世界控制操作（运行时天气 + 时间点）

`ScMP.Data.WorldControl` 与 `ScMP.Data.WorldSettings` / `ScMP.Data.Cells` 同级，同样是**联机 mod 自己
在主机侧落地**的自定义 operation（主机端不需要安装任何第三方 mod，例如 GM 工具）。它补的缺口是：
`WorldSettings` 里只有天气**总开关** `AreWeatherEffectsEnabled`，没有"开始降雨 / 起雾 / 闪电"这类运行时
状态；而引擎原生那几个按钮（`ComponentGui.Update` 里的 Precipitation / Fog / Lightning / TimeOfDay）
要求 `GameMode.Creative` 或 `WorldControl` 能力，并且完全绕过 DM 审批。

- 载荷：与 `ScMP.Data.WorldSettings` **同一套纯文本 `字段名<TAB>值`**（UTF-8，复用
  `WorldSettingsDataCodec`），同样不用 JSON。
- 字段与主机语义（`ScMultiplayerWorldControlModification.ApplyHostWorldControlModification`）：

  | 字段 | 取值 | 主机执行 |
  |------|------|----------|
  | `Precipitation` | `on` / `off` / `toggle` | `SubsystemWeather.ManualPrecipitationStart` / `ManualPrecipitationEnd` |
  | `Fog` | `on` / `off` / `toggle` | `SubsystemWeather.ManualFogStart` / `ManualFogEnd` |
  | `Lightning` | `strike` | `SubsystemWeather.ManualLightingStrike`，方向取**发起请求的客户端**的眼睛朝向，主机本地玩家兜底 |
  | `TimePoint` | `dawn` / `noon` / `dusk` / `midnight` | `TimeOfDayOffset += IntervalUtils.Interval(TimeOfDay, 目标)`，定点跳到该时间点 |
  | `TimeExact` | `0..1` | 同上，口径与 `SubsystemTimeOfDay.TimeOfDay` 一致 |

- 落地与分发：主机在游戏线程执行后调用 `SendGameWorldInfoMessage()`，复用**既有**的 2Hz 世界信息广播
  把降水 / 雾 / 时间偏移发给所有客户端 —— **没有新协议**，客户端仍不参与落地。
- 两个必须记住的行为（否则看起来"点了没反应"）：
  1. **"打开降雨 / 雾气"会顺带把 `WorldSettings.AreWeatherEffectsEnabled` 总开关打开**：客户端
     `SubsystemWeather.Update` 受总开关约束，总开关关着时降水与雾根本不渲染；摘要里会多一条
     `weatherEffects=on`。
  2. **跳时间点前会先把 `TimeOfDayMode` 切回 `Changing`**：引擎在非 `Changing` 档位把时间**固定**住，
     不切的话改 `TimeOfDayOffset` 看不到任何变化；这一步会体现在回执摘要里（`timeOfDayMode=Changing`）。

## 受信任客户端（自动同意）

审批弹窗有三个选项：`Allow` / `Reject` / **`Always allow this player`**。第三项会把该客户端的
**身份**（`UserManager.ActiveUser.UniqueId`，也就是角色记录键）写进世界目录下的
`ScMultiplayerTrustedClients.xml`；此后该身份的 DM 请求**自动同意**（不再弹窗，`default` 模式下同样生效）。
`reject` 档位仍然一律拒绝。

主机侧由此有三种"会被同意"的来源，无头服务器控制台可以在
`Multiplayer Hosting > Data modification > GM / data-modification authorisations [N]` 里直接看到前两种和在线身份：

| 来源 | 行为 |
|------|------|
| 世界受信任名单 `ScMultiplayerTrustedClients.xml` | **连审批请求都不产生**，主机直接落地 |
| 无头服务器 `server.json` 的 `autoApproveDataModificationUserIds` | 请求照常产生，只是由无头 mod 立刻 `allow`（日志里能看到 `[DM] Auto approve ...`） |
| `dataModificationMode = allow` | 所有请求一律同意 |

主机控制面事件 `ScMultiplayer.DataModification.ApprovalControl` 支持四个 operation：

| operation | 参数 | 返回 |
|-----------|------|------|
| `list`（缺省） | 无 | `pending`；主机侧另有 `trusted`（世界受信任名单）与 `clients`（在线客户端的 `clientId` / `key` / `name` / `trusted`） |
| `resolve` | `sourceClientId` / `requestId` / `transferId` / `allow` | `resolved` |
| `trust` | `identity`（优先）或 `sourceKey`，可选 `sourceClientId` | `resolved` / `trusted` / `identity` |
| `untrust` | 同上 | `resolved` / `trusted` / `identity` |

`trust` / `untrust` 分别走 `TrustDataModificationIdentity(string identity, int clientId = -1)` 与
`UntrustDataModificationIdentity(string identity)`，改的就是世界目录下的 `ScMultiplayerTrustedClients.xml`；
原来的 `TrustDataModificationClient(int clientId)` 保留，内部委托给身份版。身份优先取显式传入的
`identity`，其次取无头控制台待审批条目里的记录键 `sourceKey`（玩家可能已经离线），最后才回落到在线
客户端表。**内存集合才是真相源**（`IsTrustedDataModificationClient` 每次请求都查它），写盘只是持久化，
所以取消授权**立即生效**。

> **GM / 数据修改授权与"允许加入房间"是两套完全独立的开关。** 能不能进房间由
> `ScMultiplayerSettings.autoApproveJoinRequests`（无头控制台 `Multiplayer Hosting > Auto approve joins`）
> 决定；`trust` / `untrust` **只改数据修改（GM）权限**，绝不触碰任何加入相关设置。注意无头控制台授权页里
> 的 `server.json allowlist (autoApproveDataModificationUserIds)` 也是**数据修改**白名单，与加入白名单无关。

无头服务器控制台在这条链路上的行为（细节见 `Mod/HeadlessRenderingMod/README.md`）：授权页改名为
`GM / data-modification authorisations [N]`，可以逐条移除 `server.json` 白名单条目、也可以取消世界受信
（`operation=untrust`，按身份）；`Pending approvals` 的决策菜单新增
**「Always allow this player（授予 GM 权限）」**，动作是**先 `trust` 再 `resolve allow`**，
授权失败时该请求留在待审批里；`Recent decisions` 的父节点只显示计数，`last: …` 摘要移到子页顶部，
`ManualTrusted` / `ManualTrustFailed` 等授权记录一并进 `Recent decisions`。

主机对**远端客户端**的裁决（`Applied` / `Failed` / `Rejected` / `Busy` / `Invalid` / `NotSupported` /
`Cancelled`）过去只发给该客户端；现在 `SendResult` 在主机侧同时本地发布一次
（`PublishDataModificationResult`），使主机上的观察者（无头控制台、诊断）能看到结果与原因，消息内容不变。

## 内置角色操作

九个内置操作只允许走 Fast 通道，payload 使用 `PlayerDataModificationCodec`。`ScMP.Player.*` 命名空间由 ScMultiplayer 保留，不进入第三方 `Apply` 事件，因此其它 Mod 不能在主机校验失败后用同名处理器绕过拒绝结果。

| 操作 | 请求字段 | 主机执行结果 |
|------|----------|--------------|
| `ScMP.Player.SetRespawnAnchor` | `X/Y/Z`，全零时使用角色当前位置 | 主机在已加载地形中调用原版 `FindNoIntroSpawnPosition`，只更新安全复活点 |
| `ScMP.Player.ReturnToPlayer` | `DestinationClientId`，可选 `OffsetX/Y/Z` | 主机在目标玩家附近选择安全位置并传送申请者；乘骑状态下拒绝 |
| `ScMP.Player.GrantInventory` | `ItemValue`、`ItemCount` | 主机先计算完整容量，容量不足时不添加任何物品；成功后走现有装备增量同步 |
| `ScMP.Player.RestoreLevel` | `SetAbsoluteLevel=true` + `Level`，或 `Amount` | 主机限制有限正数范围，修改权威等级并发送健康/等级状态 |
| `ScMP.Player.Heal` | `Amount`，范围 `(0, 1]` | 只对存活角色调用原版 `ComponentHealth.Heal`，随后发送权威健康状态 |
| `ScMP.Player.SafeRespawnRelocate` | 可选 `OffsetX/Y/Z` | 主机从现有复活点附近查找安全位置，同时修改复活点和当前角色位置 |
| `ScMP.Player.Seal` | `TargetClientId`、可选 `Radius/Height` | 仅主机本地申请；主机在已加载地形用基岩生成有界外壳并执行邻近更新 |
| `ScMP.Player.SetVitals` | `Vitals` 位掩码（`Food=1` / `Stamina=2` / `Sleep=4` / `Temperature=8` / `Wetness=16`，**未置位 = 保持主机当前值**）+ `VitalsFood` / `VitalsStamina` / `VitalsSleep` / `VitalsTemperature` / `VitalsWetness` | 目标须存活；`food` / `stamina` / `sleep` / `wetness` 限 `0..1`，`temperature` 限 `0..24`（**12 = 舒适**，引擎口径）。主机复用 `ApplyAuthoritativePlayerStats` 写 `m_food` / `m_stamina` / `m_sleep` / `m_temperature` / `m_wetness` 与对应的 `m_last*`，随后 `SendAuthoritativePlayerHealth(force: true)` 权威下发 |
| `ScMP.Player.SetCondition` | `Condition` 位掩码（`Flu=1` / `Sickness=2`；`None` = 全部）、`ConditionMode`（`ConditionAction.Clear=0` / `Apply=1`）、`ConditionDuration`（秒，`0` = 引擎默认时长，上限 `3600`） | 施加走原版 `ComponentFlu.StartFlu()` / `ComponentSickness.StartSickness()`（给了秒数就写 `m_fluDuration` / `m_sicknessDuration`）；解除与 `ResetNetworkPlayerVitals` 同口径（流感 6 个字段清零；中毒含 `m_pukeParticleSystem` 置 `null` + 3 个字段清零），随后同样 `SendAuthoritativePlayerHealth(force: true)` |

复活点、位置、背包、等级、生命、生命体征和异常状态修改成功后，远程角色会立即更新 `ScMultiplayerPlayers.xml`；主机本地角色走原版项目保存。传送结果使用独立可靠权威消息送达拥有角色的客户端，并清理主机上该角色尚未执行的旧移动、瞄准、攻击、交互、丢弃和跳跃队列，避免旧输入把角色拉回。基岩封印通过 `SubsystemTerrain.ChangeCell` 写主机地形，并复用现有地形闭包和可见范围同步，不由客户端自行放置或转发。

## 两条通道

快速通道是一个小型可靠请求，单次 payload 上限 8 KiB，主机使用有界队列和每帧处理预算。

大数据通道采用 `BulkBegin -> BulkChunk -> BulkComplete`。每个 chunk 是一个必须可以独立应用的逻辑批次，单 chunk 上限 768 字节，整个传输上限 16 MiB。主机按 `dataModificationBulkApplyChunksPerFrame` 和 `dataModificationBulkApplyBytesPerFrame` 轮询各传输，每帧不超出预算；缓冲超过 2 MiB 或 30 秒未完成会被取消。分片使用可靠无序传输，避免占用其它可靠有序控制消息的队头。

客户端只在收到主机 `Accepted` 后发送 Bulk 分片。`Applied`、`Rejected`、`Busy`、`Invalid`、`NotSupported`、`Failed` 和 `Cancelled` 都是终态，客户端会立即释放对应传输；`Complete` 允许先于尚在途的可靠无序分片到达，主机仅在全部索引和总字节数校验完成后结束传输。

## 服务器设置

简易设置提供权限模式、快速通道数、大数据通道数和低/平衡/高三档帧预算。专业设置可以分别设置：

- `dataModificationFastMaxConcurrent`
- `dataModificationBulkMaxConcurrent`
- `dataModificationBulkApplyChunksPerFrame`
- `dataModificationBulkApplyBytesPerFrame`

GUI 主机从 MP 菜单进入 `Data Modification`；无头服务器从 `Multiplayer Hosting -> Data modification` 或 `multiplayer.settings` 控制。配置写入现有 `data:/ScMultiplayerSettings.json`，缺失字段按默认值迁移。

无头服务器收到 `default` 模式请求时会在控制台输出一行审批提示。管理员从 `Multiplayer Hosting -> Data modification -> Pending approvals` 处理（决策菜单里除 `Allow` / `Reject` 外还有 **「Always allow this player（授予 GM 权限）」**，动作是先 `trust` 再 `resolve allow`），或通过控制接口 `multiplayer.dm` 列出并提交决定；ScMultiplayer 保留审批队列和最终决定权，HeadlessRenderingMod 只负责显示和转发控制命令。

## 线程和性能边界

网络线程只解码并进入 ScMultiplayer 的 DM 队列；第三方 Mod 的授权和应用处理器只在游戏线程的独立 DM 阶段运行。关闭或拒绝时不构造业务应用对象，不启动额外线程，不写本地数据，也不触碰客户端本地世界状态。
## Per-client temporary capabilities (2.1.3)

The host keeps a session-only capability map keyed by transport `clientId`. It does not modify
the world's `GameMode`, and it does not persist grants in `ScMultiplayerPlayers.xml`.

Supported flags are `CreativeFly`, `WorldControl`, and `CreativeInventory`. A client submits
`ScMP.Player.RequestCapabilities` through the existing DM fast channel. A host-local tool may
target an online client; a remote client may target only itself. `ScMP.Player.RevokeCapabilities`
uses the same host validation path.

The existing `reject/default/allow` policy controls these requests. In `default`, the host sees
the normal DM approval prompt. After approval, ScMultiplayer sends a reliable directed
`PlayerCapabilityMessage` with a monotonic sequence. Clients accept only newer messages from the
host and cannot publish capability state themselves.

`CreativeFly` is enforced at the host input boundary. `WorldControl` reuses the existing
host-authoritative weather/time execution and is allowed in a non-creative world only for an
authorized source. The world change remains visible to every player, but the permission is per
client. `CreativeInventory` switches only the target entity's `ComponentMiner.Inventory` to the
already-present native `ComponentCreativeInventory`; revocation restores the original inventory.
The `9999` native count remains an infinite-count sentinel and is never treated as a physical drop
quantity. All capability state is cleared on leave, reconnect, project reset, or mod disposal.
