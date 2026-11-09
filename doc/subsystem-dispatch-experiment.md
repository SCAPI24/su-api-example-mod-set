# SuAPI 子系统分发器 —— 最小可行实验指南

> 目的：**用一个最小案例验证核心机制是否成立**，再决定是否推广到 110 个子系统
> 前置文档：`doc/subsystem-dispatch-plan.md`（设计、机制、全量挂点清单）
> 状态：**待实验**

---

## 0. 实验要验证什么

只需验证 **4 个命题**，全部成立才算方案可行：

| # | 命题 | 失败的表现 |
|---|---|---|
| **P1** | SuAPI 能独占 `Class` 参数位置 | 引擎启动崩（找不到类）或原版逻辑失效 |
| **P2** | Mod 订阅事件后，其代码**被执行** | 事件从未触发 |
| **P3** | **两个 Mod 同时**订阅都能执行 | 只有一个生效（回到原问题） |
| **P4** | Mod 能访问到它需要的一切（`Project` / 父类成员） | 拿不到 `SubsystemWeather` 或私有字段 |

**P3 是整个方案的唯一目的** —— 如果只能一个 Mod 生效，方案就没有意义。

---

## 1. 实验对象选择

**建议：`SubsystemTimeOfDay`**（不是 `MemoryBankDrawMod`）

| 理由 | 说明 |
|---|---|
| 原类**不实现 `IUpdateable`** | 分发器加 `IUpdateable` 是纯新增，不与原版冲突 |
| 原版 `Load` 简单 | 只有 2 个 `FindSubsystem` + 1 个读值 |
| 有现成真实 Mod 可参照 | `RainWithoutDawn` 的 `RWDSubsystemTimeOfDay` 就是改它 |
| 两个 Mod 的需求**正交** | 一个改天气、一个改时间，天然不冲突（适合验证 P3） |

### 关键前提（已核实）

```csharp
// Survivalcraft/Game/SubsystemTimeOfDay.cs:8
public class SubsystemTimeOfDay : Subsystem        // ← 不实现 IUpdateable
{
    private SubsystemGameInfo m_subsystemGameInfo;  // private
    private SubsystemSeasons m_subsystemSeasons;    // private
    public double TimeOfDayOffset { get; set; }     // public，可直接改
    public float TimeOfDay { get; }                 // public
    public float Midnight { get; }                  // public
    public double CalculateDay(double)              // public
    public float CalculateTimeOfDay(double)         // public

    protected override void Load(ValuesDictionary vd) { ... }
}
```

| 项 | 值 |
|---|---|
| `Dependencies` | `Seasons`（`Pak/Database.xml:3683`） |
| `Class` 参数 | `Game.SubsystemTimeOfDay`（`Pak/Database.xml:3681`） |
| 是否实现 `IUpdateable` | ❌ **不实现**（所以分发器要自己加） |
| 是否有 `Update` 方法 | ❌ **没有**（所以不需要调 `base.Update`） |

---

## 2. 实验步骤总览

```
步骤 1：写 SuAPI 分发器类
    ↓
步骤 2：SuAPI 在 GameDatabase 事件里抢占 Class 参数
    ↓
步骤 3：验证 P1（引擎能启动、原版时间正常）
    ↓
步骤 4：写 ModA（只订阅 Load，打日志）
    ↓
步骤 5：验证 P2（日志出现）
    ↓
步骤 6：写 ModB（同样只订阅 Load，打日志）
    ↓
步骤 7：验证 P3（两条日志都出现）
    ↓
步骤 8：让 ModA 改天气、ModB 改时间
    ↓
步骤 9：验证 P4（功能都生效）
```

**前 7 步只打日志，不改游戏行为** —— 这样能干净地隔离"机制是否成立"和"功能是否正确"。

---

## 3. 步骤 1：SuAPI 分发器类

### 放在哪

**`EntitySystem/SuAPICore/`** 下（SuAPI Core 是内置 Mod，所有端都有）。

