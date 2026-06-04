# 电路系统术语表 (Circuit Terminology Glossary)

> 本术语表为 CircuitAutoRouter Mod 项目专用，定义游戏中电路系统的核心概念。
> 所有术语均基于源码验证，确保后续任务描述的精确性。

---

## 1. 方格与坐标 (Cell & Coordinate)

| 术语 | 英文 | 定义 |
|------|------|------|
| **方格** | Cell | 游戏世界中的最小3D单位，每个方格占据 1×1×1 的空间，由整数坐标 (x, y, z) 唯一标识 |
| **方格值** | CellValue | 一个 int 值，编码了方格的内容（BlockIndex）和数据（Data），通过 `Terrain.ExtractContents()` / `Terrain.ExtractData()` 解码 |
| **方格面** | CellFace | 方格的6个表面之一，用 int 0-5 编码。同时也是一个结构体 `CellFace(x, y, z, face)`，表示"位于 (x,y,z) 方格的 face 面" |

### 面编号对照表 (Face Index)

| Face 值 | 方向 | 向量 | 对面 (OppositeFace) | 含义 |
|---------|------|------|---------------------|------|
| 0 | +Z | (0, 0, 1) | 2 | 前面 (Front) |
| 1 | +X | (1, 0, 0) | 3 | 右面 (Right) |
| 2 | -Z | (0, 0, -1) | 0 | 后面 (Back) |
| 3 | -X | (-1, 0, 0) | 1 | 左面 (Left) |
| 4 | +Y | (0, 1, 0) | 5 | 上面 (Top) |
| 5 | -Y | (0, -1, 0) | 4 | 下面 (Bottom) |

**关键规则**：`OppositeFace` 映射为 `[2, 3, 0, 1, 5, 4]`，即 0↔2、1↔3、4↔5 互为对面。

---

## 2. 方块类型 (Block Types)

| 术语 | 英文/类名 | 定义 |
|------|-----------|------|
| **导线** | WireBlock (Index=133) | 贴附在方格面上的导线，不占据方格内部空间。一个方格可同时拥有多个面的导线，通过 6-bit 位掩码 (`wireFacesBitmask & 0x3F`) 编码哪些面有导线。支持涂色（颜色掩码在 bit6-bit10） |
| **穿线块** | WireThroughBlock | 占据整个方格的导线块，能连通平行于某个轴的两个对面。继承自 `CubeBlock`，实现 `IElectricWireElementBlock`。有5种材质变体：Stone、Cobblestone、Bricks、Planks、Semiconductor |
| **电路元件块** | IElectricElementBlock | 所有参与电路系统的方块必须实现的接口，定义 `CreateElectricElement`、`GetConnectorType`、`GetConnectionMask` 三个方法 |
| **导线元件块** | IElectricWireElementBlock | 继承 `IElectricElementBlock`，额外定义 `GetConnectedWireFacesMask` 方法。WireBlock 和 WireThroughBlock 都实现此接口 |

### 导线 (WireBlock) 详解

- **放置方式**：贴附在方格面上，一个方格最多6面各一条导线
- **数据编码**：`data & 0x3F` = wireFacesBitmask（bit0=face0, bit1=face1, ... bit5=face5）
- **连接逻辑**：`GetConnectedWireFacesMask(value, face)` — 对于 face 上的导线，它连接到：
  - 自身 face（始终）
  - 自身所有其他面（除对面外）上的导线（"T型连接"）
  - 如果存在 T 型连接，则也连接到对面的导线
- **碰撞箱**：每面导线有独立的碰撞箱，位于方格边缘，厚度约 0.05
- **挖掘**：点击某面导线只移除该面导线（清除对应 bit）

### 穿线块 (WireThroughBlock) 详解

- **放置方式**：占据整个方格，通过 `wiredFace` 数据确定连通轴
- **3种放置状态**（由 `data & 3` 编码）：

| data & 3 | wiredFace | 连通轴 | 连通的两个面 |
|----------|-----------|--------|-------------|
| 0 | 0 | Z轴 | face0(+Z) ↔ face2(-Z) |
| 1 | 1 | X轴 | face1(+X) ↔ face3(-X) |
| 2 | 4 | Y轴 | face4(+Y) ↔ face5(-Y) |

