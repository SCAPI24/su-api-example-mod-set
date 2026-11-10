> 历史方案提示（2026-11-10）：本文的自动 Register→Add、多实例 Chain.IsLast 路线已由主仓库 [Screen 生命周期与共享宿主设计](../../doc/screen-lifecycle-dispatch-design.md) 替代。当前附加使用独立扩展对象、统一调用执行器和真实 Host；本文保留为设计演变记录，不作为新 API 使用指南。

# SuAPI 多 Mod 共存方案（原「子系统分发器」计划）

> ⚠️ **最终结论（2026-11-09）：采用 §11「同名 MemberSubsystemTemplate + 清空原版 Class」**
>
> 该方案：引擎改 3 处约 6 行、SuAPI 新增 1 个文件约 120 行，
> 满足「ModA 执行 + ModB 执行 + 原版不执行」。
>
> **实施门禁**：§13 的 5 个细节全部确认后才允许写代码。

## 导航

| 章节 | 内容 | 状态 |
|---|---|---|
| §0-§2 | 问题与引擎机制（背景知识） | 📖 参考 |
| §3-§9 | 分发器方案 | ❌ **永久否定**（理由见 §12.1） |
| §10 | 新增独立 `MemberSubsystemTemplate` | 🔸 **降级为补充**（仅适用纯附加场景） |
| **§11** | **替换方案：同名 Member + 清空原版 Class** | ✅ **有效** |
| §12 | 已否决方案清单（勿再尝试） | 📌 永久记录 |
| §13 | 实施门禁 | 🚧 待办 |
| **§14** | **两套方案的改造对照（附加 A / 替换 B）** | 🎯 **后续讨论基线** |
| **§15** | **责任链（Chain）机制：最后一个实例调 base** | ✅ **核心机制** |

### 否决要点速查

| 方案 | 否决理由 |
|---|---|
| **分发器（§3-§9）** | 局部需求强制全局成本：ModA 只需改 1-2 个子系统，却要求 108 个分发器全建好；ModB 完全不需要也被绑定。**结构性缺陷，永久否定。** |
| **针对性钩子** | 仍是穷举：要攻击力加一个、要防御力再加一个 → "变成全片都是事件" |
| **Reflection.Emit 代理** | 属分发器变体，随分发器一并否决 |

### 两个方案的适用边界

| 需求 | 方案 | 原版是否执行 | 改造量 |
|---|---|---|---|
| **纯附加**（如攻击时记日志） | §10/§14 A `AddSubsystem` | ✅ 执行 | 引擎**零改动**，SuAPI ~160 行 |
| **替换**（如攻击力 1→2） | §11/§14 B `Claim` | ❌ **不执行** | 引擎 **3 处 ~6 行**，SuAPI ~120 行 |

> 两者都要（用户 2026-11-09 确认：我也有原版要工作的需求啊）。
> 详细改造点见 **§14**。

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

## 3.5 分发器方案细节（❌ 已废弃，仅存档）

> ⛔ **本节及 §3.5.1-§3.5.12 全部废弃** —— 分发器方案已被永久否定（理由见 §12.1）。
> 保留仅作为设计过程记录。**不要按本节实施。**

> 用户已确认：分发器命名为 `SuAPI.SubsystemTimeOfDay`，放在 **`Survivalcraft/SuAPI/`**，
> 编译进 **`Survivalcraft.dll`**（Mod 本来就引用这三个 DLL 之一，可直接访问）。
> Mod 通过 `eventBus.SubscribeEvent` 订阅，用 `args[0]` 拿分发器实例。

### 3.5.1 为什么放 `Survivalcraft.dll`

| 约束 | 说明 |
|---|---|
| `EntitySystem.dll` **不引用** `Survivalcraft.dll` | 放这里就无法继承 `Game.Subsystem*`（会循环依赖） |
| `SuAPICore.dll` 不在 Mod 引用的三件套里 | Mod 引用不到 |
| **`Survivalcraft.dll`** | ✅ Mod 必引；✅ 已引用 `EntitySystem.dll`；✅ `TypeCache.FindType` 能扫到 |

依赖方向（已核实）：

```
Engine.dll
    ↑
EntitySystem.dll        ← SuAPI 接口（IModParentField 等）
    ↑
Survivalcraft.dll       ← 分发器放这里 ✅ 无循环依赖
```

### 3.5.2 引擎现状（已核实）

`Survivalcraft/Game/SubsystemTimeOfDay.cs`：

```csharp
public class SubsystemTimeOfDay : Subsystem        // ← 不实现 IUpdateable
{
    // ── public 字段 ──
    public bool TimeOfDayEnabled = true;
    public const float DayDuration = 1200f;

    // ── public 只读属性 ──
    public float DawnStart { get; private set; }
    public float DayStart  { get; private set; }
    public float DuskStart { get; private set; }
    public float NightStart { get; private set; }
    public float DayInterval / DuskInterval / NightInterval / DawnInterval { get; }
    public float Midday / Middusk / Midnight / Middawn { get; }
    public float TimeOfDay { get; }          // 当前时刻（按 TimeOfDayMode 算）
    public double Day { get; }               // 当前天数

    // ── public 可读写 ──
    public double TimeOfDayOffset { get; set; }    // ← 改这个可跳时间

    // ── public 方法 ──
    public double CalculateDay(double totalElapsedGameTime)
    public float  CalculateTimeOfDay(double totalElapsedGameTime)

    // ── 仅有的 2 个可覆写点 ──
    protected override void Load(ValuesDictionary valuesDictionary)
    protected override void Save(ValuesDictionary valuesDictionary)

    // ── private（Mod 需 IModParentField 才能访问）──
    private SubsystemGameInfo m_subsystemGameInfo;
    private SubsystemSeasons  m_subsystemSeasons;
    private void UpdateStarts();
}
```

**关键结论**：

| 项 | 值 |
|---|---|
| 可覆写方法数 | **只有 2 个**（`Load` / `Save`） |
| 是否实现 `IUpdateable` | ❌ **不实现**（所以分发器要自己加） |
| 是否有 `Update` 方法 | ❌ **没有**（所以分发器不需要调 `base.Update`） |
| `Dependencies` | `Seasons`（`Pak/Database.xml:3683`） |

> 这个类**没有业务虚方法可挂钩**，所以分发器的职责很轻：
> 只转发 `Load` / `Save` 两个时刻，外加新增一个每帧 `Update`。

### 3.5.3 文件布局

```
Survivalcraft/
└── SuAPI/                                    ← 新建目录
    ├── SubsystemTimeOfDay.cs                 → namespace SuAPI
    └── DispatchEventArgs.cs                  → namespace SuAPI
```

> ⚠️ 目录名 `SuAPI` 与 `EntitySystem/SuAPI/` 同名，但**是两个不同程序集**。
> 命名空间 `SuAPI` 跨程序集是合法的，Mod 引用时编译器会合并。

### 3.5.4 分发器实现

分发器需要一层公共的分发逻辑，建议抽成辅助类，让每个分发器代码保持精简。

#### 公共分发辅助

```csharp
// Survivalcraft/SuAPI/DispatchHelper.cs
namespace SuAPI
{
    internal static class DispatchHelper
    {
        /// <summary>
        /// 分发事件：args = [DispatchEventArgs, 宿主实例]。
        ///
        /// 为什么宿主单独放 args[1]：DispatchEventArgs 是非泛型类型（所有子系统共用），
        /// 宿主的具体类型由泛型订阅扩展 SubscribeDispatch&lt;T&gt; 在 Mod 侧还原。
        ///
        /// 为什么需要这层包装：ModEventBus.TriggerEvent 内部吞掉订阅者异常，
        /// 只写 Console.WriteLine（见 ModEventBus.cs:88-100），Game.log 里看不到。
        /// 这里检查它回填到结果里的 "Error: ..." 并转成 Log.Error。
        /// </summary>
        internal static void Fire(string eventName, DispatchEventArgs e, object host)
        {
            object[][] results;
            try
            {
                results = ModManager.Instance?.ModEventBus?.TriggerEvent(
                    eventName, new object[] { e, host });
            }
            catch (Exception ex)
            {
                Log.Error("[SuAPI][dispatch] " + eventName + " 分发失败：" + ex);
                return;
            }

            if (results == null) return;
            for (int i = 0; i < results.Length; i++)
            {
                object[] r = results[i];
                if (r == null || r.Length == 0) continue;
                if (r[0] is string s &&
                    s.StartsWith("Error:", StringComparison.Ordinal))
                {
                    Log.Error("[SuAPI][dispatch] " + eventName +
                        " 有订阅者抛异常 → " + s);
                }
            }
        }
    }
}
```

#### `SubsystemTimeOfDay` 分发器

```csharp
// Survivalcraft/SuAPI/SubsystemTimeOfDay.cs
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace SuAPI
{
    /// <summary>
    /// 替换 Game.SubsystemTimeOfDay 的分发器。
    ///
    /// 职责：占住数据库 Class 参数位置，把引擎的调用时刻转发成事件。
    /// 不预设任何 Mod 需求 —— Mod 需要什么，从事件参数里的宿主上自己取。
    /// </summary>
    public class SubsystemTimeOfDay : Game.SubsystemTimeOfDay, IUpdateable
    {
        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        // ── 生命周期（总是调 base，不允许截住）──────────────

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);       // ← 必须：父类在此抓 GameInfo/Seasons
            var e = NewArgs(Operations.Load, valuesDictionary,
                            args => base.Load((ValuesDictionary)args[0]));
            DispatchHelper.Fire("SuAPI.SubsystemTimeOfDay", e, this);
        }

        protected override void Save(ValuesDictionary valuesDictionary)
        {
            var e = NewArgs(Operations.Save, valuesDictionary,
                            args => base.Save((ValuesDictionary)args[0]));
            DispatchHelper.Fire("SuAPI.SubsystemTimeOfDay", e, this);

            if (e.CallBase && e.BaseCallCount == 0)
                base.Save(valuesDictionary);   // ← 父类在此写 TimeOfDayOffset
        }

        // ── 每帧（允许 Mod 截住 / 接替）────────────────────

        public void Update(float dt)
        {
            var e = NewArgs(Operations.Update, dt,
                            args => base.Update((float)args[0]));
            DispatchHelper.Fire("SuAPI.SubsystemTimeOfDay", e, this);

            // 没人调过 base 且允许 → 兜底调一次（默认路径）
            if (e.CallBase && e.BaseCallCount == 0)
                base.Update(dt);
        }

        private DispatchEventArgs NewArgs(string operation, object payload,
                                          Action<object[]> baseInvoker) =>
            new DispatchEventArgs
            {
                Operation = operation,
                Args = new[] { payload },
                BaseInvoker = baseInvoker
            };
    }
}
```

#### 关键设计说明

| # | 设计点 | 说明 |
|---|---|---|
| 1 | **事件 `args` 布局** | `args[0]` = `DispatchEventArgs`，`args[1]` = 宿主实例 |
| 2 | **`Load` 无条件调 `base`** | 生命周期方法不接受「截住」—— 父类不初始化会导致后续 NRE |
| 3 | **`Save` / `Update` 有 `CallBase` 判断** | Mod 可截住 |
| 4 | **`BaseInvoker` 注入** | Mod 用 `e.InvokeBase(...)` 自己调 base，自动计数 |
| 5 | **兜底规则** | `if (e.CallBase && e.BaseCallCount == 0)` —— 没人调过才由分发器调 |
| 6 | **异常进 `Game.log`** | `DispatchHelper.Fire` 检查 `TriggerEvent` 的结果 |

#### 为什么 `Load` 不支持截住

```csharp
protected override void Load(ValuesDictionary vd)
{
    base.Load(vd);    // ← 无条件
    ...
}
```

如果允许 Mod 截住 `Load`，父类的 `m_subsystemGameInfo` / `m_subsystemSeasons` 不会被赋值 →
之后任何访问 `TimeOfDay` 的代码都会 `NullReferenceException`。

**结论**：`Load` / `Save` 这类生命周期方法**总是调 base**；只有业务方法（`Update` / `OnXxx`）开放 `CallBase`。

### 3.5.5 事件参数类

`DispatchEventArgs` **不持有 Host** —— 宿主实例作为 `args[1]` 单独传，
由泛型订阅扩展转换成强类型再交给 Mod。这样 `DispatchEventArgs` 保持非泛型，
任何子系统都能复用同一个类型。

