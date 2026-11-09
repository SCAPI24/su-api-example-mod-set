# SuAPI 子系统分发器（Subsystem Dispatch）设计计划

> 状态：**仅梳理关系，未实现**
> 目标：让多个 Mod 能同时挂钩同一个引擎子系统，而不是抢同一个 `Class` 参数互相覆盖
> 本文只记录**问题、机制、方案与待定项**，不含实现代码

---

## 0. 要解决的问题

### 现象

两个 Mod 都想改同一个引擎子系统的行为（例：都想要"角色数值/攻击"相关处理），
它们都通过 SuAPI 的 Class 参数替换实现：

```csharp
// ModA
modInjector.Register("Game.SubsystemButtonBlockBehavior",
                     "ModA.SubsystemChannelBlockBehavior");

// ModB
modInjector.Register("Game.SubsystemButtonBlockBehavior",
                     "ModB.SubsystemChannelBlockBehavior");
```

**结果：只有一个生效。** 后注册的覆盖先注册的（或按 `ConflictAction` 报冲突），
另一个 Mod 的功能彻底不执行 —— 这就是用户说的"A 用掉了这个调用位置，B 就用不了了"。

### 根因

`Pak/Database.xml` 里，每个子系统模板只有**一个** `Class` 参数：

```xml
<SubsystemTemplate Name="ButtonBlockBehavior" Guid="0302cf98-...">
  <Parameter Name="Class" Guid="4407e30b-..." Value="Game.SubsystemButtonBlockBehavior" Type="string" />
</SubsystemTemplate>
```

`Class` 是 `Type="string"` 的**单值参数**。引擎加载子系统时按这个字符串反射创建**一个**实例
（`Subsystem..ctor` ← `SubsystemTemplate` → `Class` 值）。

**一个位置只能装一个类名** —— 这是数据结构的硬约束，不是实现缺陷。

### 数量级

`Pak/Database.xml` 里共有 **110 个 `<SubsystemTemplate>`**。
其中任何一个都可能被两个以上 Mod 抢，且抢的方式完全一样。

---

## 1. 目标行为

用户的原话：

> 我想让的是 A 和 B 都能用 A.Subsystem / B.Subsystem 去被执行。
> 子系统加入 A 在 `Game.Subsystem.Update` 信号来的时候，替换的 `A.Subsystem.Update` 有响应。
> B 在 `Game.Subsystem.Update` 信号来的时候，替换的 `B.Subsystem.Update` 有响应。

### 1.1 用户确认的模型（唯一正确的形态）

**SuAPI 作为唯一一次替换，把整个 Class 位置占掉；ModA / ModB 的类由 SuAPI 分派执行。**

```
原始:   Game.SubsystemButtonBlockBehavior          ← 引擎自带
            │
            │  SuAPI 唯一一次 Register + Apply
            ↓
SuAPI:  SuAPI.SubsystemButtonBlockBehavior         ← 只占这一个值，永久固定
            │
            ├── 引擎调用它的虚方法
            │       ↓
            │   TriggerEvent("Game.Subsystem.Update")
            │       ├── ModA 的逻辑执行（订阅者 A）
            │       └── ModB 的逻辑执行（订阅者 B）
            │
            └── ModA / ModB 不再 Register，不再出现在 Class 参数里
```

对照用户原话：

```
原始:  Game.SubsystemButtonBlockBehavior
SuAPI: SuAPI.SubsystemButtonBlockBehavior      ← SuAPI 覆盖 Game（唯一一次）
ModA:  A.SubsystemButtonBlockBehavior          ← ModA 从 SuAPI 执行（事件订阅）
ModB:  B.SubsystemButtonBlockBehavior          ← ModB 从 SuAPI 执行（事件订阅）
```

⚠️ **关键澄清**：ModA / ModB **不是**再往 Class 参数里写自己的类名。
它们在 Class 参数里的"位置"是**逻辑位置**（由分发器调用），不是**数据位置**（Class 字符串）。
Class 参数自始至终只有 `SuAPI.SubsystemButtonBlockBehavior` 一个值。

### 1.2 为什么必须这样

`ModInjector.Apply` 的匹配是**精确字符串比对**（见 §2.6）：

```csharp
if (currentValue != record.OriginalClassName) continue;   // ModInjector.cs:152
```

一旦 SuAPI 把值从 `Game.Xxx` 改成 `SuAPI.Xxx`，之后任何
`OriginalClassName = "Game.Xxx"` 的 Register **都匹配不上了**（静默跳过，`Applied` 永为 false）。

所以"SuAPI 先改，ModA 再改，ModB 再改"这条链**在引擎层面不成立**。
ModA / ModB 只能通过分发器的**事件分派**被执行。

### 1.3 与现状对比

| | 现状 | 目标 |
|---|---|---|
| Class 参数值 | `ModA.XxxSubsystem` 或 `ModB.XxxSubsystem`（互相覆盖） | `SuAPI.XxxSubsystem`（固定值，永不变） |
| 谁写 Class | 各 Mod 自己 Register | **只有 SuAPI Core** |
| Mod 接入方式 | 继承子系统 + 改 Class 参数 | 只 `SubscribeEvent` |
| Mod 的类在哪 | Class 参数字符串里 | **不在 Class 里**，由分发器按事件调用 |
| 同时生效 | ❌ 只能一个 | ✅ 任意多个 |
| 执行顺序 | 无（被覆盖的看不到） | 由 `EventPriority` 决定 |
| 新增第三个 Mod | 再抢一次，覆盖前两个 | 再订阅一次，零冲突 |

---

## 2. 引擎侧机制（已核实）

### 2.1 子系统是"按 Class 字符串反射创建的单实例"

- `Pak/Database.xml` → `<SubsystemTemplate>` → `<Parameter Name="Class" Value="Game.Xxx">`
- 每个模板在 `Project` 里创建**一个**子系统实例
- 不存在"同一个模板创建两个实例"的机制

**结论：不能靠实例化解决，只能靠内部分派。**

### 2.2 `Subsystem` 基类的可覆写点

`EntitySystem/GameEntitySystem/Subsystem.cs`

| 成员 | 签名 | 跨程序集能否覆写 |
|---|---|---|
| `Load` | `protected internal virtual void Load(ValuesDictionary)` | ✅ 用 `protected override` |
| `Save` | `protected internal virtual void Save(ValuesDictionary)` | ✅ 同 |
| `Dispose` | `public virtual void Dispose()` | ✅ |
| `OnEntityAdded` | `protected internal virtual void OnEntityAdded(Entity)` | ✅ |
| `OnEntityRemoved` | `protected internal virtual void OnEntityRemoved(Entity)` | ✅ |