- **放置逻辑**：根据玩家视线方向，选择最平行的轴（`GetPlacementValue` 中遍历6个面取点积最大值）
- **连接逻辑**：`GetConnectorType` 只在 wiredFace 和其对面返回 `InputOutput`，其他4面返回 null（不导电）
- **纹理**：wiredFace 和其对面显示导线纹理（`m_wiredTextureSlot`），其余4面显示普通纹理（`m_unwiredTextureSlot`）

---

## 3. 电路系统 (Electricity System)

| 术语 | 英文/类名 | 定义 |
|------|-----------|------|
| **电路子系统** | SubsystemElectricity | 管理整个电路系统的 Subsystem，负责发现连接、创建 ElectricElement、驱动模拟。实现 `IUpdateable` |
| **电路元件** | ElectricElement | 电路中一个可模拟的节点，拥有 CellFaces（占据的方格面列表）和 Connections（连接列表）。核心方法：`Simulate()`、`GetOutputVoltage(face)` |
| **导线域** | WireDomainElectricElement | 一组连通导线形成的 ElectricElement，所有连通的导线共享同一电压。电压 = 所有输入的最大值（OR 逻辑） |
| **连接** | ElectricConnection | 两个 ElectricElement 之间的连接，记录：CellFace、ConnectorFace、ConnectorType、NeighborElectricElement、NeighborCellFace、NeighborConnectorFace、NeighborConnectorType |
| **连接路径** | ElectricConnectionPath | 描述两个相邻方格面之间的连接路径，包含：NeighborOffset(X/Y/Z)、NeighborFace、ConnectorFace、NeighborConnectorFace。预计算为 120 项查找表 |
| **连接器类型** | ElectricConnectorType | 枚举：`Input`（仅输入）、`Output`（仅输出）、`InputOutput`（双向） |
| **连接器方向** | ElectricConnectorDirection | 枚举：`Top`、`Left`、`Bottom`、`Right`、`In`（5个方向，相对于 mounting face 定义） |
| **连接掩码** | ConnectionMask | int 值，用于颜色隔离。导线默认 `int.MaxValue`（全通），涂色导线只与同色导线连通（`1 << color`） |
| **电压** | Voltage | float 值，范围 0.0-1.0。`>= 0.5` 为高电平（`IsSignalHigh`），`< 0.5` 为低电平。内部以 0-15 整数精度计算 |

---

## 4. 电路连接发现 (Connection Discovery)

### 连接判定流程

1. **遍历所有方格**：SubsystemElectricity 扫描地形中的每个方格
2. **识别电路方块**：通过 `IElectricElementBlock` 接口识别
3. **查询连接器**：对每个方格的每个面调用 `GetConnectorType(terrain, value, face, connectorFace, x, y, z)`
4. **匹配连接**：相邻方格的连接器如果类型兼容（Input↔Output 或 InputOutput↔InputOutput），则建立 ElectricConnection
5. **导线域合并**：连通的导线（WireBlock + WireThroughBlock）合并为一个 WireDomainElectricElement

### 兼容性规则

| 本端 | 对端 | 可连接 |
|------|------|--------|
| Input | Output | ✅ |
| Output | Input | ✅ |
| InputOutput | InputOutput | ✅ |
| Input | Input | ❌ |
| Output | Output | ❌ |

---

## 5. 排线概念 (Routing Concepts)

### 基本块 (BasicBlock)

**定义**：基本块是排线算法中的虚拟概念，对应寻路路径上的一个方格。它是排线算法的最小操作单元。

**结构**：
- 一个基本块拥有6个面（face 0-5，与 CellFace 编码一致）
- 每个面可以放置一个"导线节点"（即该面有导线）
- 导线节点位于面的中心点

**内部连通规则**（同方格内）：
- 同一基本块内，**相邻面**上的导线节点自动连接
- "相邻面"指共享一条边的两个面（即不是对面的关系）
- 多个面有导线时，它们在方格内部形成一个连通网络
- 从方块外部看，效果就是一条可以拐角的导线

