using System.Text;
using System.Text.Json;

namespace PathEcho.Core.Diagnostics;

public sealed class LocalDiagnosticJournal
{
    private readonly string _directory;
    private readonly int _maximumBytes;
    private readonly int _retainedFiles;
    private readonly int _memoryCapacity;
    private readonly Queue<DiagnosticEvent> _events = new();
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public LocalDiagnosticJournal(string directory, int maximumBytes = 2 * 1024 * 1024, int retainedFiles = 4, int memoryCapacity = 200)
    {
        _directory = System.IO.Path.GetFullPath(directory);
        _maximumBytes = Math.Max(1024, maximumBytes);
        _retainedFiles = Math.Clamp(retainedFiles, 1, 10);
        _memoryCapacity = Math.Clamp(memoryCapacity, 10, 2000);
        try
        {
            var current = System.IO.Path.Combine(_directory, "diagnostics.jsonl");
            if (File.Exists(current) && new FileInfo(current).Length <= _maximumBytes)
            {
                foreach (var line in File.ReadLines(current))
                {
                    try
                    {
                        var item = JsonSerializer.Deserialize<DiagnosticEvent>(line);
                        if (item is null) continue;
                        _events.Enqueue(item);
                        while (_events.Count > _memoryCapacity) _events.Dequeue();
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

    }

    public void Append(DiagnosticEvent item, bool periodic = false, string? stateKey = null)
    {
        try
        {
            lock (_gate)
            {
                var bounded = item with
                {
                    AppVersion = Limit(item.AppVersion, 128),
                    Stage = Limit(item.Stage, 128),
                    Result = Limit(item.Result, 64),
                    ReasonCode = item.ReasonCode is null ? null : Limit(item.ReasonCode, 128),
                    Path = item.Path is null ? null : Limit(item.Path.Replace('\r', ' ').Replace('\n', ' '), 2048),
                    ErrorType = item.ErrorType is null ? null : Limit(item.ErrorType, 128),
                    Counters = item.Counters.Take(32).ToDictionary(pair => Limit(pair.Key, 64), pair => pair.Value),
                };
                if (periodic && stateKey is not null)
                {
                    var state = JsonSerializer.Serialize(new
                    {
                        bounded.Stage,
                        bounded.Result,
                        bounded.ReasonCode,
                        bounded.ConfigRevision,
                        bounded.Path,
                        bounded.ErrorType,
                        bounded.HResult,
                        Counters = bounded.Counters.OrderBy(pair => pair.Key).ToArray()
                    });
                    if (_states.TryGetValue(stateKey, out var previous) && previous == state) return;
                    if (_states.Count >= 200 && !_states.ContainsKey(stateKey)) _states.Remove(_states.Keys.First());
                    _states[stateKey] = state;
                }
                _events.Enqueue(bounded);
                while (_events.Count > _memoryCapacity) _events.Dequeue();
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bounded) + "\n");
                if (bytes.Length > _maximumBytes) return;
                try
                {
                    Directory.CreateDirectory(_directory);
                    var path = System.IO.Path.Combine(_directory, "diagnostics.jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > _maximumBytes)
                    {
                        var oldest = path + "." + _retainedFiles;
                        if (File.Exists(oldest)) File.Delete(oldest);
                        for (var index = _retainedFiles - 1; index >= 1; index--)
                        {
                            if (File.Exists(path + "." + index)) File.Move(path + "." + index, path + "." + (index + 1));
                        }
                        File.Move(path, path + ".1");
                    }
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    stream.Write(bytes);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Keep the in-memory evidence. Logging failures never enter the business pipeline.
                }
            }
        }
        catch { /* A malformed event or a failing diagnostic sink must not break the application. */ }
    }

    public IReadOnlyList<DiagnosticEvent> Snapshot()
    {
        lock (_gate) { return _events.ToArray(); }
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
