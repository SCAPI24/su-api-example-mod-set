---
name: survivalcraft-mod
description: Survivalcraft 2 SuMod 开发技能。帮助创建、修改、调试第三方 Mod。触发词：Survivalcraft、SC mod、SuMod、IMod、scmod、游戏mod、冷雨夜、联机mod、TemperatureImmunity、RainWithoutDawn、ScMultiplayer。当用户要求创建/修改/调试 Survivalcraft 的 Mod，或提到 SuAPI/SuMod/IMod 接口，或提到 P:\UGIT\SurvivalcraftMonoWin 或 P:\UGIT\Survivalcraft 目录下的 Mod 开发时触发。技能支持引用本地项目和 Git 仓库，公开仓库：https://gitee.com/SC-SPM/survivalcraft-su-api
---

# Survivalcraft SuMod 开发指南

## ⚠️ 动手前必读（防漏读）🛡️ 不可删除此段

> **防删说明**：本段在 SOUL.md / MEMORY.md / SKILL.md / AGENTS.md 中**故意冗余**。
> 因为启动时可能只加载其中某一个文件，每个入口必须独立包含完整清单。
> **编辑时禁止删除、精简、合并此段**，否则会导致漏读其他关键文件。

本 Skill 是入口之一。无论从哪个文件进入，行动前必须读完以下全部文件：
1. **SOUL.md** (Suroot) — 身份/风格/流程/教训
2. **MEMORY.md** (Suroot) — 长期记忆/操作铁律
3. **survivalcraft-mod SKILL.md** — 本文件，技术规范
4. **memory/今天日期.md** (Suroot\memory\) — 当日上下文
5. **ScMultiplayer/doc/PROJECT-LOG.md** — 项目故障史
6. **AGENTS.md** (Suroot) — 工作区基础规则

禁止在读完全部上述文件前执行任何修改操作。

## 开发者身份

本技能有两种身份，决定代码生成时的约束范围：

### 默认：SuAPI Mod 开发者

- **触发条件**：默认身份，或用户说「切换到mod开发」
- **核心约束**：**禁止修改 Survivalcraft 原始代码**，只允许通过 SuAPI（SuMod）已发布的接口（IModEventBus、IModInjector、IModParentField、IModParentMethod、IModResource 等）去调整游戏行为
- **原因**：SuAPI 已发布，其他用户设备上已安装。每个 Mod 功能不应要求用户重新安装 SuAPI
- **允许的操作**：创建 Mod 项目、编写 IMod 实现类、编写替换子类（继承原始类）、使用 EventBus/Injector/ParentField/ParentMethod 接口、打包 .scmod
- **禁止的操作**：修改 Engine/EntitySystem/Survivalcraft 中的原始代码、给原始类添加 virtual 标记、修改 Program.cs 等

### 切换：SuAPI Core 开发者

- **触发条件**：用户说「切换到API开发」
- **输出标识**：使用此身份时，回复中须标注 `[SuAPI core开发者]`
- **允许修改的范围**：仅限 Engine 库和 EntitySystem 库中 SuAPI（SuMod）相关代码
- **修改原则**：
  - 最小嵌入：只添加 Mod 开发者确实需要的接口或钩子，不做多余改动
  - 代码清晰：提交需审核，修改部分必须有明确注释说明用途
  - 其余代码保持原样：不在 SuAPI 之外的代码中做任何修改
  - 常见修改：给需要被 override 的方法加 `/*mod*/virtual/*...mod*/` 标记、新增事件触发点、新增 IModXxx 接口方法
- **修改标记约定**：所有对原始代码的修改用 `//mod ...mod` 或 `/*mod*/.../*...mod*/` 注释包围，便于审核和 diff

## 项目结构

```
P:\UGIT\Survivalcraft\
├── Engine/              # 引擎层（Engine.dll）
├── EntitySystem/
│   ├── GameEntitySystem/  # 游戏实体系统（含 GameDatabase.cs）
│   └── SuMod/            # ★ Mod 核心接口与实现
├── Mod/                 # ★ 第三方 Mod 示例（均已迁移至 net10.0 双平台）
│   ├── RainWithoutDawn/  # 替换 Subsystem 示例
│   ├── TemperatureImmunity/  # 替换 Component 示例
│   ├── ScMultiplayer/    # 复杂 Mod 示例（联机）+ LoadingManager.ReplaceItem 示例
│   ├── ConsoleMod/       # Widget overlay 示例（控制台）+ 条件编译 #if WINDOWS 示例
│   ├── StringInterceptor/ # 字符串拦截+翻译+字体切换示例
│   ├── MemoryBankDrawMod/ # Dialog 替换+交互式 Widget 叠加层示例（绘图编辑器）
│   ├── SurvivalcraftMiniMap/ # 新建 ComponentTemplate 示例（小地图）+ 双平台参数调优示例
│   └── Comms/           # 联机通信库（ScMultiplayer 依赖，需在 ModInfo.xml Dependencies 中声明）
├── Pak/                 # ⚠ Content.pak 解包内容（仅供查阅参考，代码中不可引用此路径）
├── Survivalcraft/       # 游戏主程序（含 Program.cs）
└── Survivalcraft.sln
```

### Content.pak 与 Pak 目录

- 游戏资源打包在 `Content.pak` 中，运行时由游戏引擎加载
- `Pak/` 目录是 Content.pak 的解包内容，**仅供开发时查阅资源结构、GUID、XML 配置等**
- **代码中禁止引用 Pak 目录路径**——游戏不识别 Pak 目录，运行时资源来自 Content.pak
- Mod 需要附加资源时，通过 .scmod 包内 `Content/` 目录 + `ModResource.LoadModResources()` 加载到 ContentCache
- 当前无 Content.pak 打包工具，无法修改或重新打包 Content.pak

### 运行时目录结构（bin）

游戏编译输出在 `Survivalcraft\bin\Debug\net48\`，这是实际运行根目录：

```
Survivalcraft\bin\Debug\net48\
├── Survivalcraft.exe      # 游戏主程序
├── Engine.dll             # 引擎
├── GameEntitySystem.dll   # 实体系统
├── OpenTK.dll             # 图形库
├── Settings.xml           # 游戏设置
├── Content.pak            # 游戏资源包
├── Mods/                  # ★ Mod 放置目录（.scmod 或 .dll）
├── Logs/Game.log          # ★ 运行时日志
├── CharacterSkins/
├── TexturePacks/
├── FurniturePacks/
├── Worlds/
└── OpenAL/
```

**Mod 加载流程**：
1. `ModLoader.LoadMods("Mods", ...)` 扫描运行根目录下 `Mods/` 目录
2. 优先加载 `.scmod`（ZIP 格式，内含 `ModInfo.xml` + `Lib/X64/*.dll`）
3. 其次加载直接放入的 `.dll` 文件
4. `.scmod` 内 DLL 按 `Lib/{Platform}/` 分目录：Windows→`Lib/X64/`，Android→`Lib/Arm64/`
5. 解析 `ModInfo.xml` 中的依赖关系，拓扑排序后按序加载
6. 通过反射查找 `IMod` 实现类，调用 `OnLoad()` 注册事件和映射

**日志路径**：
- **Windows**: `Survivalcraft\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Logs\Game.log`
- **Android**: `/sdcard/Download/Survivalcraft2/Logs/Game.log`（GameLogSink.cs `#if ANDROID` 分支用 FileStream 直接写外部存储）

使用 `Engine.Log.Information/Error` 输出日志。

⚠ **GameLogSink Android 实现注意事项**：
- `Storage.ProcessPath` 只识别 `app:` 和 `data:` 协议，传绝对路径会抛异常 → Android 上必须用 `System.IO.FileStream` 直接操作
- `FileStream` 必须用 `FileAccess.ReadWrite`，因为游戏内 `ViewGameLogDialog` 用 `StreamReader` 读取日志流，`FileAccess.Write` 不可读→`Argument_StreamNotReadable`

### Android 外部存储统一目录

Android 上所有用户可访问的文件统一放在 `/sdcard/Download/Survivalcraft2/` 下：

```
/sdcard/Download/Survivalcraft2/
├── Logs/Game.log        (GameLogSink.cs #if ANDROID)
├── Mods/                (ModLoader.GetPlatformRootDirectory() → .../Survivalcraft2/)
└── Scworld/             (AndroidSdCardExternalContentProvider.m_rootDirectory)
```

- `ModLoader.GetPlatformRootDirectory()` Android 返回 `GetPublicDownloadsPath() + "Survivalcraft2"`
- `ModLoader.GetPublicDownloadsPath()` 通过 `Android.OS.Environment.GetExternalStoragePublicDirectory(DirectoryDownloads)` 获取 `/sdcard/Download`
- 需 `MANAGE_EXTERNAL_STORAGE` 权限（AndroidManifest 已声明）

## SuMod 核心架构

### 入口：Program.Main → ModManager

```csharp
// Program.cs 中的 Mod 初始化（三个注入点）
// 1. Main() — 创建 ModManager 并加载 Mod
ModManager = new ModManager(new ModLoader(), new ModEventBus(), new ModResource(), new ModInjector(), new ModParentField(), new ModParentMethod());
ModManager.ModLoader.LoadMods(@"Mods", ModManager.ModEventBus, ModManager.ModInjector);

// 2. FrameHandler() FrameIndex==0 — Loading.Initialize 事件
foreach (var item in ModManager.ModEventBus.TriggerEvent("Loading.Initialize", new object[] { ... }))

// 3. GameDatabase 构造函数 — GameDatabase.GameDatabase 事件
foreach (var item in modManager.ModEventBus.TriggerEvent("GameDatabase.GameDatabase", new object[] { database }))
```

### 六大核心组件

| 组件 | 接口 | 作用 |
|------|------|------|
| ModLoader | IModLoader | 从 Mods/ 目录加载 .scmod/.dll，解析依赖拓扑排序 |
| ModEventBus | IModEventBus | 发布/订阅事件（SubscribeEvent/TriggerEvent） |
| ModInjector | IModInjector | 类名映射替换（Register → Shift） |
| ModParentField | IModParentField | 反射读写私有字段/属性（含静态成员），IL Emit 缓存 |
| ModParentMethod | IModParentMethod | 反射调用私有/父类方法（实例+静态），IL Emit 缓存 |
| ModResource | IModResource | 资源 KV 存储 + .scmod 内 Content 目录资源加载到 ContentCache |

## IMod 接口（必须实现）

```csharp
public interface IMod
{
    string Name { get; }                    // Mod 显示名
    string Version { get; }                 // 版本号
    IEnumerable<string> Dependencies { get; } // 依赖的 Mod 标识符
    bool IsEnabled { get; set; }            // 是否启用
    void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null);
    void OnUnload();
}
```

## 创建 Mod 的四种模式

### 模式一：数据库替换（最常用）

通过 EventBus 订阅 `GameDatabase.GameDatabase` 事件，修改 Database 中 Parameter 的 Class 值，将游戏的 Subsystem/Component 替换为自定义子类。

**适用场景**：替换游戏逻辑（Subsystem、Component）

```csharp
public class MyMod : IMod
{
    public string Name => "我的Mod";
    public string Version => "1.0.0";
    public IEnumerable<string> Dependencies => Array.Empty<string>();
    public bool IsEnabled { get; set; } = true;

    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
        {
            return HandleGameDatabase((Database)args[0]);
        }, EventPriority.HIGHEST);
    }

    public object[] HandleGameDatabase(Database database)
    {
        // 通过 GUID 找到 Parameter，替换 Class 值
        var param = database.FindDatabaseObject(
            new Guid("目标GUID"),
            database.FindDatabaseObjectType("Parameter", true),
            true);
        param.Value = "MyNamespace.MyReplacementClass";
        return new object[] { true, database }; // true=已修改, 返回修改后的 database
    }

    public void OnUnload() { }
}
```

**关键**：GUID 对应 Database 中某个 Subsystem/Component 的 Class Parameter。需在游戏代码中找到原始类的注册 GUID。

### 模式二：Injector 类名映射

通过 `modInjector.Register()` 注册类名映射，游戏在创建组件时通过 `Shift()` 替换为目标类。

**适用场景**：替代数据库替换方式，适用于 IUpdateable 接口相关类的替换

```csharp
public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
{
    // originalClassName = 命名空间.原始类名
    // newClassName = 命名空间.新类名
    modInjector.Register("Game.ComponentFlu", "MyMod.MyComponentFlu");
    modInjector.Register("Game.ComponentVitalStats", "MyMod.MyComponentVitalStats", newIsOptional: false, newLoadOrder: null);
    // Block 类型替换
    modInjector.Register(blockIndex, typeof(MyCustomBlock));
}
```

**注意**：被替换的原始类如果有 `IUpdateable.Update()` 方法，需要标记为 virtual：
```csharp
public /*mod*/virtual/*...mod*/ void Update(float dt)
```

### 模式三：Loading 注入

订阅 `Loading.Initialize` 事件，在加载阶段添加/替换 Screen 等。

**MAUI net10.0 版（当前）**：`Loading.Initialize` 事件参数为 `new object[] { typeof(LoadingManager) }`。`LoadingManager` 是静态类，通过 `QueueItem`/`ReplaceItem` 操作加载队列。

```csharp
eventBus.SubscribeEvent("Loading.Initialize", args =>
{
    // 替换 Play 屏幕注册
    // name 必须精确匹配 ScreensManager.Initialize 中的 QueueItem name
    // Source: ScreensManager.cs:167 — LoadingManager.QueueItem("Initialize PlayScreen", delegate { AddScreen("Play", new PlayScreen()); });
    bool replaced = Game.LoadingManager.ReplaceItem("Initialize PlayScreen", () =>
    {
        Game.ScreensManager.AddScreen("Play", new MyPlayScreen());
    });
    if (!replaced)
    {
        // ReplaceItem 失败时 fallback——说明原始步骤尚未注册
        // 不可用 QueueItem + 同名 AddScreen，会报 "same key" 字典冲突
        Log.Warning("[MyMod] ReplaceItem failed, screen replacement skipped");
    }
    return new object[] { false, args };
}, EventPriority.HIGHEST);
```

**⚠ 铁律**：
- `ReplaceItem(name, action)` 的 name 必须精确匹配原始 `QueueItem` 的 name（如 "Initialize PlayScreen"），不是 Screen 名（如 "Play"）
- 禁止用 `QueueItem` 添加同名 Screen 的 `AddScreen`，会导致 `ArgumentException: An item with the same key has already been added`
- `ReplaceItem` 返回 `bool`：`true` = 替换成功，`false` = 未找到该 name 的步骤
- `LoadingManager` 是 **static class**，不能声明变量，直接 `Game.LoadingManager.QueueItem/ReplaceItem`

**ScreensManager 注册名称对照表**（Source: ScreensManager.cs:Initialize）：

| QueueItem name | Screen name | Screen class |
|----------------|-------------|-------------|
| Initialize PlayerScreen | Player | PlayerScreen |
| Initialize NagScreen | Nag | NagScreen |
| Initialize MainMenuScreen | MainMenu | MainMenuScreen |
| Initialize PlayScreen | Play | PlayScreen |
| Initialize GameScreen | Game | GameScreen |
| Initialize NewWorldScreen | NewWorld | NewWorldScreen |
| ... | ... | ... |
```

