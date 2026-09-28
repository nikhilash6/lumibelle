namespace lumibelle.Services;

// Warning/error diagnostics are shared by both hosts; writable logs never live in the install folder.
public sealed class ApplicationFileLoggerProvider(ApplicationPaths paths) : ILoggerProvider
{
    private readonly object gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private void Write(string message)
    {
        try { lock (gate) { Directory.CreateDirectory(paths.Logs); File.AppendAllText(Path.Combine(paths.Logs, $"lumibelle-{DateTime.UtcNow:yyyy-MM-dd}.log"), message + Environment.NewLine); } }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private sealed class FileLogger(ApplicationFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) owner.Write($"{DateTimeOffset.UtcNow:O} [{level}] {category}: {formatter(state, error)}{(error is null ? "" : Environment.NewLine + error)}"); }
    }
}