```csharp
// Survivalcraft/SuAPI/DispatchEventArgs.cs
namespace SuAPI
{
    /// <summary>
    /// 子系统分发器的事件参数。所有字段都有名字，Mod 不需要记 args 位置。
    /// 同一个实例会依次传给所有订阅者（按 EventPriority 排序，HIGHEST 先、LOWEST 后）。
    ///
    /// 宿主实例**不在这里** —— 它是事件的 args[1]（见 DispatchHelper.Fire），
    /// 由泛型订阅扩展 SubscribeDispatch&lt;T&gt; 转成强类型后交给 Mod。
    /// </summary>
    public sealed class DispatchEventArgs
    {
        /// <summary>操作名，用 <see cref="Operations"/> 常量比较。</summary>
        public string Operation { get; init; }

        /// <summary>原方法的参数，按声明顺序（如 Update 的 dt、OnEditBlock 的 x/y/z/value/player）。</summary>
        public object[] Args { get; init; }

        /// <summary>
        /// 是否允许分发器在事件分发完毕后调用原版（base）逻辑。
        ///
        /// · 默认 <c>true</c> = 不修改就保持原有调用形式；
        /// · 多个 Mod 可各自修改，**后修改者优先**（由 EventPriority 决定谁后执行）；
        /// · 分发器在**所有订阅者执行完之后**才读取此值决定是否调 base。
        /// </summary>
        public bool CallBase { get; set; } = true;

        /// <summary>
        /// bool 返回值方法的结果槽（OnEditBlock / OnUse / OnInteract 等）。
        ///
        /// 与 CallBase 的分工：
        /// · <c>CallBase</c> 决定"跑不跑原版代码"；
        /// · <c>Handled</c> 决定"这次操作返回给引擎什么值"。
        /// 典型用法：截住原版（CallBase=false）时同时 Handled=true，
        /// 告诉引擎"我已经处理了，别再弹默认界面"。
        /// </summary>
        public bool Handled { get; set; }

        /// <summary>
        /// 分发器或 Mod 调用 base 的累计次数。用于"接替实现"场景：
        /// Mod 自己通过 <see cref="InvokeBase"/> 调用 base 后，分发器不再兜底调用，
        /// 避免原版逻辑被执行两遍。
        /// </summary>
        public int BaseCallCount { get; internal set; }

        /// <summary>分发器的 base 调用委托（由分发器注入，Mod 不直接设置）。</summary>
        internal Action<object[]> BaseInvoker { get; init; }

        /// <summary>
        /// 让 Mod 自己调用一次原版逻辑（自动计数）。
        ///
        /// 适用「接替实现」场景 —— 例如需要控制 base 被调用的次数与参数：
        /// <code>
        /// for (int i = 0; i < steps; i++)
        /// {
        ///     PrepareCircuitStep(...);
        ///     e.InvokeBase(CircuitStepDuration);   // ← 自己控制每次的 dt
        ///     CompleteCircuitStep(...);
        /// }
        /// </code>
        /// 一旦调用过，分发器不会再自动调用 base（见 BaseCallCount）。
        /// </summary>
        public void InvokeBase(params object[] args)
        {
            BaseInvoker?.Invoke(args);
            BaseCallCount++;
        }
    }

    /// <summary>操作名常量：拼错会编译报错，而不是静默失效。</summary>
    public static class Operations
    {
        // ── 生命周期 ──
        public const string Load    = "Load";
        public const string Save    = "Save";
        public const string Dispose = "Dispose";

        // ── 每帧 ──
        public const string Update = "Update";

        // ── 游戏事件（SubsystemBlockBehavior 系）──
        public const string OnBlockAdded           = "OnBlockAdded";
        public const string OnBlockRemoved         = "OnBlockRemoved";
        public const string OnBlockModified        = "OnBlockModified";
        public const string OnBlockGenerated       = "OnBlockGenerated";
        public const string OnNeighborBlockChanged = "OnNeighborBlockChanged";
        public const string OnChunkInitialized     = "OnChunkInitialized";
        public const string OnChunkDiscarding      = "OnChunkDiscarding";
        public const string OnPoll                 = "OnPoll";
        public const string OnUse                  = "OnUse";
        public const string OnInteract             = "OnInteract";
        public const string OnAim                  = "OnAim";
        public const string OnEditBlock            = "OnEditBlock";
        public const string OnEditInventoryItem    = "OnEditInventoryItem";
        public const string OnItemPlaced           = "OnItemPlaced";
        public const string OnItemHarvested        = "OnItemHarvested";
        public const string OnCollide              = "OnCollide";
        public const string OnExplosion            = "OnExplosion";
        public const string OnFiredAsProjectile    = "OnFiredAsProjectile";
        public const string OnHitAsProjectile      = "OnHitAsProjectile";
        public const string OnHitByProjectile      = "OnHitByProjectile";
    }
}
```

#### 事件的 `args` 布局

```csharp
TriggerEvent(eventName, new object[] { dispatchEventArgs, host });
//                     ↑                              ↑
//                     args[0]                        args[1]
//                 DispatchEventArgs              宿主子系统实例
```

| 位置 | 内容 | 谁用 |
|---|---|---|
| `args[0]` | `DispatchEventArgs`（Operation / Args / CallBase / Handled） | 所有 Mod |
| `args[1]` | 宿主实例（`SuAPI.SubsystemTimeOfDay` 等） | 泛型订阅扩展转成 `TSubsystem` 后交给 Mod |

#### 泛型订阅扩展（把 `args[1]` 转成强类型）

```csharp
// Survivalcraft/SuAPI/DispatchEventExtensions.cs
namespace SuAPI
{
    public static class DispatchEventExtensions
    {
        /// <summary>
        /// 订阅子系统分发器事件，回调直接拿到强类型宿主。
        /// 事件名自动用宿主类的全名（"SuAPI.SubsystemTimeOfDay"），不会拼错。
        /// </summary>
        public static EventSubscriptionToken SubscribeDispatch<TSubsystem>(
            this IModEventBus bus,
            Action<TSubsystem, DispatchEventArgs> handler,
            EventPriority priority = EventPriority.NORMAL)
            where TSubsystem : Game.Subsystem
        {
            string eventName = typeof(TSubsystem).FullName;
            return bus.SubscribeEvent(eventName, args =>
            {
                if (args != null && args.Length >= 2 &&
                    args[0] is DispatchEventArgs e &&
                    args[1] is TSubsystem host)
                {
                    handler(host, e);
                }
                return null;       // 分发器事件不需要返回值
            }, priority);
        }
    }
}
```

#### `CallBase` / `Handled` / `BaseCallCount` 的分工

| 字段 | 回答的问题 | 典型用法 |
|---|---|---|
| **`CallBase`** | 分发器要不要自动调 `base`？ | ModA 设 `false` 截住；ModB 设 `true` 放行（后者优先） |
| **`Handled`** | `bool` 方法返回值给引擎什么？ | 截住时设 `true`，避免引擎再弹原版界面 |
| **`BaseCallCount`** | 分发器是否会重复调 `base`？ | Mod 用 `InvokeBase` 调过后自动 > 0，分发器不再兜底 |

#### 多 Mod 修改 `CallBase` 的决策表

| 场景 | 最终 `CallBase` | 分发器行为 |
|---|---|---|
| 无 Mod 修改 | `true`（默认） | 调 `base` ✅ |
| ModA（HIGHEST）设 `false` | `false` | **不调** `base` |
| ModA 设 `false`，ModB（LOWEST）设 `true` | `true`（**后者优先**） | 调 `base` ✅ |
| ModA 设 `true`，ModB 设 `false` | `false`（后者优先） | 不调 `base` |
| ModA 设 `false`，ModB 不修改 | `false` | 不调 `base` |
| ModA 调过 `InvokeBase` | `false` 或不管 | 分发器看 `BaseCallCount > 0` → **不再兜底** |

> 执行顺序由 `ModEventBus.TriggerEvent` 的 `OrderBy(Priority).ThenBy(Order)` 决定
> （`ModEventBus.cs:67`），HIGHEST 最先执行、LOWEST 最后执行。

#### `Handled` 与 `CallBase` 的组合效果

| `CallBase` | `Handled` | `bool` 方法返回给引擎 | 效果 |
|---|---|---|---|
| `true` | `false` | `base.OnXxx(...)` 的结果 | 走原版 |
| **`false`** | **`true`** | `true` | **引擎认为已处理**（弹了 Mod 自己的界面） |
| `false` | `false` | `false` | 引擎以为没处理（通常会再弹原版，**少见**） |
| `true` | `true` | `base` 的结果 | 原版照跑，`Handled` 被忽略 |

#### 「接替实现」示例（`ScMultiplayer` 控制电路步进）

```csharp
// 分发器（SubsystemElectricity）—— base 调用委托注入
void IUpdateable.Update(float dt)
{
    var e = new DispatchEventArgs
    {
        Operation = Operations.Update,
        Args = new object[] { dt },
        BaseInvoker = args => base.Update((float)args[0])
    };
    DispatchHelper.Fire("SuAPI.SubsystemElectricity", e, this);

    // 没 Mod 调过 base 且允许 → 兜底调一次
    if (e.CallBase && e.BaseCallCount == 0)
        base.Update(dt);
}
```

```csharp
// Mod 侧：接替实现，自己控制步进
eventBus.SubscribeDispatch<SuAPI.SubsystemElectricity>((host, e) =>
{
    if (e.Operation != Operations.Update) return;
    if (!NeedTakeover) return;              // 不接管 → 分发器兜底调 base

    e.CallBase = false;                     // 明确表示"我自己来"
    for (int i = 0; i < steps; i++)
    {
        PrepareCircuitStep(...);
        e.InvokeBase(CircuitStepDuration);  // ← 自动计数，分发器不会重复调
        CompleteCircuitStep(...);
    }
});
```

### 3.5.6 Mod 怎么用（五种典型写法）

#### 写法 1：读当前时间（最常见的只读场景）

```csharp
public class MyMod : IMod
{
    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeDispatch<SuAPI.SubsystemTimeOfDay>((host, e) =>
        {
            if (e.Operation != SuAPI.Operations.Update) return;

            // host 已经是 SuAPI.SubsystemTimeOfDay 强类型，直接 . 访问
            float now = host.TimeOfDay;      // 当前时刻 0..1
            float midnight = host.Midnight;  // 午夜时刻
            if (now > 0.8f) { /* 天黑了，做点什么 */ }
        });
    }
}
```

#### 写法 2：强制跳时间（RainWithoutDawn 的真实逻辑）

```csharp
public class MidnightMod : IMod
{
    private SubsystemGameInfo m_gameInfo;

    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        IModParentField parentField = modInjector.ModParentField;

        eventBus.SubscribeDispatch<SuAPI.SubsystemTimeOfDay>((host, e) =>
        {
            // ① Load 时刻：用 SuAPI 工具拿父类私有字段
            if (e.Operation == SuAPI.Operations.Load)
            {
                m_gameInfo = parentField.GetParentField<SubsystemGameInfo>(
                    host, "m_subsystemGameInfo");
                return;
            }

            // ② Update 时刻：强制跳到午夜
            if (e.Operation != SuAPI.Operations.Update) return;
            if (m_gameInfo == null) return;          // 用户口径：自己判空

            if (IsNotMidnight(host))
            {
                float timeToMidnight = IntervalUtils.Interval(
                    host.CalculateTimeOfDay(m_gameInfo.TotalElapsedGameTime),
                    host.Midnight - 0.09f);
                host.TimeOfDayOffset += timeToMidnight;   // public set，直接改
            }
        });
    }

    private static bool IsNotMidnight(Game.SubsystemTimeOfDay host) =>
        !(IntervalUtils.IsBetween(host.TimeOfDay, host.Midnight - 0.1f, host.Midnight)
       || IntervalUtils.IsBetween(host.TimeOfDay, 0f, 0.1f));
}
```

#### 写法 3：改天气（自己找 `SubsystemWeather`）

```csharp
public class WeatherMod : IMod
{
    private SubsystemWeather m_weather;

    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeDispatch<SuAPI.SubsystemTimeOfDay>((host, e) =>
        {
            if (e.Operation != SuAPI.Operations.Update) return;

            // 迟到的子系统：每帧补拿一次
            if (m_weather == null)
            {
                m_weather = host.Project.FindSubsystem<SubsystemWeather>(false);
                if (m_weather == null) return;
            }

            if (!m_weather.IsPrecipitationStarted) m_weather.ManualPrecipitationStart();
            if (!m_weather.IsFogStarted)           m_weather.ManualFogStart();
        });
    }
}
```

#### 写法 4：截住 `Update`（客户端不跑原版）

```csharp
public class ClientGuardMod : IMod
{
    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeDispatch<SuAPI.SubsystemTimeOfDay>((host, e) =>
        {
            if (e.Operation != SuAPI.Operations.Update) return;

            // 客户端不允许推进原版时间 → 截住
            if (IsNetworkClient())
                e.CallBase = false;
        }, EventPriority.HIGHEST);   // ← 先执行，后面的 Mod 可以再改回来
    }
}
```

#### 写法 5：接替实现（自己控制 base 调用次数）

```csharp
public class TakeoverMod : IMod
{
    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeDispatch<SuAPI.SubsystemTimeOfDay>((host, e) =>
        {
            if (e.Operation != SuAPI.Operations.Update) return;
            if (!NeedTakeover()) return;          // 不接管 → 分发器兜底调 base

            e.CallBase = false;                   // 明确表示"我自己来"
            for (int i = 0; i < StepCount; i++)
            {
                PrepareStep(i);
                e.InvokeBase(e.Args[0]);          // ← 自己调 base，自动计数
                CompleteStep(i);
            }
        }, EventPriority.HIGHEST);
    }
}
```

### 3.5.7 访问状态的三条路

| 想拿的东西 | 怎么拿 |
|---|---|
| **public 成员**（`TimeOfDay` / `Midnight` / `TimeOfDayOffset` ...） | ✅ `host.TimeOfDay` 直接读（`host` 来自 `SubscribeDispatch<T>`）|
| **分发器自己新增的成员** | ✅ `host` 本身就是 `SuAPI.SubsystemTimeOfDay`，直接点 |
| **父类 private 字段**（`m_subsystemGameInfo` / `m_subsystemSeasons`） | ✅ `parentField.GetParentField<T>(host, "m_xxx")` |

**不需要**为访问状态新造工具 —— `IModParentField` 已在 `EntitySystem.dll` 里。

### 3.5.8 执行流程

```
Pak/Database.xml
  <SubsystemTemplate Name="TimeOfDay">
    <Parameter Name="Class" Value="Game.SubsystemTimeOfDay" />
                    ↓ SuAPI 在 GameDatabase 事件里替换（唯一一次）
    <Parameter Name="Class" Value="SuAPI.SubsystemTimeOfDay" />

引擎创建 SuAPI.SubsystemTimeOfDay（唯一实例）
    │
    ├── Load(vd)
    │     ├── base.Load(vd)                     ← 父类：抓 GameInfo/Seasons、读 TimeOfDayOffset
    │     └── TriggerEvent("SuAPI.SubsystemTimeOfDay", [DispatchEventArgs{Load, vd}])
    │             ├── ModA（判 Operation==Load）→ 拿 Weather / 拿私有字段
    │             └── ModB（判 Operation==Load）→ 拿 GameInfo
    │
    ├── Save(vd)
    │     └── TriggerEvent(..., [DispatchEventArgs{Save, vd}])
    │
    └── 每帧（SubsystemUpdate 通过 IUpdateable 驱动）
          └── TriggerEvent(..., [DispatchEventArgs{Update, dt}])
                  ├── ModA → 强制下雨/起雾
                  └── ModB → 强制跳到午夜
```

### 3.5.9 这个方案验证了什么

| # | 验证点 | 结论 |
|---|---|---|
| 1 | SuAPI 只占位、不预设 Mod 需求 | ✅ 分发器不含任何业务逻辑 |
| 2 | Mod 自己拿需要的资源 | ✅ `FindSubsystem` / `IModParentField` |
| 3 | 两个 Mod 的 `Load` / `Update` 都被调用 | ✅ `TriggerEvent` 多订阅者天然满足 |
| 4 | Mod 能直接引用分发器类型 | ✅ 在 `Survivalcraft.dll`（三件套之一） |

### 3.5.10 已知限制

| # | 限制 | 说明 |
|---|---|---|
| 1 | **`Load` / `Save` 不能被外部调用** | 引擎内部调，Mod 只能通过事件响应，不能主动触发 |
| 2 | **`Update` 是分发器新增的** | 父类没这个方法，所以 `UpdateOrder` 由分发器决定（当前 `Default`） |
| 3 | **`Load` 时机不保证其它子系统已就绪** | 用户口径：Mod 自己 `if (x != null)` 判空，SuAPI 不保证顺序 |
| 4 | **`TriggerEvent` 吞异常** | Mod 回调出错时只有 `Console.WriteLine`，**不进 `Game.log`** → 实验阶段自己 try/catch |
| 5 | **`args` 是共享数组** | 但 `DispatchEventArgs` 字段是 `init` 只读，`Handled` 可写（有意为之） |
| 6 | **操作名拼错静默失效** | 用 `SuAPI.Operations.Update` 常量，编译期检查 |

