#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Source: OpenTK/OpenTK.Graphics.ES20/GL.cs:GL.Core
 * Source: Engine/Engine/Graphics/Shader.cs:Shader.CompileShaders
 * This is a no-GPU GLES2 backend for HeadlessRenderingMod. It stores shader
 * metadata only so Engine can construct ShaderParameter objects; no pixels or
 * draw results are allocated.
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
#define GL_FLOAT_VEC2 0x8B50u
#define GL_FLOAT_VEC3 0x8B51u
#define GL_FLOAT_VEC4 0x8B52u
#define GL_FLOAT_MAT4 0x8B5Cu
#define GL_SAMPLER_2D 0x8B5Eu
#define GL_NO_ERROR 0u

#define MAX_SHADERS 128
#define MAX_PROGRAMS 64
#define MAX_SYMBOLS 128
#define MAX_NAME 128
#define MAX_SOURCE 32768
#define MAX_DEFINES 64

typedef struct
{
    char name[MAX_NAME];
    uint32_t type;
    int size;
    int location;
} Symbol;

typedef struct
{
    uint32_t handle;
    char *source;
} ShaderRecord;

typedef struct
{
    uint32_t handle;
    uint32_t vertex_shader;
    uint32_t fragment_shader;
    Symbol uniforms[MAX_SYMBOLS];
    int uniform_count;
    Symbol attributes[MAX_SYMBOLS];
    int attribute_count;
    int linked;
} ProgramRecord;

typedef struct
{
    char name[64];
    char value[64];
} Define;

static uint32_t s_next_handle = 1u;
static ShaderRecord s_shaders[MAX_SHADERS];
static ProgramRecord s_programs[MAX_PROGRAMS];
static const char s_vendor[] = "SuAPI Headless";
static const char s_renderer[] = "SuAPI Null GLES2";
static const char s_version[] = "OpenGL ES 2.0 SuAPI Null";
static const char s_extensions[] = "";

static uint32_t next_handle(void)
{
    return s_next_handle++;
}

static ShaderRecord *find_shader(uint32_t handle)
{
    int i;
    for (i = 0; i < MAX_SHADERS; ++i)
        if (s_shaders[i].handle == handle)
            return &s_shaders[i];
    return NULL;
}

static ProgramRecord *find_program(uint32_t handle)
{
    int i;
    for (i = 0; i < MAX_PROGRAMS; ++i)
        if (s_programs[i].handle == handle)
            return &s_programs[i];
    return NULL;
}

static ShaderRecord *reserve_shader(uint32_t handle)
{
    int i;
    ShaderRecord *record = find_shader(handle);
    if (record)
        return record;
    for (i = 0; i < MAX_SHADERS; ++i)
    {
        if (s_shaders[i].handle == 0)
        {
            s_shaders[i].handle = handle;
            return &s_shaders[i];
        }
    }
    return NULL;
}

static ProgramRecord *reserve_program(uint32_t handle)
{
    int i;
    ProgramRecord *record = find_program(handle);
    if (record)
        return record;
    for (i = 0; i < MAX_PROGRAMS; ++i)
    {
        if (s_programs[i].handle == 0)
        {
            s_programs[i].handle = handle;
            return &s_programs[i];
        }
    }
    return NULL;
}

static void trim(char *text)
{
    char *start = text;
    size_t length;
    while (*start == ' ' || *start == '\t' || *start == '\r' || *start == '\n')
        ++start;
    if (start != text)
        memmove(text, start, strlen(start) + 1);
    length = strlen(text);
    while (length > 0 &&
        (text[length - 1] == ' ' || text[length - 1] == '\t' ||
         text[length - 1] == '\r' || text[length - 1] == '\n'))
        text[--length] = '\0';
}

static int define_index(Define *defines, int count, const char *name)
{
    int i;
    for (i = 0; i < count; ++i)
        if (strcmp(defines[i].name, name) == 0)
            return i;
    return -1;
}

static int has_define(Define *defines, int count, const char *name)
{
    return define_index(defines, count, name) >= 0;
}

static int define_value(Define *defines, int count, const char *name, int fallback)
{
    int index = define_index(defines, count, name);
    if (index < 0 || defines[index].value[0] == '\0')
        return fallback;
    return atoi(defines[index].value);
}

static uint32_t type_from_name(const char *type)
{
    if (strcmp(type, "float") == 0) return GL_FLOAT;
    if (strcmp(type, "vec2") == 0) return GL_FLOAT_VEC2;
    if (strcmp(type, "vec3") == 0) return GL_FLOAT_VEC3;
    if (strcmp(type, "vec4") == 0) return GL_FLOAT_VEC4;
    if (strcmp(type, "mat4") == 0) return GL_FLOAT_MAT4;
    if (strcmp(type, "sampler2D") == 0) return GL_SAMPLER_2D;
    return GL_FLOAT;
}

