using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace PakExplorer;

/// <summary>
/// 读取 Survivalcraft 的 Content.pak 文件 (PK2 格式)
/// 格式: PK2头(4B) + XOR加密TOC区 + XOR加密内容区
/// Source: Engine/Content/ContentCache.cs AddPackage(), Engine/Content/PadStream.cs
/// </summary>
public class PakFile : IDisposable
{
    /// <summary>
    /// PAK文件中的单个条目
    /// </summary>
    public class PakEntry
    {
        public string Name;       // 如 "Audio/Click" (无扩展名)
        public string TypeName;   // 如 "Engine.Audio.SoundBuffer"
        public long Position;     // 内容在文件中的绝对偏移
        public long Size;         // 内容字节数
    }

    /// <summary>
    /// 虚拟文件夹节点
    /// </summary>
    public class PakFolder
    {
        public string Name;
        public string FullPath;   // 如 "Audio/Creatures"
        public Dictionary<string, PakFolder> SubFolders = new();
        public List<PakEntry> Files = new();
    }

    // PK2 文件头
    private static readonly byte[] HeaderBytes = { 0x50, 0x4B, 0x32, 0x00 }; // "PK2\0"

    // TOC加密密钥: 由 Game.Random(seed=9217) 生成229字符, UTF8编码
    private byte[] _tocPad;
    // 内容区加密密钥: new byte[1] { 63 }
    private static readonly byte[] ContentPad = new byte[1] { 63 };

    private FileStream _stream;
    private BinaryReader _reader;

    /// <summary>PAK中所有条目</summary>
    public List<PakEntry> Entries { get; } = new();

    /// <summary>虚拟根文件夹</summary>
    public PakFolder Root { get; } = new PakFolder { Name = "", FullPath = "" };

    /// <summary>TOC区结束位置 = 内容区起始偏移</summary>
    public long ContentDataOffset { get; private set; }

    /// <summary>PAK文件路径</summary>
    public string FilePath { get; }

    /// <summary>是否有未保存的修改</summary>
    public bool HasUnsavedChanges { get; private set; }

    /// <summary>修改记录: 条目名 -> 新内容</summary>
    private Dictionary<string, byte[]> _modifiedEntries = new();

    public PakFile(string path)
    {
        FilePath = path;
        _tocPad = Encoding.UTF8.GetBytes(GeneratePad());
    }

    /// <summary>
    /// 打开并解析PAK文件
    /// </summary>
    public void Open()
    {
        _stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

        // 1. 读取头 (无XOR)
        byte[] header = _reader.ReadBytes(4);
        if (header[0] != HeaderBytes[0] || header[1] != HeaderBytes[1] ||
            header[2] != HeaderBytes[2] || header[3] != HeaderBytes[3])
        {
            throw new InvalidDataException("Invalid PK2 header.");
        }

        // 2. 读取TOC (XOR with tocPad)
        // TOC格式: Int64(contentOffset) + Int32(entryCount) + [entry...] 
        // entry: ReadString(name) + ReadString(typeName) + Int64(position) + Int64(size)

        long contentDataOffset = ReadInt64Xor();
        int entryCount = ReadInt32Xor();

        ContentDataOffset = contentDataOffset;

        for (int i = 0; i < entryCount; i++)
        {
            string name = ReadStringXor();
            string typeName = ReadStringXor();
            long position = ReadInt64Xor();
            long size = ReadInt64Xor();

            // position在文件中是相对于contentDataOffset的偏移
            // Source: ContentCache.cs line 122: position = binaryReader.ReadInt64() + num
            // 实际读取时: contentDescription.Position = position + contentDataOffset
            // 所以需要加上contentDataOffset得到绝对位置
            var entry = new PakEntry
            {
                Name = name,
                TypeName = typeName,
                Position = position + contentDataOffset, // 相对偏移 + contentDataOffset = 绝对位置
                Size = size
            };
            Entries.Add(entry);
        }

        // 3. 构建虚拟文件夹树
        BuildFolderTree();
    }

