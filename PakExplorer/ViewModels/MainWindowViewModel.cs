using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PakExplorer.ViewModels;

/// <summary>
/// 主窗口ViewModel - 绑定PakFile数据，驱动所有GUI操作
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    internal PakFile _pak;

    // ===== 绑定属性 =====

    private string _title = "PakExplorer";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private bool _isLoaded;
    public bool IsLoaded
    {
        get => _isLoaded;
        set => SetProperty(ref _isLoaded, value);
    }

    private string _statusText = "请打开 Content.pak 文件";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set => SetProperty(ref _hasUnsavedChanges, value);
    }

    public ObservableCollection<FolderNodeViewModel> RootNodes { get; } = new();
    public ObservableCollection<EntryItemViewModel> CurrentFiles { get; } = new();

    private FolderNodeViewModel _selectedFolder;
    public FolderNodeViewModel SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
            {
                LoadFolderContents(value);
            }
        }
    }

    private EntryItemViewModel _selectedEntry;
    public EntryItemViewModel SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                UpdateEntryDetail(value);
            }
        }
    }

    // 详情面板
    private string _detailText = "";
    public string DetailText
    {
        get => _detailText;
        set => SetProperty(ref _detailText, value);
    }

    // 搜索
    private string _searchKeyword;
    public string SearchKeyword
    {
        get => _searchKeyword;
        set => SetProperty(ref _searchKeyword, value);
    }


    public MainWindowViewModel()
    {
    }

    /// <summary>
    /// 打开PAK文件
    /// </summary>
    public void OpenPak(string path)
    {
        ClosePak();

        try
        {
            _pak = new PakFile(path);
            _pak.Open();

            // 构建文件夹树
            RootNodes.Clear();
            var rootNode = new FolderNodeViewModel(_pak.Root);
            rootNode.IsExpanded = true;
            RootNodes.Add(rootNode);

            Title = "PakExplorer - " + Path.GetFileName(path);
            IsLoaded = true;
            HasUnsavedChanges = false;
            StatusText = $"已加载: {_pak.Entries.Count} 个条目, 内容偏移: {_pak.ContentDataOffset}";
            DetailText = "";
            PreviewText = "";
            IsPreviewVisible = false;
        }
        catch (Exception ex)
        {
            StatusText = "打开失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 关闭当前PAK
    /// </summary>
    public void ClosePak()
    {
        if (_pak != null)
        {
            _pak.Dispose();
            _pak = null;
        }
        RootNodes.Clear();
        CurrentFiles.Clear();
        Title = "PakExplorer";
        IsLoaded = false;
        HasUnsavedChanges = false;
        StatusText = "请打开 Content.pak 文件";
        DetailText = "";
        PreviewText = "";
        IsPreviewVisible = false;
    }

    // 预览
    private string _previewText;
    public string PreviewText
    {
        get => _previewText;
        set => SetProperty(ref _previewText, value);
    }

    private bool _isPreviewVisible;
    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set => SetProperty(ref _isPreviewVisible, value);
    }

    /// <summary>
    /// 加载选中文件夹的文件列表
    /// </summary>
    private void LoadFolderContents(FolderNodeViewModel node)
    {
        CurrentFiles.Clear();
        if (_pak == null || node == null)
        {
            StatusText = "LoadFolderContents: pak或node为null";
            return;
        }

        var folder = _pak.GetFolder(node.FullPath);
        if (folder == null)
        {
            StatusText = "LoadFolderContents: GetFolder返回null, path=" + node.FullPath;
            return;
        }

        // 先添加子文件夹
        foreach (var sub in folder.SubFolders.Values.OrderBy(x => x.Name))
        {
            int count = CountAllFiles(sub);
            // 直接用PakFolder的FullPath，避免手动拼接路径
            CurrentFiles.Add(new EntryItemViewModel(sub.Name, sub.FullPath, count));
        }

        // 再添加文件
        foreach (var entry in folder.Files)
        {
            CurrentFiles.Add(new EntryItemViewModel(entry, _pak));
        }

        StatusText = string.Format("{0}: {1} 个子文件夹, {2} 个文件", node.FullPath, folder.SubFolders.Count, folder.Files.Count);
    }

    /// <summary>
    /// 递归计算文件夹下的文件总数
    /// </summary>
    private int CountAllFiles(PakFile.PakFolder folder)
    {
        int count = folder.Files.Count;
        foreach (var sub in folder.SubFolders.Values)
        {
            count += CountAllFiles(sub);
        }
        return count;
    }

    /// <summary>
    /// 更新详情面板
    /// </summary>
    private void UpdateEntryDetail(EntryItemViewModel item)
    {
        if (item == null || _pak == null)
        {
            DetailText = "";
            PreviewText = "";
            IsPreviewVisible = false;
            return;
        }

        var e = item.Entry;
        string ext = PakFile.GetExtensionForType(e.TypeName);
        string modified = _pak.IsEntryModified(e) ? " [已修改]" : "";

        DetailText = $"名称: {e.Name}\n"
                   + $"类型: {e.TypeName}\n"
                   + $"扩展名: {ext}\n"
                   + $"偏移: 0x{e.Position:X} ({e.Position})\n"
                   + $"原始大小: {e.Size} 字节\n"
                   + $"当前大小: {_pak.GetEntrySize(e)} 字节{modified}";

        // 文本类型尝试预览
        if (e.TypeName is "System.Xml.Linq.XElement" or "System.String" or "Engine.Graphics.Shader")
        {
            try
            {
                byte[] data = _pak.ReadEntryContent(e);
                string text = System.Text.Encoding.UTF8.GetString(data);
                if (text.Length > 5000)
                {
                    text = text.Substring(0, 5000) + $"\n... (共 {text.Length} 字符, 显示前 5000)";
                }
                PreviewText = text;
                IsPreviewVisible = true;
            }
            catch (Exception ex)
            {
                PreviewText = "预览失败: " + ex.Message;
                IsPreviewVisible = true;
            }
        }
        else
        {
            PreviewText = "";
            IsPreviewVisible = false;
        }
    }

    /// <summary>
    /// 搜索条目
    /// </summary>
    public void Search()
    {
        if (_pak == null || string.IsNullOrWhiteSpace(SearchKeyword))
        {
            return;
        }

        string keyword = SearchKeyword.ToLower();
        CurrentFiles.Clear();

        var results = _pak.Entries.Where(e => e.Name.ToLower().Contains(keyword)).ToList();
        foreach (var entry in results)
        {
            CurrentFiles.Add(new EntryItemViewModel(entry, _pak));
        }

        StatusText = string.Format("搜索 \"{0}\": 找到 {1} 个条目", SearchKeyword, results.Count);
    }

    /// <summary>
    /// 清除搜索，恢复当前文件夹内容
    /// </summary>
    public void ClearSearch()
    {
        SearchKeyword = "";
        if (SelectedFolder != null)
        {
            LoadFolderContents(SelectedFolder);
        }
    }

    /// <summary>
    /// 提取选中条目到文件
    /// </summary>
    public void ExtractSelected(string outputPath)
    {
        if (_pak == null || SelectedEntry == null)
        {
            return;
        }
        try
        {
            _pak.ExtractEntry(SelectedEntry.Entry, outputPath);
            StatusText = "已提取: " + outputPath;
        }
        catch (Exception ex)
        {
            StatusText = "提取失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 提取当前文件夹全部
    /// </summary>
    public void ExtractCurrentFolder(string outputDir)
    {
        if (_pak == null || SelectedFolder == null)
        {
            return;
        }
        try
        {
            var folder = _pak.GetFolder(SelectedFolder.FullPath);
            if (folder != null)
            {
                _pak.ExtractFolder(folder, outputDir);
                StatusText = "已提取到: " + outputDir;
            }
        }
        catch (Exception ex)
        {
            StatusText = "提取失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 提取全部
    /// </summary>
    public void ExtractAll(string outputDir)
    {
        if (_pak == null)
        {
            return;
        }
        try
        {
            _pak.ExtractAll(outputDir);
            StatusText = "已提取全部到: " + outputDir;
        }
        catch (Exception ex)
        {
            StatusText = "提取失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 导入外部文件替换选中条目
    /// </summary>
    public void ImportToSelected(string externalPath)
    {
        if (_pak == null || SelectedEntry == null)
        {
            return;
        }
        try
        {
            byte[] newData = File.ReadAllBytes(externalPath);
            long oldSize = SelectedEntry.Entry.Size;
            _pak.ReplaceEntry(SelectedEntry.Entry, newData);
            HasUnsavedChanges = _pak.HasUnsavedChanges;
            LoadFolderContents(SelectedFolder);
            UpdateEntryDetail(SelectedEntry);
            StatusText = string.Format("已标记替换: {0} ({1} -> {2} 字节)", SelectedEntry.Entry.Name, oldSize, newData.Length);
        }
        catch (Exception ex)
        {
            StatusText = "导入失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 撤销选中条目修改
    /// </summary>
    public void RevertSelected()
    {
        if (_pak == null || SelectedEntry == null)
        {
            return;
        }
        if (_pak.RevertEntry(SelectedEntry.Entry))
        {
            HasUnsavedChanges = _pak.HasUnsavedChanges;
            LoadFolderContents(SelectedFolder);
            UpdateEntryDetail(SelectedEntry);
            StatusText = "已撤销修改: " + SelectedEntry.Entry.Name;
        }
        else
        {
            StatusText = "该条目未被修改";
        }
    }

    /// <summary>
    /// 撤销全部修改
    /// </summary>
    public void RevertAll()
    {
        if (_pak == null)
        {
            return;
        }
        _pak.RevertAll();
        HasUnsavedChanges = false;
        LoadFolderContents(SelectedFolder);
        UpdateEntryDetail(SelectedEntry);
        StatusText = "已撤销所有修改";
    }

    /// <summary>
    /// 保存修改到新PAK文件
    /// </summary>
    public void SavePak(string outputPath)
    {
        if (_pak == null)
        {
            return;
        }
        try
        {
            _pak.Save(outputPath);
            HasUnsavedChanges = false;
            StatusText = string.Format("保存成功: {0} ({1} 字节)", outputPath, new FileInfo(outputPath).Length);
        }
        catch (Exception ex)
        {
            StatusText = "保存失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 获取统计信息
    /// </summary>
    public string GetStatistics()
    {
        if (_pak == null)
        {
            return "";
        }
        var typeCounts = new System.Collections.Generic.Dictionary<string, int>();
        var typeSizes = new System.Collections.Generic.Dictionary<string, long>();
        long totalSize = 0;
        foreach (var e in _pak.Entries)
        {
            if (!typeCounts.ContainsKey(e.TypeName))
            {
                typeCounts[e.TypeName] = 0;
                typeSizes[e.TypeName] = 0;
            }
            typeCounts[e.TypeName]++;
            typeSizes[e.TypeName] += e.Size;
            totalSize += e.Size;
        }
        string result = string.Format("PAK文件: {0}\n文件大小: {1:F1} MB\nTOC偏移: {2} 字节\n条目总数: {3}\n内容总大小: {4:F1} MB\n\n按类型统计:\n",
            _pak.FilePath,
            new FileInfo(_pak.FilePath).Length / (1024.0 * 1024.0),
            _pak.ContentDataOffset,
            _pak.Entries.Count,
            totalSize / (1024.0 * 1024.0));
        foreach (var kv in typeCounts.OrderByDescending(k => typeSizes[k.Key]))
        {
            result += string.Format("  {0,-40} {1,6} {2:F1} MB\n", kv.Key, kv.Value, typeSizes[kv.Key] / (1024.0 * 1024.0));
        }
        return result;
    }
}