### 模式四：新建 ComponentTemplate（向实体添加新组件）

通过 `new DatabaseObject()` 创建 ComponentTemplate + Parameter + MemberComponentTemplate，将全新组件挂载到指定实体。

**适用场景**：向已有实体（如 Player）添加游戏原本没有的组件

```csharp
public object[] HandleGameDatabase(Database database)
{
    var componentTemplateType = database.FindDatabaseObjectType("ComponentTemplate", true);
    var parameterType = database.FindDatabaseObjectType("Parameter", true);
    var memberComponentTemplateType = database.FindDatabaseObjectType("MemberComponentTemplate", true);

    // 1. 创建 ComponentTemplate
    var componentTemplate = new DatabaseObject(componentTemplateType, new Guid("你的GUID"), "TemplateName", null);
    componentTemplate.Description = "";
    // ★ 必须：继承已有 ComponentTemplate，否则实体系统无法实例化 → "Specified cast is not valid"
    componentTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
        new Guid("已有模板GUID"), componentTemplateType, true);
    // ★ 必须：挂到 Folder 类型下（如 Gameplay），不是 EntityTemplate
    componentTemplate.NestingParent = database.FindDatabaseObject(
        "Gameplay", database.FindDatabaseObjectType("Folder", true), true);

    // 2. 创建 Class Parameter
    var parameterClass = new DatabaseObject(parameterType, new Guid("参数GUID"), "Class", "命名空间.类名");
    parameterClass.NestingParent = componentTemplate;

    // 3. 创建 MemberComponentTemplate，挂到目标实体
    var memberComponent = new DatabaseObject(memberComponentTemplateType, new Guid("成员GUID"), "TemplateName", null);
    memberComponent.Description = "";
    memberComponent.ExplicitInheritanceParent = database.FindDatabaseObject(
        new Guid("上面ComponentTemplate的GUID"), componentTemplateType, true);
    // ★ 必须：挂到 EntityTemplate 类型下（如 Player），不是 Folder
    memberComponent.NestingParent = database.FindDatabaseObject(
        "Player", database.FindDatabaseObjectType("EntityTemplate", true), true);

    return new object[] { true, database };
}
```

**⚠️ 铁律**：
- `ComponentTemplate.ExplicitInheritanceParent` 必须设置（继承已有模板），否则 "Specified cast is not valid"
- `NestingParent` 类型必须精确匹配：Gameplay → Folder，Player → EntityTemplate
- GUID 必须从参考代码复制，不要自己编

## 事件系统

### EventPriority（高→低）
`HIGHEST` → `HIGH` → `NORMAL` → `LOW` → `LOWEST`

### 已知事件名
| 事件名 | 参数 | 触发时机 |
|--------|------|----------|
| `GameDatabase.GameDatabase` | `object[] { Database }` | GameDatabase 构造时 |
| `Loading.Initialize` | `object[] { typeof(LoadingManager) }` | 游戏首帧初始化时（Initialize() 之后） |

### 事件返回值约定
- `new object[] { bool modified, data }` — modified=true 表示数据被修改需回写
- `Loading.Initialize` 返回 `new object[] { false, args }`（false=不阻断后续处理）

## 替换类编写规则

### Subsystem 替换

```csharp
// 继承原始 Subsystem，重写方法
public class MySubsystem : Game.OriginalSubsystem, IUpdateable
{
    public UpdateOrder UpdateOrder => UpdateOrder.Default;

    public void Update(float dt)
    {
        // 自定义逻辑
    }

    protected override void Load(ValuesDictionary valuesDictionary)
    {
        base.Load(valuesDictionary); // 必须调用 base
        // 初始化
    }
}
```

### Component 替换

```csharp
// 继承原始 Component
public class MyComponent : Game.OriginalComponent
{
    public /*mod*/override/*...mod*/ void Update(float dt)
    {
        // 可用 ModParentField 修改父类私有字段
        Program.ModManager.ModParentField.ModifyParentField(
            this, "m_privateField", newValue, typeof(OriginalComponent));
        base.Update(dt);
    }

    protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        base.Load(valuesDictionary, idToEntityMap);
    }
}
```

### ModParentField 常用操作

```csharp
var mpf = Program.ModManager.ModParentField;

// 读取实例私有字段
var val = mpf.GetParentField<T>(target, "fieldName", declaringType);
mpf.GetParentField(target, "fieldName", declaringType);

// 修改实例私有字段
mpf.ModifyParentField(target, "fieldName", newValue, declaringType);

// 静态字段
mpf.ModifyStaticField(typeof(TargetType), "staticField", newValue);
var val = mpf.GetStaticField<T>(typeof(TargetType), "staticField");
```

### ModParentMethod 常用操作

```csharp
var mpm = Program.ModManager.ModParentMethod;

// 调用父类私有实例方法
mpm.InvokeParentMethod(target, "MethodName", arg1, arg2);
T result = mpm.InvokeParentMethod<T>(target, "MethodName", args);

// 调用静态方法
mpm.InvokeStaticMethod(typeof(TargetType), "StaticMethod", arg1);
T result = mpm.InvokeStaticMethod<T>(typeof(TargetType), "StaticMethod", args);

// 指定参数类型（避免重载歧义）
mpm.InvokeParentMethod(target, "Method", new Type[] { typeof(int) }, args);
```

## Mod 打包格式

### .scmod 文件（ZIP 格式）

```
MyMod.scmod
├── ModInfo.xml           # 元数据
├── Lib/
│   ├── X64/              # Windows 平台 DLL
│   │   ├── MyMod.dll      # 主 Mod DLL（文件名=Identifier）
│   │   └── Dependency.dll  # 依赖 DLL（必须在 Dependencies 中声明）
│   └── Arm64/            # Android 平台 DLL
│       ├── MyMod.dll
│       └── Dependency.dll
└── Content/              # 资源文件（可选）
    └── Textures/
        └── ...
```

**⚠ 依赖 DLL 必须在 ModInfo.xml 的 `<Dependencies>` 中声明**，否则 ModLoader 不会加载它，运行时报 `ReflectionTypeLoadException: Unable to load one or more of the requested types`。即使 DLL 在 .scmod ZIP 中，ModLoader 只加载：(1) 与 Identifier 同名的 DLL，(2) Dependencies 声明的 DLL。

**命名规范**：所有由 SuAPI 生成的 `.scmod` 文件，**文件名最前面必须加 `[SuAPI]` 前缀**，与其他来源的 Mod 区分：

```
[SuAPI]MyMod.scmod
[SuAPI]联机.scmod
[SuAPI]体温保持.scmod
```

### ModInfo.xml 模板

> **⚠️ 铁律：ModInfo.xml 必须使用嵌套格式！**
> 根元素必须是 `<Mod>`，内含嵌套的 `<ModInfo>` 和 `<Dependencies>`。
> 禁止使用扁平格式 `<ModInfo><Identifier>...</Identifier></ModInfo>` —— ModLoader 代码 `doc.Root.Element("ModInfo")` 会在扁平格式下返回 null，导致 modId 解析为 null → "Invalid ModID" → .scmod 被静默跳过，且错误信息只输出到 Console（不写 Game.log），极难排查。

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
    </ModInfo>
    <Dependencies>
        <!-- ★ 必须声明 .scmod 内所有非主 Identifier 的 DLL -->
        <!-- 未声明的 DLL 不会被 ModLoader 加载，导致 ReflectionTypeLoadException -->
        <!-- <Dependency>
            <ModInfo>
                <Identifier>Comms</Identifier>
            </ModInfo>
        </Dependency> -->
    </Dependencies>
</Mod>
```

### 加载顺序

1. ModLoader 扫描 `Mods/` 目录下的 `.scmod` 和 `.dll` 文件
2. `.scmod` 优先于同名 `.dll`
3. 拓扑排序确定依赖加载顺序
4. Windows 平台查找 `Lib/X64/` 下的 DLL
5. Assembly.Load 加载 → 反射查找 IMod 实现类 → Activator.CreateInstance → OnLoad

### 独立 DLL 部署

直接将编译好的 `MyMod.dll` 放入 `Mods/` 目录即可，无需打包 .scmod。

## 项目配置（.csproj）

### net10.0 双平台项目（当前标准）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0-android;net10.0-windows10.0.19041.0</TargetFrameworks>
    <!-- SDK样式自动包含 **/*.cs，若项目有显式 Compile 项会报 NETSDK1022 -->
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
  <!-- Windows: ProjectReference 引用游戏项目 -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows10.0.19041.0'">
    <ProjectReference Include="..\..\EntitySystem\EntitySystem.csproj" />
    <ProjectReference Include="..\..\Survivalcraft\Survivalcraft.csproj" />
  </ItemGroup>
  <!-- Android: DLL 引用（项目不直接在 Android 上编译） -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0-android'">
    <Reference Include="EntitySystem">
      <HintPath>..\..\..\..\Survivalcraft\bin\Debug\net10.0-android\EntitySystem.dll</HintPath>
    </Reference>
    <Reference Include="Survivalcraft">
      <HintPath>..\..\..\..\Survivalcraft\bin\Debug\net10.0-android\Survivalcraft.dll</HintPath>
    </Reference>
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows10.0.19041.0'">
    <PackageReference Include="Obfuscar" Version="2.2.49">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime;build;native;contentfiles;analyzers;buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <Target Name="PostBuild" AfterTargets="PostBuildEvent" Condition="'$(TargetFramework)' == 'net10.0-windows10.0.19041.0'">
    <Exec Command="&quot;$(Obfuscar)&quot; Obfuscar.xml" />
  </Target>
</Project>
```