### 3.5.11 后续推广时的差异

`SubsystemTimeOfDay` 只有 2 个可覆写方法，是最简单的案例。
推广到 `SubsystemBlockBehavior` 时（21 个虚方法）：

| 差异点 | `TimeOfDay` | `BlockBehavior` 系 |
|---|---|---|
| 可覆写方法 | 2 | **21** |
| 需不需要 `IUpdateable` | ✅ 需要（父类没有 Update） | 视子类而定 |
| `bool` 返回值语义 | 无 | 6 个方法有（需定"谁接管"规则） |
| `ref` / `out` 参数 | 无 | 3 个方法有（`object[]` 无法承载原址） |
| `HandledBlocks` | 无 | **抽象成员，分发器难办**（见 §5 R9） |

---

### 3.5.12 抢占注册机制（2026-11-09 定：`NORMAL` + 让位）

#### 用户口径

> "优先级用 NORMAL，SuAPI 是「让位」"

两层含义：

| # | 要求 |
|---|---|
| 1 | `GameDatabase.GameDatabase` 订阅用 **`NORMAL`** —— 既不最先、也不最后 |
| 2 | 如果对应位置已被其它 Mod 抢占，**SuAPI 不覆盖、不报错，直接跳过** |

#### 关键机制（必须理解的陷阱）

`ModInjector.Apply` 会**一次性应用 `_injectionRecords` 里的所有记录**，
不管是谁调用的 `Apply`：

```csharp
// ModInjector.cs:36 —— 全局共享列表，所有 Mod 的 Register 都进这里
private readonly List<InjectionRecord> _injectionRecords = new List<InjectionRecord>();

// ModInjector.cs:145-152 —— 按 List 顺序（= Register 顺序）逐个尝试
private void ProcessParameter(DatabaseObject param, string currentValue)
{
    foreach (var record in _injectionRecords)
    {
        if (record.Applied) continue;
        if (currentValue != record.OriginalClassName) continue;   // ← 精确匹配
        param.Value = record.NewClassName;
        record.Applied = true;
    }
}
```

**推论**：`EventPriority` 只决定「谁调用 `Apply`」，**不决定「谁的记录先被应用」**。
记录的先后由 **`Register` 的时机**决定（`SuAPICore` 在 `Program.cs:82` 最先加载 → 最先 `Register`）。

#### 因此：单靠 `NORMAL` 无法实现「让位」

若在 `SuAPICore.OnLoad` 里立即 `Register`，则 SuAPI 的记录永远排最前，
任何一次 `Apply` 都会优先命中它 → **SuAPI 必胜，无法让位**。

#### 正确做法：延后 `Register` 到检查之后

```csharp
// EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs 的 OnLoad

private static readonly (string Original, string Dispatch, string ClassGuid)[] s_dispatchTargets =
{
    ("Game.SubsystemTimeOfDay", "SuAPI.SubsystemTimeOfDay",
     "1e884b27-fc1d-486a-bd0e-518e554b9734"),
    // 后续推广时在这里追加
};

public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    m_eventBus = eventBus;

    // ── 分发器抢占：NORMAL + 让位 ──
    eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
    {
        if (args == null || args.Length == 0 || !(args[0] is Database database))
            return new object[] { false, null };

        var parameterType = database.FindDatabaseObjectType("Parameter", false);
        if (parameterType == null) return new object[] { false, database };

        foreach (var (original, dispatch, guid) in s_dispatchTargets)
        {
            var param = database.FindDatabaseObject(new Guid(guid), parameterType, false);
            if (param == null) continue;

            string current = param.Value as string;

            // 已被其它 Mod 改过 → 让位，什么都不做
            if (current != original)
            {
                Log.Information("[SuAPI][dispatch] " + original +
                    " 的 Class 已被其它 Mod 占用（当前 = " + current + "），SuAPI 跳过");
                continue;
            }

            // 位置还在 → 现在注册并应用（此时 Register，保证排在其它 Mod 之后）
            modInjector.Register(original, dispatch);
        }

        // Apply 会把本轮新注册的记录一并应用（其记录的 modName 标记为 "SuAPI"）
        modInjector.Apply(database, "SuAPI");
        return new object[] { false, database };
    }, EventPriority.NORMAL);

    // ...原有逻辑（Linux/Windows 窗口、内置 Mod 加载、存档修复）不变...
}
```

#### 为什么这样能实现「让位」

| 场景 | `param.Value` | SuAPI 行为 |
|---|---|---|
| 无人抢占 | `Game.SubsystemTimeOfDay` | ✅ `Register` → `Apply` → 安装分发器 |
| 别的 Mod（`HIGHEST`）先改 | `Other.MyTimeOfDay` | ❌ **跳过**，不 `Register`，不覆盖 |
| 别的 Mod（`LOWEST`）后改 | 先被 SuAPI 装上了 | ⚠️ 分发器已生效，对方改不动（见下） |

**注意第 3 行**：`NORMAL` 只在「比它早执行的 Mod」面前让位。
比它晚执行的 Mod（`LOW` / `LOWEST`）拿不到位置 —— 这是 `NORMAL` 优先级的必然结果，
用户已确认接受。

#### 与现有 Mod 的共存（实测优先级分布）

| Mod | `GameDatabase` 优先级 | 相对 SuAPI |
|---|---|---|
| `ScMultiplayer` | `HIGHEST` | **先于 SuAPI** → 可抢占 |
| `SuMultiplayer` | `HIGHEST` | **先于 SuAPI** → 可抢占 |
| `PlayerAiMod` | `HIGHEST` | **先于 SuAPI** → 可抢占 |
| **`SuAPICore`（新增）** | **`NORMAL`** | — |
| `Channel` | `NORMAL` | 同优先级，按 `Order`（订阅先后） |
| `MemoryBankDrawMod` | `LOWEST` | 后于 SuAPI |

实测（`Mod/` 全仓）：`HIGHEST` 16 处、`NORMAL` 1 处、`LOWEST` 2 处。

#### ⚠️ 让位的代价：该子系统的多 Mod 共存能力消失

如果某子系统被其它 Mod 抢占，则：

| 后果 | 说明 |
|---|---|
| SuAPI 分发器**不会安装** | `Class` 保持对方的值 |
| 所有 `SubscribeDispatch<T>` 的回调**永远不触发** | 事件从不发出 |
| 已写好的 Mod **静默失效** | 只能靠上面那条 `Log.Information` 排查 |

**这是设计取舍**：SuAPI 不跟单 Mod 争独占权，代价是失去该子系统的协调能力。

#### 为什么用 `Log.Information` 而不是 `Log.Warning`

用户口径是「SuAPI 就不管」—— 不是故障，是**设计内行为**。
用 `Information` 记录事实即可，避免把正常让位报成警告噪音。


## 4. 关键设计决策（已定，详见 §7.1）

> 本节原为待定清单，2026-11-09 已全部拍板。决定摘要如下，
> 完整说明见 **§3.5（方案细节）** 与 **§7.1（已定清单 D1-D12）**。

| # | 决策点 | 结论 |
|---|---|---|
| **D1** | 分发器放哪 | `Survivalcraft/SuAPI/` → 编译进 `Survivalcraft.dll` |
| **D2** | 类名 | `SuAPI.SubsystemTimeOfDay`（与原类同名） |
| **D3** | 事件粒度 | **单事件 + `Operation` 字段**（用户原本的选择） |
| **D4** | 事件参数 | 强类型 `DispatchEventArgs`，`args[0]`=它 / `args[1]`=宿主 |
| **D5** | 原版逻辑 | `CallBase` 开关（默认 true，后改者优先）+ `Handled` + `InvokeBase` |
| **D6** | 私有成员 | 用已有 `IModParentField`，不新造 |
| **D7** | 调用次数 | 用已有 `IModParentMethod` + `e.InvokeBase(...)` 自动计数 |
| **D8** | Mod 订阅写法 | `eventBus.SubscribeDispatch<T>((host, e) => {...})` |
| **D9** | 注册优先级 | `GameDatabase.GameDatabase` 用 **`NORMAL`** |
| **D10** | 位置被抢 | **让位**：不 Register、不覆盖，写 `Log.Information` |
| **D11** | 异常 | `TriggerEvent` 吞掉的异常转 `Log.Error` |
| **D12** | args 布局 | `args[0]`=`DispatchEventArgs`，`args[1]`=宿主实例 |

---

## 4.6 组件分发（2026-11-09 定：`host` 用 `object`，组件也做分发）

### 4.6.1 为什么组件也要分发

引擎里有两类「按数据库 `Class` 参数实例化」的对象，**都只能一个实现生效**：

| 类型 | 基类 | 在数据库里的位置 | 例子 |
|---|---|---|---|
| **子系统** | `Game.Subsystem` | `SubsystemTemplate` | `SubsystemTimeOfDay` |
| **组件** | `Game.Component` | `ComponentTemplate` + `MemberComponentTemplate` | `ComponentVitalStats` |

### 4.6.2 `Fire` 的 `host` 参数改成 `object`

```csharp
// Survivalcraft/SuAPI/DispatchHelper.cs
namespace SuAPI
{
    internal static class DispatchHelper
    {
        /// <summary>
        /// 分发事件：args = [DispatchEventArgs, 宿主实例]。
        ///
        /// host 用 object 而不是 Game.Subsystem：
        ///   子系统分发器传 Game.Subsystem 实例；
        ///   组件分发器传 Game.Component 实例。
        /// Subsystem 与 Component 没有公共基类（都直接派生自 object），
        /// 所以只能放到 object 这一层。
        /// </summary>
        internal static void Fire(string eventName, DispatchEventArgs e, object host)
        {
            object[][] results;
            try
            {
                results = ModManager.Instance?.ModEventBus?.TriggerEvent(
                    eventName, new object[] { e, host });
            }
            catch (Exception ex)
            {
                Log.Error("[SuAPI][dispatch] " + eventName + " 分发失败：" + ex);
                return;
            }

            if (results == null) return;
            for (int i = 0; i < results.Length; i++)
            {
                object[] r = results[i];
                if (r == null || r.Length == 0) continue;
                if (r[0] is string s &&
                    s.StartsWith("Error:", StringComparison.Ordinal))
                {
                    Log.Error("[SuAPI][dispatch] " + eventName +
                        " 有订阅者抛异常 → " + s);
                }
            }
        }
    }
}
```

**`SubscribeDispatch<T>` 去掉 `Game.Subsystem` 约束**：

```csharp
// Survivalcraft/SuAPI/DispatchEventExtensions.cs
public static EventSubscriptionToken SubscribeDispatch<THost>(
    this IModEventBus bus,
    Action<THost, DispatchEventArgs> handler,
    EventPriority priority = EventPriority.NORMAL)
{
    string eventName = typeof(THost).FullName;
    return bus.SubscribeEvent(eventName, args =>
    {
        if (args != null && args.Length >= 2 &&
            args[0] is DispatchEventArgs e &&
            args[1] is THost host)
        {
            handler(host, e);
        }
        return null;
    }, priority);
}
```

> 去掉 `where THost : Game.Subsystem` 后，**子系统与组件共用同一个扩展方法**。

### 4.6.3 组件分发器示例：`ComponentVitalStats`

引擎现状（`Survivalcraft/Game/ComponentVitalStats.cs`）：

```csharp
public class ComponentVitalStats : Component, IUpdateable
{
    public UpdateOrder UpdateOrder => UpdateOrder.Default;
    public void Update(float dt) { ... }        // ← 非虚！子类用显式接口实现
}
```

**分发器**：

```csharp
// Survivalcraft/SuAPI/ComponentVitalStats.cs
namespace SuAPI
{
    /// <summary>替换 Game.ComponentVitalStats 的分发器。</summary>
    public class ComponentVitalStats : Game.ComponentVitalStats, IUpdateable
    {
        void IUpdateable.Update(float dt)
        {
            var e = new DispatchEventArgs
            {
                Operation = Operations.Update,
                Args = new object[] { dt },
                BaseInvoker = args => base.Update((float)args[0])
            };
            DispatchHelper.Fire("SuAPI.ComponentVitalStats", e, this);

            if (e.CallBase && e.BaseCallCount == 0)
                base.Update(dt);
        }
    }
}
```

**`Update` 同样非虚** → 用显式接口实现（与 `ScMultiplayer.SuSubsystemElectricity` 一致，见 §3.5.11）。

### 4.6.4 组件与子系统差异对照（已核实）

| 项 | 子系统 | 组件 |
|---|---|---|
| 基类 | `Game.Subsystem` | `Game.Component` |
| `Project` 访问 | ✅ `public Project Project` | ✅ `public Project Project => m_entity.Project` |
| `Entity` 访问 | ❌ 无 | ✅ `public Entity Entity => m_entity` |
| `ValuesDictionary` | 构造时传参 | ✅ `public ValuesDictionary ValuesDictionary => m_valuesDictionary` |
| `Load` 签名 | `Load(ValuesDictionary)` | **`Load(ValuesDictionary, IdToEntityMap)`** |
| `Save` 签名 | `Save(ValuesDictionary)` | **`Save(ValuesDictionary, EntityToIdMap)`** |
| `OnEntityAdded/Removed` | ✅ 有 | ✅ 有（`protected internal virtual`） |
| `Dispose` | `public virtual void Dispose()` | 同 |
| `Update` 可用性 | 视类而定 | 视类而定（同样非虚，需显式接口） |

**结论：组件与子系统差异比想象中小** —— `Project` 都能拿，
主要区别是 `Load`/`Save` 多一个参数、以及组件多了 `Entity`。

### 4.6.5 组件 API 完整清单（已核实）

```csharp
// Source: EntitySystem/GameEntitySystem/Component.cs
public abstract class Component : IDisposable
{
    public ValuesDictionary ValuesDictionary => m_valuesDictionary;      // :14
    public Entity Entity => m_entity;                                    // :16
    public Project Project => m_entity.Project;                          // :18
    public bool IsAddedToProject => m_entity.IsAddedToProject;           // :20
    public virtual IEnumerable<Entity> GetOwnedEntities();               // :22
    protected internal virtual void OnEntityAdded();                     // :27
    protected internal virtual void OnEntityRemoved();                   // :31
    protected internal virtual void Load(ValuesDictionary, IdToEntityMap);   // :35
    protected internal virtual void Save(ValuesDictionary, EntityToIdMap);   // :39
    public virtual void Dispose();                                       // :43
}
```

