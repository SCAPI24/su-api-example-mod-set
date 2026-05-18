# SurvivalcraftNet 参考项目对比分析

> 参考项目: SurvivalcraftApi-SCAPI1.9_MP (本地路径)
> 目标项目: ScMultiplayer (Comms + SuAPI Mod)
> 最后更新: 2026-05-17

---

## 一、架构差异概述

| 维度 | 参考项目 | ScMultiplayer |
|------|----------|---------------|
| 修改方式 | 直接改游戏源码 | SuAPI Mod (不改源码) |
| .NET 版本 | .NET 10 | .NET Framework 4.8 |
| 网络库 | LiteNetLib (UDP) | Comms (Comms.Drt) |
| 注入方式 | HarmonyX + 静态字段/事件绑定 | ModEventBus + ParentField/ParentMethod |
| Mod 入口 | ModLoader 继承 | IMod 接口 |
| 同步模型 | Event-Driven + 周期 (10fps) | Tick-Driven + 状态推送 (30fps) |
| 权威模型 | Server 权威 | Host (ClientID=0) 权威 |
| 包格式 | Packet 子类 + MessagePack | Message 抽象类 + Reader/Writer |
| 压缩 | DeflateStream | 无 (Comms 自动分片) |
| 房间发现 | 直连 IP:Port | 局域网广播 (Explorer) |

---

## 二、同步数据对比

### 玩家数据

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 位置/旋转/速度 | ComponentLocomotion (10fps) | GamePlayerPositionMessage (30fps) | ScMP 更频繁 |
| 视角角度 | PlayerBodyUpdatePacket | GamePlayerPositionMessage | 一致 |
| 手持物品 | ActiveSlotChangePacket | GamePlayerPositionMessage 内含 | 参考更细粒度 |
| 动画 | HumanModel.OnRow | GamePlayerPositionMessage 内含 | 一致 |
| 攻击 | PlayerHitPacket | 未同步 | **缺失** |
| 挖掘 | PlayerDigPacket | 未同步 | **缺失** |
| 交互 | PlayerInteractPacket | 未同步 | **缺失** |
| 瞄准 | PlayerAimPacket | 未同步 | **缺失** |
| 输入 | 被动 (NetworkBody控制) | GamePlayerInputMessage (注释) | 参考无主动发送 |
| 骑乘 | RiderPacket (上下马) | GamePlayerPosition 内含状态 | 参考含事件 |
| 皮肤 | Clothing.OnRequestSkin | 未同步 | **缺失** |

### 生命/伤害

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 受伤 | InjureEventPacket | 未同步 | **缺失** |
| 死亡 | HealthSyncPacket | 未同步 | **缺失** |
| 治疗 | HealthSyncPacket (2s间隔) | 未同步 | **缺失** |
| 摔伤 | InjureRequestPacket | 未同步 | **缺失** |
| 着火 | OnSetOnFire | 未同步 | **缺失** |

### 物品栏

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 槽位变化 | InventorySyncPacket | 未同步 | **缺失** |

### 方块/地形

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 方块修改 | TerrainChangeCellPacket | GameModifiedCellsMessage | 一致 |
| 地形块同步 | TerrainSyncChunkListPacket | 无 (世界存档整包传输) | 参考分块传输 |
| 玩家区块位置 | PlayerUpdateLocationPacket | 未同步 | 参考主动通知 |

### 方块交互

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 箱子 | OnOpenInventoryRequest | 未同步 | **缺失** |
| 工作台 | OnOpenInventoryRequest | 未同步 | **缺失** |
| 发射器 | OnOpenInventoryRequest | 未同步 | **缺失** |
| 熔炉 | OnFurnacesUpdate | 未同步 | **缺失** |
| 告示牌 | OnEditSignRequest | 未同步 | **缺失** |

