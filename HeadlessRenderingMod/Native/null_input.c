/*
 * Linux 版 libinput 空实现后端（无输入设备）。
 *
 * 为什么需要它：OpenTK 在 Linux 上会起一个**输入线程**（LinuxInput.Setup →
 * LibInput.CreateContext），它要求 libinput 里有一个旧版 API
 * `libinput_udev_create_for_seat`。新发行版的 libinput 已经把这个入口移除了
 * （实测 Debian 13 的 libinput.so.10 就没有它），于是进程抛
 * EntryPointNotFoundException 直接退出：
 *
 *     Unable to find an entry point named 'libinput_udev_create_for_seat'
 *        at OpenTK.Platform.Linux.LibInput.CreateContext(...)
 *        at OpenTK.Platform.Linux.LinuxInput.Setup()
 *        at OpenTK.Platform.Linux.LinuxInput.InputThreadLoop(...)
 *
 * 无头服务器根本不需要键盘/鼠标，所以这里的口径是**把输入整套置空**：
 *   · create_context 返回一个非 NULL 的假句柄（否则 OpenTK 会认为失败）；
 *   · dispatch / next_event_type 返回"没有事件"；
 *   · get_fd 返回一个永远不可读的 fd（-1）—— 输入线程会阻塞在 poll 上睡过去，
 *     不会空转烧 CPU；
 *   · 所有 device/seat 查询返回空字符串或 0。
 *
 * Source: OpenTK/OpenTK/Platform/Linux/LibInput.cs（全部 P/Invoke 声明）
 * Source: OpenTK/OpenTK/Platform/Linux/LinuxInput.cs:Setup / InputThreadLoop
 *
 * 编译：gcc -shared -fPIC -O2 -o libinput-headless.so null_input.c
 */

#include <stdint.h>
#include <stddef.h>
#include <unistd.h>

#if defined(_WIN32) || defined(_WIN64)
#  define HEADLESS_EXPORT __declspec(dllexport)
#else
#  define HEADLESS_EXPORT __attribute__((visibility("default")))
#endif

/* 假句柄：地址稳定，OpenTK 只把它当不透明指针传递。 */
static int s_fakeLibinput = 1;
static int s_fakeUdev = 2;
static int s_fakeEvent = 3;
static int s_fakeSeat = 4;
static int s_fakeDevice = 5;

/*
 * 设备事件只投递**一次**：OpenTK 的输入线程靠 DeviceAdded 建立键盘/鼠标对象，
 * 没有这一步 Keyboard.GetState() 会因为"一个键盘都没有"抛 NotSupportedException，
 * 整个 ProcessEvents 中止 → 游戏起不来。投递一次后一直返回 NULL（队列空）。
 */
static int s_deviceAddedDelivered;

// get_fd 返回的假 fd（pipe 读端，永久不可读）。
static int s_fakePollFd = -1;

/* libinput 的事件类型枚举里 0 = LIBINPUT_EVENT_NONE，正好是"没有事件"。 */
#define NO_EVENT 0

/* ── udev ─────────────────────────────────────────────────────────────── */

HEADLESS_EXPORT void *udev_new(void)
{
    return &s_fakeUdev;
}

HEADLESS_EXPORT void udev_destroy(void *udev)
{
    (void)udev;
}

HEADLESS_EXPORT void *udev_unref(void *udev)
{
    (void)udev;
    return NULL;
}

HEADLESS_EXPORT void *udev_ref(void *udev)
{
    return udev;
}

/* ── libinput 上下文 ─────────────────────────────────────────────────── */

// libinput 的 open_restricted 回调签名：int (*)(const char *path, int flags, void *user_data)
typedef int (*open_restricted_fn)(const char *path, int flags, void *user_data);

// InputInterface 的布局（OpenTK/Platform/Linux/InputInterface）：
//   struct { IntPtr open; IntPtr close; }   —— 两个函数指针
struct input_interface
{
    void *open;
    void *close;
};

HEADLESS_EXPORT void *libinput_udev_create_for_seat(struct input_interface *iface,
    void *user_data, void *udev, const char *seat_id)
{
    (void)udev;
    (void)seat_id;
    //
    // 关键：必须**回调一次 open_restricted**。
    // OpenTK 的 LinuxInput.Setup() 最后一步是：
    //     if (DeviceFDCount <= 0) { exit++; }
    // 而 DeviceFDCount 只在 open_restricted 回调里 +1。
    // 空壳若不调用它，输入线程被判失败，Keyboard 静态构造抛 NotSupportedException。
    // 回调时给一个合法 fd（fd 只用于计数，不会被真的 poll，因为我们自己管理 get_fd）。
    if (iface && iface->open)
    {
        open_restricted_fn open_fn = (open_restricted_fn)iface->open;
        // 0 = stdin，一定合法；真实设备路径无所谓（不会有人去读它）。
        (void)open_fn("/dev/null", 0, user_data);
    }
    return &s_fakeLibinput;
}

HEADLESS_EXPORT void *libinput_path_create_context(int iface, void *user_data)
{
    (void)iface;
    (void)user_data;
    return &s_fakeLibinput;
}

HEADLESS_EXPORT void libinput_destroy(void *libinput)
{
    (void)libinput;
}

HEADLESS_EXPORT void *libinput_unref(void *libinput)
{
    (void)libinput;
    return NULL;
}

HEADLESS_EXPORT void *libinput_ref(void *libinput)
{
    return libinput;
}