⚠️ `protected internal` 跨程序集**只能**以 `protected override` 覆写
（与 AGENTS.md 记录的 `Component.Load` 是同一类约束）。

### 2.3 `Update` 不在 `Subsystem` 里

`Subsystem` 本身**没有** `Update(float dt)`。
每帧驱动的机制是 `IUpdateable`：

```csharp
// Survivalcraft/Game/IUpdateable.cs
public interface IUpdateable
{
    UpdateOrder UpdateOrder { get; }
    void Update(float dt);
}
```

`SubsystemUpdate.Load()` 会自动把所有实现了 `IUpdateable` 的子系统登记进更新循环
（`SubsystemUpdate.cs:107-111`：`foreach (IUpdateable item in Project.FindSubsystems<IUpdateable>()) AddUpdateable(item);`）。

**所以分发器要每帧被调用，必须 `: Subsystem, IUpdateable`，并提供 `UpdateOrder`。**

### 2.4 子系统注册进引擎的顺序

```
Mod.OnLoad() 全部执行完毕
   ↓
GameDatabase 构造 → TriggerEvent("GameDatabase.GameDatabase")   ← 改 Class 参数在这里生效
   ↓
Project 创建子系统实例（按 Class 反射）
   ↓
SubsystemUpdate.Load() → 收集所有 IUpdateable → 每帧驱动
```

⚠️ 关键顺序：**`Register` 必须发生在 `GameDatabase.GameDatabase` 事件前或事件内**
（SuAPI 的 `modInjector.Apply(database, modName)` 就是在事件里应用的）。

### 2.5 事件总线能力

`EntitySystem/SuAPI/ModEventBus.cs:56-104` `TriggerEvent`：

```csharp
public object[][] TriggerEvent(string eventName, object[] args)
```

- ✅ **多订阅者**：遍历所有订阅，逐个回调，收集返回值
- ✅ **按优先级排序**：`OrderBy(Priority).ThenBy(Order)`
- ✅ **同一 `args` 引用**传给所有订阅者（可共同修改）
- ⚠️ **异常被吞**：订阅者抛异常 → 记 `Console.WriteLine` + 结果里塞 `"Error: ..."`，**不中断其它订阅者**

### 2.6 Class 替换是"精确字符串匹配"，不是继承链

`EntitySystem/SuAPI/ModInjector.cs:147-152`：

```csharp
private void ProcessParameter(DatabaseObject param, string currentValue)
{
    foreach (var record in _injectionRecords)
    {
        if (record.Applied) continue;
        if (currentValue != record.OriginalClassName) continue;   // ← 精确比对
        ...
    }
}
```

`Apply()` 会遍历**所有** `Class` 参数（`ModInjector.cs:135-144`），
对每个参数，用**当前值**去逐条匹配注册记录的 `OriginalClassName`：

```csharp
string currentValue = param.Value as string;   // 数据库里的当前值
ProcessParameter(param, currentValue);
```

匹配成功后 `param.Value = record.NewClassName`（`ModInjector.cs:203`），
**`currentValue` 随即被改写**，同一轮后续 record 只能靠 `_changeTracker` 走冲突分支。

#### 由此得出三条硬结论

| # | 结论 | 依据 |
|---|---|---|
| **C1** | 第一个成功的替换会"吃掉"原始类名 | 改写后 `currentValue` 不再等于 `OriginalClassName` |
| **C2** | 后续 Mod 若仍写 `Original = 原始类名`，**静默失效** | `continue` 跳过，`Applied` 永为 false，无任何提示 |
| **C3** | 想让多个 Mod 都执行，**只能靠单实例内部分派** | Class 只能存一个字符串值 |

#### 错误写法（用户最初设想，不成立）

```
原始:  Game.SubsystemButtonBlockBehavior
SuAPI: Register(Game → SuAPI)    → currentValue = "SuAPI..."
ModA:  Register(Game → A)        → ✗ 匹配不上（C2）
ModB:  Register(Game → B)        → ✗ 匹配不上（C2）
```

#### 正确写法（本文档方案）

```
原始:  Game.SubsystemButtonBlockBehavior
SuAPI: Register(Game → SuAPI)    → currentValue = "SuAPI..."（唯一一次，之后不再改）
ModA:  SubscribeEvent(...)        → ✓ 被 SuAPI 分发器调用
ModB:  SubscribeEvent(...)        → ✓ 被 SuAPI 分发器调用
```

### 2.7 现有可复用的中间件

`Mod/SuEventBus/Func/SuEventHub.cs`：

- 类型化事件（`Subscribe<TEvent>(Action<TEvent>)` / `Publish<TEvent>(TEvent)`）
- 已用于 `Channel` ↔ `ScMultiplayer` 的跨 Mod 协调
- 契约 DLL 随包分发（`Lib/SuEventBus.dll`）

**两套事件总线的区别**：

| | `ModEventBus.TriggerEvent` | `SuEventHub` |
|---|---|---|
| 定义位置 | `EntitySystem/SuAPI`（引擎侧，内置） | `Mod/SuEventBus`（外部 DLL） |
| 参数形式 | `object[]`（弱类型） | 泛型 `TEvent`（强类型） |
| 跨 Mod 共享 | ✅ 所有 Mod 都能拿到 `IModEventBus` | ✅ 需各自装 `SuEventBus.dll` |
| 用户口径 | **明确要求用这个** | 备选 |

用户明确要求 `ModManager.ModEventBus.TriggerEvent`，本文档按这条走，
`SuEventHub` 记为备选（见 §7 待定项）。

---

## 3. 方案：分发器（Dispatch）

### 3.1 核心结构

```
Pak/Database.xml
  <SubsystemTemplate Name="ButtonBlockBehavior">
    <Parameter Name="Class" Value="Game.SubsystemButtonBlockBehavior" />
                    ↓
                    │ SuAPI Core 在 GameDatabase.GameDatabase 事件里 Register + Apply
                    │ （唯一一次，此后这个值再不被任何 Mod 修改）
                    ↓
    <Parameter Name="Class" Value="SuAPI.SubsystemButtonBlockBehavior" />

SuAPI.SubsystemButtonBlockBehavior          ← 分发器：唯一被引擎实例化的类
  : Game.SubsystemButtonBlockBehavior       ← 继承具体子系统，保留原版能力
  , IUpdateable                             ← 需要每帧调用时
{
    每个虚方法 → ModEventBus.TriggerEvent("Game.Subsystem.Update", args)
                     ↓                          ↓
                  ModA 订阅                  ModB 订阅
                  （逻辑执行）                （逻辑执行）
}
```

