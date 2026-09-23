# ScMultiplayer 开发守则与限制

> ⚠️ **动手前必读** — 本文件汇总了 SOUL.md、MEMORY.md、survivalcraft-mod SKILL.md、PROJECT-LOG.md 中所有跨文件约束。
> 最后更新: 2026-05-18

---

## 一、行动前强制读取链

每轮对话开始前，必须按顺序读完以下 5 个文件：

| 序号 | 文件 | 来源 |
|------|------|------|
| 1 | SOUL.md | workspace root |
| 2 | MEMORY.md | workspace root |
| 3 | survivalcraft-mod SKILL.md | managed skill dir |
| 4 | memory/YYYY-MM-DD.md | workspace memory dir |
| 5 | ScMultiplayer/doc/PROJECT-LOG.md | 本目录 |

读完前禁止执行任何修改操作。

## 二、编译前置检查（铁律）

```
1. taskkill /F /IM Survivalcraft.exe    # 锁 DLL 的是 SC 运行时进程，非 VS
2. (Get-Item 源文件).LastWriteTime = Get-Date  # touch 防 obj 缓存跳过
3. [IO.File]::ReadAllBytes(dll) 搜索特征字符串    # 验证 DLL 含新代码
```

**根因**：
- SC 进程运行时锁定 Engine.dll/Survivalcraft.dll → Rebuild 复制失败
- MSBuild obj 缓存认为输出最新 → Build 报告成功但跳过 CoreCompile
- 不验证 DLL 内容 → 部署了旧代码也发现不了

## 三、编译工具链

| 规则 | 说明 |
|------|------|
| **必须用 MSBuild** | `d:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe` |
| **禁止 dotnet build** | 与 Engine.csproj 不兼容（AL 任务报错） |
| **ScMultiplayer 限 Debug** | Release 配置的 Survivalcraft 依赖缺少 WINDOWS 定义常量 |

## 四、Mod 开发者约束

| 约束 | 来源 |
|------|------|
| 禁止修改 Survivalcraft 原始代码 | SOUL.md L25 |
| 所有代码以项目源码为准，不凭记忆臆造 | SOUL.md L28 |
| 防臆造：每步完成后才能进下一步，禁止跳步 | SOUL.md L42-44 |
| 不允许给游戏原有代码添加额外接口 | SOUL.md L89 |
| 禁止将 Engine.dll/Survivalcraft.dll/EntitySystem.dll 打入 mod | MEMORY.md |

## 五、HandleGameDatabase 新建 ComponentTemplate（铁律）

新建组件必须三件套（缺一 = "Specified cast is not valid"）：

```
1. ComponentTemplate.ExplicitInheritanceParent — 继承已有模板（GUID 从参考代码复制）
2. ComponentTemplate.NestingParent — 挂 Folder 类型（如 Gameplay），非 EntityTemplate
3. MemberComponentTemplate.NestingParent — 挂 EntityTemplate 类型（如 Player）
```

GUID 必须从参考代码复制，禁止自己编。

## 六、.scmod 打包铁律

```
1. 先 ZIP 再改名            # Compress-Archive 不支持 .scmod 后缀
2. Push-Location $dir; Compress-Archive -Path *  # 保证根目录扁平化
3. [ZipFile]::OpenRead() 验证 Entries 首层      # 确认无中间目录
4. ModInfo.xml 必须嵌套格式 <Mod><ModInfo>...</ModInfo></Mod>
   # 扁平格式 → doc.Root.Element("ModInfo") 返回 null → "Invalid ModID" → 静默跳过
5. 文件名加 [SuAPI] 前缀    # 与其他来源 Mod 区分
```

## 七、网络与防火墙

### 设备与端口速查

| 项目 | 值 |
|------|-----|
| 本机 IP | **见 `AGENTS.local.md`**（文档里简称"本机"） |
| 平板 IP | **见 `AGENTS.local.md`**（文档里简称"平板"） |
| 服务器 IP / 端口 | **见 `AGENTS.local.md`**（文档里简称"服务器"） |
| 游戏 Server | **51459** (UDP) |
| 游戏动态端口 | **49152-65535** (UDP) |

