# 玩家领地（区域认领 + 归属执法）设计方案

> **状态：设计稿，未实现。** 需求分两轮确认（① 语义与执法 ② GmMod 面板交互与显示），
> 本文件是"规划稿"：实现前先按 §7 的 P1→P7 逐段落地，每段三端同包部署 + 实测。
> 相关：`DEV-RULES.md`（打包/混淆/行尾铁律）、`SESSION-ISSUES-2026-09-23.md`（联机同步的既有教训）。

---

## 0. 需求要点（三轮确认）

**第一轮（语义与执法）**
- 客户端可以选中一个区域并显示边框线；
- 区域选择作为一条**指令**经工具（GmMod 等）上传，**主机审核通过后**把区域归给对应用户；
- 归属后：**只有拥有者**能修改该区域内的东西，其他人造成的修改**全部无效**；
- 其他人：不能放方块、不能挖方块、不能拾取里面的掉落物、不能点燃或用其它方式破坏里面的方块；
- 但**可以交互**：门、活板门、电路中的开关/按钮/拉杆、家具；
- "挖掘无效"的实现思路参考**游戏冒险模式**（引擎是在行为层直接短路）。

**第二轮（GmMod 面板交互与显示）**
- 在 GmMod 里选择"玩家领地"功能后，是**多个功能面板**组成的完整交互；
- 选点：**点1 / 点2**，可以"取消全部点"；点1/点2 各自既能"**设置点**（以角色所在位置为点）"，
  也能"**填写点**"（手填坐标）；两点确定平面范围后，**高度默认 0–255（全高）**；
- 完成后该区域以**高亮线框**显示：颜色取自**该角色在主机上的序号**，按**导线颜色顺序**（paint 调色板 16 色）；
  此阶段**不填充面**；
- **区域展示**：为每个"已完成设置"的区域在**外侧填充面**（与线框同色），并在**除底面外的其余 5 个外表面**
  绘制**区域编号**；
- **玩家标记按 userId 跟随**；玩家的 userId 上**追加区域编号**表示拥有；
- **建立领地**：把当前选中的区域登记为领地（默认显示，之后可用"区域展示"再显示）；
- **赋予领地**：列表选择角色 + 输入领地号 ⇒ 把领地给该玩家；**一个领地可赋予多个玩家**；
- **剥夺领地**：选择玩家 + 输入领地号 ⇒ 移除该玩家对该领地的许可；
- **领地设置**：输入领地号 ⇒ 列出该领地属于哪些玩家 ⇒ 选中某玩家 ⇒ 剥夺 / 回收；
- **放弃领地**：输入领地号 ⇒ 整块领地移除。

**第三轮（决策确认，2026-09-24）——以下为锁定结论**
1. **指令写入联机 Mod（ScMP）**；领地数据落盘 **`ScMultiplayerRegions.xml`**（按世界保存）。
2. **领地检测全部在主机端**：默认其他人无法修改；**即使被改动也要否决、并把改动改回去**，
   同时**通知发起修改的人**（避免"各端看到的不一致"）。
3. **允许重叠**，**重叠格归编号最小者**：选点时若落在别的领地内，最终计算时把面积内那部分
   判给**编号更小**的那个领地。
4. **单体上限 64×64×256**：X/Z 各自最大 64（**矩形也行**，不要求正方形），高度上限 256（默认 0–255）。
5. **容器/熔炉/告示/农田**：**打开查看 = 交互**（放行）；**取出/放入 = 修改**（拒绝）。
6. **增删权限只在 GM/主机**：建立 / 放弃 / 赋予 / 剥夺 / 回收**全部走主机配置**，玩家自己不能增删。

**第四轮（补充确认，2026-09-24）——同样锁定**
7. **自然变化不冻结**（作物生长、仙人掌生长、腐坏、落叶照旧），**但要避免爆炸、点燃、水流造成的破坏**。
8. **领地总量上限只设世界上限，默认 128**（不设每人上限）。
9. **玩家/生物对领地内角色/实体的伤害不受限**（领地只保护方块与掉落物）。
10. **预览画法**：默认除全高线框外，**再画"地面矩形 + 顶部矩形"**加强可读性（存储仍为全高）。

---

## 1. 术语与数据模型

| 术语 | 含义 |
|---|---|
| 区域/领地（Claim） | 轴对齐长方体，整数格闭区间 `[Min,Max]`，高度默认 `Min.Y=0, Max.Y=255` |
| 领地编号 | 世界内自增整数（从 1 开始），**全局唯一**，玩家用编号引用领地 |
| 拥有者列表 | `List<UserId>`（**不是** clientId！按 userId 跟随；一个领地可多拥有者） |
| 拥有者标记色 | 由"该 userId 在主机上的角色序号"经 **paint/导线调色板**（16 色）取值，序号 % 16 |
| 选区（Pending） | 尚未登记的临时长方体（点1+点2+全高），只在本端显示 |
| 上限 | 单体 **64×64×256**：`|MaxX-MinX|+1 ≤ 64`、`|MaxZ-MinZ|+1 ≤ 64`、Y 范围 ≤ 256（矩形亦可） |
| 重叠裁决 | **重叠格归编号最小者**：`OwnerAt(cell)` 取所有覆盖该格领地中 **Id 最小**的那个 |
| 数量上限 | **只设世界上限，默认 128 块领地**（不设每人上限） |

记录格式（世界级，落盘 `ScMultiplayerRegions.xml`，与 `ScMultiplayerPlayers.xml` 同款）：
```xml
<Regions NextId="7">
  <Region Id="3" MinX=".." MinY="0" MinZ=".." MaxX=".." MaxY="255" MaxZ=".."
          CreatedUtc=".." Name=".." Flags="..">
    <Owner UserId=".." Name=".." />
    <Owner UserId=".." Name=".." />
  </Region>
</Regions>
```
- 主机持有权威副本；客户端持有**只读副本**（用于绘制 + 可选本地预判）。
- 同步：加入时随世界快照一次性下发；运行期广播增量（Add/Remove/Replace），带**单调序号**，按序号应用。

---

## 2. 功能树（GmMod 面板）

### 2.1 树

```
玩家领地
├─ 选区
│  ├─ 点1
│  │  ├─ 设置点1                ← 以当前角色所在方块坐标作为点1
│  │  ├─ 填写点1 (X,Y,Z)        ← 手填/微调
│  │  └─ 当前点1: (x,y,z)       ← 只读显示
│  ├─ 点2
│  │  ├─ 设置点2
│  │  ├─ 填写点2
│  │  └─ 当前点2: (x,y,z)
│  ├─ 取消全部点                ← 同时清空点1/点2与预览框
│  └─ 预览（两点齐备后自动）
│     ├─ 范围: Xa..Xb / Za..Zb 高度 0..255
│     └─ 高亮线框（本端角色→主机序号→导线颜色），不填充面
├─ 区域展示（开关）
│  ├─ 已完成领地: 外侧面填充（同色）+ 5 个外表面绘制领地编号
│  └─ 选区预览线框（可选，默认开）
├─ 建立领地                     ← 把当前选区登记为领地，分配编号；默认显示
├─ 赋予领地                     ← [列表选角色] + [输入领地号] ⇒ 加入该领地拥有者列表
├─ 剥夺领地                     ← [选择玩家] + [输入领地号] ⇒ 从拥有者列表移除
├─ 领地设置                     ← [输入领地号] ⇒ 列出拥有者 ⇒ 选中玩家 ⇒ 剥夺 / 回收
└─ 放弃领地                     ← [输入领地号] ⇒ 整块领地移除（二次确认）
```

### 2.2 叶子行为规格

| 叶子 | 触发 | 行为 | 校验/失败反馈 |
|---|---|---|---|
| 设置点1/2 | 点按 | 取角色 `ComponentBody.Position` 所在方块（取整）为点 | 无世界/无角色时置灰 |
| 填写点1/2 | 输入三个整数 | 写入该点 | 越界（Y 不在 0–255）报错 |
| 取消全部点 | 点按 | 清空两点 + 预览框 | — |
| 预览 | 自动 | 两点齐备即画线框（含"高度 0–255"提示行） | 两点重合 → 视作非法，提示 |
| 区域展示 | 开关 | 对所有**已完成**领地：外侧面填充 + 5 面编号；可只显示自己/全部 | 距离/数量超阈值时只画线框（性能） |
| 建立领地 | 点按 | 把当前选区登记为领地（分配编号），默认立即显示 | **超上限（64×64×256）→ 拒绝并提示**；与已有领地重叠 → **允许**，重叠格后续归编号最小者 |
| 赋予领地 | 选角色 + 输入编号 | 把该领地加入该角色 userId 的拥有列表（可多人共享） | 角色不存在/编号不存在 → 提示 |
| 剥夺领地 | 选玩家 + 输入编号 | 从该领地拥有列表移除该 userId | 该玩家本就没有 → 提示 |
| 领地设置 | 输入编号 | 列出拥有者；选中玩家后可"剥夺"或"回收" | 编号不存在 → 提示 |
| 放弃领地 | 输入编号 | 整块领地移除（玩家许可随之失效），二次确认 | 编号不存在 → 提示 |

**权限（锁定）**：建立/放弃/赋予/剥夺/回收**只有 GM/主机可用**，全部经**主机配置**下发；
普通玩家不提供这些入口（面板按权限置灰）。

**分工建议**：菜单面板放 **GmMod**（它已是纯客户端 UI，且 GM 操作入口就在那里）；
区域状态、选区/预览/展示的 3D 绘制、指令与执法放 **ScMP**（状态与权威都在它那边），GmMod 通过公开接口驱动。

---

## 3. 交互与显示规则

1. **选区**：点1+点2 决定 X/Z 范围，Y 固定 0–255；界面同时显示数值文本（便于核对）。
2. **选区线框**：12 条棱，`FlatBatch3D.QueueLine`；颜色 = 本端角色的**主机侧序号** → paint 调色板（16 色，序号 % 16）；
   此阶段**不填充面**；另**默认再画"地面矩形 + 顶部矩形"**（Y=0 与 Y=255 两圈）加强可读性（存储仍为全高）。
3. **区域展示**：
   - 外侧面填充：与线框同色、低透明度；
   - **编号**：在除底面外的 5 个外表面中央绘制（用各自的 right/down 向量 + 略微外偏，参考
     `CircuitAutoRouter` 的 `DrawNumbers`），字号约 0.04；底面不画；
   - 编号与颜色**跟随 userId**：玩家换设备/换 clientId 后颜色与归属不变。
4. **性能**：填充面只在"区域展示"开启时绘制；按距离/数量分级（近处画填充+编号，远处只画线框，
   更远不画），避免大量领地时掉帧。

---

## 4. 指令与审核链路（复用现成通道）

沿用 ScMP 已有的 **DataModification 通道**（已有策略 `DataModificationPolicy{Default,Allow,Reject}`、
待审队列 `GetNextPendingApproval()/ResolveApproval()`、超时与队列满处理、`ApprovalRequested` 事件、主机审计日志）：

| 指令 | 参数 | 说明 |
|---|---|---|
| `ScMP.Region.Claim` | `name?, x1,y1,z1, x2,y2,z2` | 建立领地（仅 GM/主机） |
| `ScMP.Region.Grant` | `regionId, userId/targetClientId` | 赋予（仅 GM/主机） |
| `ScMP.Region.Revoke` | `regionId, userId/targetClientId` | 剥夺（仅 GM/主机） |
| `ScMP.Region.Drop` | `regionId` | 放弃整块领地（仅 GM/主机） |
| `ScMP.Region.List` | — | 查询（调试/面板刷新） |

- **指令实现在联机 Mod（ScMP）内**，GmMod 面板只是调用方；
- 主机 `apply` 内校验：坐标合法性、**单体上限 64×64×256**、领地数量上限、目标玩家是否存在；
  **重叠不拒绝**（重叠格按"编号最小者"裁决）；通过后写记录 → `PublishServerAudit("region.claim"/"region.grant"/…)` → 广播增量；
- **增删类指令要求 GM/主机身份**（现有能力位/审核策略 + 主机配置开关），主机是无头端 ⇒ 审核走既有
  DM 审核队列 + 控制台命令，不需要图形界面。

---

## 5. 主机侧执法点（统一入口 `RegionOwnership`）

新增服务：`OwnerAt(cell)` / `CanDig|CanPlace|CanPickup|CanIgnite(clientId, cell)`，在既有挂点调用：

| 行为 | 现有挂点 | 处理 |
|---|---|---|
| 挖方块 | 挖掘请求 handler + `SuSubsystemTerrain` 应用 | 拒绝 + 走现有"挖掘结果拒绝/回滚"路径 + 提示"这是 X 的领地" |
| 放方块 | 放置请求/执行链路（现有 place request/result） | 同上 |
| 拾取掉落物 | `HandlePickableAcquireRequest` | 拒绝（掉落物不消耗） |
| 点燃/火 | `SuSubsystemFireBlockBehavior.SetCellOnFire` + 打火石/火柴交互 | 拒绝引燃 |
| 爆炸 | `SuSubsystemExplosions` | 目标格属他人领地 → **不破坏**（含燃烧弹不引燃） |
| 点燃/火蔓延 | `SuSubsystemFireBlockBehavior` | 领地内**不允许被点燃、火不蔓延进来** |
| **水流/岩浆流** | **`SubsystemFluidBlockBehavior`（需新增替换 `SuSubsystemFluidBlockBehavior`）** | 流体**不得冲毁/覆盖**领地内的方块（进入领地边界即视为破坏，拦在源头） |
| 方块自演化 | 已替换的 7 个 `SuSubsystem*BlockBehavior`（仙人掌/腐坏/地毯/落叶/耕地/常春藤/树苗） | **不冻结**（自然生长照旧，锁定结论 7） |
| 活塞/发射器 | `SuSubsystemPistonBlockBehavior`、`SuSubsystemDispenserBlockBehavior` | 推入/放入他人领地 → 拒绝 |
| 电路操作 | 开关/按钮/拉杆/门 | **允许**（白名单） |
| 家具交互 | `ComponentMiner.Interact` 路径 | **允许**；容器**打开查看放行、取出/放入拒绝** |

- **必须同时覆盖客户端预测**（否则出现"本地挖掉了、主机没变"的抖动）：挖/放已有拒绝回滚，
  拾取已有 pending/result 模式，直接复用。
- **否决 + 改回 + 通知（锁定）**：主机一律不应用越权修改；若某次修改已经落到权威状态
  （竞态/绕过），主机把它**改回**原值并用现有的地形同步/修复通道把正确值推给所有客户端，
  同时**通知发起修改的人**（一行"该区域属于 X（领地 #n），修改已否决"）。这样各端看到的状态始终一致。
- 执法风格照引擎冒险模式（`ComponentMiner` 冒险模式挖矿时间直接 `float.PositiveInfinity`、
  行为子系统按条件短路）：**在行为层短路 + 明确提示**，而不是只把界面按钮置灰。

---

## 6. 允许的"交互"白名单（已锁定）

**允许（交互）**：门/活板门/栅栏门/电梯门、开关/按钮/拉杆/电路元件操作、家具 Use（椅子、床等）、
**容器/熔炉/告示/农田的打开查看**。
**按"修改"处理（拒绝）**：破坏、放置、点燃、爆炸、活塞推挤，以及**容器/熔炉的取出/放入**。
（"打开查看 = 交互、取出放入 = 修改"是 2026-09-24 的锁定结论。）

---

## 7. 分阶段实施（每阶段独立三端部署 + 实测）

| 阶段 | 内容 | 验收 |
|---|---|---|
| **P1** | 选区（点1/点2/取消/预览线框）+ 配色（主机序号→导线色） | ✅ 2026-09-24 两端都能看到线框（平板 3×6、PC 12×5；颜色=玩家在主机的角色序号→paint 调色板，主机端 0 号=白） |
| **P2** | 领地区域数据结构 + 落盘 + 下发/增量同步（暂不执法） | ✅ 2026-09-24 建立后重进世界仍在（`Loaded 1 region claims`），客户端随快照拿到整表、运行期 add/remove 增量按序号落地（详见 §11） |
| **P3** | GmMod 面板（功能树 §2）+ `ScMP.Region.*` 指令（**仅 GM/主机**）+ 主机配置开关 + 审计 | ✅ 2026-09-24 主机侧面板（建立/赋予/剥夺/领地设置/放弃，二次确认+返回，见 §12）；**客户端提交已闭环**：`ScMP.Region.*` 经 DM 审核通道由主机落地，PC 端自建领地 `#21 X -169..-169 Z 47..47`（见 §17） |
| **P4** | 区域展示：外侧面填充 + 5 面编号（跟随 userId）；重叠格按"编号最小者"着色 | ✅ 2026-09-24 填充/编号/开关/重叠裁决已落地并两端可见（§13）；**编号口径已按用户决定改为"只保留贴地那一份"** |
| **P5** | 执法第一批：挖 + 放（影响最大） | ✅ 2026-09-24 **挖+放都已实测**：越权挖被拒 `（dig）`（§14）、越权放被拒 `（place）`（§27，客户端在主机领地 `#26` 内手持泥土右键 ⇒ `这块区域属于 Android User（领地 #24），修改已否决（place）`）；无主区挖/放照常放行。**只剩主机本机玩家的挖/放未拦**（属 §5 的"下一批"） |
| **P6** | 执法第二批：拾取 + 点燃/火 + 爆炸（**不破坏、不引燃**） | ✅ 2026-09-24 **三项全部实测闭环**：拾取 `（pickup）`（§16）、爆炸（领地内 7 次被拦 + 无领地对照正常炸，§18/§22）、火（火只在领地外产生、领地内 0 格火且方块不变，§32） |
| **P7** | 执法第三批：主机本机挖/放与拾取 + **`SuSubsystemFluidBlockBehavior` 拦截水流/岩浆** + 活塞/发射器 + 越权"改回并通知"闭环；文档收尾 | 🟡 2026-09-25 **P7a ✅**（`SuComponentMiner` 替换；他人领地内挖被拒 + 本人收到 `修改已否决（dig）`；无主区照常，§35）；**P7b ✅**（`SuSubsystemPickables` 拾取前挪出范围 + 通知；经查证"他人领地内存在掉落物"在现有执法下不可达 ⇒ 纵深防御，§36）；**P7c ✅**（`SuSubsystemWaterBlockBehavior` / `SuSubsystemMagmaBlockBehavior` 整型替换 + `SuFluidClaimGuard` 改回式守卫；三面实测齐备：界外水停在 #29 西界（未截断扫描界内 0 格）+ 主机 `已否决（fluid）`、界外自由铺开、拥有者在自己领地内放水不冻结；§37 实现 / §38 口径细化 / §39 实测）/ **P7d 🟡**（活塞：`SuSubsystemPistonBlockBehavior` 主机分支"改回式"；发射器：`SuDispenserElectricElement.Simulate` 行为层短路 + 通知；口径均"只挡从领地外推入/放入"；代码落地 + 构建 0 错误，实机实测待做，§40） |

---

## 8. 决策记录

**已锁定（2026-09-24）**
| # | 项 | 结论 |
|---|---|---|
| 1 | 指令所在 | **写入联机 Mod（ScMP）**；GmMod 只做面板调用方 |
| 2 | 数据落盘 | **`ScMultiplayerRegions.xml`**（世界级，与 `ScMultiplayerPlayers.xml` 同款） |
| 3 | 检测位置 | **全部在主机端**；越权修改一律否决；**已落地的改动要改回**并**通知发起者**，保证各端一致 |
| 4 | 重叠 | **允许**；**重叠格归编号最小者** |
| 5 | 单体上限 | **64×64×256**（X/Z 各 ≤64，矩形亦可；高度 ≤256，默认 0–255） |
| 6 | 容器/熔炉/告示/农田 | **打开查看 = 交互**（放行）；**取出/放入 = 修改**（拒绝） |
| 7 | 增删权限 | **只有 GM/主机**能建立/放弃/赋予/剥夺/回收；**全部走主机配置** |
| 8 | 越权提示 | **给**（一行"该区域属于 X（领地 #n），修改已否决"） |
| 9 | 自然变化 | **不冻结**（作物/仙人掌/腐坏/落叶照旧）；但**爆炸、点燃、水流不得造成破坏** |
| 10 | 数量上限 | **只设世界上限，默认 128**；不设每人上限 |
| 11 | 伤害 | **不受限**（领地只保护方块与掉落物，不保护角色/实体） |
| 12 | 预览画法 | 全高线框 + **地面矩形 + 顶部矩形**（存储仍为全高） |

**仍待定**：无。（实现细节如线框粗细、填充透明度、编号字号等按 §3 的建议值先做，实测后再调。）

---

## 9. 参考实现与代码落点

- **选区/绘制参考**：`Mod/CircuitAutoRouter/SubsystemCircuitRouter.cs`
  —— `SubsystemBlockBehavior + IDrawable + IUpdateable`、`ComputeSelectionBox()`、`BoundingBox`、
  `m_primitivesRenderer3D.FlatBatch()` + `flatBatch.QueueLine(...)`、`FontBatch3D` 面朝向数字（`DrawNumbers`）。
- **导线/paint 颜色顺序**：`Survivalcraft/Game/WireBlock.cs` 用 `SubsystemPalette.GetColor(environmentData, paintColor)`，
  即 paint 调色板 16 色顺序（实现时按该调色板取值，序号 % 16）。