**三个角色的分工**：

| 角色 | 在 Class 参数里的位置 | 由谁调用 |
|---|---|---|
| `Game.SubsystemButtonBlockBehavior` | 原始值（被覆盖） | — |
| `SuAPI.SubsystemButtonBlockBehavior` | **唯一占位**（永久） | 引擎直接实例化 |
| `A.SubsystemButtonBlockBehavior` | **不出现** | 由 SuAPI 分发器通过事件调用 |
| `B.SubsystemButtonBlockBehavior` | **不出现** | 由 SuAPI 分发器通过事件调用 |

ModA / ModB 的类**只存在于各自 Mod 的 DLL 里**，由事件回调触发执行。
引擎完全不知道它们的存在 —— 这正是"两者都能生效"的关键。

### 3.2 分发器要做的事

| 职责 | 说明 |
|---|---|
| **独占 Class 参数** | 由 SuAPI Core 在 `GameDatabase.GameDatabase` 里 `Register` + `Apply`，Mod 完全不碰 |
| **继承具体子系统** | 必须继承**被替换的那个**子系统（不是裸 `Subsystem`），否则原版能力全丢 |
| **转发所有可覆写点** | `Load` / `Save` / `Dispose` / `OnEntityAdded` / `OnEntityRemoved` / `Update` + 该子系统的所有 `virtual` 方法 |
| **事件名统一** | `"Game.Subsystem.Update"`（用户指定），用 `args[1]` 区分具体操作 |
| **不吞异常** | 自己的转发逻辑不能因某个 Mod 出错而中断其它 Mod（`TriggerEvent` 已天然满足） |

### 3.3 事件载荷约定

⚠️ **两种命名方式，需最终统一（见 §7 Q5）**：

#### 方式 A：单事件 + 操作名（§3.3 原草案）

```csharp
new object[]
{
    this,           // [0] 分发器实例（Mod 可读 Project / ValuesDictionary）
    "Update",       // [1] 操作名：Load / Save / Update / OnEditBlock / ...
    payload,        // [2] 该操作的原生参数（如 dt、ValuesDictionary、具体参数包）
    null,           // [3] 结果槽：订阅者写入返回值（bool / int / object）
}
```

订阅方：
```csharp
eventBus.SubscribeEvent("Game.Subsystem.Update", args =>
{
    if ((string)args[1] != "Load") return null;    // 每个订阅者都要判断操作名
    ...
});
```

#### 方式 B：每操作一事件（§3.5 示例采用）

```csharp
SuApiHooks.Fire("SubsystemTimeOfDay.Load",   new object[] { this, valuesDictionary });
SuApiHooks.Fire("SubsystemTimeOfDay.Update", new object[] { this, dt });
```

订阅方：
```csharp
eventBus.SubscribeEvent("SubsystemTimeOfDay.Load",  args => { ... });
eventBus.SubscribeEvent("SubsystemTimeOfDay.Update", args => { ... });
// 订阅精确，无判断开销
```

| | 方式 A | 方式 B |
|---|---|---|
| 事件名数量 | 1 个（按子系统） | 每个操作 1 个 |
| 订阅精确度 | 要判断操作名 | 天然精确 |
| 用户原话 | "事件名 = `Game.SubsystemButtonBlockBehavior`" | — |

**倾向 B**（订阅精确、代码清晰），但**方式 A 更接近用户最初的原话**，需 Q5 拍板。

**共通点**：`args[0]` 始终是**分发器实例**（Mod 从它取 `Project` / 任何需要的东西）。
**SuAPI 不构造上下文对象** —— Mod 需要什么自己从 `args[0]` 上取（见 §3.5.1）。

### 3.4 各角色写法对照（关键：ModA/ModB 不写 Register）

| 角色 | 要做的事 | 不做的事 |
|---|---|---|
| **SuAPI Core** | `Register("Game.SubsystemButtonBlockBehavior", "SuAPI.SubsystemButtonBlockBehavior")`<br>在 `GameDatabase.GameDatabase` 事件里 `Apply(database, "SuAPI")` | — |
| **ModA** | `eventBus.SubscribeEvent("Game.Subsystem.Update", args => { ... })` | ❌ 不 `Register`<br>❌ 不碰 Class 参数<br>❌ 不需要继承 `Subsystem` |
| **ModB** | 与 ModA 完全相同（各自订阅，互不影响） | 同上 |

**ModA / ModB 的代码形态**（不需要是 `Subsystem` 子类）：

```csharp
// 普通类即可，可以完全不含 Subsystem 继承
public sealed class ModAAttackBoost
{
    public void OnLoad(IModEventBus eventBus)
    {
        eventBus.SubscribeEvent("Game.Subsystem.Update", args =>
        {
            if (!(args[1] is string op) || op != "Update") return null;
            var project = /* 从 args[0] 或保存的引用拿 */;
            ApplyAttackBoost(project);
            return new object[] { true };
        }, EventPriority.NORMAL);
    }
}
```

> 用户原话"ModA 从 SuAPI 执行 / ModB 从 SuAPI 执行"就是这个含义：
> 两个 Mod 的代码都由 SuAPI 分发器**调用**执行，而不是各自去改 Class 参数。

---

## 3.5 完整示例：`SubsystemTimeOfDay`（用户已确认思路可行）

> 2026-11-09 用户确认："这个思路可以记录进去，这个思路是没有问题的。"

### 3.5.1 设计原则：SuAPI 只占位 + 转发，不预设 Mod 需求

**用户明确的口径**：

> SuAPI 分发器是只用于占位，我不可能提前把所有所需的东西都能够知道，
> 很多能在 mod 中拿到的需要交给 mod

| 项 | 谁负责 |
|---|---|
| 占住 `Class` 位置 | ✅ **SuAPI**（唯一一次 Register） |
| 提供 `Load` / `Update` 两个"时刻" | ✅ **SuAPI**（转发事件） |
| 知道 ModA 需要 `SubsystemWeather` | ❌ **ModA 自己 `FindSubsystem`** |
| 知道 ModB 需要 `m_subsystemGameInfo` | ❌ **ModB 自己用 SuAPI 反射工具取** |
| 提前构造"上下文对象"（如 `TimeOfDayContext`） | ❌ **不需要，也不该做** |

**关键**：分发器把 **宿主实例（`this`）** 交给 Mod，Mod 需要什么自己从宿主上取。
SuAPI 无法预知所有 Mod 的需求，也不应试图封装。

