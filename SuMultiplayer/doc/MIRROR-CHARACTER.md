# 调试镜像角色（mirrorcharacter）— 把 H3 的第三个分屏变成客户端的第一人称

> 默认关闭。纯逻辑 = `Modules/DebugMirrorCharacterPorts.cs`；引擎动作 = `Engine/MirrorCharacterEngine.cs`。
> 配置键在 `Engine/SyncConfig.cs`（`data:/Mods/SuMultiplayer.json`）。

## 配置键

| 键 | 值 | 说明 |
|---|---|---|
| `mirrorcharacter` | 角色名（例 `"H3"`）/ 空 | 主机本地角色的 `PlayerData.Name`（大小写不敏感、多重匹配拒绝）。空 / `false` / `off` / `no` / `none` / `disabled` / `0` = 关闭（默认）。 |
| `mirrorcharacterclient` | 空 / `auto` / `first` / `<clientId>`（> 0） | 镜像哪个客户端：空 = 第一个加入的客户端（最小 clientId）；指定编号 = 只认它（不在线就等待，绝不改成 auto）。槽位固定 0（`PlayerKey(clientId, 0)`）。 |
| `mirrorcharactermode` | 空 / `replace`（默认）/ `copy` | `replace` = **用 H3 的第三个分屏显示客户端角色本身**（H3 的身体从世界表现里摘掉、不复制状态）；`copy` = 旧行为（把客户端状态逐拍复制到 H3 身上）。写错 = 功能保持关闭 + 日志里给出可直接定位的原因。 |

示例：

```json
{
  "mirrorcharacter": "H3",
  "mirrorcharacterclient": "",
  "mirrorcharactermode": "replace"
}
```

## replace（默认）：第三分屏 = 客户端的第一人称

绑定条件：主机 + 世界内 + `mirrorcharacter` 名字唯一匹配 + 该客户端的联机化身有
`ComponentPlayer` / `ComponentBody` / `ComponentCreatureModel`。

绑定动作（**不复制任何状态**）：

1. **相机**：把 H3 那个 `GameWidget` 的 `Target` 换成客户端的 `ComponentPlayer`，
   `ActiveCamera` 切成 `FppCamera`（与 `Engine/DebugMirrorClient.cs:275-280` 同一条已证明路径）。
   - `ViewWidget.Draw` 只用 H3 自己的 `PlayerData.ComponentPlayer != null && IsReadyForPlaying` 决定画不画
     （Source: `Survivalcraft/Game/ViewWidget.cs:45-51`）—— H3 的 PlayerData 一直活着，所以这个分屏照常画；
   - `FppCamera.Update` 每帧读 `GameWidget.Target.ComponentCreatureModel.EyePosition / EyeRotation`
     （Source: `Survivalcraft/Game/FppCamera.cs:21-44`）—— 换 Target 后就是客户端的第一人称。
   - 注：`PlayerData` **没有** `ActiveCamera` 成员；相机是 `PlayerData.GameWidget.ActiveCamera`。
2. **H3 的身体从世界表现里摘掉**（**不改坐标**）：
   - 模型不渲染：把 H3 实体的每个 `ComponentModel` 从 `SubsystemModelsRenderer.m_componentModels`
     摘除。绘制入口只遍历这张表（Source: `Survivalcraft/Game/SubsystemModelsRenderer.cs:66-88`），
     模型不在表里 = 任何视图都看不到，阴影也不会排队（`QueueShadow` 在 DrawModels 内）。
     实体加入/移除时这张表由 `OnEntityAdded/OnEntityRemoved` 维护（`:140-159`）。
   - 不可碰撞：`SubsystemBodies.RemoveBody`（private，`IModParentMethod`；唯一能摘掉身体 AABB 的入口）。
   - 不再推挤/改状态：`SubsystemUpdate.RemoveUpdateable` 摘掉 body / input / health / vitals /
     locomotion / miner / flu / sickness / clothing / level / rider / sleep；输入清零、`m_aim` 清空、
     GameWidget 不接受命中测试。
   - **为什么不把身体挪到世界外**：主机每帧把本机角色位置广播给客户端
     （`NetworkAvatarSpawner.CapturePlayerPositionBatch`，键 `(0, slot)`），挪走会让客户端上的 H3
     一起瞬移，还会污染存档位置 / 实时地图。模型隐藏没有这些副作用。
