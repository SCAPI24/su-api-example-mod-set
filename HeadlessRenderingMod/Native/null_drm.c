/*
 * Linux/macOS 版 libdrm 空实现后端（no-GPU / 无显示器）。
 *
 * 为什么需要它：OpenTK 在 Linux 上确认"能画"的最后一道关是 DRM 显示器枚举
 * （OpenTK/Platform/Linux/LinuxDisplayDriver.cs:QueryDisplays）：
 *
 *     ModeRes *res = drmModeGetResources(fd);
 *     if (!res) return false;                       // ← 我们返回 NULL 就在这里失败
 *     for (i = 0; i < res->count_connectors; i++) { ... }
 *     // 至少要有一个 connector: connection == Connected && count_modes > 0
 *
 * 只要 `QueryDisplays` 返回 false，`LinuxFactory.CreateDisplay` 就抛
 * PlatformNotSupportedException，进程在 `Program.Main → Window.Run` 直接退出。
 *
 * 这个空壳**伪造一台 1280x720 的虚拟显示器**：
 *   · drmModeGetResources 返回一个静态 ModeRes（1 个 connector / 1 个 crtc / 1 个 encoder）；
 *   · drmModeGetConnector 返回 Connected + 1 个 mode；
 *   · drmModeGetEncoder / drmModeGetCrtc 返回配套的静态结构；
 *   · 所有 SetCrtc / PageFlip / AddFB 一律返回 0（成功）—— 反正没有真的显示设备。
 *
 * Source: OpenTK/OpenTK/Platform/Linux/LinuxDisplayDriver.cs:QueryDisplays / QueryDisplay
 * Source: OpenTK/OpenTK/Platform/DisplayDevice.cs
 *
 * 编译：gcc -shared -fPIC -O2 -o libdrm-headless.so null_drm.c
 */

#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <dlfcn.h>
#include <unistd.h>

//
// 唤醒 libc 空壳里的 eventfd（让 OpenTK 的 poll 立即返回）。
//
// 不能直接用 extern 声明：两个 .so 是分别 dlopen 的，符号不在同一个全局
// 作用域里，直接引用会在加载时报 "undefined symbol: headless_drm_notify"。
// 改成运行时用 dlsym 在已加载的库中查找（RTLD_DEFAULT）。
typedef int (*notify_fn)(void);


#if defined(_WIN32) || defined(_WIN64)
#  define HEADLESS_EXPORT __declspec(dllexport)
#else
#  define HEADLESS_EXPORT __attribute__((visibility("default")))
#endif

/* ── 与 OpenTK 里 P/Invoke 结构一一对应的布局 ─────────────────────────── */

/* ModeInfo：OpenTK 只读 hdisplay / vdisplay / vrefresh 等字段。
 * 这里按 Linux 内核 drm_mode_modeinfo 的实际布局定义（前 21 个 ushort/int）。 */
struct mode_info
{
    uint32_t clock;
    uint16_t hdisplay, hsync_start, hsync_end, htotal, hskew;
    uint16_t vdisplay, vsync_start, vsync_end, vtotal, vscan;
    uint32_t vrefresh;
    uint32_t flags;
    uint32_t type;
    char name[32];
};

/* mode_res：**必须与 OpenTK 里 ModeRes 的字段类型逐一对齐**。
 *
 * 关键陷阱：64 位下指针是 8 字节、int 是 4 字节，所以
 *   int count_fbs;      // 偏移 0
 *   int *fbs;           // 偏移 8（对齐到 8，中间补 4 字节）
 *   int count_crtcs;    // 偏移 16
 *   ...
 * 如果这里把指针写成 uint32_t* 而 OpenTK 那边是 int*，宽度虽然一样，
 * 但字段**类型**不一致会让 C 的对齐规则与 .NET 的 sequential 布局错位
 * （实测：count_crtcs 读成 0、count_encoders 读成垃圾值）。
 * 所以这里逐字照抄 OpenTK 的声明。 */
