# SuAPI Example Mod Set

Survivalcraft 2 SuAPI Mod 示例集合，演示 SuAPI 接口的各种用法。

[English README](README_EN.md)

## 项目特性

- **net8.0** — 所有 Mod 基于 .NET 8.0，SDK 样式 csproj
- **SuAPI 接口** — 通过 IModEventBus / IModInjector / IModParentField / IModParentMethod / IModResource 调整游戏行为，不修改原始代码
- **IsMergeLib** — 默认并优先使用 `IsMergeLib=true`（DLL 放 `Lib/`，双端共用）；只有明确要求平台专用程序集时才使用 `false`（按平台放 `Lib/X64` + `Lib/Arm64`）
- **Python zipfile 打包** — .scmod 必须用 Python zipfile 打包，确保正斜杠路径

## 使用方法

### 编译 Mod

从项目根目录运行（global.json 锁定 SDK 8.0.402）：

```bash
# Windows
dotnet build Mod/<ModName>/<ModName>.csproj -c Debug --framework net8.0

# Android（需要 net8.0-android 工作负载）
dotnet build Mod/<ModName>/<ModName>.csproj -c Debug --framework net8.0-android
```

所有 Mod 默认使用 IsMergeLib=true，只需编译单 TFM `net8.0`，DLL 双端共用。不得仅因为需要同时运行于 Windows 和 Android 就建立 X64/Arm64 分包。

### 打包 .scmod

```python
import zipfile, os

MOD_NAME = "YourMod"
MOD_DIR = r"D:\...\Mod\YourMod"
MODS_DIR = r"D:\...\publish\win-x64\Mods"

modinfo = os.path.join(MOD_DIR, "ModInfo.xml")
win_dll = os.path.join(MOD_DIR, "bin", "Debug", "net8.0", "Obfuscar", f"{MOD_NAME}.dll")

with zipfile.ZipFile(os.path.join(MODS_DIR, f"[SuAPI]你的Mod名.scmod"), 'w', zipfile.ZIP_DEFLATED) as zf:
    zf.write(modinfo, "ModInfo.xml")
    zf.write(win_dll, f"Lib/{MOD_NAME}.dll")          # IsMergeLib=true
    # zf.write(win_dll, f"Lib/X64/{MOD_NAME}.dll")     # IsMergeLib=false
    # zf.write(android_dll, f"Lib/Arm64/{MOD_NAME}.dll") # IsMergeLib=false
```

### 部署

将 .scmod 放入游戏 `Mods/` 目录即可加载。

### ModInfo.xml 格式

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Mod>
    <ModInfo>
        <Identifier>YourMod</Identifier>
        <LocalizedName>
            <Text lang="en_US">Your Mod</Text>
            <Text lang="zh_CN">你的Mod</Text>
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
        <!-- <Dependency><ModInfo><Identifier>Comms</Identifier></ModInfo></Dependency> -->
    </Dependencies>