3. 不复制 H3 的背包 / 生命 / 动作，也不写任何客户端身份记录（`SuMultiplayerPlayers.xml` 不动）。

解绑（客户端离开 / 离开房间 / 世界卸载 / 配置关闭 / 模块卸载）：
相机回 H3 自己的 `ComponentPlayer` + 绑定前的 `ActiveCamera`（不可用时明确回退 `FppCamera` 并写日志）；
模型重新加回渲染表；碰撞 / updateable / 输入 / 瞄准 / 命中测试 / 位置按绑定前快照写回。

## HUD：这个视图里显示什么、不显示什么

- **来自「相机 Target = 客户端角色」的**（引擎按相机目标渲染）：
  - 3D 画面 / 视锥 / 视点位置与朝向 = 客户端的第一人称；
  - 第一人称手与手持物（`ComponentFirstPersonModel.Draw` 在
    `camera.GameWidget.IsEntityFirstPersonTarget(entity)` 时绘制，Source:
    `Survivalcraft/Game/ComponentFirstPersonModel.cs:57-60`）；
  - 声音听者位置（`SubsystemAudio` 用各分屏 `ActiveCamera.ViewPosition`）；
  - 方块高亮射线（`ComponentBlockHighlight.Update` 用 `GameWidget.ActiveCamera`）。
- **不来自相机目标的**：生命 / 食物 / 温度 / 等级 / 快捷栏 / 消息提示等 HUD
  是 `ComponentGui`，它按**玩家实体**绑定（`ComponentGui.Load` 读
  `m_componentPlayer.GuiWidget` 等，Source: `Survivalcraft/Game/ComponentGui.cs:226-300`；
  数值读 `m_componentPlayer.ComponentHealth / ComponentVitalStats / ComponentMiner.Inventory`）。
  引擎没有"按相机目标渲染 HUD"的机制；而 replace 模式又禁止复制背包 / 生命到 H3（= copy 模式）。

### 反馈镜像（`Engine/MirrorViewFeedback.cs`）：只写显示层，不写权威状态

replace 期间 H3 自己的 `ComponentHealth.Update` / `ComponentVitalStats.Update` 已被摘掉
（`MirrorCharacterEngine.TryBind` 的 `RemoveUpdateable`），引擎不会再替视图所有者写这些显示值
—— 否则客户端掉血时第三分屏什么都不会亮。因此每拍由
`MirrorViewFeedback.Tick`（在 `MirrorCharacterEngine.ValidateBound` 里调用）把客户端化身的可显示状态
**重放到 H3 的显示层**：

| 显示项 | 写入目标（H3） | 公式与引擎同源 |
|---|---|---|
| 红屏 | `ComponentScreenOverlays.RedoutFactor` | `m_redScreenFactor += -4f * HealthChange` + 每拍 `Pow(0.2, dt)` 衷减 / `≤0.01` 归零（`ComponentHealth.cs:141-147, 251-261`） |
| 血条闪烉 | `HealthBarWidget.Flash(...)` | `Clamp((int)(drop*30), 0, 10)`（`ComponentHealth.cs:257`） |
| 血条数值 | `HealthBarWidget.Value` | `Health`（`ComponentHealth.cs:265`） |
| 食物条 | `FoodBarWidget.Value` | `Food`（`ComponentVitalStats.cs:345`） |
| 温度档位 | `TemperatureBarWidget.BarSubtexture` | `Temperature0..6`（阈值 22/18/14/10/6/2 严格大于，`ComponentVitalStats.cs:589-616`），复用 `PlayerVitalsPresentation` |
| 冰冻覆盖 | `ComponentScreenOverlays.IceFactor` | `Saturate(1 - Temperature/6)`（`ComponentVitalStats.cs:579`） |
| 等级文字 | `LevelLabelWidget.Text` | `"Level " + Floor(Level)`（`ComponentLevel.cs:335`） |
| 死亡表现 | `BlackoutFactor = 1` + `DisplayLargeMessage("YOU HAVE DIED", ...)` | 文本 / 30 s / 1.5 s 延迟与 `PlayerData.PlayerDead` 完全一致（`PlayerData.cs:275-300`） |