⚠️ **不要**放到某个外部 Mod —— 否则那个 Mod 没装时，`Class` 指向的类找不到 → **游戏启动崩溃**。

### 代码

```csharp
// EntitySystem/SuAPICore/Dispatch/SubsystemTimeOfDay.cs
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace SuAPI.Dispatch
{
    /// <summary>
    /// 替换 Game.SubsystemTimeOfDay 的分发器。
    ///
    /// 职责只有一个：占住 Class 参数位置，把引擎的调用时刻转发成事件。
    /// **不预设任何 Mod 需求** —— Mod 需要什么，从 args[0]（本实例）上自己取。
    /// </summary>
    public class SubsystemTimeOfDay : Game.SubsystemTimeOfDay, IUpdateable
    {
        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);          // 原版初始化照常跑
            Fire("Load", valuesDictionary);
        }

        protected override void Save(ValuesDictionary valuesDictionary)
        {
            Fire("Save", valuesDictionary);
            base.Save(valuesDictionary);
        }

        public void Update(float dt)
        {
            Fire("Update", dt);
        }

        private void Fire(string operation, object payload)
        {
            ModManager.Instance?.ModEventBus?.TriggerEvent(
                "SuAPI.SubsystemTimeOfDay." + operation,
                new object[] { this, payload });
        }
    }
}
```

### 注意事项

| 项 | 说明 |
|---|---|
| `base.Load(vd)` 必须调 | 否则 `m_subsystemGameInfo` / `m_subsystemSeasons` 为 null，`TimeOfDay` 取值会 NRE |
| `Update` **不能**调 `base.Update` | 父类没有 `Update` 方法 |
| `UpdateOrder` 默认值 | `UpdateOrder.Default`（= 0）。若与其他子系统有顺序要求需调整 |
| 事件名 | `SuAPI.SubsystemTimeOfDay.Load` / `.Update` / `.Save` |

---

## 4. 步骤 2：抢占 Class 参数

### 在哪抢

**`EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs` 的 `OnLoad`**

```csharp
public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    m_eventBus = eventBus;

    // ── 新增：抢占子系统 Class 参数 ──
    modInjector.Register(
        "Game.SubsystemTimeOfDay",                  // Original
        "SuAPI.Dispatch.SubsystemTimeOfDay");       // New = 分发器

    // ── 新增：在 GameDatabase 事件里 Apply ──
    eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
    {
        modInjector.Apply((Database)args[0], "SuAPI");
        return new object[] { false, args[0] };     // false = 不改 Database 引用
    }, EventPriority.HIGHEST);                      // ← HIGHEST：最早执行

    // ...原有的 Windows/Linux 窗口逻辑...
}
```

### 为什么必须 `HIGHEST`

`ModInjector.Apply` 的匹配是**精确字符串**（`ModInjector.cs:152`）：

```csharp
if (currentValue != record.OriginalClassName) continue;
```

如果别的 Mod 先改了这个位置，SuAPI 就匹配不上 `Game.SubsystemTimeOfDay` 了。

### 返回值说明

```csharp
return new object[] { false, args[0] };
//                     ↑     ↑
//                     │     └─ Database 引用（false 时引擎不用它）
//                     └─ false = 不替换 Database（我们只改了 Value，不用换对象）
```

---

## 5. 步骤 3：验证 P1（引擎能启动）

### 检查清单

| # | 检查项 | 怎么查 |
|---|---|---|
| 1 | 游戏能正常启动 | 无崩溃 |
| 2 | `Class` 参数确实被改了 | 日志或断点（见下） |
| 3 | 原版时间正常流动 | 进世界看日出日落 |
| 4 | 没有 `Type not found` 报错 | `Game.log` |

### 加临时诊断（验证完删除）

