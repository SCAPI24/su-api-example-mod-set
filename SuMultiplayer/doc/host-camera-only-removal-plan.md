# 主机端 Camera-only / 第三分屏功能移除计划

## 目的与范围

本文记录已确认的移除范围和验收边界，避免分阶段删除时遗漏依赖。实现删除已按本文开始推进；旧存档数据保持原样。

要从 SuMultiplayer 移除主机端为了调试客户端画面而增加的所有 Camera-only / 客户端角色分屏路线：

1. Camera-only 临时 `PlayerData` + 第 3 个 `GameWidget`，让主机额外看到客户端视角。
2. 将已有主机本地角色 H3 绑定为客户端视角的 replace / copy 镜像。
3. 为每个客户端创建真实的主机本地角色，再将该角色的分屏绑定为客户端视角。
4. 上述路线共用的 Camera-only 创建配方、相机绑定、第三屏角色反馈、第三屏 HUD / 死亡 / 睡眠表现和客户端 UI 镜像。

移除后的行为边界：主机不因客户端加入而创建、收编、重绑定或修改任何主机本地分屏角色；不再额外创建客户端视角分屏；不再把网络化身映射成主机本地角色。

## 必须保留

- 客户端在主机世界中的网络化身。保留 `PlayerAvatarModule`、`NetworkAvatarSpawner`、角色记录与外观/动作/位置/背包/生命等现有同步路径。主机仍需看见、模拟和交互处理远端客户端角色。
- 客户端自己的本地角色，以及客户端收到并显示的主机角色。
- `NetworkAvatarSpawner` 对远端角色的建造、销毁、位置和地形更新位置管理；其被多种同步模块使用，不能因为 Camera-only 路线也引用它而删除。
- `SuSubsystemPlayers` 和 `Game.SubsystemPlayers` 注入。该子类除旧 MirrorHost 自动角色标记外，还承担下载世界的本机出生点预置/加载期定位；只移除 MirrorHost 专用注册表行为，不删除整个子类或 Injector 注册。
- `PlayerVitalsModule` / `PlayerVitalsReplica` 对真正网络角色和本机角色的主机权威生命、体征、死亡、复活、睡眠、受击及本机 HUD 行为。
- 现有 `position.proto`、角色、动作、背包、生命体征等网络协议。它们服务真实网络化身，不是第三屏显示协议。
- `Terrain/TerrainSync.cs` 里的 `ClientView` 是地形兴趣范围/同步视图数据类型，与 `Engine/ClientViewEngine.cs` 的第三屏功能无关，必须保留。

## 三条分屏路线及主机接线

| 路线 | 当前实现 | 要移除的职责 |
|---|---|---|
| Debug Camera-only | `Engine/DebugMirrorClient.cs`、`Modules/DebugMirrorPorts.cs`、`UI/DebugMirrorHud.cs` | 临时 PlayerData / GameWidget、目标选择控制器、MoreContents 的 MIR 按钮与计数 |
| 自动客户端分屏 | `Engine/ClientViewEngine.cs`、`Modules/ClientViewPorts.cs` | 每客户端 camera-only 临时 PlayerData / GameWidget、槽位账本、候选与生命周期控制 |
| H3 replace/copy + MirrorHost | `Engine/MirrorCharacterEngine.cs`、`Modules/DebugMirrorCharacterPorts.cs`、`Engine/MirrorHostEngine.cs`、`Modules/MirrorHostPorts.cs` | H3 状态复制或相机绑定；MirrorHost 创建/收编本地角色并直接调用 `MirrorCharacterEngine`；相机/身体/HUD恢复、免疫和自动角色维护 |

`Plug/SuMultiplayerMod.cs` 中这三条路线相互仲裁并共用引擎：DebugMirror 组合根接线、`MirrorCharacterEngine` 实例、`IViewCameraModePort` 注册、`MirrorHostEngine(…, m_mirrorCharacterEngine, …)`、ClientView 的让位判定、周期 Tick、状态行、世界配置刷新、世界卸载/Mod 卸载清理以及 `TickMirrorHost` 都要整体清除。不要仅删除类文件而留下组合根或生命周期残引用。

`Engine/DebugMirrorWorldSettings.cs` 只承载这些世界级镜像/分屏开关：`debugmirrorclient`、`mirrorcharacter*`、`mirrorcharacterauto`、`mirrorhostnameprefix`、`clientview*`。这些运行时配置读取与应用调用均移除；已有世界目录中的 `SuMultiplayerDebug.json` 不属于仓库文件，移除代码后不再读取，也不应改动玩家世界文件。

