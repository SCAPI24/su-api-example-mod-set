# Linux 无 GPU 空壳库（linux-x64）

本目录存放 **Linux 无头服务器用的预编译空壳库**。它们被嵌入 `HeadlessRenderingMod.dll`，
由 mod 在运行时提取到游戏根目录，并用 `-headless.so` 这样的**独立文件名**加载 ——
因此**不会覆盖系统库**，卸载 mod 后游戏仍按系统库正常运行。

## 为什么需要这 6 个库

Linux 上 OpenTK 的初始化链比 Windows 长得多，下面每个环节缺一不可，缺任何一个
进程都会在 `Program.Main → Window.Run` 直接退出：

| 库 | 拦截的环节 | 缺失时的现象 |
|---|---|---|
| `libgbm-headless.so` | 打开 DRM 设备建 GBM device | `DllNotFoundException: libgbm.so.1` |
| `libdrm-headless.so` | 枚举显示器 + 唤醒 page-flip | `PlatformNotSupportedException` / 主线程卡在 `poll` |
| `libEGL-headless.so` | EGL display / context / GetProcAddress | 拿不到 GL 入口，段错误 |
| `libGLESv2-headless.so` | 89 个 GL 函数全部打空 | 无 GL 实现可用 |
| `libinput-headless.so` | 键盘/鼠标输入线程 | `TypeInitializationException: Keyboard` |
| `libc-headless.so` | 让 `open("/dev/dri/cardN")` 返回 eventfd | 主线程永久阻塞在 `poll(fd, 1, -1)` |

## 源码与产物的对应关系

源码在同级目录（`Native/null_*.c`），产物在本目录：

| 源码 | 产物（本目录） |
|---|---|
| `../null_gbm.c` | `libgbm-headless.so` |
| `../null_drm.c` | `libdrm-headless.so` |
| `../null_egl.c` | `libEGL-headless.so` |
| `../null_gles2.c` | `libGLESv2-headless.so` |
| `../null_input.c` | `libinput-headless.so` |
| `../null_libc.c` | `libc-headless.so` |

Windows 用的两个 `-headless.dll`（`libGLESv2.dll`、`opengl32-headless.dll`）也在 `Native/`
下，同样由 `null_gles2.c` / `null_opengl32.c` 编译而来，但**不是**在本目录交叉编译。

## 重新编译（在 Linux 上执行）

空壳库必须在 **Linux** 上编译（Windows 上无法产出 `.so`）。任意带 gcc 的 Linux 主机均可，
不需要 GPU、不需要安装 Mesa：

```bash
cd <本仓库>/Mod/HeadlessRenderingMod/Native

gcc -shared -fPIC -O2 -o linux-x64/libgbm-headless.so    null_gbm.c
gcc -shared -fPIC -O2 -o linux-x64/libdrm-headless.so    null_drm.c
gcc -shared -fPIC -O2 -o linux-x64/libEGL-headless.so    null_egl.c    -ldl
gcc -shared -fPIC -O2 -o linux-x64/libGLESv2-headless.so null_gles2.c
gcc -shared -fPIC -O2 -o linux-x64/libinput-headless.so  null_input.c
gcc -shared -fPIC -O2 -o linux-x64/libc-headless.so      null_libc.c   -ldl
```

编译完成后把这些 `.so` 拷回本机，重新 `dotnet build` + 打包 `.scmod` 即可 ——
csproj 里已按文件名把它们登记为嵌入资源（`HeadlessRenderingMod.Native.linux.*`）。

## 验证清单（改完任一个库后必查）

1. **符号齐全**：`nm -D --defined-only <lib>.so | grep -c ' T '` 应大于 0；
   `libGLESv2-headless.so` 应为 **89** 个，`libEGL-headless.so` 应为 **34** 个。
2. **无跨库硬依赖**：`nm -D <lib>.so | grep ' U headless_'` 应为空
   （两个 `.so` 是分别 `dlopen` 的，符号不在同一全局作用域，跨库符号会在加载时报
   `undefined symbol`。`libdrm` 唤醒 eventfd 用的是**直接 write fd**，不靠跨库符号）。
3. **实机跑通**：把 `.scmod` 放到服务器 `Mods/`，前台执行 `./Survivalcraft`，
   应当看到纯文本菜单（`World-[]` / `Manage Players` / …）与底部状态栏 `== <实例名> ==`。
   若停在 `Waiting for game screens...` 然后超时，说明主循环被阻塞 —— 先查
   `cat /proc/<pid>/wchan`，若是 `do_sys_poll` 就是 libc/libdrm 那条 page-flip 路径没打通。
