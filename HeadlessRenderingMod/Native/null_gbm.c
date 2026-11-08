/*
 * Linux/macOS 版 GBM 空实现后端（no-GPU）。
 *
 * 为什么需要它：OpenTK 在 Linux 上初始化 DisplayDevice 时，**第一步**就是
 * `Gbm.CreateDevice(open("/dev/dri/renderD128"))` —— 它想拿到一个 DRM 设备句柄
 * 再来创建 EGL display。没有 GPU 的服务器上这个库根本不存在，于是进程在
 * `Program.Main → Window.Run → DisplayDevice..cctor` 就直接 DllNotFoundException 退出。
 *
 * 这个空壳让那条探测路径"看起来成功"：
 *   · gbm_create_device 返回一个假的 device（非 NULL）；
 *   · 所有 gbm_bo_* / gbm_surface_* 返回假句柄或 0；
 *   · 查询类函数（宽高 / stride / fd）返回中性值。
 * 真正的绘制从一开始就不该发生 —— 上层由 libEGL-headless + libGLESv2-headless
 * 接住，这里只负责让探测流程走过去。
 *
 * Source: OpenTK/OpenTK/Platform/Linux/Gbm.cs（CreateDevice / CreateSurface / LockFrontBuffer）
 * Source: OpenTK/OpenTK/Platform/Linux/LinuxFactory.cs:SetupDisplay
 *
 * 编译：gcc -shared -fPIC -O2 -o libgbm-headless.so null_gbm.c
 */

#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32) || defined(_WIN64)
#  define HEADLESS_EXPORT __declspec(dllexport)
#else
#  define HEADLESS_EXPORT __attribute__((visibility("default")))
#endif

/* gbm_bo_* 需要的结构体在 OpenTK 里只按指针传递，这里给不透明占位即可。 */
struct gbm_device
{
    int fd;
    char name[64];
};

struct gbm_bo
{
    struct gbm_device *device;
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    void *user_data;
};

struct gbm_surface
{
    struct gbm_device *device;
};

/* 静态假对象：地址稳定，OpenTK 多次调用拿到同一个句柄。 */
static struct gbm_device s_fakeDevice = { -1, "headless" };
static struct gbm_bo s_fakeBo = { &s_fakeDevice, 1, 1, 4, NULL };
static struct gbm_surface s_fakeSurface = { &s_fakeDevice };

/* ─── device ─────────────────────────────────────────────────────────── */

HEADLESS_EXPORT struct gbm_device *gbm_create_device(int fd)
{
    (void)fd;
    /* 返回非 NULL 即可让 SetupDisplay 继续往下走。 */
    return &s_fakeDevice;
}

HEADLESS_EXPORT void gbm_device_destroy(struct gbm_device *device)
{
    (void)device;
}

HEADLESS_EXPORT int gbm_device_get_fd(struct gbm_device *device)
{
    return device ? device->fd : -1;
}

HEADLESS_EXPORT const char *gbm_device_get_backend_name(struct gbm_device *device)
{
    (void)device;
    return "headless";
}

HEADLESS_EXPORT int gbm_device_is_format_supported(struct gbm_device *device,
    uint32_t format, uint32_t usage)
{
    (void)device;
    (void)format;
    (void)usage;
    /* 一律不支持真实格式：上层因此不会真的去分配缓冲。 */
    return 0;
}

HEADLESS_EXPORT int gbm_device_get_format_modifier_plane_count(struct gbm_device *device,
    uint32_t format, uint64_t modifier)
{
    (void)device;
    (void)format;
    (void)modifier;
    return 0;
}

/* ─── buffer object ──────────────────────────────────────────────────── */

HEADLESS_EXPORT struct gbm_bo *gbm_bo_create(struct gbm_device *device,
    uint32_t width, uint32_t height, uint32_t format, uint32_t flags)
{
    (void)format;
    (void)flags;
    s_fakeBo.device = device;
    s_fakeBo.width = width ? width : 1;
    s_fakeBo.height = height ? height : 1;
    s_fakeBo.stride = s_fakeBo.width * 4;
    return &s_fakeBo;
}

HEADLESS_EXPORT struct gbm_bo *gbm_bo_create_with_modifiers(struct gbm_device *device,
    uint32_t width, uint32_t height, uint32_t format, const uint64_t *modifiers,
    unsigned int count)
{
    (void)modifiers;
    (void)count;
    return gbm_bo_create(device, width, height, format, 0);
}

HEADLESS_EXPORT void gbm_bo_destroy(struct gbm_bo *bo)
{
    (void)bo;
}

HEADLESS_EXPORT struct gbm_device *gbm_bo_get_device(struct gbm_bo *bo)
{
    return bo ? bo->device : &s_fakeDevice;
}