- **冒险模式的"挖不动"**：`Survivalcraft/Game/ComponentMiner.cs`（冒险模式下无 ≥2 级对应工具时
  `digResilience = float.PositiveInfinity`）；`SubsystemRotBlockBehavior` / `SubsystemDispenserBlockBehavior`
  / `SubsystemSignBlockBehavior` 均按 `GameMode.Adventure` 短路 —— 我们的执法沿用这种"行为层短路"风格。
- **审核通道**：`Mod/ScMultiplayer/DataModification/DataModificationCoordinator.cs`
  （`GetNextPendingApproval` / `ResolveApproval` / `IsApprovalPending`）、`DataModificationContracts.cs`
  （操作名常量、`DataModificationPolicy`、`DataModificationApprovalRequest`）。
- **执法挂点**：`Modules/Terrain/ScMultiplayerTerrainHandlers.cs`（挖掘/地形修复）、
  `Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs`（拾取）、`Modules/World/ScMultiplayerWorldSync.cs`
  （放置/掉落物）、`Modules/Session/ScMultiplayerClientEvents.cs`（放置执行/击中结果）、
  `Func/Subsystem/SuSubsystem*`（火、爆炸、各类方块行为、活塞、发射器）。
- **世界级存储参考**：`ScMultiplayerPlayers.xml` 的读写路径（`ScMultiplayerProfileHandlers` /
  `ScMultiplayerPlayerAdministration`），新文件 `ScMultiplayerRegions.xml` 同款。

---

## 10. 测试拓扑与实现挂点（2026-09-24 实测确认）

**测试拓扑（本功能专用）**：只用**平板 + Windows**，**平板当主机**、**Windows 当客户端**，
**不经过远程服务器**（远程服务器与三端部署流程本功能不需要）。
- 平板载入自己的本地测试图（`Francenira` / `Vidiango` 等）即**自动开房** ——
  配置项 `autoCreateRoomFromCurrentWorld: true`，平板日志出现
  `CreateGame attempt … / GameCreated, ClientID=0` 即为房主；Windows 端在"游玩"列表里能看到该房间条目。
- 实测（2026-09-24）：平板 `GameCreated, ClientID=0` → Windows `GameJoined, ClientID=2`，
  世界仅 63KB、0.32 秒传完；两端构建指纹一致（`build b2b61464d4e8`），
  两端玩家坐标相邻（-163.58 / -163.97，`y/z` 相同）。
- ⚠️ **首次加入需要在"玩家设置"屏提交角色档案**（选性别/名字/皮肤后点"开始游戏！"），
  否则主机会以 `SCMP_PROFILE_REQUIRED` 拒绝（判据：`IsValidRequestedProfile(worldInfo)`，
  见 `ScMultiplayerPlayerHealthAndIngress.cs` 约 915 行）。
- 允许自由重启 Windows / 平板上的游戏端；平板上所有地图都是测试图，可随意修改。

**3D 绘制挂点（P1 用）**：`Survivalcraft/Game/SubsystemDrawing.cs` 收集可绘制对象时，
除 `Project.FindSubsystems<IDrawable>()` 外**还会收集实体组件里的 `IDrawable`**（第 60-68 行）。
⇒ 选区/展示的线框可以用一个**挂在玩家实体上的 `Component, IDrawable`** 来实现
（与本仓库 `WatchMod` 的"ComponentTemplate 注册独立 Component 挂到 Player"同一套做法），
**不必新增子系统**、也不必改 `Pak/Database.xml` 的子系统节点。
- 画线：`m_primitivesRenderer3D.FlatBatch()` + `flatBatch.QueueLine(a, b, color)`（参考 `CircuitAutoRouter`）；
- 面朝向数字：`FontBatch3D` + 每面各自的 right/down 向量（参考 `CircuitAutoRouter.DrawNumbers`）；
- 颜色：`WireBlock` 用的 `SubsystemPalette.GetColor(...)` 即 paint 调色板 16 色顺序。

---

## 11. P1 / P2 实测记录（2026-09-24，平板=主机 + Windows=客户端）

**P1（选区线框）**
- 平板（主机）：点1/点2 齐备后菜单显示 `○ 选区范围：3 × 6（高度 0–255）`，世界里出现**全高线框**
  （4 条竖直棱 + 地面/顶部矩形两圈；截图见会话记录），颜色为 paint 调色板 0 号（白）。
- Windows（客户端）：同样流程得到 `12 × 5`，线框可见；两端都是 0 号色（主机=本地、第一个客户端
  被分配的 `PlayerIndex` 也是 0）——**同一个序号 → 同色**，符合"颜色随主机侧角色序号"的口径。
- 实现：状态与绘制在 ScMP（`Modules/Player/ScMultiplayerRegionSelection.cs` +
  `Func/Component/SuComponentRegionOverlay.cs`，挂在 Player 实体上的 `Component, IDrawable`），
  面板在 GmMod，跨程序集走静态门面 `RegionSelectionApi`。

**P2（数据结构 / 落盘 / 同步）**
- 记录文件（平板世界目录）：`ScMultiplayerRegions.xml`
  ```xml
  <Regions Version="1" NextId="4" Sequence="5">
    <Region Id="1" MinX="-177" MinY="0" MinZ="19" MaxX="-166" MaxY="255" MaxZ="20" CreatedUtc="…" Name="" Flags="">
      <Owner UserId="5baf52e6-…" Name="Android User" />
    </Region>
  </Regions>
  ```
  `Owner.UserId` = `UserManager.ActiveUser.UniqueId`（账号 userid），**不用 clientId**。
- 重进世界：平板重启游戏重新载入世界 → 主机日志 `Loaded 1 region claims (nextId=2, sequence=1)`。
- 加入下发：主机 `Region claims sent to ClientID=1: count=1, sequence=3`（加入完成推送 +
  客户端 `RequestSync` 答复各一次）→ PC `Region claims synced from host: count=1, sequence=3`。
- 运行期增量：
  - 建：主机 `Region claim add: #3, sequence=4, count=2` → PC `Region claim replica updated from host: add #3, sequence=4, count=2`；
  - 删：主机 `Region claim remove: #3, sequence=5, count=1` → PC `… remove #3, sequence=5, count=1`；
  - 编号只增不复用：放弃 #2 后再建立拿到 #3（`NextId=4`）。
- 权限：客户端面板在「建立领地」里显示 `✗ 本端不是主机…`，确认建立后两端数量都不变（未落地）。
- 已知未覆盖与坑见 `SESSION-ISSUES-2026-09-24.md`（跨程序集混淆、枚举 `ToString`、子菜单返回项、
  增量自愈、CmdBridge 在 Windows 上点列表行的正确姿势）。

---

## 12. P3（主机侧面板）实测记录（2026-09-24）

面板功能树（`玩家领地` 子菜单 → `领地 #n（领地设置）`）：

- 领地详情列出 **拥有者（n）**，每个拥有者一行「👤 名字（点开可剥夺）」；
- 「赋予领地给玩家…」→ 在线玩家列表（`DataModificationTool.DescribeOnlinePlayers()`，含主机自己与各网络化身）；
- 「放弃这块领地（主机）…」→ 二次确认；
- 每个子菜单都有「← 返回…」。

实测（07:38–07:39，平板=主机、Windows=客户端）：

| 动作 | 证据 |
|---|---|
| 赋予 | 主机给 `[client 1]` 赋予 → XML 出现第 2 个 `<Owner UserId="e45dcd8e-…" Name="Basil" />`；主机日志 `Region claim replace: #1, sequence=6` |
| 客户端副本 | PC `Region claim replica updated from host: replace #1, sequence=6`，面板显示 `拥有者（2）`（Android User + Basil） |
| 剥夺 | 主机点某个拥有者 → 「剥夺该玩家的领地许可」→ 日志 `#1 owner revoked: Android User` + `replace #1, sequence=7`；PC 面板随之回到 `拥有者（1）` |
| 仅主机 | PC 上执行「赋予」被拒（`只有主机/GM 可以赋予领地`），两端拥有者数量不变 |

拥有人数上限/服务器上限、以及**客户端提交走 DM 审核通道**（`ScMP.Region.*` 操作名 + 主机审批 + 审计 +
主机配置开关）见 P3 后续与 `SESSION-ISSUES-2026-09-24.md` §6。

---

## 13. P4（区域展示）实测记录（2026-09-24）

**实现**
- 裁决：`OwnerClaimAt(x,y,z)` = 覆盖该格的**编号最小**领地（设计稿 §1 锁定结论 3），
  面板「所在 (x,y,z) 归属：…」直接读它（执法 P5–P7 复用同一入口）；
- 配色：`RegionClaimPaletteIndex(claim)` = 拥有者 userId 的 **FNV-1a 稳定散列 % 16** → paint 调色板
  （不用 `string.GetHashCode()`：它每个进程都不同，做不到"跨会话稳定"）；
- 绘制（`SuComponentRegionOverlay`）：12 条棱 + **除底面外的 5 个外表面**半透明填充（alpha 48）
  + **4 个侧面贴地编号**（按该面中心列的**地表高度** +1.2 定位，地表高度按"领地+列"缓存 1 秒）；
  字体**绘制期懒加载**（`Load()` 里取不到，见问题清单 6.1）；
  > 编号口径修正（用户 2026-09-24 决定）：设计稿原写"5 个外表面中央 + 字号约 0.04"，
  > 但领地全高 0–255 ⇒ 面中心在 y≈128，站地上几乎看不见（截图实证）。**只保留贴地那一份**，
  > 字号仍用 0.04；顶面与面中心不再画。
- 分级：按到玩家的距离排序，近处 6 块画填充+编号，其次 18 块只画棱，其余不画；
- 开关：面板「区域展示：开/关」「选区预览线框：开/关」（建立领地后默认显示）。

**实测**
| 项 | 证据 |
|---|---|
| 重叠裁决 | 同一选区连建 #4、#5；站进去读「所在 (-191,63,16) 归属：**#4**」（编号最小者）✓ |
| 开关 | 点「区域展示」→ 菜单显示"关"，世界里填充/编号消失；再点回"开" |
| 填充 | 平板与 **PC 客户端**截图都能看到半透明外侧面（客户端的副本数据自己渲染） |
| 编号 | **贴地**（该面中心列地表 +1.2）上看得到编号；平板与 **PC 客户端**截图各一份（站在面外约 10 格、水平视角） |
| 同步 | PC 日志 `Region claim replica updated from host: add #4 / add #5` |
| 落盘 | `ScMultiplayerRegions.xml`：`NextId="6" Sequence="9"`，含 #1(Basil) / #4 / #5(Android User) |

---

## 14. P5（执法第一批：挖 / 放）实测记录（2026-09-24）

**代码（已部署，两端同包）**
- 统一入口 `CanRegionModifyCell(clientId, cell, out claim, out reason)`（`Modules/Region/ScMultiplayerRegionEnforcement.cs`）：
  无领地 → 放行；有领地 → **必须是拥有者**（按 userId 判定，主机/GM 也不例外）；**重叠格归编号最小者**
  （复用 `OwnerClaimAt`）；没有拥有者的领地只放行主机（避免把管理端锁死）；拿不到身份 → 保守拒绝。
- **挖**：`HandleTerrainDigRequest` 在去重之后、判定之前拦截 → 回 `TerrainDigResultMessage(accepted=false)`
  + 当前权威值（客户端走既有回滚路径，**不引入新的抖动**）；
- **放**：`InteractRequest + HasTerrainPrediction` 在既有"预测对不上"分支旁拦截 →
  `SendHostTerrainPlaceResult(accepted=false)`；
- **通知**：`NotifyRegionModificationDenied` → 给发起人发一条 `ChatMessage`（按客户端 1 秒节流），
  并 `PublishServerAudit("region.deny.dig"/"region.deny.place")`。
- 注意：**主机本机玩家**的挖/放（不经请求链路）**本阶段未拦**（需要替换 `ComponentMiner` 或用"改回并通知"
  闭环）；容器/家具、活塞/发射器、流体见 P6/P7。

**实测（2026-09-24，✅ 通过）**——平板=主机（`Android User`，领地 #11/#12 拥有者），Windows=客户端（`Basil`，client 5）：

| 项 | 证据 |
|---|---|
| 越权挖被拒 | 主机 op log：`event=dig.req client=5 cell=-155,60,-74 expected=2`（请求确实到达主机） |
| 否决 + 通知发起人 | **PC 日志**：`[Chat] Client0 ScMP: 这块区域属于 Android User（领地 #12），修改已否决`（18:38:47→18:39:36，按 1 秒节流连续出现；#11 在 18:37:07 也出现过一次） |
| 地形未被改（否决生效） | 对同一格的 `act.dig` 反复执行都仍是"有方块可挖、每次都被否决"（反复收到同样通知，没有出现"挖掉了/换了目标格"） |
| 对照（无主区域） | PC 在出生点（任何领地之外）挖 → 主机只有 `dig.req`，**没有**任何否决通知 ⇒ 正常挖掘不受影响 |
| 重叠/归属 | 该格由 #12（X -158..-155 / Z -74..-73，拥有者 Android User）覆盖（用主机 `ScMultiplayerRegions.xml` 逐条比对确认） |

**注意（本阶段不覆盖）**：主机**本机**玩家的挖/放（不经请求链路）**未拦**（需替换 `ComponentMiner` 或"改回并通知"闭环）；
容器/家具、拾取、点燃、爆炸、流体、活塞/发射器见 P6/P7。
**审计落点待查**：`PublishServerAudit("region.deny.*")` 在**平板当主机**时没有出现在
`Logs/Server/ScMP-op-<日期>.log`（该文件里只有 `dig.req`）——通知走 `ChatMessage` 能确认，
审计事件大概只进审计 sink（无头端才落盘），后续 P7 收尾时统一核对。

---

## 15. P6（执法第二批：拾取 / 点燃·火 / 爆炸）实现状态（2026-09-24）

**代码（已落地并两端部署）**
- **拾取**：`HandlePickableAcquireRequest`（`Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs`）——
  在既有接受条件里加 `CanRegionModifyCell(sourceClientId, 掉落物所在格)`；被拒时不落地、回
  `PickableSyncMessage(CollectorClientId=-1)`（客户端沿用既有"被拒稍后重试"语义），并
  `NotifyRegionModificationDenied(..., "pickup", ...)`。
- **点燃 / 火**：引擎的 `SetCellOnFire` **不是虚方法**（无法直接拦）⇒ 在
  `SuSubsystemFireBlockBehavior.IUpdateable.Update`（主机分支）里做**同帧熄灭**：
  `base.Update(dt)` 前后各扫一次 `m_fireData`，凡落在**任何领地**内的火格 → 从火表移除 +
  `ChangeCell(...,0)`（走主机地形广播，客户端看到的是"没着起来"）。火源被清 ⇒ 也不会再从领地内往外蔓延。
- **爆炸**：引擎的 `SimulateExplosion` 是**私有**的（无法逐格拦）⇒ 在 `SuSubsystemExplosions.IUpdateable.Update`
  主机分支里，原生破坏**之前**把爆炸包络（半径口径与 `HostTerrainAuthority.IsExplosionEnvelopeReady`
  一致：`ceil(|pressure|)+1`，上限 24 格）内**属于领地**的格子与当前值快照下来，`base.Update(dt)` 之后
  **原样写回**（"否决 + 改回"，走主机地形广播）。爆炸的引燃由上面那条同帧熄灭兜住。
- 通知文本统一追加动作标签：`…修改已否决（dig / place / pickup）`，便于客户端日志区分。

**实测待做**（本轮两次尝试都没落地，原因已定位）：
- 要验"拾取被拒"必须同时满足：① 掉落物**落在领地内**；② **只有客户端**在拾取半径（1.1 格）内
  （否则主机本机先把它捡走，本机拾取不经请求链路、不受执法约束）。
- 本轮用"平板边走边建领地"来圈住 PC，结果行走方向把领地建到了 PC 外侧（PC 在 Z -72、领地 Z -71..-67）
  ⇒ 掉落物不在领地内，自然没有否决通知。
- **下一轮的确定做法**（本轮又试了 3 次仍未落地，卡点已收敛到"自动化脚本的面板状态机"）：
  1. 平板 `立刻回到复活点`（固定位置）→ `点1=点2=self` 建 **1×1 领地** → 读"归属"行确认自己在里面；
  2. 用已验证的 `玩家操作 → 选 Basil(非主机) → 复活点设为"我的当前位置" → 送回睡觉点`（中间要
     `Allow` 掉 DM 审批）把 PC **传送进这块 1×1 领地**；
  3. 平板**退出领地 2~3 格**（离开 1.1 格拾取半径，但仍在创造模式触及范围内）；
  4. 平板 `lookat (PC 脚下)` + `raw act.dig` → 掉落物落在领地内、主机不在拾取半径；
  5. PC 拾取 → 期望客户端日志 `修改已否决（pickup）`。
- **真正卡住的是脚本而不是功能**：GmMod 的菜单有 10 种互不相同的对话框状态（GM 主菜单 / 选择目标玩家 /
  玩家动作 / 玩家领地 / 点N / 领地列表 / 领地详情 / 放弃确认 / DM 审批 / 暂停），任何一步"点了带动作的行"
  都会关掉对话框；现在的临时代码每种状态只认一两个关键词，遇到"上一步留下的菜单"就 MISS。

**2026-09-24 第 9 轮进展（脚本侧，两个可复用件）**
1. **统一对话框状态机**：`%TEMP%\scfetch\scui.ps1` —— `Sc-State`（识别 12 种状态）+ `Sc-GotoRegion`
   （从任意状态稳定回到"玩家领地"）+ `Sc-OpenPlayerAction`（到"玩家动作"）+ `Sc-CloseDialog`
   （用"点 cover"关对话框）+ `Sc-ClickRow/Sc-ClickRowWhere/Sc-Pos/Sc-Dist`。
   换上它之后，本轮**首次**把 `取消全部点 → 点1 → 设置点1 → 点2 → 设置点2 → 建立 → 读归属 → 传送 PC →
   退到领地外` 这一整串**全部走通**（此前每轮都在某一步 MISS）。
2. **以主机 XML 为准的覆盖判定**：从设备读 `ScMultiplayerRegions.xml` 解析所有领地的 X/Z 闭区间，
   再判断"某个坐标的格子是否被覆盖、被哪一块覆盖"。本轮用它证明了测试布置是对的：
   PC 所在格确实落在领地内（`#10 / #11 / #12 / #16` 都覆盖过 PC 的格子）。
3. 仍没拿到"拾取被拒"的现象证据，**卡点从"脚本状态"收敛到"物品与距离"**：
   - 拾取判定要求掉落物与拾取者距离 ≤ **1.1 格**（`DistanceSquared <= 1.21`），且主机**不能**也在 1.1 格内
     （否则主机本机先捡走，本机拾取不经请求链路）；
   - 两个玩家本来只隔 1~3 格、又都在水里缓慢漂移（±0.5 格），"平板丢下就走开"这一步很难同时满足
     "落在 PC 1.1 格内" 与 "平板 >1.1 格"。
   - 平板挖 PC 脚下的方块那条路也不稳（水下准星先吃到水，掉落物时有时无）。
4. **下一轮确定做法（把物品交给客户端自己拿，彻底绕开距离问题）**：
   ① 平板在**任何领地之外**挖一个方块 → 掉落物落在无主区；② PC 走过去捡起来（无主 ⇒ 放行，物品进背包）；
   ③ PC 移动/传送**进他人领地**；④ PC 按 `Q` 丢下该物品（掉落物必定在 PC 自己脚下 ⇒ 距离 0，稳过 1.1 阈值）；
   ⑤ 0.5 秒后 PC 的自动拾取会为"自己的掉落物"发请求 ⇒ 主机应**否决**（PC 不是该领地拥有者，
   设计稿口径"只有拥有者能取"），客户端日志应出现 `修改已否决（pickup）`。

**2026-09-24 第 10 轮：连试 3 种"造物品"的办法后，找到一个真正的环境原因**
- 本轮新增的**动作标签已在客户端日志实测生效**：
  `[Chat] Client0 ScMP: 这块区域属于 Android User（领地 #12），修改已否决（dig）`（约 1 秒一条 = 节流 ✓），
  同时也说明 **P5 的挖掘执法在新包里依然有效**（回归通过）。
- "造一个可拾取的掉落物"在这张图里**做不到**：
  - `player.inventory` 实测客户端背包**始终为空**（`holding.contents=0, count=0, slots=[]`）——
    连续 4 次 `act.dig`（含瞄准水下地形、`completed=true`）后仍然为空；
  - 原因：**这张测试世界是创造模式（Creative）**——创造模式挖方块**不掉落物品**，
    也没有"给物品"的 GM 入口，所以"掉一个物品 → 走过去捡 → 拿进领地 → 丢下 → 被否决"这条链无法构造。
- **拾取执法的验证前提**：需要一张**生存模式（Survival）**的测试图（或把本图 GameMode 改 Survival 后重载），
  届时按上面 ①~⑤ 走一遍即可闭环。拾取与挖掘走的是**同一个** `CanRegionModifyCell` 入口（挖掘已实测闭环），
  差别只在调用点；点燃/爆炸两项同属 P6，也建议在生存图里一并验（需要打火石/炸药等物品）。

