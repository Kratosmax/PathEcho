using PathEcho.Core.Models;

namespace PathEcho.Services;

public enum SyncTaskActivity
{
    Idle,
    Running,
    Succeeded,
    Failed,
}

public sealed class SyncTaskRow : NotifyObject
{
    private string _status = "等待启动";
    private SyncTaskActivity _activity;
    private bool? _leftDirectoryAvailable;
    private bool? _rightDirectoryAvailable;

    public SyncTaskRow(SyncTaskDefinition definition)
    {
        Definition = definition;

    }

    public SyncTaskDefinition Definition { get; }
    public string Name => Definition.Name;
    public string LeftPath => Definition.LeftPath;
    public string RightPath => Definition.RightPath;
    public bool IsDirectoryStateKnown => _leftDirectoryAvailable.HasValue && _rightDirectoryAvailable.HasValue;
    public bool AreDirectoriesAvailable => _leftDirectoryAvailable == true && _rightDirectoryAvailable == true;
    public bool HasDirectoryIssue => _leftDirectoryAvailable == false || _rightDirectoryAvailable == false;
    public string DirectoryState => !IsDirectoryStateKnown ? "检查中" : HasDirectoryIssue ? "需要检查" : "可用";
    public string DirectoryStateDetail => !IsDirectoryStateKnown ? "正在检查目录可用性" : (_leftDirectoryAvailable == false, _rightDirectoryAvailable == false) switch
    {
        (true, true) => "源目录和目标目录均不可用",
        (true, false) => "源目录不可用",
        (false, true) => "目标目录不可用",
        _ => "源目录和目标目录均可用",
    };
    public string Mode => Definition.Mode switch
    {
        SyncMode.LeftToRight => "左 → 右",
        SyncMode.RightToLeft => "右 → 左",
        _ => "双向",
    };
    public string Deletion => Definition.DeletionMode switch
    {
        DeletionMode.Ignore => "不传播删除",
        DeletionMode.Propagate => "传播删除",
        _ => "删除前备份",
    };

    public string Status => _status;
    public SyncTaskActivity Activity => _activity;

    public void SetStatus(SyncTaskActivity activity, string text)
    {
        SetField(ref _activity, activity, nameof(Activity));
        SetField(ref _status, text, nameof(Status));
    }

    public bool ApplyDirectoryAvailability(bool leftAvailable, bool rightAvailable)
    {
        if (_leftDirectoryAvailable == leftAvailable && _rightDirectoryAvailable == rightAvailable)
        {
            return false;
        }

        _leftDirectoryAvailable = leftAvailable;
        _rightDirectoryAvailable = rightAvailable;
        OnPropertyChanged(nameof(IsDirectoryStateKnown));
        OnPropertyChanged(nameof(AreDirectoriesAvailable));
        OnPropertyChanged(nameof(HasDirectoryIssue));
        OnPropertyChanged(nameof(DirectoryState));
        OnPropertyChanged(nameof(DirectoryStateDetail));
        return true;
    }
}
