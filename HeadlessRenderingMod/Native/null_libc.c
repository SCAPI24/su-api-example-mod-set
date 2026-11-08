/*
 * Linux 版 libc 代理（只改 open，其余全部转发给真 libc）。
 *
 * ── 为什么需要它 ───────────────────────────────────────────────────────
 *
 * 前面 5 个空壳（gbm/drm/EGL/GLESv2/libinput）已经能让 OpenTK 认为"有 GPU、
 * 有显示器"，但那样一来它就走了**真实硬件**的刷新路径：
 *
 *   Engine.Window.RenderFrameHandler  (每帧)
 *     → m_gameWindow.Context.SwapBuffers()
 *       → LinuxGraphicsContext.SwapBuffers()
 *         → QueueFlip() 把 is_flip_queued 置 true（通过 drmModePageFlip）
 *         → 下一次 WaitFlip(block: true)
 *             → poll(fd, 1, -1)        ← block=true 时 timeout 是 -1
 *
 * 这个 poll 在等 DRM 的 page-flip 事件；真实硬件上由内核发，空壳环境永远
 * 不会有。实测表现是主线程永久阻塞在 poll 上（/proc/<pid>/wchan = do_sys_poll，
 * CPU 1%），游戏主循环不再推进 —— 控制接口全部 timeout，终端菜单永远停在
 * "Waiting for game screens..."。
 *
 * ── 解决办法 ───────────────────────────────────────────────────────────
 *
 * 让 OpenTK 在 LinuxFactory.SetupDisplay 里 `open("/dev/dri/cardN")` 拿到的
 * **不是**真实的 DRM 设备 fd，而是一个我们自己创建的 **eventfd**：
 *   · eventfd 默认不可读，poll 会阻塞 —— 和真实 fd 一样"看起来像设备"；
 *   · drmModePageFlip 空壳会往它里面写一个值，poll 立刻返回可读；
 *   · drmHandleEvent 空壳随后回调 page_flip_handler，is_flip_queued 清 0；
 *   · 主循环因此每帧都能正常推进。
 *
 * 只接管 open：其余（read/close/dup/ioctl/poll/uname……）一律转发真 libc，
 * 避免影响 .NET 运行时自身的文件操作。
 *
 * Source: OpenTK/OpenTK/Platform/Linux/LinuxFactory.cs:SetupDisplay / CreateDisplay
 * Source: OpenTK/OpenTK/Platform/Linux/Libc.cs（open/close/poll 声明）
 * Source: OpenTK/OpenTK/Platform/Linux/LinuxGraphicsContext.cs:SwapBuffers / WaitFlip / QueueFlip
 *
 * 编译：gcc -shared -fPIC -O2 -o libc-headless.so null_libc.c -ldl
 */

#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <sys/eventfd.h>
#include <sys/types.h>
#include <unistd.h>

#if defined(_WIN32) || defined(_WIN64)
#  define HEADLESS_EXPORT __declspec(dllexport)
#else
#  define HEADLESS_EXPORT __attribute__((visibility("default")))
#endif

/* 假 DRM 设备 fd：所有 /dev/dri/cardN 的 open 都返回它。 */
static int s_drmEfd = -1;

/* /dev/dri 下设备路径的判定：只要命中就直接给 eventfd。 */
#define DRM_DIR_PREFIX "/dev/dri/"

int headless_drm_eventfd(void);

/*
 * 取（或创建）eventfd。
 * 全局唯一：OpenTK 多次 open 同一路径时应拿到同一个 fd，避免它以为换设备了。
 */
int headless_drm_eventfd(void)
{
    if (s_drmEfd < 0)
    {
        int efd = eventfd(0, EFD_NONBLOCK);
        if (efd >= 0)
            s_drmEfd = efd;
    }
    return s_drmEfd;
}

/* 让 poll 就绪：由 libdrm 空壳在 drmModePageFlip 里调用。 */
HEADLESS_EXPORT int headless_drm_notify(void)
{
    int efd = headless_drm_eventfd();
    if (efd < 0)
        return -1;
    uint64_t value = 1;
    ssize_t written = write(efd, &value, sizeof(value));
    return written == (ssize_t)sizeof(value) ? 0 : -1;
}

/* ── open：唯一被改写的函数 ─────────────────────────────────────────── */

typedef int (*open_fn)(const char *path, int flags, ...);

/* 路径判断：是否是我们想"假装成 DRM 设备"的路径。 */
static int is_drm_device_path(const char *path)
{
    if (!path)
        return 0;
    if (strncmp(path, DRM_DIR_PREFIX, sizeof(DRM_DIR_PREFIX) - 1) != 0)
        return 0;
    /* 只接管 card* 与 renderD*，by-path 之类保持原样。 */
    const char *name = path + (sizeof(DRM_DIR_PREFIX) - 1);
    return strncmp(name, "card", 4) == 0 || strncmp(name, "renderD", 7) == 0;
}

HEADLESS_EXPORT int open(const char *path, int flags, ...)
{
    /* 变参只在带 O_CREAT 时才有意义；这里两种写法都取一次，避免漏传。 */
    mode_t mode = 0;
    if (flags & 0100 /* O_CREAT */)
    {
        va_list ap;
        va_start(ap, flags);
        mode = (mode_t)va_arg(ap, int);
        va_end(ap);
    }

    if (is_drm_device_path(path))
    {
        int efd = headless_drm_eventfd();
        if (efd >= 0)
            return efd;
        /* eventfd 建不出来时明确失败，不要悄悄返回错误的东西。 */
        errno = ENODEV;
        return -1;
    }

    open_fn real_open = (open_fn)dlsym(RTLD_NEXT, "open");
    if (!real_open)
    {
        errno = ENOSYS;
        return -1;
    }
    if (flags & 0100 /* O_CREAT */)
        return real_open(path, flags, mode);
    return real_open(path, flags);
}

/* glibc 里 open 常被 open64 别名调用，两个符号都要给。 */
HEADLESS_EXPORT int open64(const char *path, int flags, ...)
{
    mode_t mode = 0;
    if (flags & 0100)
    {
        va_list ap;
        va_start(ap, flags);
        mode = (mode_t)va_arg(ap, int);
        va_end(ap);
    }
    if (is_drm_device_path(path))
        return headless_drm_eventfd();
    open_fn real_open = (open_fn)dlsym(RTLD_NEXT, "open");
    if (!real_open)
    {
        errno = ENOSYS;
        return -1;
    }
    if (flags & 0100)
        return real_open(path, flags, mode);
    return real_open(path, flags);
}