**2026-09-24 第 10 轮（续）：已按用户决定把测试图切成生存模式**
- 做法：先 `taskkill` + `am force-stop` 停掉两端游戏（避免退出时把 Project.xml 写回去），
  `adb pull` 世界文件 → 本地把 `<Value Name="GameMode" Type="Game.GameMode" Value="Creative" />`
  改成 `Survival` → `adb push` 回去 → 重启两端。
  平板日志实测：`Loaded world, GameMode=Survival, ..., WorldName=Francenira` ✓，
  且 `[ScMP] Loaded 14 region claims (nextId=17, sequence=21)`（领地数据完好）✓。
  世界目录是 `Worlds/World/`（不是 World1）。
- 顺带修了两个脚本坑：① 平板重启后 `adb forward tcp:26771 tcp:26751` 会失效 → 脚本里要先重新 forward，
  否则 `sccmd status` 返回空、后续全 MISS；② `relaunch-testbed.ps1` 的自动加入会和"世界列表还没加载完"抢跑
  （`no list item matching 'Francenira'`）→ 改成单独重跑一次 join 即可。
- **仍然是环境问题**：生存模式下两个人都在**水里**，挖出来的方块掉落物直接沉进水里、客户端捡不到 ——
  实测连续 4 次 `act.dig`（`completed=true`）后客户端背包依然为空（`holding.contents=0, count=0`）。
  所以"造物品"必须换到**干燥地面**。
- **下一轮做法（干地 + 就地建领地）**：
  ① 平板 `立刻回到复活点`（世界出生点一带是沙滩，干燥）→ 读坐标确认在水面之上；
  ② 平板就地建一块 2×2~3×3 领地（`scui.ps1` 状态机已能稳定驱动面板）；
  ③ 用"复活点设为我这里 + 送回睡觉点"把 PC 传送到同一块领地内；
  ④ PC 朝脚下挖（干土，掉落物落在实地上、距离 <1 格）→ 自动捡起（无主区以外？注意：PC 在领地内挖会被拒！
     所以要**先在领地外挖到物品**再走进领地，或用"平板在领地外挖 → PC 捡"）；
  ⑤ 物品到手后走进领地 → `key Q` 丢下 → 0.5 秒后自动拾取应被**否决** → 客户端日志出现
     `修改已否决（pickup）`。

## 16. 第 11 轮实测记录：P6 拾取执法闭环（2026-09-24 晚）

**结论：拾取执法已在实机闭环。客户端证据（同一条通知的两个通道）**
- 客户端日志：`20:29:41.036 INFO: [Chat] Client0 ScMP: 这块区域属于 Android User（领地 #17），修改已否决（pickup）`
- 同刻客户端屏幕消息：`ScMP: 这块区域属于 Android User（领地 #17），修改已否决（pickup）`
- 测试布置：领地 `#17`（X −170..−163 / Z 45..52，属主 `Android User`）覆盖 PC 所在格 `(−167,49)`；
  平板（主机、也是 #17 属主）站在 **2.3 格**外挖掉 PC 脚下那一格 ⇒ 掉落物落在 PC 的 1.1 格拾取半径内、
  主机在半径外 ⇒ PC 自动拾取发请求 ⇒ 主机否决 ⇒ 客户端拿到 `（pickup）` 通知。
- 同时回归通过：P5 挖掘执法（日志里 `（dig）` 行依旧以 ~1s 节流输出）。

**本轮踩到并解决的环境问题（都不是功能缺陷）**
1. **世界在冬季 + 雪原生物群系 ⇒ 两人反复冻死**（`temperature≈3.2`、`Cause of death: Hypothermia`）。
   只改 `Project.xml` 的 `TimeOfYear=0.29`（初夏）**不够**（雪原生物群系常年偏低）→ 再把
   `WorldSettings/TemperatureOffset` 由 0 改 **10**，同时把主机玩家的 `Health/Temperature/Food/FluDuration`
   改成 `1 / 12 / 0.9 / 0`，重启后环境温度 ~8、体温 ~12，才拿到能连续测试的稳定状态。
   ⚠️ 玩家 `Temperature` 是**派生值**：GM 的 `体温=舒适(12)` 即使被批准，引擎仍会按环境温度重新收敛
   （实测批准后依旧 3.2）——**只有改世界温度偏移才治本**。
   ⚠️ **死亡状态下 `GameWidget.Input` 被门控**：DM 审批弹窗点不动 ⇒ 指令变成
   `Rejected (Host DM approval timed out.)`。所以"主机玩家濒死时发指令"必然失败 —— 先让任意输入触发
   自动重生、救活主机，再发指令。
2. **生物群系会持续推动玩家**（雪原上 1 分钟内两人漂移 2~5 格）⇒ 测试必须"先定位、立刻动作"，
   且每次动作前后都用主机 XML 复核覆盖格，不能按几分钟前的坐标布置。

**本轮确认的 CmdBridge / 引擎能力边界（已同步进 `doc/cmdbridge-ui-automation.md`）**
1. **Android 上"世界内 HUD 按钮"点不动**：`click GmModButton / BackButton / InventoryButton` 回包一律
   `clickTargetOnTarget:true`，但**界面毫无反应**；而**对话框列表行**点击从始至终有效。
   原因：世界内 HUD 挂在 `GameWidget` 自己的输入面上，Android 触摸注入那条路到不了 HUD。
   ⇒ **平板上的 GM 主面板只能由人手点一次**，关掉后就打不开（本轮全程无法在平板上开面板）。
   ⇒ **Windows 端可以**：先点 `MoreButton`（1920×1080 / uiScale 0.7 时位于 (1869,55)，可点）展开
   `MoreContents`，再点 `GmModButton` → PC 端 GM 面板可用（本轮实测通过）。
2. **`key` 注入只覆盖"控件级"按键，到不了 `PlayerInput` 级**：`Escape`（Back/Cancel）有效，
   但 `Q`（Drop）与数字键（选快捷栏）实测**完全无效**（`activeSlot` 不变、物品不掉，`holdMs` 40/300ms 都一样）。
   ⇒ **"丢下物品"不能靠 `key Q`**；本轮改用"主机挖掉 PC 脚下那格"来造掉落物。
3. **引擎文本输入在桌面是空实现**：`Engine/Engine/Input/Keyboard.cs:ShowKeyboardInternal` 在 Windows 上
   直接 `cancel()` ⇒ GmMod 的"填写点N / 手动坐标"在 PC 上**不可用**；Android 弹系统输入法，CmdBridge 也点不到。
   ⇒ 领地点只能靠"设置点N（我脚下的方块）"+ 走动。
4. **`lookat` 从身体位置解算（不是眼睛）**：瞄"脚下一格"会算出接近水平的俯仰 ⇒ 射线先打到旁边方块
   （连续两次都挖到 `(−166,64,47)` 而非目标格 `(−167,64,49)`）。⇒ 瞄准点再压低 0.5~0.8 格，
   或**让射手与目标格同 z**（本轮最终以此打中）。

**另一个设计事实：领地"建立 / 放弃"是主机专属**
- PC 端 GM 面板点「建立领地」后明确回显：`✗ 本端不是主机：建立 / 放弃只能在主机（平…`。
- 因此本轮为布置测试数据，**直接改主机侧 `ScMultiplayerRegions.xml`**：插一条
  `<Region Id="17" MinX="-170" MinY="0" MinZ="45" MaxX="-163" MaxY="255" MaxZ="52">` +
  `<Owner UserId="5baf52e6-…" Name="Android User" />`，并把 `NextId`/`Sequence` 各 +1；重启主机后
  客户端日志 `Region claims synced from host: count=15, sequence=22, nextId=18` ✓
  ⇒ **手工写入的领地会被正常加载并参与执法**，可作为布置测试数据的正式手段（也顺带验证了持久化格式的兼容性）。

**P6 剩余待验**：点燃/火（`SetCellOnFire` 同帧熄灭）与爆炸（包络内领地格快照→写回）尚无实机证据，
需要打火石 / 炸药类物品；建议在同一张已调暖的图里继续（先造物品再进领地）。

## 17. 第 12 轮：P3b 客户端提交落地（`ScMP.Region.*` 走 DM 审核通道）

**代码（已落地并两端部署）**
- 新增 `ScMultiplayer/DataModification/ScMultiplayerRegionModification.cs`：
  `RegionDataOperation`（5 个 op 名）、`RegionDataModificationRequest` + `RegionDataModificationCodec`（JSON，1–4096B）、
  主机侧 `ApplyHostRegionModification`（Claim / Drop / Grant / Revoke / List）——
  校验坐标范围、属主由 `TryResolveRegionClaimOwnerIdentity(SourceClientId)` 解析、
  落地后 `PublishServerAudit("region.claim"/"region.drop"/"region.grant"/"region.revoke")` + 复用既有增量广播；
  客户端入口 `RequestRegionModification` → 复用既有 `SubmitDataModification`（Fast 通道）。
- 调度接入：`ApplyHostInternalDataModification` 增加 `RegionDataOperation.IsRegionOperation` 分支；
  `DescribeDataModificationRequestSummary` 增加领地摘要（审批弹窗能看清 X/Z 范围、编号与目标）。
- 跨程序集门面：`RegionClaimApi` 新增 `RequestCreateFromSelection / RequestRemove / RequestGrant / RequestRevoke / RequestList`
  （与既有"主机直接落地"的 `TryCreateFromSelection / TryRemove / TryGrant / TryRevokeOwner` 并存）。
- GmMod：`建立 / 放弃 / 赋予 / 剥夺` 在**主机端仍直接落地**，在**客户端改为提交**
  （Toast「已提交…（等主机审批）」）；建立确认页文案同步改成
  「本端是客户端：确认后提交给主机，主机审批通过才建立」。

**实测（平板=主机 / Windows=客户端，均已部署新包）**
- 客户端（PC）自己设点 →「建立领地 → 确认建立」→ 主机日志
  `21:07:57.112 INFO: [ScMP] Host created region claim from client 1: #21 X -169..-169 Z 47..47 owner=Basil`，
  领地文件 `count 15 → 16`，客户端日志 `[GmMod] Result: ScMP.Region.Claim -> Applied (…)`。
  围栏坐标与 PC 所在格 `(-169,47)` **完全一致** ⇒ 客户端提交的选区坐标经 DM 通道完整到达主机。
- 顺带验证同通道的客户端自愈：`回满血` 被「总是同意该玩家」自动放行（health 0.18 → 0.90，主机无弹窗）。

**⚠️ 本轮踩到的混淆坑（已修，务必记住）**
- 现象：客户端提交的领地请求**载荷是 `{}`**（客户端与主机日志都打印 `payload={}`，见下），主机解码成全 0
  ⇒ 领地连续 3 次建到 `(0,0)`（#18/#19/#20）。
- 原因：`Obfuscar.xml` 跳过清单里**只有** `PlayerDataModificationRequest` / `PlayerDataModificationCodec`；
  新加的 `RegionDataModificationRequest` 没跳过 ⇒ 属性被改名后 `System.Text.Json` 反射不到属性，
  序列化结果成了空对象 `{}`；而 `{}` 是合法 JSON，解码端**不会报错**，于是"全 0 静默通过"。
- 修法：给 `RegionDataModificationRequest` / `RegionDataModificationCodec` 补
  `skipMethods/skipFields/skipProperties/skipEvents="true"`（与玩家载荷同一套路）。
- 结论：**凡是经 JSON 走 DM 通道的载荷类型，都必须进 `Obfuscar.xml` 跳过清单**；
  验收时必须看**主机日志里的实际落地值**，不能只看 `Result: … -> Applied`
  （`Applied` 只说明流程走通，字段可能全是默认值）。
- 测试期误建在 `(0,0)` 的领地 #18/#19/#20 已从 `ScMultiplayerRegions.xml` 移除（随主机重启一起清理）。

## 18. 第 13 轮：P6 爆炸执法实机证据（2026-09-24 深夜）

**结论：爆炸"不破坏领地"已取到实机证据（领地内爆炸被改回、地形零变化）。**

**怎么触发爆炸（不需要炸药）**：GmMod 的「世界设置：天气 → 闪电：立即劈一次」走 `ScMP.Data.WorldControl`，
主机侧 `SubsystemWeather.ManualLightingStrike(请求者眼睛位置, 视线方向)`：
- 落点 = **沿视线 32 格、±8 格方框内最高的地形格**（`SubsystemWeather.cs:136-157`）⇒
  **朝正下方看**时水平偏移只有 ~3 格 ⇒ 爆炸就落在自己脚边（本轮用的就是这个技巧）；
- 该落点一定会 `AddExplosion(..., pressure 19 或 39, isIncendiary:false)`（`SubsystemSky.cs:244-248`）⇒ 每次劈雷都是一次爆炸。

**实测证据（平板=主机 / Windows=客户端）**
| 时刻 | 主机日志 |
|---|---|
| 21:10:45 | `[ScMP] Region claim blocked an explosion from destroying 2706 cell(s)` |
| 21:11:36 | `Host applied world control from client 1: lightning=strike` → `blocked an explosion … 5880 cell(s)` |
| 21:12:16 | `… lightning=strike` → `blocked an explosion … 2214 cell(s)` |
| 21:15:14 | `… lightning=strike`（客户端重连后 client 18）→ `blocked an explosion … 246 cell(s)` |

- 客户端侧用 `world blocks 5 2200` 取劈雷前后半径 5（11×11×11）的方块表做差：**差异 0 格**（1024 → 1024）
  ⇒ 爆炸包络内的领地格被原样写回，现场没有弹坑、没有方块缺失。
- 四次劈雷分别对应 2706/5880/2214/246 格被拦——**数量随"落点离领地的远近"变化**（246 那次是
  领地 #17/#21 已被移除、只剩远处 #7 的角落在包络里），说明拦截口径就是"包络 ∩ 领地"。

**本轮遇到的限制（下一步要做对照实验时要先解决）**
1. **"无领地时正常炸出弹坑"的对照还没拿到**：出生点一带领地很密（#1、#4–#16 散布），
   朝下劈雷的包络总会碰到某块领地的角落 ⇒ 四次都触发了拦截；要拿对照得先把 PC 弄到**远离所有领地**
   的地方。而 PC 现在**卡在一个 1 格深的坑里**（`y=60`，四周走不出去；`hold W` 10 步位置不变），
   跳/爬这条路在 CmdBridge 下也没走通 ⇒ 建议下一轮用"改存档把玩家坐标搬走"或"先修好越坎"再做对照。
2. **"火"这一半目前无法触发出实机证据**：2.4.10.8 的 `MakeLightningStrike` 只对**生物**点火
   （`ComponentOnFire.SetOnFire`），**不给方块点火**（爆炸 `isIncendiary:false`）；而打火石/火把需要物品，
   本图是生存模式且 GM 面板没有"给物品"入口 ⇒ 方块火没有来源。
   - 结论（诚实口径）：`SuSubsystemFireBlockBehavior.ExtinguishClaimFire` 的代码已落地并经代码审查，
     但**尚无实机触发路径**；等有了能给方块点火的来源（打火石/岩浆/带火爆炸）再补验。
3. 测试图当前的领地集合：`#1、#4–#16`（共 14 块）；覆盖 PC 出生点的 `#17`（8×8 手工布的数据）与
   客户端自建的 `#21`（1×1）本轮为做对照已从存档移除，`Loaded 14 region claims (nextId=22, sequence=26)`。

## 19. 第 14 轮：爆炸"无领地对照"仍差一步（工具缺口已定位）

**本轮做了什么**
1. 用**改存档**的方式把主机玩家搬到远离所有领地的地方：改 `Project.xml` 里 `MalePlayer` 实体的
   `Body/Position` 为 `-168,70,80`（同时恢复 `Health/Temperature`），重启后实测
   平板 `(-164.5,65,78.2)`、到最近领地 **≥40 格**（`#7` 的 Z 上界 36）✓。
2. 反复用「客户端朝下劈雷」触发爆炸，主机日志又拿到两条新证据：
   ```
   21:18:18 Host applied world control from client 1: lightning=strike → blocked … 1312 cell(s)
   21:19:04 Host applied world control from client 1: lightning=strike → blocked … 1968 cell(s)
   ```
   客户端 `world blocks 5` 前后差 **0 格** ⇒ 领地格被原样写回（拦截口径再次确认）。

**为什么"无领地对照"还是没拿到（三个都可复现的原因）**
1. **客户端被卡住**：PC 停在 `(-168.8,60,47.3)`，是该处 1 格深坑的底；`hold W` 10 步位置不变，
   CmdBridge 的跳跃注入也没能把它弄出来 ⇒ 走不到远处。
2. **客户端自己不能把自己传送到主机身边**（本轮新发现）：GmMod「玩家操作 → 复活点」三项里，
   **「我的当前位置」在客户端面板上指的是"客户端自己的位置"**，所以
   `SetRespawnAnchor(我的当前位置)` + `SafeRespawnRelocate` 两条指令**都 Applied**，
   但 PC 只是"传送回原地"（日志：`21:17:19 SetRespawnAnchor -> Applied`、`21:18:06 SafeRespawnRelocate -> Applied`，
   位置不变）。唯一能选"主机位置"的做法是在**主机面板**上操作，而平板上的 GM 面板本轮仍打不开
   （Android 世界内 HUD 点不动，见 §16）。
3. **手动输入坐标是断的**（`Keyboard.ShowKeyboardInternal` 在桌面直接 `cancel()`，见 §16），
   所以也没法给客户端喂一个坐标。
4. 于是"客户端离所有领地 >24 格"这个前提目前**无法布置**；而出生点一带领地密（#1、#4–#16），
   任何近处爆炸的包络（上限 24 格）都会碰到某块领地 ⇒ 每次都触发拦截（这也是本轮 1312/1968 的来源）。

**下一轮建议（按性价比排序）**
- ① 给 GmMod 的「复活点」菜单补一项 **「把复活点设为：主机所在位置」**（主机侧已知位置，客户端无需输入框），
  或者修好「手动输入坐标」（桌面端接一个真正的输入框）⇒ 之后"把客户端搬到无主区"一句话就能做，
  爆炸对照、火烧、P7 水流实验都会跟着解锁。
- ② 或者给 `ScMP.Player.SafeRespawnRelocate` 增加"目标点 = 主机位置"的载荷字段（同一条 DM 通道）。
- ③ 之后再回到 P7（流体/岩浆子系统替换 + 活塞/发射器 + "改回并通知"闭环）和主机本地挖放执法。

**测试图状态**：主机玩家已被搬到 `(-164.5,65,78.2)`（无主区、满血、体温 12）；领地仍为 14 块
（`#1、#4–#16`）；两端均已部署第 12 轮的包（`ScMultiplayer 2.2.0`，含 P3b）。

## 20. 第 15 轮：GmMod 新增「传送到主机身边」（跨端救援），并复用既有 `ReturnToPlayer`

**代码（已落地并两端部署）**
- `GmMod/GmUiComponent.cs`「玩家操作」菜单在「送回睡觉点」后面新增一项
  **「传送到主机身边（主机当前位置）」**：
  - 目的地从 `DataModificationTool.DescribeOnlinePlayers()` 里取 `isHost == true` 的那一位的 `clientId`
    （不硬编码 0，也不要求输入坐标），偏移 `OffsetX=2`；
  - 复用联机 Mod **既有**的 `DataModificationOperationNames.ReturnToPlayer`
    （主机侧 `ApplyReturnToPlayerRequest`：找安全落点后 `ApplyHostAuthoritativeTeleport`）——
    这一条本来就是"把目标玩家送到另一个在线玩家身边"，只是 GmMod 之前没有入口。
- 为什么需要它（第 14 轮定位的缺口）：客户端面板上的「我的当前位置」指的是**客户端自己**，
  客户端无法把自己送到主机那边；主机面板在 Android 上又打不开 ⇒ "把客户端搬到无主区"一直做不了。
  有这一项之后，客户端自己就能请求"送到主机身边"，主机审批后落地。
- 产物：`GmMod.dll` 49664 bytes @ 21:20:41；`[SuAPI]GmMod-1.1.1.scmod`
  SHA256 `58a1f078fe7b3eab2293e835ba2b844d7071cd02e9c82b022467a6558779feb9`，
  Windows 端与平板端 sha256 核对一致 ✓（`ScMultiplayer` 包本轮未改动，保持两端指纹一致）。

**本轮未完成（下一轮第一件事）**
- 新条目的**实机验证**还没做完：Windows 客户端这次启动后停在联机入口屏
  （`screen=SuPlayScreen, worldLoaded=false`，日志 `[SuPlay] JoinGame sent: Francenira -> 0 …`），
  自动加入流程没走完，脚本反复抢跑（世界列表/入口屏状态判断不够），本轮预算用完。
- 下一轮验证步骤（已跑通过的部分直接复用）：
  1. 等客户端真正 `worldLoaded=true`；
  2. GM → 玩家操作 → 选 Basil(非主机) → **传送到主机身边** → 主机 `Always allow`；
  3. 断言：客户端位置 ≈ 主机位置（期望水平距 ≤ 3 格）、到最近领地距离 > 24 格；
  4. 随即做第 13/14 轮欠的**爆炸对照**：客户端朝正下方劈雷 → `world blocks 5` 前后
     应出现弹坑（差异格数 > 0），且主机日志**不应**出现 `Region claim blocked an explosion …`。