struct mode_res
{
    int count_fbs;
    int *fbs;
    int count_crtcs;
    int *crtcs;
    int count_connectors;
    int *connectors;
    int count_encoders;
    int *encoders;
    int min_width;
    int max_width;
    int min_height;
    int max_height;
};

/* mode_connector：QueryDisplays 读 connection 与 count_modes / modes。 */
struct mode_connector
{
    uint32_t connector_id;
    uint32_t encoder_id;
    uint32_t connector_type;
    uint32_t connector_type_id;
    int connection;          /* 1 = DRM_MODE_CONNECTED */
    uint32_t mmWidth;
    uint32_t mmHeight;
    uint32_t subpixel;
    int count_modes;
    struct mode_info *modes;
    int count_props;
    int *props;
    long long *prop_values;      /* ← OpenTK 里有这个字段，漏了会让后面的 count_encoders 错位 */
    int count_encoders;
    int *encoders;
};

/* mode_encoder：QueryDisplay 通过 GetEncoder -> GetCrtc 拿 crtc。 */
struct mode_encoder
{
    uint32_t encoder_id;
    uint32_t encoder_type;
    uint32_t crtc_id;
    uint32_t possible_crtcs;
    uint32_t possible_clones;
};

/* mode_crtc：包含一个内嵌的 mode。 */
struct mode_crtc
{
    uint32_t crtc_id;
    uint32_t buffer_id;
    uint32_t x, y;
    uint32_t width, height;
    int mode_valid;
    struct mode_info mode;
    int gamma_size;
};

/* ── 静态假数据（地址稳定，多次调用返回同一份） ───────────────────────── */

static int s_crtcIds[1] = { 1 };
static int s_connectorIds[1] = { 2 };
static int s_encoderIds[1] = { 3 };
/* connector 的 encoders 数组也要有内容：OpenTK 的 GetEncoder 循环的是
 * c->count_encoders，为 0 就一次都不执行 → 拿不到 encoder → QueryDisplay 失败
 * → QueryDisplays 返回 false → 抛 PlatformNotSupportedException。
 * 这是无 GPU 环境里最容易漏的一环。 */
static int s_connectorEncoderIds[1] = { 3 };

static struct mode_info s_mode = {
    74250,                      /* clock */
    1280, 1390, 1430, 1650, 0,  /* hdisplay / hsync_* / htotal / hskew */
    720, 725, 730, 750, 0,      /* vdisplay / vsync_* / vtotal / vscan */
    60,                         /* vrefresh */
    0, 0,
    "1280x720"
};

static struct mode_connector s_connector = {
    2,                  /* connector_id */
    3,                  /* encoder_id */
    11,                 /* connector_type (DRM_MODE_CONNECTOR_HDMIA) */
    0,                  /* connector_type_id */
    1,                  /* connection = DRM_MODE_CONNECTED */
    0, 0,               /* mmWidth / mmHeight */
    0,                  /* subpixel */
    1,                  /* count_modes */
    &s_mode,            /* modes */
    0, NULL,                    /* count_props / props */
    NULL,                       /* prop_values */
    1, s_connectorEncoderIds    /* count_encoders / encoders */
};

static struct mode_encoder s_encoder = {
    3,      /* encoder_id */
    0,      /* encoder_type */
    1,      /* crtc_id */
    1,      /* possible_crtcs */
    0       /* possible_clones */
};

static struct mode_crtc s_crtc = {
    1,          /* crtc_id */
    0,          /* buffer_id */
    0, 0,       /* x, y */
    1280, 720,  /* width, height */
    1,          /* mode_valid */
    { 0 },      /* mode 会在下面补 */
    0           /* gamma_size */
};

static struct mode_res s_res = {
    0, NULL,            /* fbs */
    1, s_crtcIds,       /* crtcs */
    1, s_connectorIds,  /* connectors */
    1, s_encoderIds,    /* encoders */
    0, 4096,            /* min_width / max_width */
    0, 4096             /* min_height / max_height */
};

