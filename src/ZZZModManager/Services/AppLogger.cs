using System.Text;
using System.Text.RegularExpressions;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

public interface IAppLogger
{
    IReadOnlyList<LogEntry> Entries { get; }
    void Info(string message);
    void Warning(string message);
    void Error(string message);
    void Reload();
}

public sealed record LogCleanupResult(
    int RemovedEntries,
    long BytesBefore,
    long BytesAfter,
    bool Succeeded,
    string? Error = null);

public sealed class AppLogger : IAppLogger
{
    public const int MaximumEntries = 1000;
    private const long MaximumLogBytes = 2L * 1024 * 1024;
    private const int CleanupWriteInterval = 64;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly Regex PersistedLevelRegex = new(
        @"^\[(?<level>Info|Warning|Error)\]\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly AppPaths _paths;
    private readonly List<LogEntry> _entries = [];
    private readonly object _sync = new();
    private readonly string _logFile;
    private int _writesSinceCleanup;

    public AppLogger(AppPaths paths)
    {
        _paths = paths;
        _paths.Ensure();
        _logFile = Path.Combine(_paths.LogsRoot, "manager.log");
        Reload();
    }

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToList();
            }
        }
    }

    public void Info(string message) => Write(AppLogLevel.Info, message);

    public void Warning(string message) => Write(AppLogLevel.Warning, message);

    public void Error(string message) => Write(AppLogLevel.Error, message);

    public void Reload()
    {
        Cleanup();
        lock (_sync)
        {
            _entries.Clear();
            if (!File.Exists(_logFile))
            {
                return;
            }

            try
            {
                foreach (var line in File.ReadLines(_logFile, Encoding.UTF8).TakeLast(MaximumEntries))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _entries.Add(Parse(line));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _entries.Add(new LogEntry(DateTimeOffset.Now, AppLogLevel.Warning, $"读取历史日志失败：{ex.Message}"));
            }
        }
    }

    public LogCleanupResult Cleanup()
    {
        lock (_sync)
        {
            return CleanupLocked();
        }
    }

    private void Write(AppLogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (_sync)
        {
            _entries.Add(entry);
            while (_entries.Count > MaximumEntries)
            {
                _entries.RemoveAt(0);
            }

            try
            {
                File.AppendAllText(_logFile, entry + Environment.NewLine, Utf8NoBom);
                _writesSinceCleanup++;
                if (_writesSinceCleanup >= CleanupWriteInterval
                    || new FileInfo(_logFile).Length > MaximumLogBytes)
                {
                    CleanupLocked();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_entries.Count == 0 || !_entries[^1].Message.StartsWith("写入日志失败", StringComparison.Ordinal))
                {
                    _entries.Add(new LogEntry(DateTimeOffset.Now, AppLogLevel.Warning, $"写入日志失败：{ex.Message}"));
                    while (_entries.Count > MaximumEntries)
                    {
                        _entries.RemoveAt(0);
                    }
                }
            }
        }
    }

    private LogCleanupResult CleanupLocked()
    {
        if (!File.Exists(_logFile))
        {
            _writesSinceCleanup = 0;
            return new LogCleanupResult(0, 0, 0, true);
        }

        var temporary = _logFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytesBefore = new FileInfo(_logFile).Length;
            var retained = new Queue<string>(MaximumEntries);
            var totalEntries = 0;
            foreach (var line in File.ReadLines(_logFile, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                totalEntries++;
                retained.Enqueue(line);
                while (retained.Count > MaximumEntries)
                {
                    retained.Dequeue();
                }
            }

            var newlineBytes = Utf8NoBom.GetByteCount(Environment.NewLine);
            var retainedBytes = retained.Sum(line => (long)Utf8NoBom.GetByteCount(line) + newlineBytes);
            while (retained.Count > 0 && retainedBytes > MaximumLogBytes)
            {
                var removed = retained.Dequeue();
                retainedBytes -= Utf8NoBom.GetByteCount(removed) + newlineBytes;
            }

            File.WriteAllLines(temporary, retained, Utf8NoBom);
            File.Move(temporary, _logFile, true);
            _writesSinceCleanup = 0;

            var bytesAfter = new FileInfo(_logFile).Length;
            return new LogCleanupResult(
                Math.Max(0, totalEntries - retained.Count),
                bytesBefore,
                bytesAfter,
                true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // Preserve the original cleanup failure.
            }

            return new LogCleanupResult(0, 0, 0, false, ex.Message);
        }
    }

    private static LogEntry Parse(string line)
    {
        if (line.Length >= 10 && line[0] == '[' && line[9] == ']'
            && TimeSpan.TryParse(line.AsSpan(1, 8), out var time))
        {
            var now = DateTimeOffset.Now;
            var timestamp = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hours, time.Minutes, time.Seconds, now.Offset);
            var payload = line.Length > 11 ? line[11..] : string.Empty;
            var levelMatch = PersistedLevelRegex.Match(payload);
            if (levelMatch.Success
                && Enum.TryParse<AppLogLevel>(levelMatch.Groups["level"].Value, ignoreCase: true, out var level))
            {
                return new LogEntry(timestamp, level, levelMatch.Groups["message"].Value);
            }

            // Logs written before levels were persisted remain valid information entries.
            return new LogEntry(timestamp, AppLogLevel.Info, payload);
        }

        return new LogEntry(DateTimeOffset.Now, AppLogLevel.Info, line);
    }
}