**关键点**：
- 双 TFM: `net10.0-android` + `net10.0-windows10.0.19041.0`
- Windows 用 `ProjectReference`，Android 用 `Reference + HintPath`
- EntitySystem.csproj（不是 GameEntitySystem.csproj）
- Obfuscar 仅 Windows 端
- SDK 样式自动包含 `**/*.cs`，旧项目的显式 `<Compile Include>` 须删除（否则 NETSDK1022 重复项错误）
- 若项目有 `AssemblyInfo.cs`，须加 `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>`

### .NET Framework 4.8 项目（旧样式，已弃用）

参考 `Mod/RainWithoutDawn/RainWithoutDawn.csproj` — 需手动配置 HintPath 指向游戏输出 DLL。

### Obfuscar.xml（混淆配置，仅 Windows 端）

```xml
<?xml version='1.0'?>
<Obfuscator>
    <Var name="InPath" value=".\bin\Debug\net10.0-windows10.0.19041.0" />
    <Var name="OutPath" value=".\bin\Debug\net10.0-windows10.0.19041.0\Obfuscar" />
    <Var name="SkipExtraDependencies" value="true" />
    <Module file="$(InPath)\EntitySystem.dll"/>
    <Module file="$(InPath)\MyMod.dll"/>
</Obfuscator>
```

### 双平台打包脚本（PowerShell）

```powershell
$modName = "MyMod"
$modDir = "P:\UGIT\Survivalcraft\Mod\$modName"
$winBin = "$modDir\bin\Debug\net10.0-windows10.0.19041.0"
$androidBin = "$modDir\bin\Debug\net10.0-android"
$winMods = "P:\UGIT\Survivalcraft\Survivalcraft\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Mods"

# 1. 编译
dotnet build $modDir -c Debug

# 2. 打包 .scmod
$packDir = "$env:TEMP\pack_$modName"
if (Test-Path $packDir) { Remove-Item -Recurse -Force $packDir }
New-Item -ItemType Directory -Path "$packDir\Lib\X64", "$packDir\Lib\Arm64" -Force | Out-Null

# Windows: Obfuscar 混淆后 DLL
Copy-Item "$winBin\Obfuscar\$modName.dll" "$packDir\Lib\X64\"
Copy-Item "$winBin\Obfuscar\EntitySystem.dll" "$packDir\Lib\X64\"  # 如果依赖需要

# Android: 直接编译输出
Copy-Item "$androidBin\$modName.dll" "$packDir\Lib\Arm64\"
Copy-Item "$androidBin\EntitySystem.dll" "$packDir\Lib\Arm64\"  # 如果依赖需要

# ModInfo.xml + Content
Copy-Item "$modDir\ModInfo.xml" "$packDir\"
if (Test-Path "$modDir\Content") { Copy-Item -Recurse "$modDir\Content" "$packDir\" }

# 打包为 ZIP → 改名 .scmod
$scmodPath = "$winMods\[SuAPI]$modName.scmod"
Push-Location $packDir
Compress-Archive -Path * -DestinationPath "$env:TEMP\$modName.zip" -Force
Pop-Location
if (Test-Path $scmodPath) { Remove-Item -LiteralPath $scmodPath -Force }
Move-Item -LiteralPath "$env:TEMP\$modName.zip" $scmodPath

# 验证 ZIP 结构
Add-Type -AssemblyName System.IO.Compression
[System.IO.Compression.ZipFile]::OpenRead($scmodPath).Entries | ForEach-Object { $_.FullName }
```

**⚠ 注意**：
- 文件名含 `[SuAPI]` 前缀时，PowerShell 的 `Move-Item`/`Rename-Item` 需用 `-LiteralPath`
- adb push 同理：先 `Copy-Item` 到临时文件名再 push
- Obfuscar 仅 Windows 端运行，Android 端用 bin 直接输出
- 禁止将 Engine.dll / Survivalcraft.dll / EntitySystem.dll 打入 .scmod

## 创建新 Mod 的标准流程

### 快速流程（简单 Mod）

1. 在 `P:\UGIT\Survivalcraft\Mod\` 下创建 Mod 目录
2. 创建 .csproj（SDK 样式，双 TFM: net10.0-android + net10.0-windows10.0.19041.0）
3. 创建 `Plug/` 目录放 IMod 入口类
4. 按需创建 `Subsystem/`、`Component/`、`Func/` 等子目录放替换类
5. 实现 IMod 接口，在 OnLoad 中订阅事件或注册 Injector
6. 编写替换类（继承原始类，重写必要方法）
7. 创建 ModInfo.xml 和 Obfuscar.xml
8. 编译输出 DLL 到 `Mods/` 目录或打包 .scmod

### 严谨流程（复杂 Mod / 需要深度代码分析）

当 Mod 需要与深层游戏逻辑交互、替换多个类、或涉及复杂的状态管理时，必须按以下四步流程执行，确保不遗漏细节、不产生幻觉代码：

#### 第1步：结构感知与依赖梳理

针对 Mod 需要交互的游戏代码，阅读并输出分析报告：
- 目标类的完整源码（包括所有 `using`、字段、属性、方法）
- 目标类的继承链（基类、接口）
- 目标类被哪些其他类引用/调用
- 魔法数字、默认配置值、GUID 等硬编码值
- 数据流转过程（哪个方法修改哪个字段、事件触发链）

#### 第2步：细节穷举与边界探测

针对每个将被 Mod 替换/交互的类和方法，回答：
- 所有输入参数：类型、是否可选、默认值、取值范围
- 内部分支路径：if/else/switch 每一条
- 可能抛出的异常及条件
- 隐式依赖：全局变量、单例、文件系统、子系统引用
- 边界情况：空输入、超长值、特殊字符、并发访问

#### 第3步：生成 Mod 代码（严格约束）

- **禁止添加原始代码中不存在的逻辑**——不确定的行为必须回到源码确认
- 每个对外暴露的功能点，注释标注源自哪个源文件、哪个类/方法
- 若 Mod 需要对输入做适配，显式说明转换规则
- 代码中必须包含使用示例（至少一个调用场景）

#### 第4步：自我验证

生成 Mod 后，立即验证：
- 所有主要业务流程是否覆盖
- 所有已知异常分支是否处理
- 空输入、子系统未找到、依赖缺失等边界是否防御
- 是否遵守当前身份约束（Mod 开发者不改原始代码 / Core 开发者最小嵌入）

> 详细的代码分析方法论和提示词模板，见 [references/code-analysis-workflow.md](references/code-analysis-workflow.md)

## 常用命名空间引用

```csharp
using Engine;           // Vector3, Log, MathUtils 等
using Game;             // 游戏类（Subsystem*, Component*, ScreensManager 等）
using GameEntitySystem; // Project, Subsystem, Component, IUpdateable
using SuMod;            // IModEventBus, IModInjector, ModManager
using SuMod.Tools;      // IMod, EventPriority
using TemplatesDatabase;// Database, DatabaseObject, ValuesDictionary
```

## 获取游戏运行时对象

```csharp
// 获取 ModManager
Program.ModManager

// 获取 Subsystem
GameManager.Project.FindSubsystem<SubsystemType>(throwOnError: true)

// 获取 Component 列表
GameManager.Project.FindSubsystem<SubsystemPlayers>(throwOnError: true).ComponentPlayers

// 获取 Screen
ScreensManager.FindScreen<Screen>("ScreenName")

// 添加自定义 Screen
ScreensManager.AddScreen("MyScreen", new MyScreen())

// 切换 Screen
ScreensManager.SwitchScreen("ScreenName", args)
```

### LoadingManager API（MAUI net10.0 版）

`LoadingManager` 是 **static class**，不能声明变量。

```csharp
// 添加加载步骤（追加到队列末尾）
Game.LoadingManager.QueueItem(string name, Action action)

// 替换已注册的加载步骤（name 必须精确匹配）
bool Game.LoadingManager.ReplaceItem(string name, Action newAction)
// 返回 true=替换成功，false=未找到该 name
```

**⚠ 铁律**：
- `ReplaceItem` 的 name 是 QueueItem 注册名（如 "Initialize PlayScreen"），不是 Screen 名（如 "Play"）
- 禁止用 `QueueItem` + 同名 `AddScreen`，会报 `ArgumentException: An item with the same key has already been added`
- `TriggerEvent("Loading.Initialize")` 在 `Initialize()` 之后触发，所以原始 QueueItem 已注册，ReplaceItem 可正常工作

## 调试技巧

### 编译前置检查（铁律）

> **⚠️ 每次编译前必须执行，漏做会导致 DLL 锁定或缓存跳过重编译。**

1. **杀 SC 进程**：`taskkill /F /IM Survivalcraft.exe` — 锁 DLL 的是 SC 运行时进程，不是 VS
2. **touch 源文件**：`(Get-Item 源文件路径).LastWriteTime = Get-Date` — 防 obj 缓存认为输出最新而跳过 CoreCompile
3. **验证 DLL 含新代码**：`[IO.File]::ReadAllBytes(dll路径)` 搜索特征字符串

### CLI 编译验证

**net10.0 项目**：可直接用 `dotnet build`

```powershell
dotnet build "P:\UGIT\Survivalcraft\Mod\MyMod\MyMod.csproj" -c Debug --verbosity minimal
```

**net48 项目（已弃用）**：必须用 MSBuild，`dotnet build` 与 Engine.csproj 不兼容

```powershell
& "d:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
    "P:\UGIT\Survivalcraft\Mod\MyMod\MyMod.csproj" `
    /p:Configuration=Debug /verbosity:minimal
```

- MSBuild 路径：`d:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe`
- 编译错误可直接读取，无需打开 VS
- 编译通过 ≠ 运行正确，但编译失败一定不能打包
- net10.0 编译输出含 Obfuscar 混淆步骤，最终 DLL 在 `bin\Debug\net10.0-windows10.0.19041.0\Obfuscar\` 目录

### 运行时日志

- 使用 `Engine.Log.Information()` / `Engine.Log.Error()` 输出日志
- 日志输出到游戏日志文件，事后可读取分析
- 关键调试点建议加日志：Mod OnLoad 入口、事件订阅回调、Subsystem/Component 替换类 Load/Update

### VS 调试数据

- **当前无法从外部 agent 直接读取 VS 调试数据**（断点、堆栈、即时窗口、输出窗口）
- VS MCP Server Manager 是 VS 消费外部 MCP Server，不暴露自身调试状态
- 需要 VS 调试信息时，手动从 VS 复制粘贴给 agent 分析

### 常见问题排查

- Mod 加载失败时检查：DLL 是否在 Mods/ 目录、IMod 实现类是否 public、依赖 DLL 是否缺失
- 事件订阅时注意 EventPriority 顺序
- 数据库替换的 GUID 必须精确匹配原始代码中的注册值
- Component 的 Update 方法如需 override，确认原始类已标记 `/*mod*/virtual/*...mod*/`
- **编译必须用 MSBuild**：仅限 net48 项目。net10.0 项目可用 `dotnet build`
- **打包 .scmod 时 PowerShell 文件名含 `[]`**：必须用 `-LiteralPath` 参数，否则通配符解析失败
- **新建 ComponentTemplate 三件套缺一不可**：ExplicitInheritanceParent + 正确 NestingParent 类型 + 完整 GUID
- **"Specified cast is not valid"**：通常是 ComponentTemplate 缺少 ExplicitInheritanceParent
- **"An item with the same key has already been added"**：检查 GUID 是否与存档/其他模板冲突，或是否重复注册
- **.scmod 报错但 .dll 正常**：优先对比参考版本的同功能 .scmod 代码，不要猜测引擎内部机制
- **.scmod ZIP 根目录禁止包含中间文件夹**：ModLoader 在 ZIP 根查找 `ModInfo.xml`。`Compress-Archive -LiteralPath $dir` 会把目录本身打进去导致根目录变成目录名。必须 `Push-Location $dir; Compress-Archive -Path *` 保证扁平结构。打包后 `[ZipFile]::OpenRead()` 验证
- **ModEventBus 异常静默吞咽**：`TriggerEvent` 的异常只写 `Console.WriteLine`，不记入 `Game.log`。调试时在 handler 外围 try-catch 打 `Log.Error()` 或加步进标记
- **依赖 DLL 打包**：若 Mod 依赖其他 DLL（如 Comms），将依赖 DLL 放入 `.scmod` 的 `Lib/X64/`（和 `Lib/Arm64/`），并在 `ModInfo.xml` 的 `<Dependencies>` 中声明其 Identifier。未声明的 DLL 不会被 ModLoader 加载，运行时报 `ReflectionTypeLoadException`
- **LoadingManager.ReplaceItem vs QueueItem**：MAUI net10.0 版中，替换 Screen 必须用 `ReplaceItem("Initialize XxxScreen", action)`，不能用 `QueueItem` + 同名 `AddScreen`——后者会导致 `ArgumentException: An item with the same key has already been added. Key: Play`。name 参数是 QueueItem 注册名（如 "Initialize PlayScreen"），不是 Screen 名（如 "Play"）
- **⚠️ ModInfo.xml 必须使用嵌套格式**：根元素必须是 `<Mod>`，内部嵌套 `<ModInfo>` 和 `<Dependencies>`。ModLoader 解析逻辑是 `doc.Root.Element("ModInfo").Element("Identifier")`，如果根元素直接是 `<ModInfo>`（扁平格式），`doc.Root.Element("ModInfo")` 返回 null → modId = null → "Invalid ModID" → .scmod 被静默跳过。其他正常工作的 Mod（ConsoleMod、MiniMap、体温保持）均使用嵌套格式，务必参照模板
- **⚠️ 打包时 ModInfo.xml 必须放在 ZIP 根目录**：`Copy-Item "$modDir\ModInfo.xml" "$packDir\Content\"` 之类的批量拷贝会把 ModInfo.xml 误放入 Content/ 子目录 → ModLoader 在根目录找不到 → mod 静默跳过。必须分开拷贝：`ModInfo.xml → $packDir\`（根），`zh_CN.xml → $packDir\Content\`（Content）
- **Loading.Initialize 屏幕注册索引**：`actions` 末尾批量注册 Screen，Play 在倒数第 13 位。动态计算 `actions.Count - 13`，禁止硬编码 `actions[803]`——`ContentManager.List()` 末尾数量不固定

