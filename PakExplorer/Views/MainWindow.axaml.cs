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

        var input = new TextBox { Text = "NewFolder", Watermark = Lang.Get("Dlg_NewFolder_Watermark"), Width = 200 };
        var cancelBtn = new Button { Content = Lang.Get("Dlg_Cancel"), Width = 70, Tag = false };
        var okBtn = new Button { Content = Lang.Get("Dlg_Confirm"), Width = 70, Tag = true };

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
        inputPanel.Children.Add(new TextBlock { Text = Lang.Get("Dlg_NewFolder_Label") });
        inputPanel.Children.Add(input);

        var dock = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        dock.Children.Add(buttonPanel);
        dock.Children.Add(inputPanel);

        var dialog = new Window
        {
            Title = Lang.Get("Dlg_NewFolder_Title"),
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

        var nameInput = new TextBox { Text = "NewFile", Watermark = Lang.Get("Dlg_NewFile_NameWatermark"), Width = 200 };
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

        var cancelBtn = new Button { Content = Lang.Get("Dlg_Cancel"), Width = 70, Tag = false };
        var okBtn = new Button { Content = Lang.Get("Dlg_Confirm"), Width = 70, Tag = true };

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
        inputPanel.Children.Add(new TextBlock { Text = Lang.Get("Dlg_NewFile_NameLabel") });
        inputPanel.Children.Add(nameInput);
        inputPanel.Children.Add(new TextBlock { Text = Lang.Get("Dlg_NewFile_TypeLabel") });
        inputPanel.Children.Add(typeCombo);

        var dock = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        dock.Children.Add(buttonPanel);
        dock.Children.Add(inputPanel);

        var dialog = new Window
        {
            Title = Lang.Get("Dlg_NewFile_Title"),
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
            var miOpen = new MenuItem { Header = Lang.Get("Ctx_Open") };
            miOpen.Click += OnOpenWithSystem;
            var miOpenWith = new MenuItem { Header = Lang.Get("Ctx_OpenWith") };
            miOpenWith.Click += OnOpenWithDialog;
            var miNewFolder = new MenuItem { Header = Lang.Get("Ctx_NewFolder") };
            miNewFolder.Click += OnTreeNewFolder;
            var miNewFile = new MenuItem { Header = Lang.Get("Ctx_NewFile") };
            miNewFile.Click += OnGridNewFile;
            var miExtract = new MenuItem { Header = Lang.Get("Ctx_Extract") };
            miExtract.Click += OnExtractSelected;
            var miImport = new MenuItem { Header = Lang.Get("Ctx_Import") };
            miImport.Click += OnImport;
            var miDelete = new MenuItem { Header = Lang.Get("Ctx_Delete") };
            miDelete.Click += OnDeleteEntry;
            var miRevert = new MenuItem { Header = Lang.Get("Ctx_Revert") };
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
            var miNewFolder = new MenuItem { Header = Lang.Get("Ctx_NewFolder") };
            miNewFolder.Click += OnTreeNewFolder;
            var miNewFile = new MenuItem { Header = Lang.Get("Ctx_NewFile") };
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
            VM.StatusText = Lang.Get("Status_OpenFailed", ex.Message);
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
            VM.StatusText = Lang.Get("Status_FolderNoOpenWith");
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
            VM.StatusText = Lang.Get("Status_OpenFailed", ex.Message);
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

        // Language菜单点击
        var langItem = this.FindControl<MenuItem>("MenuLanguageItem");
        if (langItem != null)
        {
            langItem.Click += OnLanguage;
        }

        // 订阅语言切换事件，刷新所有UI文本
        Lang.LanguageChanged += RefreshUITexts;
        RefreshUITexts();
    }

    // ===== 语言与翻译 =====

    private async void OnLanguage(object sender, RoutedEventArgs e)
    {
        var enRadio = new RadioButton { Content = "English", IsChecked = Lang.Current == "en", Margin = new Avalonia.Thickness(0, 8, 0, 4) };
        var zhRadio = new RadioButton { Content = "中文", IsChecked = Lang.Current == "zh", Margin = new Avalonia.Thickness(0, 4, 0, 8) };

        var cancelBtn = new Button { Content = Lang.Get("Dlg_Cancel"), Width = 70 };
        var okBtn = new Button { Content = Lang.Get("Dlg_Confirm"), Width = 70 };

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
        inputPanel.Children.Add(new TextBlock { Text = Lang.Get("Dlg_Language_Title") });
        inputPanel.Children.Add(enRadio);
        inputPanel.Children.Add(zhRadio);

        var dock = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        dock.Children.Add(buttonPanel);
        dock.Children.Add(inputPanel);

        var dialog = new Window
        {
            Title = Lang.Get("Dlg_Language_Title"),
            Width = 300, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new Border { Padding = new Avalonia.Thickness(16), Child = dock }
        };

        bool confirmed = false;
        cancelBtn.Click += (_, _) => dialog.Close();
        okBtn.Click += (_, _) => { confirmed = true; dialog.Close(); };

        await dialog.ShowDialog(this);

        if (confirmed)
        {
            string newLang = enRadio.IsChecked == true ? "en" : "zh";
            if (newLang != Lang.Current)
            {
                Lang.LoadLanguage(newLang);
            }
        }
    }

    /// <summary>
    /// 语言切换后刷新所有UI文本
    /// </summary>
    private void RefreshUITexts()
    {
        // 菜单栏
        SetHeader("MenuFile", "Menu_File");
        SetHeader("MenuFileNewPak", "Menu_File_NewPak");
        SetHeader("MenuFileOpenPak", "Menu_File_OpenPak");
        SetHeader("MenuFileClose", "Menu_File_Close");
        SetHeader("MenuFileSave", "Menu_File_Save");
        SetHeader("MenuFileExit", "Menu_File_Exit");
        SetHeader("MenuAction", "Menu_Action");
        SetHeader("MenuActionExtractSel", "Menu_Action_ExtractSelected");
        SetHeader("MenuActionExtractFolder", "Menu_Action_ExtractFolder");
        SetHeader("MenuActionExtractAll", "Menu_Action_ExtractAll");
        SetHeader("MenuActionImport", "Menu_Action_Import");
        SetHeader("MenuActionRevertSel", "Menu_Action_RevertSelected");
        SetHeader("MenuActionRevertAll", "Menu_Action_RevertAll");
        SetHeader("MenuView", "Menu_View");
        SetHeader("MenuViewStats", "Menu_View_Stats");
        SetHeader("MenuPreferencesItem", "Menu_Preferences");
        SetHeader("MenuLanguageItem", "Menu_Preferences_Language");

        // 工具栏
        SetContent("BtnOpen", "Toolbar_Open");
        SetContent("BtnExtract", "Toolbar_Extract");
        SetContent("BtnImport", "Toolbar_Import");
        SetContent("BtnSave", "Toolbar_Save");
        SetContent("BtnSearch", "Toolbar_Search");
        SetContent("BtnClear", "Status_Clear");

        // 搜索框
        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox != null) searchBox.Watermark = Lang.Get("Toolbar_Search");

        // TreeView右键菜单
        SetHeader("CtxNewFolder", "Ctx_NewFolder");
        SetHeader("CtxNewFile", "Ctx_NewFile");
        SetHeader("CtxCopyFolder", "Ctx_CopyFolder");
        SetHeader("CtxCutFolder", "Ctx_CutFolder");
        SetHeader("CtxPasteFolder", "Ctx_PasteToFolder");
        SetHeader("CtxDeleteFolder", "Ctx_DeleteFolder");
        SetHeader("CtxExtractFolder", "Ctx_ExtractFolder");

        // DataGrid列头
        var grid = this.FindControl<Avalonia.Controls.DataGrid>("EntryGrid");
        if (grid != null)
        {
            var headers = new[] { "Col_Name", "Col_Type", "Col_Ext", "Col_Size", "Col_Modified" };
            for (int i = 0; i < grid.Columns.Count && i < headers.Length; i++)
            {
                grid.Columns[i].Header = Lang.Get(headers[i]);
            }
        }

        // ViewModel属性
        if (VM != null)
        {
            VM.MenuPreferences = Lang.Get("Menu_Preferences");
            VM.StatusText = Lang.Get("Status_OpenHint");
        }
    }

    private void SetHeader(string name, string key)
    {
        var item = this.FindControl<MenuItem>(name);
        if (item != null) item.Header = Lang.Get(key);
    }

    private void SetContent(string name, string key)
    {
        var btn = this.FindControl<Button>(name);
        if (btn != null) btn.Content = Lang.Get(key);
    }

    // ===== 文件操作 =====

    private async void OnNewPak(object sender, RoutedEventArgs e)
    {
        // 弹出输入对话框获取PAK名称
        var nameInput = new TextBox { Text = "NewPak", Watermark = Lang.Get("Dlg_NewPak_Watermark"), Width = 200 };

        var cancelBtn = new Button { Content = Lang.Get("Dlg_Cancel"), Width = 70, Tag = false };
        var okBtn = new Button { Content = Lang.Get("Dlg_Confirm"), Width = 70, Tag = true };

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
        inputPanel.Children.Add(new TextBlock { Text = Lang.Get("Dlg_NewPak_Label") });
        inputPanel.Children.Add(nameInput);

        var dock = new DockPanel();
        dock.Children.Add(buttonPanel);
        dock.Children.Add(inputPanel);

        var dialog = new Window
        {
            Title = Lang.Get("Dlg_NewPak_Title"),
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
            Title = Lang.Get("Dlg_OpenPak_Title"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Lang.Get("Dlg_OpenPak_Filter"))
                {
                    Patterns = new[] { "*.pak" }
                },
                new FilePickerFileType(Lang.Get("Dlg_AllFiles"))
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
            var confirm = await ShowConfirmDialog(Lang.Get("Dlg_UnsavedConfirm"));
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
            Title = Lang.Get("Dlg_ExtractFile_Title"),
            SuggestedFileName = defaultName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(Lang.Get("Dlg_OriginalFormat")) { Patterns = new[] { "*" + ext } },
                new FilePickerFileType(Lang.Get("Dlg_AllFiles")) { Patterns = new[] { "*.*" } }
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
            Title = Lang.Get("Dlg_SelectOutputDir"),
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
            Title = Lang.Get("Dlg_SelectOutputDir"),
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
            Title = Lang.Get("Dlg_SelectImportFile"),
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
            Title = Lang.Get("Dlg_SavePak_Title"),
            SuggestedFileName = "NewPak.pak",
            FileTypeChoices = new[]
            {
                new FilePickerFileType(Lang.Get("Dlg_OpenPak_Filter")) { Patterns = new[] { "*.pak" } }
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
                Title = Lang.Get("Menu_View_Stats"),
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
            VM.StatusText = Lang.Get("Status_OpenFailed", ex.Message);
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
            VM.StatusText = Lang.Get("Status_FolderNotFound", folderPath);
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
            Title = Lang.Get("Dlg_ConfirmTitle"),
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
                            new Button { Content = Lang.Get("Dlg_Cancel"), Width = 80, Tag = false },
                            new Button { Content = Lang.Get("Dlg_Confirm"), Width = 80, Tag = true }
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
            VM.StatusText = Lang.Get("Status_RenameWIP");
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
