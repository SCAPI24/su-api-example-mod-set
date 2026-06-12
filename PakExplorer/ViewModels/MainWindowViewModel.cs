using System;
using System.Collections.Generic;
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

    // 多PAK支持: 每个根节点对应一个PakFile
    internal Dictionary<FolderNodeViewModel, PakFile> _pakMap = new();

    // 剪贴板
    private EntryItemViewModel _clipboardEntry;
    private bool _clipboardIsCut;

    // ===== 绑定属性 =====

    private string _title = "PakExplorer";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private string _menuPreferences = "Preferences(_P)";
    public string MenuPreferences
    {
        get => _menuPreferences;
        set => SetProperty(ref _menuPreferences, value);
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
                RaisePropertyChanged(nameof(HasSelectedEntry));
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
                RaisePropertyChanged(nameof(HasSelectedEntry));
                RaisePropertyChanged(nameof(CanPaste));
            }
        }
    }

    /// <summary>
    /// 是否有选中条目或文件夹（工具栏按钮启用状态）
    /// </summary>
    public bool HasSelectedEntry => SelectedEntry != null || SelectedFolder != null;

    /// <summary>
    /// 是否可以粘贴（剪贴板中有条目或文件夹）
    /// </summary>
    public bool CanPaste => _clipboardEntry != null || _clipboardFolder != null;

    /// <summary>
    /// 多选列表（Extended模式）
    /// </summary>
    public List<EntryItemViewModel> SelectedEntries { get; set; } = new();

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
        try
        {
            var pak = new PakFile(path);
            pak.Open();

            // 构建文件夹树 — 添加新根节点（不清除已有的）
            var rootNode = new FolderNodeViewModel(pak.Root);
            rootNode.Name = Path.GetFileName(path);
            rootNode.IsExpanded = true;
            RootNodes.Add(rootNode);
            _pakMap[rootNode] = pak;

            // 切换到新打开的PAK
            _pak = pak;
            Title = "PakExplorer - " + rootNode.Name;
            IsLoaded = true;
            HasUnsavedChanges = false;
            StatusText = Lang.Get("Status_Loaded", pak.Entries.Count, pak.ContentDataOffset);
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
    /// 新建空PAK
    /// </summary>
    public void NewPak(string name)
    {
        var pak = new PakFile(); // 空PAK构造

        var rootNode = new FolderNodeViewModel(pak.Root);
        rootNode.Name = name + " *";
        rootNode.IsExpanded = true;
        RootNodes.Add(rootNode);
        _pakMap[rootNode] = pak;

        _pak = pak;
        Title = "PakExplorer - " + name + " *";
        IsLoaded = true;
        HasUnsavedChanges = true;
        StatusText = Lang.Get("NewPak_Status", name);
        DetailText = "";
        PreviewText = "";
        IsPreviewVisible = false;
    }

    /// <summary>
    /// 关闭当前PAK
    /// </summary>
    public void ClosePak()
    {
        // 关闭当前选中的PAK（根据SelectedFolder找到根节点）
        if (SelectedFolder != null)
        {
            var root = FindRoot(SelectedFolder);
            if (root != null && _pakMap.TryGetValue(root, out var pak))
            {
                pak.Dispose();
                _pakMap.Remove(root);
                RootNodes.Remove(root);
            }
        }

        if (_pakMap.Count == 0)
        {
            _pak = null;
            CurrentFiles.Clear();
            Title = "PakExplorer";
            IsLoaded = false;
            HasUnsavedChanges = false;
            StatusText = Lang.Get("Status_OpenHint");
        }
        else
        {
            // 切换到剩余的第一个PAK
            var first = _pakMap.First();
            _pak = first.Value;
            Title = "PakExplorer - " + first.Key.Name;
            IsLoaded = true;
        }

        DetailText = "";
        PreviewText = "";
        IsPreviewVisible = false;
    }

    /// <summary>
    /// 根据子节点找到根节点
    /// </summary>
    private FolderNodeViewModel FindRoot(FolderNodeViewModel node)
    {
        foreach (var root in RootNodes)
        {
            if (root == node || IsDescendant(root, node))
            {
                return root;
            }
        }
        return null;
    }

    private bool IsDescendant(FolderNodeViewModel parent, FolderNodeViewModel target)
    {
        foreach (var child in parent.Children)
        {
            if (child == target || IsDescendant(child, target))
            {
                return true;
            }
        }
        return false;
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
        string modified = _pak.IsEntryModified(e) ? Lang.Get("Detail_Modified") : "";

        DetailText = Lang.Get("Detail_Name", e.Name) + "\n"
                   + Lang.Get("Detail_Type", e.TypeName) + "\n"
                   + Lang.Get("Detail_Ext", ext) + "\n"
                   + Lang.Get("Detail_Position", e.Position, e.Position) + "\n"
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
            StatusText = Lang.Get("Status_Extracted", outputPath);
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
                StatusText = Lang.Get("Status_ExtractedTo", outputDir);
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
    /// 保存后清除根节点的 * 未保存标记
    /// </summary>
    public void ClearUnsavedMarker()
    {
        if (SelectedFolder != null)
        {
            var root = SelectedFolder;
            while (root.Parent != null) root = root.Parent;
            if (root.Name.EndsWith(" *"))
            {
                root.Name = root.Name.Substring(0, root.Name.Length - 2);
                RaisePropertyChanged(nameof(Title));
                Title = "PakExplorer - " + root.Name;
            }
        }
    }

    // ===== 剪贴板操作 =====

    // 文件夹剪贴板
    private FolderNodeViewModel _clipboardFolder;
    private bool _clipboardFolderIsCut;
    private PakFile _clipboardFolderPak; // 来源PAK

    /// <summary>
    /// 复制选中的文件夹（TreeView右键/Ctrl+C）
    /// </summary>
    public void CopyFolder()
    {
        if (SelectedFolder == null) return;

        _clipboardFolder = SelectedFolder;
        _clipboardFolderIsCut = false;
        _clipboardFolderPak = _pak;

        // 同时设置条目剪贴板，让粘贴按钮可用
        _clipboardEntry = null; // 清除条目剪贴板
        _clipboardItems = null;
        RaisePropertyChanged(nameof(CanPaste));
        StatusText = $"已复制文件夹: {SelectedFolder.FullPath}";
    }

    /// <summary>
    /// 剪切选中的文件夹（TreeView右键/Ctrl+X）
    /// </summary>
    public void CutFolder()
    {
        if (SelectedFolder == null) return;

        _clipboardFolder = SelectedFolder;
        _clipboardFolderIsCut = true;
        _clipboardFolderPak = _pak;

        _clipboardEntry = null;
        _clipboardItems = null;
        RaisePropertyChanged(nameof(CanPaste));
        StatusText = $"已剪切文件夹: {SelectedFolder.FullPath}";
    }

    /// <summary>
    /// 粘贴到当前选中的文件夹（TreeView右键/Ctrl+V）
    /// </summary>
    public void PasteToFolder()
    {
        // 条目剪贴板优先
        if (_clipboardItems != null && _clipboardItems.Count > 0)
        {
            PasteToCurrentFolder();
            return;
        }

        // 文件夹剪贴板
        if (_clipboardFolder == null || _pak == null) return;

        string targetFolder = SelectedFolder?.FullPath ?? "";
        var sourcePak = _clipboardFolderPak ?? _pak;
        int pasted = 0;

        // 复制文件夹下所有文件（递归）
        var sourceFolder = sourcePak.GetFolder(_clipboardFolder.FullPath);
        if (sourceFolder == null)
        {
            StatusText = "源文件夹未找到: " + _clipboardFolder.FullPath;
            return;
        }

        // 文件夹本身也要作为子文件夹复制过去
        // 目标路径 = targetFolder + "/" + 源文件夹名
        string folderName = _clipboardFolder.Name;
        string newTargetPath = string.IsNullOrEmpty(targetFolder)
            ? folderName
            : targetFolder + "/" + folderName;

        pasted = PasteFolderRecursive(sourceFolder, sourcePak, newTargetPath, _clipboardFolder.FullPath);

        if (pasted > 0)
        {
            HasUnsavedChanges = true;
            RefreshCurrentTree();
            if (SelectedFolder != null) LoadFolderContents(SelectedFolder);
            StatusText = $"已粘贴 {pasted} 个条目到 {targetFolder}";
        }
    }

    /// <summary>
    /// 递归复制文件夹内容到目标路径
    /// </summary>
    private int PasteFolderRecursive(PakFile.PakFolder source, PakFile sourcePak, string targetPath, string sourcePath = "")
    {
        int count = 0;

        // 复制当前文件夹下的文件
        foreach (var entry in source.Files)
        {
            byte[] data;
            try
            {
                data = sourcePak.ReadEntryContent(entry);
            }
            catch
            {
                continue;
            }

            if (data == null || data.Length == 0)
            {
                continue;
            }

            // entry.Name是完整路径如 "Audio/Sub/Click"
            // 需要替换源前缀为目标路径
            string targetName;
            if (!string.IsNullOrEmpty(sourcePath) && entry.Name.StartsWith(sourcePath + "/"))
            {
                // 替换前缀: "Audio/Sub/Click" -> targetPath + "/Click"
                string relativeName = entry.Name.Substring(sourcePath.Length + 1);
                targetName = string.IsNullOrEmpty(targetPath)
                    ? relativeName
                    : targetPath + "/" + relativeName;
            }
            else
            {
                // 无法替换前缀，取文件名拼目标路径
                int lastSlash = entry.Name.LastIndexOf('/');
                string fileName = lastSlash >= 0 ? entry.Name.Substring(lastSlash + 1) : entry.Name;
                targetName = string.IsNullOrEmpty(targetPath)
                    ? fileName
                    : targetPath + "/" + fileName;
            }

            // 同名加后缀
            if (sourcePak == _pak && targetName == entry.Name)
            {
                targetName += "_copy";
            }

            _pak.AddOrReplaceEntry(targetName, entry.TypeName, data);
            count++;
        }

        // 递归子文件夹
        foreach (var sub in source.SubFolders.Values)
        {
            string subSourcePath = string.IsNullOrEmpty(sourcePath)
                ? sub.Name
                : sourcePath + "/" + sub.Name;
            string subTarget = string.IsNullOrEmpty(targetPath)
                ? sub.Name
                : targetPath + "/" + sub.Name;
            count += PasteFolderRecursive(sub, sourcePak, subTarget, subSourcePath);
        }

        return count;
    }

    /// <summary>
    /// 剪切选中条目
    /// </summary>
    public void CutSelected()
    {
        if (SelectedEntry == null && SelectedEntries.Count == 0) return;

        var items = SelectedEntries.Count > 0 ? SelectedEntries : new List<EntryItemViewModel> { SelectedEntry };
        _clipboardEntry = items[0]; // 保存第一个作为CanPaste标记
        _clipboardIsCut = true;
        _clipboardItems = items;
        _clipboardFolder = null; // 清除文件夹剪贴板
        _clipboardFolderPak = null;
        RaisePropertyChanged(nameof(CanPaste));
        StatusText = $"已剪切 {items.Count} 个条目";
    }

    /// <summary>
    /// 复制选中条目
    /// </summary>
    public void CopySelected()
    {
        if (SelectedEntry == null && SelectedEntries.Count == 0) return;

        var items = SelectedEntries.Count > 0 ? SelectedEntries : new List<EntryItemViewModel> { SelectedEntry };
        _clipboardEntry = items[0];
        _clipboardIsCut = false;
        _clipboardItems = items;
        _clipboardFolder = null; // 清除文件夹剪贴板
        _clipboardFolderPak = null;
        RaisePropertyChanged(nameof(CanPaste));
        StatusText = $"已复制 {items.Count} 个条目";
    }

    /// <summary>
    /// 粘贴到当前文件夹
    /// </summary>
    public void PasteToCurrentFolder()
    {
        if (_clipboardItems == null || _clipboardItems.Count == 0 || _pak == null) return;

        string targetFolder = SelectedFolder?.FullPath ?? "";
        int pasted = 0;

        foreach (var item in _clipboardItems)
        {
            if (item.IsFolder) continue; // 文件夹暂不支持复制
            if (item.Entry == null) continue;

            // 从源PAK读取内容
            var sourcePak = FindPakForEntry(item);
            if (sourcePak == null)
            {
                StatusText = "无法找到源PAK: " + item.Entry.Name;
                continue;
            }

            byte[] data;
            try
            {
                data = sourcePak.ReadEntryContent(item.Entry);
            }
            catch (Exception ex)
            {
                StatusText = "读取源内容失败: " + item.Entry.Name + " - " + ex.Message;
                continue;
            }

            if (data == null || data.Length == 0)
            {
                continue;
            }

            // Entry.Name是完整路径如 "Audio/Click"，取最后一段作为文件名
            string fileName = item.Entry.Name;
            int lastSlash = fileName.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                fileName = fileName.Substring(lastSlash + 1);
            }

            // 拼接目标完整路径
            string targetName = string.IsNullOrEmpty(targetFolder)
                ? fileName
                : targetFolder + "/" + fileName;

            // 如果目标路径与源路径完全相同（同PAK同位置），加后缀
            if (sourcePak == _pak && targetName == item.Entry.Name)
            {
                targetName += "_copy";
            }

            _pak.AddOrReplaceEntry(targetName, item.Entry.TypeName, data);
            pasted++;
        }

        if (pasted > 0)
        {
            HasUnsavedChanges = true;
            RefreshCurrentTree();
            // 刷新当前文件夹
            if (SelectedFolder != null) LoadFolderContents(SelectedFolder);
            StatusText = $"已粘贴 {pasted} 个条目到 {targetFolder}";
        }
    }

    // ===== 新建操作 =====

    /// <summary>
    /// 在当前选中的文件夹下新建子文件夹
    /// </summary>
    public void NewFolder(string folderName)
    {
        if (_pak == null || string.IsNullOrEmpty(folderName)) return;

        string parentPath = SelectedFolder?.FullPath ?? "";
        string newPath = string.IsNullOrEmpty(parentPath)
            ? folderName
            : parentPath + "/" + folderName;

        if (_pak.CreateFolder(newPath))
        {
            HasUnsavedChanges = _pak.HasUnsavedChanges;
            RefreshCurrentTree();
            // 刷新右侧列表：新建的子文件夹作为当前文件夹的子项显示
            if (SelectedFolder != null) LoadFolderContents(SelectedFolder);
            StatusText = Lang.Get("Status_NewFolder", newPath);
        }
        else
        {
            StatusText = Lang.Get("Status_FolderExists", newPath);
        }
    }

    /// <summary>
    /// 在当前选中的文件夹下新建空文件
    /// </summary>
    public void NewFile(string fileName, string typeName)
    {
        if (_pak == null || string.IsNullOrEmpty(fileName)) return;

        string parentPath = SelectedFolder?.FullPath ?? "";
        string fullPath = string.IsNullOrEmpty(parentPath)
            ? fileName
            : parentPath + "/" + fileName;

        var entry = _pak.CreateFile(fullPath, typeName);
        if (entry != null)
        {
            HasUnsavedChanges = _pak.HasUnsavedChanges;
            RefreshCurrentTree();
            if (SelectedFolder != null) LoadFolderContents(SelectedFolder);
            StatusText = Lang.Get("Status_NewFile", fullPath);
        }
        else
        {
            StatusText = Lang.Get("Status_FileExists", fullPath);
        }
    }

    // ===== 删除操作 =====

    /// <summary>
    /// 删除右侧DataGrid选中的条目
    /// </summary>
    public void DeleteSelected()
    {
        if (_pak == null) return;

        int deleted = 0;
        var items = SelectedEntries.Count > 0 ? SelectedEntries : (SelectedEntry != null ? new List<EntryItemViewModel> { SelectedEntry } : new List<EntryItemViewModel>());

        foreach (var item in items)
        {
            if (item.IsFolder) continue;
            if (item.Entry == null) continue;

            if (_pak.RemoveEntry(item.Entry))
            {
                deleted++;
            }
        }

        if (deleted > 0)
        {
            HasUnsavedChanges = _pak.HasUnsavedChanges;
            RefreshCurrentTree();
            SelectedEntry = null;
            if (SelectedFolder != null) LoadFolderContents(SelectedFolder);
            StatusText = $"已删除 {deleted} 个条目";
        }
    }

    /// <summary>
    /// 删除左侧TreeView选中的文件夹（文件夹本身+所有内容）
    /// </summary>
    public void DeleteFolder()
    {
        if (_pak == null || SelectedFolder == null) return;

        var folder = _pak.GetFolder(SelectedFolder.FullPath);
        if (folder == null) return;

        // 找到父文件夹和key
        string folderName = SelectedFolder.Name;
        string parentPath = "";
        int lastSlash = SelectedFolder.FullPath.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            parentPath = SelectedFolder.FullPath.Substring(0, lastSlash);
            folderName = SelectedFolder.FullPath.Substring(lastSlash + 1);
        }

        var parentFolder = _pak.GetFolder(parentPath);
        if (parentFolder == null) return;

        int deleted = _pak.RemoveFolder(folder, parentFolder, folderName);
        if (deleted > 0)
        {
            HasUnsavedChanges = _pak.HasUnsavedChanges;
            RefreshCurrentTree();
            if (SelectedFolder != null) LoadFolderContents(SelectedFolder);
            StatusText = $"已删除文件夹 {SelectedFolder.FullPath}（{deleted} 个条目）";
        }
    }

    private List<EntryItemViewModel> _clipboardItems;

    /// <summary>
    /// 查找条目所属的PakFile
    /// </summary>
    private PakFile FindPakForEntry(EntryItemViewModel item)
    {
        foreach (var kv in _pakMap)
        {
            if (kv.Value.Entries.Any(e => e == item.Entry))
            {
                return kv.Value;
            }
        }
        return _pak;
    }

    /// <summary>
    /// 刷新当前PAK对应的TreeView根节点（粘贴后更新文件夹树）
    /// </summary>
    private void RefreshCurrentTree()
    {
        if (_pak == null) return;

        // 找到当前PAK对应的根节点
        foreach (var kv in _pakMap)
        {
            if (kv.Value == _pak)
            {
                kv.Key.Refresh(_pak.Root);
                break;
            }
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