using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace YikzClipboard.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public sealed record LogEntry(long Id, DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public string Format()
    {
        return Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " +
            LevelTag(Level) + " [" + Category + "] " + Message;
    }

    public static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };
}

public interface ILog
{
    void Write(LogLevel level, string category, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string category, string message) => log.Write(LogLevel.Debug, category, message);

    public static void Info(this ILog log, string category, string message) => log.Write(LogLevel.Info, category, message);

    public static void Warn(this ILog log, string category, string message, Exception? ex = null) => log.Write(LogLevel.Warning, category, message, ex);

    public static void Error(this ILog log, string category, string message, Exception? ex = null) => log.Write(LogLevel.Error, category, message, ex);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
    }
}

public sealed partial class Logger : ILog, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly LogEntry[] _ring;
    private int _ringStart;
    private int _ringCount;
    private long _nextId;
    private readonly Channel<LogEntry>? _fileChannel;
    private readonly Task? _fileTask;
    private readonly string? _directory;
    private readonly string _baseName;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;

    public Logger(string? directory, int ringCapacity = 5000, long maxFileBytes = 2 * 1024 * 1024, int maxFiles = 5, string baseName = "yikz-clipboard")
    {
        _ring = new LogEntry[Math.Max(16, ringCapacity)];
        _directory = directory;
        _baseName = baseName;
        _maxFileBytes = maxFileBytes;
        _maxFiles = Math.Max(1, maxFiles);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
            _fileChannel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions { SingleReader = true });
            _fileTask = Task.Run(FileLoopAsync);
        }
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public string? LogDirectory => _directory;

    public string? CurrentFilePath => _directory == null ? null : Path.Combine(_directory, _baseName + ".log");

    public event Action<LogEntry>? EntryAdded;

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < MinimumLevel)
        {
            return;
        }
        var text = exception == null ? message : message + ": " + exception.GetType().Name + ": " + exception.Message;
        text = Redact(text);
        LogEntry entry;
        lock (_gate)
        {
            entry = new LogEntry(++_nextId, DateTimeOffset.Now, level, category, text);
            var index = (_ringStart + _ringCount) % _ring.Length;
            _ring[index] = entry;
            if (_ringCount < _ring.Length)
            {
                _ringCount++;
            }
            else
            {
                _ringStart = (_ringStart + 1) % _ring.Length;
            }
        }
        _fileChannel?.Writer.TryWrite(entry);
        try
        {
            EntryAdded?.Invoke(entry);
        }
        catch
        {
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<LogEntry>(_ringCount);
            for (var i = 0; i < _ringCount; i++)
            {
                list.Add(_ring[(_ringStart + i) % _ring.Length]);
            }
            return list;
        }
    }

    public void ClearBuffer()
    {
        lock (_gate)
        {
            _ringStart = 0;
            _ringCount = 0;
        }
    }

    public IReadOnlyList<string> LogFiles()
    {
        if (_directory == null)
        {
            return Array.Empty<string>();
        }
        var files = new List<string>();
        for (var i = _maxFiles - 1; i >= 1; i--)
        {
            var p = Path.Combine(_directory, _baseName + "." + i + ".log");
            if (File.Exists(p))
            {
                files.Add(p);
            }
        }
        var current = Path.Combine(_directory, _baseName + ".log");
        if (File.Exists(current))
        {
            files.Add(current);
        }
        return files;
    }

    public async Task FlushAsync()
    {
        if (_fileChannel == null)
        {
            return;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushWaiters.Enqueue(tcs);
        _fileChannel.Writer.TryWrite(FlushMarker);
        await Task.WhenAny(tcs.Task, Task.Delay(3000)).ConfigureAwait(false);
    }

    private static readonly LogEntry FlushMarker = new(-1, DateTimeOffset.MinValue, LogLevel.Debug, "", "");
    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> _flushWaiters = new();

    private async Task FileLoopAsync()
    {
        var reader = _fileChannel!.Reader;
        StreamWriter? writer = null;
        long size = 0;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var entry))
                {
                    if (ReferenceEquals(entry, FlushMarker))
                    {
                        writer?.Flush();
                        while (_flushWaiters.TryDequeue(out var w))
                        {
                            w.TrySetResult();
                        }
                        continue;
                    }
                    try
                    {
                        if (writer == null)
                        {
                            (writer, size) = OpenWriter();
                        }
                        var line = entry.Format();
                        writer.WriteLine(line);
                        size += Encoding.UTF8.GetByteCount(line) + 2;
                        if (size >= _maxFileBytes)
                        {
                            writer.Dispose();
                            writer = null;
                            Rotate();
                        }
                    }
                    catch
                    {
                        writer?.Dispose();
                        writer = null;
                    }
                }
                writer?.Flush();
            }
        }
        finally
        {
            writer?.Dispose();
            while (_flushWaiters.TryDequeue(out var w))
            {
                w.TrySetResult();
            }
        }
    }

    private (StreamWriter, long) OpenWriter()
    {
        var path = Path.Combine(_directory!, _baseName + ".log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return (new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false }, stream.Length);
    }

    private void Rotate()
    {
        var dir = _directory!;
        var oldest = Path.Combine(dir, _baseName + "." + (_maxFiles - 1) + ".log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (var i = _maxFiles - 2; i >= 1; i--)
        {
            var src = Path.Combine(dir, _baseName + "." + i + ".log");
            if (File.Exists(src))
            {
                File.Move(src, Path.Combine(dir, _baseName + "." + (i + 1) + ".log"), true);
            }
        }
        var current = Path.Combine(dir, _baseName + ".log");
        if (File.Exists(current))
        {
            if (_maxFiles > 1)
            {
                File.Move(current, Path.Combine(dir, _baseName + ".1.log"), true);
            }
            else
            {
                File.Delete(current);
            }
        }
    }

    [GeneratedRegex(@"yc_[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static string Redact(string text)
    {
        if (!text.Contains("yc_", StringComparison.Ordinal))
        {
            return text;
        }
        return TokenPattern().Replace(text, "yc_[redacted]");
    }

    public async ValueTask DisposeAsync()
    {
        if (_fileChannel != null)
        {
            _fileChannel.Writer.TryComplete();
            if (_fileTask != null)
            {
                await Task.WhenAny(_fileTask, Task.Delay(3000)).ConfigureAwait(false);
            }
        }
    }
}