```csharp
// SuAPICoreMod.OnLoad 里临时加
eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
{
    modInjector.Apply((Database)args[0], "SuAPI");

    // 临时诊断：确认替换成功
    var db = (Database)args[0];
    var param = db.FindDatabaseObject(
        new System.Guid("1e884b27-fc1d-486a-bd0e-518e554b9734"),   // TimeOfDay 的 Class 参数 GUID
        db.FindDatabaseObjectType("Parameter", true), false);
    Log.Information("[SuAPI-实验] TimeOfDay.Class = " + (param?.Value ?? "<null>"));

    return new object[] { false, args[0] };
}, EventPriority.HIGHEST);
```

**预期日志**：`[SuAPI-实验] TimeOfDay.Class = SuAPI.Dispatch.SubsystemTimeOfDay`

> ⚠️ 诊断日志用 `[SuAPI-实验]` 前缀，**验证完立即删除**（AGENTS.md 要求）。

### 如果 P1 失败

| 现象 | 原因 | 修法 |
|---|---|---|
| 启动崩溃 `Type "SuAPI.Dispatch.SubsystemTimeOfDay" not found` | 分发器类不在已加载程序集里 | 确认在 SuAPICore 工程内、且 SuAPICore 先于 GameDatabase 加载 |
| 时间不动 | `Load` 里没调 `base.Load` | 补上 |
| `NullReferenceException` in `TimeOfDay` getter | `m_subsystemGameInfo` 为 null | 同上 |

---

## 6. 步骤 4-5：验证 P2（Mod 能被调用）

### 测试 Mod（先只打日志）

```csharp
namespace TestMod
{
    public class TestModA : IMod
    {
        public string Name => "TestModA";
        public string Version => "0.0.1";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            eventBus.SubscribeEvent("SuAPI.SubsystemTimeOfDay.Load", args =>
            {
                Log.Information("[TestModA] Load 被调用！");
                return new object[] { false };
            }, EventPriority.NORMAL);

            eventBus.SubscribeEvent("SuAPI.SubsystemTimeOfDay.Update", args =>
            {
                // 每帧触发，别打日志（会刷屏），用计数器
                return new object[] { false };
            }, EventPriority.NORMAL);
        }

        public void OnUnload() { }
    }
}
```

### 检查清单

| # | 检查项 | 预期 |
|---|---|---|
| 1 | `[TestModA] Load 被调用！` 出现在日志 | ✅ 一次（世界加载时） |
| 2 | 进世界后时间正常流动 | ✅ 说明分发器的 `Update` 被驱动 |

> ⚠️ `Update` 每帧都会触发，**绝对不要在 Update 里打日志**（20 FPS × 每秒 = 刷屏）。
> 用静态计数器 + 每 N 秒打一次。

---

## 7. 步骤 6-7：验证 P3（两个 Mod 同时生效）—— **最关键**

### 再写一个 TestModB

与 TestModA **完全相同的代码**，只改名字和日志前缀：

```csharp
Log.Information("[TestModB] Load 被调用！");
```

### 检查清单

| # | 预期日志 | 说明 |
|---|---|---|
| 1 | `[TestModA] Load 被调用！` | ✅ |
| 2 | `[TestModB] Load 被调用！` | ✅ |
| 3 | **两条都出现** | 🎯 **这就是 P3 成立的证据** |

### 如果 P3 失败

| 现象 | 原因 | 修法 |
|---|---|---|
| 只有一条 | 第二个 Mod 没加载 | 检查 `.scmod` 是否都在 `Mods/` |
| 只有一条 | 事件名拼错 | 两边核对字符串 |
| 一条都没有 | 分发器没被实例化 | 回步骤 3 查 P1 |

---

## 8. 步骤 8-9：验证 P4（Mod 能拿到所需资源）

### TestModA：只改天气