static int is_precision_qualifier(const char *token)
{
    return token && (strcmp(token, "lowp") == 0 ||
        strcmp(token, "mediump") == 0 || strcmp(token, "highp") == 0);
}

static int symbol_exists(Symbol *symbols, int count, const char *name)
{
    int i;
    size_t length = strlen(name);
    for (i = 0; i < count; ++i)
        if (strcmp(symbols[i].name, name) == 0 ||
            (strncmp(symbols[i].name, name, length) == 0 &&
             symbols[i].name[length] == '['))
            return 1;
    return 0;
}

static void add_symbol(Symbol *symbols, int *count, const char *type,
    const char *raw_name, Define *defines, int define_count)
{
    char name[MAX_NAME];
    char *bracket;
    int size = 1;
    if (*count >= MAX_SYMBOLS || !raw_name)
        return;
    strncpy(name, raw_name, MAX_NAME - 1);
    name[MAX_NAME - 1] = '\0';
    trim(name);
    bracket = strchr(name, '[');
    if (bracket)
    {
        char expression[64];
        size_t length = strlen(bracket + 1);
        if (length > 0 && bracket[1 + length - 1] == ']')
            --length;
        if (length >= sizeof(expression))
            length = sizeof(expression) - 1;
        memcpy(expression, bracket + 1, length);
        expression[length] = '\0';
        size = atoi(expression);
        if (size <= 0)
            size = define_value(defines, define_count, expression, 1);
        *bracket = '\0';
    }
    if (symbol_exists(symbols, *count, name))
        return;
    if (size > 1)
        snprintf(symbols[*count].name, MAX_NAME, "%s[0]", name);
    else
        snprintf(symbols[*count].name, MAX_NAME, "%s", name);
    symbols[*count].type = type_from_name(type);
    symbols[*count].size = size;
    symbols[*count].location = *count;
    ++*count;
}

static int parse_shader_source(const char *source, Symbol *uniforms,
    int *uniform_count, Symbol *attributes, int *attribute_count)
{
    Define defines[MAX_DEFINES];
    int define_count = 0;
    int active_stack[32];
    int parent_stack[32];
    int depth = 0;
    char *copy;
    char *line;
    char *next;
    if (!source)
        return 0;
    copy = (char *)malloc(strlen(source) + 1);
    if (!copy)
        return 0;
    strcpy(copy, source);

    line = copy;
    while (line && *line)
    {
        char text[512];
        char directive[64];
        char name[128];
        char value[128];
        next = strchr(line, '\n');
        if (next)
        {
            *next = '\0';
            ++next;
        }
        strncpy(text, line, sizeof(text) - 1);
        text[sizeof(text) - 1] = '\0';
        trim(text);
        if (sscanf(text, "#define %63s %127s", name, value) >= 1)
        {
            char *space = strchr(name, '(');
            if (space) *space = '\0';
            if (define_count < MAX_DEFINES && !has_define(defines, define_count, name))
            {
                snprintf(defines[define_count].name, sizeof(defines[0].name), "%.63s", name);
                if (sscanf(text, "#define %63s %127s", name, value) == 2)
                    snprintf(defines[define_count].value, sizeof(defines[0].value), "%.63s", value);
                else
                    defines[define_count].value[0] = '\0';
                defines[define_count].value[sizeof(defines[0].value) - 1] = '\0';
                ++define_count;
            }
        }
        if (sscanf(text, "#ifdef %63s", directive) == 1)
        {
            if (depth < 31)
            {
                parent_stack[depth] = depth == 0 ? 1 : active_stack[depth - 1];
                active_stack[depth] = parent_stack[depth] && has_define(defines, define_count, directive);
                ++depth;
            }
        }
        else if (sscanf(text, "#ifndef %63s", directive) == 1)
        {
            if (depth < 31)
            {
                parent_stack[depth] = depth == 0 ? 1 : active_stack[depth - 1];
                active_stack[depth] = parent_stack[depth] && !has_define(defines, define_count, directive);
                ++depth;
            }
        }
        else if (strncmp(text, "#else", 5) == 0 && depth > 0)
            active_stack[depth - 1] = parent_stack[depth - 1] && !active_stack[depth - 1];
        else if (strncmp(text, "#endif", 6) == 0 && depth > 0)
            --depth;
        else if ((depth == 0 || active_stack[depth - 1]) &&
            (sscanf(text, "uniform %63s %127[^;];", directive, name) == 2 ||
             sscanf(text, "attribute %63s %127[^;];", directive, name) == 2))
        {
            char actual_type[64];
            char actual_name[128];
            if (is_precision_qualifier(directive) &&
                sscanf(name, "%63s %127[^;]", actual_type, actual_name) == 2)
            {
                snprintf(directive, sizeof(directive), "%s", actual_type);
                snprintf(name, sizeof(name), "%s", actual_name);
            }
            if (strncmp(text, "uniform ", 8) == 0)
                add_symbol(uniforms, uniform_count, directive, name, defines, define_count);
            else
                add_symbol(attributes, attribute_count, directive, name, defines, define_count);
        }
        line = next;
    }
    free(copy);
    return 1;
}