### Android 日志性能铁律

1. **Microsoft.Extensions.Logging.Debug 包必须移除** — MAUI 模板默认引入，Debug 配置下 `DebugLoggerProvider` 向 Android logcat 输出大量 MAUI 框架内部日志（页面生命周期、绑定追踪、布局更新、手势识别），logcat 是系统级 I/O，大量写入导致严重卡顿。Windows 无影响（输出到 VS Output 窗口）。csproj 中注释掉或删除该 PackageReference
2. **Console.WriteLine 在 Android 上输出到 logcat** — .NET for Android runtime 将 Console.Write/WriteLine 重定向到 Android logcat（debug tag），即使移除了 ConsoleLogSink，SuMod 中的 Console.WriteLine 仍输出到 logcat
3. **Engine.Log + GameLogSink 每次 Flush()** — GameLogSink.Log() 每次调用都 Flush()，高频日志=高频磁盘 I/O。非热路径可用，热路径禁止
4. **MIUIInput 日志不可控** — MIUI ROM 系统层触摸事件日志（`I/MIUIInput: [MotionEvent]`），每次触摸/滑动都打印，应用侧无法关闭，但不影响应用进程性能（系统级，不经过应用进程）
5. **诊断日志验证后必须移除** — `[Window]`、`[SuAPI]` 等标记日志是临时诊断用，验证完成后立即删除，不得提交

### MiniMap 双平台调优铁律

1. **定位参数拆分**: 不能用同一个系数(如 RmapRadius)同时控制视觉半径和定位偏移。增大 RmapRadius→偏移量也增大→地图挤向屏幕中央。拆分为: `visualRadiusPx = mapRadius * MapScale` 控定位，`MapScale` 控大小，`marginX/Y` 控边距
2. **SC 屏幕坐标系 Y 向上**: OpenGL 坐标系 Y 从下往上，和 WinForms/WPF 相反。`center.Y` 越大越靠上
3. **Android 边距需更大**: Android UI 按钮/状态栏尺寸比例比 Windows 大，同等百分比边距不够。Windows 10%，Android 15%
4. **MapScale 不能盲目翻倍**: Windows 翻倍合理不代表 Android 也该翻倍。Android 保持原始比例的增量即可

### Release Android AOT/Linker 裁剪铁律

Release Android 使用 AOT+Linker，会裁剪主程序未使用的 BCL 类型/方法。Mod 中使用被裁剪的方法会抛 `MissingMethodException`，但 ModEventBus 的 catch 只写 `Console.WriteLine`（不写 Game.log），异常被静默吞掉，问题极难排查。

1. **被裁剪 API 清单**（已验证）:
   - `HashSet<T>.RemoveWhere(Predicate<T>)` → 用 foreach + 临时列表 + Remove 替代
   - `List<T>.Sort(Comparison<T>)` → 用冒泡排序或 Array.Sort + ToArray 替代
   - `XDocument.Load(string)` → 用 `XDocument.Load(Stream)` + FileStream 替代
   - `System.Threading.Timer` → 用 Frame.Update 事件驱动替代
   - `XDocument(params object[])` → 用 `new XDocument()` + `doc.Add(root)` 替代
   - `XDeclaration` → 避免
   - `File.WriteAllText(string,string,Encoding)` / `StreamWriter(string,bool,Encoding)` → 用 `FileStream` + `StreamWriter(Stream,Encoding)` 替代
2. **通用原则**: Release AOT 下，任何主程序未使用的方法/泛型实例都可能被裁剪。Mod 代码只使用最基础的集合操作（foreach/Add/Remove/索引器），避免 Linq 扩展方法（Where/Select/ToList/Any）、委托排序、params 构造函数、高级便利方法
3. **诊断方法**: 若 Mod 在 Release Android 上静默失效，在 handler 外围加 try-catch + `Log.Error()` 捕获异常，常见的就是 MissingMethodException
4. **EventBus 异常吞咽使问题隐蔽**: TriggerEvent 的 catch 只写 Console.WriteLine，AOT 下异常被完全吞掉，没有任何可见错误。调试时必须在 handler 外围加 try-catch + `Log.Error()`

## Widget Overlay 模式（不切 Screen）

当需要在游戏画面上叠加 UI（如控制台、信息面板）而不暂停游戏时，可替换 `SubsystemGameWidgets`，在其 `GameWidget.GuiWidget` 上挂载自定义 Widget。

### 输入拦截机制（核心）

控制台打开时需要**阻止游戏输入但保持画面运行**，不能暂停游戏。用两个机制组合实现：

| 机制 | 作用 | 实现方式 |
|------|------|----------|
| **Dialog 模态** | 解锁鼠标光标 | `DialogsManager.ShowDialog(guiWidget, m_modalDialog)` + `MakeCoverTransparent()` 反射修改 CoverWidget 为透明 |
| **Keyboard.Clear()** | 清空按键状态 | 每帧先用 `CaptureInput()` 读键，再 `Keyboard.Clear()` + `Mouse.Clear()` 清空，使 ComponentInput（-10）看到空状态 |
| **UpdateOrder=-100** | 抢先拦截键盘 | `public new UpdateOrder UpdateOrder => (UpdateOrder)(-100)`，比 `ComponentInput`（-10）更早执行 |

> **关键流程**：UpdateOrder(-100) → CaptureInput() 读键 → Keyboard.Clear() 清空 → ComponentInput(-10) 看到空 → 不触发游戏操作

**Dialog 透明遮罩反射**（MakeCoverTransparent）：
```csharp
private void MakeCoverTransparent()
{
    var animDataField = typeof(DialogsManager).GetField("m_animationData",
        BindingFlags.NonPublic | BindingFlags.Static);
    var animData = animDataField.GetValue(null) as System.Collections.IDictionary;
    var data = animData[m_modalDialog];
    var cover = data.GetType().GetField("CoverWidget",
        BindingFlags.Public | BindingFlags.Instance).GetValue(data) as RectangleWidget;
    // 不遮挡画面但保持输入拦截
    cover.FillColor = Color.Transparent;
    cover.IsHitTestVisible = false;
}
```

### 适用场景
- 游戏内控制台
- HUD 信息面板
- 调试信息显示

### 核心思路
1. EventBus 替换 `SubsystemGameWidgets`（GUID: `6bf14dc6-32e7-4e8c-b3c4-438e0eee13ad`）
2. 替换类继承 `SubsystemGameWidgets`，override `Update`（需 `/*mod*/virtual/*...mod*/` 标记）
3. 在 `Update` 中监听键盘快捷键，按需 Attach/Detach Widget 到 `GameWidgets[0].GuiWidget`
4. 使用 `CanvasWidget` + `RectangleWidget` + `LabelWidget` 构建 overlay UI

### Widget 类型速查

| Widget | 用途 | 关键属性 |
|--------|------|----------|
| `CanvasWidget` | 绝对定位容器 | `Size`, `VerticalAlignment=Far` |
| `RectangleWidget` | 矩形背景 | `FillColor=Color(0,0,0,180)`, `OutlineColor` |
| `LabelWidget` | 文字显示 | `Color=Color.White`, `FontScale=0.6f` |

### 键盘输入

```csharp
// 单次按键检测
if (Keyboard.IsKeyDownOnce(Key.Tilde)) { ... }
if (Keyboard.IsKeyDownOnce(Key.Enter)) { ... }
if (Keyboard.IsKeyDownOnce(Key.Escape)) { ... }
if (Keyboard.IsKeyDownOnce(Key.Delete)) { ... }  // 必须在 BackSpace 之前判断

// ⚠ Delete 与 BackSpace 陷阱
// OpenTK 的 KeyDownHandler 对两键都设置 KeyboardInput.DeletePressed = true
// 必须先用 IsKeyDownOnce(Key.Delete) 独立判断 Delete，再判断 BackSpace

// 字符输入（推荐：每帧获取所有字符）
string inputChars = KeyboardInput.GetInput();
foreach (char c in inputChars)
{
    if (!char.IsControl(c) && c != '`' && c != '~')
        m_inputText.Insert(m_cursorPos, c);
}