```csharp
public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    SubsystemWeather m_weather = null;

    eventBus.SubscribeEvent("SuAPI.SubsystemTimeOfDay.Load", args =>
    {
        var host = (Game.SubsystemTimeOfDay)args[0];

        // 用户口径：Load 时机不保证对方已 Load 完 → 自己判空，不要 throwOnError: true
        m_weather = host.Project.FindSubsystem<SubsystemWeather>(false);
        Log.Information("[TestModA] Weather = " + (m_weather != null ? "OK" : "null"));
        return new object[] { false };
    }, EventPriority.NORMAL);

    eventBus.SubscribeEvent("SuAPI.SubsystemTimeOfDay.Update", args =>
    {
        var host = (Game.SubsystemTimeOfDay)args[0];
        if (m_weather == null)
        {
            m_weather = host.Project.FindSubsystem<SubsystemWeather>(false);   // 迟到的补拿
            if (m_weather == null) return new object[] { false };
        }
        if (!m_weather.IsPrecipitationStarted) m_weather.ManualPrecipitationStart();
        if (!m_weather.IsFogStarted)           m_weather.ManualFogStart();
        return new object[] { false };
    }, EventPriority.NORMAL);
}
```

### TestModB：只改时间（用 SuAPI 工具读私有字段）

```csharp
public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    IModParentField parentField = modInjector.ModParentField;   // ← SuAPI 已有工具
    SubsystemGameInfo m_gameInfo = null;

    eventBus.SubscribeEvent("SuAPI.SubsystemTimeOfDay.Load", args =>
    {
        var host = (Game.SubsystemTimeOfDay)args[0];
        m_gameInfo = parentField.GetParentField<SubsystemGameInfo>(
            host, "m_subsystemGameInfo");
        Log.Information("[TestModB] GameInfo = " + (m_gameInfo != null ? "OK" : "null"));
        return new object[] { false };
    }, EventPriority.NORMAL);

    eventBus.SubscribeEvent("SuAPI.SubsystemTimeOfDay.Update", args =>
    {
        var host = (Game.SubsystemTimeOfDay)args[0];
        if (m_gameInfo == null) return new object[] { false };

        // 强制跳到午夜（RainWithoutDawn 的原逻辑）
        if (IsNotMidnight(host))
        {
            float timeToMidnight = IntervalUtils.Interval(
                host.CalculateTimeOfDay(m_gameInfo.TotalElapsedGameTime),
                host.Midnight - 0.09f);
            host.TimeOfDayOffset += timeToMidnight;
        }
        return new object[] { false };
    }, EventPriority.NORMAL);
}

private static bool IsNotMidnight(Game.SubsystemTimeOfDay host) =>
    !(IntervalUtils.IsBetween(host.TimeOfDay, host.Midnight - 0.1f, host.Midnight)
   || IntervalUtils.IsBetween(host.TimeOfDay, 0f, 0.1f));
```

### 检查清单

| # | 检查项 | 预期 |
|---|---|---|
| 1 | `[TestModA] Weather = OK` | ✅ |
| 2 | `[TestModB] GameInfo = OK` | ✅（证明 `IModParentField` 能读私有字段） |
| 3 | **游戏里一直下雨+起雾** | ✅ ModA 生效 |
| 4 | **时间被强制跳到午夜** | ✅ ModB 生效 |
| 5 | **两个效果同时存在** | 🎯 **P4 + P3 联合验证** |

---

## 9. 实验记录表（执行时填写）

| 步骤 | 验证项 | 结果 | 备注 |
|---|---|---|---|
| 1 | 分发器类编译进 SuAPICore | ☐ | |
| 2 | `Register` + `Apply` 已加 | ☐ | |
| P1 | 游戏能启动 | ☐ | |
| P1 | `TimeOfDay.Class` 已变成分发器 | ☐ | 日志： |
| P1 | 原版时间正常流动 | ☐ | |
| P2 | TestModA 的 Load 被调用 | ☐ | |
| P3 | TestModA + TestModB 都被调用 | ☐ | 🎯 |
| P4 | ModA 能拿到 `SubsystemWeather` | ☐ | |
| P4 | ModB 能拿到 `m_subsystemGameInfo` | ☐ | |
| P4 | 下雨+起雾 与 强制午夜 同时生效 | ☐ | 🎯 |

---

## 10. 已知风险（实验前必读）