static void link_program(ProgramRecord *program)
{
    ShaderRecord *vertex;
    ShaderRecord *fragment;
    if (!program) return;
    program->uniform_count = 0;
    program->attribute_count = 0;
    vertex = find_shader(program->vertex_shader);
    fragment = find_shader(program->fragment_shader);
    if (vertex)
        parse_shader_source(vertex->source, program->uniforms, &program->uniform_count,
            program->attributes, &program->attribute_count);
    if (fragment)
        parse_shader_source(fragment->source, program->uniforms, &program->uniform_count,
            program->attributes, &program->attribute_count);
    program->linked = 1;
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

__declspec(dllexport) uint32_t glGetError(void) { return GL_NO_ERROR; }

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
    int i;
    if (!buffers) return;
    for (i = 0; i < count; ++i) buffers[i] = next_handle();
}

__declspec(dllexport) void glGenTextures(int count, uint32_t *textures)
{
    int i;
    if (!textures) return;
    for (i = 0; i < count; ++i) textures[i] = next_handle();
}

__declspec(dllexport) void glGenFramebuffers(int count, uint32_t *buffers)
{
    int i;
    if (!buffers) return;
    for (i = 0; i < count; ++i) buffers[i] = next_handle();
}

__declspec(dllexport) void glGenRenderbuffers(int count, uint32_t *buffers)
{
    int i;
    if (!buffers) return;
    for (i = 0; i < count; ++i) buffers[i] = next_handle();
}

__declspec(dllexport) int glCreateShader(uint32_t type)
{
    uint32_t handle = next_handle();
    (void)type;
    reserve_shader(handle);
    return (int)handle;
}

__declspec(dllexport) int glCreateProgram(void)
{
    uint32_t handle = next_handle();
    reserve_program(handle);
    return (int)handle;
}

__declspec(dllexport) void glShaderSource(uint32_t shader, int count,
    const char **strings, const int *lengths)
{
    ShaderRecord *record = reserve_shader(shader);
    size_t total = 0;
    int i;
    if (!record || !strings || count <= 0) return;
    for (i = 0; i < count; ++i)
    {
        if (!strings[i]) continue;
        if ((unsigned char)strings[i][1] == 0 && strings[i][2] != 0)
            total += lengths && lengths[i] >= 0
                ? (size_t)lengths[i]
                : strlen(strings[i]) / 2;
        else
            total += lengths && lengths[i] >= 0 ? (size_t)lengths[i] : strlen(strings[i]);
    }
    if (total > MAX_SOURCE - 1) total = MAX_SOURCE - 1;
    free(record->source);
    record->source = (char *)malloc(total + 1);
    if (!record->source) return;
    total = 0;
    for (i = 0; i < count && total < MAX_SOURCE - 1; ++i)
    {
        size_t length;
        if (!strings[i]) continue;
        if ((unsigned char)strings[i][1] == 0 && strings[i][2] != 0)
        {
            size_t characters = lengths && lengths[i] >= 0
                ? (size_t)lengths[i]
                : strlen(strings[i]) / 2;
            if (characters > MAX_SOURCE - 1 - total)
                characters = MAX_SOURCE - 1 - total;
            for (length = 0; length < characters; ++length)
                record->source[total + length] = strings[i][length * 2];
            total += characters;
            continue;
        }
        length = lengths && lengths[i] >= 0 ? (size_t)lengths[i] : strlen(strings[i]);
        if (length > MAX_SOURCE - 1 - total) length = MAX_SOURCE - 1 - total;
        memcpy(record->source + total, strings[i], length);
        total += length;
    }
    record->source[total] = '\0';
}

__declspec(dllexport) void glGetShaderiv(uint32_t shader, uint32_t name, int *value)
{
    (void)shader;
    if (value) *value = (name == GL_COMPILE_STATUS) ? 1 : 0;
}