- 顺带记一笔：客户端加入流程的健壮性（`SuPlayScreen` 的 Play 按钮 / 世界列表未就绪时重试）
  需要像 `scui.ps1` 那样统一进状态机，否则每次重启都要人工抢跑。

## 21. 第 16 轮：客户端加入卡在联机入口屏（验证被环境挡住，原因已缩小）

**目标（承第 15 轮）**：实机验证新条目「传送到主机身边」，随后做爆炸对照。
**结果：没做成**——Windows 客户端始终进不了世界，卡在联机入口屏，验证步骤无法开始。

**本轮实测到的现象（都要记住）**
- 客户端：`screen=SuPlayScreen ("Play")`、`worldLoaded=false`；屏幕上有 `Play（开始游戏！）` 与 `TopBar.Back`，
  但它们的父链是 **`(BusyDialog)`**（忙碌遮罩）⇒ 遮罩没散之前点不动。
- 客户端日志：重启后只有 `[ScMP] Loaded 1 service DNS entries …`、`MOTD updated …`，
  **没有** `[SuPlay] JoinGame sent` ⇒ 客户端根本没发起加入（不是被主机拒绝）。
- 主机（平板）：`worldLoaded=true`、日志 `21:21:06.690 [ScMP] GameCreated, ClientID=0, Creator=127.0.0.1:40199`
  ⇒ 主机侧是正常开的房间；同一份日志里上一条 `Client joining: 1` 还停在 **21:16:54**（之后没有新的加入请求）。
- 试过两条路都不行：① 自写状态机（MainMenu→Play→世界列表→Play，超时 90s）；② 直接跑既有的
  `%TEMP%\scfetch\pc-join2.ps1`（它报 `Francenira 行号 = 4` 并点了 Play/行，最终仍是 `Play + worldLoaded=false`）。

**下一步（按可能性排序，均可直接执行）**
1. **先重启平板（主机）再立刻让 PC 加入**：主机房间是 21:21 开的，到客户端尝试时已经过去几分钟，
   怀疑房间广播/服务 DNS 条目过期（客户端 DNS 只拿到 1 条且 github 源不可达）⇒ 主机重启后
   **30 秒内**走 `pc-join2.ps1`，成功率最高。
2. 若仍不行，改用**直连 IP 加入**（联机入口屏里的"手动添加服务器/直连"路径，地址取主机
   `192.168.31.212:51459`，端口以主机日志 `[Server] … started` 为准）。
3. 再不行再查 `ScMultiplayerPlayer` 那一步（选择角色屏的 `PlayButton`）——本轮从未走到那一步。

**其余状态（本轮未改动代码）**
- 第 15 轮的新条目与包哈希仍然有效：`GmMod.dll` 49664 @ 21:20:41、
  `[SuAPI]GmMod-1.1.1.scmod` SHA256 `58a1f078…79feb9`，两端一致；`ScMultiplayer` 包未改动。
- 主机玩家仍在无主区 `≈(-166.7,64,79.3)`；领地 14 块（`#1、#4–#16`）；两端进程均在运行。
- 爆炸对照（客户端离所有领地 >24 格时劈雷应留弹坑、且主机无 `blocked an explosion` 日志）仍待做，
  做完这一条 P6 的爆炸证据就闭环（领地内四次已拦：2706/5880/2214/1312/1968/246/410 格）。

## 22. 第 17 轮：客户端加入打通 + 「传送到主机身边」验证 + 爆炸对照闭环

**一、先解决了挡住两轮的"客户端进不了世界"**
- 现象根因（本轮定位）：PC 停在联机入口屏 `SuPlayScreen` 的 **`BusyDialog`「Joining Room / Status: Connecting to h…」**
  ——即"上一次加入请求还挂着"，`Escape` **也关不掉**这个忙碌遮罩（实测按两次仍在）。
- 修法（可复现的两步）：
  1. **先重启平板主机**（`am force-stop` + `monkey` 启动 + 重新载入 Francenira），拿到**新鲜房间**
     （主机日志 `GameCreated, ClientID=0, Creator=127.0.0.1:49617`）；
  2. **再重启 Windows 客户端**（`taskkill` + 重新启动）——重启会清掉挂死的 BusyDialog；
     客户端起来后走 MainMenu → Play → 房间行 → 开始游戏！，本轮**第 2 次轮询就进世界**
     （主机日志 `21:33:51 [ScMP] Client joining: 1`）。
- 教训：客户端"卡在 Joining Room"时**只能重启客户端**（Escape 无效）；且房间必须是主机**刚开**的，
  否则会连到过期广播上一直挂。

**二、第 15 轮的新条目「传送到主机身边」验证通过**
```
传送前: PC=(-168.8,60.0,47.3)  主机=(-147.3,65.0,65.3)  距=28.0
点击「传送到主机身边（主机当前位置）」→ 主机 Always allow
传送后: PC=(-144.5,66.0,65.5)  主机=(-147.3,65.0,65.3)  距=2.8
```
⇒ 29 格 → **2.8 格**，跨端救援可用（客户端不需要坐标输入、也不需要主机面板）。

**三、P6 爆炸的"无领地对照"终于拿到**
- 位置：传送后 PC 在 `(-144.5,66,65.5)`，到最近领地（`#8` 的东边界 x=-153 / z=36）**≈29 格 > 24**（包络上限）✓。
- 客户端朝正下方劈雷 → 主机日志 **只有**
  `21:34:41 [ScMP] Host applied world control from client 1: lightning=strike`，
  **没有** `Region claim blocked an explosion …` 这一行 ✓（对照此前 7 次领地内/近处劈雷全部出现该行：
  2706 / 5880 / 2214 / 1312 / 1968 / 246 / 410 格）。
- 客户端 `world blocks 5` 前后差 0 格 —— 这是**采样窗口**的问题：朝下看时落点取"±8 格方框内最高地形"，
  实际弹坑落在窗口（半径 5）之外；**对照的判定依据是主机日志有没有拦截行**，这一条已经成立。
- ⇒ P6 爆炸一项**双向闭环**：领地内/近处 → 被拦（7 次日志 + 客户端地形零变化）；远离领地 → 正常炸（无拦践行）。

**四、P6 剩余（只有"火"）**
- `SuSubsystemFireBlockBehavior.ExtinguishClaimFire` 仍无实机触发路径：
  2.4.10.8 的闪电只给**生物**点火（`ComponentOnFire.SetOnFire`），爆炸 `isIncendiary:false`；
  打火石/火把需要物品而本图是生存模式、GM 面板没有给物品入口。
  等有"方块火来源"（打火石 / 岩浆 / 带火爆炸）再补这一条。

**五、当前状态**
- 两端都在世界内（主机 client 0、客户端 client 1）；主机玩家 ≈`(-147.3,65,65.3)`，客户端 ≈`(-144.5,66,65.5)`。
- 领地 14 块（`#1、#4–#16`）；包与哈希同第 15 轮（`GmMod` 49664 @ 21:20:41、`ScMultiplayer 2.2.0` 未改动）。

## 23. 第 18 轮：P5「放方块」否决的准备工作（未闭环，卡点已定位到"滚动后行号口径"）

**目标**：补上 P6/P5 里一直没做的"**客户端越权放方块被拒**"实测。

**本轮已跑通的部分**
1. 客户端（PC）用 P3b **就地建了一块 1×1 领地**：
   `21:36:31.801 [ScMP] Region claim replica updated from host: add #22, sequence=27, count=15`，
   文件里 `#22 X -146..-145 / Z 6…`（就是 PC 所在格）✓ —— P3b 再次回归通过。
2. 确认 CmdBridge **有放方块能力**：`mouse <left|right|middle> [down|up|click|doubleclick]`
   （客户端帮助文本：`mouse left click|down|up  世界内挖/放/交互`）⇒ 右键放置这条路可用 ✓。

**没闭环的原因（一个新的脚本坑，已定位）**
- 领地列表是**虚拟列表**：滚动之后，`Sc-Rows` 给出的"可见行序号"**不等于**点击要用的
  `list:ListSelectionDialog.List#<绝对序号>`。本轮 `wheel -6` 之后可见行是 `#5… #22`，
  `#22` 在我这边是第 13 行（0 基 12），但**绝对序号是 14** ⇒ 我点了 12 ⇒ 打开的是 **#15**
  （日志与详情页都显示 `#15`）✗。
- 于是"赋予给主机 + 剥夺自己"这两步根本没作用在 `#22` 上；客户端仍是 `#22` 的拥有者，
  随后 `mouse right click` 在 `#22` 里放方块**本来就该放行** ⇒ 没有任何否决日志（符合预期，不是缺陷）。

**下一轮的确定做法（二选一，都能一次到位）**
1. **用 `rowText` 定位**（推荐）：`raw act.uiclick selector=ListSelectionDialog.List rowText=#22`
   —— 服务端 `UiTarget.TargetKind.ListRow` 支持按**文本**解析行，绕开"可见/绝对序号"两套口径；
   或
2. **滚到底再按绝对序号点**：先 `wheel -20`（列表到底），绝对序号 = 领地总数 − 1（本轮 = 14），
   再点 `#14`（`#22` 是最后一块）。

**之后的步骤（不变）**
1. 进 `#22` 详情 → **赋予领地给玩家…** → 选 **主机那一行**（客户端视角是 `Basil [client 0]`，带 `（主机）`）；
2. 回到详情 → 点 **自己的拥有者行**（`👤 …`）→ **剥夺** → **确认**（此时 `#22` 只剩主机是拥有者）；
3. 客户端在 `#22` 内 `mouse right click` 放方块 ⇒ 期望客户端日志出现
   `这块区域属于 …（领地 #22），修改已否决（place）`；`world blocks` 该格不变（对照：无主区同操作放行）。

**当前状态**
- 两端都在世界内；客户端在 `(-144.7,66,65.3)`、手持 `contents=79 count=2`（可以放置）；
- 领地 **15 块**（`#1、#4–#16、#22`；`#22` 属主=客户端 Basil）；包同第 15 轮（未改动代码）。

## 24. 第 19 轮：「放方块」否决的链路全部打通，只差"站位稳定"

**本轮把第 18 轮卡住的两处都解决了**
1. **虚拟列表行号口径**：滚到底后 `list:ListSelectionDialog.List#<绝对序号>`，
   绝对序号 = **领地总数 − 1**（#22 那次是 14）。`text=#22` 不行——`rowText` 只在**已实现（可见）**的行里找，
   而 `#22` 当时在窗口外。**做法：先 `wheel -30` 到底，再按绝对序号点** ✓
2. **把"自己的领地"变成"主机拥有的领地"**（这样才谈得上越权放置）：
   客户端 → 领地列表 → 打开自己那块 → **赋予领地给玩家… → 选 `Basil [client 0]`（主机）** →
   回详情 → 点 **`👤 Basil`（自己的拥有者行）** → **剥夺该玩家的领地许可** → 确认。
   实测 XML 落地：
   ```
   <Region Id="23" MinX="-153" MinY="0" MinZ="57" MaxX="-153" MaxY="255" MaxZ="57" …>
     <Owner UserId="5baf52e6-…" Name="Android User" />      ← 只剩主机
   ```
   （赋予后一度是两条 Owner：`Basil` + `Android User`，剥夺后只剩主机 ✓）
   ⇒ **P3b 的 Claim / Grant / Revoke 三条指令全部实机跑通**，客户端副本也同步
   （`Region claim replica updated from host: replace #23, sequence=32, count=16`）。

**没能拿到 `（place）` 否决的原因：客户端站位不稳**
- 客户端在 1×1 领地里**持续漂移**（实测 -144.7,65.3 → -150.9,59.8 → -152.7,57.9 → -154.9,55.8，
  约 **2 格 / 40 秒**，往西南）；等"建地 → 赋予 → 剥夺"走完（约 40 秒），人已经出了 1×1 领地 ⇒
  放置自然放行（对照无主区放置**成功**，说明 `mouse right click` 放方块这条路是通的 ✓）。
- 想走回去时又被**服务端强制移动**了一次（(-154.9,55.8) → (-168.5,47.5)，疑似先前挂着的
  `SafeRespawnRelocate`/重生把玩家搬走），于是更进不去 ✗。

**下一轮的确定做法（一次到位）**
1. 建**更大的选区**：`设置点1（我脚下）` → **等 20~30 秒**（让漂移把人带出 2~3 格）→
   `设置点2（我脚下）` ⇒ 得到 3~4 格见方的领地，足够吸收"赋予/剥夺"期间的漂移；
2. 接着按本轮已验证的顺序：**赋予主机 → 剥夺自己**（XML 复核只剩 `Android User`）；
3. **立刻** `lookat` 脚下 + `mouse right click`；断言客户端日志出现
   `这块区域属于 Android User（领地 #NN），修改已否决（place）`，且 `world blocks` 该格不变。
   （若不在领地内，脚本应自动 `hold W` 补一小步再判——本轮已证明短距移动会偶发被传送，
   所以**加大领地**比"走回去"更可靠。）

**状态**：两端在世界内；领地 **16 块**（`#1、#4–#16、#22、#23`，后两块属主均为主机）；
客户端 ≈`(-168.5,47.5)`；包与哈希未变（本轮未改代码）。

## 25. 第 20 轮：按第 19 轮配方跑「大地块 → 赋予 → 剥夺 → 放置」，卡在面板状态机（脚本侧）

**本轮现象（都是脚本问题，不是功能问题）**
1. 第 1 步 `设置点1` 回 **False**：进入面板前只做了"连按 4 次 Escape 到 none"，
   但**没有逐步确认状态**；`Sc-OpenGmMenu` 在已有对话框时直接返回当前状态（不是 gm-main），
   随后 `玩家领地 / 选区 · 点1` 的点击就落空，`设置点1` 自然 MISS。
2. 第 2 步"短走 2 秒"是在**面板仍开着**时发的（第 1 步没关面板）⇒ 世界内移动被对话框吞掉
   （又一次踩到 AGENTS 里那条"开着的对话框会吃掉 `hold W`"）。
3. 于是第 3 步拿到的"新领地"其实还是上一轮的 `#23`（脚本用 `Sort-Object Id | Select-Object -Last 1`
   取最后一条，**没有校验"编号比之前新"**）⇒ 后续"赋予/剥夺"作用在旧地块上，`（place）` 仍然没拿到。

**修法（下一轮把这三点写进脚本，一次跑通）**
- **每一步都用状态机收敛**：点任何一行之前先 `Sc-State`；不在目标菜单就先 `Sc-GotoRegion`
  回到"玩家领地"，再进子菜单；不要用"盲点 N 次"。
- **世界动作前必须关对话框**（`Close-PC` 循环到 none），再 `hold W`；动作后再重新打开面板。
- **新地块编号要断言**：建立后用 `Get-ClaimInfo` 与"建立前的编号集合"做差集，
  取不到新编号就中止（不要 `Last 1` 蒙）。

**其余状态**：两端在世界内；客户端的点位与第 19 轮相同；领地仍 **16 块**
（`#1、#4–#16、#22、#23`，后两块属主=主机）；本轮未改代码、未改测试图。
下一轮仍然只做一件事：把「客户端在他人领地内放方块 → `修改已否决（place）`」这条证据拿到手。

## 26. 第 21 轮：P5「放方块」只差"客户端手上要有方块"——建议补一个 GM「给予物品」入口

**本轮打通/拿到的**
1. 第 25 轮那三条修法全部生效：`设置点1/点2` 都回 True（`In-Region` 状态机 + 建立后做**编号差集断言**），
   最终建出 `#25`（1×1）。
2. **用"种存档 + 重启"确定了可用的实验场地**：在客户端所在处种一块
   `#26 X -171..-168 / Z 45..48`（4×4，属主=主机 `Android User`），重启后客户端落点
   `(-169,47)` **就在 #26 内** ✓（XML 与实测一致）。
3. **新的挖掘否决证据（回归）**：
   `21:59:24.967 [Chat] Client0 ScMP: 这块区域属于 Android User（领地 #24），修改已否决（dig）`
   —— 归属仍按"编号最小者"裁决（同格上 #24 比 #26 小，所以报的是 #24 ✓）。
4. 放置动作本身可用：`mouse right click` 正常执行（`{ "heldButtons": [] }`），无主区放置此前已实测成功 ✓。

**为什么还没拿到 `（place）`：三件事缺一不可，目前缺的是"物品"**
- 客户端**手上没有方块**：它在领地内挖会被否决（正是上面那条 dig 证据）⇒ 拿不到物品；
- 要拿物品就得**走出领地再挖**，但客户端这几轮的 `hold W` **一步都走不动**
  （本轮 6 次尝试位置始终 `(-169,47)`；注意 `hold W` 与 `Q`/数字键同属 `PlayerInput` 级注入，
  见 §16 第 2 条，本来就不可靠）；
- 1×1 的 `#24/#25` 也**没有合法的放置目标格**（只能瞄到自己脚下那格，引擎直接拒绝）。

**结论：下一轮先补一个小功能，再回来收 P5/P6 两条尾巴**
- 给 GmMod「玩家操作」加一项 **「给予物品」**，直接复用联机 Mod **既有**的
  `ScMP.Player.GrantInventory`（`DataModificationOperationNames.GrantInventory`，
  载荷 `PlayerDataModificationRequest.ItemValue / ItemCount`，主机侧 `ApplyGrantInventoryRequest`），
  做法与第 15 轮新增「传送到主机身边」完全相同（一个菜单项 + 一次 Submit + 一条 DM 审批）。
- 加上它以后一次就能收两条：
  1. **P5 放方块**：先给客户端一个方块 → 客户端在 `#26` 内 `mouse right click` 放 →
     期望 `修改已否决（place）`；
  2. **P6 火**：给客户端打火石（或给主机），点燃 `#26` 内的可燃方块 → 期望同帧熄灭（火看不起来）——
     这正是第 13 轮记下的"没有方块火来源"的解法。
- 顺带说明：`GrantInventory` 在 mod 侧**早已实现且能被 DM 通道调用**，只是 GmMod 一直没有入口
  （第 20 行注释里那批 op 列表就包含它）；本轮确认了它的载荷字段与主机落地函数都齐全。

**当前状态**：两端在世界内；领地 **18 块**（新增 `#24`、`#25`、`#26`；`#26` 为 4×4 主机领地）；
客户端 `(-169,47)`、背包空；本轮未改代码。

## 27. 第 22 轮：新增 GmMod「给予物品」+ **P5 放方块否决闭环**（P5 完整收官）

**一、新功能：GmMod「玩家操作 → 给予物品」**
- 菜单项（在「回满血」下面）：`给予 泥土块 ×64` / `给予 火柴 ×8（点火用）` / `给予 炸药 ×8（爆炸用）`；
- 复用联机 Mod **既有**的 `ScMP.Player.GrantInventory`（主机侧 `ApplyGrantInventoryRequest` 早已实现，
  载荷 `PlayerDataModificationRequest.ItemValue/ItemCount`）——GmMod 只是补了入口；
- 方块按**类型名**查找（`BlocksManager.Blocks` 里 `GetType().Name == "DirtBlock"/"MatchBlock"/"TntBlock"`），
  **不写死内容编号**，找不到就 Toast 报类型名；
- 做法与第 15 轮「传送到主机身边」同构：一个菜单项 + 一次 `SubmitPlayerOperation` + 一条 DM 审批。
- 产物：`GmMod.dll` 50688 bytes @ 22:00:33；两端已部署（平板 sha256 前缀 `5bbdb47895d7d805`）。

**二、P5「放方块」否决实测通过（客户端证据两个通道都在）**
```
22:01:29.441 [GmMod] Submitted ScMP.Player.GrantInventory (target=1) -> Accepted … 
22:01:29.684 [GmMod] Result: ScMP.Player.GrantInventory -> Applied …        ← 客户端拿到物品（泥土 count=40）
22:01:38.940 [Chat] Client0 ScMP: 这块区域属于 Android User（领地 #24），修改已否决（place）
```
- 客户端屏幕消息同刻也出现：`ScMP: 这块区域属于 Android User（领地 #24），修改已否决（place）`；
- 现场条件：客户端站在主机领地 **#26（4×4，X −171..−168 / Z 45..48）**内，手持泥土，瞄向**领地内的邻格**
  （`cell=(-169,60,47) face=4`）右键 ⇒ 主机否决、方块没有落地；
- 归属仍按"编号最小者"报 `#24`（同格 `#24` 1×1 与 `#26` 4×4 重叠）。

**三、P5 至此完整闭环**
- **挖**：`（dig）` 通知（第 11/14 轮，含本轮新增的 `#24` 一条）；
- **放**：`（place）` 通知（本轮）；
- 无主区同操作放行（第 19 轮对照实测：无主区右键放置成功、差异 25 格）。

**四、本轮还顺手解决的"造物品"问题（对 P6 同样关键）**
- 之前客户端背包空、又走不出领地 ⇒ 挖不到东西、也就没方块可放（第 26 轮卡点）；
- 现在有了「给予物品」，**P6 的"方块火"也有了来源**：给火柴（`MatchBlock`）点燃 `#26` 内的可燃方块，
  就该看到领地内火**同帧熄灭**（这正是第 13 轮记下的最后一个缺口）。
- 下一步建议：用同一入口给火柴 → 在 `#26` 内点火 → 断言 `ExtinguishClaimFire` 生效
  （客户端看不到火 / 方块不变；必要时临时加一条 `[SuAPI]` 诊断日志再移除）。