**示例**：
```
face0(+Z) 和 face1(+X) 有导线 → 内部连通 → 形成 Z→X 拐角
face0(+Z) 和 face4(+Y) 有导线 → 内部连通 → 形成 Z→Y 拐角
face0(+Z) 和 face2(-Z) 有导线 → 不直接连通（对面），需第3面桥接
face0(+Z)、face1(+X)、face4(+Y) 有导线 → 三面互通 → 形成 Z-X-Y 拐角
```

**与游戏 WireBlock 的对应关系**：

| 基本块概念 | 游戏实现 |
|-----------|---------|
| 基本块某面有导线 | WireBlock 的 wireFacesBitmask 对应 bit 为 1 |
| 基本块内相邻面导线连通 | WireBlock.GetConnectedWireFacesMask 的 T型连接逻辑 |
| 基本块内对面需桥接 | WireBlock 需要3面导线才能连通对面 |

**相邻面的判定**：
两个面"相邻"（共享一条边） ⟺ 两个面不是对面（OppositeFace）

| 面 | 相邻面（可直连） | 对面（不可直连） |
|----|-----------------|-----------------|
| 0 (+Z) | 1, 3, 4, 5 | 2 (-Z) |
| 1 (+X) | 0, 2, 4, 5 | 3 (-X) |
| 2 (-Z) | 1, 3, 4, 5 | 0 (+Z) |
| 3 (-X) | 0, 2, 4, 5 | 1 (+X) |
| 4 (+Y) | 0, 1, 2, 3 | 5 (-Y) |
| 5 (-Y) | 0, 1, 2, 3 | 4 (+Y) |

**跨基本块连通**：
- 基本块 A 的 face N 与相邻基本块 B 的 OppositeFace(N) 连通
- 例：A 的 face0(+Z) 连通 B(x,y,z+1) 的 face2(-Z)
- 这等价于 WireBlock 跨方格的导线连接

### 基本块内的面关系 (Face Relationships in BasicBlock)

基本块内部，两个面之间的关系只有两种：

| 关系 | 定义 | 距离 | 示例 |
|------|------|------|------|
| **面对面**（相对面） | 两个面互为 OppositeFace | 1（直通） | face0(+Z) ↔ face2(-Z) |
| **面邻面**（相邻面） | 两个面共享一条边（非对面） | 1（拐角） | face0(+Z) ↔ face1(+X) |

**距离约定**：假设基本块内每个面到另一个面的距离都是 1。面对面和面邻面的距离都是 1，区别在于：
- 面对面：导线直通穿过方块中心
- 面邻面：导线在中心拐角

### 连接规则：基本块 → 穿线块替换 (BasicBlock → WireThrough Replacement)

**核心规则**：当排线路径经过基本块时，如果入口面和出口面是**面对面**关系，则将该基本块替换为**穿线块**，穿线块的连通轴平行于该对面所在的轴。

| 入口面和出口面关系 | 替换结果 | 穿线块连通轴 | data&3 |
|-------------------|---------|-------------|--------|
| 面对面 (face0↔face2) | 穿线块 | Z轴 | 0 |
| 面对面 (face1↔face3) | 穿线块 | X轴 | 1 |
| 面对面 (face4↔face5) | 穿线块 | Y轴 | 2 |
| 面邻面 | 保留基本块（导线在内部拐角） | — | — |

**示例**：两个相邻基本块左右排列，路径从左到右
1. 起点在左侧基本块的 face3(-X)
2. 出口面为 face1(+X) → 面对面关系
3. 替换为穿线块，data&3=1（X轴连通）
4. 右侧基本块同理 → 也是穿线块
5. 最终结果：两个X轴穿线块横向连接，路径长度=2
- 基本块某面要放导线，该面的**内侧**（面朝向的反方向）必须有实心不透明的方块支撑
- 即：face N 上放导线 → 坐标 (x - FaceToPoint3(N).X, y - FaceToPoint3(N).Y, z - FaceToPoint3(N).Z) 处的方块必须 `IsCollidable && !IsTransparent`
- 源码验证：`WireDomainElectricElement.OnNeighborBlockChanged` — 当支撑方块被移除或变透明时，该面导线自动脱落
- **空气方格的基本块**：6个面中只有贴着实心方块的面才能放导线，悬空面不能放