硬边界：**只读**客户端化身的 `ComponentHealth` / `ComponentVitalStats`（含私有
`m_targetTemperature`）与 `PlayerData.Level`；**不写**它们的任何字段、不 `Injure`、不复制背包 / 衣物 /
位置 / 动作（copy 模式才做）。**不切相机**（第三分屏的 `ActiveCamera` 必须留在客户端
第一人称 `FppCamera`）。快捷栏仍然是 H3 自己的。

解绑时：把绑定前抓到的 HUD 显示值写回（血条 / 食物条 / 温度档位 / 等级文字）、
清掉我们写过的黑屏 / 红屏 / 冰冻覆盖与死亡大消息，并停止更新（`MirrorViewFeedback.Release`）。
- 客户端的**整身模型**在它自己的第一人称视图里由引擎隐藏
  （`ComponentModel.CalculateIsVisible`：`IsEntityFirstPersonTarget` ⇒ `IsVisibleForCamera=false`，
  Source: `Survivalcraft/Game/ComponentModel.cs:69-74`），所以第三分屏只画客户端的双手 / 手持物，
  不会出现"两个角色模型重叠"。

## copy（可选，旧行为）

每拍把客户端角色的位置 / 体征 / 背包 / 衣物 / 动作复制到 H3 身上（`Engine/MirrorCharacterEngine.cs`
的 `ApplyBody/ApplyVitals/ApplyInventory/ApplyActions`），H3 就是客户端的精确替身。
绑定期间 H3 的存档值是镜像值，解绑按快照写回；H3 的 `PlayerData.Name` / PlayerStats / 身份记录从不写入。

## 计数 / 日志（10 秒限流）

状态行（加载日志 + 5 秒周期日志）与绑定 / 解绑日志带 `mirrorMode=replace|copy`。计数器：

`mirrorBind` / `mirrorUnbind` / `mirrorNotFound` / `mirrorMultipleMatches` /
`mirrorViewBound` / `mirrorViewRestored` / `mirrorBodyParked` / `mirrorParkedRestored` /
`mirrorCollisionDisabled` / `mirrorRestored` / `mirrorFailures`
（copy 模式另有 `mirrorPosCopy` / `mirrorVitalsCopy` / `mirrorInvCopy` / `mirrorActionCopy`）。

`mirrorBodyParked` 口径 = "H3 的身体被摘出世界表现（模型不渲染 + 不可碰撞 + updateable 已停）"，
实现**不改坐标**；`mirrorParkedRestored` = 解绑时模型/渲染恢复。

replace 模式还有第三分屏反馈镜像的计数（同一行状态行 / 10 秒限流日志输出，
由 `IMirrorCharacterPort.DescribeViewFeedback()` 提供）：

`viewDamageFlash` / `viewHealthBarFlash` / `viewHudNumbers` / `viewDeathOverlay` /
`viewFeedbackFailures` / `viewFeedbackSkipped`（+ `viewSkippedReason` / `viewFailureReason`），
侦测用细项 `viewIceWrites` / `viewRespawnClears` / `viewFrames`。
未接入（配置关闭 / 已解绑）时全为 0，状态行不会缺键。

## 设备验证清单

1. 主机有本地角色 `H3`（分屏里能看到它），客户端加入房间；
2. `mirrorcharactermode` 留空或 `replace`，`mirrorcharacter=H3`；
3. 主机日志出现 `mirrorBind … mirrorMode=replace … viewBound=1 bodyHidden=1`；
4. **H3 原来站的位置看不到 H3 了**（模型不渲染、其它玩家的视角里也看不见），且走过去不会撞到它；
5. **第三个分屏 = 客户端的第一人称**（画面随客户端转头/走动；看得到客户端的双手 / 手持物）；
6. **屏幕上只有一个客户端角色模型**（在 H3 分屏里只有第一人称手；H3 的模型不再出现）；
7. 快捷栏仍是 H3 自己的（replace 不复制背包）；血条 / 食物条 / 温度档位 / 等级文字
   由反馈镜像每拍跟客户端（见上文"反馈镜像"）；