## 第三屏 UI / 表现支线

界面状态传输和主机分屏呈现作为一条功能链移除：

- `Engine/ClientUiStateReader.cs`：采集客户端输入形式、HUD、屏幕/对话框/模态面板、聊天输入、消息/覆盖层、手工制作格和相机模式。
- `Modules/UiStateSyncModule.cs`：`ui-state` 模块、`CLIENT_UI_STATE` 的发送/接收、revision、重试、清理、统计及相机模式转发。
- `Engine/UiMirrorEngine.cs`、`Engine/UiMirrorCrafting.cs`、`Engine/UiMirrorMessageWidget.cs`：主机分屏的 HUD/输入开关、UI 面板/对话框、容器和手工制作显示、小消息、覆盖文字、按钮状态以及引擎私有 MessageWidget 访问。
- `Net/UiStateNet.cs`、`Net/UiMirrorPresentation.cs`、`Modules/UiStatePorts.cs`：以上 UI 镜像专用的数据模型、差异/编解码/验证/策略、纯呈现计划与端口。
- `Plug/SuMultiplayerMod.cs` 中 `m_uiStateModule`、`m_uiStateReader`、`m_uiMirrorEngine` 的字段、构造/注册、初始与周期状态日志、卸载 Detach/清空。
- `Modules/BlockUseSyncModule.cs` 仅移除 `IUiContainerCellPort` 实现和 `TryGetLocalContainerCell` / `TryGetClientContainerCell` 两个 UI 镜像适配器。`m_openKey`、`m_openContainers` 及它们的开关/编辑/库存同步逻辑必须保留；这些记录被容器同步本身使用。

`UiMirrorMessageWidget.cs:164` 所在代码是 `CLIENT_UI_STATE` 的消息列表适配器，不是网络角色创建器；它随整条 UI 镜像链移除。

## 第三屏死亡、HUD、睡眠与受击反馈

这些反馈依附于 H3/第三屏的显示目标，不属于真实网络角色状态同步：

- 移除 `Engine/MirrorViewFeedback.cs`、`Engine/MirrorViewFeedbackPolicy.cs`、`Engine/MirrorBottomHudPolicy.cs`：第三屏血条/体征/冰冻覆盖、红黑屏、死亡大消息、特效观察、底部条和蹲伏显示策略。
- `Engine/MirrorCharacterEngine.cs` 被整体移除后，随之移除其 `MirrorViewFeedback`、视角模式、第三屏身体隐藏/恢复、HUD桥接和绑定快照。
- `Engine/PlayerVitalsReplica.cs` 中只服务 H3 第三屏的 `MirrorSleepState`、`m_mirrorSleep`/缓冲集合、`RefreshMirrorSleepPresentation`、`GetOrCreateMirrorSleep`、`ClearMirrorSleep` 和对应计数/状态行字段移除。
- `Modules/PlayerVitalsModule.cs` 中 `TickHostMirrorSleep` 调用、方法、专用累计计数和日志移除；客户端本机 `RefreshSleepPresentation`、主机权威睡眠请求/状态同步、真实化身睡眠处理保留。
- `Modules/PlayerSleepPorts.cs` 继续保留 `IPlayerSleepPort` 的本机睡眠/唤醒接口和共享表现计数，但删除 `RefreshMirrorSleepPresentation` 端口方法及 `MirrorApplied` / `MirrorCleared` 专用计数成员。
- `PlayerVitalsReplica` 中本机睡眠与第三屏共用的睡眠表现写入器不能整段删除；移除第三屏调用和状态后，确认本机调用仍完整。
- `TryGetAuthorityFreeze`、`ViewFreezeProbe` 及仅供第三屏读取冻结值的缓存/诊断入口属于第三屏反馈支线。删除这些查询边；但 `CaptureOne` 计算并装入 `PlayerVitalsSnapshot.FreezeProgress/FreezeStage`、`vitals.proto` 字段 22/23、客户端插值及本机冰冻 HUD 必须保留，因为它们服务正常生命体征同步。

`MirrorViewFeedback.cs` 中对 UiMirrorEngine 隐藏 HUD 的注释和死亡文字定位代码与第三屏绑定流程一并清理。`PlayerVitalsReplica` 内本机死亡屏、权威死亡/复活以及真实客户端本机 HUD 的处理不是第三屏反馈，不要误删。