static int s_initialized;

static void ensure_init(void)
{
    if (s_initialized) return;
    /* 把 mode 复制进 crtc（避免 C 里对嵌套结构的动态初始化）。 */
    s_crtc.mode = s_mode;
    s_initialized = 1;
}

/* ── 导出函数 ─────────────────────────────────────────────────────────── */

HEADLESS_EXPORT void *drmModeGetResources(int fd)
{
    (void)fd;
    ensure_init();
    return &s_res;
}

HEADLESS_EXPORT void drmModeFreeResources(void *ptr)
{
    (void)ptr;
}

HEADLESS_EXPORT void *drmModeGetConnector(int fd, uint32_t connector_id)
{
    (void)fd;
    (void)connector_id;
    ensure_init();
    return &s_connector;
}

HEADLESS_EXPORT void *drmModeGetConnectorCurrent(int fd, uint32_t connector_id)
{
    return drmModeGetConnector(fd, connector_id);
}

HEADLESS_EXPORT void drmModeFreeConnector(void *ptr)
{
    (void)ptr;
}

HEADLESS_EXPORT void *drmModeGetEncoder(int fd, uint32_t encoder_id)
{
    (void)fd;
    (void)encoder_id;
    ensure_init();
    return &s_encoder;
}

HEADLESS_EXPORT void drmModeFreeEncoder(void *ptr)
{
    (void)ptr;
}

HEADLESS_EXPORT void *drmModeGetCrtc(int fd, uint32_t crtc_id)
{
    (void)fd;
    (void)crtc_id;
    ensure_init();
    return &s_crtc;
}

HEADLESS_EXPORT void drmModeFreeCrtc(void *ptr)
{
    (void)ptr;
}

/* 下面这些在无头环境里没有真实意义：一律报告"成功"。 */

HEADLESS_EXPORT int drmModeSetCrtc(int fd, uint32_t crtcId, uint32_t bufferId,
    int32_t x, int32_t y, uint32_t *connectors, int count, void *mode)
{
    (void)fd; (void)crtcId; (void)bufferId; (void)x; (void)y;
    (void)connectors; (void)count; (void)mode;
    return 0;
}

HEADLESS_EXPORT int drmModeSetCursor(int fd, uint32_t crtcId, uint32_t handle,
    uint32_t width, uint32_t height)
{
    (void)fd; (void)crtcId; (void)handle; (void)width; (void)height;
    return 0;
}

HEADLESS_EXPORT int drmModeSetCursor2(int fd, uint32_t crtcId, uint32_t handle,
    uint32_t width, uint32_t height, int32_t hotx, int32_t hoty)
{
    (void)fd; (void)crtcId; (void)handle; (void)width; (void)height;
    (void)hotx; (void)hoty;
    return 0;
}

HEADLESS_EXPORT int drmModeMoveCursor(int fd, uint32_t crtcId, int x, int y)
{
    (void)fd; (void)crtcId; (void)x; (void)y;
    return 0;
}

HEADLESS_EXPORT int drmModeAddFB(int fd, uint32_t width, uint32_t height,
    uint8_t depth, uint8_t bpp, uint32_t pitch, uint32_t bo_handle,
    uint32_t *buf_id)
{
    (void)fd; (void)width; (void)height; (void)depth; (void)bpp;
    (void)pitch; (void)bo_handle;
    if (buf_id) *buf_id = 1;
    return 0;
}

HEADLESS_EXPORT int drmModeAddFB2(int fd, uint32_t width, uint32_t height,
    uint32_t pixel_format, const uint32_t bo_handles[4],
    const uint32_t pitches[4], const uint32_t offsets[4], uint32_t *buf_id,
    uint32_t flags)
{
    (void)fd; (void)width; (void)height; (void)pixel_format;
    (void)bo_handles; (void)pitches; (void)offsets; (void)flags;
    if (buf_id) *buf_id = 1;
    return 0;
}

