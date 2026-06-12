using System.Collections.ObjectModel;
using System.Linq;

namespace PakExplorer.ViewModels;

/// <summary>
/// 左侧文件夹树的节点
/// </summary>
public class FolderNodeViewModel : ViewModelBase
{
    public string Name { get; }
    public string FullPath { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; }

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

    public FolderNodeViewModel(PakFile.PakFolder folder)
    {
        Name = string.IsNullOrEmpty(folder.Name) ? "Content.pak" : folder.Name;
        FullPath = folder.FullPath;
        Children = new ObservableCollection<FolderNodeViewModel>();

        foreach (var sub in folder.SubFolders.Values.OrderBy(x => x.Name))
        {
            Children.Add(new FolderNodeViewModel(sub));
        }
    }
}