| # | 风险 | 影响 | 应对 |
|---|---|---|---|
| **R1** | `TypeCache.FindType` 找不到分发器类 | **游戏启动崩溃** | 分发器必须在 SuAPICore 工程内，且 SuAPICore 早于 `GameDatabase` 构造加载 |
| **R2** | 分发器的 `Update` 每帧触发事件 | 性能开销 | 实验阶段 Mod 里**不要**在 Update 打日志；用计数器 |
| **R3** | `Load` 时机不保证对方子系统已 `Load` | `FindSubsystem` 拿到实例但字段为 null | **Mod 自己判空**（用户口径：不改 SuAPI 保证顺序） |
| **R4** | `TriggerEvent` 吞异常 | Mod 出错**无任何可见报错** | 实验阶段 Mod 回调里自己 try/catch + 打日志 |
| **R5** | 事件名硬编码字符串 | 拼错则静默失效 | 建议 SuAPI 提供常量 |
| **R6** | `UpdateOrder.Default` 可能与其它子系统顺序冲突 | 时间推进顺序异常 | 若异常，改 `UpdateOrder` 试探 |

### R4 补充：如何看到 Mod 里的异常

`ModEventBus.TriggerEvent` 的实现（`ModEventBus.cs:88-100`）：

```csharp
catch (Exception ex)
{
    Console.WriteLine($"事件处理异常: {ex.Message}");   // ← 只到 Console，不进 Game.log
    results.Add(new object[] { $"Error: {ex.Message}" });
}
```

**所以实验阶段每个 Mod 回调都应包裹 try/catch 并写 `Log.Error`**，否则出错时完全看不到。

---

## 11. 实验成功后

如果 P1~P4 全部通过：

1. **删除所有临时诊断代码和测试 Mod**
2. 把 `SubsystemTimeOfDay` 的分发器**正式落地**（去掉诊断）
3. 按 `subsystem-dispatch-plan.md` §4.5 的挂点表，逐步推广到其它子系统
4. **优先推广被抢过的**：`Game.SubsystemElectricity`（见文档 §6）

### 实验中发现的问题要回写

| 发现 | 回写到 |
|---|---|
| 某个机制与文档不符 | `subsystem-dispatch-plan.md` 对应章节 |
| 新的风险 | 本文 §10 |
| `HandledBlocks` 等硬伤的实际影响 | `subsystem-dispatch-plan.md` §7 待确认项 |

---

## 12. 附：不需要实验就能确定的事（已核实）

| 事实 | 位置 |
|---|---|
| `SubsystemTimeOfDay` 不实现 `IUpdateable` | `Survivalcraft/Game/SubsystemTimeOfDay.cs:8` |
| `SubsystemTimeOfDay.Load` 内容 | 同上 `:89-96` |
| `TimeOfDay` 的 Class 参数 GUID | `1e884b27-fc1d-486a-bd0e-518e554b9734`（`Pak/Database.xml:3681`） |
| `TimeOfDay` 的 Dependencies | `Seasons`（`Pak/Database.xml:3683`） |
| `Apply` 是精确字符串匹配 | `EntitySystem/SuAPI/ModInjector.cs:152` |
| `TriggerEvent` 多订阅、吞异常 | `EntitySystem/SuAPI/ModEventBus.cs:56-104` |
| `IModParentField.GetParentField<T>` 可用 | `EntitySystem/SuAPI/IModParentField.cs:60` |
| `IUpdateable` 自动登记 | `Survivalcraft/Game/SubsystemUpdate.cs:107-113` |
| `Subsystem.Project` 是 public | `EntitySystem/GameEntitySystem/Subsystem.cs:12` |
| `Subsystem.Load` 是 `protected internal virtual` | 同上 `:24` |

---

## 13. 一句话总结

**实验只做一件事：证明两个 Mod 订阅同一事件后都能执行。**

P1（能启动）、P2（能触发）、P4（能拿资源）都是已知可行的，
**只有 P3（两个同时生效）是真正需要验证的** —— 它是整个方案的唯一价值。
