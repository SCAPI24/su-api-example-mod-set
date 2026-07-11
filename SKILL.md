---
name: survivalcraft-mod
description: Survivalcraft 2 SuAPI 开发技能。帮助创建、修改、调试第三方 Mod。触发词：Survivalcraft、SC mod、SuAPI、IMod、scmod、游戏mod、冷雨夜、联机mod、TemperatureImmunity、RainWithoutDawn、ScMultiplayer。公开仓库：https://gitee.com/SC-SPM/su-api-example-mod-set
---

# Survivalcraft SuAPI 开发指南

## 开发者身份

### 默认：SuAPI Mod 开发者

- **核心约束**：**禁止修改 Survivalcraft 原始代码**，只允许通过 SuAPI 已发布接口调整游戏行为
- **允许的操作**：创建 Mod 项目、编写 IMod 实现类、使用 EventBus/Injector/ParentField/ParentMethod 接口、打包 .scmod
- **禁止的操作**：修改 Engine/EntitySystem/Survivalcraft 中的原始代码

### 切换：SuAPI Core 开发者

- **触发条件**：用户说「切换到API开发」
- **输出标识**：回复中须标注 `[SuAPI core开发者]`
- **允许修改**：仅限 Engine 和 EntitySystem 中 SuAPI 相关代码
- **修改标记**：`//mod ...mod` 或 `/*mod*/.../*...mod*/`

## 项目结构

```
项目根目录/
├── Engine/                # 引擎层（Engine.dll）
├── EntitySystem/
│   ├── GameEntitySystem/  # 游戏实体系统（含 SuAPI 合并）
│   └── SuAPI/             # ★ Mod 核心接口与实现
├── Mod/                   # ★ 第三方 Mod 示例
│   ├── ConsoleMod/        # Widget overlay + 条件编译示例
│   ├── StringInterceptor/ # 字符串拦截+翻译示例
│   ├── MemoryBankDrawMod/ # Dialog 替换示例（IsMergeLib=true）
│   ├── SurvivalcraftMiniMap/ # 新建 ComponentTemplate 示例
│   ├── WatchMod/          # ComponentTemplate+IUpdateable UI 挂载示例
│   ├── GodMode/           # 数据库替换多 Component 示例
│   ├── RainWithoutDawn/   # Subsystem 替换示例
│   ├── ScMultiplayer/     # 复杂 Mod 示例（联机）
│   ├── TemperatureImmunity/ # Component 替换示例
│   └── Comms/             # 联机通信库（ScMultiplayer 依赖）
├── Survivalcraft/         # 游戏主程序
└── publish/
    └── win-x64/Mods/      # ★ scmod 部署目录
```

### DLL 架构（3 DLL 合并后）

| DLL | 合并内容 |
|-----|---------|
| Engine.dll | Engine + FluxJpeg.Core + Hjg.Pngcs + NVorbis + OpenTK(Android) |
| GameEntitySystem.dll | GameEntitySystem + SuAPI + TemplatesDatabase + XmlUtilities |
| Survivalcraft.dll | Survivalcraft 本体 |

**Mod csproj 只需 3 个引用**（Windows: ProjectReference, Android: DLL Reference）：
- Engine.csproj / Engine.dll
- GameEntitySystem.csproj / GameEntitySystem.dll
- Survivalcraft.csproj / Survivalcraft.dll

⚠ 不再单独引用 SuAPI/TemplatesDatabase/XmlUtilities，否则 CS0433 类型冲突。

## IMod 接口

```csharp
public interface IMod
{
    string Name { get; }
    string Version { get; }
    IEnumerable<string> Dependencies { get; }
    bool IsEnabled { get; set; }
    bool IsMergeLib { get; }  // true=仅加载Lib/, false=按平台加载Lib/X64或Lib/Arm64
    void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null);
    void OnUnload();
}
```

**IsMergeLib 双通道**：ModInfo.xml `<IsMergeLib>` 供 ModLoader 读取 + IMod 属性供运行时查询。

## 创建 Mod 的四种模式

### 模式一：数据库替换（最常用）

通过 EventBus 订阅 `GameDatabase.GameDatabase`，修改 Database 中 Parameter 的 Class 值。

