#include <stdint.h>
#include <string.h>

/* Source: OpenTK/OpenTK.Graphics.ES20/GL.cs:GL.Core
 * Source: Engine/Engine/Graphics/VertexBuffer.cs:VertexBuffer.AllocateBuffer
 * This is a no-GPU GLES2 backend for HeadlessRenderingMod. It deliberately
 * stores no pixels and performs no drawing; handles only keep Engine resource
 * lifetimes valid while the root Widget draw tree is disabled.
 */

#define GL_VENDOR 0x1F00u
#define GL_RENDERER 0x1F01u
#define GL_VERSION 0x1F02u
#define GL_EXTENSIONS 0x1F03u
#define GL_RED_BITS 0x0D52u
#define GL_GREEN_BITS 0x0D53u
#define GL_BLUE_BITS 0x0D54u
#define GL_ALPHA_BITS 0x0D55u
#define GL_DEPTH_BITS 0x0D56u
#define GL_STENCIL_BITS 0x0D57u
#define GL_FRAMEBUFFER_COMPLETE 0x8CD5u
#define GL_COMPILE_STATUS 0x8B81u
#define GL_LINK_STATUS 0x8B82u
#define GL_INFO_LOG_LENGTH 0x8B84u
#define GL_ACTIVE_ATTRIBUTES 0x8B89u
#define GL_ACTIVE_UNIFORMS 0x8B86u
#define GL_FLOAT 0x1406u
#define GL_NO_ERROR 0u

static uint32_t s_next_handle = 1u;
static const char s_vendor[] = "SuAPI Headless";
static const char s_renderer[] = "SuAPI Null GLES2";
static const char s_version[] = "OpenGL ES 2.0 SuAPI Null";
static const char s_extensions[] = "";

static uint32_t next_handle(void)
{
    return s_next_handle++;
}

__declspec(dllexport) const char *glGetString(uint32_t name)
{
    switch (name)
    {
    case GL_VENDOR: return s_vendor;
    case GL_RENDERER: return s_renderer;
    case GL_VERSION: return s_version;
    case GL_EXTENSIONS: return s_extensions;
    default: return "";
    }
}

__declspec(dllexport) uint32_t glGetError(void)
{
    return GL_NO_ERROR;
}

__declspec(dllexport) void glGetIntegerv(uint32_t name, int *value)
{
    if (!value) return;
    switch (name)
    {
    case GL_RED_BITS:
    case GL_GREEN_BITS:
    case GL_BLUE_BITS: *value = 8; break;
    case GL_ALPHA_BITS: *value = 0; break;
    case GL_DEPTH_BITS: *value = 16; break;
    case GL_STENCIL_BITS: *value = 8; break;
    default: *value = 0; break;
    }
}

__declspec(dllexport) void glGenBuffers(int count, uint32_t *buffers)
{
    if (!buffers) return;
    for (int i = 0; i < count; ++i) buffers[i] = next_handle();
}

__declspec(dllexport) void glGenTextures(int count, uint32_t *textures)
{
    if (!textures) return;
    for (int i = 0; i < count; ++i) textures[i] = next_handle();
}

__declspec(dllexport) void glGenFramebuffers(int count, uint32_t *buffers)
{
    if (!buffers) return;
    for (int i = 0; i < count; ++i) buffers[i] = next_handle();
}

__declspec(dllexport) void glGenRenderbuffers(int count, uint32_t *buffers)
{
    if (!buffers) return;
    for (int i = 0; i < count; ++i) buffers[i] = next_handle();
}