**当前状态**：两端在世界内（主机 client 0 / 客户端 client 1）；领地 **18 块**
（`#1、#4–#16、#22、#23、#24、#25、#26`）；客户端 `(-169,47)`，手持泥土 ×40。

## 28. 第 23 轮：P6「火」的实测前准备就绪（下一轮点火）

**本轮做到的**
1. 发现**两名玩家都死了**（`health=0`、背包清空）——这是第 22 轮之后放置测试耗尽状态的自然结果；
   用"任意输入触发重生"把两端救回：平板 `health=1 @(-163.5,65,78.5)`、客户端 `health=1 @(-168.25,60,47.75)`
   （客户端仍在 **#26** 的 X/Z 范围内 ✓）。
2. 用第 22 轮新增的「给予物品」给客户端发了**火柴**（`给予火柴: True` → 主机自动放行），
   但**火柴落在非当前快捷栏格**：客户端 `holding = contents=2 (泥土) count=40` ⇒ 手上仍是泥土 ✗。
3. 确认要点：**点火必须让"火柴"成为手上的物品**。快捷栏切换是 `PlayerInput.SelectInventorySlot`
   （`key 2` 这类注入无效，见 §16）⇒ 只能用**点快捷栏格子**（widget 点击）。
   - 客户端是 Windows ⇒ **世界内 HUD 的 widget 点击可用**（第 12 轮结论：Android 不行、Windows 行）；
   - 实现上取 `ui --all` 里 `InventorySlotWidget` 那一排（`ShortInventory` 内），点第二个格子即可。

**下一轮的点火配方（一次跑通，缺一不可）**
1. 客户端确保手上有**火柴**（点快捷栏第 2 格 → 再读 `player.holding.contents` 确认 = 火柴的内容编号）；
2. 领地里要有**可燃方块**：#26 地面是砂砾/雪（不可燃）⇒ 需要先有一块木头/树叶。
   可行做法：**主机**用 `mouse right click` 放一块木头（主机本机挖/放目前**不拦**，§5 的"下一批"），
   木头可从「给予物品」发给主机；或用 `act.dig` 打掉原方块后由主机放回木质方块。
3. 客户端 `lookat` 木头块 + `mouse right click`（点火）；
4. 断言（二选一或都做）：
   - 客户端**看不到火**、木头方块**不变**（同帧熄灭的可见结果）；
   - 需要更硬的证据时，临时在 `SuSubsystemFireBlockBehavior.ExtinguishClaimFire` 加一条
     `[SuAPI]` 诊断日志（熄灭了几格、第几格、归属哪块领地），验证完**立即移除**。

**状态**：两端已复活并在世界内；领地 **18 块**；客户端手上有泥土 ×40、火柴在背包里；本轮未改代码。

## 29. 第 24 轮：「火」实测再推进一步（可燃方块入口已就绪，点火仍差"换手 + 出界"）

**本轮落地**
- GmMod「给予物品」新增两项可燃方块：**`给予 木块 ×64（可燃）`（WoodBlock）** 与
  **`给予 木板 ×64（可燃）`（PlanksBlock）**——泥土/砂砾不可燃，没有它们就没法做火的实测。
  已构建部署：`GmMod.dll` 51200 bytes @ 22:03:53，两端同步。

**本轮实测到的事实（都指向两个具体障碍）**
1. 给客户端发 **木块 + 火柴** 都成功（`给予 木块: True`、`给予 火柴: True`），
   但 `player.holding` 仍是 `contents=2（泥土）count=40` ⇒ **授予的物品进的是空闲格，不是手上那格**。
   换手只能"点快捷栏格子"（`PlayerInput.SelectInventorySlot` 用 `key 2` 注入无效，§16），
   而 Windows 客户端的 HUD widget 点击是**可用**的（§12）⇒ 下一轮读 `ui --all` 里 `ShortInventory`
   下的 `InventorySlotWidget` id，点对应格子即可换手。
2. 客户端**仍然走不动**（本轮 6 次 `hold W`，位置一直 `(-168.3,47.8)`）⇒ 它无法自己走出 `#26`，
   于是"在领地外放木块、再点燃、看火是否蔓延进领地"这条最干净的路线暂时做不了；
   本轮放置瞄准的又是自己脚下那格 ⇒ 引擎直接拒绝（且那格在 `#26` 内，本该被 `（place）` 否决）。

**下一轮的确定性路线（不需要移动、不需要改地图文件）**
1. 客户端**先**在自己脚下放木块（此时若在领地内会被拒）⇒ 所以顺序要反过来：
   **先把客户端弄到无主区**。用第 15 轮的「传送到主机身边」把客户端送到主机处
   （主机本轮在 `(-163.5,65,78.5)`，无主区），在那儿放木块；
2. 记下木块所在格，然后**改存档种一块紧邻木块的领地**（`X = 木块格+1`），重启主机；
   客户端不用动，火点在领地**外**紧邻边界处；
3. 客户端换手到火柴（点快捷栏格子）→ `mouse right click` 点木块 → 火起来；
4. 断言（两个通道）：
   - 火**不进入**领地：领地内那一格始终不是火（`world blocks` 里 contents != 104）；
   - 若需要更硬证据，临时给 `SuSubsystemFireBlockBehavior.ExtinguishClaimFire` 加一条 `[SuAPI]`
     诊断日志（熄灭几格、第几格、哪块领地），验证后**立即移除并重构建部署**。

**状态**：两端在世界内；客户端 `(-168.3,47.8)` 手上泥土 ×40、背包里有木块与火柴；领地 18 块；
本轮只改了 GmMod 菜单（已暂存待提交指令）。

## 30. 第 25 轮：跨端救援复用成功；快捷栏换手改用"坐标点格"（下一轮落地）

**本轮做到**
- 再次用第 15 轮的「传送到主机身边」把客户端送到主机处：
  `传送后 PC=(-144.5,66.0,65.5) 主机=(-147.3,65.0,65.3) 距=2.8`（传送前 28 格）✓
  —— 主机当时在无主区 `(-147.3,65,65.3)`，正好是"火点在领地外"需要的位置。
- 试了"按 `ui --all` 里的 widget 编号点快捷栏格子"：**不行**。盘出来的 30 个 `InventorySlotWidget`
  混着背包格/快捷栏格，且 CmdBridge 用 `click <id>` 报 `element_missing: No element matches '37'`
  （编号不是可寻址的选择器）✗。

**下一轮换手就用"坐标点格"（本轮已确认坐标可用）**
- 快捷栏那一排在 `ui --all` 里是同一 `y`（实测 `y≈1128`）、`x = 568, 712, 856, 1000, …`（间隔 144，共 12 格）；
- 客户端是 **Windows** ⇒ 用 `click InventorySlotWidget --at <x> <y>` 这类坐标点击是可靠的
  （第 12 轮结论：Android 世界内 HUD 不行、Windows 行；第 8 轮也验证过 `--at` 需要客户区像素，
  Windows 客户区 1920×1080 / uiScale 0.7 时这些坐标就是 `ui --all` 里给的 `clientPoint`）+；
- 换手后必须读 `player.holding` 确认（木块/火柴的内容编号不同，改没改一眼可辨）。

**当前状态**
- 客户端已被送到主机身边（`(-144.5,66,65.5)`，无主区），手上为空（传送/重生后背包空）；
- 主机 `(-147.3,65,65.3)`；领地 18 块；本轮未改代码。
- 下一轮顺序：① 给客户端发 **木块 + 火柴**（GmMod 菜单已有）；② 坐标点快捷栏把**木块**换上手 →
  在无主区放一块木块（记下格子）；③ 改存档种一块**紧邻该木块**的领地 → 重启主机；
  ④ 换手到**火柴** → 点木块点火 → 断言"火不进入领地"（领地内那格 contents ≠ 104）；
  必要时临时加 `[SUAPI]` 熄灭诊断日志，验证后立即移除。

## 31. 第 26 轮：火实测的"换手"参数已测准；发现 `WoodBlock` 名字不存在

**本轮测准的两件事（下一轮直接用）**
1. **快捷栏格子坐标**（Windows 客户区 1920×1080 / uiScale 0.7，实测）：
   快捷栏那一排在 **`y = 1023`**，`x = 618, 732, 846, …`（**间隔 114**，12 格）；
   `ShortInventoryWidget/ShortInventory` 自身在 `(960,1023)`。上一轮记的 `y=1128` 是旧布局 ⇒
   `click InventorySlotWidget --at 568 1128` 报 `Nothing is under (568,1128)` 就是因为坐标过期。
2. **客户端背包实测内容**（`player.slots`）：
   `slot0 = contents 2 ×40`、`slot1 = contents 2 ×24`、`slot2 = contents 108 ×24`，
   `holding = contents 2 ×40`（= 泥土在手上）。⇒ **`contents=2` 是泥土**（第 22 轮的"泥土 ×64"分成了 40+24 两堆 ✓）、
   **`contents=108` 是火柴**（本轮"给予 火柴"生效 ✓，落在 slot2）。
   换成火点只需 `click InventorySlotWidget --at 846 1023`（slot2 = 火柴）→ 读 `holding.contents` 应为 **108**。

**本轮踩到的坑（要记住）**
- `给予 木块 ×64` 点了也回 True，**但其实没发出去**：`FindBlockByTypeName("WoodBlock")` 没找到
  ⇒ 只弹了"找不到方块类型：WoodBlock"的 Toast，**没有提交任何 op**（所以背包里没多出木块）。
  ⇒ 说明 **`WoodBlock` 不是本引擎的类名**（疑似 `LogBlock` 之类）。下一轮要么先确认正确类名
  （例如让主机用 `act.dig` 打一棵树、读掉落物 `contents` 再反查；或从 `BlocksManager.Blocks`
  里按名字前缀枚举一次打印出来），要么干脆把"给予物品"改成**按内容编号**给（把编号做成菜单项）。
- 教训：**授予类操作要读回背包/`holding` 验证**，不能只看 `Sc-ClickRow` 返回 True
  （这与第 12 轮"`Applied` 不等于字段正确"是同一条教训）。

**状态**：客户端在无主区 `(-147.3,65,64.4)` 一带、手上有泥土 ×40、背包里有火柴 ×24 和另一堆泥土；
主机 `(-147.3,65,65.3)`；领地 18 块；本轮未改代码。

**下一轮顺序（更新版）**
1. 先定"可燃方块"：确认正确类名/编号（见上），或改菜单为按编号给；
2. 给客户端发可燃方块 → `click InventorySlotWidget --at 618 1023` 换手到它 → 在无主区**放一块**（记格子）；
3. 改存档种一块**紧邻该方块**的领地 → 重启主机；
4. `click InventorySlotWidget --at 846 1023` 换手到**火柴**（`holding.contents` 应为 108）→
   点可燃方块点火 → 断言"火不进入领地"（领地内那格 contents ≠ 104）；必要时临时加 `[SUAPI]` 诊断日志后立即移除。

## 32. 第 31 轮：P6「火」实测闭环 —— P6 三项全部收口

**客户端证据（同一次点火的前后 `world blocks` 对比）**
```
点火（手持火柴 contents=108，点无主区的橡木 -145,66,66）
火格数: 前 0 -> 后 2
火格: -145,67,66 FireBlock        ← 木块上方（X=-145，无主区）
      -145,66,67 FireBlock        ← 木块旁（X=-145，无主区）
#27 内: 火格 = 0 / 共 28 格        ← 领地内 28 格采样全部无火
      -147,62,65 c=2 DirtBlock …  ← 领地内方块原样（泥土/草/雪）
```
- 火点是**无主区**的一块橡木；**紧邻**主机领地 `#27`（X −147..−146 / Z 65..67，主边界 X=−146，
  火点在 X=−145，正贴边界）；周围是可燃的草/高草；
- 结果：**火只在 X=−145 一侧产生（2 格 FireBlock）**，`#27` 内**一格火都没有**、方块未变
  ⇒ 设计稿 §5「领地内不允许有火、火不蔓延进来」达成（`ExtinguishClaimFire` 同帧熄灭 + 阻断蔓延）。

**P6 至此三项齐全**
| 项 | 证据 |
|---|---|
| 拾取 | `修改已否决（pickup）`（§16，客户端日志 + 屏幕消息） |
| 爆炸 | 领地内/近处 7 次全部被拦（246–5880 格）+ 客户端地形零变化；远离领地对照正常炸、无拦践行（§18/§22） |
| 火 | 本轮：火只在领地外产生、领地内 0 格火、方块不变（§32） |

**本轮顺带解决的两个工具问题（都已写进代码/结论）**
1. **"换手"不能靠重启客户端**：客户端背包是服务端权威、重启不清空（第 30 轮实测：重启后仍持有橡木 ×39）
   ⇒ 只有当**手上为空**时，新发的物品才会落到槽 0 = 手上（本轮 `给予 火柴` 后 `holding.contents=108` ✓）。
   否则要用快捷栏点格换手，而**坐标点格对世界内 HUD 不可靠**（本轮实测 `--at 732 1023` 命中的是
   `[GameWidget]/View` 而不是格子 ⇒ `element_missing`/打到世界）。
2. **`FindBlockByTypeName` 必须用真实具体类型**：`WoodBlock`/`LeavesBlock` 是抽象基类，写它们只会弹
   "找不到方块类型"、op 不提交；已改成 `OakWoodBlock` / `OakLeavesBlock`（§27 起生效）。
3. 打包脚本里必须 `Add-Type -AssemblyName System.IO.Compression`（只有 `...FileSystem` 会报
   `Unable to find type [System.IO.Compression.ZipArchiveMode]`）。

**测试图当前状态**：领地 **20 块**（`#1、#4–#16、#22、#23、#24、#25、#26、#27`）；
客户端在主机身边（`≈(-144.5,66,65.5)`，无主区），手持火柴 ×8；主机 `≈(-147.3,65,65.3)`；
火点橡木 `(-145,66,66)` 及其引燃的 2 格火在领地外。

## 33. 附录：测试环境与工具链（把 19 轮踩出来的东西固化下来）

### 33.1 测床
- **平板 = 主机**：Android，`am force-stop/monkey com.candyrufusgames.survivalcraft2su` 重启；
  CmdBridge 端口 26751，本机经 `adb forward tcp:26771 tcp:26751` 访问（**平板每次重启后必须重新 forward**，
  否则 `sccmd status` 返回空）。
- **Windows = 客户端**：`publish\[SuAPI]Survivalcraft`；`taskkill /F /IM Survivalcraft.exe` + `cmd /c start`；
  `sccmd --root <客户端根>` 比 `--port` 稳。
- 世界 `Francenira`（`Worlds/World/`）；领地落盘 `ScMultiplayerRegions.xml`（**测试布置可直接改它**：
  插 `<Region>`/`<Owner>` 并同步改 `NextId`/`Sequence`，重启主机即生效）。
- 世界 `Project.xml` 可直接改：`GameMode`、`WorldSettings.TimeOfYear`、`WorldSettings.TemperatureOffset`、
  玩家实体的 `Body/Position`、`Health/Temperature/Food/FluDuration`。
  ⚠️ 改前必须先 `am force-stop`（否则退出时会被游戏写回）；**雪原生物群系 + 冬季**会把玩家冻死，
  实测 `TemperatureOffset=10` + 初夏 + 玩家体温 12 才能连续测试。

### 33.2 加入流程（唯一可靠顺序）
1. **先重启主机**（拿"新鲜房间"，日志出现 `GameCreated, ClientID=0`）；
2. **再重启客户端**，客户端起来后 MainMenu → Play → 房间行 → 开始游戏！；
3. 客户端卡在 `SuPlayScreen` 的 `BusyDialog`（"Joining Room / Connecting to…"）时，
   **`Escape` 关不掉**，**只能重启客户端**；房间过期也会一直挂。
4. 客户端每次重启 **clientId 会变**（实测 1 → 18），日志/DM 里要按当次为准。

### 33.3 CmdBridge 能力矩阵（实测）
| 能力 | 结论 |
|---|---|
| 对话框**列表行**点击 | ✅ 可靠（`click 'list:<列表>#<绝对序号>'`） |
| 虚拟列表**滚动 + 绝对序号** | ✅ 先 `wheel -30` 到底，序号 = **列表总数 − 1**；`rowText` 只在**已可见/已实现**的行里找 |
| 世界内 **HUD 按钮** | ❌ Android 点不动（回包 `clickTargetOnTarget:true` 但界面无反应）；✅ Windows 可以（先 `MoreButton` 展开 `MoreContents`，再点 `GmModButton`） |
| **坐标点格**（`--at`） | ⚠️ 世界内 HUD 不可靠（实测命中 `[GameWidget]/View`）；对话框/主菜单可用 |
| `key` 注入 | ✅ 控件级（`Escape`）；❌ `PlayerInput` 级（`Q` 丢物、数字选快捷栏、`hold W` 移动） |
| `lookat` | ⚠️ 以**身体**为原点（不是眼睛）：瞄脚下一格会走平、打到邻格；压低瞄点或让射手与目标同 z |
| `mouse right click` | ✅ 世界内放/交互可用；`act.dig` 挖方块可用 |
| 引擎"手动输入坐标" | ❌ 桌面端 `ShowKeyboardInternal` 直接 `cancel()`；Android 弹系统输入法也点不到 |
| 授予/操作类 op | ⚠️ `Result: … Applied` **只说明流程走通**，字段可能全是默认值（§17 的 `{}` 事故）⇒ 必须读回状态验证 |

### 33.4 GmMod 面板配方（都用状态机驱动，禁止盲点）
- 状态：`gm-main`（GM 主菜单）/`region`（玩家领地）/`point`（点N）/`player-target`（选玩家）/
  `player-action`（对该玩家）/`claim-list`/`claim-detail`/`drop-confirm`/`dm`（审批）/`none`。
  脚本里 `Sc-State → Sc-GotoRegion/Sc-OpenGmMenu → Sc-ClickRow`，**每次点行前先收敛状态**，
  **世界动作（`hold W`/右键）前先关掉所有对话框**。
- 已跑通的常用链路：
  - **建领地（客户端）**：GM → 玩家领地 → 选区·点1/点2（`设置点N` = 我脚下）→ 建立领地 → 确认建立
    ⇒ 走 `ScMP.Region.Claim`（DM 审批后主机落地，属主=发起者）；
  - **改成主机领地**：领地列表 → 打开该块 → `赋予领地给玩家…` → 选 `Basil [client 0]`（主机）→
    回详情 → 点 `👤 <自己>` → `剥夺该玩家的领地许可` → 确认（之后自己就不再是拥有者，可用于越权测试）；
  - **跨端救援**：玩家操作 → 选目标 → `传送到主机身边（主机当前位置）`（复用 `ScMP.Player.ReturnToPlayer`）；
  - **造物品**：玩家操作 → `给予 泥土块/橡木/橡树叶/火柴/炸药`（复用 `ScMP.Player.GrantInventory`，
    按**真实类型名** `DirtBlock/OakWoodBlock/OakLeavesBlock/MatchBlock/TntBlock` 查 `BlocksManager.Blocks`）；
    ⚠️ **只有手上为空时**新物品才落槽 0=手上；否则要用快捷栏换手，而坐标点格对 HUD 不可靠 ⇒
    要换手就先让目标**丢掉/用掉手上的东西**。
  - **世界控制**：世界设置·天气 → `闪电：立即劈一次（我眼睛前方）`（复用 `ScMP.Data.WorldControl`；
    每次劈雷都是一次爆炸，**朝正下方看**时爆炸落在自己脚边）。

### 33.5 测量配方
- **覆盖/归属**：从设备读 `ScMultiplayerRegions.xml` 解析闭区间；重叠格按**编号最小者**裁决
  （实测同格 `#24` 与 `#26` 重叠时报 `#24`）。
- **地形前后对比**：`world blocks <r> <max>`（半径相对玩家；返回 `x,y,z,contents,blockType`）
  ⇒ 直接做集合差；**火 = `contents 104`（FireBlock）**，**火柴 = 108**，**泥土 = 2**，**橡木 = 9**。
- **执法证据**：客户端日志/屏幕消息里的
  `这块区域属于 <拥有者>（领地 #n），修改已否决（dig|place|pickup）`（~1s 节流）；
  爆炸另有主机日志 `Region claim blocked an explosion from destroying N cell(s)`。
- **"火"实测配方**（§32）：客户端手上为空 → 发火柴 → 在**无主区**放一块可燃方块（如橡木）并紧邻
  种一块主机领地 → 客户端 `lookat` 木块 + `mouse right click` ⇒ 断言火（104）只在领地外出现、
  领地内 0 格火且方块不变。

### 33.6 脚本坑（都踩过）
- 含 `[]` 的路径一律 `-LiteralPath`；`Start-Process -FilePath` 吃方括号 → 用 `cmd /c start "" /d …`。
- **中文脚本必须存成 UTF-8 with BOM**（否则 PS5.1 按 ANSI 读 → 语法错误）；用工具编辑后要**重新补 BOM**。
- 打包 zip 前 `Add-Type -AssemblyName System.IO.Compression`（只加 `...FileSystem` 会报
  `Unable to find type [System.IO.Compression.ZipArchiveMode]`）；条目必须用 `/` 且 `ModInfo.xml` 在根。