HEADLESS_EXPORT uint32_t gbm_bo_get_width(struct gbm_bo *bo)
{
    return bo ? bo->width : 1;
}

HEADLESS_EXPORT uint32_t gbm_bo_get_height(struct gbm_bo *bo)
{
    return bo ? bo->height : 1;
}

HEADLESS_EXPORT uint32_t gbm_bo_get_stride(struct gbm_bo *bo)
{
    return bo ? bo->stride : 4;
}

HEADLESS_EXPORT uint32_t gbm_bo_get_stride_for_plane(struct gbm_bo *bo, int plane)
{
    (void)plane;
    return bo ? bo->stride : 4;
}

HEADLESS_EXPORT uint32_t gbm_bo_get_format(struct gbm_bo *bo)
{
    (void)bo;
    return 0;
}

HEADLESS_EXPORT uint32_t gbm_bo_get_bpp(struct gbm_bo *bo)
{
    (void)bo;
    return 32;
}

HEADLESS_EXPORT uint64_t gbm_bo_get_modifier(struct gbm_bo *bo)
{
    (void)bo;
    return 0;
}

HEADLESS_EXPORT int gbm_bo_get_plane_count(struct gbm_bo *bo)
{
    (void)bo;
    return 1;
}

HEADLESS_EXPORT int gbm_bo_get_offset(struct gbm_bo *bo, int plane)
{
    (void)bo;
    (void)plane;
    return 0;
}

HEADLESS_EXPORT int gbm_bo_get_fd(struct gbm_bo *bo)
{
    (void)bo;
    return -1;
}

HEADLESS_EXPORT int gbm_bo_get_fd_for_plane(struct gbm_bo *bo, int plane)
{
    (void)bo;
    (void)plane;
    return -1;
}

HEADLESS_EXPORT void *gbm_bo_get_user_data(struct gbm_bo *bo)
{
    return bo ? bo->user_data : NULL;
}

HEADLESS_EXPORT void gbm_bo_set_user_data(struct gbm_bo *bo, void *data,
    void (*destroy_user_data)(struct gbm_bo *, void *))
{
    (void)destroy_user_data;
    if (bo) bo->user_data = data;
}

/* gbm_bo_handle 在真正的 gbm.h 里是 union；OpenTK 按"值"接收，
 * 这里用一个同宽的 typedef，并用一条正规函数导出它的内容。 */
typedef union
{
    void *ptr;
    int32_t s32;
    uint32_t u32;
    int64_t s64;
    uint64_t u64;
} gbm_bo_handle_t;

/*
 * 注意：C 不允许"函数返回匿名 union"，所以这里返回真实 gbm.h 里的名字。
 * 为了让 dlsym 能找到 `gbm_bo_get_handle`，符号名必须精确一致；
 * ABI 上这个 union 是 8 字节按值返回，与真实库一致。
 */
HEADLESS_EXPORT gbm_bo_handle_t gbm_bo_get_handle(struct gbm_bo *bo)
{
    gbm_bo_handle_t handle;
    handle.u64 = 0;
    (void)bo;
    return handle;
}

HEADLESS_EXPORT int gbm_bo_write(struct gbm_bo *bo, const void *buf, size_t count)
{
    (void)bo;
    (void)buf;
    (void)count;
    /* 不真的写显存：报告"写成功"即可，没有人会去读回来。 */
    return 0;
}

/* ─── surface ────────────────────────────────────────────────────────── */

HEADLESS_EXPORT struct gbm_surface *gbm_surface_create(struct gbm_device *device,
    uint32_t width, uint32_t height, uint32_t format, uint32_t flags)
{
    (void)width;
    (void)height;
    (void)format;
    (void)flags;
    s_fakeSurface.device = device;
    return &s_fakeSurface;
}

HEADLESS_EXPORT struct gbm_surface *gbm_surface_create_with_modifiers(
    struct gbm_device *device, uint32_t width, uint32_t height, uint32_t format,
    const uint64_t *modifiers, unsigned int count)
{
    (void)modifiers;
    (void)count;
    return gbm_surface_create(device, width, height, format, 0);
}

HEADLESS_EXPORT void gbm_surface_destroy(struct gbm_surface *surface)
{
    (void)surface;
}

HEADLESS_EXPORT int gbm_surface_has_free_buffers(struct gbm_surface *surface)
{
    (void)surface;
    return 1;
}

HEADLESS_EXPORT struct gbm_bo *gbm_surface_lock_front_buffer(struct gbm_surface *surface)
{
    (void)surface;
    return &s_fakeBo;
}

HEADLESS_EXPORT void gbm_surface_release_buffer(struct gbm_surface *surface,
    struct gbm_bo *bo)
{
    (void)surface;
    (void)bo;
}

HEADLESS_EXPORT int gbm_surface_is_defined(struct gbm_surface *surface)
{
    (void)surface;
    return 1;
}