### 3.5.2 引擎现状（已核实）

`Survivalcraft/Game/SubsystemTimeOfDay.cs`：

```csharp
public class SubsystemTimeOfDay : Subsystem        // ← 不实现 IUpdateable
{
    private SubsystemGameInfo m_subsystemGameInfo;  // ← private
    private SubsystemSeasons m_subsystemSeasons;    // ← private
    public bool TimeOfDayEnabled = true;
    public double TimeOfDayOffset { get; set; }     // ← public，可直接改
    public float TimeOfDay { get; }                 // ← public
    public float Midnight { get; }                  // ← public
    public double CalculateDay(double)              // ← public
    public float CalculateTimeOfDay(double)         // ← public

    protected override void Load(ValuesDictionary valuesDictionary)
    {
        m_subsystemGameInfo = base.Project.FindSubsystem<SubsystemGameInfo>(true);
        m_subsystemSeasons  = base.Project.FindSubsystem<SubsystemSeasons>(true);
        TimeOfDayOffset = valuesDictionary.GetValue<double>("TimeOfDayOffset");
        UpdateStarts();
    }
}
```

| 项 | 状态 | 对 Mod 的影响 |
|---|---|---|
| 实现 `IUpdateable` | ❌ | 分发器要自己加 `IUpdateable` |
| 有 `Update` 方法 | ❌ | 分发器的 `Update` 是**新增**，无 `base` 可调 |
| `m_subsystemGameInfo` | private | Mod 需 SuAPI 反射工具 |
| `TimeOfDay` / `Midnight` / `TimeOfDayOffset` | **public** | Mod 直接读改，**不需要工具** |

> 这正是"很多能在 mod 中拿到的需要交给 mod"的体现：
> 大部分状态是 public，Mod 直接访问即可；只有少数 private 才需要反射工具。

### 3.5.3 SuAPI 分发器（薄，不含任何业务知识）

```csharp
namespace SuAPI
{
    /// <summary>
    /// 仅占位 + 转发。不预设任何 Mod 需要什么。
    /// · 继承原类 → Mod 可用 base 语法访问原类 public 成员；
    /// · 实现 IUpdateable → 每帧驱动所有订阅了 Update 事件的 Mod。
    /// </summary>
    public class SubsystemTimeOfDay : Game.SubsystemTimeOfDay, IUpdateable
    {
        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);          // 原版初始化照跑
            SuApiHooks.Fire("SubsystemTimeOfDay.Load",
                new object[] { this, valuesDictionary });
        }

        protected override void Save(ValuesDictionary valuesDictionary)
        {
            SuApiHooks.Fire("SubsystemTimeOfDay.Save",
                new object[] { this, valuesDictionary });
            base.Save(valuesDictionary);
        }

        public void Update(float dt)
        {
            SuApiHooks.Fire("SubsystemTimeOfDay.Update",
                new object[] { this, dt });
        }
    }
}
```

**分发器全部代码就这些。** 它不知道有 ModA / ModB，
不知道 ModA 要改天气、ModB 要改时间，只提供两个时刻。

### 3.5.4 SuAPI 的辅助工具（极简）

```csharp
namespace SuAPI
{
    public static class SuApiHooks
    {
        public static void Fire(string eventName, object[] args) =>
            ModManager.Instance?.ModEventBus?.TriggerEvent(eventName, args);
    }
}
```

**SuAPI Core 唯一要做的事**（`SuAPICoreMod.OnLoad` 里）：

```csharp
modInjector.Register("Game.SubsystemTimeOfDay", "SuAPI.SubsystemTimeOfDay");
```

### 3.5.5 ModA：改下雨和起雾（自己找 `SubsystemWeather`）

```csharp
namespace ModA
{
    public sealed class WeatherPatch
    {
        private SubsystemWeather m_weather;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            // ① Load 钩子：自己找需要的子系统（SuAPI 不知道也不关心）
            eventBus.SubscribeEvent("SubsystemTimeOfDay.Load", args =>
            {
                var host = (Game.SubsystemTimeOfDay)args[0];
                // 用户口径：Load 时机不保证对方已 Load 完，自己判空即可
                m_weather = host.Project.FindSubsystem<SubsystemWeather>(false);
                return new object[] { false };
            }, EventPriority.NORMAL);

            // ② Update 钩子：每帧强制天气
            eventBus.SubscribeEvent("SubsystemTimeOfDay.Update", args =>
            {
                var host = (Game.SubsystemTimeOfDay)args[0];
                if (m_weather == null)
                {
                    // 迟到的子系统：在 Update 里补拿一次（此时所有 Load 都已跑完）
                    m_weather = host.Project.FindSubsystem<SubsystemWeather>(false);
                    if (m_weather == null) return new object[] { false };
                }
                if (!m_weather.IsPrecipitationStarted) m_weather.ManualPrecipitationStart();
                if (!m_weather.IsFogStarted)           m_weather.ManualFogStart();
                return new object[] { false };
            }, EventPriority.NORMAL);
        }
    }
}
```

### 3.5.6 ModB：只管改时间（用 SuAPI 已有工具读 private 字段）

```csharp
namespace ModB
{
    public sealed class MidnightPatch
    {
        private SubsystemGameInfo m_gameInfo;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            // SuAPI 已有工具（不需要自己写反射）
            IModParentField parentField = modInjector.ModParentField;

            // ① Load 钩子：用 SuAPI 工具拿父类私有字段
            eventBus.SubscribeEvent("SubsystemTimeOfDay.Load", args =>
            {
                var host = (Game.SubsystemTimeOfDay)args[0];
                m_gameInfo = parentField.GetParentField<SubsystemGameInfo>(
                    host, "m_subsystemGameInfo");
                return new object[] { false };
            }, EventPriority.NORMAL);

            // ② Update 钩子：强制跳到午夜
            eventBus.SubscribeEvent("SubsystemTimeOfDay.Update", args =>
            {
                var host = (Game.SubsystemTimeOfDay)args[0];
                if (m_gameInfo == null) return new object[] { false };   // ← 自己判空

                if (IsNotMidnight(host))
                {
                    float timeToMidnight = IntervalUtils.Interval(
                        host.CalculateTimeOfDay(m_gameInfo.TotalElapsedGameTime),
                        host.Midnight - 0.09f);
                    host.TimeOfDayOffset += timeToMidnight;   // public，直接改
                }
                return new object[] { false };
            }, EventPriority.NORMAL);
        }

        private static bool IsNotMidnight(Game.SubsystemTimeOfDay host) =>
            !(IntervalUtils.IsBetween(host.TimeOfDay, host.Midnight - 0.1f, host.Midnight)
           || IntervalUtils.IsBetween(host.TimeOfDay, 0f, 0.1f));
    }
}
```