</Mod>
```

## 已收录 Mod

### SurvivalcraftMiniMap

![MiniMap 截图](images/MiniMap.png)

小地图 Mod，通过 ComponentTemplate 向 Player 挂载地图组件，实时显示玩家位置和周围地形。

### WatchMod

![WatchMod 截图](images/WatchMod.png)

手表 Mod，ComponentTemplate+IUpdateable 独立组件模式，handcrafting slot 2 放置 RealTimeClockBlock 时显示游戏时间。不替换 SubsystemGameWidgets，与其他 UI Mod 兼容。

### ConsoleMod

![ConsoleMod 截图](images/ConsoleMod.png)

游戏内控制台，按 `·` 打开，支持 `move +x300` 等指令。Windows 端用 KeyboardInput 内联输入，Android 端用 `Keyboard.ShowKeyboard()` 对话框输入。

### TranslationMod

![TranslationMod 截图](images/string-interceptor.png)

字符串翻译 Mod，Widget 树文本拦截 + IStringProcessor 翻译接口，将游戏界面翻译为中文。演示 `LoadingManager.QueueItem` 和 `ReplaceItem` 用法。

### RainWithoutDawn

![RainWithoutDawn 截图](images/RainWithoutDawn.png)

Subsystem 替换天气系统，移除下雨逻辑。简洁的 Subsystem 替换范例。

### MemoryBankDrawMod

![MemoryBankDrawMod 截图](images/MemoryBankDrawMod.png)

Memory Bank 绘图编辑器，替换 `SubsystemMemoryBankBlockBehavior`，增加 16×16 像素 Draw 模式，16 色画笔和拖拽填充。IsMergeLib=true，单 DLL 双端运行。

### ScMultiplayer

![ScMultiplayer 联机实机截图](images/ScMultiplayer.png)

当前版本：`2.1.0`

适配 SuAPI：`0.1.5.0` / `0.1.5.1`

[下载 Beta0.1.5.1](https://gitee.com/SC-SPM/su-api-example-mod-set/releases/tag/Beta0.1.5.1)

多人联机 Mod，基于 Comms 通信库和主机权威架构，同步玩家、地形、容器、掉落物、投射物、动物、天气、电路、睡眠和世界时间。支持 Windows 与 Android 客户端、无头服务器、地图传输、断线恢复和网络诊断。

#### 从 Beta0.1.3.4 到 2.1.0

统计基线为 [`Beta0.1.3.4`](https://gitee.com/SC-SPM/su-api-example-mod-set/tree/Beta0.1.3.4)（`a7a36dc`，2026-07-24）。截至 `2.0.9` 的 33 次 ScMultiplayer 相关提交统计保持不变；之后新增加入收尾和乘骑/船只同步两个提交，当前版本为 `2.1.0`（`6e45d62`，2026-08-11）。

- `1.9.x`：集中修复加入恢复、玩家与地形同步、移动投射物、电路控制、世界刷新和权威击退。
- `2.0.0`：完善容器拖放、物品丢弃与拾取、交互同步及联机输入处理。
- `2.0.7`：发布地形兴趣范围、批量恢复、可靠传输与网络诊断相关改进。
- `2.0.8`：完成联机模块化重构，继续收口睡眠、电路、地形 checkpoint、复活、骑乘和发射器同步。
- `2.0.9`：修复发射器主机权威执行、创建时序和多端容器状态同步。
- `2.1.0`：将上马、下马、上船和下船拆为可靠动作与状态消息；主机按坐骑网络 ID 执行原版状态机，防止旧位置快照重复挂载；加入阶段补发已有船只的权威初始状态，并修复动作早于 8Hz 坐骑扫描时的网络 ID 竞态。

#### 逐提交更新记录

| 日期 | 提交 | 修改量 | 更新内容 |
|------|------|--------|----------|
| 2026-07-25 | [`5c910c4`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/5c910c4) | 1 文件，+117/-21 | 修复加入房间后的电路同步恢复。 |
| 2026-07-25 | [`9a4de42`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/9a4de42) | 1 文件，+1/-0 | 更新联机服务器目录。 |
| 2026-07-25 | [`0ae9728`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/0ae9728) | 1 文件，+2/-1 | 再次更新联机服务器目录。 |
| 2026-07-26 | [`e14889f`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/e14889f) | 9 文件，+1503/-212 | 提升多人同步可靠性。 |
| 2026-07-26 | [`4240f23`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/4240f23) | 6 文件，+536/-203 | 稳定服务器发现和后台模拟。 |
| 2026-07-26 | [`8ca035b`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/8ca035b) | 2 文件，+80/-7 | 补偿移动状态下释放投射物的偏移。 |
| 2026-07-26 | [`9f0f522`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/9f0f522) | 7 文件，+1044/-19 | 增加可持久保存的联机世界链接。 |
| 2026-07-26 | [`f0535fd`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/f0535fd) | 9 文件，+641/-33 | 加固电路与控制操作同步。 |
| 2026-07-26 | [`4d2441c`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/4d2441c) | 1 文件，+1/-4 | 从服务器目录移除非永久服务器。 |
| 2026-07-26 | [`a2a8d8f`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/a2a8d8f) | 6 文件，+487/-82 | 稳定世界与玩家状态同步。 |
| 2026-07-30 | [`bf18316`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/bf18316) | 7 文件，+91/-324 | 发布 ScMultiplayer 1.9.1，并更新无头服务器工具。 |
| 2026-07-30 | [`3d10824`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/3d10824) | 1 文件，+42/-10 | 在世界刷新时保留联机会话。 |
| 2026-07-31 | [`dad0ab4`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/dad0ab4) | 2 文件，+195/-48 | 同步主机权威的角色击退与飞行状态。 |
| 2026-08-02 | [`c1b6b80`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/c1b6b80) | 2 文件，+8/-7 | 更新 Mod 元数据和世界控制。 |
| 2026-08-03 | [`3a6784b`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/3a6784b) | 15 文件，+2025/-247 | 修复地形方块放置的多端复制。 |
| 2026-08-04 | [`de30972`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/de30972) | 7 文件，+1561/-106 | 发布 ScMultiplayer 1.9.4，并更新无头服务器 Mod。 |
| 2026-08-04 | [`48794cb`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/48794cb) | 5 文件，+13/-89 | 整理联机引用的翻译与字体资源。 |
| 2026-08-04 | [`4d4d77a`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/4d4d77a) | 2 文件，+3/-3 | 更新至 SuAPI 0.1.5.0。 |
| 2026-08-05 | [`fd99b2e`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/fd99b2e) | 18 文件，+2691/-234 | 修复容器拖放、物品丢弃和交互同步。 |
| 2026-08-05 | [`218117f`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/218117f) | 3 文件，+4/-4 | 发布 ScMultiplayer 2.0.0。 |
| 2026-08-06 | [`1a30d2b`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/1a30d2b) | 17 文件，+2891/-489 | 发布 ScMultiplayer 2.0.7，并更新 ModDns。 |
| 2026-08-08 | [`233a97b`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/233a97b) | 95 文件，+27008/-20383 | 将大型联机实现拆分为独立模块，并稳定加入、地形和网络同步。 |
| 2026-08-08 | [`e1be835`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/e1be835) | 1 文件，+1/-1 | 将 ModDns 中的 ScMultiplayer 更新至 2.0.8。 |
| 2026-08-08 | [`f1b9f7d`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/f1b9f7d) | 1 文件，+4/-4 | 增加 SuAPI 0.1.5.1 发布兼容信息。 |
| 2026-08-08 | [`8e30e5d`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/8e30e5d) | 7 文件，+156/-6 | 在电路恢复期间同步睡眠唤醒状态。 |
| 2026-08-08 | [`18f5397`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/18f5397) | 8 文件，+297/-31 | 稳定电路与睡眠同步。 |
| 2026-08-08 | [`7984e2a`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/7984e2a) | 5 文件，+53/-6 | 在主机权威时间加速结束后唤醒客户端。 |
| 2026-08-09 | [`28eff36`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/28eff36) | 30 文件，+2807/-1134 | 按客户端兴趣范围协调地形 checkpoint 与恢复。 |
| 2026-08-09 | [`98fd750`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/98fd750) | 11 文件，+386/-24 | 保留地形修订记录和联机角色复活状态。 |
| 2026-08-09 | [`2c1eb59`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/2c1eb59) | 25 文件，+1076/-73 | 同步远程骑乘状态，并建立发射器主机权威路径。 |
| 2026-08-10 | [`c7b7adb`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/c7b7adb) | 1 文件，+8/-1 | 保持发射器效果只由主机权威执行。 |
| 2026-08-10 | [`7f9b7c7`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/7f9b7c7) | 1 文件，+11/-0 | 在电路元件创建完成后排队执行发射器操作。 |
| 2026-08-10 | [`d0859d8`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/d0859d8) | 4 文件，+14/-54 | 发布 ScMultiplayer 2.0.9，完成发射器同步收口。 |
| 2026-08-10 | [`516a1db`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/516a1db) | 3 文件，+17/-10 | 解除加入收尾对可靠窗口的额外阻塞，避免客户端长期停留在 Joining Room。 |
| 2026-08-11 | [`6e45d62`](https://gitee.com/SC-SPM/su-api-example-mod-set/commit/6e45d62) | 16 文件，+826/-51 | 发布 ScMultiplayer 2.1.0：可靠乘骑动作/状态、主机坐骑 ID 分配、已有船只加入快照和远处船只延迟创建。 |

[查看 Beta0.1.3.4 至当前版本的完整差异](https://gitee.com/SC-SPM/su-api-example-mod-set/compare/Beta0.1.3.4...master)

### HeadlessRenderingMod

Windows 无画面服务器 Mod。直接运行实例目录中的 `Survivalcraft.exe`，关闭世界和 UI 实际绘制，并通过本机 TCP JSON 接口提供命令行和 AI 控制。

### 其他 Mod

| Mod | 类型 | 说明 |
|-----|------|------|
| TemperatureImmunity | Component 替换 | 替换体温组件，保持恒温 |
| Comms | 联机通信库 | SuAPI 联机 Mod 通信基础库，ScMultiplayer 依赖 |

## 资源加载

Mod 有两种资源加载方式，可按需混用：

### 1. scmod Content/ 目录 → ContentCache

将资源文件放入 scmod 的 `Content/` 目录，ModLoader 启动时自动提取并缓存到 `ContentCache`。

**Key 规则**：`Content/{relativePath}.{ext}` → `ContentCache.Get<T>("Mod/{relativePath}")`（去掉 `Content/` 前缀和扩展名）

```
Content/SuConsoleButton.png  → ContentCache.Get<Texture2D>("Mod/SuConsoleButton")
Content/Fonts/chinese12.png  → ContentCache.Get<Texture2D>("Mod/Fonts/chinese12")
Content/zh_CN.xml            → ContentCache.Get<XElement>("Mod/zh_CN")
```

**代码**：
```csharp
using Engine.Content;
var tex = ContentCache.Get<Texture2D>("Mod/SuConsoleButton");
```

**打包**：
```python
zf.write("Content/SuConsoleButton.png", "Content/SuConsoleButton.png")
```

**适用**：纹理、字体、翻译 XML、模型等需要运行时替换的资源。优点是无需重新编译 DLL 即可替换资源。

### 2. DLL 嵌入资源 → GetManifestResourceStream

将资源编译进 DLL 作为嵌入资源，运行时通过 `Assembly.GetManifestResourceStream` 读取。

**Key 规则**：csproj 中 `<EmbeddedResource Include="Content\YourFile.png" />` → 资源名 `{Namespace}.{Content.YourFile.png}`

**csproj**：
```xml
<ItemGroup>
  <EmbeddedResource Include="Content\YourButton_Pressed.png" />