### 电路元件

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 电池 | OnEditBlockVoltage | 未同步 | **缺失** |
| 按键 | OnEditBlockVoltage | 未同步 | **缺失** |
| 开关 | OnEditBlockVoltage | 未同步 | **缺失** |
| 存储体 | OnEditBlockData | 未同步 | **缺失** |
| 活塞 | OnEditBlockData | 未同步 | **缺失** |
| 可调延时门 | OnEditBlockDelay | 未同步 | **缺失** |
| 真值表 | OnEditBlockData | 未同步 | **缺失** |
| 电路电压 | OnElectricityPersistentVoltageChanged | 未同步 | **缺失** |

### 实体/AI

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 追逐行为 | OnIsAttackChanged | 未同步 | **缺失** |
| 挖泥行为 | OnIsDigInChanged | 未同步 | **缺失** |
| 觅食行为 | OnIsFeedChanged x3 | 未同步 | **缺失** |
| 鱼跃行为 | OnIsBendChanged | 未同步 | **缺失** |
| 吸引掉落物 | OnPickableAttract | 未同步 | **缺失** |
| 生物音效 | OnPlaySound | 未同步 | **缺失** |

### 状态效果

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 流感 | OnFluUpdate/Effect/Start/Sneeze | 未同步 | **缺失** |
| 疾病 | OnSicknessUpdate | 未同步 | **缺失** |
| 睡眠 | OnSleepEvent/OnWakeUp | 未同步 | **缺失** |
| 生命体征 | OnVitalStatsUpdate | 未同步 | **缺失** |

### 创造模式/管理

| 数据 | 参考项目 | ScMultiplayer | 差距 |
|------|----------|---------------|------|
| 飞行切换 | OnPlayerCreativeFly | 未同步 | **缺失** |
| 手动闪电 | OnManualLightingStrike | 未同步 | **缺失** |
| 手动天气 | OnManualWeatherUpdate | 未同步 | **缺失** |
| 手动时间 | OnManualTimeUpdate | 未同步 | **缺失** |
| 等级/经验 | OnAddExperience | 未同步 | **缺失** |
| 踢出玩家 | 通过 Packet.To 定向 | 未实现 | **缺失** |

### ScMP 独有 (参考未实现)

| 数据 | 说明 |
|------|------|
| ChatMessage | 聊天系统 |
| GameWorldInfoMessage | 世界列表广播 |
| GameWorldInfoMessage1 | 时间同步 |
| GamePakWorldMessage | 首连世界存档下载 |
| Explorer 局域网发现 | 自动扫描房间 |

---

## 三、技术路径对比

### 参考项目: 事件驱动静态注入

```
SubsystemNetwork.BindProject()
  |
  └── 注入 IsNetworkMode 静态字段到游戏类
  └── 订阅游戏类事件 -> Network* 处理函数
        |
        └── 事件触发 -> new XxxPacket() -> QueuePacket()
              |
              └── Server: 广播给所有客户端
              └── Client: 发给服务端

实例:
ComponentHealth.OnHealthInjured
  -> NetworkHealth.OnHealthInjured(health, injury, amount)
    -> if (!IsClient) new InjureEventPacket() -> QueuePacket()
```

### ScMultiplayer: Replace + Poll

```
ScMultiplayer.OnLoad()
  |
  └── EventBus 替换 ComponentInput / SubsystemTerrain / PlayScreen
  └── Client.GameStep 事件 -> 解析 Message -> HandleXxxMessage()
  └── Update() 30fps -> 拉取本机状态 -> SendGamePlayerPositionMessage()

实例:
ScMultiplayer.Update()
  -> if (client.IsConnected) SendGamePlayerPositionMessage()
    -> 读取 SubsystemPlayers.ComponentPlayers[playerIndex]
    -> new GamePlayerPositionMessage() -> Serialize() -> client.SendInput()
```

### 何时选择哪种模式