HEADLESS_EXPORT int drmModeRmFB(int fd, uint32_t bufferId)
{
    (void)fd; (void)bufferId;
    return 0;
}

HEADLESS_EXPORT int drmModePageFlip(int fd, uint32_t crtc_id, uint32_t fb_id,
    uint32_t flags, void *user_data)
{
    (void)fd; (void)crtc_id; (void)fb_id; (void)flags; (void)user_data;
    //
    // 必须立刻让 OpenTK 的 poll 就绪。它的等待循环是：
    //     while (is_flip_queued) { poll(fd, 1, -1); drmHandleEvent(fd, &ctx); }
    // 而这个标志正是本函数（QueueFlip 调用）置位的。
    // 往 libc 空壳的 eventfd 写一个值 -> poll 立刻返回可读 ->
    // 紧接着 drmHandleEvent 回调 page_flip_handler 把标志清掉 -> 主循环继续。
    //
    // 直接往 fd 里写一个 8 字节计数 —— 这个 fd 就是 libc 代理在
    // open("/dev/dri/cardN") 时返回的 eventfd（OpenTK 把它存进
    // LinuxWindowInfo.FD 再传给这里）。poll 因此立刻返回可读。
    //
    // 为什么不用 headless_drm_notify() 跨库调用：两个 .so 是分别 dlopen 的，
    // 符号不在同一个全局作用域，dlsym(RTLD_DEFAULT) 找不到（实测报
    // "headless_drm_notify not found"）。而 fd 本身就是现成的通道，
    // 直接按 eventfd 语义写就行，不需要任何跨库符号。
    {
        uint64_t wake = 1;
        ssize_t written = write(fd, &wake, sizeof(wake));
        (void)written;
    }
    return 0;
}

//
// drmHandleEvent：OpenTK 的 page-flip 等待循环就靠它把 is_flip_queued 清掉。
//
// OpenTK 的等待逻辑（OpenTK/Platform/Linux/LinuxGraphicsContext.WaitFlip）：
//     while (is_flip_queued) {
//         poll(fd, 1, -1);          // block=true 时 timeout=-1，会一直等可读
//         drmHandleEvent(fd, &evctx);
//     }
// is_flip_queued 只有 page_flip_handler 里才会被清掉。若这里什么都不做，
// 主线程就会永远卡在 poll 上（实测 /proc/<pid>/wchan = do_sys_poll，CPU 1%），
// 游戏主循环不再推进 → 控制接口全部超时、终端菜单永远停在 Waiting。
//
// 所以这里主动用 evctx 里的 page_flip_handler 回调一次，表示"翻页已完成"。
// 回调签名（drm_event.h）：
//     void (*page_flip_handler)(int fd, unsigned int sequence,
//                              unsigned int tv_sec, unsigned int tv_usec, void *user_data)
HEADLESS_EXPORT int drmHandleEvent(int fd, void *evctx)
{
    if (!evctx) return 0;
    // EventContext 布局（OpenTK/Platform/Linux/EventContext）：
    //   int version; void *vblank_handler; void *page_flip_handler;
    // 64 位下 version 占 4 字节 + 4 字节对齐填充，两个指针各 8 字节。
    unsigned char *base = (unsigned char *)evctx;
    void *page_flip_handler = *(void **)(base + 16);
    if (!page_flip_handler) return 0;
    typedef void (*page_flip_fn)(int, unsigned int, unsigned int, unsigned int, void *);
    page_flip_fn handler = (page_flip_fn)page_flip_handler;
    handler(fd, 0, 0, 0, NULL);
    return 0;
}

HEADLESS_EXPORT int drmGetVersion(void *fd)
{
    (void)fd;
    return 0;
}

HEADLESS_EXPORT char *drmGetVersionString(void *version)
{
    (void)version;
    return "headless";
}

HEADLESS_EXPORT void drmFreeVersion(void *version)
{
    (void)version;
}