### 3.5.7 执行流程

```
Pak/Database.xml
  <SubsystemTemplate Name="TimeOfDay">
    <Parameter Name="Class" Value="Game.SubsystemTimeOfDay" />
                    ↓ SuAPI 替换（唯一一次，此后永不改变）
    <Parameter Name="Class" Value="SuAPI.SubsystemTimeOfDay" />

引擎创建 SuAPI.SubsystemTimeOfDay（唯一实例）
    │
    ├── Load(vd)
    │     ├── base.Load(vd)                    ← 原版：抓 GameInfo/Seasons、读 TimeOfDayOffset
    │     └── Fire("SubsystemTimeOfDay.Load")
    │             ├── ModA：host.Project.FindSubsystem<SubsystemWeather>()   ← 自己找
    │             └── ModB：parentField.GetParentField<SubsystemGameInfo>(host, "m_subsystemGameInfo")  ← SuAPI 工具
    │
    └── 每帧（SubsystemUpdate 通过 IUpdateable 接口驱动）
          └── Fire("SubsystemTimeOfDay.Update")
                  ├── ModA：强制下雨 / 起雾
                  └── ModB：强制跳到午夜
```

### 3.5.8 这个示例验证的三件事

| # | 验证点 | 结果 |
|---|---|---|
| 1 | SuAPI 只占位、不预设 Mod 需求 | ✅ 分发器仅 20 行，不含任何业务逻辑 |
| 2 | Mod 自己拿需要的资源 | ✅ ModA `FindSubsystem`、ModB 反射取字段 |
| 3 | 两个 Mod 的 `Load` / `Update` 都被调用 | ✅ `TriggerEvent` 多订阅者天然满足 |

### 3.5.9 遗留注意点

| 项 | 说明 |
|---|---|
| **`Load` 顺序** | `Project.LoadSubsystem` 按依赖递归调 `Load`。`SubsystemTimeOfDay.Load` 触发时，其他子系统**实例已存在**（`Project.cs:65` 在 `Load` 前 `Add`），但**它们的 `Load` 可能还没跑**。<br>**用户口径（2026-11-09）：Mod 自己用 `if (xxx != null)` 判断即可，SuAPI 不做任何保证。** |
| **`Update` 无 `base`** | `SubsystemTimeOfDay` 本身不实现 `IUpdateable`、无 `Update`，所以分发器的 `Update` 是新增的，**不需要也不能调 `base.Update`** |
| **私有字段访问** | **用 SuAPI 已有工具 `IModParentField`**，不要自己写反射（见 §3.5.10） |
| **事件名约定** | 本示例用 `"SubsystemTimeOfDay.Load"` / `"SubsystemTimeOfDay.Update"`（按操作分事件）。§3.3 草案用的是单事件 + `args[1]` 操作名，**两者需统一**（见 Q5） |

### 3.5.10 私有成员访问：用 SuAPI 已有工具 `IModParentField`

> 用户口径（2026-11-09）："现有 Mod 的真实做法（ScMultiplayer），只需要用 SuAPI 已经存在的工具就行了，在工具集里有"

**不需要造新工具。** SuAPI 已提供 `IModParentField` / `IModParentMethod`，
Mod 在 `OnLoad(eventBus, modInjector)` 里就能拿到 `modInjector.ModParentField`。

#### 工具 API（`EntitySystem/SuAPI/IModParentField.cs`）

| 方法 | 用途 | 适用场景 |
|---|---|---|
| `T GetParentField<T>(object target, string fieldName, Type declaringType = null)` | 泛型读私有字段 | 一次性读 |
| `object GetParentField(object target, string fieldName, Type declaringType = null)` | 非泛型读 | 类型未知时 |
| `void ModifyParentField(object target, string fieldName, object newValue, Type declaringType = null)` | 写私有字段 | 一次性写 |
| `ModFieldRef<TTarget, TValue> BindFieldRef<TTarget, TValue>(string fieldName)` | 绑定成 `ref` 委托 | **每帧热路径必用** |
| `void ModifyStaticField(Type, string, object)` / `object GetStaticField(Type, string)` | 静态字段 | — |

`IModParentMethod` 对应提供：
`InvokeParentMethod(...)` / `GetInstanceMethodInfo(...)` / `GetStaticMethodInfo(...)` /
`BindMethodRef(...)`（同样有 `Bind` 版本给热路径）。

#### 修正后的 ModB 示例（用 SuAPI 工具，不写反射）

```csharp
namespace ModB
{
    public sealed class MidnightPatch
    {
        private SubsystemGameInfo m_gameInfo;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            IModParentField parentField = modInjector.ModParentField;

            eventBus.SubscribeEvent("SubsystemTimeOfDay.Load", args =>
            {
                var host = (Game.SubsystemTimeOfDay)args[0];

                // ✅ 用 SuAPI 已有工具读父类私有字段（不自己写 GetField）
                m_gameInfo = parentField.GetParentField<SubsystemGameInfo>(
                    host, "m_subsystemGameInfo");

                return new object[] { false };
            }, EventPriority.NORMAL);

            eventBus.SubscribeEvent("SubsystemTimeOfDay.Update", args =>
            {
                var host = (Game.SubsystemTimeOfDay)args[0];
                if (m_gameInfo == null) return new object[] { false };   // ← 用户口径：自己判空
                ...
                return new object[] { false };
            }, EventPriority.NORMAL);
        }
    }
}
```

#### 读 `SubsystemWeather` 私有成员的写法

```csharp
// SubsystemWeather 的私有成员（实测清单）
//   m_subsystemGameInfo      SubsystemGameInfo
//   m_subsystemBlocksScanner SubsystemBlocksScanner   ← ScMultiplayer 实际用到
//   m_subsystemParticles     SubsystemParticles
//   m_subsystemAudio         SubsystemAudio
//   m_subsystemSeasons       SubsystemSeasons
//   m_precipitationStartTime double / m_precipitationEndTime double
//   m_fogStartTime           double / m_fogEndTime           double
//   m_lightningIntensity     float  / m_rainVolumeFactor     float
// 注意：SubsystemTerrain / SubsystemSky / SubsystemTime 是 public，不需要工具

var weather = host.Project.FindSubsystem<SubsystemWeather>(false);
if (weather == null) return;                       // 用户口径：自己判空

// 读（一次性）
var scanner = parentField.GetParentField<SubsystemBlocksScanner>(
    weather, "m_subsystemBlocksScanner");

// 写（一次性）
parentField.ModifyParentField(weather, "m_lightningIntensity", 1f);

// 每帧热路径：先 Bind，之后零查找零装箱
private ModFieldRef<SubsystemWeather, float> m_getRainVolume;
// Load 阶段：
m_getRainVolume = parentField.BindFieldRef<SubsystemWeather, float>("m_rainVolumeFactor");
// 每帧：
ref float v = ref m_getRainVolume(weather);
v = 0.5f;
```

