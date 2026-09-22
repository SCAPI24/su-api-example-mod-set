# GM 工具（GmMod）

> 本文件随 `.scmod` 一起分发（打包在 `Content/doc/GM-MOD.md`），
> 拿到 mod 的人不需要访问仓库也能看到接口契约。

## 一、它是什么（纯客户端 UI）

一个**独立 mod**，只做三件事：加一个 HUD 按钮、给菜单、把"要改什么"提交给主机。

- 进入世界后，屏幕**右上角三个点（`MoreButton`）那一排**会多出一个 `GM` 按钮；
- 点击打开菜单：
  1. **季节 / 时段切换**：四季 × 初/仲/晚 = **12 档**（例：初冬 = `TimeOfYear 0.525`）；
  2. **立刻回到复活点**：把角色安全传送到**睡觉点**（`PlayerData.SpawnPosition`）。

**它不做落地、不做同步、不做分发**，因此：

- ✅ **主机端不需要安装 GmMod**（主机只负责审批 + 落地 + 分发，全部由联机 mod 完成）；
- ✅ 客户端之间的一致性由**主机广播**保证，不靠任何 mod 自行同步。

## 二、授权模型：复用联机 mod 的数据修改（DM）通道

| 环节 | 接口 | 说明 |
|---|---|---|
| 客户端提交 | `DataModificationTool.SubmitFast("GmMod", operation, payload)` | 可靠（TCP）通道；立即返回 `Accepted`（已排队给主机） |
| 主机批准 | `ScMultiplayerSettings.DataModificationMode` | `Reject` 直接拒 / **`Default` 弹窗问主机** / **`Allow` 默认同意** |
| 主机落地 | **联机 mod 自己**（无需任何第三方 mod） | `ScMP.Data.WorldSettings` 是联机 mod 的**通用**数据修改：按字段名写 `WorldSettings` |
| 主机分发 | 世界信息广播（2Hz，主机→全端） | 主机把整份 `WorldSettings` 快照发给所有客户端，客户端各自应用 |
| 结果回执 | `DataModificationTool.ResultReceived` | 主机批准并生效后回到发起端（本 mod 用它冒泡提示） |

**受信任客户端**：主机审批弹窗有三个选项 —— `Allow` / `Reject` / **`Always allow this player`**。
选第三项会把该客户端的**身份**（`UserManager.ActiveUser.UniqueId` = 角色记录键）写进主机世界目录下的
`ScMultiplayerTrustedClients.xml`，以后该身份的请求**自动同意**，不再弹窗。

**客户端反馈只有底部冒泡（`ComponentGui.DisplaySmallMessage`），不弹对话框**：
提交后 `已发送主机：初冬 (Early Winter)（等待主机同意）`，回执到达后
`季节/时段：主机已同意并生效` / `主机拒绝了该操作` / …（主机那边的审批弹窗是联机 mod 自己弹的）。

## 三、GM 操作契约

### 1) `ScMP.Data.WorldSettings`（联机 mod 的通用数据修改，不是内置 `ScMP.Player.*`）

- 载荷：**纯文本，每行 `字段名<TAB>值`**（UTF-8）。例：`TimeOfYear\t0.525`
  ⚠ 不要用 JSON：本 mod 与联机 mod 的 DLL 都会被 Obfuscar 改名，JSON 依赖属性名会**静默解析成默认值**
  （实测把季节写成了夏至）；纯文本是双方约定的字面格式，不受混淆影响。
- 取值：`WorldSettings` 的**任意简单字段**都能改（float / int / bool / string / Vector2 / enum），
  与引擎读 Project.xml 的口径一致；写进去后主机立刻生效（`SubsystemSeasons.Update` 每帧读 `TimeOfYear`）。
- 季节/日期就是 `TimeOfYear`：夏 `0.00`、秋 `0.25`、冬 `0.50`、春 `0.75`（每季跨度 `0.25`）。

### 2) `ScMP.Player.SafeRespawnRelocate`（联机 mod 内置，本 mod 直接调用）

- 载荷（JSON，由联机 mod 自己编解码）：`{"TargetClientId": -1}`（`-1` = 自己）
- 主机执行：把玩家安全传送到 `PlayerData.SpawnPosition`（睡觉点）；骑乘状态会拒绝

## 四、依赖要求

- 需要 **ScMultiplayer 2.1.5+**（`ModInfo.xml` 里声明了依赖）。
- 联机 mod 的 `Obfuscar.xml` 必须保留 GM 门面类型/成员名字
  （`SkipType` + `skipMethods/skipFields/skipProperties/skipEvents="true"`），
  否则混淆后 GmMod 在运行时找不到 `DataModificationTool` 等类型。