// 字符输入（旧方案：仅获取每帧最后一个字符，快速打字可能丢字）
char? lastChar = Keyboard.LastChar;
```

### 关键注意
- `GameWidgets` 列表在 Load 后才有内容，首次 Attach 需判空
- 控制台打开时游戏不暂停，键盘输入可能同时触发游戏操作——必须调用 `Keyboard.Clear()` 清空状态
- `KeyboardInput.GetInput()` 捕获并清空每帧所有字符，推荐代替 `Keyboard.LastChar`
- **UpdateOrder 必须设为 -100**（小于 ComponentInput 的 -10），确保在游戏输入处理前读取按键
- Dialog 的 `m_animationData.CoverWidget` 通过反射改为透明，否则画面会变暗
- 关闭控制台后需 `DialogsManager.HideDialog()` 移除模态，否则鼠标光标不恢复
- 参考 ConsoleMod 完整实现

### Mod 管理（agent 操作）

游戏运行时 Mod 加载基于文件后缀：`Directory.GetFiles(modDirectory, "*.scmod")` + `Directory.GetFiles(modDirectory, "*.dll")`，仅扫描 `Mods/` 顶层目录。因此**修改后缀即可控制加载与否**，无需删除文件。

#### 查询已安装 Mod

扫描 Mods/ 目录下的 `.scmod` 文件，解析内部 ModInfo.xml 获取信息：

```powershell
# 列出所有已安装 Mod（含已禁用）
Get-ChildItem "P:\UGIT\Survivalcraft\Survivalcraft\bin\Debug\net10.0-windows10.0.19041.0\Mods\*" -Include *.scmod,*.scmod.unint | ForEach-Object {
    $status = if ($_.Extension -eq ".scmod") { "启用" } else { "禁用" }
    Write-Output "$status | $($_.Name)"
}
```

**ModInfo.xml 结构**（解析 .scmod 内的 ZIP 条目可获取）：

| 字段 | XML 路径 | 说明 |
|------|----------|------|
| Identifier | `/Mod/ModInfo/Identifier` | Mod 唯一标识 |
| LocalizedName | `/Mod/ModInfo/LocalizedName/Text[@lang='zh_CN']` | 中文名 |
| Version | `/Mod/ModInfo/ModVersion/Version` | Mod 版本 |
| APIVersion | `/Mod/ModInfo/ModVersion/APIVersion` | 要求的 SuAPI 版本 |
| ContentRoot | `/Mod/ModInfo/Asset/ContentRoot` | 资源根目录 |
| Dependencies | `/Mod/Dependencies/Dependency/ModInfo/Identifier` | 依赖的其他 Mod |

#### 禁用 Mod（卸载）

将 `.scmod` 后缀改为 `.scmod.unint`，游戏启动时不会扫描该后缀：

```powershell
# 禁用指定 Mod
$modDir = "P:\UGIT\Survivalcraft\Survivalcraft\bin\Debug\net10.0-windows10.0.19041.0\Mods"
Rename-Item "$modDir\TargetMod.scmod" "TargetMod.scmod.unint"
```

#### 启用 Mod（重新加载）

将 `.scmod.unint` 改回 `.scmod`：

```powershell
# 启用指定 Mod
$modDir = "P:\UGIT\Survivalcraft\Survivalcraft\bin\Debug\net10.0-windows10.0.19041.0\Mods"
Rename-Item "$modDir\TargetMod.scmod.unint" "TargetMod.scmod"
```

> **注意**：禁用/启用需要重启游戏生效。游戏运行时通过 `IModLoader.UnloadMod()` 只能卸载已加载的 Mod，不能热加载新 Mod。

## Dialog 替换模式（不切 Screen）

当需要修改游戏内已有对话框（如 Memory Bank 编辑器）的行为时，可替换创建该对话框的 Subsystem，用自定义 Dialog 替代原始 Dialog。

### 核心思路
1. EventBus 替换创建对话框的 Subsystem（如 `SubsystemMemoryBankBlockBehavior`）
2. 替换类继承原始 Subsystem，重写 `OnEditInventoryItem`/`OnEditBlock`
3. 自定义 Dialog 继承 `Dialog`（非原始 Dialog 类），从同一 XML 加载布局，但完全自建 UI 逻辑

### 关键约束
- **TextBoxWidget 是 internal 类**——跨程序集无法直接引用。用反射访问 Text 属性：
  ```csharp
  private static readonly PropertyInfo s_textProp =
      typeof(Widget).Assembly.GetType("Game.TextBoxWidget")?.GetProperty("Text");
  private static string GetTextBoxText(Widget tb) => (string)s_textProp.GetValue(tb);
  private static void SetTextBoxText(Widget tb, string value) => s_textProp.SetValue(tb, value);
  ```
- **ClickableWidget 继承 Widget（非 ContainerWidget）**——不能添加 Children。交互元素需 CanvasWidget 容器 + ClickableWidget 叠加层模式
- **构造函数必须初始化 TextBox 内容**——XML 加载的 TextBox 为空，首帧 Update 会从空 TextBox 读取覆盖数据
- **不能反射设置其他类的 private 字段**——如果当前类不包含目标字段，SetValue 会抛 ArgumentException
- **Widget.Size 仅 CanvasWidget 有**——ClickableWidget 无 Size 属性，需用容器 CanvasWidget 设置尺寸
- **CanvasWidget.SetWidgetPosition 是实例方法**——`canvasWidget.SetWidgetPosition(child, pos)`
- **StackPanelWidget.Direction 用 LayoutDirection 枚举**——`LayoutDirection.Horizontal/Vertical`
- **SignFont FontScale=1.5**——`ContentManager.Get<BitmapFont>("Fonts/SignFont")` 获取，1.0 太小看不清，2.0 溢出格子，1.5 合适

### 交互式 Widget 叠加层模式

ClickableWidget 不能有子控件，需要叠加视觉元素时：
```csharp
var container = new CanvasWidget();
container.Size = new Vector2(width, height);

var visual = new RectangleWidget(); // 视觉元素
visual.IsHitTestVisible = false;     // 不拦截点击
visual.Size = new Vector2(width, height);

var label = new LabelWidget();       // 文字标签
label.IsHitTestVisible = false;     // 不拦截点击
label.HorizontalAlignment = WidgetAlignment.Center;
label.VerticalAlignment = WidgetAlignment.Center;

var clickable = new ClickableWidget(); // 点击区域（最上层）
clickable.Tag = someData;              // 用 Tag 存储关联数据

container.Children.Add(visual);
container.Children.Add(label);
container.Children.Add(clickable);    // ClickableWidget 必须最后添加（最上层）
```

读取时通过 `ParentWidget` 回溯：
```csharp
var container = clickable.ParentWidget as ContainerWidget;
var rect = container.Children[0] as RectangleWidget;
var label = container.Children[1] as LabelWidget;
```

### 适用场景
- 修改已有对话框的功能（添加模式、按钮、视图）
- 扩展编辑器界面
- 任何需要在 Dialog 基础上添加交互式 Widget 的场景

### 参考实现
- MemoryBankDrawMod：三态视图（Linear/Grid/Draw）+ 16×16 像素绘图网格

### 数据同步方向

Dialog 中多模式切换时，数据流方向取决于当前模式：
- **Linear/Grid 模式**：TextBox → `m_tmpMemoryBankData`（SyncTextBoxesToData）
- **Draw 模式**：`m_tmpMemoryBankData` → TextBox（SyncDrawDataToTextBoxes）
- **OK 回调**：`m_tmpMemoryBankData` → `m_memoryBankData` → `StoreItemDataAtUniqueId`/`SetBlockData`

> ⚠ 切换模式前必须确保当前模式的数据已同步到 `m_tmpMemoryBankData`，否则会丢失编辑。

### 16 色映射（Memory Bank Draw）

```
0=黑(20,20,20),    1=深灰(40,40,40),  2=灰(60,60,60),    3=浅灰(80,80,80)
4=中灰(100,100,100), 5=亮灰(120,120,120), 6=淡灰(140,140,140), 7=银灰(160,160,160)
8=白(255,255,255),  9=青(0,255,255),   A=红(255,0,0),     B=蓝(0,0,255)
C=黄(255,255,0),    D=绿(0,255,0),     E=橙(255,165,0),   F=紫(160,32,240)
```

- 值 0 和 8~F：纯色填充，无文字
- 值 1~7：灰色底 + 白色 SignFont 数字（对比度不足需文字标识）

## 自定义字体与资源加载

### ContentCache + ModResource 资源加载

.scmod 内 `Content/` 下的资源由 `ModResource.LoadModResources()` 自动加载到 `ContentCache`：

| 扩展名 | 自动加载为 | ContentCache Key |
|--------|-----------|-----------------|
| `.png` / `.jpg` | `Texture2D` | `Mod/<相对路径>/<文件名>` |
| `.txt` | `string` | `Mod/<相对路径>/<文件名>` |
| `.xml` | `XElement` | `Mod/<相对路径>/<文件名>` |
| `.dae` | `Model` | `Mod/<相对路径>/<文件名>` |
| `.lst` | ❌ **不支持** | — |
| `.json` | ❌ 不自动加载 | — |

⚠ **`.lst` 不支持** → 必须重命名为 `.txt`。⚠ **`.xml` 加载为 `XElement` 而非 `string`**。若需 JSON 格式数据，用 `.txt` 扩展名（加载为 string 后自行解析）。

⚠ **ContentCache key 冲突**：同路径同文件名（去扩展名）的 `.png` 和 `.txt` 会注册为同 key 但不同类型，`ContentCache.Set` 会覆盖！命名策略：纹理用 `chinese12.png`，数据用 `chinese12data.txt`（key 分别为 `Mod/Fonts/chinese12` 和 `Mod/Fonts/chinese12data`，避免冲突）。

**Key 格式**：`Content/Fonts/MyFont.png` → `Mod/Fonts/MyFont`（去掉扩展名）

```csharp
var texture = ContentCache.Get<Texture2D>("Mod/Fonts/ChinesePericles", false);
var data = ContentCache.Get<string>("Mod/Fonts/ChinesePericlesData", false);
```

**加载时机**：必须在 `Loading.Initialize` 事件回调的末尾 Action 中执行（ContentManager.Initialize 之后）。

### BitmapFont 构建

Mod 开发者无法调用 `BitmapFont.Initialize()`（internal），但可使用 **public constructor**：

```csharp
public BitmapFont(Texture2D texture, IEnumerable<Glyph> glyphs, char fallbackCode,
    float glyphHeight, Vector2 spacing, float scale)
```

以及公开的：
- `Glyph(char code, Vector2 texCoord1, Vector2 texCoord2, Vector2 offset, float width)` — public
- `SetKerning(char code, char followingCode, float kerning)` — public
- `GetGlyph(char code)` — public
- **`Clone(float scale, Vector2 spacingOffset)`** — 共享纹理和 glyph 数组，零额外内存，只改 Scale/Spacing

### 多尺寸字体 Scale 校准（铁律）

当替换/补充游戏原生字体时，**渲染高度 = GlyphHeight × Scale**。如果自定义字体的 Scale 与原生字体不同，必须校准否则中英文字号不一致。

**校准公式**：`校准Scale = 原生Scale × 原生GH / 自定义GH`

**示例**（Pericles Scale=0.632，中文各尺寸 GH 不同）：

| 原生字体 | 原生GH | 自定义GH | 校准Scale | 渲染高度 ≈ 原生渲染高度 |
|----------|--------|----------|-----------|-------------------------|
| Pericles12 | 24 | 16 | 0.948 | 15.17 ≈ 24×0.632 |
| Pericles18 | 34 | 24 | 0.895 | 21.49 ≈ 34×0.632 |
| Pericles24 | 45 | 32 | 0.889 | 28.44 ≈ 45×0.632 |
| Pericles32 | 59 | 43 | 0.867 | 37.29 ≈ 59×0.632 |

**实现**：`rawFont?.Clone(校准Scale, Vector2.Zero)` — Clone 共享纹理，零额外内存开销。

### GlyphHeight → 中文字体映射

按 Pericles 字体的 GlyphHeight 区间选择对应的中文字体：

```csharp
// Source: StringInterceptor v1.5.0 字体切换逻辑
if (glyphHeight <= 24f) return ChineseFont12;  // Pericles12 GH=24 → chinese12 GH=16
if (glyphHeight <= 34f) return ChineseFont18;  // Pericles18 GH=34 → chinese18 GH=24
if (glyphHeight <= 45f) return ChineseFont24;  // Pericles24 GH=45 → chinese24 GH=32
return ChineseFont32;                          // Pericles32 GH=59 → chinese32 GH=43
```

| Pericles 字体 | Pericles GH | 映射到 | Chinese GH | 校准Scale |
|--------------|------------|--------|-----------|----------|
| Pericles12 | 24 | ChineseFont12 | 16 | 0.948 |
| Pericles18 | 34 | ChineseFont18 | 24 | 0.895 |
| Pericles24 | 45 | ChineseFont24 | 32 | 0.889 |
| Pericles32 | 59 | ChineseFont32 | 43 | 0.867 |

### .lst 字体数据格式

游戏原生字体使用 `.lst` 格式定义 glyph 坐标。格式（Source: BitmapFont.cs Initialize）：

```
<glyph_count>                           # 第 1 行：glyph 总数
<char> <texL> <texT> <texR> <texB> <offX> <offY> <advance>  # × glyph_count
<glyph_height>                          # 单个数字
<spacing_x> <spacing_y>                 # 两个浮点数
<scale>                                 # 单个浮点数
<fallback_code>                         # 单个字符（如 ?）
<kerning_count>                         # kerning 对数
<char> <following_char> <amount>        # × kerning_count
```

- **tex 坐标**：归一化 0-1，直接对应纹理 UV
- **offset/advance**：像素值，未缩放
- **空格字符**：行首字段为空，Split 后变为 7 个字段（需补 `" "` 占位）

**⚠️ 注意**：`m_glyphsByCode` 是 internal，Mod 中不能直接访问。用 `GetGlyph()` 代替。

### StringInterceptor 管线模式

一种通过字符串管道拦截 + 翻译 + 字体切换的 Mod 架构：

```
StringsManager.m_strings  ──→  ProcessStrings() 一次性处理
ScreensManager.RootWidget ──→  Timer + Dispatcher 每帧扫描
           │
    IStringProcessor.Pipeline()
           │
    ├── TranslationProcessor   ("Play" → "开始游戏")
    ├── DefaultStringProcessor  ("[N] 原文")
    └── ContainsChinese() → TrySetChineseFont()
