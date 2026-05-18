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

### 远程设备速查

| 项目 | 值 |
|------|-----|
| 远程 IP | **192.168.31.25** |
| 远程调试 | **8514** (TCP) |
| 游戏 Server | **51459** (UDP) |
| 游戏动态端口 | **49152-65535** (UDP) |
| 远程桌面 | **3389** (TCP) |

### ScMultiplayer 端口（每个设备 3 个 UDP Socket）

| Socket | 端口 | 确定方式 | 说明 |
|--------|------|----------|------|
| **Server** | **51459** | 固定 `"SuSCMP".ToDynamicPort()` | 监听 DiscoveryRequest + 客户端连接 |
| **Explorer** | **动态** | OS 分配 | 发送广播 → 51459，接收 DiscoveryResponse |
| **Client** | **动态** | OS 分配 | 游戏数据通信（位置/方块/聊天等） |

> 三个独立 `UdpTransmitter` → 三个独立 `Socket` → 三个不同端口。
> 实际日志示例：Server=**:51459**, Explorer=**:56367**, Client=**:56369**。
> Server 端口固定 51459，Explorer/Client 端口每次启动随机。
| Remote Debug | **8514** | TCP | HTTP 文件服务器，日志/Mods/进程管理 |

### 防火墙开放

远程 .25 只开了 RDP (3389)，每次重装/重置后需重新开放以下端口：

```powershell
# 远程 .25 管理员 PowerShell 执行全部三条：
netsh advfirewall firewall add rule name="SC Remote Debug" dir=in action=allow protocol=TCP localport=8514
netsh advfirewall firewall add rule name="ScMultiplayer Server" dir=in action=allow protocol=UDP localport=51459
netsh advfirewall firewall add rule name="ScMultiplayer Dynamic" dir=in action=allow protocol=UDP localport=49152-65535

# 验证
netsh advfirewall firewall show rule name="ScMultiplayer Server"
netsh advfirewall firewall show rule name="SC Remote Debug"
```

> **⚠️ 仅开放端口 255 或仅 RDP(3389) 不够** — ScMultiplayer 需要 UDP 51459 + 49152-65535，远程调试需要 TCP 8514。

### 网络要求

- 两台设备必须在同一局域网子网（如 192.168.31.0/24）
- UDP 广播不能被路由器过滤
- ZeroTier/VPN 虚拟网卡可能导致选错 IP（已修复：多网卡探测改为绑定指定 IP）
- 远程仅开放 TCP 3389 是常态，每次调试前确认 8514 和 51459 已开放

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
