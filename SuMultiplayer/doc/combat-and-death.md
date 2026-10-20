# 近战攻击表现与客户端死亡（主机裁决口径）

> 本文记录两条客户端表现契约：**近战不显示预测标记**、**客户端死亡姿态由本地补写**。
> 镜像分屏在客户端死亡 / 复活时的相机切换见 [`MIRROR-CHARACTER.md`](MIRROR-CHARACTER.md)
> 的「客户端死亡 / 复活：相机切 DeathCamera / FppCamera」。

## 1. 近战：预测标记已关闭，表现只由主机回执驱动

**产品口径**：客户端不再显示红色 `*` 命中标记，也不再显示白色预测 `Miss` 粒子。
攻击的可见结果只有主机回执：

| 主机回执 | 客户端表现 |
|---|---|
| 命中（`DamagePopup`） | 权威伤害数字（`-N`），位置 / 方向来自回执（伤害为 0 / 无敌时不画数字） |
| 未命中（`MissMarker`） | 一次权威 `Miss` 标记（白色），不播命中音 |
| 拒绝（`out-of-reach` / `cooldown` / `target-missing` …） | 按未命中展示，原因进日志与计数 |

注意：挥手动作 + 本地 `Audio/Swoosh` 仍是即时表现（`PresentSwing` 在预测分支之前，`:657`），
关闭预测标记不影响它们；被关掉的只有红色 `*` 与白色预测 `Miss` 粒子。

实现（`Engine/MeleeReplicaEngine.cs`）：

- `PredictWithStream` 仍用共享战斗流算出 `predictedHit`，但预测标记**恒为 `null`**：
  `ParticleSystemBase marker = null; m_predictionMarkers[sequence] = marker;`（`:674-678`）。
  `RemovePredictionMarker` 遇到空槽直接返回，因此不会再出现任何预测粒子。
- 结果处理只画权威表现：命中回执画伤害数字（`:1359-1363`），未命中回执画 `Miss`（`:1365-1369`）；
  预测不再抑制权威 `Miss`（`:1336-1338`）。
- 预测逻辑仍在，只是**不参与视觉**：`CombatRngClient.Reconcile` 按主机回执对账流位置
  （`:1343-1352`）、`m_predictionConfirmed` 命中确认（`:1322-1323`）、
  不匹配 / 拒绝回滚与重同步（`:1290-1319`）。
- 共享战斗流：主机按 `(clientId, slot)` 持有一条引擎同源 LCG，客户端镜像同一条流
  （`Net/CombatRng.cs:10-20`）；客户端只预测“会命中 / 不会”，**不预测伤害数字、不写生命**。
- 配置 `meleeprediction` 仍控制是否建立预测表（`false` → `m_predictionDisabled++` 并跳过预测，
  `:658-662`；接线见 `Plug/SuMultiplayerMod.cs:421`），但**不再控制任何可见标记**——标记已无条件关闭。

旧行为曾用预测命中标记 `*` 做即时反馈，主机确认后再删除它；保留到确认之后会同时看到 `*` 与 `-N`
（`:1327-1329` 记录了这个实测故障）。现在的口径是预测标记整个关闭，表现统一由回执产生。

## 2. 客户端死亡姿态：Health 压到 0.001，DeathPhase 写 1

客户端本机角色死亡时：

1. `ComponentHealth.Health` 写非致命极小值 `0.001`（`LocalDeathHealthFloor`，
   `Engine/PlayerVitalsReplica.cs:63-67`；写入 `:496-511`）。这样引擎 `PlayerData` 不会进入
   `PlayerDead`（本地删实体 → 本地复活），死亡裁决仍只在主机；血条 / 死亡提示按主机下发的 0 显示
   （`WriteLocalHud(..., 0f, ..., true)`，`:513-517`）。
2. 死亡屏显示时写 `ComponentCreatureModel.DeathPhase = 1f`（通过父字段注入
   `<DeathPhase>k__BackingField`，`:940-950`）。引擎最终姿态取
   `max(DeathPhase, m_lieDownFactorModel)`（`ComponentHumanModel.cs:420`），所以角色呈死亡躺姿；
   而 `ComponentCreatureModel` 只在 `Health == 0` 时推进 `DeathPhase`
   （`ComponentCreatureModel.cs:184-187`），0.001 不会触发，因此必须由本模块补写。

复活（主机 `PLAYER_VITALS_EVENTS` 的 Respawn 边沿）：

- `DeathPhase = 0` + `DeathCauseOffset = 0`（`:551-557`；远端化身走 `ResetDeathState` 同一口径，`:2651-2670`）；
- 睡眠 / 火焰表现清理、HUD 回到 1、死亡屏清除（`:524-580`），相机回 `FppCamera`（`HideLocalDeath`，`:962-976`）。

写失败不静默：日志 `death-pose:<slot>` 明确写“角色会保持站立”（`:952-956`）。

## 3. 镜像分屏的死亡相机

客户端角色死亡时，主机镜像分屏（显式 `H3` 与 `mirrorcharacterauto` 自动宿主共用同一段绑定代码）
切到 `DeathCamera`，复活切回 `FppCamera`。原因、日志与内部计数见
[`MIRROR-CHARACTER.md`](MIRROR-CHARACTER.md) 的「客户端死亡 / 复活：相机切 DeathCamera / FppCamera」：
`FppCamera` 盯死亡模型时眼睛位置失效会黑屏，`DeathCamera` 是引擎 `PlayerDead` 用的同一台相机。

## 验证清单

1. 两个客户端进同一世界，A 攻击 B：主机结果到达前**看不到红色 `*` / 白色预测 `Miss` 粒子**；
   命中只出现一次权威伤害数字，未命中只出现一次 `Miss`。
2. A 的客户端日志里近战 10 秒摘要的 `meleePredicted` 随攻击递增、`meleePredictionConfirmed`
   随回执递增，表示预测逻辑仍在跑（`Engine/MeleeReplicaEngine.cs:1462-1470`）。
3. 客户端角色被主机判定死亡：从另一客户端或主机非镜像分屏观察，角色呈躺姿；
   客户端装了 CmdBridge 时可 `sccmd players` 读本机玩家的 `action.deathPhase=1`，复活后回 `0` 并站起。
4. 主机日志出现 `view-death-camera:<clientId>` → 第三 / 第四分屏切到 `DeathCamera`；
   客户端复活后出现 `view-death-camera-restore:<clientId>` → 切回 `FppCamera`。
