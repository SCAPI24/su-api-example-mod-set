# StringInterceptor

字符串拦截器 Mod — 演示如何拦截游戏中所有英文文本字符串，通过回调接口实现自定义处理（如翻译、编号等）。

## 功能

- **StringsManager.m_strings** — 拦截游戏本地化字典，一次性处理所有键值对
- **Widget 树扫描** — 每帧递归遍历 ScreensManager.RootWidget，捕获硬编码文本：
  - `LabelWidget.Text` — 标签文本（如 `new LabelWidget { Text = "Game Statistics" }`）
  - `ButtonWidget.Text` — 按钮文本（如 "OK"、"Cancel"）
  - 嵌套容器中的 LabelWidget（如 `AddStat` 中的统计项标题）

## 架构

### IStringProcessor 接口

```csharp
public interface IStringProcessor
{
    string Process(string key, string original, int index);
}
```

- `key` — 字符串来源标识（StringsManager 键名 或 Widget 类型名）
- `original` — 原始英文文本
- `index` — 全局顺序编号（从1开始）
- 返回处理后的字符串

外部 Mod 可注册自定义 IStringProcessor，例如翻译处理器：

```csharp
var mod = // 获取 StringInterceptorMod 实例
mod.RegisterProcessor(new TranslationProcessor()); // 实现 IStringProcessor
```

### 处理流程

```
Loading.Initialize 回调
  ├── 追加 Action 1: ProcessStrings()
  │     读取 StringsManager.m_strings → 遍历所有 key → IStringProcessor 链处理 → 写回
  └── 追加 Action 2: StartWidgetScanner()
        Timer(16ms) → Dispatcher.Dispatch → ScanWidgetTree()
          ├── 帧去重 (Time.FrameIndex)
          ├── 递归遍历 RootWidget.Children
          ├── HashSet<LabelWidget/ButtonWidget> 引用追踪，只处理一次
          └── 自适应频率：活跃16ms / 空闲200ms
```

### 为什么用 Timer + Dispatcher.Dispatch 而不是其他方案

| 方案 | 可行性 | 原因 |
|------|--------|------|
| Injector 替换 MessageDialog | ❌ | Injector 仅支持 IUpdateable Component 和 Block，不支持 Dialog |
| EventBus 订阅 Dialog 事件 | ❌ | 无此事件 |
| Hook DialogsManager.ShowDialog | ❌ | 静态方法，无 SuAPI 接口 |
| 替换 Screen.Update | ❌ | Screen 不通过 Injector 注册 |
| **Timer + Dispatcher.Dispatch** | ✅ | .NET BCL，跨线程安全，覆盖所有 Widget |

### 自适应扫描频率

| 状态 | 间隔 | 触发条件 |
|------|------|----------|
| 活跃 | 16ms（每帧） | 发现新 Widget / Widget 被销毁 |
| 空闲 | 200ms | 连续 60 帧（~1秒）无新 Widget |

目的：活跃期保证新 Widget 下一帧就被处理（无闪烁），空闲期降低遍历开销。

## 关键技术点

### 1. StringsManager.m_strings 的读写时机

```csharp
// Loading.Initialize 在 FrameIndex==0 触发，此时 LoadStrings 尚未执行
// 解决：在 m_loadActions 末尾追加 Action，确保在 LoadStrings 之后执行
eventBus.SubscribeEvent("Loading.Initialize", args => {
    var actions = (List<Action>)args[0];
    actions.Add(() => ProcessStrings()); // LoadStrings 之后的 Action
    return new object[] { false, actions };
});
```

### 2. ModParentField 操作静态字段

```csharp
var _mpf = Program.ModManager.ModParentField;

// 读取静态字段
var strings = _mpf.GetStaticField<Dictionary<string, string>>(
    typeof(StringsManager), "m_strings");

// Dictionary 是引用类型，修改后无需 ModifyStaticField 写回
// 直接 strings[key] = result 即可生效
```

### 3. 跨线程访问 UI 对象

Timer 回调在 ThreadPool 线程执行，不能直接访问 UI Widget。必须通过 `Dispatcher.Dispatch` 回到主线程：

```csharp
_widgetScanTimer = new Timer(_ => {
    Dispatcher.Dispatch(() => ScanWidgetTree());
}, null, 16, 16);
```

注意：`Dispatcher.Dispatch` 在主线程调用时**同步执行**（直接调用 action()），所以帧去重用 `Time.FrameIndex` 防止同一帧重复扫描。

### 4. Widget 引用追踪防重复

```csharp
// 按对象引用追踪，处理过的 Widget 永远跳过
private readonly HashSet<LabelWidget> _processedLabels = new HashSet<LabelWidget>();

if (child is LabelWidget label && !_processedLabels.Contains(label))
{
    _processedLabels.Add(label);
    // 处理 Text...
}
```

好处：
- 同一 Widget 只处理一次，不覆盖游戏的动态更新（如 "Level 5" → "Level 6"）
- Widget 销毁后（`ParentWidget == null`）自动清理引用，防内存泄漏

### 5. 递归遍历 Widget 树

```csharp
private void ScanContainer(ContainerWidget container, ref int labelCount, ref int buttonCount)
{
    foreach (var child in container.Children)
    {
        if (child is LabelWidget label) { /* 处理 */ }
        else if (child is ButtonWidget button) { /* 处理 */ }

        // 递归 — ButtonWidget 继承 CanvasWidget → ContainerWidget，内部有 LabelWidget
        if (child is ContainerWidget childContainer)
            ScanContainer(childContainer, ref labelCount, ref buttonCount);
    }
}
```

## .scmod 打包

```powershell
# 扁平化打包（Push-Location + -Path * 保证 ZIP 根目录不含外层文件夹）
Push-Location $modDir
Compress-Archive -Path * -DestinationPath $zipPath -Force
Pop-Location

# 文件名含 [] 需用 -LiteralPath
Move-Item -LiteralPath $zipPath -Destination $scmodPath -Force
```

## 压缩包结构

```
[SUAPI]StringInterceptor.scmod
├── Lib/
│   └── X64/
│       └── StringInterceptor.dll
└── ModInfo.xml
```

## 安装位置

```
Application/
├── Survivalcraft.exe
└── Mods/
    └── [SuAPI]StringInterceptor.scmod
```