```csharp
public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
    {
        return HandleGameDatabase((Database)args[0]);
    }, EventPriority.HIGHEST);
}

public object[] HandleGameDatabase(Database database)
{
    var param = database.FindDatabaseObject(
        new Guid("目标GUID"),
        database.FindDatabaseObjectType("Parameter", true),
        true);
    param.Value = "MyNamespace.MyReplacementClass";
    return new object[] { true, database };
}
```

### 模式二：Injector 类名映射

```csharp
modInjector.Register("Game.ComponentFlu", "MyMod.MyComponentFlu");
```

### 模式三：Loading 注入

```csharp
eventBus.SubscribeEvent("Loading.Initialize", args =>
{
    Game.LoadingManager.ReplaceItem("Initialize PlayScreen", () =>
    {
        Game.ScreensManager.AddScreen("Play", new MyPlayScreen());
    });
    return new object[] { false, args };
}, EventPriority.HIGHEST);
```

⚠ `ReplaceItem` 的 name 是 QueueItem 注册名（"Initialize PlayScreen"），不是 Screen 名。

### 模式四：新建 ComponentTemplate

向已有实体添加新组件。三件套：ComponentTemplate + Parameter + MemberComponentTemplate。

⚠ 铁律：
- `ExplicitInheritanceParent` 必须设置
- `NestingParent` 类型精确匹配：Gameplay→Folder, Player→EntityTemplate
- GUID 从参考代码复制，不要自己编

## 事件系统

| 事件名 | 参数 | 触发时机 |
|--------|------|---------|
| `GameDatabase.GameDatabase` | `{ Database }` | GameDatabase 构造时 |
| `Loading.Initialize` | `{ typeof(LoadingManager) }` | 游戏首帧初始化 |

EventPriority：HIGHEST → HIGH → NORMAL → LOW → LOWEST

返回值约定：`new object[] { bool modified, data }`

## 替换类编写规则

### Subsystem 替换

```csharp
public class MySubsystem : Game.OriginalSubsystem, IUpdateable
{
    public UpdateOrder UpdateOrder => UpdateOrder.Default;
    public void Update(float dt) { /* 自定义逻辑 */ }
    protected override void Load(ValuesDictionary valuesDictionary)
    {
        base.Load(valuesDictionary); // 必须调用
    }
}
```

### Component 替换

```csharp
public class MyComponent : Game.OriginalComponent
{
    public /*mod*/override/*...mod*/ void Update(float dt)
    {
        Program.ModManager.ModParentField.ModifyParentField(
            this, "m_privateField", newValue, typeof(OriginalComponent));
        base.Update(dt);
    }
}
```

### ModParentField

```csharp
var mpf = Program.ModManager.ModParentField;
var val = mpf.GetParentField<T>(target, "fieldName", declaringType);
mpf.ModifyParentField(target, "fieldName", newValue, declaringType);
mpf.ModifyStaticField(typeof(TargetType), "staticField", newValue);
```

### ModParentMethod

```csharp
var mpm = Program.ModManager.ModParentMethod;
mpm.InvokeParentMethod(target, "MethodName", arg1, arg2);
T result = mpm.InvokeParentMethod<T>(target, "MethodName", args);
mpm.InvokeStaticMethod(typeof(TargetType), "StaticMethod", arg1);
```

## Mod 打包格式

### .scmod 文件（ZIP 格式）

**IsMergeLib=true（双端共用）**：
```
MyMod.scmod
├── ModInfo.xml
└── Lib/
    └── MyMod.dll
```

**IsMergeLib=false（按平台分目录）**：
```
MyMod.scmod
├── ModInfo.xml
└── Lib/
    ├── X64/
    │   └── MyMod.dll
    └── Arm64/
        └── MyMod.dll
```

### ModInfo.xml 模板

⚠ 必须使用嵌套格式（根元素 `<Mod>`，内嵌 `<ModInfo>` 和 `<Dependencies>`）。

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Mod>
    <ModInfo>
        <Identifier>MyMod</Identifier>
        <LocalizedName>
            <Text lang="en_US">My Mod</Text>
            <Text lang="zh_CN">我的Mod</Text>
        </LocalizedName>
        <ModVersion>
            <Version>1.0.0</Version>
            <APIVersion>2.1.0</APIVersion>
        </ModVersion>
        <Asset>
            <ContentRoot>Content</ContentRoot>
        </Asset>
        <IsMergeLib>true</IsMergeLib>
    </ModInfo>
    <Dependencies>
    </Dependencies>
