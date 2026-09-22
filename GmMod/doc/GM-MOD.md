# GM 工具（GmMod）

> 本文件随 `.scmod` 一起分发（打包在 `Content/doc/GM-MOD.md`），
> 拿到 mod 的人不需要访问仓库也能看到接口契约。

## 一、它是什么（纯客户端 UI）

一个**独立 mod**，只做三件事：加一个 HUD 按钮、给菜单、把"要改什么"提交给主机。

- 进入世界后，屏幕**右上角三个点（`MoreButton`）那一排**会多出一个 `GM` 按钮；
- 点击打开菜单：
  1. **季节 / 时段切换**：四季 × 初/仲/晚 = **12 档**（例：初冬 = `TimeOfYear 0.525`）；
  2. **昼夜**：白天 / 日出 / 日落 / 夜晚 / 循环（**加入后一片漆黑时用它定格到白天**）；
  3. **天气**：天气效果 开 / 关（关掉后**立刻**停雨、停雪、散雾），以及**运行时天气** ——
     降雨 开 / 关、雾气 开 / 关、闪电立刻劈一次；
  4. **世界控制：时间点**：黎明 / 正午 / 黄昏 / 午夜 / 精确 `0..1`（主机定点跳，再广播给全端）；
  5. **玩家操作（所有玩家）**：等级 / 回满血 / 送回睡觉点，以及新增的
     **设置复活点**（该角色当前位置 / 我的当前位置 / 手动输入 `x,y,z`）、
     **饱食度**（`0` / `0.25` / `0.5` / `0.75` / `1` + 手动输入 `0..1`）、
     **体温**（舒适 `12` / 偏冷 `6` / 严寒 `0` / 酷热 `24` + 手动输入 `0..24`）、
     **异常状态**（施加 / 解除流感、施加 / 解除中毒、解除全部、施加流感自定义秒数）；
  6. **立刻回到复活点**：把角色安全传送到**睡觉点**（`PlayerData.SpawnPosition`）。

**它不做落地、不做同步、不做分发**，因此：

- ✅ **主机端不需要安装 GmMod**（主机只负责审批 + 落地 + 分发，全部由联机 mod 完成）；
- ✅ 客户端之间的一致性由**主机广播**保证，不靠任何 mod 自行同步。

**GM 按钮只会有一个**：死亡复活会重建角色实体，旧组件在 `Dispose()` 之后同一步还会被 `Update`
一次（`SubsystemUpdate` 的更新列表是"本步快照、下一步才摘除"），只靠"按钮字段为空"判重会再挂一个
—— 这正是"复活后 `MoreContents` 里冒出两个 GM 按钮"的成因。现在按**父面板里名为 `GmModButton`
的按钮**判重（命中即接管，并顺手清掉多余的同名 / 同文案按钮），再加上实例 `m_disposed` 守卫。

## 二、授权模型：复用联机 mod 的数据修改（DM）通道

| 环节 | 接口 | 说明 |
|---|---|---|
| 客户端提交 | `DataModificationTool.SubmitFast("GmMod", operation, payload)` | 可靠（TCP）通道；立即返回 `Accepted`（已排队给主机） |
| 主机批准 | `ScMultiplayerSettings.DataModificationMode` | `Reject` 直接拒 / **`Default` 弹窗问主机** / **`Allow` 默认同意** |
| 主机落地 | **联机 mod 自己**（无需任何第三方 mod） | `ScMP.Data.WorldSettings` 是联机 mod 的**通用**数据修改：按字段名写 `WorldSettings` |
| 主机落地（运行时天气 / 时间点） | `ScMP.Data.WorldControl` | 与 `ScMP.Data.WorldSettings` 同级、**同一套载荷**；主机改的是 `SubsystemWeather` / `SubsystemTimeOfDay`，不是 `WorldSettings` 字段 |
| 主机分发 | 世界信息广播（2Hz，主机→全端） | 主机把整份 `WorldSettings` 快照（以及运行时降水 / 雾 / 时间偏移）发给所有客户端，客户端各自应用 |
| 结果回执 | `DataModificationTool.ResultReceived` | 主机批准并生效后回到发起端（本 mod 用它冒泡提示） |

**受信任客户端**：主机审批弹窗有三个选项 —— `Allow` / `Reject` / **`Always allow this player`**。
选第三项会把该客户端的**身份**（`UserManager.ActiveUser.UniqueId` = 角色记录键）写进主机世界目录下的
`ScMultiplayerTrustedClients.xml`，以后该身份的请求**自动同意**，不再弹窗。