8. **客户端被打掉血**：第三分屏闪红 + 血条下降 / 闪烉（与客户端自己的屏幕同一套公式）；
9. **客户端死亡**：第三分屏黑屏 + "YOU HAVE DIED" + 死因（不出现 "Tap to respawn"）；
10. **客户端复活**：黑屏 / 大消息立刻消失，血条回到 1；
11. **H3 自己不受影响**：绑定期间打 H3 不会让第三分屏亢血（镙到的是客户端）；
12. 客户端离开 / 关房 / 退世界 / 关配置后：`mirrorUnbind … restored=True viewRestored=True bodyRestored=True`，
    H3 恢复渲染 / 碰撞 / 自己的第一人称视图，位置与背包不变；
    红屏 / 黑屏 / 死亡大消息清除，HUD 显示值写回绑定前的值（血条 / 食物条 / 温度档位 / 等级文字）。

## 镜像宿主（`mirrorcharacterauto`，**默认开启**）：客户端加入时主机自动建一个真实本地角色

> 纯逻辑 = `Modules/MirrorHostPorts.cs`（配置 / 池计算 / 闸门 / 命名 / 复用 / 生命周期 / 计数）；
> 引擎动作 = `Engine/MirrorHostEngine.cs`（只用公开 API）；绑定复用同一份 `Engine/MirrorCharacterEngine.cs`。
> 与上面的 `mirrorcharacter`（显式 H3）是**两件事**：`mirrorcharacter` 手动指定一个已存在的角色，
> `mirrorcharacterauto` 由主机**自动创建**角色 —— 后者才是"让第三 / 第四分屏变成真实本地角色"的调试功能。

### 为什么要建真实本地角色

引擎的分屏视图必须是**本机 PlayerData** 的 GameWidget；只有真的是"本机玩家"才会带
引擎自带的触控控件 / HUD / 界面镜像 / 睡眠与受击表现。`clientview` 那种 camera-only 壳
（实体不进 Project、PlayerData 立刻从 PlayersData 摘掉）只有画面，没有这些表现。
本功能按引擎官方链路创建一个真实角色，再把它的相机绑到客户端的第一人称，
所以第三 / 第四分屏 = 客户端的第一人称 + 完整的本地角色表现。

### 配置键

| 键 | 值 | 说明 |
|---|---|---|
| `mirrorcharacterauto` | `true`（默认）/ `false` | 关闭 = 一个引擎调用都不发生（零影响） |
| `mirrorhostnameprefix` | 默认 `"Mirror"` | 自动角色名 = `<前缀>#<clientId>`，重名依次追加 `-2` / `-3`…；空 = 默认；带控制字符 / 超 16 字符 = 配置写错（功能关闭 + 明确告警） |
| `mirrorcharacter` | 角色名 / 空 | **显式名字优先**：世界里已有该角色 → 直接用它当宿主（见下文"复用"）；手动创建的角色不会被归入 auto 账本 |
| `mirrorcharactermode` | 空 / `replace`（默认）/ `copy` | `replace` = 该分屏显示客户端第一人称、宿主身体隐藏不可碰撞；`copy` = 逐拍复制客户端状态到宿主身上 |

### 池规则（用户口径）

```
同屏可用池 freeLocalSlots = 4 − SubsystemPlayers.PlayersData.Count
```

- `PlayersData.Count` **只算真实本地角色**：远端化身在 `NetworkAvatarSpawner.TryCreate` 里已经被移出
  `PlayersData`（GameWidget 也摘了），**不占池**；自动创建的宿主**在** `PlayersData` 里，同样占池，
  4 个本地角色的上限不因为"自动"而放宽；