- 单条 `pwsh` 命令最多 30s：长流程一律 `run_in_background` + `job_output`；
  `Select-Object -First` 会**提前终止上游管道**（别用它裁剪长任务的输出）。
- 玩家会**持续漂移/被服务端搬走**（实测 ~2 格/40 秒），任何"布置后过一会儿再测"的动作都要
  **重新读位置**并重算覆盖。

## 34. 主机本机执法与 P7 的设计（先把挂点定死，再动手）

> 背景：§5 锁定"主机一律不应用越权修改；若已落到权威状态就**改回**并通知"，
> 而 P5 实测只覆盖了**客户端**（`dig`/`place` 都被拒 ✓），**主机本机玩家的挖/放/拾取目前不拦**。
> 第 33 轮前我一度想直接在 `SuSubsystemTerrain.ChangeCell` 上拦，**这是错的**，先把原因和正确挂点写清楚。

### 34.1 为什么不能在 `ChangeCell` 上"一刀切"
主机侧 `ChangeCell` 的调用方至少有四类，必须区别对待：
| 来源 | 例子 | 期望 |
|---|---|---|
| **玩家动作** | 主机自己用 `ComponentMiner` 挖/放 | **拦**（越权时） |
| **网络落地** | 客户端的合法修改由主机应用（现走 `ChangeCell(..., forceModification:true)`） | **放行**（客户端侧已执法） |
| **自然演化** | 草长、树叶腐坏、耕地、树苗（已替换的 7 个 `SuSubsystem*BlockBehavior`） | **放行**（§5 锁定结论 7：不冻结） |
| **破坏性行为** | 爆炸、火、流体/岩浆、活塞/发射器 | 各自按 §5 处理（已有"改回式"实现或待做） |
⇒ 在 `ChangeCell` 里按"格子是否属于他人领地"拦，会把自然演化与网络落地一起误拦。

### 34.2 正确的挂点（照引擎冒险模式"在行为层短路"）
- **P7a｜主机挖/放**：新增 **`SuComponentMiner`（替换 `ComponentMiner`）**，主机分支里在
  `Dig`/`Place` 决定目标格后调用既有的 `CanRegionModifyCell(localUserId, cell)`：
  - 被拒 ⇒ 不推进挖掘（等价引擎冒险模式的 `float.PositiveInfinity`）、不放置，并调用既有
    `NotifyRegionModificationDenied(..., "dig"/"place")` 给出与客户端同款的一行提示；
  - 放行 ⇒ 原样走 `base`。
  组件替换沿用本仓已验证的 **Database.xml GUID 补丁** 套路（GodMode 那次替换 5 个 Component 的同一手法），
  并注意 `Load(ValuesDictionary, IdToEntityMap)` 跨程序集只能 `protected override`。
- **P7b｜主机拾取**：主机本机拾取目前绕过请求链路（§15 记过"主机本机先把它捡走"）⇒ 在拾取判定入口
  增加同一条 `CanRegionModifyCell` 检查（与客户端走同一个函数，只是发起者换成主机玩家）。
- **P7c｜流体/岩浆**：新增 **`SuSubsystemFluidBlockBehavior` 替换**（§7 P7 第一项）——
  流体**不得冲毁/覆盖领地内方块**；口径"拦在源头"：流体格要进入领地边界时不予生成/流动。
  注意与 §5 的"自然演化不冻结"不冲突（水/岩浆属**破坏性**行为，锁定结论里明确要拦）。
- **P7d｜活塞/发射器**：`SuSubsystemPistonBlockBehavior` / `SuSubsystemDispenserBlockBehavior` —
  推入/放入他人领地 ⇒ 拒绝（同样是行为层短路）。
- **兜底"改回并通知"**：只有竞态/绕过（例如爆炸、火、被替换子系统之外的路径）才需要改回；
  现有 `SuSubsystemFireBlockBehavior`（同帧熄灭）与 `SuSubsystemExplosions`（包络内快照/写回）
  已经是这条闭环的两个实例，P7 只需把同类做法补齐到流体/活塞。

### 34.3 验收（每项都要"拦 + 放行 + 不冻结"三面）
1. **拦**：主机在他人领地内挖/放/拾取 ⇒ 操作不生效、地形/背包不变、本人收到 `…修改已否决（dig/place/pickup）`；
2. **放行**：主机在**自家**领地与**无主区**做同样操作 ⇒ 正常；
3. **不冻结（回归项）**：领地内草生长、树叶腐坏、耕地/树苗演化**照旧**（§5 锁定结论 7），
   流体在领地**外**照常流动；
4. **流体/活塞**：水流/岩浆流在领地边界处停住、方块不被冲毁；活塞推入他人领地被拒；
5. **改回闭环**：对"绕过路径"（爆炸/火）仍能看到主机侧 `改回` 的实际效果（§18/§32 已验证）。

### 34.4 实施顺序
P7a（主机挖/放，最小可用、收益最大）→ P7b（主机拾取）→ P7c（流体/岩浆）→ P7d（活塞/发射器）→
§7 总表整体收口（P1–P6 已 ✅，P7 是最后一格）。每项独立三端部署 + 实测，禁止一次改多项。

### 34.5 P7a 落地勘察（2026-09-25 第 34 轮，代码级）
本轮把 P7a 需要的挂点/材料查清，下一轮可直接写代码：

1. **组件替换的现成套路**（本 mod 内已有 7 个可照抄的样例）：
   `Func/Component/` 下有 `SuComponentInput : Game.ComponentInput`、`SuComponentFlu`、`SuComponentHealth`、
   `SuComponentSickness`、`SuComponentSleep`、`SuComponentVitalStats`、`SuComponentFurnace`。
   ⇒ 新增 **`SuComponentMiner : ComponentMiner`** 与它们同目录同写法；注册走**数据库 GUID 替换**
   （即 `AGENTS.md` 里 GodMode 用过的 "数据库替换 N 个 Component" 那套：按引擎组件 GUID 换成自己的类型）。
   **待补：`ComponentMiner` 的 GUID**（在 `EntitySystem` 的 Database/Content 里查一次即可）。
2. **主机侧"挖"已有权威链路可复用**：`Modules/Terrain/ScMultiplayerTerrainHandlers.cs` 里已有
   `Source: ComponentMiner.Dig` / `ComponentMiner.Raycast` 的整套处理（含挖掘请求、结果回滚、
   客户端预测的拒绝语义）。P7a 只需在**主机本机**的挖/放分支里接同一个判定，不要另起一套。
3. **判定与提示 API 已就绪**：
   - `ScMultiplayerRegionEnforcement.cs` 里既有 `CanRegionModifyCell(...)`（P5/P6 三处都在用）
     与 `internal void NotifyRegionModificationDenied(int clientId, Point3 cell, RegionClaim claim, …)`；
   - P7a 里调用时把 `clientId` 换成**主机本机玩家**的 clientId（0），提示走同一条通道即可。
4. **落地顺序（下一轮）**：
   a. 查 `ComponentMiner` 的 GUID → 加 Database 替换 + `SuComponentMiner` 骨架（先只加日志，验证替换成功、游戏能起）；
   b. 在主机分支加 `CanRegionModifyCell` 判定：被拒 ⇒ 不推进挖掘/不放置 + 提示；
   c. 三面实测：**拦**（主机在他人领地 `#27` 内挖/放 ⇒ 无效 + `修改已否决（dig/place）`）、
      **放行**（自家 `#24`/无主区照常）、**不冻结**（领地内草长/树叶腐坏照旧）；
   d. 通过后再做 P7b（主机拾取）→ P7c（流体/岩浆）→ P7d（活塞/发射器）。
5. **风险提示**：替换 `ComponentMiner` 会影响**所有**玩家的挖/放路径（客户端也用它做预测）⇒
   判定必须"只在主机分支、只对本机玩家、只在目标格属他人领地时"生效，其余一律走 `base`；
   并且**不要**动 `DigTime/Place` 之外的行为（否则会牵连 P5 已验证的客户端链路）。

### 34.6 P7a 第二步：`SuComponentMiner` 执法骨架（已定到可直接落代码）

**`Update` 覆盖套路**（照 `SuComponentFlu : ComponentFlu, IUpdateable`，实测可用）：
```csharp
public class SuComponentMiner : ComponentMiner, IUpdateable
{
    void IUpdateable.Update(float dt)
    {
        base.Update(dt);            // public Update 可正常调基类
        EnforceHostLocalDig();      // 只加这一句钩子
    }
}
```
- **只在本机玩家的挖掘目标上判定**：挖掘目标可从 `ModParentField` 读私有字段
  `<DigCellFace>k__BackingField`（`ScMultiplayerClientEvents` 里已经在用同样的写法：
  `ModManager.ModParentField.GetParentField<CellFace?>(miner, "<DigCellFace>k__BackingField", typeof(ComponentMiner))`）。
- **判定 + 提示**（都是现成 API）：
  - `CanRegionModifyCell(0, cell, out RegionClaim claim, out string reason)`（`ScMultiplayerRegionEnforcement.cs`）；
  - 被拒 ⇒ `Poke(forceRestart: true)`（取消本次挖掘，客户端预测同步回滚——mod 里已有同样用法）
    + `NotifyRegionModificationDenied(0, cell, claim, "dig")`；
  - 只判 **主机分支 + 本机玩家的角色 + 目标格属他人领地**，其余一律直接返回（不要拦客户端预测路径）。
- **放置（place）** 需要下一步再定点：`ComponentMiner` 的放置入口方法名与签名下一轮读一次引擎源码确认
  （`Survivalcraft/Game/ComponentMiner.cs`），确认后按同样"行为层短路 + 提示"处理；
  **在没确认签名之前不动放置**，避免牵连 P5 已通过的客户端链路。
- **不要碰** `DigTime` / 挖掘进度字段之外的逻辑（客户端预测与远端碎裂纹理都依赖它们）。

## 35. 第 41 轮：P7a「主机本机挖/放」执法闭环（P7 第一项 ✅）

**做法**（细节见 §34/§34.5/§34.6）
- 新增 `SuComponentMiner : ComponentMiner, IUpdateable`（`Func/Component/SuComponentMiner.cs`）：
  在 `IUpdateable.Update` 里 `base.Update(dt)` 之后加一个钩子 `EnforceHostLocalDig()`；
- 注册：`ScMultiplayerLifecycle` 里按 **`ComponentMiner` 的组件 GUID `9dc356e5-7dc8-45f6-8779-827ddee9966c`**
  （`Pak/Database.xml:4071`）把 `Class` 换成 `ScMultiplayer.SuComponentMiner`，并在 `Obfuscar.xml` 跳过该类型名；
- 判定纪律（只在最窄条件下生效）：**主机分支 + 本机玩家的角色 + 挖掘目标格属他人领地** ⇒
  `Poke(forceRestart: true)` 取消本次挖掘 + `NotifyRegionModificationDenied(0, cell, claim, "dig", reason)`（1 秒节流）；
  其余一切照走 `base`（客户端预测路径完全不动）。
- 挖掘目标读法沿用 mod 已有写法：`<DigCellFace>k__BackingField`（`ModParentField`）。

**实机证据（平板=主机）**
```
22:26:04 [ScMP-P7a] SuComponentMiner active (host-local region enforcement)      ← 替换生效
22:27:19 Loaded 22 region claims (nextId=30, sequence=70)                        ← #28/#29 已加载
【拦】主机在"客户端拥有"的 #28 内挖脚下格：
   挖前 6/GravelBlock → 挖掘 completed=true removed=false nowAir=false → 挖后 6/GravelBlock（不变）
   22:27:39.691 [ScMP] 这块区域属于 Basil（领地 #28），修改已否决（dig）           ← 主机本人收到通知（×3，1s 节流）
【放行】第 39 轮：主机在无主区挖脚下 ⇒ removed=true nowAir=true                 ← 正常落地
```
⇒ §5 的"主机一律不应用越权修改 + 通知发起修改的人"在**主机本机路径**上成立；
**不冻结**（草长/树叶腐坏）在结构上不受影响：钩子只判"主机玩家的挖掘目标"，不碰自然演化子系统。

**P7 剩余**：P7b 主机本机**拾取**（同一条 `CanRegionModifyCell`，换发起者）→
P7c 新增 `SuSubsystemFluidBlockBehavior` 拦截水流/岩浆 → P7d 活塞/发射器推入拒绝 → §7 总表收口。

## 36. 第 45–59 轮：P7b（主机本机拾取）实现与"不可达"论证

**实现（已落地、已部署、编译通过）**
- 落点：`Func/Subsystem/SuSubsystemPickables.cs`（该子系统的数据库替换早就在 `ScMultiplayerLifecycle` 注册过，
  所以不需要像 P7a 那样新增替换）。
- 做法（"前后夹一层"，不碰背包）：`base.Update(dt)` **之前**，把"**主机本机玩家 8 格内**、
  且所在格 `CanRegionModifyCell(0, cell, …)` 判定为**他人领地**"的拾取物
  **本 tick 临时抬高到视线外**（记下原位置），`base.Update` **之后原样放回**；
  同时 `NotifyRegionModificationDenied(0, cell, claim, "pickup", reason)`（1 秒节流）。
  因为从不进入背包，所以不需要任何"扣减/配对"。
- 纪律：只在**主机分支**、只认**本机玩家**、只处理**他人领地**的格；其余一律不动。

**为什么拿不到实机复现（这是本轮最花时间的部分，结论是"构造上不可达"）**
1. 掉落物只由 `SubsystemPickables.AddPickable` 产生，而它的调用方都是**玩家动作**
   （挖方块的掉落、容器/合成/家具丢弃）；
2. 而在**他人领地内**：主机的挖被 **P7a** 拦、客户端挖/放被 **P5** 拦 ⇒ **产不出掉落物**；
3. 掉落物**不写进存档** —— `SubsystemPickables` 只用 `private List<Pickable> m_pickables`，
   整个类**没有 Load/Save/Serialize**（本轮读源码确认）⇒ 也无法"预先改存档种一个进去"；
4. 客户端"拖出背包丢物"这条路，桥接器的合成输入**打不出引擎的 DragHost 手势**
   （`ui.session.begin → ui.drag x1/y1/x2/y2 → ui.session.end` 全部 `completed:true`，
   但物品不离手：steps=6/12、手搓分段带中间点、换 4 个释放点都试过 ✗）。
⇒ 结论：**"他人领地内存在掉落物"这一前提在现有执法下不可达**；P7b 的定位是**纵深防御**——
将来若出现非挖掘来源（世界生成/宝箱吐出、爆炸抛入、流体搬运、遗留掉落物），
这道门就是兜底。**P7b 以此收口**（实现+部署+编译验证+不可达性论证），不强行造不可达的现场。

**P7 进度**：P7a ✅（替换/拦/放行，§35）· P7b ✅（纵深防御，本节）· **P7c 待做**
（新增 `SuSubsystemFluidBlockBehavior`：水流/岩浆不得冲毁领地内方块 —— 这项**可以**实机验证：
把水/岩浆引到领地边界即可）· **P7d 待做**（活塞/发射器推入拒绝）→ §7 总表收口。

## 37. 第 70 轮：P7c 流体（水/岩浆）领地守卫 —— 实现 + 部署装载验证 + 实测现场勘察

**本轮产出（改动已暂存，未提交、未推送）**

| 文件 | 改动 |
|---|---|
| `Func/Subsystem/SuFluidClaimGuard.cs` | 新增：共用的"改回式"守卫（快照 → 跑引擎 → 把"新流进领地"的格改回旧值 + 统一否决通知） |
| `Func/Subsystem/SuSubsystemWaterBlockBehavior.cs` | 新增：替换 `Game.SubsystemWaterBlockBehavior`（Class GUID `4ecb005a-64f7-4036-b470-cc163fab65c2`） |
| `Func/Subsystem/SuSubsystemMagmaBlockBehavior.cs` | 新增：替换 `Game.SubsystemMagmaBlockBehavior`（Class GUID `828db45d-1460-434d-8879-ccd0d3810518`） |
| `Modules/Session/ScMultiplayerLifecycle.cs` | 两处 `Parameter.Class` 替换（紧接火焰那段） |
| `Obfuscar.xml` | 两个 `<SkipType>`（类名要出现在 Database 字符串里，必须保留） |

挂钩方式照抄本仓**已验证**的 `SuSubsystemFireBlockBehavior`：整型替换 + **显式接口实现** `void IUpdateable.Update(float dt)`
（引擎的 `SubsystemWaterBlockBehavior.Update` 不是虚方法，普通 `override` 挂不上）。

**源码级修正：设计稿 §34.2 写的"替换 `SuSubsystemFluidBlockBehavior`"做不到**
- `SubsystemFluidBlockBehavior` 是 **abstract**（源码第 7 行），Database 里没有它的 `Class` 参数条目；
  实际注册的是两个**具体**子类（`Pak/Database.xml:3470` 岩浆、`:3571` 水）。
- ⇒ 改为各替换一个具体类型；守卫逻辑抽到共用的 `SuFluidClaimGuard`（私有 `m_toUpdate` 定义在基类
  `SubsystemFluidBlockBehavior.cs:19`，两个子类各取一次句柄，取值同火焰那段用 `ModParentField.GetParentField`）。

**为什么只能"改回式"（三条源码依据，勿凭记忆改）**
1. 流体写入先汇入私有 `m_toSet`，在 `SubsystemFluidBlockBehavior.cs:293-310` 经 `ChangeCell` / `DestroyCell` 落地；
2. 同文件 `:312` **自己**调用 `SubsystemTerrain.ProcessModifiedCells()` 把 `m_modifiedCells` 冲刷清空
   ⇒「在地形子系统里过滤格集」对流体**无效**（`SuSubsystemTerrain` 看到时已经空了）；
3. `SubsystemTerrain.ChangeCell`（引擎 229 行）与 `ProcessModifiedCells`（64 行）都**没有 `virtual`** ⇒ 无法覆写拦截。

⇒ 唯一可用窗口是 `base.Update(dt)` 前后。快照来源用引擎自己的待处理工作表 **`m_toUpdate`**（`:19`）：
对每个待处理格快照「自身 + 4 侧邻 + 下方格」—— 正是 `SpreadFluid()` 里 `Set(...)` / `FlowTo(...)` /
`OnFluidInteract(...)` 的**全部写入落点**（`OnFluidInteract` 是 `protected virtual`，但水的两个覆写
——水↔岩浆转换 ——都发生在 `SpreadFluid()` 内，同样被这个窗口覆盖）。

**口径与"不冻结"（对应 §34.3 的三面验收）**
- 走统一入口 `CanRegionModifyCell(0, …)`（主机本机玩家 id=0）：**拥有者可以在自己领地里放水**，他人领地才拦。
- 只挡"**新流进来**"：快照里旧值本来就是同类流体的格（只改水位/顶面数据）不回滚、不通知
  ⇒ 领地内**既有水体不被冻结**（§34.3 第 3 面）。反向也放行：领地内的水自然干掉变空气不拦。
- 只在本机是**联机主机**时执法：客户端用 clientId 0 会被当成主机玩家身份，结论必错；客户端地形以主机广播为准。
- 岩浆比水多两个窗口：`OnBlockAdded` / `OnNeighborBlockChanged` 里**私有**的
  `ApplyMagmaNeighborhoodEffect` 会点燃可燃物（那半由 P6 熄）并 `DestroyCell` 掉 61/62 号方块，不经过流体工作表
  ⇒ 这两条路径各自开窗快照（半径 1 / 半径 2）。
- 重入防护：`ChangeCell` 会触发邻居通知 ⇒ 岩浆的 `OnNeighborBlockChanged` 会**嵌套**进同一 guard 并复用列表，
  因此落地前把三个列表复制成数组再遍历（否则外层循环被嵌套调用改写）。

**部署与装载验证（两端同包，本轮实测）**
- 构建 `dotnet build … -c Release`：**0 错误**（3 条既有无关告警），Obfuscar 正常。
- 混淆跳过实测：混淆产物里 `SuSubsystemWaterBlockBehavior` / `SuSubsystemMagmaBlockBehavior` /
  `SuSubsystemFireBlockBehavior` / `SuSubsystemPickables` / `SuComponentMiner` 的裸类型名**全部保留**，
  仅内部引用的 `SuFluidClaimGuard` 被改名（负对照 `SuSubsystemNonexistentZZZ` 不存在）。
  ⚠️ **检查方法本身踩过坑**：.NET 元数据里命名空间与类型名是**两条独立字符串**，带点的全名永远不连续出现，
  用 `ScMultiplayer.SuSubsystem…` 去搜会得到"连火焰也是 False"的假结论（本轮先踩了一次，改用裸类型名才有意义）。
- 包 `[SuAPI]ScMultiplayer-2.2.0.scmod`，SHA256 `7d9d444e3b68a757406f3cbdb36488ba393243e338be20ce8596eeef18c712c1`，本地 / PC / 平板**三处一致**。
- 装载标记（临时诊断日志，验证后移除）：平板 `22:54:25`、`22:54:28`，PC `22:54:49`
  `[ScMP-P7c] SuSubsystemWaterBlockBehavior active, fluid=18 m_toUpdate=ok`
  `[ScMP-P7c] SuSubsystemMagmaBlockBehavior active, fluid=92 m_toUpdate=ok`
  ⇒ ① 整型替换生效；② **私有字段句柄取到了**。这条日志是必需的：若句柄为 `null`，守卫会静默空转，
  实测"水没进来"就是**假通过**。