```

**核心模式**：
1. `IStringProcessor` 接口 — `string Process(string key, string original, int index)`
2. 处理器链 `List<IStringProcessor>` 顺序调用
3. `ContainsChinese()` — 检测 U+4E00–U+9FFF 范围
4. Widget 扫描去重 — `HashSet<Widget>` 引用追踪，防重复处理

**注意**：
- Timer 在 ThreadPool 线程，UI 操作必须 `Dispatcher.Dispatch` 回主线程
- `Dispatcher.Dispatch` 在主线程调用时**同步执行**（见 Dispatcher.cs:31-34）
- 帧去重用 `Time.FrameIndex` 而非时间戳

### 翻译收集系统

StringInterceptor 的翻译收集输出到 `Logs/zh_CN.xml`：

**XML 结构**（Screen 分组 + 组内 ABC 排序）：
```xml
<Screen Name="Play">
  <Entry Original="Play" Translation="开始游戏"/>
  <Entry Original="Settings" Translation="设置"/>
</Screen>
<Screen Name="StringsManager">
  <Entry Original="Loading..." Translation="加载中..."/>
</Screen>
```

**追加合并模式**：`SaveCollected()` 加载已有 `Logs/zh_CN.xml` → 合并新条目（去重 by Original）→ 写回，不覆盖已有翻译。

**首运行播种**：若 `Logs/zh_CN.xml` 不存在，从 .scmod 内 `Content/zh_CN.xml` 复制一份作为种子。

**Screen 分类来源**：
- `ProcessStrings()` → `"StringsManager"`
- Widget scanner → `ScreensManager.CurrentScreen.GetType().Name`

**兼容性**：`LoadTranslations()` 兼容新旧两种 XML 格式（`<Screen>` 分组格式 和 旧 `<Entry>` 扁平格式）。

## SimpleJson（游戏内置 JSON 库）

Survivalcraft 内部包含 `SimpleJson.SimpleJson`（internal class），位于 Engine.dll 或 Survivalcraft.dll。Mod 可通过反射调用其静态方法：

```csharp
// 反射定位 SimpleJson 类型
var asm = typeof(Game.ScreensManager).Assembly;
var simpleJsonType = asm.GetType("SimpleJson.SimpleJson");

// 反序列化字典
var deserializeMethod = simpleJsonType.GetMethods()
    .First(m => m.Name == "DeserializeObject" && m.IsGenericMethod)
    .MakeGenericMethod(typeof(Dictionary<string, string>));
var dict = deserializeMethod.Invoke(null, new[] { jsonString }) as Dictionary<string, string>;

// 序列化
var serializeMethod = simpleJsonType.GetMethods()
    .First(m => m.Name == "SerializeObject" && !m.IsGenericMethod
        && m.GetParameters().Length == 1);
string json = (string)serializeMethod.Invoke(null, new[] { obj });
```

**关键注意事项**：
- **`GetMethod` 会抛 `AmbiguousMatchException`**：SimpleJson 有两个 `DeserializeObject(string)` 重载（泛型+非泛型），`GetMethod` 无法区分。必须用 `GetMethods()` 遍历 + `IsGenericMethod` 筛选
- **BOM 敏感**：带 UTF-8 BOM 的 JSON 字符串会导致 SimpleJson 返回 null。写入 JSON 用 `new UTF8Encoding(false)`，确保源文件无 BOM
- **不支持 `.json` 扩展名**：ModResource 不会自动加载 `.json` 文件。改用 `.txt` 扩展名，加载为 string 后自行解析

### DLL 字符串验证

.NET DLL 中字符串为 UTF-16LE 编码。编译后验证 DLL 是否含新代码时，UTF-8 搜索搜不到中文和标识符：

```powershell
# 正确：UTF-16LE 搜索
[System.Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes("path\to\MyMod.dll")).IndexOf("特征字符串")

# 错误：UTF-8 搜索搜不到
```

> const float 等常量被 JIT 内联为浮点指令，字符串搜索搜不到常量名，但日志字符串（如 `Log.Information("...")` 中的字面量）可搜。

## Android 移植调试 (SurvivalcraftMonodroid, net10.0-android)

### 环境
- 项目: `P:\UGIT\SurvivalcraftMonodroid` (独立于 SurvivalcraftMonoWin)
- 目标: ARM64 真机 (NX729J, Adreno 740, Android 15) + x86_64 模拟器
- 文档: `P:\UGIT\SurvivalcraftMonodroid\doc\PROJECT-LOG.md`

### 已知陷阱与修复

#### GLES Context 版本
`Engine/Platforms/Android/Window.cs` 创建 OpenGL 上下文时 **必须显式指定版本号**：
```csharp
// ❌ GLES 1.1 上下文（不支持 shader）→ glCreateShader = NULL 函数指针 → SIGSEGV
GraphicsAPI api = new(ContextAPI.OpenGLES, new APIVersion());

// ✅ GLES 2.0+ 上下文
GraphicsAPI api = new(ContextAPI.OpenGLES, new APIVersion(2, 0));
```
Windows 端对应代码位于 `Engine/Platforms/Windows/Window.cs`，使用 `APIVersion(3, 2)`。

#### 静态 UnlitShader 字段
`BaseFlatBatch.cs` / `BaseTexturedBatch.cs` / `BaseFontBatch.cs` 的静态字段初始化在类加载时即编译 shader，若此时 GLES 版本不支持则崩溃。应改为 lazy-loading：
```csharp
// ❌ 类加载时就 new UnlitShader(...) → CompileShaders() → GL.CreateShader()
internal static UnlitShader m_shader = new UnlitShader(useVertexColor: true, ...);

// ✅ 延迟到首次 Flush 时创建
private static UnlitShader s_shader;
internal static UnlitShader GetShader()
{
    if (s_shader == null)
        s_shader = new UnlitShader(useVertexColor: true, ...);
    return s_shader;
}
```

#### `mono_runtime_class_init_full` + `pc=0` 崩溃诊断
此崩溃签名 = ARM64 Mono 运行时在类初始化时调到 NULL 地址。常见原因：
1. **P/Invoke 目标不存在** — `DllImport` 声明的函数在 native lib 中不存在 (如 GLES 1.1 无 `glCreateShader`)，mono 填 NULL 不报错
2. **AOT 编译 bug** — ARM64 的 AOT 编译器生成错误机器码
3. **GL 上下文版本不匹配** — 请求了错误版本的 OpenGL 上下文

排查流程：
1. 逐层加 `Log.Information` 步进日志 (粗→细)
2. 对比 x86_64 和 ARM64 行为差异
3. 检查 native .so 完整性 (`unzip -l app.apk | grep arm64`)
4. 检查 GL 上下文初始化参数

#### Android 调试工具链
```bash
# 安装与启动
adb -s FY231741055D push <apk> /data/local/tmp/sc2.apk
adb -s FY231741055D shell pm install -r /data/local/tmp/sc2.apk
adb -s FY231741055D shell am start -n com.candyrufus.survivalcraft2/crc64138d896b0a338567.MainActivity

# 日志 (logcat 在 Android 15 上不可用)
adb -s FY231741055D shell "run-as com.candyrufus.survivalcraft2 cat files/Documents/Logs/Game.log"

# Native crash 分析
adb -s FY231741055D shell dumpsys dropbox data_app_native_crash --print