### 4.6.6 组件分发与「新增组件」的区别（重要）

| 场景 | 做法 | 需要分发器 |
|---|---|---|
| **新增**一个组件（如 WatchMod 的手表） | 新建 `ComponentTemplate` + `Parameter Class` + `MemberComponentTemplate` | ❌ **不需要**（追加对象，不抢占） |
| **替换**已有组件（如改 `ComponentVitalStats`） | 改已有 `ComponentTemplate` 的 `Class` 参数 | ✅ **需要** |

**WatchMod 属于前者**（`Plug/WatchMod.cs:29-73` 全是 `new DatabaseObject(...)`），
不冲突、不需要分发。

**`SuApocalypse` 属于后者**（`Plug/SuApocalypseMod.cs:21`
`modInjector.Register("Game.ComponentVitalStats", "SuApocalypse.SuApocalypseVitalStats")`），
会抢占，需要分发。


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

### 6.1 全量抢占扫描结果（2026-11-09 实测）

扫描 `Mod/` 全仓的 `modInjector.Register("Game.Xxx", "Yyy")` 调用：

| 指标 | 数量 |
|---|---|
| **被 Mod 抢占的 `Class` 位置** | **56** |
| **真实多 Mod 冲突** | **2** |
| 单一 Mod 独占 | 54 |

### 6.2 两个真实冲突

#### 冲突 1：`Game.ComponentVitalStats`

| Mod | 替换为 | 位置 |
|---|---|---|
| `SuApocalypse` | `SuApocalypse.SuApocalypseVitalStats` | `Plug/SuApocalypseMod.cs:21` |
| `TemperatureImmunity` | `Sumod.SumodComponentVitalStats` | `Plug/TemperatureImmunity.cs:38` |

**这是组件（不是子系统）** → 正好验证 §4.6 的组件分发必要性。

#### 冲突 2：`Game.SubsystemElectricity`

| Mod | 替换为 | 位置 |
|---|---|---|
| `ScMultiplayer` | `ScMultiplayer.SuSubsystemElectricity` | 旧实现 |
| `SuMultiplayer` | `SuMultiplayer.SuSubsystemElectricity` | `Plug/SuMultiplayerMod.cs` |

**实际不冲突**：`ScMultiplayer` 已被 `SuMultiplayer` 取代，两者互斥安装。

### 6.3 `SuMultiplayer` 独占 54 个位置（最大占用者）

`SuMultiplayer` 一个 Mod 就 `Register` 了 **50+ 个 `Class` 位置**，覆盖：

| 类别 | 例子 |
|---|---|
| 核心子系统 | `SubsystemTerrain`、`SubsystemPlayers`、`SubsystemSpawn`、`SubsystemWeather` |
| 方块行为（BlockBehavior） | `SubsystemButtonBlockBehavior`、`SubsystemWaterBlockBehavior`、`SubsystemGrassBlockBehavior`… |
| 组件 | `ComponentGui`、`ComponentInput`、`ComponentHumanModel`、`ComponentFlu` |

**这是分发器最大的潜在受益者** —— 它现在的做法是「独占 54 个位置」，
任何想改同一个子系统的 Mod 都无法共存。

### 6.4 会被影响的现有 Mod

| Mod | 现状 | 迁移后 |
|---|---|---|
| `Channel` | `Register("Game.SubsystemButtonBlockBehavior", ...)` | 改为订阅分发器事件 |
| `ConsoleMod` | 直接写 `param.Value`（`Plug/ConsoleMod.cs:34`） | 改为订阅 |
| `MemoryBankDrawMod` | 直接写 `param.Value`（`Plug/MemoryBankDrawMod.cs:47`） | 改为订阅（§6.5） |
| `SuApocalypse` | `Register("Game.ComponentVitalStats", ...)` | 改为订阅组件分发器 |
| `TemperatureImmunity` | `Register` 4 个组件 | 改为订阅 |

### 6.5 `MemoryBankDrawMod` 迁移示例（具体案例）

**现状**（`Plug/MemoryBankDrawMod.cs`）：

```csharp
// 直接改数据库 Value（绕过 ModInjector.Register）
public object[] HandleGameDatabase(Database database)
{
    var param = database.FindDatabaseObject(
        new Guid("32a2d9ef-b01a-4f80-a6f8-5d2d5e9e9275"),   // SubsystemMemoryBankBlockBehavior 的 Class 参数
        database.FindDatabaseObjectType("Parameter", true), true);
    param.Value = "MemoryBankDrawMod.SuSubsystemMemoryBankBlockBehavior";
    return new object[] { true, database };
}
```

```csharp
// 继承替换，覆写 2 个方法
public class SuSubsystemMemoryBankBlockBehavior : SubsystemMemoryBankBlockBehavior
{
    public override bool OnEditInventoryItem(...) { ... }   // 换对话框
    public override bool OnEditBlock(...) { ... }            // 换对话框
}
```

**迁移后**：

```csharp
public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    // ① 方块编辑：截住原版，弹自己的对话框
    eventBus.SubscribeDispatch<SuAPI.SubsystemMemoryBankBlockBehavior>((host, e) =>
    {
        if (e.Operation != SuAPI.Operations.OnEditBlock) return;
        if (e.Handled) return;                        // 已被别人接管

        int x = (int)e.Args[0], y = (int)e.Args[1], z = (int)e.Args[2];
        int value = (int)e.Args[3];
        var player = (ComponentPlayer)e.Args[4];

        MemoryBankData data = host.GetBlockData(new Point3(x, y, z)) ?? new MemoryBankData();
        DialogsManager.ShowDialog(player.GuiWidget, new SuEditMemoryBankDialog(data, delegate
        {
            host.SetBlockData(new Point3(x, y, z), data);
            // ...原有后处理
        }));

        e.CallBase = false;      // 不跑原版
        e.Handled = true;        // 告诉引擎"已处理"
    });

    // ② 物品编辑：同上
    eventBus.SubscribeDispatch<SuAPI.SubsystemMemoryBankBlockBehavior>((host, e) =>
    {
        if (e.Operation != SuAPI.Operations.OnEditInventoryItem) return;
        // ...
        e.CallBase = false;
        e.Handled = true;
    });

    // ③ 对话框兼容（原有逻辑保留）
    eventBus.SubscribeEvent("Frame.Update", args =>
    {
        MemoryBankDialogCompatibility.ReplaceNativeDialogs();
        return args;
    }, EventPriority.LOWEST);
}
```

**删除** `Func/SuSubsystemMemoryBankBlockBehavior.cs` 整个文件。

**可行性已验证**：`GetBlockData` / `SetBlockData` / `GetItemData` /
`StoreItemDataAtUniqueId` 全部是 **`public`**（`SubsystemEditableItemBehavior.cs:23-47`），
事件回调里能直接调用。

### 6.6 与 `SuEventBus` 的关系（待定）

`Mod/SuEventBus` 已经是可用的跨 Mod 事件中间件（类型安全）。
按用户口径：**分发器完成后再考虑**是否统一。

- 两套并存？还是 `SuEventBus` 逐步废弃？
- 新的分发器事件是否也走 `SuEventBus`（更类型安全）？

---

## 7. 已定与待定

### 7.1 已定（2026-11-09）

| # | 问题 | 决定 |
|---|---|---|
| **D1** | 分发器放哪 | ✅ `Survivalcraft/SuAPI/` → 编译进 **`Survivalcraft.dll`**（Mod 三件套之一） |
| **D2** | 类名 | ✅ `SuAPI.SubsystemTimeOfDay`（与原类同名，命名空间区分） |
| **D3** | 事件粒度 | ✅ **一个子系统一个事件**，用 `DispatchEventArgs.Operation` 区分操作 |
| **D4** | 事件参数形态 | ✅ 强类型 `DispatchEventArgs`（不是裸 `object[]`） |
| **D5** | 原版逻辑怎么控制 | ✅ `CallBase`（默认 true，多 Mod 后改者优先）+ `Handled`（bool 返回值）+ `BaseCallCount`（接替实现） |
| **D6** | 私有成员访问 | ✅ 用已有工具 `IModParentField`（不新造） |
| **D7** | 调用次数控制 | ✅ 用已有工具 `IModParentMethod`，分发器提供 `e.InvokeBase(...)` 自动计数 |
| **D8** | Mod 侧订阅写法 | ✅ 泛型扩展 `eventBus.SubscribeDispatch<T>((host, e) => {...})` |
| **D9** | 抢占注册优先级 | ✅ `GameDatabase.GameDatabase` 用 **`NORMAL`** —— 不最先、不最后 |
| **D10** | 位置被抢时 | ✅ **让位**：检测到 `Class` 已被其它 Mod 改过就不 `Register`、不覆盖；写 `Log.Information` 记录 |
| **D11** | 异常处理 | ✅ `TriggerEvent` 吞掉的异常转成 `Log.Error`（见 §7.4） |
| **D12** | 事件 `args` 布局 | ✅ `args[0]` = `DispatchEventArgs`，`args[1]` = 宿主实例（见 §3.5.5） |

### 7.2 已明确不处理（2026-11-09 用户口径）

| # | 问题 | 决定 | 理由 |
|---|---|---|---|
| **X1** | `UpdateOrder` 只能有一个值 | ❌ **不处理** | 实测无 Mod 覆写它，风险低 |
| **X2** | 数值冲突怎么合并 | ❌ **不处理** | 由 Mod 自己操作字段的自然结果决定（`+=` 叠加 / `=` 后者覆盖） |
| **X3** | `SuEventHub` 与 `ModEventBus` 的关系 | ⏳ **分发器完成后再考虑** | 当前两套并存，先不统一 |
| **X4** | 事件名靠 `typeof(T).FullName`，改类名会让旧 Mod 静默失效 | ❌ **不处理** | 接受这个约束 |

### 7.3 必须处理

| # | 问题 | 决定 |
|---|---|---|
| **Y1** | **`TriggerEvent` 吞异常** | ✅ **必须把异常写进 `Log.Error`**（见 §7.4） |
| **Y2** | `HandledBlocks` 只读一次 | ⏳ 待评估（见 §7.5） |
| **Y3** | `ref` / `out` 参数丢失 | ⏳ 待评估（见 §7.5） |

### 7.4 异常必须写进 `Game.log`（Y1）

**问题**：`ModEventBus.TriggerEvent` 的实现（`ModEventBus.cs:88-100`）：

```csharp
catch (Exception ex)
{
    Console.WriteLine($"事件处理异常: {ex.Message}");   // ← 只到 Console，不进 Game.log
    results.Add(new object[] { $"Error: {ex.Message}" });
}
```

Mod 回调抛异常时，`Game.log` 里**一行都没有**，排查时完全看不到。

**解决**：分发器在 `Dispatch` 里检查返回值，把错误写进 `Log.Error`。

```csharp
private void Dispatch(string eventName, DispatchEventArgs e)
{
    object[][] results = null;
    try
    {
        results = ModManager.Instance?.ModEventBus?.TriggerEvent(
            eventName, new object[] { e });
    }
    catch (Exception ex)
    {
        Log.Error("[SuAPI][dispatch] " + eventName + " 分发本身失败：" + ex);
        return;
    }

    if (results == null) return;
    for (int i = 0; i < results.Length; i++)
    {
        object[] r = results[i];
        if (r == null || r.Length == 0) continue;
        if (r[0] is string s && s.StartsWith("Error:", StringComparison.Ordinal))
        {
            // TriggerEvent 把订阅者异常包成 "Error: <message>" 塞进结果
            Log.Error("[SuAPI][dispatch] " + eventName + " 有订阅者抛异常 → " + s);
        }
    }
}
```

**效果**：

| 场景 | 之前 | 之后 |
|---|---|---|
| Mod 回调抛异常 | `Console.WriteLine`（玩家看不到） | ✅ `Game.log` 里有 `ERROR: [SuAPI][dispatch] ...` |
| 多个 Mod 都抛异常 | 只有最后一条 Console | ✅ 每条都记录 |

> ⚠️ 注意：`TriggerEvent` 只把 `ex.Message` 放进结果，**丢失堆栈**。
> 若需要堆栈，分发器可以额外订阅一个"错误详情"通道，或在文档里约定
> 「Mod 回调必须自己 `try/catch` 并 `Log.Error`」（推荐后者，零成本）。

### 7.5 待评估的两个结构性硬伤

#### Y2. `HandledBlocks` 只读一次

**机制**（`SubsystemBlockBehaviors.cs:36-48`）：

```csharp
foreach (SubsystemBlockBehavior item2 in Project.FindSubsystems<SubsystemBlockBehavior>())
{
    int[] handledBlocks = item2.HandledBlocks;      // ← 建索引时读一次，缓存进字典
    foreach (int key in handledBlocks)
        dictionary[key].Add(item2);
}
```

**影响**：分发器无法在运行时汇总多个 Mod 的方块需求。

| Mod | 现状 | 用分发器后 |
|---|---|---|
| `CircuitAutoRouter` | `override int[] HandledBlocks => new[] { RodBlockIndex }` | ❌ 做不到 |
| `SuAPIShadows.SubsystemDynamicShadows` | 也覆写 | ❌ 做不到 |
| `SuMultiplayer.SuSubsystemBlockBehaviors` | `Array.Empty<int>()` | ⚠️ 需评估 |

**可能解法**：SuAPI 提供**静态注册表**，Mod 在 `Project` 创建前声明自己要处理哪些方块。

```csharp
// Mod.OnLoad（早于 Project 创建）
SuAPI.Dispatch.ClaimBlocks("Game.SubsystemCircuitRouter", RodBlockIndex);

// 分发器的 HandledBlocks
public override int[] HandledBlocks => SuAPI.Dispatch.GetClaimedBlocks("...").ToArray();
```

⚠️ 这是 **SuAPI 新增 API**（当前正在改 SuAPI，所以可行）。

#### Y3. `ref` / `out` 参数

**涉及的 3 个方法**（`SubsystemBlockBehavior`）：

```csharp
public virtual void OnItemPlaced(int x, int y, int z, ref BlockPlacementData placementData, int itemValue)
public virtual void OnItemHarvested(int x, int y, int z, int blockValue, ref BlockDropValue dropValue, ref int newBlockValue)
public virtual void ProcessInventoryItem(..., out int processedValue, out int processedCount)
```

`BlockPlacementData` / `BlockDropValue` 都是 **`struct`**（值类型）。

**问题**：