</Mod>
```

### 打包脚本

```python
import zipfile, os

MOD_NAME = "YourMod"
MOD_DIR = r"<项目根目录>\Mod\YourMod"
MODS_DIR = r"<项目根目录>\publish\win-x64\Mods"

modinfo = os.path.join(MOD_DIR, "ModInfo.xml")
win_dll = os.path.join(MOD_DIR, "bin", "Debug", "net8.0", "Obfuscar", f"{MOD_NAME}.dll")

with zipfile.ZipFile(os.path.join(MODS_DIR, f"[SuAPI]你的Mod名.scmod"), 'w', zipfile.ZIP_DEFLATED) as zf:
    zf.write(modinfo, "ModInfo.xml")
    zf.write(win_dll, f"Lib/{MOD_NAME}.dll")          # IsMergeLib=true
```

⚠ 必须用 Python zipfile，Compress-Archive 反斜杠路径→ModLoader 匹配失败。

## 项目配置（.csproj）

### IsMergeLib=true（单 TFM，推荐）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Engine\Engine\Engine.csproj" />
    <ProjectReference Include="..\..\EntitySystem\GameEntitySystem\GameEntitySystem.csproj" />
    <ProjectReference Include="..\..\Survivalcraft\Survivalcraft.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Obfuscar" Version="2.2.49">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime;build;native;contentfiles;analyzers;buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <Target Name="PostBuild" AfterTargets="PostBuildEvent">
    <Exec Command="&quot;$(Obfuscar)&quot; Obfuscar.xml" />
  </Target>
</Project>
```

### IsMergeLib=false（双 TFM）

```xml
<TargetFrameworks>net8.0;net8.0-android</TargetFrameworks>
<SupportedOSPlatformVersion Condition="...">21</SupportedOSPlatformVersion>
```

Android 端用 DLL Reference（HintPath 指向对应 bin/Debug/net8.0-android/）。

## 获取游戏运行时对象

```csharp
// Subsystem
var bodies = GameManager.Project?.FindSubsystem<SubsystemBodies>(false);
var terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);

// Player
var playerData = GameManager.Project.FindSubsystem<SubsystemPlayers>(true).PlayersDataList[0];

// Screen
var playScreen = ScreensManager.CurrentScreen as PlayScreen;
```

## 调试技巧

- **Windows 日志**：`<publish>/win-x64/Logs/Game.log`
- **Android 日志**：`/sdcard/Download/Survivalcraft2/Logs/Game.log`
- **诊断日志加 `[SuAPI]` 前缀**，验证后必须移除
- **EventBus 静默吞异常**：handler 外围 try-catch + Log.Error()

## 常用命名空间

```csharp
using Engine;
using Game;
using SuAPI;
using TemplatesDatabase;
```

## 运行时铁律

1. ModLoader 只加载 Identifier 同名的和 Dependencies 声明的 DLL
2. ReplaceItem name 是 QueueItem 注册名，不是 Screen 名
3. EventBus 静默吞异常
4. Release Android AOT/Linker 裁剪 — 避免 Linq/委托排序/params 构造函数
5. SC 坐标系 Y 向上 — 定位参数拆分 visualRadiusPx + marginX/Y
6. 禁止提交诊断 Log
7. Storage.ProcessPath 只识别 `app:` / `data:` 协议
8. SubsystemGameWidgets 只能被一个 Mod 替换（ConsoleMod 已占）
9. Component.Load 跨assembly 用 `protected override`
10. 禁止自主 git push
11. 禁止 CRLF 改 LF — .gitattributes `* text eol=crlf`

## ScreensManager 注册名称对照表

| QueueItem name | Screen name | Screen class |
|----------------|-------------|-------------|
| Initialize PlayerScreen | Player | PlayerScreen |
| Initialize NagScreen | Nag | NagScreen |
| Initialize MainMenuScreen | MainMenu | MainMenuScreen |
| Initialize PlayScreen | Play | PlayScreen |
| Initialize GameScreen | Game | GameScreen |
| Initialize NewWorldScreen | NewWorld | NewWorldScreen |

## 相关仓库

- SuAPI 核心：https://gitee.com/SC-SPM/survivalcraft-su-api
- Mod 示例集：https://gitee.com/SC-SPM/su-api-example-mod-set