> 设备的具体 IP、账号、凭据等私有信息只在 `AGENTS.local.md` 维护（该文件被 `.gitignore` 忽略），
> **禁止写回本文档**——本仓库是公开仓库，写进来的地址会随 git 历史一起分发出去。
> 现已**没有**独立的"远程机"：局域网只有本机 + 平板，公网那台就是服务器。

### ScMultiplayer 端口（每个设备 3 个 UDP Socket）

| Socket | 端口 | 确定方式 | 说明 |
|--------|------|----------|------|
| **Server** | **51459** | 固定 `"SuSCMP".ToDynamicPort()` | 监听 DiscoveryRequest + 客户端连接 |
| **Explorer** | **动态** | OS 分配 | 发送广播 → 51459，接收 DiscoveryResponse |
| **Client** | **动态** | OS 分配 | 游戏数据通信（位置/方块/聊天等） |

> 三个独立 `UdpTransmitter` → 三个独立 `Socket` → 三个不同端口。
> 实际日志示例：Server=**:51459**, Explorer=**:56367**, Client=**:56369**。
> Server 端口固定 51459，Explorer/Client 端口每次启动随机。

### 防火墙开放

**只有"当主机的那一端"需要入站规则**；客户端（本机 / 平板）只发出流量，不需要放行。

- 局域网对局（本机当主机）：放行**本机**
- 公网对局（服务器当主机）：放行**服务器**（云主机还要在控制台安全组里一并放行）

```powershell
# 主机端管理员 PowerShell 执行两条：
netsh advfirewall firewall add rule name="ScMultiplayer Server" dir=in action=allow protocol=UDP localport=51459
netsh advfirewall firewall add rule name="ScMultiplayer Dynamic" dir=in action=allow protocol=UDP localport=49152-65535

# 验证
netsh advfirewall firewall show rule name="ScMultiplayer Server"
netsh advfirewall firewall show rule name="ScMultiplayer Dynamic"
```

> **⚠️ 只开 51459 不够** —— Explorer 的广播应答与客户端数据连接还会落在 49152-65535 的动态 UDP 端口上。

### 网络要求

- 局域网对局：两端必须同一子网（子网段见 `AGENTS.local.md`）
- 公网对局：连服务器地址，主机端放行 UDP 51459 + 49152-65535
- UDP 广播不能被路由器过滤（AP 隔离会让房间发现直接失败）
- ZeroTier/VPN 虚拟网卡可能导致选错 IP（已修复：多网卡探测改为绑定指定 IP）

## 八、ModEventBus 调试注意

- `TriggerEvent` catch 只写 `Console.WriteLine`，不记入 `Game.log`
- 调试时 handler 外围 try-catch + `Log.Error()` 或加步进 `Log.Information` 标记
- 捕获 Console 输出：`Start-Process -RedirectStandardOutput`

## 九、Loading.Initialize 屏幕替换

- Play 屏幕在 `actions` 末尾倒数第 13 位
- 动态计算：`actions.Count - 13`，**禁止硬编码** `actions[803]`
- Source: ScreensManager.cs:Initialize — ContentManager.List() 数量不固定

## 十、Comms.Drt 限制

- **不引用 Engine**，无法使用 `Engine.Log.Information()`
- 诊断日志只能用 `Console.WriteLine`
- Message 注册基于反射按字母序分配 ID，新增 Message 子类自动注册

## 十一、Mod 管理

```powershell
# 禁用
Rename-Item "Mod.scmod" "Mod.scmod.unint"
# 启用
Rename-Item "Mod.scmod.unint" "Mod.scmod"
```
- 需重启游戏生效
- 仅扫描 Mods/ 顶层目录

## 十二、.scmod 打包后验证（铁律）

```
1. 打包完成后必须启动 SC 验证加载
2. 检查 Console 输出: "Loaded mod: xxx (from scmod)"
3. 确认 Game.log 有 Mod 入口日志
```

> **禁止打包完就部署** — 02:29 的 .scmod 从未被加载验证，扁平格式错误潜伏到 03:14 才暴露。

## 十三、故障记录规则

所有失败根因 + 解决方案 → 写入 `doc/PROJECT-LOG.md`

## ⚠️ 混淆改名：按"类名字符串"引用的类型必须进 Obfuscar 保留名单

