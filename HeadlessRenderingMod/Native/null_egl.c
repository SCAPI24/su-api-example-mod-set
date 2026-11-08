/*
 * Linux/macOS 版 EGL 空实现后端（no-GPU）。
 *
 * 与 Windows 的 `opengl32-headless.dll` 是同一角色的东西：Windows 上那个替身
 * 代理真实的 WGL 调用，只在缺失时回落到空实现；Linux 上 OpenTK 走的是 EGL，
 * 而这台无头服务器**根本没有 GPU**，所以这里直接给出一整套"看起来成功、
 * 实际什么都不画"的 EGL 实现。
 *
 * Source: OpenTK/OpenTK/Platform/Linux/LinuxFactory.cs（EGL 初始化与显示探测）
 * Source: OpenTK/OpenTK/Graphics/GraphicsContext.cs（上下文创建）
 * Source: Engine/Engine/Window.cs:Window.Run（用到 DisplayDevice.Default）
 *
 * 设计口径（用户 2026-11-08 定）：**所有需要 OpenGL 的渲染一律置空**。
 * 因此这里不尝试加载真实的 libEGL：
 *   · eglGetDisplay 返回同一个假 display（非 EGL_NO_DISPLAY）；
 *   · eglInitialize / eglChooseConfig / eglCreateContext 一律返回成功；
 *   · eglGetProcAddress 把请求转给本库自己的 GLES 空实现（同目录的
 *     libGLESv2-headless.so）—— 这样 OpenTK 拿到的每个 GL 入口都是空函数，
 *     不会去碰真实驱动；
 *   · eglSwapBuffers 什么都不做（没有需要呈现的画面）。
 *
 * 编译：gcc -shared -fPIC -O2 -o libEGL-headless.so null_egl.c -ldl
 */

#include <dlfcn.h>
#include <unistd.h>
#include <stdint.h>
#include <string.h>
#include <stdio.h>

#if defined(_WIN32) || defined(_WIN64)
#  define HEADLESS_EXPORT __declspec(dllexport)
#else
#  define HEADLESS_EXPORT __attribute__((visibility("default")))
#endif

/* EGL 公开常量（只列这里用到的）。 */
#define EGL_FALSE 0
#define EGL_TRUE 1
#define EGL_SUCCESS 0x3000
#define EGL_NO_CONTEXT ((void *)0)
#define EGL_NO_DISPLAY ((void *)0)
#define EGL_NO_SURFACE ((void *)0)
#define EGL_VENDOR 0x3053
#define EGL_VERSION 0x3054
#define EGL_EXTENSIONS 0x3055

/* 假的 display / surface / context 句柄：只要非 NULL，OpenTK 就认为初始化成功。 */
static int s_fakeDisplay = 1;
static int s_fakeSurface = 2;
static int s_fakeContext = 3;

/* 指向 GLES 空实现的函数指针（延迟加载，找不到就返回 NULL 让调用方自己报错）。 */
static void *s_glesHandle;

/*
 * 找到 GLES 空实现库。
 *
 * 关键：**必须用绝对路径**。dlopen 给相对名时只查 RPATH / LD_LIBRARY_PATH 与
 * 系统库目录，**不会**查"当前进程可执行文件所在目录"——而无头 mod 把库提取在
 * 游戏目录里（AppContext.BaseDirectory），相对名永远找不到（实测：所有
 * eglGetProcAddress 都返回 NULL，随后 OpenTK 调用空函数指针直接 SIGSEGV）。
 *
 * 探测顺序：
 *   1. 游戏目录/libGLESv2.so.2          ← openal.so.1 同款命名，发行包里的正式名
 *   2. 游戏目录/libGLESv2-headless.so   ← mod 自带命名（若将来独立提取）
 *   3. 相对名（兜底：系统真装了 Mesa 的情况）
 */
static void *gles_library(void)
{
    if (s_glesHandle) return s_glesHandle;

    /* 读 /proc/self/exe 拿可执行文件目录 —— 不依赖 .NET 的 AppContext。 */
    char exeDir[4096];
    exeDir[0] = '\0';
    ssize_t n = readlink("/proc/self/exe", exeDir, sizeof(exeDir) - 1);
    if (n > 0)
    {
        exeDir[n] = '\0';
        char *slash = strrchr(exeDir, '/');
        if (slash) *slash = '\0';
    }

    const char *candidates[] = {
        "libGLESv2.so.2",
        "libGLESv2-headless.so",
        NULL
    };
    for (int i = 0; candidates[i]; i++)
    {
        if (exeDir[0])
        {
            char path[4096];
            snprintf(path, sizeof(path), "%s/%s", exeDir, candidates[i]);
            s_glesHandle = dlopen(path, RTLD_LAZY);
            if (s_glesHandle) return s_glesHandle;
        }
        s_glesHandle = dlopen(candidates[i], RTLD_LAZY);
        if (s_glesHandle) return s_glesHandle;
    }
    return NULL;
}

HEADLESS_EXPORT void *eglGetDisplay(void *nativeDisplay)
{
    (void)nativeDisplay;
    return (void *)&s_fakeDisplay;
}