| 项 | 后果 |
|---|---|
| 塞进 `object[]` 会装箱复制 | Mod 改的是副本 |
| 分发器要手动写回 | 必须**硬编码字段映射** |
| 多个 Mod 都改 | 只能写回**最后一个**的值 |
| `out` 参数 | 无 Mod 处理时，分发器不知道填什么默认值 |

**可能解法**：为这 3 个方法定义**专用事件类**（带可写属性）。

```csharp
public sealed class ItemPlacedEventArgs
{
    public int X, Y, Z, ItemValue;
    public BlockPlacementData PlacementData;    // 字段，可改
}
// 分发器写回：
public override void OnItemPlaced(..., ref BlockPlacementData placementData, ...)
{
    var e = new ItemPlacedEventArgs { ..., PlacementData = placementData };
    Dispatch("...", e);
    placementData = e.PlacementData;            // ← 明确写回
}
```

**当前评估**：实测 mod 集里**没有 Mod 覆写这 3 个方法**（`OnItemPlaced` / `OnItemHarvested` 使用次数为 0）。所以**优先级低**，可以先不做。


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

## 10. 补充方案：新增独立 `MemberSubsystemTemplate`（仅适用纯附加场景）

> 🔸 **状态（2026-11-09 二次更新）：降级为补充手段，不是主方案。**
>
> 本节方案能让多个实例共存，但**原版仍会执行** ——
> 因此只适用于「纯附加」（如攻击时记录日志，不改变原行为）。
>
> 若要**替换**原行为（如攻击力 1→2，原版不能再扣 1），
> 必须用 **§11** 的同名 Member + 清空原版 Class 方案。
>
> 两方案互补：
> · 附加 → 本节 `AddSubsystem`（原版照跑）
> · 替换 → §11 `Claim`（原版不跑）

### 10.1 核心洞察

引擎加载子系统时，**只遍历 `MemberSubsystemTemplate`**（`Project.cs:37-44`）：

```csharp
foreach (ValuesDictionary item in
    from x in projectData.ValuesDictionary.Values
    select x as ValuesDictionary into x
    where x != null
       && x.DatabaseObject != null
       && x.DatabaseObject.Type == gameDatabase.MemberSubsystemTemplateType   // ← 只认 Member
    select x)
{
    bool value = item.GetValue<bool>("IsOptional");
    string value2 = item.GetValue<string>("Class");
    Type type = TypeCache.FindType(value2, skipSystemAssemblies: false, !value);
    if (type != null)
    {
        object obj = Activator.CreateInstance(type);
        if (!(obj is Subsystem subsystem))
            throw new InvalidOperationException(...);
        subsystem.Initialize(this, item);
        dictionary.Add(item.DatabaseObject.Name, subsystem);   // ← 名字来自 Member
        m_subsystems.Add(subsystem);
    }
}
```

**关键结论**：

| 事实 | 含义 |
|---|---|
| 引擎遍历的是 `MemberSubsystemTemplate` 列表 | 每个 Member 创建一个实例 |
| 名字来自 `MemberSubsystemTemplate.Name` | **只要名字不同就不会冲突** |
| 名字没有语义用途，只是内部标识 | Mod 可以随便起唯一名字 |
| `m_subsystems` 收全部实例 | `FindSubsystems<T>()` 能拿到所有 |

**所以：Mod 只要新增一个 `MemberSubsystemTemplate`，就多了一个实例 —— 不抢原来那个。**

### 10.2 数据库结构（三层）

```
ProjectTemplate（引擎的 Project 定义）
  └── MemberSubsystemTemplate  Name="TimeOfDay"          ← 引擎按这些加载
        └── ExplicitInheritanceParent → SubsystemTemplate Name="TimeOfDay"
              └── Parameter Name="Class" Value="Game.SubsystemTimeOfDay"
```

| 层 | 作用 | 数量 |
|---|---|---|
| `SubsystemTemplate` | 定义「这个子系统是什么类的实例」 | 108 |
| **`MemberSubsystemTemplate`** | **挂到 Project 上，决定「要加载哪些」** | **106** |
| `Parameter Name="Class"` | 实际类名 | 108 |

**Mod 要新增的是 `MemberSubsystemTemplate`（第 2 层），复用已有的 `SubsystemTemplate`（或自建）。**

### 10.3 SuAPI 提供的辅助 API

```csharp
// Survivalcraft/SuAPI/SubsystemRegistration.cs
namespace SuAPI
{
    /// <summary>
    /// 让 Mod 向 Project 追加额外的子系统/组件实例，而不抢占原有 Class 位置。
    ///
    /// 原理：引擎按 MemberSubsystemTemplate / MemberComponentTemplate 逐个实例化，
    /// 只要新增条目的 Name 不与已有重名，就会多出一个独立实例，
    /// 与原有的并存（引擎遍历数组时全部调用）。
    /// </summary>
    public static class SubsystemRegistration
    {
        /// <summary>
        /// 追加一个子系统实例。
        /// </summary>
        /// <param name="database">GameDatabase.GameDatabase 事件传入的 Database</param>
        /// <param name="memberName">唯一名字（不能与已有 MemberSubsystemTemplate 重名）</param>
        /// <param name="memberGuid">唯一 GUID（Mod 自己生成并固定下来）</param>
        /// <param name="className">类全名，如 "MyMod.MyButtonBehavior"</param>
        /// <param name="classParamGuid">Class 参数的唯一 GUID</param>
        /// <param name="subsystemTemplateGuid">SubsystemTemplate 的 GUID（与 classParamGuid 不同）</param>
        /// <param name="parentSubsystemTemplateGuid">
        /// 继承哪个已有 SubsystemTemplate（拿 Dependencies/IsOptional 等参数）。
        /// 传 default(Guid) 表示不继承。
        /// </param>
        public static void AddSubsystem(
            Database database,
            string memberName,
            Guid memberGuid,
            string className,
            Guid subsystemTemplateGuid,
            Guid classParamGuid,
            Guid parentSubsystemTemplateGuid = default)
        {
            var subTemplateType = database.FindDatabaseObjectType("SubsystemTemplate", true);
            var memberType      = database.FindDatabaseObjectType("MemberSubsystemTemplate", true);
            var paramType       = database.FindDatabaseObjectType("Parameter", true);
            var projectType     = database.FindDatabaseObjectType("ProjectTemplate", true);

            // ① SubsystemTemplate
            var subTemplate = new DatabaseObject(subTemplateType, subsystemTemplateGuid,
                memberName + "Template", null);
            subTemplate.Description = "";
            if (parentSubsystemTemplateGuid != default)
                subTemplate.ExplicitInheritanceParent =
                    database.FindDatabaseObject(parentSubsystemTemplateGuid, subTemplateType, false);

            // ② Class 参数
            var clsParam = new DatabaseObject(paramType, classParamGuid, "Class", className);
            clsParam.NestingParent = subTemplate;

            // ③ MemberSubsystemTemplate（挂到 Project）
            var member = new DatabaseObject(memberType, memberGuid, memberName, null);
            member.Description = "";
            member.ExplicitInheritanceParent = subTemplate;
            // 「Project」是引擎的 ProjectTemplate 名（Db 中 Guid=85023bf8-...，
            // 它继承了 GameProject，105 个 Member 都挂在这个下面）
            member.NestingParent = database.FindDatabaseObject(
                "Project", projectType, true);
        }

        /// <summary>
        /// 追加一个组件实例（挂到指定实体的 MemberComponentTemplate 列表）。
        /// </summary>
        public static void AddComponent(
            Database database,
            string entityTemplateName,          // 如 "Player"
            string componentName,
            Guid memberGuid,
            string className,
            Guid componentTemplateGuid,
            Guid classParamGuid,
            Guid parentComponentTemplateGuid = default,
            int loadOrder = 0)
        { ... }
    }
}
```

### 10.4 Mod 侧用法（一行调用）

```csharp
public class MyMod : IMod
{
    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
        {
            var db = (Database)args[0];

            // 追加自己的方块行为实例（与原版共存）
            SuAPI.SubsystemRegistration.AddSubsystem(
                db,
                memberName:           "MyModButtonBehavior",
                memberGuid:           new Guid("11111111-1111-1111-1111-111111111111"),
                className:            "MyMod.MyButtonBehavior",
                subsystemTemplateGuid:new Guid("22222222-2222-2222-2222-222222222222"),
                classParamGuid:       new Guid("33333333-3333-3333-3333-333333333333"),
                parentSubsystemTemplateGuid: new Guid("fefb9590-4972-4893-b02a-76063611b745")); // ButtonBlockBehavior 的父模板

            return new object[] { true, db };
        }, EventPriority.NORMAL);
    }
}
```

```csharp
// Mod 自己的类 —— 直接继承引擎基类，不继承被替换者
namespace MyMod
{
    public class MyButtonBehavior : Game.SubsystemBlockBehavior
    {
        public override int[] HandledBlocks => new[] { 142 };   // 按键方块

        public override bool OnEditBlock(int x, int y, int z, int value, ComponentPlayer player)
        {
            // 纯自己的逻辑；原版 SubsystemButtonBlockBehavior 不受影响
            ...
            return false;      // false = 让原版也处理
        }
    }
}
```

### 10.5 为什么这比分发器好

| 维度 | 分发器（废弃） | **新增 Member（采用）** |
|---|---|---|
| 需要写的类 | **107 个**（~3500 行） | **0 个**（Mod 自己写自己的） |
| SuAPI 代码量 | ~3500 行 | **~150 行** |
| 引擎改动 | 无 | **无** |
| `HandledBlocks` | ❌ 硬伤（只读一次，无法汇总） | ✅ **天然支持**（每个实例独立声明） |
| `ref`/`out` 参数 | ❌ 硬伤（`object[]` 无法承载） | ✅ **天然支持**（直接 override） |
| `UpdateOrder` 冲突 | ❌ 只能一个值 | ✅ **各自独立** |
| Mod 迁移成本 | 改订阅事件 | **一行调用 + 继承基类** |
| 静默失效风险 | ⚠️ 被抢则事件不触发 | ❌ 无 |

### 10.6 已验证的引擎机制

#### 方块行为：引擎原生多实例

```csharp
// SubsystemBlockBehaviors.cs:38-45
foreach (SubsystemBlockBehavior item2 in base.Project.FindSubsystems<SubsystemBlockBehavior>())
{
    m_blockBehaviors.Add(item2);
    int[] handledBlocks = item2.HandledBlocks;
    foreach (int key in handledBlocks)
        dictionary[key].Add(item2);          // ← 同一方块可挂多个
}

// SubsystemTerrain.cs:258-268 —— 遍历数组，全部调用
SubsystemBlockBehavior[] blockBehaviors = m_subsystemBlockBehaviors.GetBlockBehaviors(num);
for (int i = 0; i < blockBehaviors.Length; i++)
    blockBehaviors[i].OnBlockRemoved(cellValueFast, value, x, y, z);
```

**这是引擎自带的「多 Mod 共存」机制**，不需要任何改动。

#### 组件：同样支持多实例

```csharp
// Entity.cs:114-140 —— 遍历所有 MemberComponentTemplate
foreach (ValuesDictionary item in
    from x in valuesDictionary.Values
    where x.DatabaseObject.Type == project.GameDatabase.MemberComponentTemplateType
    select x)
{
    ...
    component.Initialize(this, item);
    list.Add(new KeyValuePair<int, Component>(value3, component));
}
```

**按 `LoadOrder` 排序，全部实例化，无重名限制。**

### 10.7 什么时候还需要「改 Project.cs 一行」

**只有一种情况**：多个 Mod 想用**完全相同的** `MemberSubsystemTemplate.Name`。

```csharp
// Project.cs:63
dictionary.Add(item.DatabaseObject.Name, subsystem);   // ← 重名抛 ArgumentException
```

**不需要改，因为名字本来就该唯一**（Mod 各自起名即可）。

**如果未来确实需要**（如两个 Mod 都想叫 `"BlockBehaviors"`），改动是：

```csharp
//mod 允许重名：dictionary 只用于 Dependencies 顺序解析，真正的查询走 m_subsystems
if (!dictionary.ContainsKey(item.DatabaseObject.Name))
    dictionary.Add(item.DatabaseObject.Name, subsystem);
m_subsystems.Add(subsystem);        // ← 不变
//..mod
```

并配合 `Project.cs:65-68` 改用 `m_subsystems` 遍历（否则重名的第二个永远不会 `Load`）：

```csharp
//mod 遍历 m_subsystems 而非 dictionary.Values，确保重名实例也被 Load
foreach (Subsystem value3 in new List<Subsystem>(m_subsystems))
    LoadSubsystem(value3, dictionary, loadedSubsystems, 0);
//..mod
```

**但当前不需要 —— 先按方案 C 实施。**

### 10.8 实施清单

| # | 文件 | 动作 |
|---|---|---|
| 1 | `Survivalcraft/SuAPI/SubsystemRegistration.cs` | 🆕 新建（~150 行） |
| 2 | `Survivalcraft/SuAPI/ComponentRegistration.cs` | 🆕 新建（或与上合并） |
| 3 | 引擎代码 | ❌ **不改** |

**不需要**：
- ❌ 107 个分发器类
- ❌ `DispatchEventArgs` / `DispatchHelper` / `DispatchEventExtensions`
- ❌ `DispatchRegistry`（`HandledBlocks` 静态注册表）
- ❌ 改 `SuAPICoreMod.cs` 做抢占注册

### 10.9 数据库结构核实结果

| 项 | 数量 / 值 | 位置 |
|---|---|---|
| `SubsystemTemplate`（定义类） | 108 | — |
| `MemberSubsystemTemplate`（挂载） | **106** | 分布在不同 `ProjectTemplate` 下 |
| `ComponentTemplate` | 87 | — |
| `MemberComponentTemplate` | **733** | — |
| 引擎实际使用的 `ProjectTemplate` | **`Project`**（Guid `85023bf8-1c90-4dd1-9442-e6c13691d078`） | `Pak/Database.xml:4290` |
| 其继承父模板 | `GameProject`（Guid `9e9a67f8-...`） | `Pak/Database.xml:4285` |
| `Project` 下挂的 Member 数 | **105** | — |

> ⚠️ 注意：新条目必须挂到 **`Project`** 这个 `ProjectTemplate` 下（不是 `GameProject`），
> 因为 `Project` 才是所有子系统 Member 的实际父节点。

### 10.10 待验证

| # | 验证项 | 方式 |
|---|---|---|
| 1 | 新增 `MemberSubsystemTemplate` 后引擎能否实例化 | 实验：加一个最简单的子系统 |
| 2 | `SubsystemBlockBehaviors` 是否收集到新实例 | 日志打印 `BlockBehaviors.Count` |
| 3 | 新的 `HandledBlocks` 是否生效 | 游戏内验证方块行为 |
| 4 | 组件是否同样可行 | 同上 |