# 权限
adb -s FY231741055D shell appops set com.candyrufus.survivalcraft2 MANAGE_EXTERNAL_STORAGE allow
```

#### 构建与签名
```bash
cd P:\UGIT\SurvivalcraftMonodroid
dotnet build Survivalcraft\Survivalcraft.csproj -f net10.0-android -c Debug
# 签名: ~/.android/debug.keystore, alias=androiddebugkey, pw=android
```

## MAUI 移植版 Windows 端调试 (Survivalcraft, net10.0-windows)

### 环境
- 项目: `P:\UGIT\SurvivalcraftMonoWin` (WindowsandAndroid 分支)
- GPU: AMD Radeon RX 7900 XTX, OpenGL ES 3.2.0
- 文档: `P:\UGIT\Survivalcraft\doc\PROJECT-LOG.md`
- 构建命令: `& $msbuild "Survivalcraft\Survivalcraft.csproj" -t:Build -p:TargetFramework=net10.0-windows10.0.19041.0 -v:minimal`
- 编译符号 `WINDOWS` 由 `net10.0-windows` 目标框架自动定义

### 核心问题：opengl32.dll 只有 GL 1.1

Monodroid 分支将原始 OpenTK 替换为手写 `DllImport` 绑定（仅 Android 有效）。Windows 上 `opengl32.dll` 只导出 GL 1.1 核心函数，`glCreateShader` 等 GL 2.0+ 函数不在导出表中，`DllImport("opengl32")` 报 `Unable to find an entry point`。

**必须通过 `wglGetProcAddress` 动态获取扩展函数指针**——DllImport 不支持此机制。

### 解决方案：Silk.NET.OpenGL 双平台绑定

项目已引用 Silk.NET 2.23.0（含 Silk.NET.OpenGL），Windows 端用 Silk.NET 自动处理扩展加载：

```
Engine/Graphics/Bindings/
├── GL.cs            (#if !WINDOWS — Android DllImport 实现)
└── GL.Windows.cs    (#if WINDOWS — Silk.NET.OpenGL 代理实现)
```

#### GL.Windows.cs 实现要点

```csharp
#if WINDOWS
using SGLE = Silk.NET.OpenGL.GLEnum;

namespace Engine.Graphics.Bindings
{
    public unsafe static class GL
    {
        private static Silk.NET.OpenGL.GL G;

        public static void Initialize(Silk.NET.OpenGL.GL gl) => G = gl;

        // 枚举强转
        public static void Enable(EnableCap cap) => G.Enable((SGLE)cap);

        // Clear 接受 uint（非 GLEnum）
        public static void Clear(ClearBufferMask mask) => G.Clear((uint)mask);

        // ClearDepth 有 float+double 双重重载，直接传 float
        public static void ClearDepth(float depth) { G.ClearDepth(depth); }

        // Gen 系列用 stackalloc uint[] 做 int↔uint 中转
        public static void GenTextures(int n, int* textures)
        {
            uint* u = stackalloc uint[n];
            G.GenTextures((uint)n, u);
            for (int i = 0; i < n; i++) textures[i] = (int)u[i];
        }

        // ShaderSource 差异大：Silk.NET 用 string，Engine 用 string[]+int*
        public static void ShaderSource(int shader, int count, string[] source, int* length)
        {
            for (int i = 0; i < count; i++)
                G.ShaderSource((uint)shader, source[i]);
        }
        // ... 72+ 方法
    }
}
#endif
```

#### 枚举映射表

| Engine 枚举 | Silk.NET 枚举 | 说明 |
|-------------|--------------|------|
| EnableCap | GLEnum 强转 | 直接 `(SGLE)cap` |
| ClearBufferMask | uint | `Clear()` 接受 uint，非 GLEnum |
| BufferTarget | BufferTargetARB | 名称不同 |
| BufferUsageHint | BufferUsageARB | 名称不同 |
| FramebufferSlot | FramebufferAttachment | 名称不同 |
| RenderbufferStorage | InternalFormat | 名称不同 |
| ShaderParameterName | ShaderParameterName | 同名但类型不同 |
| ProgramParameterName | ProgramPropertyARB | 名称不同 |

### DllImport 库名条件编译

GL + OpenAL 绑定文件需按平台切换库名：

```csharp
// GL.cs
#if WINDOWS
private const string Library = "opengl32";
#else
private const string Library = "libGLESv2";
#endif

// AL.cs / Alc.cs / OpenAL.cs
#if WINDOWS
private const string Library = "openal32.dll";
#else
private const string Library = "libopenal.so";
#endif
```

### Window.cs 初始化

```csharp
// Engine/Platforms/Windows/Window.cs — InitializeAll() 开头
var silkGL = Silk.NET.OpenGL.GL.GetApi(m_view.GLContext);
Engine.Graphics.Bindings.GL.Initialize(silkGL);
```

### 其他 WindowsandAndroid 修复

| 问题 | 根因 | 修复 |
|------|------|------|
| GamePad.BeforeFrame NullReferenceException | InitializeAll try-catch 吞异常 | `if (m_gamepads == null) return;` |
| ContentManager Mods 目录不存在崩溃 | Directory.GetFiles 抛异常 | Directory.Exists + CreateDirectory |
| VS 启动打开 Android 模拟器 | csproj TargetFrameworks 顺序 | Windows 优先 + launchSettings.json |
| NativeLibrary resolver 方案 | 只解析库级不解析函数级 | 不可行，改用 Silk.NET |

### 铁律（Windows OpenGL 绑定）

1. **opengl32.dll 只有 GL 1.1** — GL 2.0+ 扩展函数必须 `wglGetProcAddress`，DllImport 不行
2. **Silk.NET.OpenGL 自动处理扩展加载** — `GL.GetApi(context)` 一步到位
3. **GLEnum 强转** — `(Silk.NET.OpenGL.GLEnum)value`，但 `Clear()` 用 `(uint)`
4. **ClearDepth/DepthRange 有 float+double 双重重载** — 直接传 float/double 不强转
5. **int↔uint 中转** — Gen/Delete 系列用 `stackalloc uint[]` + 循环转换
6. **ShaderSource 差异** — Silk.NET 接受 string，Engine 用 string[]+int*，需逐个调用
7. **`unsafe class`** — GL.Windows.cs 必须 unsafe（有指针操作）
8. **`WINDOWS` 符号自动定义** — net10.0-windows 框架自带
9. **NativeLibrary resolver 不可行** — 函数级解析（wglGetProcAddress）不走 resolver
10. **双平台编译验证** — 每次修改后同时验证 Windows 和 Android 编译

## MAUI 移植版 Android 端 UI 与屏幕尺寸 (2026-05-24)

### Silk.NET vs OpenTK 屏幕尺寸差异（铁律）

| 属性 | OpenTK (旧版) | Silk.NET (新版) | 说明 |
|------|--------------|----------------|------|
| `Window.Size` | `View.Size.Width/Height` (EGL 物理像素) | `m_view.FramebufferSize.X/Y` (物理像素) | 用于 GL viewport，必须用物理像素 |
| `Window.ScreenSize` | `DisplayMetrics.WidthPixels/HeightPixels` | **必须用 DisplayMetrics** | Silk.NET `m_view.Size` 是逻辑像素(dp)，不是物理像素 |
| `Window.Scale` | `DisplayMetrics.Density` | Android: `DisplayMetrics.Density`; Windows: `FramebufferSize.X/Size.X` | 缩放因子 |

**关键差异**：
- **Silk.NET `m_view.Size` = 逻辑像素(dp)**，如 720×1600dp。**不是** DisplayMetrics 的物理像素
- **Silk.NET `m_view.FramebufferSize` = 物理像素**，用于 GL 渲染目标
- `Window.ScreenSize` 必须用 `Activity.Resources.DisplayMetrics.WidthPixels/HeightPixels`，**不用 `m_view.Size`**

### UI 缩放计算链

```
Window.Size(物理像素) → Display.BackbufferSize → Display.Viewport
    ↓
ScreensManager.Draw: num = 850 / UIScale * DebugUiScale
    ↓
num2 = ViewportWidth / num  → LayoutTransform scale
```

- 虚拟坐标空间 850×382.5，映射到物理像素
- 若 Window.Size 返回逻辑像素(dp) 而非物理像素 → UI 元素过小（约 1/3~1/2）

### UIScale 默认值（铁律）

**合并时丢失的经验**：`SettingsManager.Initialize()` 中 UIScale 默认值必须根据屏幕尺寸条件判断，**不能硬编码**：

```csharp
// ❌ 硬编码（合并时丢失了条件逻辑）
UIScale = 0.7f;

// ✅ 参考项目正确逻辑
UIScale = ((!(ScreenResolutionManager.ApproximateScreenInches > 6.5f)) ? 1f : ((ScreenResolutionManager.ApproximateScreenInches > 9f) ? 0.7f : 0.85f));
```

| 屏幕尺寸 | UIScale | 显示 | 典型设备 |
|----------|---------|------|----------|
| ≤ 6.5" | 1.0 | 100% | 手机 |
| 6.5"~9" | 0.85 | 85% | 小平板 |
| > 9" | 0.7 | 70% | 大平板 |

**已有存档注意**：`Settings.xml` 中保存的旧值会被 `LoadSettings()` 加载覆盖默认值。如果旧存档保存了 0.7，需手动在设置界面调到 100% 或删除 Settings.xml 重置。

### ScreenResolutionManager

```csharp
// Android
public static float ApproximateScreenDpi => DisplayMetrics.DensityDpi;  // 初始化时从 Activity 获取
public static float ApproximateScreenInches => √(W²+H²) / ApproximateScreenDpi;

// Windows
public static float ApproximateScreenDpi => RawDpiX/Y 的平均值;  // DisplayInformation.GetForCurrentView()
```

- Android 静态构造函数中初始化（依赖 `Window.Activity`）
- Windows 需手动调用 `Initialize()`（依赖 UI 线程的 `DisplayInformation`）

### AndroidManifest 必需配置

```xml
<application
    android:theme="@android:style/Theme.Black.NoTitleBar.Fullscreen"
    android:hardwareAccelerated="true"
    android:usesCleartextTraffic="true"
    android:icon="@drawable/androidicon">
    <!-- usesCleartextTraffic: Android 9+ 默认禁止明文 HTTP，游戏连 files.kaalus.com 需要 -->
</application>
<uses-permission android:name="android.permission.VIBRATE" />
```

### Android 部署调试

| 方式 | Game.log 读取 | 可靠性 |
|------|-------------|--------|
| VS 调试部署 | `run-as ... cat files/Documents/Logs/Game.log` | ✅ 完整处理 Fast Deployment |
| 手动 `adb install` Debug | ❌ Fast Deployment 不完整 | Incremental Install 缺少 managed DLL |
| 手动 `adb install` Release | ❌ `run-as: package not debuggable` | 完整 APK 但无法调试 |
| `dotnet run` Debug | ✅ | 但有时 FastDeploy 失败 |

**建议**：用 VS 调试部署，不要手动 adb 操作。卸载后重装也通过 VS 完成。

**Game.log 路径**：`adb shell "run-as com.candyrufus.survivalcraft2 cat files/Documents/Logs/Game.log"`

## 参考文档

- **代码分析工作流**：见 [references/code-analysis-workflow.md](references/code-analysis-workflow.md) — 四步严谨流程（结构感知→细节穷举→代码生成→自我验证），含分析报告模板、边界探测清单、即用型提示词模板
- **API 详细参考**：见 [references/sumod-api.md](references/sumod-api.md) — 完整接口定义与方法签名
- **Mod 示例分析**：见 [references/mod-examples.md](references/mod-examples.md) — 现有 Mod 的详细分析

## WindowsandAndroid 分支 Android 输入系统完整架构 (2026-05-23 ~ 2026-05-24)

### 输入数据流

```
[硬件触摸屏] → [Android Activity] → [DispatchTouchEvent/DispatchKeyEvent/...]
       │                                      │
       │                      ┌────────────────┴──────────────────┐
       │                      │  源过滤: InputSourceType 位掩码    │
       │                      │  Touchscreen → Touch              │
       │                      │  Gamepad/Jostick → GamePad        │
       │                      │  Keyboard → Keyboard              │
       │                      │  Mouse → Mouse                    │
       │                      └────────────────┬──────────────────┘
       │                                      │
       │                      Touch.HandleTouchEvent(MotionEvent)
       │                              │
       │                    ConcurrentQueue<TouchInfo>
       │                           (UI线程只入队)
       │                              │
       │  ═══ 线程边界 ═══            │
       │                              │
       │                      Touch.BeforeFrame()
       │                    (游戏线程 drain + 原子处理)
       │                              │
       │                      Touch.TouchLocations
       │                      (完整一致的状态表)
       │                              │
       ├──────────────────────────────┤
       │              │               │
  WidgetInput    TouchInputWidget  每个Widget的Update
 (UI层)          (游戏控件: 移动/视角/按钮)
```

### 核心原则：ConcurrentQueue + BeforeFrame 批量消费

```csharp
// ❌ 原始架构（UI线程直接改m_touchLocations）—— 竞态条件
internal static void HandleTouchEvent(MotionEvent e)
{
    ProcessTouchPressed(...);  // 在UI线程直接改List
    ProcessTouchMoved(...);
    ProcessTouchReleased(...);
}

// ✅ SCAPI1.9架构（ConcurrentQueue线程安全缓冲）
struct TouchInfo { int PointerId; Vector2 Position; int ActionMasked; }

internal static void HandleTouchEvent(MotionEvent e)
{
    m_cachedTouchEvents.Enqueue(new TouchInfo(...));  // UI线程只入队
}

internal static void BeforeFrame()
{
    while (!m_cachedTouchEvents.IsEmpty)
    {
        if (m_cachedTouchEvents.TryDequeue(out TouchInfo info))
        {
            switch (info.ActionMasked) // 1:down, 2:move, 3:up
            {
                case 1: ProcessTouchPressed(info.PointerId, info.Position); break;
                case 2: ProcessTouchMoved(info.PointerId, info.Position); break;
                case 3: ProcessTouchReleased(info.PointerId, info.Position); break;
            }
        }
    }
}
```

**为什么必须这样做**：
- UI线程处理触摸事件（DispatchTouchEvent调用），游戏线程运行Update()/AfterFrame()
- 如果UI线程在Update()和AfterFrame()之间新增/修改touch → AfterFrame的状态转换基于不一致数据
- **多指触控致命场景**：双指同时操作（一滑一点），UI线程在Update前后交错处理两指事件 → 点击的手指DOWN+UP之间刚好被Update()"跳过" → WidgetInput从未见过Pressed状态 → Tap永久丢失
- BeforeFrame在Update前一次性drain所有pending事件 → Update()总是看到完整touch状态表

### 源过滤路由

```csharp
// Source: SCAPI1.9 EngineActivity.cs — 必须用Dispatch级别

public override bool DispatchTouchEvent(MotionEvent e)
{
    if ((e.Source & InputSourceType.Touchscreen) == InputSourceType.Touchscreen)
        Touch.HandleTouchEvent(e);
    else if (mouse source) Mouse.HandleMotionEvent(e);
    return true;  // 返回true=消费事件，阻止SDL2二次处理
}

public override bool DispatchKeyEvent(KeyEvent e)
{
    // Gamepad/Joystick → GamePad.HandleKeyEvent; else → Keyboard
    return base.DispatchKeyEvent(e);  // 不能return true!
}

public override bool DispatchGenericMotionEvent(MotionEvent e)
{
    // Gamepad → GamePad.HandleMotionEvent; Mouse → Mouse.HandleMotionEvent
    return true;
}
```

**关键陷阱**：
- `return base.DispatchTouchEvent(e)` → SDL2也处理事件 → 触摸double-dip → 白点+双重响应
- `return true` → 正确消费，只我们自己处理
- `DispatchKeyEvent` 不能return true → Back/Volume需要Android继续处理

### 六种已知触摸问题及修复

| # | 问题 | 根因 | 修复 |
|---|------|------|------|
| 1 | 点击~25%丢失 | ProcessTouchReleased直接设Released跳过ReleaseQueued | 统一用 ReleaseQueued=true |
| 2 | 白点飞过屏幕 | WidgetInput.Draw()渲染软鼠标光标 | `#if ANDROID` 跳过Draw() |
| 3 | 进地图冻结 | Lit.psh/vsh被识别为立陶宛语卫星程序集 | `EmbeddedResource WithCulture="false"` |
| 4 | 角色自动向斜下移动 | DispatchTouchEvent把坐标转发给GamePad | 删除该行，源过滤分离 |
| 5 | 手柄按键伪注册 | DispatchKeyEvent全送GamePad→摇杆光标模式 | 源过滤路由 |
| 6 | **滑动时点击按钮无反应** | UI线程改m_touchLocations vs 游戏线程Update竞态 | ConcurrentQueue+BeforeFrame |

### Touch 状态机铁律

1. **ProcessTouchMoved 必须无条件更新位置** — 状态检查（`State==Moved`）与AfterFrame时序冲突，帧内MOVE被吞
2. **ProcessTouchReleased 必须统一使用 ReleaseQueued** — 直接设Released = AfterFrame可能下帧立即Remove
3. **TouchLocations 是唯一消费端数据源** — 事件可能无人订阅，Widget直接遍历TouchLocations
4. **AfterFrame 在帧末执行** — Pressed→Moved 延迟一整帧，Downtime位置须实时更新
5. **Android touch 路由用 Dispatch 级别** — 不是OnTouchEvent，自定义处理必须走DispatchTouchEvent
6. **避免 base.DispatchTouchEvent(e)** — 会让SDL2再次处理 → 双重事件
7. **DispatchKeyEvent 不能 return true** — Back/Volume 系统键需要Android处理

### WidgetInput 多指触控局限

```csharp
// WidgetInput 只有一个 m_touchId → 多指触控时后按的覆盖先按的
int? m_touchId;
foreach (TouchLocation t in TouchLocations) {
    if (t.State == Pressed)  { m_touchId = t.Id; /* 覆盖! */ }
    if (t.State == Moved && m_touchId != t.Id) continue; // 原有触摸被跳过
}
```
- **WidgetInput 不支持真正多指触控** — 这是SCAPI1.9已知局限
- **TouchInputWidget 各自独立 m_touchId** — 每个实例(Move/Look/Button)独立跟踪
- 实践结论：游戏内动作按钮用 TouchInputWidget，UI按钮用 WidgetInput

### Windows 鼠标输入

- `Window.InitializeAll()` → `m_inputContext = m_view.CreateInput()` → 订阅 `m_inputContext.Mice[0].MouseDown/Up/Move/Scroll += ...`
- Mouse → `ProcessMouseDown/Up/Move/Wheel`（原为private，改为internal）
- `MouseWheelMovement` 用 `scrollWheel.Y * 120f` 转换，累加模式（`+= delta`）
- **MouseMovement 必须用轮询计算**，不能依赖事件差值

#### 鼠标轮询修复（铁律）

**问题**：CursorMode.Raw 下光标锁定在窗口中心，MouseMove 事件的 position 在中心附近不变，`MouseMovement = position - lastPosition` delta 极小，视角几乎不动。

**修复**：恢复 BeforeFrame 轮询模式，在 Window.BeforeFrameAll 中调用 `Mouse.PollMousePosition(IMouse.Position)`，用 `m_pollLastPosition` 缓存上帧位置计算 delta。

```csharp
// Mouse.cs
internal static void PollMousePosition(Point2 position)
{
    if (m_pollLastPosition.HasValue)
        MouseMovement = position - m_pollLastPosition.Value;
    if (!IsMouseVisible)
    {
        m_pollLastPosition = position;
        MousePosition = null;  // 第一人称不显示光标
    }
    else
    {
        m_pollLastPosition = position;
        MousePosition = position;
    }
}

// ProcessMouseMove 不再计算 MouseMovement，只更新 MousePosition + 触发事件
internal static void ProcessMouseMove(Point2 position)
{
    if (Window.IsActive && !Keyboard.IsKeyboardVisible)
    {
        if (IsMouseVisible) MousePosition = position;
        MouseMove?.Invoke(new MouseEvent { Position = position });
    }
}
```

**对比**：

| 特性 | OpenTK（旧版） | Silk.NET 纯事件（错误） | Silk.NET 轮询（正确） |
|------|--------------|----------------------|---------------------|
| MouseMovement | BeforeFrame 轮询差值 | ProcessMouseMove 事件差值 | PollMousePosition 轮询差值 |
| Raw 下 position | 系统光标自由移动 | 光标锁定中心，delta≈0 | IMouse.Position 内部累加 |
| BeforeFrame | 轮询 Mouse.GetState() | 空 | PollMousePosition |

**DLL 搜索注意**：.NET DLL 元数据用 UTF-8 编码存储标识符，不是 UTF-16LE。

**禁止热路径日志**：Debug.WriteLine/Console.WriteLine 在 ProcessMouseMove 等每帧调用的方法中会导致严重卡顿。调试日志→验证→立即移除。

### 调试铁律

1. **Mouse/Touch Initialize 空方法** — 不报错没告警，必须主动验证事件订阅链
2. **对比 Keyboard 实现** — Keyboard正确订阅了Silk.NET事件，Mouse/Touch空壳即有问题
3. **Android activity 输入必须重写** — 继承SilkActivity后手动DispatchTouchEvent/DispatchKeyEvent
4. **Loading 错误对话框需触屏** — Screen构造失败弹MessageDialog，触屏异常=永远卡加载
5. **验证方法** — Log.Information → build → install → 操作 → 查 Game.log
6. **文件修改未生效排查** — 确认 DLL 在 APK 中 + confirm AOT .so 含新字符串

### GLFW 窗口图标设置

GLFW 窗口不会自动继承 exe 内嵌图标资源，必须显式通过 Win32 API 设置：

```csharp
// Engine/Platforms/Windows/Window.cs — 在 LoadHandler 开头调用
static void SetWindowIconWin32()
{
    IntPtr hwnd = Handle; // m_view.Native.Win32.Hwnd
    if (hwnd == IntPtr.Zero) return;

    IntPtr hModule = Marshal.GetHINSTANCE(typeof(Window).Assembly.GetType().Module);
    // 先从 exe 资源加载（ApplicationIcon 嵌入的 .ico）
    IntPtr hIconSmall = LoadImage(hModule, (IntPtr)1, 14, 16, 16, 0x40);
    IntPtr hIconBig = LoadImage(hModule, (IntPtr)1, 14, 32, 32, 0x40);

    // Fallback: 从 Engine 嵌入资源流加载 ICO，用 CreateIconFromResourceEx 从内存创建 HICON
    if (hIconSmall == IntPtr.Zero || hIconBig == IntPtr.Zero)
    {
        using var stream = typeof(Window).Assembly.GetManifestResourceStream("Engine.Resources.icon.ico");
        if (stream != null)
        {
            byte[] icoBytes = new byte[stream.Length];
            stream.Read(icoBytes, 0, icoBytes.Length);
            // 解析 ICO: ICONDIR(6B) + ICONDIRENTRY[Count](16B each)
            int iconCount = icoBytes[4] | (icoBytes[5] << 8);
            for (int i = 0; i < iconCount; i++)
            {
                int entryOffset = 6 + i * 16;
                int w = icoBytes[entryOffset] == 0 ? 256 : icoBytes[entryOffset];
                int h = icoBytes[entryOffset + 1] == 0 ? 256 : icoBytes[entryOffset + 1];
                int dataSize = icoBytes[entryOffset + 8] | (icoBytes[entryOffset + 9] << 8) | (icoBytes[entryOffset + 10] << 16) | (icoBytes[entryOffset + 11] << 24);
                int dataOffset = icoBytes[entryOffset + 12] | (icoBytes[entryOffset + 13] << 8) | (icoBytes[entryOffset + 14] << 16) | (icoBytes[entryOffset + 15] << 24);
                byte[] iconData = new byte[dataSize];
                Array.Copy(icoBytes, dataOffset, iconData, 0, dataSize);
                // 尺寸匹配: 16→small, 32→big
                if (w == 16 && hIconSmall == IntPtr.Zero)
                    hIconSmall = CreateIconFromResourceEx(iconData, (uint)dataSize, true, 0x00030000, 16, 16, 0);
                if (w == 32 && hIconBig == IntPtr.Zero)
                    hIconBig = CreateIconFromResourceEx(iconData, (uint)dataSize, true, 0x00030000, 32, 32, 0);
            }
        }
    }

    if (hIconSmall != IntPtr.Zero) SendMessage(hwnd, 0x0080, (IntPtr)0, hIconSmall);
    if (hIconBig != IntPtr.Zero) SendMessage(hwnd, 0x0080, (IntPtr)1, hIconBig);
}

// P/Invoke — ⚠ LoadImage 不能声明为两个不同名字的函数！
[DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr hInst, IntPtr name, uint uType, int cx, int cy, uint flags);
[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true, EntryPoint = "LoadImage")] private static extern IntPtr LoadImageFromFile(IntPtr hInst, string name, uint uType, int cx, int cy, uint flags);
[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr CreateIconFromResourceEx(byte[] pbIconBits, uint cbIconBits, bool fIcon, uint dwVersion, int cxDesired, int cyDesired, uint uFlags);
```

**关键陷阱**：
1. **HWND=0 在 Create 之后** — `Window.Create()` 后 HWND 为 0，必须等 `LoadHandler` 内（GLFW 初始化完毕）才能获取
2. **P/Invoke 同名函数** — `LoadImage` 在 user32.dll 只有一个入口，不能声明为 `LoadImageFromResource` 之类的假名字。必须用 `EntryPoint="LoadImage"` + 不同参数类型声明重载
3. **exe 资源加载可能失败** — .NET 打包方式可能导致 `LoadImage(hModule, (IntPtr)1, RT_GROUP_ICON=14, ...)` 返回 0。Fallback 改为从嵌入资源流加载（`Engine.Resources.icon.ico`），用 `CreateIconFromResourceEx` 从内存字节直接创建 HICON，无需输出目录中的单独文件

**csproj 配置**：
```xml
<ApplicationIcon>Resources.icon.ico</ApplicationIcon>  <!-- 嵌入 exe 图标 -->
<!-- 不需要 Content CopyToOutputDirectory，fallback 用 Engine 嵌入资源流 + CreateIconFromResourceEx -->
```

### F11 全屏切换

在 LoadHandler 中订阅 Keyboard.KeyDown 事件，检测 F11 切换 GLFW 窗口全屏/窗口模式：

```csharp
// Engine/Platforms/Windows/Window.cs — LoadHandler 中
Engine.Input.Keyboard.KeyDown += (key) =>
{
    if (key == Engine.Input.Key.F11)
    {
        if (m_gameWindow.WindowState == Silk.NET.Windowing.WindowState.Fullscreen)
            m_gameWindow.WindowState = Silk.NET.Windowing.WindowState.Normal;
        else
            m_gameWindow.WindowState = Silk.NET.Windowing.WindowState.Fullscreen;
    }
};
```

**注意**：原始 UWP 版使用 Borderless 模式实现全屏（UWP 框架自动管理标题栏行为），Win32 桌面版需自行实现全屏切换。

### MAUI 模板清理

新建 MAUI 项目必须清理模板默认资源，否则运行时看到 .NET 紫色图标和 dotnet_bot：

| 删除 | 替换为 | 位置 |
|------|--------|------|
| `appicon.svg` + `appiconfg.svg` | `appicon.png` (SC图标) | `Resources/AppIcon/` |
| `splash.svg` | `splash.png` (SC图标) | `Resources/Splash/` |
| `dotnet_bot.png` | 无 | `Resources/Images/` |

**csproj 对应修改**：
```xml
<MauiIcon Include="Resources\AppIcon\appicon.png" Color="#000000" />
<MauiSplashScreen Include="Resources\Splash\splash.png" Color="#000000" BaseSize="310,310" />
<!-- 删除 <MauiImage Include="Resources\Images\dotnet_bot.png" /> -->
```

### Android 图标补充

参考原始 APK，需在 `Platforms/Android/Resources/drawable-xxhdpi/` 添加高分辨率图标：
- `androidicon.png` (144×144)
- `androidicontrial.png` (144×144)

### Windows 资源图标

参考原始 MSIX，`Platforms/Windows/Resources/` 需添加：
- `Logo.png` (150×150)
- `SmallLogo.png` (30×30)
- `SplashScreen.png` (620×300)
- `Square310x310Logo.png` (310×310)
- `StoreLogo.png` (50×50)
- `Wide310x150Logo.png` (310×150)

### 构建输出语言文件夹清理

WinUI 的 `Microsoft.UI.Xaml.dll.mui` 多语言资源文件不受 `SatelliteResourceLanguages` 控制（只控制 .NET 卫星程序集）。需在 csproj 中添加 Build Target 清理：

```xml
<Target Name="RemoveUnusedLanguageFolders" AfterTargets="Build" Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'">
    <Exec Command="powershell -NoProfile -Command &quot;Get-ChildItem '$(OutDir)' -Directory | Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z]{3,4})?-[A-Z]{2}$|^[a-z]{2}(-[A-Z]{2,4})?$' } | Remove-Item -Recurse -Force&quot;" IgnoreExitCode="true" />
</Target>
```

正则覆盖：`xx`(2字母), `xx-XX`(语言-国家), `xx-Latn-XX`(三段式) 格式。