## SuSubsystemPlayers 与旧存档

`Engine/SuSubsystemPlayers.cs` 不能删除：它还负责下载世界的本机出生点预置。移除 MirrorHost 专用的 `AutoHost<index>` / `Host<index>` 注册表 API 和相关注释，但不扫描、不清理、不迁移、不改写已有世界存档中的任何 `PlayerData` 或注册表值。旧数据保留原样，代码删掉即可。

保留 SuSubsystemPlayers 的客户端出生点行为、子类替换与 Injector 注册。删除标记 API 不得改动 `PlayersData`，也不得调用 `RemovePlayerData`。

## 协议与兼容性

- 移除 `Protocol/uistate.proto` 中 `ClientUiState` / `UiSmallMessage` 全部消息和字段；相应生成物 `Protocol/Generated/Uistate.cs` 随协议重新生成后移除。
- 移除 `Protocol/protocol.proto` 里 `CLIENT_UI_STATE = 100` 的有效消息定义与说明。协议号 100 已经发布过，必须作为退役编号保留、禁止复用；同步更新 101/102 的“100 是 CLIENT_UI_STATE”历史说明。
- 移除 `Net/MultiplayerModCompatibility.cs` 的 `UistateReflection.Descriptor` 条目，并更新协议清单/哈希描述。Mod 的严格版本/构建兼容校验仍保留；协议哈希因描述符集合改变而更新。
- `Protocol/Generated/Protocol.cs` 由 `.proto` 重新生成，不手工保留已移除的消息枚举成员；生成结果必须与保留的退役编号规则一致。
- 搜索确认不存在其他第三屏或相机绑定专用网络字段。不要移除角色位置、姿态、生命体征等远端化身同步协议。

## 文档与代码引用

- 删除功能专用的 `doc/MIRROR-CHARACTER.md`，或将其内容归档替换为本移除记录；不得继续把 H3/第三屏描述成现役功能。
- 更新 `doc/message-protocol.md` 中 `uistate.proto`、消息号 100 和“当前最大协议号”说明。
- 检查 `doc/combat-and-death.md` 以及所有源码注释/状态日志中关于 H3、第三屏死亡消息、HUD、睡眠镜像、相机模式、自动 MirrorHost 和 camera-only 的描述，只删改依附于废弃功能的部分。
- 清理 `Plug/SuMultiplayerMod.cs` 中的字段、日志、Tick步骤、错误步骤名、卸载清理和世界调试配置方法。
- 全仓搜索时区分 `Terrain/TerrainSync.cs` 的 `ClientView` 兴趣区域类型，以及其他正常“远端角色/客户端视图”描述；不能按字符串名粗暴删除。

## 实施顺序与验收

1. 记录现有引用清单；不对旧世界存档做迁移或清理。
2. 从组合根去掉三条分屏路线的构造、注册、优先级仲裁、Tick、状态日志、世界配置刷新及 Dispose/卸载顺序。
3. 移除 DebugMirror、ClientViewEngine、MirrorCharacter、MirrorHost、MirrorHostImmunity 和第三屏反馈实现；在 PlayerVitals/PlayerSleep 共享文件中只切除第三屏专属成员。
4. 移除 CLIENT_UI_STATE/UI mirror 实现、端口、容器适配器和协议描述符；保留真实容器同步与所有网络化身路径。
5. 从 `SuSubsystemPlayers` 移除旧自动宿主标记 API，保留下载出生点代码；更新协议生成物、协议兼容哈希清单和文档。
6. 全仓引用搜索和 .NET 10 Release 构建；确认没有第三屏创建/相机绑定入口、过期状态日志、悬空接口或协议类型。
7. 回归验证：主机本地角色数量/PlayerIndex/分屏布局不因远端加入离开而变化；客户端加入后主机仍能看见网络化身；角色/地形/容器/电路/生命体征等现有同步继续工作；下载世界出生点预置继续工作；既有世界存档不被扫描或修改。

## 当前状态

代码移除已完成并暂存。按用户要求，既有存档中的旧 `PlayerData` 和 MirrorHost 标记均保留原样；没有扫描、清理、迁移或改写。Mod 的 .NET 10 Release 全量 Rebuild 和 protobuf 生成通过，WorldTransferRegression 通过。TsVerify 受其 net8.0 目标与当前仅支持 net10.0 的 Engine 包不兼容所阻塞；LmVerify 因工程缺少 Mod/引擎/Protobuf 引用而失败。未部署、未提交。
