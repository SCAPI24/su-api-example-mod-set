using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PakExplorer.ViewModels;

namespace PakExplorer.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel VM => (MainWindowViewModel)DataContext;

    /// <summary>
    /// TreeView选中项变化时，通知ViewModel加载文件列表
    /// </summary>
    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is TreeView tree)
        {
            if (tree.SelectedItem is FolderNodeViewModel node)
            {
                vm.SelectedFolder = node;
            }
        }
    }

    // ===== 打开/打开方式 =====

    /// <summary>
    /// 提取到临时文件并用系统默认程序打开
    /// </summary>
    private void OnOpenWithSystem(object sender, RoutedEventArgs e)
    {
        if (VM?.SelectedEntry == null || VM?._pak == null)
        {
            return;
        }

        // 文件夹项：导航进入
        if (VM.SelectedEntry.IsFolder)
        {
            NavigateToFolder(VM.SelectedEntry.FolderPath);
            return;
        }

        try
        {
            string tempPath = ExtractToTemp(VM.SelectedEntry);
            if (tempPath != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            VM.StatusText = "打开失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 提取到临时文件并弹出系统"打开方式"对话框
    /// </summary>
    private void OnOpenWithDialog(object sender, RoutedEventArgs e)
    {
        if (VM?.SelectedEntry == null || VM?._pak == null)
        {
            return;
        }

        // 文件夹项不支持打开方式
        if (VM.SelectedEntry.IsFolder)
        {
            VM.StatusText = "文件夹不支持打开方式操作";
            return;
        }

        try
        {
            string tempPath = ExtractToTemp(VM.SelectedEntry);
            if (tempPath != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = "shell32.dll,OpenAs_RunDLL " + tempPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            VM.StatusText = "打开失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 提取条目到临时目录（标准文件格式）
    /// </summary>
    private string ExtractToTemp(EntryItemViewModel item)
    {
        if (item == null)
        {
            return null;
        }

        var entry = item.Entry;
        string ext = PakFile.GetExtensionForType(entry.TypeName);
        string safeName = entry.Name.Replace("/", "_") + ext;
        string tempDir = Path.Combine(Path.GetTempPath(), "PakExplorer");
        if (!Directory.Exists(tempDir))
        {
            Directory.CreateDirectory(tempDir);
        }
        string tempPath = Path.Combine(tempDir, safeName);

        // 使用标准格式提取
        byte[] data = VM._pak.ExtractAsStandardFormat(entry);
        File.WriteAllBytes(tempPath, data);
        return File.Exists(tempPath) ? tempPath : null;
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    // ===== 文件操作 =====

    private async void OnOpenPak(object sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this).StorageProvider;
        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Content.pak 文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PAK 文件")
                {
                    Patterns = new[] { "*.pak" }
                },
                new FilePickerFileType("所有文件")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (result.Count > 0)
        {
            string path = result[0].TryGetLocalPath();
            if (path != null)
            {
                VM.OpenPak(path);
            }
        }
    }

    private void OnClosePak(object sender, RoutedEventArgs e)
    {
        VM.ClosePak();
    }

    private async void OnExit(object sender, RoutedEventArgs e)
    {
        if (VM.HasUnsavedChanges)
        {
            var confirm = await ShowConfirmDialog("有未保存的修改，确定退出?");
            if (!confirm)
            {
                return;
            }
        }
        Close();
    }
    // ===== 提取操作 =====

    private async void OnExtractSelected(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedEntry == null)
        {
            return;
        }

        var storage = GetTopLevel(this).StorageProvider;
        var entry = VM.SelectedEntry.Entry;
        string ext = PakFile.GetExtensionForType(entry.TypeName);
        string defaultName = entry.Name.Replace("/", "_") + ext;

        var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "提取文件",
            SuggestedFileName = defaultName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("原始格式") { Patterns = new[] { "*" + ext } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        if (result != null)
        {
            string path = result.TryGetLocalPath();
            if (path != null)
            {
                VM.ExtractSelected(path);
            }
        }
    }

    private async void OnExtractFolder(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedFolder == null)
        {
            return;
        }

        var storage = GetTopLevel(this).StorageProvider;
        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择输出目录",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            string dir = result[0].TryGetLocalPath();
            if (dir != null)
            {
                VM.ExtractCurrentFolder(Path.Combine(dir, VM.SelectedFolder.Name));
            }
        }
    }

    private async void OnExtractAll(object sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this).StorageProvider;
        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择输出目录",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            string dir = result[0].TryGetLocalPath();
            if (dir != null)
            {
                VM.ExtractAll(Path.Combine(dir, "Content_Extracted"));
            }
        }
    }

    // ===== 导入/修改操作 =====

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedEntry == null)
        {
            return;
        }

        var storage = GetTopLevel(this).StorageProvider;
        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要导入的文件",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            string path = result[0].TryGetLocalPath();
            if (path != null)
            {
                VM.ImportToSelected(path);
            }
        }
    }

    private async void OnSavePak(object sender, RoutedEventArgs e)
    {
        if (!VM.HasUnsavedChanges)
        {
            return;
        }

        var storage = GetTopLevel(this).StorageProvider;
        var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存修改后的PAK",
            SuggestedFileName = "Content_modified.pak",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PAK 文件") { Patterns = new[] { "*.pak" } }
            }
        });

        if (result != null)
        {
            string path = result.TryGetLocalPath();
            if (path != null)
            {
                VM.SavePak(path);
            }
        }
    }

    private void OnRevertSelected(object sender, RoutedEventArgs e)
    {
        VM.RevertSelected();
    }

    private void OnRevertAll(object sender, RoutedEventArgs e)
    {
        VM.RevertAll();
    }

    // ===== 搜索 =====

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        VM.Search();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        VM.ClearSearch();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            VM.Search();
        }
    }

    // ===== 视图 =====

    private async void OnStatistics(object sender, RoutedEventArgs e)
    {
        string stats = VM.GetStatistics();
        if (!string.IsNullOrEmpty(stats))
        {
            var dialog = new Window
            {
                Title = "PAK 统计信息",
                Width = 550,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox
                {
                    Text = stats,
                    IsReadOnly = true,
                    FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    AcceptsReturn = true,
                    Margin = new Avalonia.Thickness(8)
                }
            };
            await dialog.ShowDialog(this);
        }
    }

    // ===== 双击提取 =====

    // ===== 双击打开 =====

    private void OnEntryDoubleTapped(object sender, TappedEventArgs e)
    {
        if (VM?.SelectedEntry == null)
        {
            return;
        }

        // 双击子文件夹：导航进入该文件夹
        if (VM.SelectedEntry.IsFolder)
        {
            NavigateToFolder(VM.SelectedEntry.FolderPath);
            return;
        }

        try
        {
            string tempPath = ExtractToTemp(VM.SelectedEntry);
            if (tempPath != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            VM.StatusText = "打开失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 导航到指定文件夹路径（更新左侧TreeView选中项）
    /// </summary>
    private void NavigateToFolder(string folderPath)
    {
        if (VM == null || string.IsNullOrEmpty(folderPath))
        {
            return;
        }

        // 在ViewModel的树中递归查找节点
        var node = FindFolderNode(VM.RootNodes, folderPath);
        if (node != null)
        {
            // 展开父节点链
            ExpandParentChain(VM.RootNodes, folderPath);
            // 设置选中项（这会触发OnFolderSelectionChanged）
            var tree = this.FindControl<TreeView>("FolderTree");
            if (tree != null)
            {
                tree.SelectedItem = node;
            }
            // 同时直接设置ViewModel
            VM.SelectedFolder = node;
        }
        else
        {
            VM.StatusText = "未找到文件夹: " + folderPath;
        }
    }

    /// <summary>
    /// 展开到目标路径的父节点链
    /// </summary>
    private void ExpandParentChain(System.Collections.IEnumerable items, string targetPath)
    {
        foreach (var item in items)
        {
            if (item is FolderNodeViewModel node)
            {
                if (targetPath.StartsWith(node.FullPath))
                {
                    node.IsExpanded = true;
                    if (targetPath != node.FullPath)
                    {
                        ExpandParentChain(node.Children, targetPath);
                    }
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 在ViewModel节点树中递归查找指定路径的文件夹
    /// </summary>
    private FolderNodeViewModel FindFolderNode(System.Collections.IEnumerable items, string path)
    {
        foreach (var item in items)
        {
            if (item is FolderNodeViewModel node)
            {
                if (node.FullPath == path)
                {
                    return node;
                }
                var found = FindFolderNode(node.Children, path);
                if (found != null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    // ===== 辅助 =====

    private async Task<bool> ShowConfirmDialog(string message)
    {
        var dialog = new Window
        {
            Title = "确认",
            Width = 350,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "取消", Width = 80, Tag = false },
                            new Button { Content = "确定", Width = 80, Tag = true }
                        }
                    }
                }
            }
        };

        bool? result = null;
        var buttonPanel = (StackPanel)((StackPanel)dialog.Content).Children[1];
        foreach (var child in buttonPanel.Children)
        {
            var btn = (Button)child;
            btn.Click += (s, e) =>
            {
                result = (bool)btn.Tag;
                dialog.Close();
            };
        }

        await dialog.ShowDialog(this);
        return result == true;
    }
}