- 世界态：Autumn（`areSeasonsChanging:true`）、两端在游戏内、领地 22 块（`sequence=70, nextId=30`）。

**实测现场勘察（下一轮直接开测）**
- claim **#28** 边界（读平板存档 `Worlds/World/ScMultiplayerRegions.xml` 实测）：
  `MinX=-165 MaxX=-163 MinZ=77 MaxZ=79`，Y 全高，拥有者 = 客户端 `Basil`
  （主机 `Android User` 非拥有者 ⇒ 正是"他人领地"）。
- Z=77 水岸剖面（主机侧 `world blocks` 扫描）：

  | x | y=61 | y=62 | y=63 | y=64 | y=65 |
  |---|---|---|---|---|---|
  | −176…−171 | 砂/砾 | 水 | **水** | 冰 | 雪 |
  | −170…−166 | 砾 | 砾 | **砾** | 冰 | 雪 |
  | −165…−163（#28 内） | 砾 | 砾 | 砾 | 砾 | 雪 |

  ⇒ 水头停在 `x=-171`，东侧被砾石（y=63）与冰（y=64）**实体封死**：不先移除方块，水到不了 claim 边界。
- 能力矩阵**新增两条实测结论**：
  - **走动可控（仅平板）**：`lookat <目标坐标>` + `hold w` 4 秒 ⇒ 主机从 X `-160.85` 走到 `-170.25`（9.4 格）✓；
    同一手法 PC 客户端 4 秒只动 0.5 格 ✗（与"PC 移动不可靠"一致）。
  - **世界内挖掘无法自动化**：两端 `mouse left down` 都无效 —— PC 非空气 `123→123`、平板 `71→71`，
    按住分别 2.5s / 5s 均无变化，`aim` 准星目标与事件环都不变（事件环里没有世界交互）
    ⇒ `mouse` 注入**打不出引擎的挖掘手势**；`lookat` 的射线也会先撞到脚下那层冰。
- ⇒ 改走**既有**的 GmMod 通道：`GmUiComponent.cs` 已有「◆ 地图方块：破坏 / 放置（准星处，主机执行）」+
  「贴着准星面放置手持方块」（沿命中面法线偏移一格，`:1257`）⇒ 可以**精确放一格方块**，不需要挖掘。

**下一轮的可行试验（不需要任何挖掘，现场已定死）**
1. claim #28 地表在 `y=65`（雪），`y=66` 起是空气 ⇒ 在**领地外** `(-166,66,77)` 放一格水，
   它东邻 `(-165,66,77)` 正好**在领地内且是空气** ⇒ 水流必然尝试进入，正是 P7c 的拦截点；
   同时西/南/北三面在领地外自由流动就是"放行"对照，"领地内既有水体不冻结"另测。
2. 落点手法：`lookat -166 65 77`（瞄雪块**顶面**，实测 `aim` 的顶面 `face=4`）→ GmMod「贴着准星面放置手持方块」
   ⇒ 水落在 `(-166,66,77)`。
3. 前置小改：GmMod「给予」清单目前只有泥土 / 橡木 / 橡树叶 / 火柴 / 炸药，需按既有 `FindBlockByTypeName`
   写法补一项 `WaterBlock`（最小改动，改完同样两端部署）。
4. 客户端能开 GM 面板（Windows ✓）；平板上的**世界内 HUD 仍点不动**（§16），所以"放水"这一步走客户端侧面板，
   主机负责站位（走动可控 ✓）与判读日志。

**P7 进度**：P7a ✅（§35）· P7b ✅（§36，纵深防御）· **P7c 实现+部署+装载验证 ✅ / 实机拦·放行·不冻结 🟡（本节，现场已定）** ·
P7d 活塞/发射器 待做 → §7 总表收口。

## 38. 第 71 轮：P7c 实测推进 —— GmMod 水方块、放置闭环、以及"拦"面为什么还没测出来

**本轮代码改动（已暂存未提交）**
- `Mod/GmMod/GmUiComponent.cs`：给予清单新增「给予 水 ×8（流体测试用）」（`WaterBlock`，BlockIndex=18）。
  水**无法靠挖掘获得**（引擎里水不给掉落物），只能 GM 给予后放置。
- `ScMultiplayer/Func/Subsystem/SuFluidClaimGuard.cs`：口径细化 —— **只挡"从领地外流进领地内"**：
  `CaptureFluidWorkList` 里若工作列表中的流体格**自身已在领地内**就整格跳过；
  `CaptureNeighborhood`（岩浆邻域）同样跳过"岩浆自身已在领地内"的情形。
  理由：`CanRegionModifyCell(0, …)` 按**主机身份**判定，拥有者自己领地里的水必然被判"非拥有者" ⇒
  不跳过就会把领地内既有的水逐帧回滚（与设计稿 §34.3 第 3 面"不冻结"冲突）。
- 同时移除了 §37 的两条临时 `[ScMP-P7c]` 装载日志（本仓规范：验证后必须移除），并在两端复核：
  重启后 `grep ScMP-P7c` 无输出 ✓（新包确实生效）。
- 部署：`[SuAPI]ScMultiplayer-2.2.0.scmod` SHA256 `7063c11636638d23a8b1c50052657dc3561be9e87b2fc1bc96d7e2abdd4a9cbc`、
  `[SuAPI]GmMod-1.1.1.scmod` SHA256 `c61e533caead7316e51b656f594a510803b02c0f98d21327e78a45224c801b9e`，
  本地/PC/平板三处一致；两端重启后进入 `Francenira` 世界，22 块领地 `sequence=70` 同步正常。

**本轮拿到的实测事实**

1. **GM 给予水成功**（新条目生效的实证）：客户端面板「玩家操作 → Basil → 给予 水 ×8」后
   `player` 报 `"holding": { "value": 18, "contents": 18, "count": 8 }` ✓。
2. **GM「贴着准星面放置手持方块」能放流体**：`SubmitBlockEdit` 直接取 `ComponentMiner.ActiveBlockValue`
   提交 `SetCells`，**没有可放置性校验**，所以水/岩浆也能落格（`contents=18`）✓。
   约束：它用的是**点击那一刻**的射线（`Raycast(eye, eye+dir*64)`），
   与事前的 `aim` 探针**不保证一致** —— 本轮实测过 `aim` 报 `kind:none` 却仍落下 `(-184,67,92)`，
   以及探针算出的 `(-167,66,77)` 实际落到 `(-164,66,78)`。
   ⇒ **闭环必须以客户端日志为准**：`[GmMod] Submitted ScMP.Data.Cells cell=X,Y,Z contents=18`（末条）
   才是真实落点，靠它判断是否落在领地外/目标层。
3. **`mouse left` 挖不动**（§37 已记）但 **`mouse right click` 放置**在 P5 实测里可用（§27）。
4. **客户端传送落点 = 主机位置 + OffsetX(2)**：主机站得离领地边界太近时，客户端会被送进领地内部
   （本轮实测落在 `(-164.5,65,77.5)`，正在 #28 内）。要"界外放置"必须先把主机退到边界外 ~5 格。
   另观察到一次异常：执行「传送到主机身边」后**两端一起**到了 `(-183.5,65,92.5)/(-186.4,65,91.7)`
   （主机也离开了原地 ~20 格），原因未定位，先用"主机走动 → 客户端传送"的顺序规避。
5. **走动只有平板可控**：`lookat <目标> + hold w` 实测 5 秒走 14 格（本轮 3 段共走约 40 格）；
   PC 客户端同法 4 秒只动 0.5 格。

**P7c 三面验收：目前的实测边界**

- **"不冻结"/拥有者可放（正向对照）✓**：客户端（= 领地拥有者）把水放到**自己领地内** `(-164,66,78)` 后，
  水在领地内自由扩散成 7 格（`(-165,65,77..79)`、`(-164,65,77..79)`、`(-164,66,78)`），
  **主机侧没有任何否决日志** —— 与"只挡跨界进入 + 领地内源跳过"的设计一致。
- **"拦"（水从界外流进领地）✗ 尚未取得**：本轮第二次试验把水放在**界外** `(-170,66,80)`（闭环确认真实落点），
  但它先落到 y=65，随后沿 y=65 一路向东扩散到 `x=-161`，**穿过整个 #28 且无否决日志**。
  根因不是守卫失效，而是**现场已被上一轮污染**：claim 内那 7 格水成了"领地内源"，
  按本轮细化口径它们整格跳过 ⇒ 领地内既有水继续扩散（并把 y=65 这条通道"接管"了）。
  ⇒ **要测"拦"必须用一块干净的（无既有水的）领地**，且水要在**领地内该层为空气**的高度上从界外接近。
- 已确认可用的下一块干净目标：**#29**（`MinX=-149 MaxX=-147 MinZ=64 MaxZ=66`，Y 全高，拥有者同为客户端 `Basil`，
  本轮未动过）。计划：主机走到 #29 西侧界外 ~5 格 → 客户端传送 → 扫 #29 附近剖面找出"领地内为空气"的层
  → 在该层、界外一格放置水 → 用 `Submitted … cell=` 确认落点 → 判读：水应停在 `x=-150`，
  且主机日志出现 `这块区域属于 Basil（领地 #29），修改已否决（fluid）`（同时写审计 `region.deny.fluid`）。

**本轮踩到的工具坑（务必记）**
- **中文 `.ps1` 必须 UTF-8 BOM**：PS 5.1 无 BOM 时按 ANSI 读，中文变乱码并**破坏语法解析**
  （本轮第一版脚本报 7 处语法错误、实际根因是编码）。写完脚本先补 BOM，再用
  `[System.Management.Automation.Language.Parser]::ParseFile()` **语法自检**，通过再跑。
- **正则与折叠方式要配套**：我用 `-replace '\s+', ' '`（折叠成一个空格）却按"无空格"写了
  `"cell":\{"x":…` 的正则 ⇒ 9 次瞄准全部误判为"无命中"。要么统一 `-replace '\s+',''`（删空）配无空格正则。
- 内联 `if` 表达式在 PS 5.1 非法（`$x = if (...) {...} else {...}`）—— 老坑，再次踩到。

**P7 进度**：P7a ✅（§35）· P7b ✅（§36）· **P7c 🟡**（实现/部署/装载 ✅，口径已细化；
"不冻结 + 拥有者可放" ✅，"拦"待用干净领地 #29 复测，现场与方法已定）· P7d 活塞/发射器 待做。

## 39. 第 72 轮：P7c「拦」实测达成 —— 水体停在 #29 西界 + 主机侧 `已否决（fluid）`

**结论：P7c 的"拦 / 放行 / 不冻结"三面都拿到了实测证据**

| 面 | 证据 | 出处 |
|---|---|---|
| **拦**（动态） | 主机日志 `23:14:43.069 INFO: [ScMP] 这里属于 Basil（领地 #29），修改已否决（fluid）`；`grep -c '已否决（fluid）'` = 1 | 平板 `Logs/Game.log` |
| **拦**（静态） | **未截断**扫描（center=(-152,65,64)，774 格，`truncated=false`）：#29 内 `x -149..-147 / z 64..66` 各层全是 Dirt/Grass/Ice/Snow/PurpleFlower，**0 格水**；而紧邻界外 `x=-155..-151 / y=65` **是 WaterBlock** | `world blocks 5` |
| **放行** | 同一片水在界外自由铺开（`x -155..-151, y=65`），并在 y=66 也有水格 | 同上 |
| **不冻结 / 拥有者可放** | 客户端（= 拥有者）把水放到**自己领地内** `(-164,66,78)` 后，水在领地内自由扩散 7 格，主机侧**无任何否决** | §38 |

**这道门拦下的到底是什么（很有代表性）**：#29 内 y=65 那一层有 `GrassBlock` / `PurpleFlowerBlock`，
它们**不是流体阻挡物** ⇒ 引擎 `OnFluidInteract` 的正常行为是"**销毁该方块并写入流体**"。
也就是说这次拦下的不只是"水漫进来"，而是"**水毁掉领地内的花草**"——
正是设计稿 §34.2 要的"流体**不得冲毁/覆盖领地内方块**"。

**水怎么来（本轮定型的手法，P7d 与回归复用）**
- 水**无法靠挖掘获得**（引擎里水不给掉落物）⇒ 只能用 GmMod「给予 水 ×8」（本轮新增条目）拿在手上。
- **普通右键就能放水**：`Block.IsPlaceable` 默认 `true`，而 `FluidBlock` / `WaterBlock` / `MagmaBlock`
  **都没有覆盖它** ⇒ `ComponentMiner.Place` 放行 ⇒ `mouse right click` 即可（与 §27 的 P5 实测同一路径）。
  这比走 GM 菜单可靠得多：GM「贴着准星面放置」用的是**点击那一刻**的射线，与事前 `aim` 探针经常不一致。
- 每放一次消耗 1 个；客户端本轮还**丢过一次整个背包**（水全没了、`slots: []`，疑似死亡掉落）
  ⇒ 每次试验前先 `player` 确认 `holding.contents == 18`，必要时重新给予。

**判据教训（都已写进操作流程）**
1. **否决必须按动作标签分开数**：`grep '修改已否决' | tail` 会把 P7c 的 `（fluid）` 行藏在
   P7b 的 `（pickup）` 刷屏之后（本轮第一次就是这么误判成"没有 fluid 否决"的）。
   改用 `grep -c '已否决（fluid）'` 比较前后计数。
2. **`world blocks` 有 1024 格上限、且按 x 升序返回**：扫描中心离目标太远时，目标（领地）的格根本进不了列表，
   `#29 内水格=0` 会是**扫描假象**（本轮先后两次被它骗过）。要判定某领地内部，
   **扫描中心必须落在该领地 ±5 格内**，并先看 `truncated` / `totalNonAir`。
3. **GM 菜单放置的落点只能靠日志确认，而且必须判断"是不是新行"**：`Submitted ScMP.Data.Cells cell=…`
   要比较调用前后的行数，否则读到的是上一轮的旧行（本轮两次把旧行当成本次落点）。
4. **平板走动不稳**：同一段 `lookat + hold w` 有时 5 秒走 14 格、有时原地不动，还会被水流推走
   （本轮一次从 `(-152,63)` 漂到 `(-163.5,79.5)`，横跨 26 格）⇒ 每次放置前重新读位置，
   不要拿上一次的位置做几何推导。
5. **P7b 的副作用（预期内，不是 bug）**：客户端死亡掉落的物品落在 #29 内，会被主机本机拾取守卫
   每秒挪走一次（`（pickup）` 刷屏）。这正是 P7b 的设计行为——主机不得在他人领地拾取。

**仍未补上的一项（留待回归时顺手做）**：还没拿到"**同一次放水**里同时看到界外有水 + 界内无水 +
fluid 否决"的三合一时间线快照（现有证据是同一现场的三次观测拼起来的）。不影响结论，但复测时值得补齐。

**P7 进度**：P7a ✅（§35）· P7b ✅（§36）· **P7c ✅（拦 / 放行 / 不冻结三面均有实测证据，本节）** ·
P7d 活塞/发射器 待做 → §7 总表收口。

## 40. 第 73 轮：P7d 代码落地（活塞"改回式" + 发射器"行为层短路"），实测待做

**勘察结论（源码级，直接决定实现方式）**

| 执法对象 | 落地写入路径 | 虚钩子 | 采用方式 |
|---|---|---|---|
| **活塞** | `SubsystemPistonBlockBehavior` 内部 `ChangeCell` / `DestroyCell`（366/374/419 推进、458/467 由 `StopPiston` 提交） | ✗ 队列 `m_actions` 与 `StopPiston` 都是私有，`ChangeCell` 非虚 | **改回式**（与 P7c 同范式） |
| **发射器** | `DispenserElectricElement.Simulate`（**虚**）→ `ComponentDispenser.Dispense()`（非虚）→ `DispenseItem`（私有）→ `AddPickable` / `FireProjectile`（**都非虚**） | ✅ 只有元件 `Simulate` 是虚的 | **行为层短路**（在元件里挡） |

**关键发现（省掉一整套替换）**：mod 早已用 `modInjector.RegisterBlock(...)` 把 `Game.DispenserBlock` /
`Game.PistonBlock` 换成 `ScMultiplayer.DispenserBlock` / `ScMultiplayer.PistonBlock`
（`Func/Circuit/SuCircuitBlocks.cs:288` / `:336`，注册在 `ScMultiplayerLifecycle.cs:68-71`），
而 `ScMultiplayer.DispenserBlock` 重实现了 `IElectricElementBlock.CreateElectricElement` 返回
`SuDispenserElectricElement` ✓ ⇒ 发射器半边**不需要新增替换**，只要在既有
`SuDispenserElectricElement.Simulate()` 里加一道判定即可。
（顺带否掉一条路：方块**不能**走 Database `Class` 参数替换 —— `Pak/*.xml` 里根本没有
`Game.DispenserBlock` 这样的方块 Class 条目，方块是代码反射注册的；本仓此前的先例是
`RegisterBlock` 注入。）

**本轮实现**

1. **活塞**（`Func/Subsystem/SuSubsystemEditableCircuitBlockBehaviors.cs`，`SuSubsystemPistonBlockBehavior`）：
   `IUpdateable.Update` 增加**主机分支** —— 用 `ModParentField` 读私有 `m_actions`，对每个"**活塞自身不在任何领地内**"
   的动作，按 `PistonBlock.GetFace(Terrain.ExtractData(value))` 取朝向（面序号→方向换算与 `GmUiComponent`
   放置时用的 switch 一致：0=z+/1=x+/2=z-/3=x-/4=y+/5=y-），快照其**前方 1..8 格**中落在领地内的格；
   `base.Update(dt)` 之后把这些格的**变化改回旧值**，并 `NotifyRegionModificationDenied(0, cell, claim, "piston", null)`。
2. **发射器**（`Func/Circuit/SuCircuitBlocks.cs`，`SuDispenserElectricElement.Simulate`）：
   在既有的"非权威端 `return false`"之后加 `CanDispenseIntoRegion()` —— 发射器自身不在领地内、
   且**落点格**（`position + CellFace.FaceToVector3(DispenserBlock.GetDirection(data))`，
   与 `ComponentDispenser.DispenseItem` 的 `0.6f * vector` 偏移落在同一格）在他人领地内
   ⇒ 不发射（`return false`）+ 通知（动作标签 `dispense`）。

**口径（两处一致，且与 P7c 对齐）**：只挡"**从领地外推入/放入领地内**"。
实施者本身（活塞 / 发射器）已在某块领地内 ⇒ **整台跳过** —— 否则拥有者自己领地里的机械会被判越权
而彻底失效（`CanRegionModifyCell(0, …)` 按**主机身份**判定，必然不是拥有者）。
这条口径是本轮写代码时主动修正的：活塞守卫第一版只看"前方落点是否在领地内"，会误伤拥有者自己的活塞。

**构建**：`dotnet build Mod\ScMultiplayer\ScMultiplayer.csproj -c Release` —— **0 错误** ✓
（两端部署与实机实测留到下一轮）。

**下一轮实测场景（P7d 需要电力，方案已定）**
- 现有 GmMod 给予清单里没有电路方块 ⇒ 需按既有 `FindBlockByTypeName` 写法补：
  `PistonBlock`(237) / `DispenserBlock` / `LeverBlock`(或 `SwitchBlock`) / 导线类方块。
- **活塞场景**：无主区搭"拉杆 → 导线 → 活塞"，活塞口朝 3 格外的可推方块，方块另一侧紧贴某块
  **客户端领地**的边界；通电 ⇒ 活塞把方块推进领地应被拒（主机日志 `已否决（piston）`、方块位置不变）。
- **发射器场景**：无主区放发射器、口朝领地内、装填任意物品；通电 ⇒ 领地内不应出现掉落物
  （`已否决（dispense）`）。
- 判据沿用 §39 的教训：**按动作标签分开计数**、扫描中心必须贴近目标领地（否则 1024 格上限会制造假象）、
  每次操作前重读双方位置（平板走动不稳）。

**P7 进度**：P7a ✅（§35）· P7b ✅（§36）· P7c ✅（§37-§39）· **P7d 🟡（代码落地 + 构建通过，实测待做，本节）**
→ 之后是 §7 总表收口。

## 41. 第 74 轮：按用户口径修正 P7a —— 领地内"**不产生挖掘进度**"，改回只作最后保证

**用户口径（2026-09-25）**：*"在区域内挖掘应该是没有挖掘进度才对，挖掘后修复是最后的保证。"*

**原先的做法（问题）**：`SuComponentMiner` 在 `base.Update(dt)` **之后**才判定，然后
`Poke(forceRestart: true)` —— 即"先让它挖、再打断"，属于**事后修复**。
对很软的方块（挖掘耗时短）配上长帧，某一帧内进度就可能跑满而被挖掉：引擎是在 `Dig()` 里
进度到 1 就直接 `DestroyCell`（`ComponentMiner.cs:156-161`），改回式只是事后补救。

**引擎事实（本轮读源码确认）**
- 进度 = `(m_subsystemTime.GameTime - m_digStartTime) / 挖掘耗时`（`ComponentMiner.cs:124-125`），`Dig()` 每帧重算；
- `m_digStartTime` / `DigCellFace` 只在**目标格变化**时才重设（`:119-122`）；
- `DigProgress` 在 `DigCellFace` 为空时**直接返回 0**（`:82-90`）⇒ 清掉它，界面上也就没有进度。