**客户端反馈只有底部冒泡（`ComponentGui.DisplaySmallMessage`），不弹对话框**：
提交后 `已发送主机：初冬 (Early Winter)（等待主机同意）`，回执到达后
`世界设置：主机已同意并生效` / `主机拒绝了该操作` / …（主机那边的审批弹窗是联机 mod 自己弹的）。
回执文案由 `GmMod.BuildResultToast` 生成：只有 `ScMP.Data.WorldSettings`（显示成"世界设置"）与
`ScMP.Player.SafeRespawnRelocate`（"回到复活点"）有中文别名，其余玩家操作直接显示 operation 名
（例：`ScMP.Player.SetVitals：主机已同意并生效`）；失败原因不塞进冒泡，去 `Logs/Game.log` 看。

## 三、GM 操作契约

### 1) `ScMP.Data.WorldSettings`（联机 mod 的通用数据修改，不是内置 `ScMP.Player.*`）

- 载荷：**纯文本，每行 `字段名<TAB>值`**（UTF-8）。例：`TimeOfYear\t0.525`
  ⚠ 不要用 JSON：本 mod 与联机 mod 的 DLL 都会被 Obfuscar 改名，JSON 依赖属性名会**静默解析成默认值**
  （实测把季节写成了夏至）；纯文本是双方约定的字面格式，不受混淆影响。
- 取值：`WorldSettings` 的**任意简单字段**都能改（float / int / bool / string / Vector2 / enum），
  与引擎读 Project.xml 的口径一致；写进去后主机立刻生效（`SubsystemSeasons.Update` 每帧读 `TimeOfYear`）。
- 季节/日期就是 `TimeOfYear`：夏 `0.00`、秋 `0.25`、冬 `0.50`、春 `0.75`（每季跨度 `0.25`）。
- **昼夜**就是 `TimeOfDayMode`（枚举名：`Changing` / `Day` / `Night` / `Sunrise` / `Sunset`）。
  `SubsystemTimeOfDay.Update` **每帧**读它，所以改成非 `Changing` 会**立刻**定格亮度：
  - 加入后看不到东西、只听见下雨 → 先设 `Day`（白天），再按需关天气；
  - `Changing` 恢复正常昼夜流逝。
- **天气**就是 `AreWeatherEffectsEnabled`（bool）：关掉后主机侧 `SubsystemWeather.Update`
  把降水强度**直接清零**（立即停雨，雾同理），并持久写进世界设置；开回来则恢复模拟。

### 2) `ScMP.Player.SafeRespawnRelocate`（联机 mod 内置，本 mod 直接调用）

- 载荷（JSON，由联机 mod 自己编解码）：`{"TargetClientId": -1}`（`-1` = 自己）
- 主机执行：把玩家安全传送到 `PlayerData.SpawnPosition`（睡觉点）；骑乘状态会拒绝

### 3) `ScMP.Data.WorldControl`（联机 mod 自己落地的运行时天气 + 时间点）

- 与 `ScMP.Data.WorldSettings` **同级、同一套载荷**：纯文本，每行 `字段名<TAB>值`（UTF-8），
  复用 `WorldSettingsDataCodec`（同样的理由不用 JSON）。
  为什么单独一条 op：运行时天气与"推到某个时间点"**不是 `WorldSettings` 字段**
  （那里只有天气总开关 `AreWeatherEffectsEnabled`），而引擎原生那几个按钮
  （`ComponentGui.Update` 里的 Precipitation / Fog / Lightning / TimeOfDay）要求
  `GameMode.Creative` 或 `WorldControl` 能力，而且完全绕过 DM 审批。
- 字段与主机语义（`ScMultiplayerWorldControlModification.ApplyHostWorldControlModification`）：

  | 字段 | 取值 | 主机执行 |
  |---|---|---|
  | `Precipitation` | `on` / `off` / `toggle` | `SubsystemWeather.ManualPrecipitationStart` / `ManualPrecipitationEnd` |
  | `Fog` | `on` / `off` / `toggle` | `SubsystemWeather.ManualFogStart` / `ManualFogEnd` |
  | `Lightning` | `strike` | `SubsystemWeather.ManualLightingStrike`，方向取**发起者的眼睛朝向**，主机本地玩家兜底 |
  | `TimePoint` | `dawn` / `noon` / `dusk` / `midnight` | `TimeOfDayOffset += IntervalUtils.Interval(TimeOfDay, 目标)`，定点跳到该时间点 |
  | `TimeExact` | `0..1` | 同上，口径与 `SubsystemTimeOfDay.TimeOfDay` 一致 |