__declspec(dllexport) int glCreateShader(uint32_t type) { (void)type; return (int)next_handle(); }
__declspec(dllexport) int glCreateProgram(void) { return (int)next_handle(); }
__declspec(dllexport) void glGetShaderiv(uint32_t shader, uint32_t name, int *value)
{
    (void)shader;
    if (value) *value = (name == GL_COMPILE_STATUS) ? 1 : 0;
}
__declspec(dllexport) void glGetProgramiv(uint32_t program, uint32_t name, int *value)
{
    (void)program;
    if (!value) return;
    if (name == GL_LINK_STATUS) *value = 1;
    else if (name == GL_ACTIVE_UNIFORMS) *value = 1;
    else *value = 0;
}
__declspec(dllexport) void glGetShaderInfoLog(uint32_t shader, int max_length, int *length, char *log)
{
    (void)shader; (void)max_length;
    if (length) *length = 0;
    if (log) log[0] = '\0';
}
__declspec(dllexport) void glGetProgramInfoLog(uint32_t program, int max_length, int *length, char *log)
{
    (void)program; (void)max_length;
    if (length) *length = 0;
    if (log) log[0] = '\0';
}
__declspec(dllexport) void glGetActiveUniform(uint32_t program, uint32_t index,
    int buffer_size, int *length, int *size, uint32_t *type, char *name)
{
    (void)program;
    if (length) *length = 8;
    if (size) *size = 1;
    if (type) *type = GL_FLOAT;
    if (name && buffer_size > 0)
    {
        strncpy(name, "u_glymul", (size_t)buffer_size);
        name[buffer_size - 1] = '\0';
    }
    (void)index;
}
__declspec(dllexport) void glGetActiveAttrib(uint32_t program, uint32_t index,
    int buffer_size, int *length, int *size, uint32_t *type, char *name)
{
    (void)program; (void)index; (void)buffer_size;
    if (length) *length = 0;
    if (size) *size = 0;
    if (type) *type = 0;
    if (name && buffer_size > 0) name[0] = '\0';
}
__declspec(dllexport) int glGetUniformLocation(uint32_t program, const char *name)
{
    (void)program;
    return (name && strcmp(name, "u_glymul") == 0) ? 0 : -1;
}
__declspec(dllexport) int glGetAttribLocation(uint32_t program, const char *name)
{
    (void)program; (void)name; return -1;
}
__declspec(dllexport) uint32_t glCheckFramebufferStatus(uint32_t target)
{
    (void)target; return GL_FRAMEBUFFER_COMPLETE;
}
__declspec(dllexport) unsigned char glIsEnabled(uint32_t cap) { (void)cap; return 0; }

__declspec(dllexport) void glGetRenderbufferParameteriv(uint32_t target, uint32_t name, int *value)
{
    (void)target;
    if (!value) return;
    *value = 0;
}

/* State-changing, upload, draw and deletion entry points intentionally no-op. */
#define VOID_STUB(name) __declspec(dllexport) void name(void) {}
VOID_STUB(glActiveTexture)
VOID_STUB(glAttachShader)
VOID_STUB(glBindBuffer)
VOID_STUB(glBindFramebuffer)
VOID_STUB(glBindRenderbuffer)
VOID_STUB(glBindTexture)
VOID_STUB(glBlendColor)
VOID_STUB(glBlendEquation)
VOID_STUB(glBlendEquationSeparate)
VOID_STUB(glBlendFunc)
VOID_STUB(glBlendFuncSeparate)
VOID_STUB(glBufferData)
VOID_STUB(glBufferSubData)
VOID_STUB(glClear)
VOID_STUB(glClearColor)
VOID_STUB(glClearDepthf)
VOID_STUB(glClearStencil)
VOID_STUB(glColorMask)
VOID_STUB(glCompileShader)
VOID_STUB(glCullFace)
VOID_STUB(glDeleteBuffers)
VOID_STUB(glDeleteFramebuffers)
VOID_STUB(glDeleteProgram)
VOID_STUB(glDeleteRenderbuffers)
VOID_STUB(glDeleteShader)
VOID_STUB(glDeleteTextures)
VOID_STUB(glDepthFunc)
VOID_STUB(glDepthMask)
VOID_STUB(glDepthRangef)
VOID_STUB(glDetachShader)
VOID_STUB(glDisable)
VOID_STUB(glDisableVertexAttribArray)
VOID_STUB(glDrawArrays)
VOID_STUB(glDrawElements)
VOID_STUB(glEnable)
VOID_STUB(glEnableVertexAttribArray)
VOID_STUB(glFramebufferRenderbuffer)
VOID_STUB(glFramebufferTexture2D)
VOID_STUB(glFrontFace)
VOID_STUB(glGenerateMipmap)
VOID_STUB(glLinkProgram)
VOID_STUB(glPixelStorei)
VOID_STUB(glPolygonOffset)
VOID_STUB(glReadPixels)
VOID_STUB(glRenderbufferStorage)
VOID_STUB(glScissor)
VOID_STUB(glShaderSource)
VOID_STUB(glTexImage2D)
VOID_STUB(glTexParameterf)
VOID_STUB(glTexParameterfv)
VOID_STUB(glTexParameteri)
VOID_STUB(glTexParameteriv)
VOID_STUB(glTexSubImage2D)
VOID_STUB(glUniform1f)
VOID_STUB(glUniform1i)
VOID_STUB(glUniform2f)
VOID_STUB(glUniform2i)
VOID_STUB(glUniform3f)
VOID_STUB(glUniform3i)
VOID_STUB(glUniform4f)
VOID_STUB(glUniform4i)
VOID_STUB(glUniform1fv)
VOID_STUB(glUniform2fv)
VOID_STUB(glUniform3fv)
VOID_STUB(glUniform4fv)
VOID_STUB(glUniformMatrix4fv)
VOID_STUB(glUseProgram)
VOID_STUB(glVertexAttribPointer)
VOID_STUB(glViewport)