#### 为什么直接复用而不是新造

| 理由 | 说明 |
|---|---|
| 已经是公开契约 | `IModInjector.ModParentField` → `IModParentField`，所有 Mod 的 `OnLoad` 都拿得到 |
| 已处理多态 | `declaringType` 参数用于"字段声明在父类、实例是子类"的场景 |
| 有热路径优化 | `BindFieldRef` 返回 `ref` 委托，无反射查找、无装箱 |
| 全仓已在用 | `Mod/SuMultiplayer/Engine/AvatarActionBridge.cs:543-596` 等大量实际使用 |

---

## 4. 关键设计决策（待定，需用户拍板）

### D1. 分发器放哪？

| 选项 | 优点 | 缺点 |
|---|---|---|
| **A. SuAPI Core**（`EntitySystem/SuAPICore`） | 内置，所有 Mod 天然可用，无需额外装 | 增加 Core 体积；Core 是内置 Mod，改动影响全局 |
| **B. 独立中间件 Mod**（如 `SuDispatch`） | 隔离、可独立升级 | 用户必须额外安装；否则分发器类找不到 → 反射失败 |

**倾向 A**：这是基础设施，不该让用户额外装。且 SuAPICore 已有 `OnLoad(eventBus, modInjector)`，天然能拿到 `Register` 能力。

⚠️ **风险**：SuAPI Core 是内置 Mod（`LoadBuiltInMod`），如果分发器类在 Core 里，
但被替换的子系统在**更早**的加载阶段被创建，会找不到类。需验证加载顺序。

### D2. 事件粒度

| 选项 | 形式 | 优点 | 缺点 |
|---|---|---|---|
| **A. 单一事件 + 操作名字段** | `"Game.Subsystem.Update"` + `args[1]="OnEditBlock"` | 用户原话；订阅一次管所有 | 每个订阅者都要 if 判断操作名 |
| **B. 每操作一事件** | `"Game.Subsystem.Update.OnEditBlock"` 等 | 订阅精确、无判断开销 | 19 个虚方法 → 19 个事件名；用户原话是单事件 |

**用户已明确要 A**，本文档按 A 记录。B 记为备选。

### D3. 原版行为怎么处理？

分发器继承具体子系统后，虚方法里有三种选择：

| 选项 | 语义 | 适用 |
|---|---|---|
| **A. 先调 base，再发事件** | 原版逻辑跑完，Mod 再改 | 大多数情况（保留引擎行为） |
| **B. 只发事件，不调 base** | 原版逻辑**完全消失**，全靠 Mod | 想彻底接管时 |
| **C. 由 Mod 决定** | 事件结果槽里写 `SkipBase=true` | 最灵活，但复杂度高 |

**待定。** 需要用户明确：换了分发器之后，**原版功能是否还保留**。

### D4. 数值冲突怎么合并？

用户例子：两个 Mod 都改"角色攻击力"。若两者都直接写同一个字段：

```csharp
// ModA: stats.AttackPower = 10f;
// ModB: stats.AttackPower = 20f;   // 后者覆盖前者
```

| 选项 | 做法 |
|---|---|
| **A. 后者覆盖** | 简单，符合 `TriggerEvent` 的天然顺序 |
| **B. 加算/乘算叠加** | 需要约定"增量"语义（谁提供 base、谁提供 modifier） |
| **C. Mod 自己协商** | 分发器不管，Mod 之间用其它事件协商（如 `SuEventHub`） |

**待定。** 这是最容易出隐蔽 bug 的地方。

### D5. 110 个子系统全做还是按需做？

`Pak/Database.xml` 有 **110 个 `<SubsystemTemplate>`**。每个都可能被抢。

| 选项 | 工作量 | 说明 |
|---|---|---|
| **A. 只做被实际争抢的** | 小 | 按用户实际遇到的（例：`ButtonBlockBehavior`）逐个人工做 |
| **B. 全部 110 个都生成分发器** | 大 | 代码量爆炸；且每个要继承不同基类、覆写不同虚方法 |
| **C. 先做少数 + 保留扩展机制** | 中 | 先做常见的几个，其余按需增补 |

**倾向 C。** 需要用户给出**优先做哪几个**。

---

## 4.5 全量挂钩点清单（已扫描 mod 集实测）

> 扫描范围：`Mod/**/*.cs` 里所有继承 `Subsystem`（含 `Su*Subsystem`）的类
> 统计结果：**87 个 Subsystem 子类**，共使用 **20 种挂钩点**

### 4.5.1 一张必须实现的转发表

分发器要"一个实例驱动多个 Mod 逻辑"，**必须把这 20 种挂钩点全部转发**。
下表按实测使用频次排序（次数 = 使用该挂钩点的 mod 子类数）。

| # | 挂钩点 | 实测次数 | 是否 virtual | 分发器如何实现 |
|---|---|---|---|---|
| 1 | `IUpdateable.Update` | **61** | ❌ 非虚（引擎方法） | 显式接口实现 `void IUpdateable.Update(float)` |
| 2 | `OnBlockRemoved` | 33 | ✅ | `public override void` |
| 3 | `OnNeighborBlockChanged` | 31 | ✅ | `public override void` |
| 4 | `Load` | 30 | ✅ `protected internal` | `protected override void` |
| 5 | `OnBlockAdded` | 26 | ✅ | `public override void` |
| 6 | `OnPoll` | 18 | ✅（`IEditable`） | `public override void` |
| 7 | `OnBlockGenerated` | 14 | ✅ | `public override void` |
| 8 | `OnEditInventoryItem` | 12 | ✅ | `public override bool` |
| 9 | `OnEditBlock` | 11 | ✅ | `public override bool` |
| 10 | `OnBlockModified` | 11 | ✅ | `public override void` |
| 11 | `OnChunkDiscarding` | 11 | ✅ | `public override void` |
| 12 | `OnUse` | 7 | ✅ | `public override bool` |
| 13 | `OnAim` | 4 | ✅ | `public override bool` |
| 14 | `Save` | 3 | ✅ `protected internal` | `protected override void` |
| 15 | `OnCollide` | 3 | ✅ | `public override void` |
| 16 | `OnFiredAsProjectile` | 3 | ✅ | `public override void` |
| 17 | `Update`（具体类的普通方法） | 2 | ⚠️ 视基类而定 | 需按基类分别处理 |
| 18 | `OnExplosion` | 2 | ✅ | `public override void` |
| 19 | `OnInteract` | 1 | ✅ | `public override bool` |
| 20 | `OnEntityAdded` | 1 | ✅ `protected internal` | `protected internal override void` |
| — | `Dispose` | 0（实测未用） | ✅ `public virtual` | `public override void`（建议仍转发） |
| — | `OnEntityRemoved` | 0（实测未用） | ✅ `protected internal` | 同上（建议仍转发） |