**症状（2026-09-23 实测，2.2.x）**：主机侧 mod 正常加载（`[ScMP] Wire protocol …`、`Server started OK`、`Database hooks applied`），
但日志**到此为止**：无异常、无 `serverError`，`world.join <世界>` 被接受却 `worldLoaded` 永不成立 →
表现就是"主机加载世界卡死 / Error loading world / 发现列表里没有这台服务器"。

**真因**：新增的替换类被 **Obfuscar 改名**。凡是**按类名字符串引用**的类型都会因此找不到：
- `Pak/Database.xml` 的 `Class` 参数（`subsystemX.Value = "ScMultiplayer.SuSubsystemXBlockBehavior"`）；
- `modInjector.Register("Game.X", "ScMultiplayer.SuX")`；
- 任何反射 `Type.GetType("ScMultiplayer.X")` / 由外部 mod 按名引用。
引擎侧报的是 `Loading error. Reason: Type "ScMultiplayer.X" not found in any loaded assembly.`（在 `Logs/Game.log` 里搜这句即可确诊）。

**处理规矩**
1. 只要新加了"按类名字符串被引用"的类型，**必须**在 `Mod/ScMultiplayer/Obfuscar.xml` 的 `<Module>` 里补一行
   `<SkipType name="ScMultiplayer.<类名>" />`（保留原名，可连带 `skipMethods/skipFields/...` 视需要）。
2. 两种注入方式（`modInjector.Register` 与 `Pak/Database.xml` GUID 补丁）**本身都可用**，
   区别不在机制而在"**是否被混淆改名**"——所以先查 `Obfuscar.xml`，不要先怀疑机制。
3. 排查顺序：`Logs/Game.log` 搜 `not found in any loaded assembly` → 查 `Obfuscar.xml` 有无该 `<SkipType>` →
   补上 → 重编译（构建日志会打印 `Renaming: Types…`，说明混淆确实在跑）→ 三端部署验证 `worldLoaded:true`。

**排查纪律（同一批教训）**
- ssh/scp 一律带 `ConnectTimeout` + `ServerAliveCountMax` + `NumberOfPasswordPrompts=1`，整段设总 deadline；
  **等待失败必须立即打印一行明确结论**，不要放进没有硬超时的重试循环（曾静默挂住数十分钟）。
- 超过 ~60 秒的操作走后台任务；先用一次廉价调用验证参数拼对了，再进重试（见 `remote_server_ops.py` 的 `join/world.list`）。
- 加载世界用无头 mod 的动作：`remote_server_ops.py join <world>`（选择器用 `world.list` 里的 `name`，如 `201ser`；
  目录名是 `data:/Worlds/World4`）；`restart` 不吃 `--world`。
## ⚠️ 存档 Project.xml 不得出现"被 mod 替换/新增"的类型名

**规则**：存档 `Project.xml` 里只能出现**原版能映射的类型名**。原版只认它自己的类型（`Game.` / `Engine.` /
`EntitySystem.` 等），所以凡是**被 mod 替换或新增**的类型名（当前实现里就是所有 `ScMultiplayer.*`）都**不得**写进存档；
否则**未加 mod 的原版**读这个存档会报 `Type ... not found`（或 `Error loading world`）。
（"非 `Game.` 前缀"只是当前实现下的近似判据；真正判据是"这个类型原版认不认"。）

**自查（每次涉及替换/DB 补丁/注入器映射后都要跑，含服务器存档）**
1. 全文搜 `ScMultiplayer\.[A-Za-z_]+` → 应为 **0 命中**；
2. 搜带命名空间的属性值 `(class|type|subsystem\w*)="[^"]*\.[^"]*"` → **不应命中**。

存档位置：PC `publish\[SuAPI]Survivalcraft\Worlds\<世界>\Project.xml`；
服务器 `C:\SurvivalcraftServer\Worlds\<世界>\Project.xml`。
**已核查（2026-09-23）**：`World` / `World1` / `World2` / 服务器 `World4`（=201ser）**全部 0 命中** ✓。

**格式差异（别照错模板）**：DB 里的子系统类名在 `Pak\Database.xml`，形如
`<Parameter Name="Class" Guid="…" Value="Game.X" Type="string" />`；
而**存档 `Project.xml` 只落子系统字段值、不写类名** —— 拿 DB 的形式去存档里搜会 0 命中，属正常，不要据此下结论。