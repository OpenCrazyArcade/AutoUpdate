using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// Writes the launcher's log to a file beside its configuration.
///
/// A launcher has no console. Until now everything it logged went to a debug listener nobody
/// reads, so when an update failed on somebody else's machine there was no way to find out what
/// it had actually done — only what the window said, which was not the same thing.
///
/// Deliberately plain: one line per entry, append-only, nothing to configure. The file is
/// truncated when it passes a few megabytes, because the alternative is a launcher that fills a
/// disk over a year of use.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private readonly string _path;
    private readonly Lock _lock = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);

            Append($"{Environment.NewLine}===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} launcher khởi động " +
                   $"(máy {Environment.MachineName}) =====");
        }
        catch { /* a launcher that cannot log still has to start */ }
    }

    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    public void Dispose() { }

    private void Append(string line)
    {
        lock (_lock)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }

    private sealed class Writer(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception error,
                                Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(level)) return;

            string shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                .Append("  ")
                .Append(level.ToString().ToUpperInvariant().PadRight(5))
                .Append("  ")
                .Append(shortCategory)
                .Append("  ")
                .Append(formatter(state, error));

            if (error is not null) sb.Append(Environment.NewLine).Append("        ").Append(error);

            owner.Append(sb.ToString());
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
