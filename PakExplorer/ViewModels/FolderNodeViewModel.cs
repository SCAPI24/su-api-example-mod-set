using System.Collections.ObjectModel;
using System.Linq;

namespace PakExplorer.ViewModels;

/// <summary>
/// 左侧文件夹树的节点
/// </summary>
public class FolderNodeViewModel : ViewModelBase
{
    private string _name;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
    public string FullPath { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; }
    public FolderNodeViewModel Parent { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public FolderNodeViewModel(PakFile.PakFolder folder, FolderNodeViewModel parent = null)
    {
        Name = string.IsNullOrEmpty(folder.Name) ? "Content.pak" : folder.Name;
        FullPath = folder.FullPath;
        Children = new ObservableCollection<FolderNodeViewModel>();
        Parent = parent;

        foreach (var sub in folder.SubFolders.Values.OrderBy(x => x.Name))
        {
            Children.Add(new FolderNodeViewModel(sub, this));
        }
    }

    /// <summary>
    /// 根据最新的PakFolder结构刷新子节点
    /// </summary>
    public void Refresh(PakFile.PakFolder folder)
    {
        // 移除不再存在的子节点
        var existingNames = new System.Collections.Generic.HashSet<string>();
        foreach (var sub in folder.SubFolders.Values)
        {
            existingNames.Add(sub.Name);
        }

        for (int i = Children.Count - 1; i >= 0; i--)
        {
            if (!existingNames.Contains(Children[i].Name))
            {
                Children.RemoveAt(i);
            }
        }

        // 添加新子节点或递归刷新
        var currentNames = new System.Collections.Generic.HashSet<string>(
            Children.Select(c => c.Name));

        foreach (var sub in folder.SubFolders.Values.OrderBy(x => x.Name))
        {
            if (currentNames.Contains(sub.Name))
            {
                // 递归刷新已有节点
                var child = Children.First(c => c.Name == sub.Name);
                child.Refresh(sub);
            }
            else
            {
                // 插入新节点（保持排序）
                Children.Add(new FolderNodeViewModel(sub, this));
            }
        }
    }
}