### 4.5.2 `SubsystemBlockBehavior` 的完整虚方法表（21 个）

来源：`Survivalcraft/Game/SubsystemBlockBehavior.cs`

| 成员 | 签名 | 返回值语义 |
|---|---|---|
| `HandledBlocks` | `abstract int[] HandledBlocks { get; }` | **抽象**，分发器必须提供 |
| `OnChunkInitialized` | `void (TerrainChunk)` | — |
| `OnChunkDiscarding` | `void (TerrainChunk)` | — |
| `OnBlockGenerated` | `void (int value, int x, int y, int z, bool isLoaded)` | — |
| `OnBlockAdded` | `void (int value, int oldValue, int x, int y, int z)` | — |
| `OnBlockRemoved` | `void (int value, int newValue, int x, int y, int z)` | — |
| `OnBlockModified` | `void (int value, int oldValue, int x, int y, int z)` | — |
| `OnNeighborBlockChanged` | `void (int x, int y, int z, int nx, int ny, int nz)` | — |
| `OnUse` | `bool (Ray3, ComponentMiner)` | `true` = 已消费 |
| `OnInteract` | `bool (TerrainRaycastResult, ComponentMiner)` | `true` = 已消费 |
| `OnAim` | `bool (Ray3, ComponentMiner, AimState)` | `true` = 已消费 |
| `OnEditBlock` | `bool (int x, int y, int z, int value, ComponentPlayer)` | `true` = 已处理 |
| `OnEditInventoryItem` | `bool (IInventory, int slotIndex, ComponentPlayer)` | `true` = 已处理 |
| `OnItemPlaced` | `void (..., **ref** BlockPlacementData, int)` | ⚠️ **带 ref** |
| `OnItemHarvested` | `void (..., **ref** BlockDropValue, **ref** int)` | ⚠️ **带 ref** |
| `OnCollide` | `void (CellFace, float velocity, ComponentBody)` | — |
| `OnExplosion` | `void (int value, int x, int y, int z, float damage)` | — |
| `OnFiredAsProjectile` | `void (Projectile)` | — |
| `OnHitAsProjectile` | `bool (CellFace?, ComponentBody, WorldItem)` | `true` = 已命中 |
| `OnHitByProjectile` | `void (CellFace, WorldItem)` | — |
| `GetProcessInventoryItemCapacity` | `int (IInventory, int slotIndex, int value)` | 容量 |
| `ProcessInventoryItem` | `void (..., **out** int, **out** int)` | ⚠️ **带 out** |

### 4.5.3 分发器的三段分裂：`bool` / `void` / `ref`-`out`

转发语义按签名种类分三类，**必须分别定规则**：

| 类别 | 方法 | 多 Mod 如何合流 |
|---|---|---|
| **`void`**（纯粹通知） | `OnBlockAdded` / `OnBlockRemoved` / `OnNeighborBlockChanged` / `OnPoll` / `OnChunkDiscarding` / `OnCollide` / `OnExplosion` / `OnFiredAsProjectile` / `OnBlockGenerated` / `OnBlockModified` / `OnChunkInitialized` / `OnHitByProjectile` | ✅ **全部调用**，互不影响（最安全） |
| **`bool`**（消费语义） | `OnUse` / `OnInteract` / `OnAim` / `OnEditBlock` / `OnEditInventoryItem` / `OnHitAsProjectile` | ⚠️ **要定规则**：<br>· 短路（第一个 `true` 后停）？<br>· 还是全跑（忽略返回值）？ |
| **`ref` / `out`**（就地改写） | `OnItemPlaced`（`ref`）<br>`OnItemHarvested`（`ref`）<br>`ProcessInventoryItem`（`out`） | ⚠️ **最复杂**：<br>· `ref`：后执行者看到前者的修改（链式）<br>· `out`：必须由某个 Mod 定值，否则无值 |

### 4.5.4 关键发现：`IUpdateable.Update` 是最大挂点（61 处）

**为什么不用 `override`**：多数引擎子系统的 `Update` 是**非虚**的
（如 `SubsystemElectricity.Update` 是 `public void`，没有 `virtual`）。

Mod 的写法是**显式接口实现**：

```csharp
public sealed class SuSubsystemElectricity : SubsystemElectricity, IUpdateable
{
    void IUpdateable.Update(float dt)     // ← 显式接口，不是 override
    {
        ...
        base.Update(dt);                  // ← 手动调基类的非虚方法
    }
}
```

**为什么这样可行**：`SubsystemUpdate` 保存和调用的都是 **`IUpdateable` 接口引用**
（`SubsystemUpdate.cs:107-113` → `foreach (IUpdateable item in Project.FindSubsystems<IUpdateable>())`），
所以走接口虚分派，命中子类的显式实现。

**对分发器的要求**：

```csharp
// 分发器也要用显式接口实现
void IUpdateable.Update(float dt)
{
    base.Update(dt);                      // 原版逻辑（若基类有）
    foreach (var part in m_parts)
        part.Update(dt);                  // 所有 Mod 的 Update
}
```

⚠️ **注意**：显式接口实现**不覆写**基类的 `Update`。若基类是非虚 `public void Update(float)`，
通过具体类型调用仍走基类实现。分发器要确保**只用接口引用被驱动**（引擎正是这么做的）。

### 4.5.5 `Load` 只占 30/87 —— 不能只做 `Load`

`Load` 虽是生命周期入口（初始化自己的状态），但：

| 挂钩点 | 用途 | 不能替代 |
|---|---|---|
| `Load` | 拿 `Project`、`FindSubsystem`、初始化字段 | ❌ 不能做每帧/事件逻辑 |
| `Update` | 每帧逻辑 | ❌ 拿不到初始化时机 |
| `OnXxx` | 游戏事件响应 | ❌ 拿不到每帧时机 |

**结论**：三者缺一不可，分发器必须全部转发。

