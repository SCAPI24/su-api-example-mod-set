using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PakExplorer.ViewModels;

namespace PakExplorer.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel VM => (MainWindowViewModel)DataContext;

    // 工具栏悬停提示
    private DispatcherTimer _tooltipTimer;
    private Window _tooltipWindow;

    // ===== TreeView 右键菜单 =====

    private void OnTreeCopyFolder(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.CopyFolder();
    }

    private void OnTreeCutFolder(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.CutFolder();
    }

    private void OnTreePasteFolder(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.PasteToFolder();
    }

    private void OnTreeDeleteFolder(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.DeleteFolder();
    }

    private void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.DeleteSelected();
    }

    private async void OnTreeNewFolder(object sender, RoutedEventArgs e)
    {
        if (VM == null) return;

        var input = new TextBox { Text = "NewFolder", Watermark = "文件夹名称", Width = 200 };
        var cancelBtn = new Button { Content = "取消", Width = 70, Tag = false };
        var okBtn = new Button { Content = "确定", Width = 70, Tag = true };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(okBtn);

        var inputPanel = new StackPanel { Spacing = 4 };
        inputPanel.Children.Add(new TextBlock { Text = "新建文件夹名称:" });
        inputPanel.Children.Add(input);

        var dock = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        dock.Children.Add(buttonPanel);
        dock.Children.Add(inputPanel);

        var dialog = new Window
        {
            Title = "新建文件夹",
            Width = 350, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Border { Padding = new Avalonia.Thickness(16), Child = dock }
        };

        string folderName = null;
        cancelBtn.Click += (_, _) => dialog.Close();
        okBtn.Click += (_, _) => { folderName = input.Text?.Trim(); dialog.Close(); };

        await dialog.ShowDialog(this);

        if (!string.IsNullOrEmpty(folderName))
        {
            VM.NewFolder(folderName);
        }
    }

    private async void OnTreeNewFile(object sender, RoutedEventArgs e)
    {
        await ShowNewFileDialog();
    }

    private async void OnGridNewFile(object sender, RoutedEventArgs e)
    {
        await ShowNewFileDialog();
    }

    private async Task ShowNewFileDialog()
    {
        if (VM == null) return;

        var nameInput = new TextBox { Text = "NewFile", Watermark = "文件名称", Width = 200 };
        var typeCombo = new ComboBox { Width = 200 };
        var types = new List<string>
        {
            "System.Xml.Linq.XElement",
            "Engine.Graphics.Texture2D",
            "Engine.Audio.SoundBuffer",
            "Engine.Graphics.Model",
            "System.String",
            "Engine.Media.BitmapFont",
            "Engine.Graphics.Shader",
            "Engine.Media.StreamingSource"
        };
        typeCombo.ItemsSource = types;
        typeCombo.SelectedIndex = 0;

        var cancelBtn = new Button { Content = "取消", Width = 70, Tag = false };
        var okBtn = new Button { Content = "确定", Width = 70, Tag = true };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(okBtn);

        var inputPanel = new StackPanel { Spacing = 4 };
        inputPanel.Children.Add(new TextBlock { Text = "文件名称:" });
        inputPanel.Children.Add(nameInput);
        inputPanel.Children.Add(new TextBlock { Text = "类型:" });
        inputPanel.Children.Add(typeCombo);

        var dock = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        dock.Children.Add(buttonPanel);
        dock.Children.Add(inputPanel);

        var dialog = new Window
        {
            Title = "新建文件",
            Width = 350, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Border { Padding = new Avalonia.Thickness(16), Child = dock }
        };

        string fileName = null;
        string typeName = null;
        cancelBtn.Click += (_, _) => dialog.Close();
        okBtn.Click += (_, _) =>
        {
            fileName = nameInput.Text?.Trim();
            typeName = typeCombo.SelectedItem as string;
            dialog.Close();
        };

        await dialog.ShowDialog(this);

        if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(typeName))
        {
            VM.NewFile(fileName, typeName);
        }
    }

    /// <summary>
    /// DataGrid键盘快捷键（Del删除）
    /// </summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (VM == null) return;

        if (e.Key == Key.Delete)
        {
            VM.DeleteSelected();
            e.Handled = true;
        }
    }

    /// <summary>
    /// TreeView键盘快捷键（Ctrl+C/X/V）
    /// </summary>
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (VM == null) return;

        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            VM.CopyFolder();
            e.Handled = true;
        }
        else if (e.Key == Key.X && e.KeyModifiers == KeyModifiers.Control)
        {
            VM.CutFolder();
            e.Handled = true;
        }
        else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            VM.PasteToFolder();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            VM.DeleteFolder();
            e.Handled = true;
        }
    }

    /// <summary>
    /// TreeView选中项变化时，通知ViewModel加载文件列表
    /// </summary>
    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is TreeView tree)
        {
            if (tree.SelectedItem is FolderNodeViewModel node)
            {
                // 切换到对应根节点的PakFile
                var root = node;
                while (root.Parent != null) root = root.Parent;
                if (vm._pakMap.TryGetValue(root, out var pak))
                {
                    vm._pak = pak;
                }
                vm.SelectedFolder = node;
            }
        }
    }

    /// <summary>
    /// TreeView点击空白区域时取消选中
    /// </summary>
    private void OnTreePointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (sender is TreeView tree)
        {
            var pos = e.GetPosition(tree);
            var hit = tree.InputHitTest(pos) as Visual;
            // 如果点击的不是TreeViewItem，则取消选中
            var item = hit;
            while (item != null && item != tree)
            {
                if (item is TreeViewItem) return; // 点击了项，不处理
                item = item.GetVisualParent() as Visual;
            }
            // 点击了空白区域
            tree.SelectedItem = null;
            if (VM != null) VM.SelectedFolder = null;
        }
    }

    /// <summary>
    /// DataGrid隧道事件：右键弹菜单，左键空白取消选中
    /// </summary>
    private void OnDataGridPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var props = e.GetCurrentPoint(grid).Properties;

        // 判断是否点击在行上
        var pos = e.GetPosition(grid);
        var hit = grid.InputHitTest(pos) as Visual;
        bool onRow = false;
        var item = hit;
        while (item != null && item != grid)
        {
            if (item is DataGridRow)
            {
                onRow = true;
                break;
            }
            item = item.GetVisualParent() as Visual;
        }

        if (props.IsRightButtonPressed)
        {
            if (!onRow)
            {
                grid.SelectedItem = null;
                if (VM != null) VM.SelectedEntry = null;
            }
            e.Handled = true;
            var rightPanel = this.FindControl<Grid>("RightPanel");
            if (rightPanel != null)
            {
                ShowRightContextMenu(rightPanel);
            }
        }
        else if (!onRow)
        {
            // 左键空白区域：延迟取消选中，让DataGrid先完成内部处理
            Dispatcher.UIThread.Post(() =>
            {
                grid.SelectedItem = null;
                if (VM != null) VM.SelectedEntry = null;
            });
        }
    }

    /// <summary>
    /// 右侧区域：右键弹菜单，左键空白取消选中
    /// </summary>
    private void OnRightPanelPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (sender is not Grid panel) return;
        var props = e.GetCurrentPoint(panel).Properties;

        if (props.IsRightButtonPressed)
        {
            e.Handled = true;
            ShowRightContextMenu(panel);
        }
        else
        {
            // 左键：判断是否点击在DataGrid行上
            var grid = this.FindControl<Avalonia.Controls.DataGrid>("EntryGrid");
            if (grid != null)
            {
                var pos = e.GetPosition(grid);
                var hit = grid.InputHitTest(pos) as Visual;
                bool onRow = false;
                var item = hit;
                while (item != null && item != grid)
                {
                    if (item is DataGridRow)
                    {
                        onRow = true;
                        break;
                    }
                    item = item.GetVisualParent() as Visual;
                }

                if (!onRow)
                {
                    grid.SelectedItem = null;
                    if (VM != null) VM.SelectedEntry = null;
                }
            }
        }
    }

    private ContextMenu _rightMenu;

    private void ShowRightContextMenu(Control target)
    {
        // 复用同一个ContextMenu实例，避免重复弹出
        if (_rightMenu == null)
        {
            _rightMenu = new ContextMenu();
        }
        else
        {
            _rightMenu.Close();
            _rightMenu.Items.Clear();
        }

        // 没有打开PAK或没有选中文件夹时，不弹菜单
        if (VM == null || !VM.IsLoaded || VM.SelectedFolder == null) return;

        if (VM.SelectedEntry != null)
        {
            // 有选中条目：完整菜单
            var miOpen = new MenuItem { Header = "打开" };
            miOpen.Click += OnOpenWithSystem;
            var miOpenWith = new MenuItem { Header = "打开方式..." };
            miOpenWith.Click += OnOpenWithDialog;
            var miNewFolder = new MenuItem { Header = "新建文件夹" };
            miNewFolder.Click += OnTreeNewFolder;
            var miNewFile = new MenuItem { Header = "新建文件" };
            miNewFile.Click += OnGridNewFile;
            var miExtract = new MenuItem { Header = "提取..." };
            miExtract.Click += OnExtractSelected;
            var miImport = new MenuItem { Header = "导入替换..." };
            miImport.Click += OnImport;
            var miDelete = new MenuItem { Header = "删除" };
            miDelete.Click += OnDeleteEntry;
            var miRevert = new MenuItem { Header = "撤销修改" };
            miRevert.Click += OnRevertSelected;

            _rightMenu.Items.Add(miOpen);
            _rightMenu.Items.Add(miOpenWith);
            _rightMenu.Items.Add(new Separator());
            _rightMenu.Items.Add(miNewFolder);
            _rightMenu.Items.Add(miNewFile);
            _rightMenu.Items.Add(new Separator());
            _rightMenu.Items.Add(miExtract);
            _rightMenu.Items.Add(miImport);
            _rightMenu.Items.Add(new Separator());
            _rightMenu.Items.Add(miDelete);
            _rightMenu.Items.Add(new Separator());
            _rightMenu.Items.Add(miRevert);
        }
        else
        {
            // 无选中条目：新建菜单（在当前选中文件夹下操作）
            var miNewFolder = new MenuItem { Header = "新建文件夹" };
            miNewFolder.Click += OnTreeNewFolder;
            var miNewFile = new MenuItem { Header = "新建文件" };
            miNewFile.Click += OnGridNewFile;

            _rightMenu.Items.Add(miNewFolder);
            _rightMenu.Items.Add(miNewFile);
        }

        _rightMenu.Open(target);
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

        // 用隧道事件拦截DataGrid的右键（冒泡事件在行上被DataGrid吞掉）
        var grid = this.FindControl<Avalonia.Controls.DataGrid>("EntryGrid");
        if (grid != null)
        {
            grid.AddHandler(PointerPressedEvent, OnDataGridPointerPressed, RoutingStrategies.Tunnel);

            // 设置每列最小宽度，确保标题文字始终可见
            if (grid.Columns.Count > 0)
            {
                foreach (var col in grid.Columns)
                {
                    string header = col.Header?.ToString() ?? "";
                    double minWidth = 0;
                    foreach (char c in header)
                    {
                        minWidth += c > 127 ? 16 : 9;
                    }
                    minWidth += 38;
                    col.MinWidth = minWidth;
                }
            }
        }
    }

    // ===== 文件操作 =====

    private async void OnNewPak(object sender, RoutedEventArgs e)
    {
        // 弹出输入对话框获取PAK名称
        var nameInput = new TextBox { Text = "NewPak", Watermark = "输入PAK名称", Width = 200 };

        var cancelBtn = new Button { Content = "取消", Width = 70, Tag = false };
        var okBtn = new Button { Content = "确定", Width = 70, Tag = true };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(okBtn);

        var inputPanel = new StackPanel { Spacing = 4 };
        inputPanel.Children.Add(new TextBlock { Text = "PAK名称:" });
        inputPanel.Children.Add(nameInput);

        var dock = new DockPanel();
        dock.Children.Add(buttonPanel);
        dock.Children.Add(inputPanel);

        var dialog = new Window
        {
            Title = "新建PAK",
            Width = 350,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Border { Padding = new Avalonia.Thickness(16), Child = dock }
        };

        string pakName = null;
        cancelBtn.Click += (_, _) => dialog.Close();
        okBtn.Click += (_, _) =>
        {
            pakName = nameInput.Text?.Trim();
            dialog.Close();
        };

        await dialog.ShowDialog(this);

        if (string.IsNullOrEmpty(pakName))
        {
            return;
        }

        // 创建空PAK并添加到ViewModel
        if (VM != null)
        {
            VM.NewPak(pakName);
        }
    }

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
        if (VM == null || !VM.HasUnsavedChanges)
        {
            return;
        }

        // 如果PAK有FilePath，直接覆盖保存
        if (!string.IsNullOrEmpty(VM._pak?.FilePath) && File.Exists(VM._pak.FilePath))
        {
            VM.SavePak(VM._pak.FilePath);
            VM.ClearUnsavedMarker();
            return;
        }

        // 新建的PAK没有FilePath，必须选择保存路径
        var storage = GetTopLevel(this).StorageProvider;
        string savePath = null;
        var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存PAK",
            SuggestedFileName = "NewPak.pak",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PAK 文件") { Patterns = new[] { "*.pak" } }
            }
        });

        if (result != null)
        {
            savePath = result.TryGetLocalPath();
        }

        // 先释放StorageProvider的文件句柄，再执行Save
        if (result != null)
        {
            result.Dispose();
        }

        if (savePath != null)
        {
            VM.SavePak(savePath);
            VM.ClearUnsavedMarker();
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

    // ===== 选中状态同步 =====

    private void OnEntrySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VM != null && sender is DataGrid grid)
        {
            // Extended模式下同步多选到ViewModel
            VM.SelectedEntries = grid.SelectedItems.Cast<EntryItemViewModel>().ToList();
        }
    }

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

    // ===== 工具栏按钮 =====

    private void OnToolCut(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.CutSelected();
    }

    private void OnToolCopy(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.CopySelected();
    }

    private void OnToolPaste(object sender, RoutedEventArgs e)
    {
        if (VM != null) VM.PasteToFolder();
    }

    private void OnToolRename(object sender, RoutedEventArgs e)
    {
        if (VM != null && VM.SelectedEntry != null)
        {
            // TODO: 重命名功能
            VM.StatusText = "重命名功能开发中...";
        }
    }

    private void OnToolExport(object sender, RoutedEventArgs e)
    {
        OnExtractSelected(sender, e);
    }

    private void OnToolDelete(object sender, RoutedEventArgs e)
    {
        if (VM != null)
        {
            // 如果选中了文件夹节点，删除文件夹
            if (VM.SelectedFolder != null && (VM.SelectedEntry == null || VM.SelectedEntry.IsFolder))
            {
                VM.DeleteFolder();
            }
            else
            {
                VM.DeleteSelected();
            }
        }
    }

    // ===== 工具栏悬停提示 =====

    private void OnToolPointerEntered(object sender, PointerEventArgs e)
    {
        if (sender is Button btn && btn.IsEnabled)
        {
            _tooltipTimer?.Stop();
            _tooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tooltipTimer.Tick += (_, _) =>
            {
                _tooltipTimer.Stop();
                ShowToolTooltip(btn);
            };
            _tooltipTimer.Start();
        }
    }

    private void OnToolPointerExited(object sender, PointerEventArgs e)
    {
        _tooltipTimer?.Stop();
        _tooltipTimer = null;
        HideToolTooltip();
    }

    private void ShowToolTooltip(Button btn)
    {
        HideToolTooltip();

        var text = btn.Tag?.ToString();
        if (string.IsNullOrEmpty(text)) return;

        // 计算按钮在屏幕上的位置
        var topLeft = btn.PointToScreen(new Avalonia.Point(0, btn.Bounds.Height + 2));

        var tip = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(6, 3),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Avalonia.Media.Brushes.Black,
                FontSize = 12
            }
        };

        _tooltipWindow = new Window
        {
            Content = tip,
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            IsHitTestVisible = false,
            Focusable = false,
            Topmost = true,
            Background = Avalonia.Media.Brushes.Transparent,
            TransparencyLevelHint = new List<WindowTransparencyLevel> { WindowTransparencyLevel.Transparent },
            SizeToContent = SizeToContent.WidthAndHeight,
        };

        _tooltipWindow.Position = new PixelPoint((int)topLeft.X, (int)topLeft.Y);
        _tooltipWindow.Show(this);
    }

    private void HideToolTooltip()
    {
        if (_tooltipWindow != null)
        {
            _tooltipWindow.Close();
            _tooltipWindow = null;
        }
    }
}