- 例：主机 h1/h2（2 个本地角色）→ 池剩 2 → 客户端 1 自动占第 3 个、客户端 2 自动占第 4 个；
- 只有 `freeLocalSlots > 0` 才能建；还要同时满足：该客户端**还没有**镜像宿主、世界模式不是
  Cruel / Adventure（引擎规则：`PlayersScreen.cs:48-55` 在这两种模式下拒绝加角色）；
- 池满时第 3 个客户端加入 → **明确拒绝**（`mirrorHostBlocked(max-players)`），不崩、不静默、
  **绝不顶掉已有角色**；同一客户端被拒绝后 2 秒内不重复尝试（不刷计数），客户端离开 / 池空出后自动补上。

### 引擎链路（file:line）

| 步骤 | 引擎事实 | 本功能用法 |
|---|---|---|
| 新建 | `PlayerScreen.cs:121` `m_playerData = new PlayerData(project)` | `new PlayerData(project)` |
| 设名 | `PlayerData.cs:84-98`（`Name`）/ `:122`（`InputDevice`） | `Name = <前缀>#<clientId>`；`InputDevice = WidgetInputDevice.None` |
| 加入 | `SubsystemPlayers.cs:62-75`（上限 4、发 PlayerIndex、触发 `PlayerAdded`） | `players.AddPlayerData(playerData)` |
| 分屏 | `SubsystemGameWidgets.cs:85-100`（自动建 GameWidget + `GamesWidget` 布局切换） | 不用管，引擎自己建 |
| 出生 | `PlayerData.cs:136-323`（状态机 PrepareSpawn → WaitForTerrain → SpawnPlayer，数秒） | 只等，不催 |
| 就绪 | `PlayerData.cs:124-134` `IsReadyForPlaying` | `IsReadyForPlaying && ComponentPlayer != null` 都满足才绑定 |
| 移除 | `SubsystemPlayers.cs:77-90`（`PlayerRemoved` → 摘 GameWidget + 删实体 + Dispose） | `players.RemovePlayerData(playerData)`（**只对 auto 角色**） |
| 绑定 | 同 `mirrorcharacter` 的 replace | `MirrorCharacterEngine.TryBind(name, clientId, MirrorCharacterPlan.Replace)` |

### 创建 / 绑定 / 清理流程

1. **创建**：`ClientJoined`（模块侧建出联机化身之后）+ 主机 + 世界内 + `mirrorcharacterauto` +
   该客户端无宿主 + 池未空 → `TryCreate` → `m_sessionOwned[clientId] = PlayerData`（auto 账本）；
2. **绑定**：宿主未就绪只计数 `mirrorHostPending`（**禁止提前绑**）；就绪后 `mirrorHostBind`，
   replace = 该分屏相机 = 客户端第一人称 + 宿主身体不渲染 / 不可碰撞（细节见本文上半部分）；
3. **清理**（同一条路径）：客户端离开 / 离开房间 / 世界卸载 / 配置关闭 / 应用退出或 Mod 卸载 →
   先解绑镜像（写回宿主绑定前快照），再 `RemovePlayerData`；**只删 auto 账本里的角色**，
   手动创建的 h1/h2/H3 绝不触碰。

### 复用与存档策略（重要）

- 自动角色是**真实本地角色**，所以在 `SubsystemPlayers.Save` 的遍历范围内 —— **会进世界存档**；
- 为了在重启后还能分清"这是自动角色"，本功能把一个极薄的引擎子系统
  `MirrorHostEngine+RegistrySubsystem` 注入到 `Game.SubsystemPlayers` 模板下，
  把 `AutoHost<PlayerIndex>=1` / `Host<PlayerIndex>=clientId` 写进**它自己的 ValuesDictionary**
  （`Project.SaveProject` 把每个子系统的 ValuesDictionary 整张写进存档、进世界原样读回；
  `PlayerData.Save` 只写引擎固定字段，写不下自定义标记）；
- **再次进房 / 新客户端**：世界里已有同前缀的 auto 角色（`<前缀>#<N>` 且带标记）→ 直接**复用**，
  计数 `mirrorHostReused`，不新建；同一个客户端 1 离开又回来，也只会有一个同名角色；