| 场景 | 推荐模式 |
|------|----------|
| 连续变化的数据 (位置/旋转) | Poll/Send (ScMP 方式) |
| 离散事件 (受伤/死亡/交互) | Event-Driven (参考方式) |
| 方块修改 | Event-Driven ✅ 两者都用 |
| 物品栏变更 | Event-Driven (参考方式) |
| 状态效果 (流/睡眠) | Event-Driven (参考方式) |

---

## 四、ScMultiplayer 可借鉴的设计

### 1. Event-Driven 模式移植

参考项目的 SubsystemNetwork 绑定事件模式可通过 SuAPI 实现:

```
参考: ComponentHealth.OnHealthInjured += NetworkHealth.OnHealthInjured
ScMP: 替换 ComponentHealth 为 SuComponentHealth, override 关键方法, 在合适时机发消息
```

### 2. 包定向 (Packet.To)

参考项目支持定向发送包给指定客户端:
```
new SomePacket() { Except = client, To = client }
```

这是踢人功能的基础。ScMP 对应: Peer.DisconnectPeer(PeerData)

### 3. 地形分块传输

参考项目使用 TerrainChunk 按需传输地形块, 而非整包世界存档:
- 优点: 渐进式加载, 不阻塞
- ScMP 当前: 一次性传输整包 WorldData (PakWorld)

### 4. 状态压缩

参考项目使用 DeflateStream 压缩世界数据:
```
NetworkManager.Compress(stream)
NetworkManager.Decompress(bytes)
```

ScMP 当前无压缩。大世界存档可考虑用 UnityEngine.Compression 或 System.IO.Compression

### 5. 玩家身体网络控制

参考项目通过 NetworkBody.ShouldBeNetworkControlled 判断哪些实体由网络控制:
```
IsClient: 非主玩家 && 无父体 || 玩家本体为空 && 无子玩家
IsServer: 非主玩家 && 无父体 || 玩家本体为空 && 无主玩家子体
```

ScMP 当前通过 PlayerIndex 映射实现, 但未区分骑乘/载具场景的网络控制

---

## 五、ScMultiplayer 实现参考对应表

参考项目的同步功能在 ScMultiplayer 中的实现方式:

| 参考功能 | ScMP 实现路径 | 难度 |
|----------|---------------|------|
| 生命/伤害 | 新建 SuComponentHealth 替换 ComponentHealth + 消息 | 中 |
| 物品栏 | 新建 SuComponentInventory 或 hook SubsystemPlayers.Update | 中 |
| 玩家攻击/挖掘 | hook ComponentPlayer.Update 或 ComponentMiner 事件 | 中 |
| 方块交互 | 替换相应 Subsystem (Chest/Furnace等) | 高 |
| 电路 | 替换相应 Subsystem | 极高 |
| 实体行为 | 替换相应 Component | 极高 |
| 骑乘 | 已部分支持 (GamePlayerPosition) + 需增加事件 | 低 |
| 创造模式 | 替换 ComponentGui 或 hook 相关方法 | 中 |
| 睡眠 | 替换 ComponentSleep | 低 |
| 状态效果 | 替换对应 Component | 中 |
| 踢人 | Peer.DisconnectPeer(PeerData) | 低 |
| 皮肤 | 替换 ComponentClothing | 低 |

---

## 六、优先级总结

### 低成本高收益 (优先)
1. 踢人 (Peer.DisconnectPeer) — 接口已可用
2. 生命值同步 — 替换 ComponentHealth, 单文件改动
3. 玩家热部署同步 (Active Slot) — GamePlayerPosition 已含手持信息
4. 睡眠同步 — 单一 Component

### 中等成本
5. 物品栏同步 — hook Inventory 变更
6. 玩家交互事件 (攻击/挖掘/使用) — hook ComponentPlayer
7. 状态效果 (流/疾病/生命体征) — 各一个 Component
8. 骑乘事件 — 扩展现有逻辑

### 高成本
9. 方块交互 (箱子/熔炉/工作台) — 涉及多 Subsystem
10. 电路同步 — 复杂度高
11. 实体/AI — 需要确定性同步
12. 地形分块传输 — 需改架构
