using PathEcho.Core.Backup;

namespace PathEcho.Services;

public static class HistoryDiagnosticPresentation
{
    public static string Summary(IEnumerable<HistoryScanReport> reports)
    {
        var items = reports.ToArray();
        if (items.Length == 0) return "尚未检查备份目录，可点击‘检查与导出诊断’。";
        var candidates = items.Sum(item => item.Summary.CandidateDirectories);
        var ready = items.Sum(item => item.Summary.ReadyVersions);
        var pending = items.Sum(item => item.Summary.AwaitingManifest);
        var content = items.Sum(item => item.Summary.AwaitingContent);
        var invalid = items.Sum(item => item.Summary.ManifestInvalid + item.Summary.IdentityMismatch + item.Summary.AccessDenied + item.Summary.OtherFailures);
        var text = $"发现 {candidates} 个快照目录 · {ready} 个可回档版本";
        if (pending > 0) text += $" · {pending} 个等待备份清单";
        if (content > 0) text += $" · {content} 个等待内容同步";
        if (invalid > 0) text += $" · {invalid} 个需要检查";
        if (items.Any(item => !item.IsComplete)) text += " · 检查未完成，不能据此判断备份不存在";
        if (candidates == 0 && items.All(item => item.IsComplete))
        {
            var issue = items.SelectMany(item => item.Issues).FirstOrDefault();
            text += issue is null ? " · 所选目录尚未发现历史" : " · " + Reason(issue.ReasonCode);
        }
        return text;
    }

    public static string Reason(string code) => code switch
    {
        "MANIFEST_MISSING" => "缺少 manifest.json，等待备份清单同步",
        "MANIFEST_INVALID_JSON" => "备份清单尚未写完或 JSON 格式损坏",
        "MANIFEST_INVALID_SCHEMA" => "备份清单格式或字段无效",
        "PROFILE_ID_MISMATCH" => "备份清单与所在档案目录的 ID 不匹配",
        "PROFILE_UNASSOCIATED" or "SOURCE_UNASSOCIATED" => "备份来源未与当前游戏关联",
        "OBJECT_MISSING" => "内容对象尚未同步到本机",
        "OBJECT_LENGTH_MISMATCH" => "内容对象大小不匹配，等待同步或校验",
        "ROOT_NOT_FOUND" => "当前配置的备份目录不存在",
        "ROOT_ACCESS_DENIED" or "MANIFEST_ACCESS_DENIED" or "OBJECT_ACCESS_DENIED" => "访问被拒绝，请检查目录权限",
        "MANIFEST_CLOUD_UNAVAILABLE" => "云盘清单暂时无法读取",
        "SOURCE_NOT_FOUND" => "没有找到已关联来源的备份目录",
        "LINK_SKIPPED" => "为避免读错位置，已跳过重定向链接",
        "CANCELED" => "检查已取消，保留不完整结果",
        "TIMEOUT" => "检查超时，保留不完整结果",
        "SCAN_INCOMPLETE" => "达到检查范围限制，结果不完整",
        "READY" => "清单与内容元数据可用，回档前仍将校验内容",
        _ => "目录或文件读取失败，可导出诊断查看阶段与错误码",
    };
}
