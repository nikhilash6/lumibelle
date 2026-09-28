using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace lumibelle.Services.AI;

public interface ICodexTransport : IAsyncDisposable
{
    event Action<JsonElement>? Notification;
    bool Alive { get; }
    Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct);
    Task NotifyAsync(string method, object? parameters, CancellationToken ct);
}
public interface ICodexTransportFactory
{
    Task<ICodexTransport> StartAsync(string executable, CancellationToken ct);
}

public sealed class CodexTransportFactory : ICodexTransportFactory
{
    public Task<ICodexTransport> StartAsync(string executable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string resolved;
        try { resolved = ResolveExecutable(executable); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new AiGenerationException("The Codex executable path is invalid. Choose an installed CLI executable."); }
        var info = StartInfo(resolved);
        try { return Task.FromResult<ICodexTransport>(new CodexTransport(Process.Start(info) ?? throw new InvalidOperationException())); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new AiGenerationException("Could not start Codex. Install the CLI or choose its executable in Connections."); }
    }
    internal static ProcessStartInfo StartInfo(string resolved)
    {
        var info = new ProcessStartInfo(resolved)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath()
        };
        info.ArgumentList.Add("app-server"); info.ArgumentList.Add("--listen"); info.ArgumentList.Add("stdio://");
        foreach (var pair in CodexClient.IsolationConfig.Where(p => !CodexClient.ImageToolFeatures.Contains(p.Key)))
        { info.ArgumentList.Add("-c"); info.ArgumentList.Add(pair.Key + "=" + JsonSerializer.Serialize(pair.Value)); }
        // Auth remains owned by Codex. The environment is inherited unchanged, so Codex
        // uses the account the user set up, as it would in a terminal.
        return info;
    }
    public static string ResolveExecutable(string configured)
    {
        var candidates = string.IsNullOrWhiteSpace(configured)
            ? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).SelectMany(dir =>
                OperatingSystem.IsWindows() ? new[] { Path.Combine(dir.Trim('"'), "codex.exe"), Path.Combine(dir.Trim('"'), "codex.cmd") }
                    : new[] { Path.Combine(dir, "codex") }) : new[] { Path.GetFullPath(configured) };
        foreach (var path in candidates.Where(File.Exists))
        {
            if (!OperatingSystem.IsWindows() || Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return path;
            // npm shims cannot be launched directly without a shell. Resolve the packaged binary.
            var root = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@openai", "codex");
            var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            var triple = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
            foreach (var package in new[] { Path.Combine(root, "node_modules", "@openai", "codex-win32-" + architecture), Path.Combine(Path.GetDirectoryName(root)!, "codex-win32-" + architecture) })
            {
                var binary = Path.Combine(package, "vendor", triple, "bin", "codex.exe");
                if (File.Exists(binary)) return binary;
            }
        }
        throw new AiGenerationException("Codex CLI was not found. Install Codex 0.153.4 or later, then check the executable path.");
    }
}

internal sealed class CodexTransport : ICodexTransport
{
    private readonly Process? _process;
    private readonly StreamReader _output;
    private readonly StreamWriter _input;
    private readonly Func<ValueTask> _close;
    private readonly SemaphoreSlim _write = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _reader, _errors;
    private long _id;
    public event Action<JsonElement>? Notification;
    private bool _disposed, _disconnected;
    public bool Alive => !_disposed && !_disconnected && _process?.HasExited != true;
    internal CodexTransport(StreamReader output, StreamWriter input, Func<ValueTask> close)
    {
        _output = output; _input = input; _close = close;
        _reader = ReadAsync(); _errors = Task.CompletedTask;
    }
    public CodexTransport(Process process) : this(process.StandardOutput, process.StandardInput, () =>
    {
        if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        return ValueTask.CompletedTask;
    })
    {
        _process = process;
        // Drain diagnostics without retaining prompts, paths, or credentials in application logs.
        _errors = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is not null) { } });
    }
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        if (!Alive) throw new AiGenerationException("Codex is disconnected. Retry explicitly to start a new request.");
        var id = Interlocked.Increment(ref _id);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, ct);
            return await completion.Task.WaitAsync(ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    public Task NotifyAsync(string method, object? parameters, CancellationToken ct) => WriteAsync(new { method, @params = parameters }, ct);
    private async Task WriteAsync(object message, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try { await _input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct); await _input.FlushAsync(ct); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { throw new AiGenerationException("The Codex process disconnected. Retry explicitly; no request was repeated."); }
        finally { _write.Release(); }
    }
    private async Task ReadAsync()
    {
        try
        {
            while (await _output.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (root.TryGetProperty("method", out _))
                {
                    if (root.TryGetProperty("id", out var serverId))
                    {
                        // Never leave a server approval or elicitation awaiting an invisible UI.
                        await WriteAsync(new { id = serverId.Clone(), error = new { code = -32601, message = "Lumibelle does not support interactive tool or permission requests." } }, CancellationToken.None);
                        Notification?.Invoke(JsonSerializer.SerializeToElement(new { method = "lumibelle/interactiveRequest", @params = root.TryGetProperty("params", out var parameters) ? parameters.Clone() : JsonSerializer.SerializeToElement(new { }) }));
                    }
                    else Notification?.Invoke(root.Clone());
                }
                else if (root.TryGetProperty("id", out var id) && id.TryGetInt64(out var key) && _pending.TryGetValue(key, out var pending))
                {
                    if (root.TryGetProperty("error", out var error)) pending.TrySetException(CodexClient.Failure(error));
                    else pending.TrySetResult(root.GetProperty("result").Clone());
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        finally
        {
            _disconnected = true;
            foreach (var pending in _pending.Values) pending.TrySetException(new AiGenerationException("Codex disconnected. Inspect saved results and retry explicitly."));
            Notification?.Invoke(JsonSerializer.SerializeToElement(new { method = "lumibelle/disconnected", @params = new { } }));
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        await _close();
        await Task.WhenAll(_reader, _errors); _process?.Dispose();
    }
}
