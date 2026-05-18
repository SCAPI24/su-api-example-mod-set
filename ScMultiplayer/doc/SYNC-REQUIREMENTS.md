# 联机同步需求分析

> 目标: 单设备多人 -> 多设备局域网联机
> 网络中间件: Comms (Comms.Drt Tick 驱动)
> 参考: SurvivalcraftNet (https://gitee.com/SC-SPM/SurvivalcraftNet)
> 注意: 参考项目改源码, ScMultiplayer 用 SuAPI Mod 方式, 技术路径不同

---

## 一、同步模型选择

### SurvivalcraftNet: 输入同步 (确定性模拟)
- 所有客户端运行完整游戏逻辑
- 仅同步 PlayerInput
- 游戏逻辑必须完全确定性

### ScMultiplayer (当前): 状态同步
- Host 作为权威
- 同步位置/方块/时间等状态数据
- 每个客户端独立更新自身

### 推荐: 混合模式
- **本地玩家**: 状态同步 (发送位置等信息)
- **方块等关键数据**: Host 权威 + 远程执行
- **可引入输入同步**: 利用已有 GamePlayerInputMessage 减少带宽

---

## 二、同步数据详细分析

### A 类: 高频实时 (每帧/Tick)

| 数据 | 类型 | 当前 | 带宽估算 | 优化 |
|------|------|------|----------|------|
| 玩家位置 | Vector3 | Reliable 30fps | ~12 bytes/frame | UnreliableSequenced |
| 玩家旋转 | Quaternion | Reliable 30fps | ~16 bytes/frame | 可压缩为 3 floats |
| 玩家速度 | Vector3 | Reliable 30fps | ~12 bytes/frame | 预测用, 可省略 |
| 视角角度 | Vector2 | Reliable 30fps | ~8 bytes/frame | 量化压缩 |
| 蹲/飞/骑 | bool x3 | Reliable 30fps | ~3 bytes/frame | OK |

**优化方向**: GamePlayerPosition 改用 UnreliableSequenced + 添加序列号 + 客户端插值

### B 类: 状态变更 (事件驱动)

| 数据 | 触发条件 | 优先级 | 实现难度 |
|------|----------|--------|----------|
| 生命值 | ComponentHealth.Health 改变 | **高** | 低 - Hook Update |
| 物品栏 | Inventory 槽位变化 | **高** | 中 - 差分同步 |
| 玩家死亡 | Health <= 0 | **高** | 低 - 事件触发 |
| 玩家重生 | 死亡后重生 | **高** | 中 - 状态恢复 |
| 方块修改 | Terrain.ChangeCell | **已实现** | 已验证 |
| 箱子/熔炉 | ComponentInventoryBase 交互 | 中 | 高 - 复杂状态 |
| 门/活板门 | 开关状态 | 中 | 中 - 状态追踪 |
| 家具放置/破坏 | Furniture 操作 | 中 | 高 - 多子系统 |
| 掉落物生成 | 挖方块/杀生物 | 中 | 中 - Pickable |
| 实体 AI | 动物移动 | 低 | 极高 - 确定性 |

### C 类: 世界状态 (低频)

| 数据 | 当前 | 优先级 |
|------|------|--------|
| 时间 | 已同步 (GameWorldInfo1) | 已验证 |
| 天气 | 未同步 | 中 |
| 游戏模式 | 未同步 | 中 |
| 世界存档 | 已同步 (GamePakWorld) | 已验证 |

### D 类: 元数据

| 数据 | 当前 | 优先级 |
|------|------|--------|
| 聊天 | 已同步 | 已验证 |
| 玩家加入通知 | 已同步 (GameStep) | 已验证 |
| 玩家离开通知 | 已同步 (GameStep) | 已验证 |
| 玩家列表 | 未实现 UI | 高 |
| 踢出玩家 | 未实现 | **高** (需求明确) |

---

## 三、延迟补偿方案

### 方案 A: 客户端预测 + 服务端和解 (推荐但复杂)

输入 -> 本地立即预测 -> SendInput
                 |
                 v
Server 权威模拟 -> 广播状态 -> Client 收到 -> 对比预测 -> 回滚/插值

**复杂度**: 高 (需要可回滚的游戏状态快照)

### 方案 B: 服务端权威 + 客户端插值 (中等)

本机输入 -> SendInput -> Server -> 权威模拟 -> 广播位置
远程玩家位置 -> 缓冲 2-3 帧 -> 线性插值

**复杂度**: 中 (当前最接近的方案, 优先实施)

### 方案 C: 纯状态同步 (当前, 最简单)

本机 -> 广播状态 -> 远程 -> 直接设置

**问题**: 远程玩家位置跳变 (无插值), 网络抖动影响明显

---

## 四、开发路线图

### Phase 1: 核心稳定 (当前)
- [x] 玩家位置/旋转同步
- [x] 方块修改同步
- [x] 世界时间同步
- [x] 世界存档下载
- [x] 聊天消息
- [ ] 位置同步优化 (UnreliableSequenced + 插值)
- [ ] 断线处理 (Disconnected 事件)

### Phase 2: 功能完善
- [ ] 玩家生命值同步
- [ ] 物品栏同步 (核心物品)
- [ ] 踢出玩家 (Peer.DisconnectPeer)
- [ ] 玩家列表 UI
- [ ] 聊天 UI 改进
- [ ] 掉落物同步 (基础)

### Phase 3: 体验优化
- [ ] 延迟补偿 (方案 B 插值)
- [ ] 实体同步 (动物基础移动)
- [ ] 门/家具基础同步
- [ ] 天气同步
- [ ] 死亡/重生处理

### Phase 4: 完善
- [ ] 客户端预测 (方案 A, 可选)
- [ ] 断线重连
- [ ] 状态校验 (DesyncDetection)
- [ ] 互联网联机
- [ ] 玩家皮肤同步