</ItemGroup>
```

**代码**：
```csharp
using System.Reflection;
var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ConsoleMod.Content.YourButton_Pressed.png");
var tex = Texture2D.Load(stream);
stream.Dispose();
```

**适用**：不希望用户替换的资源（如按下状态纹理）、小体积资源。优点是资源与 DLL 一体，不会丢失。

### 混用示例（ConsoleMod）

普通按钮纹理放 Content/（可替换），按下纹理嵌入 DLL（不可替换）：

```xml
<!-- ConsoleMod.csproj -->
<ItemGroup>
  <EmbeddedResource Include="Content\SuConsoleButton_Pressed.png" />
</ItemGroup>
```

```csharp
// 普通纹理：从 ContentCache 加载（scmod Content/ 目录）
m_buttonNormalTex = ContentCache.Get<Texture2D>("Mod/SuConsoleButton");
// 按下纹理：从 DLL 嵌入资源加载
m_buttonPressedTex = LoadEmbeddedTexture("ConsoleMod.Content.SuConsoleButton_Pressed.png");
```

## 运行时铁律

1. **ModLoader 依赖加载** — .scmod 内 DLL 不会自动全部加载，只有 Identifier 同名的和 `<Dependencies>` 声明的才会被加载
2. **ReplaceItem name 匹配** — `LoadingManager.ReplaceItem(name, action)` 的 name 是 QueueItem 注册名（"Initialize PlayScreen"），不是 Screen 名
3. **EventBus 静默吞异常** — 回调异常只写 Console.WriteLine，不记入 Game.log
4. **Release Android AOT/Linker 裁剪** — 主程序未使用的方法会被 linker 移除，Mod 使用→MissingMethodException。避免 Linq/委托排序/params 构造函数
5. **SC 坐标系 Y 向上** — 定位参数不能耦合大小参数，必须拆分为 visualRadiusPx + marginX/Y
6. **禁止提交诊断 Log** — 临时调试日志验证后必须移除
7. **Storage.ProcessPath** — 只识别 `app:` 和 `data:` 协议，绝对路径抛异常
8. **.scmod ZIP 正斜杠** — 必须用 Python zipfile 打包，Compress-Archive 反斜杠路径→ModLoader 匹配失败
9. **ModInfo.xml 根目录** — 打包时 ModInfo.xml 必须在 ZIP 根目录
10. **PowerShell `[]` 通配符** — 操作含 `[SuAPI]` 路径时必须用 `-LiteralPath`

## 相关仓库

- SuAPI 核心：https://gitee.com/SC-SPM/survivalcraft-su-api