- 主机落地后调用 `SendGameWorldInfoMessage()`，复用**既有**的 2Hz 世界信息广播把降水 / 雾 /
  时间偏移发给所有客户端 —— **没有新协议**。
- ⚠ 两个"点了没反应"的坑（主机侧已处理，这里写清原因）：
  1. **"打开降雨 / 雾气"会顺带把总开关 `WorldSettings.AreWeatherEffectsEnabled` 打开**：
     客户端 `SubsystemWeather.Update` 受总开关约束，总开关关着时降水与雾根本不渲染，
     摘要里会多出一条 `weatherEffects=on`；
  2. **跳时间点前会先把 `TimeOfDayMode` 切回 `Changing`**：引擎在非 `Changing` 档位把时间**固定**
     住，不切的话改 `TimeOfDayOffset` 看不到任何变化；这一步会出现在回执摘要里
     （`timeOfDayMode=Changing`）。

### 4) 生命体征 / 异常状态 / 复活点（联机 mod 内置 `ScMP.Player.*`，本 mod 直接调用）

| 操作 | 载荷字段 | 主机执行结果 |
|---|---|---|
| `ScMP.Player.SetVitals` | `Vitals` 位掩码（`Food=1` / `Stamina=2` / `Sleep=4` / `Temperature=8` / `Wetness=16`，**未置位 = 保持主机当前值**）+ `VitalsFood` / `VitalsStamina` / `VitalsSleep` / `VitalsTemperature` / `VitalsWetness` | 目标须存活；`food` / `stamina` / `sleep` / `wetness` 限 `0..1`，`temperature` 限 `0..24`（**12 = 舒适**，引擎口径）。主机复用 `ApplyAuthoritativePlayerStats` 写 `m_food` / `m_stamina` / `m_sleep` / `m_temperature` / `m_wetness` 与对应的 `m_last*`，随后 `SendAuthoritativePlayerHealth(force: true)` 权威下发 |
| `ScMP.Player.SetCondition` | `Condition` 位掩码（`Flu=1` / `Sickness=2`；`None` = 全部）、`ConditionMode`（`ConditionAction.Clear=0` / `Apply=1`）、`ConditionDuration`（秒，`0` = 引擎默认时长，上限 `3600`） | 施加走原版 `ComponentFlu.StartFlu()` / `ComponentSickness.StartSickness()`（给了秒数就写 `m_fluDuration` / `m_sicknessDuration`）；解除与 `ResetNetworkPlayerVitals` 同口径（流感 6 个字段清零；中毒含 `m_pukeParticleSystem` 置 `null` + 3 个字段清零），随后同样 `SendAuthoritativePlayerHealth(force: true)` |
| `ScMP.Player.SetRespawnAnchor` | `X` / `Y` / `Z` | 主机在已加载地形里找安全落点后更新复活点。`X/Y/Z` **全 0 = 主机回退到该角色当前坐标**，所以"把复活点设为该角色当前位置"就是发一个零向量，**不需要给在线玩家列表加坐标字段** |

菜单里的"手动输入"全部复用引擎自己的文本输入（`Engine.Input.Keyboard.ShowKeyboard`，Android 弹软键盘、
桌面直接键入）；坐标输入接受 `x,y,z`，中文逗号 / 空格 / 分号也能当分隔符。

## 四、依赖要求

- 需要 **ScMultiplayer 2.1.7+**（`ModInfo.xml` 里声明了依赖）：
  第三节里的 `ScMP.Player.SetVitals` / `ScMP.Player.SetCondition` / `ScMP.Data.WorldControl`
  都是 2.1.7 才有的操作，更旧的联机 mod 主机没有这些处理器，请求会失败。
- 联机 mod 的 `Obfuscar.xml` 必须保留 GM 门面类型/成员名字
  （`SkipType` + `skipMethods/skipFields/skipProperties/skipEvents="true"`），
  否则混淆后 GmMod 在运行时找不到 `DataModificationTool` 等类型。
