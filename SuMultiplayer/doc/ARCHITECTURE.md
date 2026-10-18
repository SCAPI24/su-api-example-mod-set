# SuMultiplayer 骨架说明

> 本文件只说明**当前骨架有什么、什么还没定**。架构细节与实现方案待重写时再确定，
> 这里不做预设，避免把未经验证的设计固化成约束。

## 1. 当前状态

骨架 + 第一个独立组件，均通过 `dotnet build`（0 错误 0 警告）：

| 文件 | 作用 |
|---|---|
| `ModInfo.xml` | Mod 标识 `SuMultiplayer`，版本 `0.1.0`，`IsMergeLib=true`，扁平写法 |
| `SuMultiplayer.csproj` | `net8.0`；`Engine` / `EntitySystem` / `Survivalcraft` / `SuAPICore` 走 NuGet 制品源 |
| `Plug/SuMultiplayerMod.cs` | `IMod` 入口：`OnLoad` / `OnUnload` 各一行日志，无业务逻辑 |
| `Journal/*.cs` | **已实现**：状态变化队列（环形内存队列 + 分段落盘 + 分类筛选 + 崩溃恢复） |

`Journal/` 不依赖引擎（纯 .NET），因此可独立测试：设计、可播放演示与实测数据见
[`state-journal.html`](state-journal.html)。

未引入 Comms / SuEventBus 依赖（接入时再补 csproj 引用与 `ModInfo.xml` 的 `Dependencies`，两处必须一致）。

## 2. 待重写（全部细节）

- 网络传输与会话生命周期；
- 加入流程与世界传输；
- 玩家、地形、实体、电路、容器、世界控制的同步实现；
- 模块划分、目录结构与接口形态（`Journal/` 的接线方式见 `WIR01`）；
- UI / 操作入口；
- 数据调整通道与领地等扩展能力。

## 3. 重写时必须对齐的东西

重写的自由度在**实现**，不在**行为契约**。以下内容以旧实现为准（解析见
[`multiplayer-architecture.html`](multiplayer-architecture.html)）：

- 协议：消息 wire ID、字段顺序、修订号、投递语义（`PRT04`、`MAP02`）；
- 权威模型：客户端只提交意图，世界结果由主机裁决（`AUT01`、`AUT05`）；
- 时序不变量：可靠/可替换通道分工、请求幂等、地形可收敛（`PRT07`、`AUT03`、`SYN03`）；
- 工程硬约束：三端同包（`SEC01`）、存档纯净（`SEC03`）、混淆保留（`SEC02`）、
  不改原版源码（`SEC06`）。

## 4. 讨论移植的坐标

`multiplayer-architecture.html` 里的每个 GUID（3 位域字母 + 2 位序号，如 `FLW04`）
既是页面锚点，也是讨论坐标：说「按 `FLW04` 重写」即可精确指向世界下载那一段。
完整索引在该文档的 `IDX01`。