## 11. 最终方案（2026-11-09 定稿）

> **用户口径**：
> - "我要的就是你前面提到的，最简的路径，现在只考虑这一个方向。"
> - "这里的优先级不需要吧。要修改 database，就是要订阅 database 的事件，事件订阅的时候，事件有优先级。然后这个 `SuAPI.SubsystemSlot.Claim` 应该只放到事件中。"
>
> 本节是唯一有效方向。§3-§9（分发器）**永久否定**，见 §12。

### 11.1 要解决的问题

**只解决一件事**：一个 Mod 替换掉原子系统 `Class` 后，其它 Mod 没法再替换。

| 要的 | 不要的 |
|---|---|
| ModA 执行 | ❌ 不管 ModA 与 ModB 之间怎么协调 |
| ModB 执行 | ❌ 不管数据冲突合并 |
| **原版不执行** | ❌ 不建 108 个分发器 |

**A 与 B 的冲突由 Mod 开发者自己解决**（用 `EventPriority` 表达先后），SuAPI 只解决「只能一个执行」。

### 11.2 核心机制：**用 `EventPriority` 表达占位顺序**

`Claim` 只在 `GameDatabase.GameDatabase` **事件回调里**调用，因此天然带上订阅优先级。

```csharp
// ModEventBus.cs:67 —— 按 Priority 升序（HIGHEST=0 最先），同优先级按订阅顺序
var sorted = list.OrderBy(s => s.Priority).ThenBy(s => s.Order).ToList();
```

**执行顺序 = `EventPriority` 顺序。先执行的那个 Mod 抢到正位。**

### 11.3 Mod 侧写法

```csharp
public class ModA : IMod
{
    public string Name => "ModA";

    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
        {
            var db = (Database)args[0];

            // 立刻 Register（抢正位）；先执行者胜
            modInjector.Register("Game.SubsystemTimeOfDay", "ModA.MyTimeOfDay");

            return new object[] { false, db };
        }, EventPriority.HIGHEST);        // ← 用事件优先级表达"我要先"
    }
}
```

```csharp
public class ModB : IMod
{
    public string Name => "ModB";

    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
        {
            var db = (Database)args[0];

            // 同样的写法：Register。如果 ModA 已占位，SuAPI 自动降级为附加
            modInjector.Register("Game.SubsystemTimeOfDay", "ModB.MyTimeOfDay");

            return new object[] { false, db };
        }, EventPriority.NORMAL);         // ← 后执行
    }
}
```

**两个 Mod 都只写 `Register`，不用改任何写法。**

### 11.4 SuAPI 侧的自动仲裁

关键：**`Register` 被调用时，SuAPI 检查该位置是否已被占**。

```csharp
// ModInjector.cs 的 Register 内部（SuAPI 改造点）
public void Register(string originalClassName, string newClassName)
{
    //mod 同名位置已被占用 → 自动降级为"附加"（新增不同名的 Member）
    if (m_claimedPositions.ContainsKey(originalClassName))
    {
        string occupiedBy = m_claimedPositions[originalClassName];
        Log.Information("[SuAPI][slot] " + originalClassName + " 已被 " + occupiedBy +
            " 占据，" + (CurrentModName ?? "?") + " 自动降级为附加");

        SuAPI.SubsystemRegistration.AddSubsystem(
            m_pendingDatabase, originalClassName, newClassName);
        return;
    }
    //..mod

    // 原逻辑
    _injectionRecords.Add(new InjectionRecord { ... });
    m_claimedPositions[originalClassName] = CurrentModName;
}
```

**效果**：

| 场景 | 结果 |
|---|---|
| 只装 ModB | B 占正位（`Class = "ModB.MyTimeOfDay"`），原版不创建 ✅ |
| 只装 ModA | A 占正位，原版不创建 ✅ |
| A + B 都装 | A（`HIGHEST`）先执行占正位；B（`NORMAL`）后执行 → **自动降级为附加**（新增 `Member "TimeOfDay__ModB"`）<br>结果：A 执行、B 执行、原版不执行 ✅ |

### 11.5 数据流

#### 情况 1：只装 ModB

```
ModB.Register("Game.SubsystemTimeOfDay", "ModB.MyTimeOfDay")
  → 该位置无人占 → 登记为 InjectionRecord
  → Apply 时：Class = "ModB.MyTimeOfDay"

数据库结果：
  Member "TimeOfDay"  Class="ModB.MyTimeOfDay"

引擎：
  原版 Game.SubsystemTimeOfDay 实例：❌ 不创建
  ModB.MyTimeOfDay 实例：✅ 创建
```

#### 情况 2：A + B 都装

```
ModA.Claim（HIGHEST，先执行）
  → ModA.Register("Game.SubsystemTimeOfDay", "ModA.MyTimeOfDay")
  → 位置无人占 → 登记为 InjectionRecord，标记已占
  → Apply 时：Class = "ModA.MyTimeOfDay"

ModB（NORMAL，后执行）
  → ModB.Register("Game.SubsystemTimeOfDay", "ModB.MyTimeOfDay")
  → SuAPI 检测到位置已被 ModA 占
  → 自动降级：新增 Member "TimeOfDay__ModB" Class="ModB.MyTimeOfDay"

数据库结果：
  Member "TimeOfDay"        Class="ModA.MyTimeOfDay"      ← 正位
  Member "TimeOfDay__ModB"  Class="ModB.MyTimeOfDay"      ← 附加

引擎：
  原版实例：❌ 不创建（正位被 ModA 顶替）
  ModA 实例：✅
  ModB 实例：✅
```

### 11.6 关于「改名」的说明

**`Member` 名字唯一性**：`DatabaseObject.cs:176` 有重名检查，
`sibling` 下不能有两个同名 `DatabaseObject`。

**所以降级时 SuAPI 必须给新 Member 起不同的名字**（如 `"TimeOfDay__ModB"`）。

**`Dependencies` 的影响**：

```xml
<!-- Sky 的 Dependencies -->
<Parameter Name="Dependencies" Value="TimeOfDay, Seasons" />
```

| 场景 | `dictionary["TimeOfDay"]` 指向 |
|---|---|
| 只装 B | ModB 实例 |
| A + B | **ModA 实例**（正位），不是 ModB |

**这是必然的**：名字只有一个。`Dependencies` 只能解析到正位那个。

### 11.7 引擎改动清单

#### 改动 ①：`ModInjector.Register` 增加占位检测

```csharp
// EntitySystem/SuAPI/ModInjector.cs
private readonly Dictionary<string, string> m_claimedPositions = new();

public void Register(string originalClassName, string newClassName)
{
    //mod 位置已占用 → 自动降级为附加（新增不同名 Member），保证多 Mod 都能执行
    //mod 为什么：Class 参数是单值，只能一个 Mod 顶替原版；
    //mod   其它 Mod 通过新增独立 MemberSubsystemTemplate 达成"也执行"，
    //mod   而原版因为正位被顶替，不会创建。
    if (m_claimedPositions.TryGetValue(originalClassName, out string occupiedBy))
    {
        Log.Information("[SuAPI][slot] " + originalClassName + " 已被 " + occupiedBy +
            " 占据，" + (CurrentModName ?? "?") + " 自动降级为附加");
        m_pendingAttaches.Add((originalClassName, newClassName));
        return;
    }

    _injectionRecords.Add(new InjectionRecord { ... });
    m_claimedPositions[originalClassName] = CurrentModName;
    //..mod
}
```

#### 改动 ②：`Apply` 里一并创建降级的附加实例

```csharp
public void Apply(Database database, string modName)
{
    // ...原有逻辑...

    //mod 为降级的 Mod 新增附加 Member（不同名，避免冲突）
    foreach (var (original, replacement) in m_pendingAttaches)
    {
        SuAPI.SubsystemRegistration.AddSubsystem(
            database, original, replacement, memberSuffix: SanitizeModName(modName));
    }
    m_pendingAttaches.Clear();
    //..mod
}
```

#### 改动 ③：`ModLoader` 记录当前 Mod 名

```csharp
// ModLoader.cs:36 / :268 / :446 —— 三处 OnLoad 调用前
//mod 记录当前 Mod，供 ModInjector 在降级时打出可定位的日志
(ModInjector as ModInjector)?.SetCurrentMod(mod.Name);
mod.OnLoad(eventBus, modInjector);
//..mod
```

### 11.8 SuAPI 新增

| 文件 | 内容 | 估算 |
|---|---|---|
| `Survivalcraft/SuAPI/SubsystemRegistration.cs` | `AddSubsystem(database, originalMemberName, className, string memberSuffix)` | ~100 行 |

**`AddSubsystem` 做的事**：

```
1. 找到名为 originalMemberName 的 MemberSubsystemTemplate，取其 NestingParent（= "Project"）
2. 找到它对应的 SubsystemTemplate，用于继承 Dependencies
3. 新建 SubsystemTemplate（名字 = originalMemberName + "__" + suffix）
4. 新建 Parameter Name="Class" Value=className
5. 新建 MemberSubsystemTemplate（名字 = originalMemberName + "__" + suffix），挂到 Project 下
```

### 11.9 改动量汇总

| # | 文件 | 改动 | 行数 |
|---|---|---|---|
| 1 | `EntitySystem/SuAPI/ModInjector.cs` | 占位检测 + 降级 | ~25 行 |
| 2 | `EntitySystem/SuAPI/ModLoader.cs` | 3 处记录 Mod 名 | 3 行 |
| 3 | `Survivalcraft/SuAPI/SubsystemRegistration.cs` | 🆕 `AddSubsystem` | ~100 行 |

**引擎 `Project.cs` 零改动** ✅
**引擎 `DatabaseObject.cs` / `ValuesDictionary.cs` 零改动** ✅

### 11.10 为什么这样是最小实现

| 方案 | 引擎核心改动 | SuAPI 改动 | 是否解决"只能一个" |
|---|---|---|---|
| 分发器（§3-§9） | 无 | ~3500 行（108 个类） | ✅ 但成本过高，已否决 |
| 同名 Member + 空 Class（原 §11 草案） | 3 处（含 `ValuesDictionary`） | ~120 行 | ❌ **同名不许共存，方案错误** |
| **本节方案** | **零** | **~128 行** | ✅ |

**关键**：不需要改 `Project.cs`，因为：
- 正位靠现有 `Register` 改 `Class`（引擎本来就这么工作）
- 附加靠新增**不同名** Member（引擎本来就支持多 Member）

### 11.11 效果边界

| 引擎访问方式 | 正位（先执行者） | 附加（降级者） | 原版 |
|---|---|---|---|
| `FindSubsystems<T>()` 遍历（`IUpdateable`、`SubsystemBlockBehavior`） | ✅ | ✅ | ❌ 不创建 |
| `FindSubsystem<T>()` 单引用 | ✅ 返回正位 | ⚠️ 实例在，但引擎只拿第一个 | ❌ 不创建 |

**降级者的实例会被创建并 `Load`，但若引擎用 `FindSubsystem<T>()` 单引用，只有正位那个被调用。**

### 11.12 待确认

| # | 细节 | 状态 |
|---|---|---|
| 1 | `ModInjector` 如何知道「当前是哪个 Mod 在 Register」 | ⏳ 需 `ModLoader` 配合 |
| 2 | 降级后 Member 名字后缀格式（`__ModB`？`_ModB`？） | ⏳ |
| 3 | 同一 Mod 重复 `Register` 同名位置是否幂等 | ⏳ |
| 4 | `AddSubsystem` 添加时 `DatabaseObject` 重名检查如何绕过 | ⏳ 需用不同名 |
| 5 | 降级发生时 `m_pendingDatabase` 何时可用 | ⏳ 需在 `Apply` 前 |


## 12. 已否决的方案（永久记录，勿再尝试）

### 12.1 分发器方案（§3-§9）— **永久否定**

**用户原话**：

> "我现在所有子系统都要执行一边，那我所有子系统都需要重建做一个分发器，我modA做了分发器，modB完全不需要这个功能。这种方案直接永久否定"

**否决理由（结构性缺陷，非实现细节）**：

| # | 缺陷 | 说明 |
|---|---|---|
| 1 | **局部需求 → 全局成本** | ModA 只需要改 1-2 个子系统，却要求 108 个分发器全部预先建好 |
| 2 | **所有权错位** | ModA 写了 `SubsystemX` 的分发器，ModB 就被这个决定绑定 |
| 3 | **ModB 完全不需要** | 但也被迫承担 108 个转发类的维护成本 |
| 4 | 硬伤 | `HandledBlocks` 只读一次无法汇总；`ref`/`out` 参数 `object[]` 无法承载 |

**已废弃的具体内容**：

- ❌ `Survivalcraft/SuAPI/DispatchEventArgs.cs`
- ❌ `Survivalcraft/SuAPI/DispatchHelper.cs`
- ❌ `Survivalcraft/SuAPI/DispatchEventExtensions.cs`
- ❌ `Survivalcraft/SuAPI/Subsystem{Name}.cs`（107 个分发器类）
- ❌ `Survivalcraft/SuAPI/DispatchRegistry.cs`（`HandledBlocks` 静态注册表）
- ❌ `SuAPICoreMod.cs` 里的 `Register` + `NORMAL` 订阅抢占逻辑
- ❌ `CallBase` / `Handled` / `BaseCallCount` / `InvokeBase` 语义
- ❌ `SubscribeDispatch<T>` 泛型扩展

### 12.2 「针对性钩子」方案 — **否定**

**用户原话**：

> "SuAPI 提供事件这提供的方法是要通用的方法，不是我要攻击力，我就加一个，然后下一个 Mod 不需要这个攻击力，他要防御力，再加一个，就变成全片都是事件了"

**否决理由**：仍是「一个个具体钩子点」的穷举，每来一个新需求就要加一个事件。

**已废弃的具体内容**：

- ❌ `AttackEventData` / `InjureEventData` / `EditBlockEventData`
- ❌ `ComponentMiner.AttackBody` 插入事件
- ❌ `ComponentHealth.Injure` 插入事件

### 12.3 「新增 `MemberSubsystemTemplate`」（§10）— **降级为补充**

**不是否决，但不够**：它能让多个实例共存，但**原版仍会执行**。

| 场景 | 是否适用 |
|---|---|
| 纯附加（如记日志） | ✅ 适用 |
| **替换（如攻击力 1→2）** | ❌ 不适用（会叠加：1+2=3） |

**结论**：保留作为「附加场景」的补充手段，主方案是 §11。

