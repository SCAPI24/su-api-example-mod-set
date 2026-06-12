namespace PakExplorer.ViewModels;

/// <summary>
/// 右侧文件列表的单个条目（文件或子文件夹）
/// </summary>
public class EntryItemViewModel : ViewModelBase
{
    public PakFile.PakEntry Entry { get; }

    public string DisplayName { get; }
    public string TypeName { get; }
    public string Extension { get; }
    public long Size { get; }
    public string SizeFormatted { get; }
    public long Position { get; }

    /// <summary>
    /// 是否为子文件夹
    /// </summary>
    public bool IsFolder { get; }

    /// <summary>
    /// 子文件夹的完整路径（仅当IsFolder=true时有值）
    /// </summary>
    public string FolderPath { get; }

    /// <summary>
    /// 图标emoji
    /// </summary>
    public string Icon { get; }

    private bool _isModified;
    public bool IsModified
    {
        get => _isModified;
        set => SetProperty(ref _isModified, value);
    }

    /// <summary>
    /// 文件条目构造
    /// </summary>
    public EntryItemViewModel(PakFile.PakEntry entry, PakFile pak)
    {
        Entry = entry;
        // 取最后一段作为显示名
        int idx = entry.Name.LastIndexOf('/');
        DisplayName = idx >= 0 ? entry.Name.Substring(idx + 1) : entry.Name;
        TypeName = entry.TypeName;
        Extension = PakFile.GetExtensionForType(entry.TypeName);
        Size = pak.GetEntrySize(entry);
        SizeFormatted = FormatSize(Size);
        Position = entry.Position;
        IsModified = pak.IsEntryModified(entry);
        IsFolder = false;
        FolderPath = null;
        Icon = GetIconForType(entry.TypeName);
    }

    /// <summary>
    /// 子文件夹条目构造
    /// </summary>
    public EntryItemViewModel(string folderName, string folderPath, int fileCount)
    {
        Entry = null;
        DisplayName = folderName;
        TypeName = "文件夹";
        Extension = "";
        Size = 0;
        SizeFormatted = fileCount + " 个文件";
        Position = 0;
        IsModified = false;
        IsFolder = true;
        FolderPath = folderPath;
        Icon = "\U0001F4C1"; // 📁
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }
        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("F1") + " KB";
        }
        return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
    }

    /// <summary>
    /// 根据TypeName返回对应的emoji图标
    /// </summary>
    private static string GetIconForType(string typeName)
    {
        return typeName switch
        {
            "Engine.Graphics.Texture2D" => "\U0001F5BC",   // 🖼 图片
            "Engine.Graphics.Shader" => "\U0001F4A8",      // 💨 着色器
            "Engine.Graphics.Model" => "\U0001F4D0",       // 📐 模型
            "Engine.Audio.SoundBuffer" => "\U0001F50A",    // 🔊 音效
            "Engine.Media.StreamingSource" => "\U0001F3B5", // 🎵 音频流
            "Engine.Media.BitmapFont" => "\U0001F4DA",     // 📚 字体
            "System.Xml.Linq.XElement" => "\U0001F4C4",    // 📄 XML
            "System.String" => "\U0001F4DD",               // 📝 文本
            _ => "\U0001F4CB"                               // 📋 未知
        };
    }
}