__declspec(dllexport) void glGetProgramiv(uint32_t program, uint32_t name, int *value)
{
    ProgramRecord *record = find_program(program);
    if (!value) return;
    if (name == GL_LINK_STATUS) *value = record && record->linked ? 1 : 0;
    else if (name == GL_ACTIVE_UNIFORMS) *value = record ? record->uniform_count : 0;
    else if (name == GL_ACTIVE_ATTRIBUTES) *value = record ? record->attribute_count : 0;
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

static void copy_symbol_name(const char *name, int buffer_size, int *length, char *output)
{
    int actual;
    if (!output || buffer_size <= 0) return;
    actual = (int)strlen(name);
    if (actual >= buffer_size) actual = buffer_size - 1;
    memcpy(output, name, (size_t)actual);
    output[actual] = '\0';
    if (length) *length = actual;
}

__declspec(dllexport) void glGetActiveUniform(uint32_t program, uint32_t index,
    int buffer_size, int *length, int *size, uint32_t *type, char *name)
{
    ProgramRecord *record = find_program(program);
    if (!record || index >= (uint32_t)record->uniform_count)
    {
        if (length) *length = 0;
        if (size) *size = 0;
        if (type) *type = 0;
        if (name && buffer_size > 0) name[0] = '\0';
        return;
    }
    if (size) *size = record->uniforms[index].size;
    if (type) *type = record->uniforms[index].type;
    copy_symbol_name(record->uniforms[index].name, buffer_size, length, name);
}

__declspec(dllexport) void glGetActiveAttrib(uint32_t program, uint32_t index,
    int buffer_size, int *length, int *size, uint32_t *type, char *name)
{
    ProgramRecord *record = find_program(program);
    if (!record || index >= (uint32_t)record->attribute_count)
    {
        if (length) *length = 0;
        if (size) *size = 0;
        if (type) *type = 0;
        if (name && buffer_size > 0) name[0] = '\0';
        return;
    }
    if (size) *size = record->attributes[index].size;
    if (type) *type = record->attributes[index].type;
    copy_symbol_name(record->attributes[index].name, buffer_size, length, name);
}

static int find_symbol_location(Symbol *symbols, int count, const char *name)
{
    int i;
    if (!name) return -1;
    for (i = 0; i < count; ++i)
    {
        if (strcmp(symbols[i].name, name) == 0) return symbols[i].location;
        if (symbols[i].size > 1 && strncmp(symbols[i].name, name, strlen(name)) == 0)
            return symbols[i].location;
    }
    return -1;
}

__declspec(dllexport) int glGetUniformLocation(uint32_t program, const char *name)
{
    ProgramRecord *record = find_program(program);
    return record ? find_symbol_location(record->uniforms, record->uniform_count, name) : -1;
}

__declspec(dllexport) int glGetAttribLocation(uint32_t program, const char *name)
{
    ProgramRecord *record = find_program(program);
    return record ? find_symbol_location(record->attributes, record->attribute_count, name) : -1;
}

__declspec(dllexport) void glAttachShader(uint32_t program, uint32_t shader)
{
    ProgramRecord *record = reserve_program(program);
    if (!record) return;
    if (record->vertex_shader == 0) record->vertex_shader = shader;
    else record->fragment_shader = shader;
}

__declspec(dllexport) void glLinkProgram(uint32_t program)
{
    link_program(find_program(program));
}

__declspec(dllexport) uint32_t glCheckFramebufferStatus(uint32_t target)
{
    (void)target; return GL_FRAMEBUFFER_COMPLETE;
}

__declspec(dllexport) unsigned char glIsEnabled(uint32_t cap) { (void)cap; return 0; }

__declspec(dllexport) void glGetRenderbufferParameteriv(uint32_t target, uint32_t name, int *value)
{
    (void)target; (void)name;
    if (value) *value = 0;
}

__declspec(dllexport) void glDeleteProgram(uint32_t program)
{
    ProgramRecord *record = find_program(program);
    if (record)
        memset(record, 0, sizeof(*record));
}

__declspec(dllexport) void glDeleteShader(uint32_t shader)
{
    ShaderRecord *record = find_shader(shader);
    if (record)
    {
        free(record->source);
        memset(record, 0, sizeof(*record));
    }
}

/* State-changing, upload, draw and deletion entry points intentionally no-op. */
#define VOID_STUB(name) __declspec(dllexport) void name(void) {}
VOID_STUB(glActiveTexture)
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
VOID_STUB(glDeleteRenderbuffers)
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
VOID_STUB(glPixelStorei)
VOID_STUB(glPolygonOffset)
VOID_STUB(glReadPixels)
VOID_STUB(glRenderbufferStorage)
VOID_STUB(glScissor)
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