### 12.4 「`Reflection.Emit` 运行时生成代理」— **未评估**

**思路**：运行时动态生成继承 `Game.SubsystemXxx` 的代理类型，避免手写 108 个类。

**技术前提（已核实）**：

- 桌面：`PublishTrimmed=False`、AOT 未启用 ✅
- Android：`RunAOTCompilation=False`、`PublishTrimmed=False` ✅
- 仓库已在用 `Reflection.Emit`（`EntitySystem/SuAPI/ModParentField.cs:5`、`ModParentMethod.cs:5`）✅

**但**：这仍然属于「分发器」思路的一个实现变体（全局建 108 个代理），
若分发器方案本身被否决，这个变体同样不成立。

**状态**：随 §12.1 一并否决。

### 12.5 「同名 Member」方案 — **已否决（技术上不可能）**

**曾提出的思路**：保留原版同名 `Member` 并把 `Class` 置空，再新增一条同名 `Member` 指向 Mod 的类。

**用户质疑（完全正确）**：

> 「Member "TimeOfDay" Class="" → 不创建（原版被禁用）
> Member "TimeOfDay" Class="ModA.MyTimeOfDay" → 创建 ModA 的实例，这不是都两条了吗」

**为什么不可能**：

| 层 | 数据结构 | 同名能否共存 |
|---|---|---|
| `DatabaseObject.InternalNestingChildren` | `List`，但 `NestingParent` setter 有重名检查 | ❌ **添加时直接抛异常** |
| `ValuesDictionary` | `Dictionary<string, object>` | ❌ **同名直接覆盖** |

```csharp
// DatabaseObject.cs:176-179 —— 设置 NestingParent 时检查重名
if (value.FindExplicitNestedChild(Name, null, directChildrenOnly: true, throwIfNotFound: false) != null)
{
    throw new InvalidOperationException(
        "Another database object with name \"" + Name + "\" is already nested in database object \"" + value.Name + "\".");
}

// ValuesDictionary.cs:120 —— 同名直接覆盖
SetValue(effectiveNestingChild.Name, valuesDictionary);
```

**要让同名共存**，需要改 `DatabaseObject.cs`（去重名检查）+ `ValuesDictionary.cs`（改键名策略）
+ `Project.cs`（3 处）—— **共 5 处核心改动，已否决。**

### 12.6 当前唯一有效方案

**§11：用 `EventPriority` 表达占位顺序 + `Register` 自动降级为附加**

| 项 | 内容 |
|---|---|
| 引擎核心 | **零改动**（`Project.cs` / `DatabaseObject.cs` / `ValuesDictionary.cs` 都不动） |
| SuAPI 改动 | `ModInjector` ~25 行 + `ModLoader` 3 行 + 新增 `SubsystemRegistration.cs` ~100 行 |
| 只装单个 Mod | ✅ 它自己占正位，原版不创建 |
| 多 Mod 同时装 | ✅ 先执行者占正位，后执行者自动降级为附加 |
| 原版 | ❌ 不创建（正位被顶替） |
| Mod 侧 | 只写 `Register`，**不需要额外参数** |

## 13. 实施门禁

> **用户口径**："全部实现路径完全齐全了才允许实现。"

**当前状态**：§11 方案已定，但 §11.12 的 5 个细节待确认。

**允许开始实现的条件**：以下 5 项全部有确定答案。

| # | 待确认细节 | 影响 | 状态 |
|---|---|---|---|
| 1 | `ModInjector` 如何知道「当前是哪个 Mod 在 `Register`」 | 降级日志能否定位到 Mod | ⏳ |
| 2 | 降级后 Member 名字后缀格式（`__ModB` / `_ModB`） | 名字唯一性 | ⏳ |
| 3 | 同一 Mod 重复 `Register` 同名位置是否幂等 | 健壮性 | ⏳ |
| 4 | `AddSubsystem` 添加时 `DatabaseObject` 重名检查如何绕过 | 必须用不同名 | ⏳ |
| 5 | 降级发生时 `m_pendingDatabase` 何时可用 | 时序问题 | ⏳ |

### 13.1 关键前置事实（已核实）

| 事实 | 位置 | 用途 |
|---|---|---|
| `EventPriority.HIGHEST=0`，升序排序 → 最先执行 | `EventPriority.cs` / `ModEventBus.cs:67` | 占位顺序靠它 |
| `Register` 按**原始类名精确匹配** | `ModInjector.cs:152` | 第二个 Mod 无法匹配（`Class` 已改） |
| `_injectionRecords` 是**全局共享列表** | `ModInjector.cs:36` | 所有 Mod 的 `Register` 进同一列表 |
| `Apply` 遍历所有记录 | `ModInjector.cs:145-152` | 一次 `Apply` 应用所有 |
| `DatabaseObject` 同级不允许重名 | `DatabaseObject.cs:176-179` | 降级必须改名 |
| `ValuesDictionary` 用 `Dictionary` 存 | `ValuesDictionary.cs:12` | 同名会覆盖 |
| `Project.LoadSubsystem` 按名字解析 `Dependencies` | `Project.cs:363-389` | 只有一个名字能被依赖 |
| `FindSubsystem<T>` 返回**第一个** | `Project.cs:92-107` | 降级者的实例引擎可能不调用 |
| `ModLoader.OnLoad` 三处调用点 | `ModLoader.cs:36/:268/:446` | 记录当前 Mod 名的插入点 |
| `Directory.GetFiles` 顺序不保证 | `ModLoader.cs:59-60` | 不能靠文件名排序决定占位 |


## 14. 两套方案的改造对照（2026-11-09 定：两者都要）

> 用户确认："我也有原版要工作的需求啊"、"这两套方案都要，我后续要重点讨论这两个方法所需要进行的改造"

### 14.1 两套方案的定位

| # | 方案 | 语义 | 原版 |
|---|---|---|---|
| **A** | **附加** | 「我要加功能，但不改变原行为」 | ✅ **继续执行** |
| **B** | **替换** | 「我要改行为，原版不许执行」 | ❌ **不执行** |

**判定标准**：问一句「原版那部分逻辑还要不要跑？」

| 需求例子 | 原版要不要跑 | 用哪个 |
|---|---|---|
| 攻击时记录日志 | ✅ 要（还是要扣原版的 1 血） | **A** |
| 攻击时播放音效 | ✅ 要 | **A** |
| 攻击力 1 → 2 | ❌ 不要（不能再扣 1） | **B** |
| 伤害翻倍 | ❌ 不要 | **B** |

### 14.2 方案 A（附加）需要的改造

#### A-1. 引擎改动：**零**

新增 `MemberSubsystemTemplate` 是纯数据库操作，引擎已经会遍历它并实例化。

#### A-2. SuAPI 新增

| 文件 | 内容 | 估算 |
|---|---|---|
| `Survivalcraft/SuAPI/SubsystemRegistration.cs` | `AddSubsystem(database, originalMemberName, className, memberSuffix)` | ~100 行 |

#### A-3. 数据库层面要做的事

```
1. 找到原版 MemberSubsystemTemplate（按 Name），取其 NestingParent（= "Project"）
2. 找到它对应的 SubsystemTemplate，用于继承 Dependencies
3. 新建 SubsystemTemplate（名字 = 原名 + "__" + suffix）
4. 新建 Parameter Name="Class" Value=className
5. 新建 MemberSubsystemTemplate（名字 = 原名 + "__" + suffix），挂到 Project 下
```

**名字必须不同**（`DatabaseObject.cs:176` 不允许同级重名）。

#### A-4. 生效范围（关键边界）

| 引擎访问方式 | 是否生效 |
|---|---|
| `FindSubsystems<T>()` 遍历（`IUpdateable`、`SubsystemBlockBehavior`） | ✅ **生效** |
| `FindSubsystem<T>()` 单引用（`SubsystemTerrain`、`ComponentMiner`） | ⚠️ **实例被创建和 `Load`，但引擎不会调用它** |

**原因**（`Project.cs:92-107`）：返回第一个匹配就停。

#### A-5. Mod 侧需要做的事

| 步骤 | 内容 |
|---|---|
| 1 | 写自己的类，继承引擎基类 |
| 2 | 在 `GameDatabase.GameDatabase` 事件里调 `AddSubsystem(...)` |
| 3 | 固定自己的 GUID |

### 14.3 方案 B（替换）需要的改造

#### B-1. 引擎核心改动：**零**

**关键**：不新增同名 Member，而是用现有 `Register` 改 `Class` 值。

#### B-2. SuAPI 改动

| # | 文件 | 改动 | 行数 |
|---|---|---|---|
| 1 | `EntitySystem/SuAPI/ModInjector.cs` | `Register` 加占位检测 + 自动降级 | ~25 行 |
| 2 | `EntitySystem/SuAPI/ModLoader.cs` | 3 处记录当前 Mod 名 | 3 行 |
| 3 | `Survivalcraft/SuAPI/SubsystemRegistration.cs` | 降级时调用（与 A 共用） | — |

#### B-3. 机制

```csharp
// ModA（EventPriority.HIGHEST，先执行）
modInjector.Register("Game.SubsystemTimeOfDay", "ModA.MyTimeOfDay");
  → 位置无人占 → 登记，标记已占

// ModB（EventPriority.NORMAL，后执行）
modInjector.Register("Game.SubsystemTimeOfDay", "ModB.MyTimeOfDay");
  → 检测到位置已被 ModA 占 → 自动降级
  → 新增 Member "TimeOfDay__ModB" Class="ModB.MyTimeOfDay"
  → 写 Log.Information 说明降级
```

**数据库结果**：

```
Member "TimeOfDay"        Class="ModA.MyTimeOfDay"      ← 正位
Member "TimeOfDay__ModB"  Class="ModB.MyTimeOfDay"      ← 附加
```

**引擎结果**：原版不创建（正位被顶替）、ModA 执行、ModB 执行 ✅

#### B-4. 只装单个 Mod 时

```csharp
// 只装 ModB
modInjector.Register("Game.SubsystemTimeOfDay", "ModB.MyTimeOfDay");
  → 位置无人占 → 直接占正位
```

**结果**：ModB 占正位、原版不创建 ✅ **不需要任何额外参数**。

#### B-5. Mod 侧需要做的事

| 步骤 | 内容 |
|---|---|
| 1 | 写自己的类，**继承原版类**（如 `Game.SubsystemTimeOfDay`） |
| 2 | 订阅 `GameDatabase.GameDatabase`，回调里调 `Register` |
| 3 | 用 `EventPriority` 表达「我要多早占位」 |
| 4 | **不用管** outros Mod（SuAPI 自动降级） |

### 14.4 两套方案的改造量对照

| 项 | 方案 A（附加） | 方案 B（替换） |
|---|---|---|
| **引擎核心改动** | ❌ 零 | ❌ **零** |
| **SuAPI 改动** | 新增 1 文件 ~100 行 | `ModInjector` ~25 行 + `ModLoader` 3 行 |
| **数据库操作** | 新增 Member（**不同名**） | 改原版 `Class` 值 + 落选者新增 Member（不同名） |
| **Mod 继承什么** | 引擎基类 | **原版类** |
| **原版执行** | ✅ | ❌ |
| **生效范围** | 仅遍历多实例处 | 正位全生效；降级者同 A |
| **多 Mod 冲突** | 天然无冲突 | 用 `EventPriority` 表达先后 |

### 14.5 两套方案能否同时用

**能，且互不干扰。**

```
ModA 用 B（HIGHEST）：
  Member "TimeOfDay"        Class="ModA.MyTimeOfDay"      ← 正位

ModB 用 B（NORMAL）：
  Member "TimeOfDay__ModB"  Class="ModB.MyTimeOfDay"      ← 自动降级

ModC 用 A（不需要 EventPriority）：
  Member "TimeOfDay__ModC"  Class="ModC.TimeLogger"       ← 主动附加

结果：
  ModA 执行（占正位）
  ModB 执行（降级附加）
  ModC 执行（主动附加）
  原版不执行
```

### 14.6 待讨论的开放问题

| # | 问题 | 影响 |
|---|---|---|
| **Q1** | `AddSubsystem` 的 `memberSuffix` 由谁定（SuAPI 自动 / Mod 指定） | 名字唯一性 |
| **Q2** | `ModInjector` 如何知道「当前是哪个 Mod 在 `Register`」 | 降级日志定位 |
| **Q3** | 降级发生时 `Database` 是否已可用（`Register` 时可能还没有） | 时序问题 |
| **Q4** | 同一 Mod 重复 `Register` 同名位置是否幂等 | 健壮性 |
| **Q5** | 降级者若用 `FindSubsystem<T>()` 拿不到，是否需要额外机制 | 功能完整度 |
| **Q6** | 组件是否同样需要 A/B 两套 | 覆盖范围 |
| **Q7** | 纯附加场景（ModC）是否需要 `EventPriority` | 是否需要 |

### 14.7 实施门禁

> 用户口径："全部实现路径完全齐全了才允许实现。"

**当前状态**：两套方案的改造点已列清（§14.2 - §14.4），但 §14.6 的 Q1-Q7 待讨论。

**允许开始实现的条件**：Q1-Q7 全部有确定答案。

| # | 问题 | 状态 |
|---|---|---|
| Q1 | `memberSuffix` 由谁定 | ⏳ |
| Q2 | 当前 Mod 名的获取方式 | ⏳ |
| Q3 | 降级时的 `Database` 可用性 | ⏳ |
| Q4 | 重复 `Register` 幂等性 | ⏳ |
| Q5 | 降级者的 `FindSubsystem<T>()` 限制 | ⏳ |
| Q6 | 组件是否需要 A/B 两套 | ⏳ |
| Q7 | 纯附加场景是否需要 `EventPriority` | ⏳ |


## 15. 责任链（Chain）机制（2026-11-09 定）

> 用户口径：
> - "B和C都是用 `modInjector.Register(...)` 的吗"
> - "我让你记到文档里面，你真不记啊，如果 C 仍写 Register，然后 SuAPI 给他自动降级。"

### 15.1 要解决的问题

多个 Mod 都写了自己的类，都要调 `base.Update(dt)`：

```csharp
// ModB
public class SuComponentLocomotionB : Game.ComponentLocomotion
{
    public override void Update(float dt)
    {
        SyncPositionToClients();
        if (SuAPI.Chain.IsLast("ComponentLocomotion.Update", this))
            base.Update(dt);
    }
}

// ModC —— 结构完全相同
public class SuComponentLocomotionC : Game.ComponentLocomotion
{
    public override void Update(float dt)
    {
        RecordTravelledDistance(dt);
        if (SuAPI.Chain.IsLast("ComponentLocomotion.Update", this))
            base.Update(dt);
    }
}
```