HEADLESS_EXPORT int eglInitialize(void *display, int *major, int *minor)
{
    (void)display;
    if (major) *major = 1;
    if (minor) *minor = 5;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglTerminate(void *display)
{
    (void)display;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglGetError(void) { return EGL_SUCCESS; }

HEADLESS_EXPORT const char *eglQueryString(void *display, int name)
{
    (void)display;
    switch (name)
    {
    case EGL_VENDOR: return "HeadlessRenderingMod";
    case EGL_VERSION: return "1.5 Headless";
    case EGL_EXTENSIONS: return "";
    default: return "";
    }
}

HEADLESS_EXPORT int eglChooseConfig(void *display, const int *attribs, void **configs,
    int configSize, int *numConfig)
{
    (void)display;
    (void)attribs;
    /* 给一个假配置：OpenTK 只需要拿到非空配置数组与数量 > 0。 */
    if (configs && configSize > 0)
        configs[0] = (void *)&s_fakeContext;
    if (numConfig) *numConfig = 1;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglGetConfigs(void *display, void **configs, int configSize,
    int *numConfig)
{
    (void)display;
    if (configs && configSize > 0)
        configs[0] = (void *)&s_fakeContext;
    if (numConfig) *numConfig = 1;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglGetConfigAttrib(void *display, void *config, int attribute,
    int *value)
{
    (void)display;
    (void)config;
    (void)attribute;
    if (value) *value = 0;
    return EGL_TRUE;
}

HEADLESS_EXPORT void *eglCreateContext(void *display, void *config, void *shareContext,
    const int *attribs)
{
    (void)display;
    (void)config;
    (void)shareContext;
    (void)attribs;
    return (void *)&s_fakeContext;
}

HEADLESS_EXPORT int eglDestroyContext(void *display, void *context)
{
    (void)display;
    (void)context;
    return EGL_TRUE;
}

HEADLESS_EXPORT void *eglCreateWindowSurface(void *display, void *config,
    void *nativeWindow, const int *attribs)
{
    (void)display;
    (void)config;
    (void)nativeWindow;
    (void)attribs;
    return (void *)&s_fakeSurface;
}

HEADLESS_EXPORT void *eglCreatePbufferSurface(void *display, void *config,
    const int *attribs)
{
    (void)display;
    (void)config;
    (void)attribs;
    return (void *)&s_fakeSurface;
}

HEADLESS_EXPORT void *eglCreatePixmapSurface(void *display, void *config,
    void *nativePixmap, const int *attribs)
{
    (void)display;
    (void)config;
    (void)nativePixmap;
    (void)attribs;
    return (void *)&s_fakeSurface;
}

HEADLESS_EXPORT void *eglCreatePbufferFromClientBuffer(void *display, int buftype,
    void *buffer, void *config, const int *attribs)
{
    (void)display;
    (void)buftype;
    (void)buffer;
    (void)config;
    (void)attribs;
    return (void *)&s_fakeSurface;
}

HEADLESS_EXPORT int eglDestroySurface(void *display, void *surface)
{
    (void)display;
    (void)surface;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglMakeCurrent(void *display, void *draw, void *read, void *context)
{
    (void)display;
    (void)draw;
    (void)read;
    (void)context;
    return EGL_TRUE;
}

HEADLESS_EXPORT void *eglGetCurrentContext(void) { return (void *)&s_fakeContext; }

HEADLESS_EXPORT void *eglGetCurrentDisplay(void) { return (void *)&s_fakeDisplay; }

HEADLESS_EXPORT void *eglGetCurrentSurface(int readdraw)
{
    (void)readdraw;
    return (void *)&s_fakeSurface;
}

HEADLESS_EXPORT int eglQueryContext(void *display, void *context, int attribute,
    int *value)
{
    (void)display;
    (void)context;
    (void)attribute;
    if (value) *value = 0;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglQuerySurface(void *display, void *surface, int attribute,
    int *value)
{
    (void)display;
    (void)surface;
    (void)attribute;
    if (value) *value = 0;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglSwapBuffers(void *display, void *surface)
{
    (void)display;
    (void)surface;
    /* 没有画面要呈现 —— 这就是"渲染置空"的核心。 */
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglSwapInterval(void *display, int interval)
{
    (void)display;
    (void)interval;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglBindAPI(unsigned int api)
{
    (void)api;
    return EGL_TRUE;
}

HEADLESS_EXPORT unsigned int eglQueryAPI(void) { return 0x30A0u; /* EGL_OPENGL_ES_API */ }

HEADLESS_EXPORT int eglReleaseThread(void) { return EGL_TRUE; }

HEADLESS_EXPORT int eglWaitClient(void) { return EGL_TRUE; }

HEADLESS_EXPORT int eglWaitGL(void) { return EGL_TRUE; }

HEADLESS_EXPORT int eglWaitNative(int engine) { (void)engine; return EGL_TRUE; }

HEADLESS_EXPORT int eglSurfaceAttrib(void *display, void *surface, int attribute,
    int value)
{
    (void)display;
    (void)surface;
    (void)attribute;
    (void)value;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglBindTexImage(void *display, void *surface, int buffer)
{
    (void)display;
    (void)surface;
    (void)buffer;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglReleaseTexImage(void *display, void *surface, int buffer)
{
    (void)display;
    (void)surface;
    (void)buffer;
    return EGL_TRUE;
}

HEADLESS_EXPORT int eglCopyBuffers(void *display, void *surface, void *nativePixmap)
{
    (void)display;
    (void)surface;
    (void)nativePixmap;
    return EGL_TRUE;
}

/*
 * eglGetProcAddress：OpenTK 靠它拿 GL 入口。
 * 一律转给 GLES 空实现库 —— GL 调用全部变成空操作，正是我们要的效果。
 */
HEADLESS_EXPORT void *eglGetProcAddress(const char *name)
{
    if (!name) return 0;
    void *library = gles_library();
    if (library)
    {
        void *symbol = dlsym(library, name);
        if (symbol) return symbol;
    }
    return 0;
}
