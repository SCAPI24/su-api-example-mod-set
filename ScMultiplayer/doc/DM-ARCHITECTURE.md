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

## 内置角色操作

七个内置操作只允许走 Fast 通道，payload 使用 `PlayerDataModificationCodec`。`ScMP.Player.*` 命名空间由 ScMultiplayer 保留，不进入第三方 `Apply` 事件，因此其它 Mod 不能在主机校验失败后用同名处理器绕过拒绝结果。

| 操作 | 请求字段 | 主机执行结果 |
|------|----------|--------------|
| `ScMP.Player.SetRespawnAnchor` | `X/Y/Z`，全零时使用角色当前位置 | 主机在已加载地形中调用原版 `FindNoIntroSpawnPosition`，只更新安全复活点 |
| `ScMP.Player.ReturnToPlayer` | `DestinationClientId`，可选 `OffsetX/Y/Z` | 主机在目标玩家附近选择安全位置并传送申请者；乘骑状态下拒绝 |
| `ScMP.Player.GrantInventory` | `ItemValue`、`ItemCount` | 主机先计算完整容量，容量不足时不添加任何物品；成功后走现有装备增量同步 |
| `ScMP.Player.RestoreLevel` | `SetAbsoluteLevel=true` + `Level`，或 `Amount` | 主机限制有限正数范围，修改权威等级并发送健康/等级状态 |
| `ScMP.Player.Heal` | `Amount`，范围 `(0, 1]` | 只对存活角色调用原版 `ComponentHealth.Heal`，随后发送权威健康状态 |
| `ScMP.Player.SafeRespawnRelocate` | 可选 `OffsetX/Y/Z` | 主机从现有复活点附近查找安全位置，同时修改复活点和当前角色位置 |
| `ScMP.Player.Seal` | `TargetClientId`、可选 `Radius/Height` | 仅主机本地申请；主机在已加载地形用基岩生成有界外壳并执行邻近更新 |

复活点、位置、背包、等级和生命修改成功后，远程角色会立即更新 `ScMultiplayerPlayers.xml`；主机本地角色走原版项目保存。传送结果使用独立可靠权威消息送达拥有角色的客户端，并清理主机上该角色尚未执行的旧移动、瞄准、攻击、交互、丢弃和跳跃队列，避免旧输入把角色拉回。基岩封印通过 `SubsystemTerrain.ChangeCell` 写主机地形，并复用现有地形闭包和可见范围同步，不由客户端自行放置或转发。

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

无头服务器收到 `default` 模式请求时会在控制台输出一行审批提示。管理员从 `Multiplayer Hosting -> Data modification -> Pending approvals` 处理，或通过控制接口 `multiplayer.dm` 列出并提交决定；ScMultiplayer 保留审批队列和最终决定权，HeadlessRenderingMod 只负责显示和转发控制命令。

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