**问题**：`Class` 参数是单值，只能指向一个类。另一个类永远不会被实例化。

### 15.2 核心设计：**最后一个实例调 `base`**

```
引擎每帧（按实例创建顺序）：
  ① B.Update(dt)
       MyLogicB(dt)
       Chain.IsLast(slot, this) → false（后面还有 C）
       （跳过 base）

  ② C.Update(dt)
       MyLogicC(dt)
       Chain.IsLast(slot, this) → true（我是链尾）
       base.Update(dt)                              ← 原版只调一次
```

| 项 | 结果 |
|---|---|
| ModB 逻辑 | ✅ 执行 |
| ModC 逻辑 | ✅ 执行 |
| 原版 `Update` | ✅ **只执行一次** |
| B 知道 C 存在吗 | ❌ 不需要 |
| B/C 代码结构 | ✅ 完全相同 |

### 15.3 Mod 侧写法（**B 和 C 完全一样**）

```csharp
// ModB —— EventPriority 决定谁是正位
public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
    {
        modInjector.Register("Game.ComponentLocomotion",
                             "ModB.SuComponentLocomotionB");
        return new object[] { false, (Database)args[0] };
    }, EventPriority.HIGHEST);
}
```

```csharp
// ModC —— 完全相同，只是类名不同
public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
    {
        modInjector.Register("Game.ComponentLocomotion",
                             "ModC.SuComponentLocomotionC");
        return new object[] { false, (Database)args[0] };
    }, EventPriority.NORMAL);
}
```

**关键**：
- C **不需要知道** B 存在
- C **不需要改成** `Add`
- C **只管写** `Register`

### 15.4 SuAPI 自动降级（关键机制）

```csharp
// EntitySystem/SuAPI/ModInjector.cs
private readonly Dictionary<string, string> m_claimedPositions = new();
private readonly List<(string original, string replacement, string mod)> m_pendingAttaches = new();

public void Register(string originalClassName, string newClassName)
{
    //mod 位置已被占 → 自动降级为附加（新增独立实例，不覆盖）
    //mod 为什么：多个 Mod 各自写了自己的类，都需要实例化才能参与 Chain；
    //mod   Class 只能指一个类，所以后来者通过新增 MemberComponentTemplate /
    //mod   MemberSubsystemTemplate 的方式获得自己的实例。
    if (m_claimedPositions.TryGetValue(originalClassName, out string occupiedBy))
    {
        Log.Information("[SuAPI][slot] " + originalClassName + " 已被「" + occupiedBy +
            "」占据 → 「" + m_currentModName + "」自动降级为附加实例");
        m_pendingAttaches.Add((originalClassName, newClassName, m_currentModName));
        return;
    }
    //..mod

    Log.Information("[SuAPI][slot] 「" + m_currentModName + "」占据 " +
        originalClassName + " → " + newClassName);
    m_claimedPositions[originalClassName] = m_currentModName;

    _injectionRecords.Add(new InjectionRecord
    {
        OriginalClassName = originalClassName,
        NewClassName      = newClassName,
        ModName           = m_currentModName,      // ← 注册时就记录，不用等 Apply
        ...
    });
}
```

### 15.5 数据库结果

```
原版：
  MemberComponentTemplate "Locomotion"  Class="Game.ComponentLocomotion"

ModB（HIGHEST，先执行）：
  MemberComponentTemplate "Locomotion"  Class="ModB.SuComponentLocomotionB"   ← 正位，原版被顶替

ModC（NORMAL，后执行）：
  MemberComponentTemplate "Locomotion__ModC"  Class="ModC.SuComponentLocomotionC"   ← 自动降级的附加实例

最终：
  原版 Game.ComponentLocomotion              ❌ 不创建
  ModB.SuComponentLocomotionB                ✅ 创建
  ModC.SuComponentLocomotionC                ✅ 创建
```

### 15.6 `Chain` 实现要点

#### API

```csharp
namespace SuAPI
{
    public static class Chain
    {
        /// <summary>
        /// 返回 true = 我是链尾，调用者应执行 base；
        /// 返回 false = 后面还有同类型实例，跳过 base。
        ///
        /// 首次调用时 SuAPI 自动扫描所有同类型实例并排序，Mod 不需要登记。
        /// </summary>
        public static bool IsLast<THost>(string slot, THost instance) where THost : class;

        /// <summary>世界卸载时清空（实例会重新创建）。</summary>
        public static void Clear();
    }
}
```

#### 为什么用泛型 `IsLast<T>`

`Chain` 需要枚举同类型实例：

| 实例类型 | 枚举方式 | 是否需要反射 |
|---|---|---|
| 子系统 | `project.Subsystems` 遍历 + `IsInstanceOfType` | ❌ |
| 组件 | `entity.FindComponents<T>()` | 泛型无反射；`FindComponents(Type)` 需反射 |

**用泛型 `IsLast<T>` 可在内部直接用 `FindComponents<T>()`，零反射。**

#### 实现草案

```csharp
private static readonly object s_lock = new object();
private static readonly Dictionary<string, List<object>> s_ordered = new();

public static bool IsLast<THost>(string slot, THost instance) where THost : class
{
    if (instance == null) return true;

    List<object> ordered;
    lock (s_lock)
    {
        if (!s_ordered.TryGetValue(slot, out ordered))
        {
            ordered = BuildOrder(instance);
            s_ordered[slot] = ordered;
            Log.Information("[SuAPI][chain] " + slot + " 自动登记 " +
                ordered.Count + " 个实例");
        }
    }
    return ReferenceEquals(ordered[ordered.Count - 1], instance);
}

private static List<object> BuildOrder(object instance)
{
    var result = new List<object>();
    Project project = GetProject(instance);
    if (project == null) return result;

    Type targetType = instance.GetType();

    // ① 子系统（按 m_subsystems 的创建顺序）
    foreach (Subsystem s in project.Subsystems)
        if (targetType.IsInstanceOfType(s)) result.Add(s);

    // ② 组件（每个实体一套，见 §15.7 的作用域问题）
    foreach (Entity e in project.Entities)
        foreach (object c in e.FindComponents(targetType))
            result.Add(c);

    return result;
}

private static Project GetProject(object instance)
{
    if (instance is Subsystem s) return s.Project;
    if (instance is Component c) return c.Project;
    return null;
}
```

### 15.7 待解决：组件链的作用域

**问题**：世界里可能有 100 个角色，每个都有自己的 `ComponentLocomotion`。

```csharp
// slot = "ComponentLocomotion.Update"
// 但每个角色的 Locomotion 是独立的
// 不应该跨实体比较"谁是链尾"
```

**正确的作用域**：**同一实体内的同类组件**

```csharp
public static bool IsLast<THost>(string slot, THost instance) where THost : class
{
    if (instance is Component c)
    {
        // 只在 c.Entity 的组件里比较
        return IsLastInEntity(slot, c);
    }
    // 子系统是全局的，用 project.Subsystems
    return IsLastInProject(slot, instance);
}
```

**这会导致链的 key 变成 `(slot, entity)`**，需要重新设计缓存键。

### 15.8 已确认的接口变化

| # | 接口 | 变化 | 状态 |
|---|---|---|---|
| 1 | `IModInjector.Register` | 增加占位检测 + 自动降级为附加 | ✅ 定 |
| 2 | `IModInjector` 新增 `Add(original, newClass)` | 手动附加（不需要自动降级的场景） | ✅ 定 |
| 3 | `IModInjector` 新增 `SetCurrentMod(string)` | 供 `Register` 记录归属 | ✅ 定 |
| 4 | `SuAPI.Chain.IsLast<T>(slot, instance)` | 🆕 责任链判定 | ✅ 定 |
| 5 | `SuAPI.Chain.Clear()` | 🆕 世界卸载清理 | ✅ 定 |

### 15.9 待确认

| # | 问题 | 说明 |
|---|---|---|
| 1 | 组件链的作用域（§15.7） | 必须按实体隔离，缓存键要带 entity |
| 2 | `slot` 命名规范 | `"ComponentLocomotion.Update"` 是否够 |
| 3 | 实例销毁后缓存失效 | 需要 `Clear()` 的调用时机 |
| 4 | 链顺序 = 实例创建顺序，是否可靠 | 子系统是 `List` ✅；组件是 `List` ✅；但 `project.Entities` 是 `Dictionary.Keys` ⚠️ |


## 16. `ModInjector` 已知缺陷与修复（2026-11-09 定）

> 用户口径："ModInjector旧版有个问题，经常不知道谁去覆盖的谁，你检查一下还有没有这个问题。"

### 16.1 检查结果：**问题仍然存在，且更严重**

| # | 缺陷 | 位置 | 后果 |
|---|---|---|---|
| **P1** | `ModName` 在 `Apply` 时才赋值 | `ModInjector.cs:126-129` | **归属错误**：先 `Apply` 的 Mod 把别人的注册全算成自己的 |
| **P2** | 覆盖发生时不写日志 | 全文件只有 2 处 `Console.WriteLine`（异常用） | 无法知道谁覆盖了谁 |
| **P3** | `ChangeEntry.AppliedRecords` 有数据但不输出 | `ModInjector.cs:27-33` | 覆盖链记录了，但从没展示过 |
| **P4** | 日志只到 `Console`，不进 `Game.log` | — | 玩家/服务端都看不到 |
| **P5** | `OnOverridden` 默认 `null` | `ModInjector.cs:91` | 覆盖时什么都不会发生 |

### 16.2 P1 的实例（归属错误）

```csharp
// ModInjector.cs:126-129
public void Apply(Database database, string modName)
{
    //mod 标记未 Applied 的 Record 的 modName
    foreach (var record in _injectionRecords)
    {
        if (!record.Applied && record.ModName == null)
            record.ModName = modName;      // ← 谁先 Apply，所有未标记的记录都算这个 Mod 的！
    }
```

**推演**：

```
ModA.OnLoad → Register("Game.SubsystemTimeOfDay", "ModA.MyTimeOfDay")
             （此时 ModName = null）

ModB.OnLoad → Register("Game.SubsystemOther", "ModB.MyOther")
             （此时 ModName = null）

ModB 的 GameDatabase 回调（HIGHEST 先执行）→ Apply(db, "ModB")
  → 遍历所有记录，两条都没 Applied、ModName 都 null
  → 两条都被标记成 "ModB"      ← ModA 的记录被误记为 ModB 的
```

### 16.3 修复 1：`Register` 时就记录 Mod 名

```csharp
// EntitySystem/SuAPI/ModInjector.cs
private string m_currentModName;

public void SetCurrentMod(string modName) => m_currentModName = modName;

public void Register(string originalClassName, string newClassName)
{
    _injectionRecords.Add(new InjectionRecord
    {
        OriginalClassName = originalClassName,
        NewClassName      = newClassName,
        ModName           = m_currentModName,      // ← 注册时就确定
        ...
    });
}

// Apply 里的错误赋值删掉
public void Apply(Database database, string modName)
{
    //mod 删除：不再在这里赋 ModName（会导致归属错误）
    // foreach (var record in _injectionRecords)
    //     if (!record.Applied && record.ModName == null) record.ModName = modName;
}
```

### 16.4 修复 2：`ModLoader` 三处设置当前 Mod

```csharp
// ModLoader.cs:36 / :268 / :446
//mod 记录当前 Mod，供 ModInjector 正确归属 Register 调用
if (modInjector is ModInjector concrete)
    concrete.SetCurrentMod(mod.Name);
mod.OnLoad(eventBus, modInjector);
//..mod
```

### 16.5 修复 3：占位与降级都写日志

```csharp
public void Register(string originalClassName, string newClassName)
{
    //mod 占位检测：位置已被占 → 自动降级为附加
    if (m_claimedPositions.TryGetValue(originalClassName, out string occupiedBy))
    {
        Log.Information("[SuAPI][slot] " + originalClassName + " 已被「" + occupiedBy +
            "」占据 → 「" + m_currentModName + "」自动降级为附加实例");
        m_pendingAttaches.Add((originalClassName, newClassName, m_currentModName));
        return;
    }

    Log.Information("[SuAPI][slot] 「" + m_currentModName + "」占据 " +
        originalClassName + " → " + newClassName);
    m_claimedPositions[originalClassName] = m_currentModName;
    //..mod
    ...
}
```

### 16.6 修复 4：`Apply` 后输出完整覆盖链

```csharp
// Apply 末尾
//mod 输出覆盖链：多 Mod 改同一个参数时能看出谁覆盖了谁
foreach (var kv in _changeTracker)
{
    var entry = kv.Value;
    if (entry.AppliedRecords.Count > 1)           // 发生过多 Mod 覆盖
    {
        var chain = string.Join(" → ", entry.AppliedRecords.Select(
            r => r.ModName + "(" + r.NewClassName + ")"));
        Log.Information("[SuAPI][inject] 参数 " + entry.ParameterGuid +
            " 发生多 Mod 覆盖：原始=" + entry.OriginalValue +
            "，链=" + chain + "，最终=" + entry.CurrentValue);
    }
}
//..mod
```

### 16.7 修复 5：日志进 `Game.log`

全部改用 `Log.Information` / `Log.Warning`（`Engine/Log.cs`），不用 `Console.WriteLine`。

### 16.8 修复后的日志效果

```
[SuAPI][slot] 「ModA」占据 Game.SubsystemTimeOfDay → ModA.MyTimeOfDay
[SuAPI][slot] Game.SubsystemTimeOfDay 已被「ModA」占据 → 「ModB」自动降级为附加实例
[SuAPI][slot] 「ModC」占据 Game.ComponentLocomotion → ModC.SuComponentLocomotionC
[SuAPI][chain] ComponentLocomotion.Update 自动登记 2 个实例
[SuAPI][inject] 参数 1e884b27-... 发生多 Mod 覆盖：
    原始=Game.SubsystemTimeOfDay，链=ModA(ModA.MyTimeOfDay)，最终=ModA.MyTimeOfDay
```

**每一项都能看出：谁、占了什么、从什么改成什么、谁被降级了、链上有几个实例。**

### 16.9 修复清单汇总

| # | 文件 | 改动 | 行数 |
|---|---|---|---|
| 1 | `EntitySystem/SuAPI/ModInjector.cs` | `SetCurrentMod` + 占位检测 + 降级 + 覆盖链日志 + 删除错误赋值 | ~40 |
| 2 | `EntitySystem/SuAPI/ModLoader.cs` | 3 处 `SetCurrentMod` | 3 |
| 3 | `EntitySystem/SuAPI/IModInjector.cs` | 暴露 `SetCurrentMod` + `Add` | ~10 |