### 寻路路径 (Routing Path)

**定义**：区域1和区域2之间的排线路径，由一系列基本块组成，每个基本块内部记录哪些面有导线节点。

**路径表示**：路径 = 基本块序列，每个基本块携带 wireFacesBitmask（6-bit）

---

## 6. 3D 电路拓扑 (3D Circuit Topology)

### 导线的3D连通性

导线在3D空间中的连通规则：

1. **同方格内**：同一方格不同面的导线自动连通（T型连接），但需要至少3面导线才能连通对面
2. **跨方格**：face 上的导线连接到相邻方格的对面（face0 连接到 (x,y,z+1) 的 face2）
3. **穿线块**：直接连通 wiredFace 和 OppositeFace(wiredFace)，等效于一条贯穿方格的导线

### 电路的3D特性

- 电路是真正的3D网络，不限于2D平面
- 导线可以沿 X/Y/Z 任意轴走线
- 穿线块允许导线"穿过"一个实心方块（如墙壁）
- 电压信号在3D网络中传播，无方向限制

---

## 6. 常用代码模式 (Code Patterns)

### 读取方格的导线信息

```csharp
int cellValue = terrain.GetCellValue(x, y, z);
int contents = Terrain.ExtractContents(cellValue);
int data = Terrain.ExtractData(cellValue);

// 导线
if (BlocksManager.Blocks[contents] is WireBlock) {
    int wireFacesBitmask = WireBlock.GetWireFacesBitmask(cellValue); // 6-bit
    bool hasWireOnFace0 = WireBlock.WireExistsOnFace(cellValue, 0);
}

// 穿线块
if (BlocksManager.Blocks[contents] is WireThroughBlock) {
    int wiredFace = WireThroughBlock.GetWiredFace(data); // 返回 0/1/4
    // wiredFace=0 → Z轴连通, wiredFace=1 → X轴连通, wiredFace=4 → Y轴连通
}
```

### 面方向操作

```csharp
int oppositeFace = CellFace.OppositeFace(face);     // 对面
Vector3 dir = CellFace.FaceToVector3(face);          // 面的方向向量
Point3 offset = CellFace.FaceToPoint3(face);         // 邻居偏移量
```

### 电压判断

```csharp
bool isHigh = ElectricElement.IsSignalHigh(voltage); // voltage >= 0.5f
```

---

## 7. 术语对照速查 (Quick Reference)

| 中文 | 英文 | 代码中的位置 |
|------|------|-------------|
| 导线 | Wire | `WireBlock.cs` |
| 穿线块 | WireThrough | `WireThroughBlock.cs` 及5个材质子类 |
| 方格面 | CellFace | `CellFace.cs` |
| 电路元件 | ElectricElement | `ElectricElement.cs` |
| 导线域 | WireDomain | `WireDomainElectricElement.cs` |
| 连接 | Connection | `ElectricConnection.cs` |
| 连接路径 | ConnectionPath | `ElectricConnectionPath.cs` |
| 连接器类型 | ConnectorType | `ElectricConnectorType.cs` |
| 连接器方向 | ConnectorDirection | `ElectricConnectorDirection.cs` |
| 电路子系统 | SubsystemElectricity | `SubsystemElectricity.cs` |
| 电压 | Voltage | `ElectricElement.GetOutputVoltage()` |
| 高电平 | Signal High | `ElectricElement.IsSignalHigh()` → voltage >= 0.5 |
| 低电平 | Signal Low | voltage < 0.5 |
| 连接掩码 | ConnectionMask | `IElectricElementBlock.GetConnectionMask()` |
| 导线面掩码 | WireFacesBitmask | `WireBlock.GetWireFacesBitmask()` → data & 0x3F |
| 连通面掩码 | ConnectedWireFacesMask | `IElectricWireElementBlock.GetConnectedWireFacesMask()` |
| 基本块 | BasicBlock | 排线算法虚拟概念，对应路径上一个方格，6面各可有导线节点，相邻面自动连通 |
| 寻路路径 | Routing Path | 区域间排线路径，由基本块序列组成 |
