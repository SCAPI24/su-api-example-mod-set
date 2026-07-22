#define WINGDIAPI
#include <windows.h>

#if defined(__GNUC__)
#pragma GCC diagnostic ignored "-Wattributes"
#endif

/*
 * Source: OpenTK/OpenTK/Graphics/GraphicsContext.cs and OpenTK/OpenTK/Graphics/GL.cs
 * Keep the real WGL context and every available GL2 entry point.  Only a missing
 * entry point is replaced by the no-op symbols from the embedded GLES backend.
 */
#define NULL_OPENGL_PROXY 1
#include "null_gles2.c"

static HMODULE s_real_opengl32;

static HMODULE real_opengl32(void)
{
    if (!s_real_opengl32)
        s_real_opengl32 = LoadLibraryA("opengl32.dll");
    return s_real_opengl32;
}

static FARPROC real_proc(const char *name)
{
    HMODULE module = real_opengl32();
    return module ? GetProcAddress(module, name) : NULL;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(instance);
    return TRUE;
}

__declspec(dllexport) HGLRC WINAPI wglCreateContext(HDC dc)
{
    typedef HGLRC (WINAPI *Fn)(HDC);
    Fn fn = (Fn)real_proc("wglCreateContext");
    return fn ? fn(dc) : NULL;
}

__declspec(dllexport) BOOL WINAPI wglDeleteContext(HGLRC context)
{
    typedef BOOL (WINAPI *Fn)(HGLRC);
    Fn fn = (Fn)real_proc("wglDeleteContext");
    return fn ? fn(context) : FALSE;
}

__declspec(dllexport) BOOL WINAPI wglMakeCurrent(HDC dc, HGLRC context)
{
    typedef BOOL (WINAPI *Fn)(HDC, HGLRC);
    Fn fn = (Fn)real_proc("wglMakeCurrent");
    return fn ? fn(dc, context) : FALSE;
}

__declspec(dllexport) HGLRC WINAPI wglGetCurrentContext(void)
{
    typedef HGLRC (WINAPI *Fn)(void);
    Fn fn = (Fn)real_proc("wglGetCurrentContext");
    return fn ? fn() : NULL;
}

__declspec(dllexport) HDC WINAPI wglGetCurrentDC(void)
{
    typedef HDC (WINAPI *Fn)(void);
    Fn fn = (Fn)real_proc("wglGetCurrentDC");
    return fn ? fn() : NULL;
}

__declspec(dllexport) PROC WINAPI wglGetProcAddress(LPCSTR name)
{
    if (!name) return NULL;
    FARPROC proc = real_proc(name);
    if (proc) return (PROC)proc;

    HMODULE self = NULL;
    GetModuleHandleExA(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCSTR)&wglGetProcAddress,
        &self);
    return self ? (PROC)GetProcAddress(self, name) : NULL;
}

__declspec(dllexport) BOOL WINAPI wglShareLists(HGLRC first, HGLRC second)
{
    typedef BOOL (WINAPI *Fn)(HGLRC, HGLRC);
    Fn fn = (Fn)real_proc("wglShareLists");
    return fn ? fn(first, second) : FALSE;
}