- **宿主标记的含义 = "离开时应被移除"**：客户端离开 / 关配置 / 卸载时只删带标记的前缀命名角色；
  世界卸载 / 主机关游戏那一刻 Project 已经被 Dispose，移除做不了 → 下次进世界第一拍由
  `ReconcileOrphans` 补做（只删带标记的前缀命名角色，手动角色不动）；
- 配置关闭时不自动删（"关闭 = 零引擎动作"），日志里写清"世界里还留着 auto 角色，请手动在
  Edit Players 里删除"；
- **手动创建的角色永远不在 auto 账本里**：即使 `mirrorcharacter` 指向它、即使它名字像自动名，
  也既不会被当作自动宿主复用，更不会被删除。

### 与 `clientview` 的关系

`mirrorcharacterauto` 生效时，同一个客户端的自动客户端视图**让位**（显式镜像优先）：
组合根把"该客户端已有镜像宿主"传给 `ClientViewController` 的显式镜像判定，
命中时计 `clientViewYieldToExplicit`（状态行里是 `yieldExplicit=`）并拆掉那个 camera-only 视图。
当前 `clientview` 已配置为 `false`，两者不会同时生效。

### 计数器 / 日志（全部 10 秒限流、全部显式）

状态行与加载日志：

```
mirror-host: config=on prefix="Mirror" mode=replace explicit=none state=on
  hostLocals=2 used=2/4 free=2 mirrorHostCreated=1 mirrorHostReused=0 mirrorHostRemoved=0
  mirrorHostPending=3 mirrorHostBind=2 mirrorHostRestore=0 mirrorHostBlocked=1(max-players=1)
  mirrorHostFailures=0 mirrorHostFreeSlots=1 mirrorHostActive=1
```

| 计数 | 口径 |
|---|---|
| `mirrorHostCreated` | 新建的自动角色数（事件） |
| `mirrorHostReused` | 复用世界里已有 auto 角色的次数（事件） |
| `mirrorHostRemoved` | 移除 auto 角色的次数（事件） |
| `mirrorHostPending` | 宿主"还没就绪"的**帧**数（每帧累加，日志 10 秒一行） |
| `mirrorHostBind` | 成功绑定到客户端第一人称的次数（事件） |
| `mirrorHostRestore` | 解绑并写回宿主快照的次数（事件） |
| `mirrorHostBlocked(reason)` | 拒绝次数，按原因分项：`max-players` / `cruel` / `adventure` / `duplicate-name` / `not-hosting` / `no-client` |
| `mirrorHostFailures` | 创建 / 绑定 / 解绑 / 移除失败次数（原因写进同一条日志） |
| `mirrorHostFreeSlots` | 当前池（= 4 − `PlayersData.Count`） |
| `mirrorHostActive` | 当前自动宿主数 |

### 设备验证清单（镜像宿主）

1. 主机有 h1/h2 两个本地角色（两个分屏），客户端 1 加入；
2. 主机日志出现 `mirrorHostCreated=1 … 新建本地角色「Mirror#1」`；随后 `mirrorHostPending` 增长，
   数秒后 `mirrorHostBind=1`；
3. **主机屏幕出现第三个分屏**，画面 = 客户端 1 的第一人称，带引擎自带的触控控件 / HUD；
   世界里看不到 Mirror#1 的身体，也撞不到它；
4. 客户端 2 加入 → `Mirror#2` 占第 4 个本地角色（四宫格），`mirrorHostFreeSlots=0`；
5. 客户端 3 加入 → `mirrorHostBlocked(max-players)=1`，**游戏不崩**、Mirror#1/#2 不受影响；
6. 客户端 1 离开 → `mirrorHostRestore=1` / `mirrorHostRemoved=1`，Mirror#1 的分屏消失、槽位释放；
   **h1/h2 两个分屏原样保留**（位置 / 背包 / HUD 不变）；
7. 客户端 1 再进来 → `mirrorHostReused=1`（世界存档里已有 Mirror#1 时）或重新创建同名角色，
   不会出现越来越多个 Mirror#k；
8. 关房 / 退世界 / 关 `mirrorcharacterauto` / 卸载 Mod → 都走同一条清理路径（只删 auto）。