    /// <summary>
    /// 读取指定条目的原始内容（XOR解密后）
    /// 如果条目已被修改，返回修改后的内容
    /// 注意：返回的是引擎序列化格式，不是标准文件格式
    /// </summary>
    public byte[] ReadEntryContent(PakEntry entry)
    {
        // 优先返回修改后的内容
        if (_modifiedEntries.TryGetValue(entry.Name, out byte[] modified))
        {
            return modified;
        }

        _stream.Position = entry.Position;
        byte[] data = new byte[entry.Size];
        _stream.Read(data, 0, data.Length);

        // XOR解密内容区
        for (int i = 0; i < data.Length; i++)
        {
            long absPos = entry.Position + i;
            data[i] = (byte)(data[i] ^ ContentPad[absPos % ContentPad.Length]);
        }

        return data;
    }

    /// <summary>
    /// 将条目提取为标准文件格式
    /// 根据TypeName反序列化引擎内部格式，转换为标准文件格式
    /// </summary>
    public byte[] ExtractAsStandardFormat(PakEntry entry)
    {
        byte[] raw = ReadEntryContent(entry);
        var ms = new System.IO.MemoryStream(raw);
        var br = new System.IO.BinaryReader(ms);

        try
        {
            switch (entry.TypeName)
            {
                case "System.Xml.Linq.XElement":
                {
                    // BinaryReader.ReadString() -> XML字符串
                    string xml = br.ReadString();
                    return System.Text.Encoding.UTF8.GetBytes(xml);
                }

                case "System.String":
                {
                    // BinaryReader.ReadString() -> 纯文本
                    string text = br.ReadString();
                    return System.Text.Encoding.UTF8.GetBytes(text);
                }

                case "Engine.Graphics.Texture2D":
                {
                    // Texture2D格式: bool keepImageInTag + byte mipLevelsCount + Image[]
                    // Image格式: byte imageFileFormat + data
                    // ImageFileFormat枚举: 0=RawRgba, 1=Bmp, 2=Png, 3=Jpg
                    bool keepImage = br.ReadBoolean();
                    byte mipLevels = br.ReadByte();
                    byte format = br.ReadByte(); // imageFileFormat

                    switch (format)
                    {
                        case 0: // RawRgba
                        {
                            // RawRgba格式: "RAW"(3B) + Format(1B:0=RGBA8,1=RGB8,2=LA8,3=L8) + Width(UInt16) + Height(UInt16) + pixels
                            byte[] rawHeader = br.ReadBytes(3);
                            if (rawHeader[0] != 0x52 || rawHeader[1] != 0x41 || rawHeader[2] != 0x57)
                            {
                                return raw; // 不是RAW格式，返回原始数据
                            }
                            int pixelFormat = br.ReadByte();
                            int width = br.ReadUInt16();
                            int height = br.ReadUInt16();

                            // 用ImageSharp转PNG
                            var image = new Image<Rgba32>(width, height);
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    Rgba32 pixel = default;
                                    switch (pixelFormat)
                                    {
                                        case 0: // RGBA8
                                            pixel = new Rgba32(br.ReadByte(), br.ReadByte(), br.ReadByte(), br.ReadByte());
                                            break;
                                        case 1: // RGB8
                                            pixel = new Rgba32(br.ReadByte(), br.ReadByte(), br.ReadByte(), 255);
                                            break;
                                        case 2: // LA8
                                        {
                                            byte l = br.ReadByte();
                                            byte a = br.ReadByte();
                                            pixel = new Rgba32(l, l, l, a);
                                            break;
                                        }
                                        case 3: // L8
                                        {
                                            byte l = br.ReadByte();
                                            pixel = new Rgba32(l, l, l, 255);
                                            break;
                                        }
                                    }
                                    image[x, y] = pixel;
                                }
                            }
                            using var pngMs = new MemoryStream();
                            image.SaveAsPng(pngMs);
                            return pngMs.ToArray();
                        }
                        case 1: // Bmp
                        {
                            long remaining = raw.Length - ms.Position;
                            return br.ReadBytes((int)remaining);
                        }
                        case 2: // Png
                        {
                            long remaining = raw.Length - ms.Position;
                            return br.ReadBytes((int)remaining);
                        }
                        case 3: // Jpg
                        {
                            int dataLen = br.ReadInt32();
                            return br.ReadBytes(dataLen);
                        }
                        default:
                        {
                            return raw;
                        }
                    }
                }

                case "Engine.Audio.SoundBuffer":
                {
                    // SoundBuffer格式: bool keepData + bool isOggVorbis + int channels + int frequency + int bytesCount + data
                    bool keepData = br.ReadBoolean();
                    bool isOgg = br.ReadBoolean();
                    if (isOgg)
                    {
                        // OGG数据直接跟在头部后面
                        long remaining = raw.Length - ms.Position;
                        int channels = br.ReadInt32();
                        int frequency = br.ReadInt32();
                        int bytesCount = br.ReadInt32();
                        return br.ReadBytes(bytesCount);
                    }
                    else
                    {
                        // 原始PCM数据，需要添加WAV头
                        int channels = br.ReadInt32();
                        int frequency = br.ReadInt32();
                        int bytesCount = br.ReadInt32();
                        byte[] pcmData = br.ReadBytes(bytesCount);
                        return BuildWavHeader(channels, frequency, 16, pcmData);
                    }
                }

                case "Engine.Media.StreamingSource":
                {
                    // 直接是标准音频文件格式（OGG等）
                    return raw;
                }

                case "Engine.Graphics.Shader":
                {
                    // Shader格式: string vertexCode + string pixelCode + int macroCount + macros[]
                    string vertexCode = br.ReadString();
                    string pixelCode = br.ReadString();
                    int macroCount = br.ReadInt32();
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("// Vertex Shader");
                    sb.AppendLine(vertexCode);
                    sb.AppendLine();
                    sb.AppendLine("// Pixel Shader");
                    sb.AppendLine(pixelCode);
                    sb.AppendLine();
                    if (macroCount > 0)
                    {
                        sb.AppendLine("// Macros");
                        for (int i = 0; i < macroCount; i++)
                        {
                            string name = br.ReadString();
                            string value = br.ReadString();
                            sb.AppendLine($"// #define {name} {value}");
                        }
                    }
                    return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                }

                default:
                {
                    // 其他类型（Model, BitmapFont等）返回原始数据
                    return raw;
                }
            }
        }
        catch
        {
            // 反序列化失败，返回原始数据
            return raw;
        }
    }

    /// <summary>
    /// 为原始PCM数据构建WAV文件头
    /// </summary>
    private static byte[] BuildWavHeader(int channels, int sampleRate, int bitsPerSample, byte[] pcmData)
    {
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataSize = pcmData.Length;

        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms);

        // RIFF header
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Write(pcmData);

        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 替换条目内容（内存中暂存，需save写入磁盘）
    /// </summary>
    public void ReplaceEntry(PakEntry entry, byte[] newData)
    {
        _modifiedEntries[entry.Name] = newData;
        HasUnsavedChanges = true;
    }

    /// <summary>
    /// 从外部文件导入替换条目
    /// </summary>
    public void ImportFile(PakEntry entry, string externalFilePath)
    {
        byte[] data = File.ReadAllBytes(externalFilePath);
        ReplaceEntry(entry, data);
    }

    /// <summary>
    /// 获取条目当前有效大小（考虑修改）
    /// </summary>
    public long GetEntrySize(PakEntry entry)
    {
        if (_modifiedEntries.TryGetValue(entry.Name, out byte[] modified))
        {
            return modified.Length;
        }
        return entry.Size;
    }

    /// <summary>
    /// 检查条目是否已被修改
    /// </summary>
    public bool IsEntryModified(PakEntry entry)
    {
        return _modifiedEntries.ContainsKey(entry.Name);
    }

    /// <summary>
    /// 撤销条目修改
    /// </summary>
    public bool RevertEntry(PakEntry entry)
    {
        bool removed = _modifiedEntries.Remove(entry.Name);
        if (removed)
        {
            HasUnsavedChanges = _modifiedEntries.Count > 0;
        }
        return removed;
    }

    /// <summary>
    /// 撤销所有修改
    /// </summary>
    public void RevertAll()
    {
        _modifiedEntries.Clear();
        HasUnsavedChanges = false;
    }

    /// <summary>
    /// 保存修改到新PAK文件
    /// 格式: PK2头(4B) + XOR加密TOC区 + XOR加密内容区
    /// </summary>
    public void Save(string outputPath)
    {
        // 1. 计算新的内容区布局
        //    所有条目按原顺序排列，内容紧跟TOC之后
        //    TOC格式: Int64(contentOffset) + Int32(entryCount) + [entry...]
        //    entry: WriteString(name) + WriteString(typeName) + Int64(position) + Int64(size)

        // 先计算TOC大小以确定contentOffset
        long tocSize = CalculateTocSize();
        long contentOffset = 4 + tocSize; // 4字节头 + TOC

        // 2. 计算每个条目的新位置
        var entryLayouts = new List<(PakEntry Entry, long NewPosition, long NewSize)>();
        long currentPos = contentOffset;

        foreach (var entry in Entries)
        {
            long size = GetEntrySize(entry);
            entryLayouts.Add((entry, currentPos, size));
            currentPos += size;
        }

        // 3. 写入新文件
        using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(outStream, Encoding.UTF8, leaveOpen: true);

        // 3a. 写入PK2头（无XOR）
        writer.Write(HeaderBytes);

        // 3b. 写入TOC（XOR加密）
        // contentOffset
        WriteInt64Xor(writer, contentOffset);
        // entryCount
        WriteInt32Xor(writer, Entries.Count);

        // 每个条目
        foreach (var layout in entryLayouts)
        {
            // position在TOC中存储的是相对于contentOffset的偏移
            // Source: ContentCache.cs line 122: position = binaryReader.ReadInt64() + num
            // 写入时需要减去contentOffset
            long relativePosition = layout.NewPosition - contentOffset;

            WriteStringXor(writer, layout.Entry.Name);
            WriteStringXor(writer, layout.Entry.TypeName);
            WriteInt64Xor(writer, relativePosition);
            WriteInt64Xor(writer, layout.NewSize);
        }

        // 3c. 写入内容区（XOR加密）
        foreach (var layout in entryLayouts)
        {
            byte[] data = ReadEntryContent(layout.Entry);

            // XOR加密: 用ContentPad加密，基于绝对位置
            byte[] encrypted = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                long absPos = layout.NewPosition + i;
                encrypted[i] = (byte)(data[i] ^ ContentPad[absPos % ContentPad.Length]);
            }

            writer.Write(encrypted);
        }

        writer.Flush();

        // 4. 保存成功后清除修改标记
        HasUnsavedChanges = false;
        _modifiedEntries.Clear();
    }

    /// <summary>
    /// 计算TOC区字节数
    /// </summary>
    private long CalculateTocSize()
    {
        long size = 8 + 4; // contentOffset(Int64) + entryCount(Int32)

        foreach (var entry in Entries)
        {
            // 7-bit编码字符串长度
            size += Get7BitEncodedStringSize(entry.Name);
            size += Get7BitEncodedStringSize(entry.TypeName);
            size += 8 + 8; // position(Int64) + size(Int64)
        }

        return size;
    }

    /// <summary>
    /// 计算7-bit编码字符串的字节数
    /// </summary>
    private static long Get7BitEncodedStringSize(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        int lengthBytes = 0;
        int len = utf8.Length;
        do
        {
            lengthBytes++;
            len >>= 7;
        } while (len > 0);

        return lengthBytes + utf8.Length;
    }

    /// <summary>
    /// XOR写入Int64 (TOC区)
    /// </summary>
    private void WriteInt64Xor(BinaryWriter writer, long value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        long pos = writer.BaseStream.Position;
        XorBuffer(bytes, pos, _tocPad);
        writer.Write(bytes);
    }

    /// <summary>
    /// XOR写入Int32 (TOC区)
    /// </summary>
    private void WriteInt32Xor(BinaryWriter writer, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        long pos = writer.BaseStream.Position;
        XorBuffer(bytes, pos, _tocPad);
        writer.Write(bytes);
    }

    /// <summary>
    /// XOR写入7-bit编码字符串 (TOC区)
    /// </summary>
    private void WriteStringXor(BinaryWriter writer, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        // 写入7-bit编码长度
        int len = utf8.Length;
        long pos = writer.BaseStream.Position;
        while (len > 0)
        {
            byte b = (byte)(len & 0x7F);
            len >>= 7;
            if (len > 0)
            {
                b |= 0x80;
            }
            writer.Write((byte)(b ^ _tocPad[pos % _tocPad.Length]));
            pos++;
        }

        // 写入UTF-8字节
        if (utf8.Length > 0)
        {
            XorBuffer(utf8, pos, _tocPad);
            writer.Write(utf8);
        }
    }

    /// <summary>
    /// 导出指定条目到文件
    /// </summary>
    public void ExtractEntry(PakEntry entry, string outputPath)
    {
        byte[] data = ExtractAsStandardFormat(entry);
        string dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(outputPath, data);
    }

    /// <summary>
    /// 导出整个文件夹（递归）
    /// </summary>
    public void ExtractFolder(PakFolder folder, string outputDir, Action<string> onProgress = null)
    {
        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        foreach (var entry in folder.Files)
        {
            // 根据TypeName推断文件扩展名
            string ext = GetExtensionForType(entry.TypeName);
            string fileName = entry.Name.Contains("/")
                ? entry.Name.Substring(entry.Name.LastIndexOf('/') + 1)
                : entry.Name;
            string outputPath = Path.Combine(outputDir, fileName + ext);
            onProgress?.Invoke(outputPath);
            ExtractEntry(entry, outputPath);
        }

        foreach (var sub in folder.SubFolders.Values)
        {
            ExtractFolder(sub, Path.Combine(outputDir, sub.Name), onProgress);
        }
    }

    /// <summary>
    /// 导出全部内容
    /// </summary>
    public void ExtractAll(string outputDir, Action<string> onProgress = null)
    {
        ExtractFolder(Root, outputDir, onProgress);
    }

    /// <summary>
    /// 获取指定路径的文件夹
    /// </summary>
    public PakFolder GetFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return Root;
        }

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        PakFolder current = Root;

        foreach (string part in parts)
        {
            if (!current.SubFolders.TryGetValue(part, out current))
            {
                return null;
            }
        }
        return current;
    }

    /// <summary>
    /// 获取指定路径的条目
    /// </summary>
    public PakEntry GetEntry(string path)
    {
        string folderPath = path.Contains("/") ? path.Substring(0, path.LastIndexOf('/')) : "";
        string fileName = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path;

        PakFolder folder = GetFolder(folderPath);
        if (folder == null)
        {
            return null;
        }

        foreach (var entry in folder.Files)
        {
            string entryFileName = entry.Name.Contains("/")
                ? entry.Name.Substring(entry.Name.LastIndexOf('/') + 1)
                : entry.Name;
            if (entryFileName == fileName)
            {
                return entry;
            }
        }
        return null;
    }

    // ===== 私有方法 =====

    /// <summary>
    /// XOR读取Int64 (TOC区)
    /// </summary>
    private long ReadInt64Xor()
    {
        byte[] bytes = new byte[8];
        _reader.Read(bytes, 0, 8);
        XorBuffer(bytes, _stream.Position - 8, _tocPad);
        return BitConverter.ToInt64(bytes, 0);
    }

    /// <summary>
    /// XOR读取Int32 (TOC区)
    /// </summary>
    private int ReadInt32Xor()
    {
        byte[] bytes = new byte[4];
        _reader.Read(bytes, 0, 4);
        XorBuffer(bytes, _stream.Position - 4, _tocPad);
        return BitConverter.ToInt32(bytes, 0);
    }

    /// <summary>
    /// XOR读取7-bit编码字符串 (TOC区)
    /// Source: BinaryReader.ReadString() = 7-bit encoded length + UTF-8 bytes
    /// </summary>
    private string ReadStringXor()
    {
        // 读取7-bit编码的长度
        int length = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByteXor();
            length |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }
            shift += 7;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        // 读取UTF-8字节
        byte[] strBytes = new byte[length];
        long startPos = _stream.Position;
        _reader.Read(strBytes, 0, length);
        XorBuffer(strBytes, startPos, _tocPad);

        return Encoding.UTF8.GetString(strBytes);
    }

    /// <summary>
    /// XOR读取单字节 (TOC区)
    /// </summary>
    private byte ReadByteXor()
    {
        long pos = _stream.Position;
        byte b = _reader.ReadByte();
        return (byte)(b ^ _tocPad[pos % _tocPad.Length]);
    }

    /// <summary>
    /// 对buffer进行XOR解密
    /// </summary>
    private static void XorBuffer(byte[] buffer, long absoluteOffset, byte[] pad)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(buffer[i] ^ pad[(absoluteOffset + i) % pad.Length]);
        }
    }

    /// <summary>
    /// 构建虚拟文件夹树
    /// </summary>
    private void BuildFolderTree()
    {
        foreach (var entry in Entries)
        {
            string[] parts = entry.Name.Split('/');
            PakFolder current = Root;

            // 逐层创建文件夹
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!current.SubFolders.TryGetValue(parts[i], out var sub))
                {
                    sub = new PakFolder
                    {
                        Name = parts[i],
                        FullPath = string.Join("/", parts, 0, i + 1)
                    };
                    current.SubFolders[parts[i]] = sub;
                }
                current = sub;
            }

            // 添加文件到当前文件夹
            current.Files.Add(entry);
        }
    }

    /// <summary>
    /// 生成TOC XOR密钥 (与ContentManager.Pad()完全一致)
    /// Source: Survivalcraft/Game/ContentManager.cs Pad() + Survivalcraft/Game/Random.cs
    /// </summary>
    private static string GeneratePad()
    {
        string text = string.Empty;
        string charset = "0123456789abdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        GameRandom random = new GameRandom(9217);
        for (int i = 0; i < 229; i++)
        {
            text += charset[random.Int(charset.Length)];
        }
        return text;
    }

    /// <summary>
    /// Game.Random的精确C#复现 (XorShift128+)
    /// Source: Survivalcraft/Game/Random.cs
    /// </summary>
    private class GameRandom
    {
        private uint m_s0;
        private uint m_s1;

        public GameRandom(int seed)
        {
            Seed(seed);
        }

        public void Seed(int seed)
        {
            m_s0 = Hash((uint)seed);
            m_s1 = Hash((uint)(seed + 1));
        }

        private static uint Hash(uint key)
        {
            key ^= key >> 16;
            key *= 2146121005;
            key ^= key >> 15;
            key *= 2221713035u;
            key ^= key >> 16;
            return key;
        }

        private static uint RotateLeft(uint x, int k)
        {
            return (x << k) | (x >> (32 - k));
        }

        public uint UInt()
        {
            uint s = m_s0;
            uint s2 = m_s1;
            s2 ^= s;
            m_s0 = RotateLeft(s, 26) ^ s2 ^ (s2 << 9);
            m_s1 = RotateLeft(s2, 13);
            return RotateLeft(s * 2654435771u, 5) * 5;
        }

        public int Int()
        {
            return (int)(UInt() & 0x7FFFFFFF);
        }

        public int Int(int bound)
        {
            return (int)((long)Int() * (long)bound / 2147483648u);
        }
    }

    /// <summary>
    /// 根据TypeName推断文件扩展名
    /// </summary>
    public static string GetExtensionForType(string typeName)
    {
        return typeName switch
        {
            "Engine.Audio.SoundBuffer" => ".wav",
            "Engine.Graphics.Texture2D" => ".png",
            "Engine.Graphics.Model" => ".dae",
            "Engine.Media.BitmapFont" => ".fnt",
            "Engine.Media.StreamingSource" => ".ogg",
            "Engine.Graphics.Shader" => ".fsh",
            "System.Xml.Linq.XElement" => ".xml",
            "System.String" => ".txt",
            _ => ".bin"
        };
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _stream?.Dispose();
    }
}
