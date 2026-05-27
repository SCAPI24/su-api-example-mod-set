# WatchMod 项目文档

## 概述

WatchMod（手表Mod）在游戏UI左侧InventoryButton下方显示实时时钟，当玩家handcrafting slot 2（2x2网格左下角）放置RealTimeClockBlock（Index=187）时触发，模拟佩戴手表效果。

## 架构

### 替换模式：ComponentTemplate + IUpdateable（不替换SubsystemGameWidgets）

**为什么不用SubsystemGameWidgets替换？**
- ConsoleMod已替换SubsystemGameWidgets（GUID 6bf14dc6-32e7-4e8c-b3c4-438e0eee13ad）
- 同一个Subsystem只能被一个Mod替换，冲突会导致后者静默失败
- WatchMod改用独立Component挂到Player实体，与ConsoleMod完全兼容

### 核心类

| 类 | 作用 |
|----|------|
| WatchMod (Plug/WatchMod.cs) | IMod入口，EventBus订阅GameDatabase.GameDatabase，注册ComponentTemplate |
| SuWatchComponent (Subsystem/SuWatchComponent.cs) | Component + IUpdateable，检测slot物品，动态Attach/Detach Widget |

### 数据流

```
IMod.OnLoad → SubscribeEvent("GameDatabase.GameDatabase")
  → HandleGameDatabase: 创建 ComponentTemplate + Parameter + MemberComponentTemplate
    → Player实体实例化 SuWatchComponent

SuWatchComponent.Load → 获取 SubsystemTimeOfDay + SubsystemGameWidgets + ComponentCraftingTable
SuWatchComponent.Update (每帧):
  → ShouldShowWatch: 检查 craftingTable slot 2 是否有 RealTimeClockBlock(187)
  → 条件满足 → AttachUI: 创建 CanvasWidget + RectangleWidget + LabelWidget，挂到 LeftControlsContainer
  → 条件不满足 → DetachUI: 从容器移除 Widget
  → UI已挂 → UpdateDisplay: 从 SubsystemTimeOfDay 读时间，更新 Label
```

### 时间计算

```
SubsystemTimeOfDay.TimeOfDay = 0-1 float (0=午夜, ~0.25=黎明, ~0.5=正午, ~0.75=黄昏)
hours = (int)(TimeOfDay * 24 * 60) / 60
minutes = (int)(TimeOfDay * 24 * 60) % 60
day = (int)Math.Floor(Day) + 1  // Day从0开始计数
```

## 关键踩坑记录

### 1. SubsystemGameWidgets 替换冲突
- **错误**: 最初WatchMod替换SubsystemGameWidgets，游戏日志显示ConsoleMod先替换，WatchMod被静默覆盖
- **修复**: 改用ComponentTemplate+IUpdateable独立Component模式

### 2. Component.Load 签名
- **错误**: `Load(ValuesDictionary)` 单参数签名
- **修复**: `Load(ValuesDictionary, IdToEntityMap)` 双参数签名

### 3. Component.Load 跨assembly override
- **错误**: `protected internal override void Load(...)`
- **修复**: 跨assembly只能 `protected override void Load(...)`（SuComponentMap源码确认）

### 4. Database没有AddDatabaseObject
- **错误**: 调用 `database.AddDatabaseObject(componentTemplate)`
- **修复**: DatabaseObject构造时设NestingParent即自动加入database

### 5. LayoutDirection vs Direction
- **错误**: `StackPanelWidget.Direction` 使用 `Direction` 枚举
- **修复**: Direction属性的类型是 `LayoutDirection` 枚举

## 文件结构

```
WatchMod/
├── Plug/WatchMod.cs         # IMod入口
├── Subsystem/SuWatchComponent.cs  # Component+IUpdateable实现
├── WatchMod.csproj          # net8.0双平台项目
├── Obfuscar.xml             # 混淆配置
├── ModInfo.xml              # Mod信息
└── doc/PROJECT-LOG.md       # 本文件
```

## 关键源码引用

| 类 | 文件 | 作用 |
|----|------|------|
| ComponentGui | Survivalcraft/Game/ComponentGui.cs | 左侧按钮布局，LeftControlsContainer |
| ComponentCraftingTable | GameEntitySystem/ComponentCraftingTable.cs | handcrafting 2x2 grid，slot 0-3 |
| RealTimeClockBlock | Game/RealTimeClockBlock.cs | Index=187 |
| RealTimeClockElectricElement | Game/RealTimeClockElectricElement.cs | 时间电路元件，输出电压 |
| SubsystemTimeOfDay | Game/SubsystemTimeOfDay.cs | TimeOfDay 0-1，Day整数 |
| SubsystemGameWidgets | Game/SubsystemGameWidgets.cs | 管理GameWidget列表 |
| GameWidget | Engine/GameWidget.cs | CanvasWidget，含LeftControlsContainer |
| MiniMap Plug | Mod/SurvivalcraftMiniMap/Plug/MiniMap.cs | ComponentTemplate注册参考 |