/*
 * 输入线程的主循环靠 fd 做 poll。返回 -1 让它认为"没有可读 fd"并退化成
 * 定时轮询（OpenTK 里对 -1 会等待一个超时），不会空转。
 */
HEADLESS_EXPORT int libinput_get_fd(void *libinput)
{
    (void)libinput;
    // 必须返回有效的非负 fd：OpenTK 的 LinuxInput.Setup() 里
    //     fd = GetFD(ctx);  if (fd < 0) { exit++; return; }
    // 返回 -1 会把输入线程判成失败，构造函数抛 NotSupportedException，
    // 游戏在 Keyboard 静态构造就崩。
    //
    // 这里用一个 pipe 的读端：没有任何写入者，poll() 会一直阻塞，
    // 输入线程安静睡过去，不空转烧 CPU。
    if (s_fakePollFd < 0)
    {
        int fds[2];
        if (pipe(fds) == 0)
        {
            s_fakePollFd = fds[0];
            close(fds[1]);
        }
        else
        {
            s_fakePollFd = 0;
        }
    }
    return s_fakePollFd;
}

HEADLESS_EXPORT int libinput_dispatch(void *libinput)
{
    (void)libinput;
    /* 0 = 成功且没有错误；没有事件要派发。 */
    return 0;
}

HEADLESS_EXPORT void *libinput_get_event(void *libinput)
{
    (void)libinput;
    /* NULL = 队列空。 */
    if (!s_deviceAddedDelivered)
    {
        s_deviceAddedDelivered = 1;
        return &s_fakeEvent;
    }
    /* NULL = 队列空。 */
    return NULL;
}

HEADLESS_EXPORT int libinput_next_event_type(void *libinput)
{
    (void)libinput;
    return NO_EVENT;
}

HEADLESS_EXPORT void libinput_suspend(void *libinput)
{
    (void)libinput;
}

HEADLESS_EXPORT void libinput_resume(void *libinput)
{
    (void)libinput;
}

/* ── 事件 ─────────────────────────────────────────────────────────────── */

HEADLESS_EXPORT void libinput_event_destroy(void *event)
{
    (void)event;
}

HEADLESS_EXPORT void *libinput_event_get_device(void *event)
{
    (void)event;
    return &s_fakeDevice;
}

HEADLESS_EXPORT int libinput_event_get_type(void *event)
{
    /* 1 = InputEventType.DeviceAdded（见 OpenTK/Platform/Linux/InputEventType） */
    return 1;
}

HEADLESS_EXPORT void *libinput_event_get_context(void *event)
{
    (void)event;
    return &s_fakeLibinput;
}

/* OpenTK 把这些返回结构体**按值**接收，这里给全零结构即可（不会有人读）。 */

typedef struct { int dummy; } fake_struct_16;

HEADLESS_EXPORT fake_struct_16 libinput_event_get_keyboard_event(void *event)
{
    (void)event;
    fake_struct_16 value;
    value.dummy = 0;
    return value;
}

HEADLESS_EXPORT fake_struct_16 libinput_event_get_pointer_event(void *event)
{
    (void)event;
    fake_struct_16 value;
    value.dummy = 0;
    return value;
}

HEADLESS_EXPORT fake_struct_16 libinput_event_get_touch_event(void *event)
{
    (void)event;
    fake_struct_16 value;
    value.dummy = 0;
    return value;
}

/* ── 设备 ─────────────────────────────────────────────────────────────── */

HEADLESS_EXPORT const char *libinput_device_get_sysname(void *device)
{
    (void)device;
    return "";
}

HEADLESS_EXPORT const char *libinput_device_get_name(void *device)
{
    (void)device;
    return "";
}

HEADLESS_EXPORT const char *libinput_device_get_output_name(void *device)
{
    (void)device;
    return "";
}

HEADLESS_EXPORT void *libinput_device_get_user_data(void *device)
{
    (void)device;
    return NULL;
}

HEADLESS_EXPORT void libinput_device_set_user_data(void *device, void *user_data)
{
    (void)device;
    (void)user_data;
}

HEADLESS_EXPORT void *libinput_device_get_seat(void *device)
{
    (void)device;
    return &s_fakeSeat;
}

HEADLESS_EXPORT void *libinput_device_get_context(void *device)
{
    (void)device;
    return &s_fakeLibinput;
}

HEADLESS_EXPORT int libinput_device_has_capability(void *device, int capability)
{
    (void)device;
    (void)capability;
    /* DeviceCapability: 0 = Keyboard。报"有键盘"让 OpenTK 能建出键盘对象，
       真实按键永远不会到达（get_event 之后一律返回 NULL），等于输入全空。 */
    return capability == 0;
}

HEADLESS_EXPORT int libinput_device_config_tap_get_finger_count(void *device)
{
    (void)device;
    return 0;
}

HEADLESS_EXPORT void libinput_device_ref(void *device)
{
    (void)device;
}

HEADLESS_EXPORT void libinput_device_unref(void *device)
{
    (void)device;
}

/* ── seat ─────────────────────────────────────────────────────────────── */

HEADLESS_EXPORT const char *libinput_seat_get_logical_name(void *seat)
{
    (void)seat;
    return "seat0";
}

HEADLESS_EXPORT const char *libinput_seat_get_physical_name(void *seat)
{
    (void)seat;
    return "headless";
}

HEADLESS_EXPORT void libinput_seat_ref(void *seat)
{
    (void)seat;
}

HEADLESS_EXPORT void libinput_seat_unref(void *seat)
{
    (void)seat;
}