### 4.5.6 实测使用 `IUpdateable` 的 Mod 子类（部分）

| Mod | 类 | 基类 |
|---|---|---|
| `ScMultiplayer` | `SuSubsystemElectricity` | `SubsystemElectricity` |
| `ScMultiplayer` | `SuSubsystemCreatureSpawn` | `SubsystemCreatureSpawn` |
| `ScMultiplayer` | `SuSubsystemWeather` | `SubsystemWeather` |
| `ScMultiplayer` | `SuSubsystemPickables` | `SubsystemPickables` |
| `ScMultiplayer` | `SuSubsystemFireBlockBehavior` 等 | `Subsystem*BlockBehavior` |
| `CircuitAutoRouter` | `SubsystemCircuitRouter` | `SubsystemBlockBehavior` |
| `RainWithoutDawn` | `RWDSubsystemTimeOfDay` | `SubsystemTimeOfDay` |

> 完整清单可在实现时用同样的扫描方式重新生成。

---

## 5. 已知风险与限制

| # | 风险 | 说明 |
|---|---|---|
| R1 | **子系统继承链** | 分发器必须继承**具体**子系统（如 `SubsystemButtonBlockBehavior`），不是裸 `Subsystem`。不同子系统虚方法不同，无法用一套通用代码覆盖 |
| R2 | **`protected internal` 覆写** | 跨程序集只能用 `protected override`，不能用 `protected internal override`（C# 语言限制） |
| R3 | **`IUpdateable` 不是自动的** | 只有实现了 `IUpdateable` 的子系统才会每帧被调用。`SubsystemButtonBlockBehavior` 本身不实现，若要在 `Update` 里分派，分发器要自己加 `IUpdateable` |
| R4 | **加载顺序** | `Register` 必须在 `GameDatabase.GameDatabase` 事件内/前应用，否则 `Class` 已被读走 |
| R5 | **反射失败即崩** | `Class` 指向的类找不到 → 子系统创建失败 → **整个游戏启动失败**。分发器必须存在于**所有**端（含移动端） |
| R6 | **异常吞没** | `ModEventBus.TriggerEvent` 吞掉订阅者异常，只 `Console.WriteLine`。Mod 出错时**没有任何 Mod 层可见的报错**，排查困难 |
| R7 | **跨程序集类型传递** | 事件参数只能是 `object`。Mod 想读分发器的状态，要么反射，要么约定接口（需共享 DLL） |
| R8 | **性能** | 若分发器挂在高频方法（`OnNeighborBlockChanged`、`Update`），每次调用都走一遍事件总线（含 lock）。需评估开销 |

---

## 6. 与现有 Mod 的关系

### 6.1 会被影响的现有 Mod

| Mod | 现状 | 迁移后 |
|---|---|---|
| `Channel` | `Register("Game.SubsystemButtonBlockBehavior", "Channel.SubsystemChannelBlockBehavior")` | 改为订阅 `"Game.Subsystem.Update"`；不再碰 Class 参数 |
| `ConsoleMod` | `Register("Game.SubsystemGameWidgets", "ConsoleMod.ConsoleSubsystemGameWidgets")`（`HIGHEST`） | 同上（若该子系统也做分发器） |
| 其它 | 各自 `Register` 抢不同子系统 | 按需迁移 |

### 6.2 与 `SuEventBus` 的关系（待定）

`Mod/SuEventBus` 已经是可用的跨 Mod 事件中间件（类型安全）。
本文档按用户的 `ModEventBus.TriggerEvent` 走，但需明确：

- 两套并存？还是 `SuEventBus` 逐步废弃？
- 新的分发器事件是否也走 `SuEventBus`（更类型安全）？

---

## 7. 待确认清单

| # | 问题 | 选项 |
|---|---|---|
| **Q1** | 分发器放 SuAPI Core 还是独立 Mod？ | A. Core / B. 独立 |
| **Q2** | 原版逻辑是否保留？ | A. 先调 base / B. 完全替代 / C. Mod 可决定 |
| **Q3** | 数值冲突怎么合并？ | A. 后者覆盖 / B. 加算乘算 / C. Mod 自行协商 |
| **Q4** | 先做哪几个子系统？ | 需用户列出 |
| **Q5** | 事件粒度确认 | A. 单事件+操作名（用户原话）/ B. 每操作一事件 |
| **Q6** | `SuEventBus` 与 `ModEventBus` 的关系 | A. 并存 / B. 统一到其中一个 |
| **Q7** | 是否要"声明式能力"（如 Mod 声明"我要接管 index 142"） | 需要额外的注册表与冲突检测 |

---

## 8. 建议的实施顺序（确认后）

1. **Q1~Q7 拍板**（本文档更新）
2. **挑 1 个子系统做原型**（建议 `ButtonBlockBehavior`，因为 `Channel` 已经在用，迁移成本可见）
3. **验证 3 件事**：
   - 分发器能被引擎创建（Class 反射成功）
   - 两个 Mod 的订阅都执行
   - 原版行为按 Q2 的决定表现正确
4. **迁移 `Channel`** 作为第一个真实用户
5. 根据 Q4 扩大范围

---

## 9. 附：核实过的源码位置

| 事实 | 位置 |
|---|---|
| 事件触发点 | `EntitySystem/GameEntitySystem/GameDatabase.cs:30-40` |
| 事件返回值语义 | 同上（`item[0]==true` 时替换 `Database`） |
| `TriggerEvent` 实现 | `EntitySystem/SuAPI/ModEventBus.cs:56-104` |
| `Subsystem` 可覆写点 | `EntitySystem/GameEntitySystem/Subsystem.cs:16-38` |
| `IUpdateable` 定义 | `Survivalcraft/Game/IUpdateable.cs` |
| `UpdateOrder` 枚举 | `Survivalcraft/Game/UpdateOrder.cs` |
| `IUpdateable` 自动登记 | `Survivalcraft/Game/SubsystemUpdate.cs:107-111` |
| Class 参数结构 | `Pak/Database.xml:3406-3408`（`ButtonBlockBehavior` 示例） |
| 110 个子系统模板 | `Pak/Database.xml`（`grep -c "<SubsystemTemplate "`） |
| 现有 Class 替换示例 | `Mod/Channel/Plug/ChannelMod.cs:40-48`、`Mod/ConsoleMod/Plug/ConsoleMod.cs:20-38` |
| 备选事件中间件 | `Mod/SuEventBus/Func/SuEventHub.cs` |
| SuAPICore 入口 | `EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs:35`（`OnLoad(eventBus, modInjector)`） |