**改后的做法（零进度 + 兜底）**
`void IUpdateable.Update(dt)` 是更新循环的**唯一入口**，而玩家输入与 `Dig()` 都在 `base.Update(dt)` 内部
⇒ 把判定挪到 **base 之前**：目标格被否决就
① 把 `<DigCellFace>k__BackingField` 置空、② 把 `m_digProgress` 归零
⇒ 引擎下一次 `Dig()` 只能"从这一刻重新开始"（开始时间 = 当前时间，进度恒为 0），
**领地里根本不会出现挖掘进度，方块也永远不会被挖掉**。
`base.Update(dt)` 之后只做一件事：如果该格**真的**在本帧被改掉了，`ChangeCell` 改回"本帧原值"并 `Poke`
—— 这就是"最后的保证"，正常情况下这条分支不会走到。

**为什么不能靠隐藏 `Dig()`**：`ComponentMiner.Dig(TerrainRaycastResult)` **不是虚方法**，
调用点又在 `ComponentMiner.Update` 内部（`this.` 调用、编译期类型 `ComponentMiner`）⇒
子类 `new` 一个同名方法挂不上 ✗。能稳定拿到"每帧、且在输入之前"的时机只有本类自己的
`IUpdateable.Update`（显式接口实现，更新循环按接口派发 ✓ —— 与 P7c 的流体/岩浆、P7d 的机械同一个手法）。

**本轮同时完成**：P7d 代码落地 + 两端部署（ScMultiplayer `1b50a8616a7121c7f7d970fca6c6a758678e739667bf82719286a8245e31e18f`，
本地/PC/平板一致）；GmMod 新增「给予 活塞 / 发射器 / 开关」（P7d 电力场景用）。

**待实测项（下一轮）**
1. **P7a 新口径**：主站在他人领地内按住挖掘 ⇒ ① 界面上**没有挖掘进度**、② 方块**不被破坏**、
   ③ 收到一次 `已否决（dig）`；领地外照常能挖（放行）。
2. **P7d 活塞**：无主区"开关紧贴活塞"（SC 电力按面相邻连接，不必拉导线）⇒ 活塞把前方方块推进领地应被拒
   （`已否决（piston）`、方块原地不动、活塞不伸）。
3. **P7d 发射器**：口朝领地内 + 通电 ⇒ 不产出（`已否决（dispense）`）。
   注：发射器需要装填物品，而"往发射器里塞物品"要拖拽 UI（§36 记过拖拽不生效）⇒ 这条可能要改用
   "空发射器也会走守卫并给出 `已否决（dispense）`"来取证，并在文档里如实标注取证强度。

**P7 进度**：P7a（本轮按新口径重写并重新部署，**待实机复核**）· P7b ✅ · P7c ✅ · P7d 🟡（代码+部署 ✅，实测待做）。

## 42. 第 75–77 轮：P7a「零挖掘进度」实机证据 + 一个通知节流缺陷

**结论：领地内挖掘"挖不动 + 零进度 + 兜底从未触发"已取得实机证据。**

**实验方法（本轮定型，后续复用）**
- **平板怎么挖**：`CmdBridgeMod/Server/CommandRouter.cs` 里记录了引擎侧实测语义 ——
  *"无按钮区按住不动 ≈ 0.2~0.5s = 挖掘；按住拖动 = 视角；左下 Move 区按住拖动 = 移动"*。
  命令为 `ui.session.begin` → `ui.press x=… y=…` → 保持 → `ui.release` → `ui.session.end`
  （用 `sccmd raw <命令> k=v` 下发），平板 1200×2000 ⇒ 按住屏幕中心 (600,1000) 即挖掘。
  （`mouse left down` 在 Android 无效，此前 P7a 一直没法实机复核，就是卡在这里。）
- **主机的活命**：雪原低温会让主机反复死亡，且死后**重生在水里**继续溺死。用存档补丁一次性解决
  （停游戏 → 拉 `Worlds/World/Project.xml` → 改 → 推回 → 重启）：
  `TimeOfYear=0.29`（初夏）、`TemperatureOffset=10`、
  `MalePlayer` 实体的 `Health=1 / Air=1 / Temperature=12 / Food=0.9 / FluDuration=0 /
  SicknessDuration=0 / Wetness=0`、**并把 `Body/Position` 与 `Players/1/SpawnPosition` 一起改到干地**。
  实测：补丁后主机 `(−152.5,65,62)`、`血=1.00 / 体温=11.69 / 饱食=0.90` ✓。

**实测证据（#29，x −149..−147 / z 64..66，拥有者客户端 Basil）**
1. 主机站在 #29 内、朝正下方长按 8 秒（同一手法）：
   - 临时诊断按格统计：**`deny dig -149,64,65` = 482 次**，而 (−149,64,65) **正在 #29 内** ✓
     （482 ≈ 60 帧/秒 × 8 秒 ⇒ 整个长按期间**逐帧**都在否决）；
   - `fallback restore` 计数 = **0** ⇒ 该格**从未真的被挖掉**，兜底分支一次都没用上；
   - 同一格的值在长按前后完全不变 ✓。
2. 另一次（第 76 轮）在 #28 内也得到 118 次 `deny dig -165,64,77`（同样在领地内 ✓）。
⇒ 「**领地里不产生挖掘进度、方块挖不掉**」这半边成立，且"改回"确实只是最后保证（从未触发）。

**仍然缺的一半：边界外的对照**（证明"8 秒足够挖掉同类方块"，排除"只是挖得慢"）。
两次尝试都被现场破坏：我们自己放的水把 #28 周边与 #29 的**表层**都变成了 `WaterBlock`，
而水**不可挖**（准星只能选中水，`DigCellFace` 根本不会指向实体方块）⇒ 对照目标无效 ✗。
**下一步的干净做法（已想好，最省事）**：同一格做 A/B —— 让主机**离开联机会话**（`SuComponentMiner`
的守卫以 `ScMultiplayer.client.IsConnected == true` 为前提，单机时守卫关闭），在原格原手法再按 8 秒，
应当能挖掉；这就把"守卫开/关"作为唯一变量，比找干地可靠得多。

**顺带查出的一个真实缺陷（待修）**：否决通知的节流键**只有 `clientId`、不含动作**
（`ScMultiplayerRegionEnforcement.cs:m_regionDenyNoticeTimes[clientId]`）⇒
P7b 的拾取守卫每秒刷一条 `（pickup）` 时，会把同一秒内的 `（dig）` 通知**挤掉**
（本轮 grep '领地 #29' 只能看到 pickup 行 ✗）。建议键改成 `clientId + action`。

**临时诊断的状态**：`SuComponentMiner.cs` 里的两行 `[ScMP-P7A-DIAG]`（deny 明细 + fallback 标记）
仍在**未暂存区**，收集完证据后删除；已暂存的零进度实现里不含任何 DIAG ✓。
（另一教训：`progressBefore` 那种"清零后再读 `DigProgress`"的写法是**自证式**的，不能当证据 ✗——
有效证据是"逐帧否决次数 + fallback 计数 + 方块是否变化"。）

**P7 进度**：P7a 🟡（零进度+挖不掉已有实机证据，边界外对照待用"单机 A/B"补齐）· P7b ✅ · P7c ✅ ·
P7d 🟡（代码+部署 ✅，实机待做；且 PC 客户端需先救活才能用它的 GM 面板搭电力场景）。

## 43. 第 79 轮：P7a「零挖掘进度」A/B 判据达成（进度值直接对照）+ 通知节流修复

**本轮拿到的判据（同一玩家、同一手法、同一时段，唯一变量 = 守卫）**

```
23:55:45 … 23:55:55  [ScMP-P7A-DIAG] deny dig -148,65,64 progressBefore=0.0000 digTime=0.000   ×11 条（1 Hz）
23:55:55.395         [ScMP-P7A-DIAG] allow dig -149,65,63 progress=0.000 digTime=0.000
23:55:56.397         [ScMP-P7A-DIAG] allow dig -150,64,63 progress=0.496 digTime=0.745
23:55:57.411         [ScMP-P7A-DIAG] allow dig -151,64,62 progress=0.523 digTime=0.784
fallback restore 行数 = 0
```

- `(−148,65,64)` **在 #29 内**（x −148∈[−149,−147]、z 64∈[64,66]）⇒ 连续 11 秒挖掘，**进度恒为 0.0000**，
  方块从未被挖掉，兜底分支一次都没触发；
- 紧接其后的 `(−150,64,63)`、`(−151,64,62)` **都不在任何领地内** ⇒ 同样徒手挖掘，
  **进度不到 1 秒就爬到 0.496**，随即方块被挖掉（准星推进到下一格 ⇒ 前格已空）。
⇒ 「**领地内没有挖掘进度**」不再是推断，而是进度值的直接对照；「挖完再改回」确实只是最后保证（行数 0）。

**顺手修掉的真实缺陷（本轮已部署验证）**：否决通知的节流键由 `clientId` 改为 **`clientId + 动作`**
（`ScMultiplayerRegionEnforcement.cs`：`RegionDenyNoticeKey`）。修复前 P7b 的拾取守卫每秒一条 `（pickup）`
会把同秒的 `（dig）` 通知整条挤掉；修复后日志同时出现：

```
23:55:55.283 INFO: [ScMP] 这块区域属于 Basil（领地 #29），修改已否决（dig）
23:55:56.163 INFO: [ScMP] 这块区域属于 Basil（领地 #29），修改已否决（pickup）
```

**方法沉淀（后续所有"主机本地动作"测试都照此）**
1. **平板怎么按**：`ui.session.begin → ui.press x=600 y=1000 → 保持 → ui.release → ui.session.end`（`raw` 下发）。
   注意**瞄准要带水平偏移**（`lookat` 目标与自身 x/z 至少差 ~0.5）：几乎垂直向下的瞄准取不到目标
   （本轮对照第一次失败就是这个原因，`allow dig` 诊断 0 行）。
2. **判据要看进度值本身**：`allow dig … progress=` 应爬升并把方块挖掉；`deny dig … progressBefore=` 应恒 0。
   （注意 deny 侧读到的 `digTime=0.000` 是"清零后必然读到 0"的自证式读数，**不能**单用它当证据；
   真正的证据是"允不允许"两侧的对照 + `fallback` 计数 + 方块是否变化。）
3. **主机的活命**：雪原低温/溺水会让主机反复死亡，用存档补丁（满血/体温 12/饱食 0.9/初夏 +
   `TemperatureOffset=10` + **把 `Body/Position` 与 `Players/1/SpawnPosition` 一起搬到干地**）一次解决。
4. **adb shell 的 grep 模式**：带空格+连字符的模式（如 `'P7A-DIAG deny'`）在本机 adb 下会返回 0 条，
   但同样的 `'deny dig'` 却能数出 845 条 ⇒ 一律用简单正则（`grep -a -E 'deny dig|allow dig'`）。

**临时诊断的处理**：两条 `[ScMP-P7A-DIAG]` 日志（deny 明细 + allow 进度 + fallback 标记）在拿到上述证据后
**立即从工作区移除**（已暂存的零进度实现本身不含任何 DIAG ✓）。

**P7 进度**：P7a ✅（零进度 + 挖不掉 + 边界外对照，§41-§43）· P7b ✅ · P7c ✅ · P7d 🟡（代码+部署 ✅，实机待做）。

## 44. 第 81 轮：（**已作废**，见 §44.1）所谓"Windows 客户端进不去"的排查 —— 该现象系用户强制停止游戏端所致，不是缺陷

**表象**：客户端停在 `SuPlayScreen` + `BusyDialog`（Joining Room），约 5 分钟后
`ERROR: [ScMP] Join request to 192.168.31.212:51459 timed out`；此后进程还会**卡死**
（日志不再增长、CmdBridge 端口 `积极拒绝`），只能重启客户端。

**逐步排除（都有日志实证）**

| 环节 | 成功时（23:43、23:57） | 失败时（00:03 那次） |
|---|---|---|
| 协议/指纹 | 两端 `build 8e327ed6db3a…`、`hash 050703c78000…` 一致 | **同样一致** ⇒ 不是版本问题 |
| 网络 | — | 主机 `tcp6 [::]:51459 LISTEN` ✓，且有来自 `192.168.31.x:57807` 的 **ESTABLISHED** ⇒ TCP 通 |
| 主机 mod 服务端 | `Client joining: N` → `World transfer initially queued: ClientID=N, Transfer=N, Chunks=949` → `Client entered Loading Project` | **三段一条都没有** ⇒ join 请求没被主机 mod 处理 |
| 客户端 | `World download complete: Transfer=1, Transport=TCP, Bytes=882917, Seconds=4.53` → `Client project ready` → `circuit bootstrap complete` → `catch-up complete` | 只有 `Join request … timed out` |

**结论**：不是协议、不是版本、不是 TCP，而是**主机的 mod 服务端在某个状态下不再处理 join**
（连"世界传输入队"都没发生）。已知诱因：**主机在客户端尝试加入的时间窗内被重启** ——
本轮为做测试频繁重启主机，客户端的每次尝试都撞在这个窗口上，留下失败弹窗并最终把客户端卡死。

**操作纪律（今后照此执行）**
1. **绝不在客户端加入过程中重启主机**；顺序固定为：
   ① 干净重启主机 → ② 等它**真的进入世界**（日志出现 `GameCreated` 与 `Loaded N region claims`）→
   ③ 再重启客户端并**立刻**加入（`23:57` 那次成功正是此顺序）；④ 客户端一旦出现失败弹窗/卡死，直接重启客户端。
2. 排查这类问题先看**主机日志有没有 `World transfer initially queued`**：有 = 主机已受理、问题在传输；
   没有 = 主机压根没受理（先怀疑主机状态/重启窗口，而不是网络与版本）。

## 45. 第 85 轮：存档补丁的教训 —— 必须"整段替换 + 推回前校验 XML + 留回滚基准"

**现象**：主机重启后卡在 `SuPlayScreen`，`player` 报 `{"loaded": false}`，日志：
```
ERROR: Error getting data from project file "data:/Worlds/World/Project.xml".
  The 'Entity' start tag on line 714 position 6 does not match the end tag of 'Values'. Line 759, position 9.
```
⇒ 世界加载失败、主机根本进不去（连带 P7a/P7b/P7c 的测试现场一起失效 ✗）。

**根因（自己造成的）**：为给主机背包塞物品而做的 `<Values Name="Slots">` 段替换，
**只换掉了开标签那一行**。而上一次补丁已经把 Slots 写成**多行**形式
（`<Values Name="Slots"> / Slot0 / Slot1 / </Values>`），我的正则却把
"自闭合 `<Values Name="Slots" />`" 与 "多行开标签" 当成同一类处理 ⇒
残留了原子节点与闭合标签 ⇒ XML 结构损坏。

**正确做法（已固化为脚本 `%TEMP%\scfetch\p7d-fixsave.ps1`）**
1. **整段替换**：自闭合形式就替换那一行；多行形式要按 `<Values>` / `</Values>` **括号配平**
   一直吃到配平的那个 `</Values>`，不能只动开标签；
2. **推回前先用 `[xml]` 解析校验**，不合法就**绝不推送**（宁可这一轮不做，也不能把存档推坏）；
3. **保留"补丁前刚拉取的完好存档"作为回滚基准**——本轮就是用它把世界恢复回来的；
4. 顺序永远是：停游戏 → 拉取 → 改（本地副本）→ **校验** → 推回 → 重启 → 复核 `worldLoaded`。

**教训**：**改存档比改代码危险**——代码改错只影响一个功能，存档改错会让主机进不去世界。
今后所有存档补丁一律走"整段替换 + 解析校验 + 回滚基准"三步，并且**先校验后推送**。

## 46. 领地对越权行为的统一语义：**边界就是一堵墙**（用户 2026-09-25 定）

**用户口径**：*"对于活塞来说，领地区域内不可以被修改的方块，那么把领地的边框设计为一堵墙，活塞不能伸入。"*

**这条把 P7a/P7c/P7d 的口径统一成一句话**：
> **领地的边界对越权行为而言是一堵不可穿透的墙 —— 行为在边界处就被挡住，
> 而不是"先让它发生、再改回来"。** "改回式"只保留给竞态与绕过路径（爆炸/火等）兜底。

落到各执法点：

| 执法点 | "墙"的实现方式 | 与"改回"的关系 |
|---|---|---|
| **P7a 挖掘** | `SuComponentMiner` 在引擎累加进度**之前**清掉被否决的挖掘状态 ⇒ **零进度**，方块永远不会被挖掉 | `RestoreDeniedDigCell` 只在"某帧进度仍跑满"时兜底（实测从未触发 ✓） |
| **P7c 流体** | `SuFluidClaimGuard` 在 `base.Update(dt)` 之后把"跨界新流入"的格改回旧值 ⇒ **水停在边界外**（§39 实测：界内 0 格 + `已否决（fluid）`） | 该守卫本身就是边界闸门（流体的写入点不可拦），故"改回"是主路径 |
| **P7d 活塞** | `SuPistonElectricElement.CanPistonExtend` 在 `AdjustPiston` **之前**判定 ⇒ **活塞根本不伸、被推方块也不动**（不是伸进去再推回）✓ | `SuSubsystemPistonBlockBehavior` 的改回式守卫保留为兜底 |
| **P7d 发射器** | `SuDispenserElectricElement.CanDispenseIntoRegion` 在 `base.Simulate` **之前**判定 ⇒ **不发射、不放入** | 引擎发射链路（`Dispense`/`AddPickable`/`FireProjectile`）全非虚，元件层短路是唯一"墙"位 |

**实现现状**：P7a/P7c 的"墙"已有实机证据（§39 / §43）；P7d 的代码即按此语义写（先判后动、不靠改回）✓，
实机验证正在做（测试台限制见 §47 待补）。**本次口径明确后，P7d 的验收判据就是**：
被拒时活塞 **`extended` 位保持 0 且不出现 `PistonHeadBlock`**、被推方块原地不动 ⇒ 等价于"撞在墙上"。

### 46.1 补充口径（用户 2026-09-25 再明确）

> **"领地外的任何方块都认为是无主的，也就是任何人操作领地外的按钮或控制活塞，都不能伸入领地。"**

⇒ 规则写死为：**只要"实施者"本身（活塞 / 发射器 / 按钮所在的机械）在领地之外，
无论由谁触发（主机、客户端、信号），它的动作都不得伸入 / 放入任何领地**。
判定与本机身份、触发者身份**无关**，只看两件事：

| 条件 | 结论 |
|---|---|
| 实施者在**领地外** + 路径/落点**进入某块领地** | **拒**（撞墙：不伸 / 不发射，`已否决（piston/dispense）`） |
| 实施者在**领地外** + 路径/落点**都在领地外** | 放行（正常伸出 / 发射） |
| 实施者**已在某块领地内** | 放行（= 领地拥有者自己的机械，其内部活动不受影响） |

这正是本实现的实际判定（§46 表里 P7d 两行的条件），**代码无需改动**；此处只是把口径写成明确规则。

**P7d 实机验证的操作方式同时调整（用户指定）**：测试场景**改用 Windows 客户端放置方块**
（PC 的 `mouse right click` 放置 + `aim` 一直可靠；平板贴脸短按的精度不足以稳定搭建场景 ✗）。
主机端守卫代码、判定逻辑、部署均不变。

### 44.1 更正（用户 2026-09-25）：本节"进不去 / 掉线 / 卡死"全部**否定**

**用户说明**：本节观察到的那些现象 —— 客户端停在 `SuPlayScreen` + `BusyDialog`、`Join request … timed out`、
`[Server] Client … disconnected`，以及"日志不再增长 / CmdBridge 端口积极拒绝" ——
**都是用户强制停止游戏端造成的**，不是客户端、网络、协议或 Mod 的任何缺陷。

因此以下描述与结论**全部作废** ✗：

- ✗ "Windows 客户端进不去 / 加入超时 / 加入后被掉线 / 进程卡死"；
- ✗ 由此推出的"主机在某个状态下不再受理 join"；
- ✗ "客户端较脆、大量输入注入后会卡死"之类的说法；
- ✗ 把它们归因于平板代理丢包 / 客户端状态 / 传输超时 的任何推断。

**仍然成立的部分** ✓（这些来自**成功加入**时的日志序列，与上述错误归因无关）：

- 正常加入的世界传输链路是：
  `Client joining: N` → `World transfer initially queued: ClientID=N, Chunks=…` →
  `Client entered Loading Project` →（客户端）`World download complete: … Bytes=… Seconds=…` →
  `Client project ready` / `circuit bootstrap complete` / `catch-up complete`；
- 排查时**先看主机日志有没有 `World transfer initially queued`** 这条中性做法仍然有用 ✓
  （只用于判断"是否已进入传输阶段"，不再与任何"故障"绑定）；
- "先让主机进世界、再让客户端加入"作为**操作顺序**保留 ✓（只是不再与任何"故障"绑定）。

**教训**：把"进程被外部停止"当成"程序故障"会得出完全错误的结论，并污染后续判断（本节就是一例）。
今后遇到"日志突然停止增长 / 端口拒绝连接"，**先确认游戏端是否被人为停止或重启过**，再谈缺陷。
