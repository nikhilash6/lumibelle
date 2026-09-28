using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace lumibelle.Services.AI;

public interface IClaudeCodeProcess : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken ct);
    void CloseInput();
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);
    Task<int> WaitForExitAsync(CancellationToken ct);
    bool HasExited { get; }
    // First diagnostic line, kept in memory only to explain a failed start.
    string? Diagnostic { get; }
}
public interface IClaudeCodeProcessFactory
{
    IClaudeCodeProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);
}

public sealed class ClaudeCodeProcessFactory : IClaudeCodeProcessFactory
{
    public IClaudeCodeProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        (string File, string? Script) resolved;
        try { resolved = ResolveExecutable(executable); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new AiGenerationException("The Claude Code executable path is invalid. Choose an installed CLI executable."); }
        var info = StartInfo(resolved, arguments, workingDirectory);
        try { return new ClaudeCodeProcess(Process.Start(info) ?? throw new InvalidOperationException()); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new AiGenerationException("Could not start Claude Code. Install the CLI or choose its executable in Connections."); }
    }
    internal static ProcessStartInfo StartInfo((string File, string? Script) resolved, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(resolved.File)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };
        // The environment is inherited, so Claude Code authenticates as the user set it up
        // (plan sign-in, API key, or a cloud provider), minus any parent Claude Code
        // session's own variables. The connection check uses the same environment.
        if (info.Environment.TryGetValue("CLAUDECODE", out var nested) && nested == "1") RemoveSessionEnvironment(info.Environment);
        if (resolved.Script is not null) info.ArgumentList.Add(resolved.Script);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    // Provider configuration that a user may set; it passes through even when nested.
    private static readonly HashSet<string> ProviderEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_SKIP_BEDROCK_AUTH",
        "CLAUDE_CODE_SKIP_VERTEX_AUTH", "CLAUDE_CODE_SKIP_FOUNDRY_AUTH", "CLAUDE_CODE_OAUTH_TOKEN"
    };
    // When Lumibelle itself runs inside a Claude Code session, requests must not attach to
    // that session's IDs, messaging or local gateway; they run as from a normal terminal.
    internal static void RemoveSessionEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.ToArray())
            if (name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase) || name.Equals("CLAUDE_PID", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CLAUDE_AGENT_SDK_VERSION", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) && !ProviderEnvironment.Contains(name))
                environment.Remove(name);
        if (environment.TryGetValue("ANTHROPIC_BASE_URL", out var gateway) && Uri.TryCreate(gateway, UriKind.Absolute, out var uri) && uri.IsLoopback)
            environment.Remove("ANTHROPIC_BASE_URL");
    }
    public static (string File, string? Script) ResolveExecutable(string configured)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"];
        var candidates = string.IsNullOrWhiteSpace(configured)
            ? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => dir.Trim('"')).Append(Path.Combine(home, ".local", "bin")).Append(Path.Combine(home, ".claude", "local"))
                .SelectMany(dir => names.Select(name => Path.Combine(dir, name)))
            : [Path.GetFullPath(configured)];
        foreach (var path in candidates.Where(File.Exists))
        {
            if (!OperatingSystem.IsWindows() || Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return (path, null);
            // npm shims cannot be launched without a shell. Current packages install a
            // per-platform native binary; older ones run a script with Node.js.
            var scope = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@anthropic-ai");
            var package = Path.Combine(scope, "claude-code");
            var platform = "claude-code-win32-" + (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");
            var binary = new[] { package, Path.Combine(package, "node_modules", "@anthropic-ai", platform), Path.Combine(scope, platform) }
                .SelectMany(root => new[] { Path.Combine(root, "bin", "claude.exe"), Path.Combine(root, "claude.exe") })
                .FirstOrDefault(File.Exists);
            if (binary is not null) return (binary, null);
            var script = Path.Combine(package, "cli.js");
            var node = new[] { Path.Combine(Path.GetDirectoryName(path)!, "node.exe") }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(dir => Path.Combine(dir.Trim('"'), "node.exe")))
                .FirstOrDefault(File.Exists);
            if (File.Exists(script) && node is not null) return (node, script);
        }
        throw new AiGenerationException($"Claude Code CLI was not found. Install Claude Code {ClaudeCodeClient.MinimumVersion} or later, then check the executable path.");
    }
}

internal sealed class ClaudeCodeProcess : IClaudeCodeProcess
{
    private readonly Process _process;
    private readonly Task _errors;
    private int _disposed;
    public string? Diagnostic { get; private set; }
    public ClaudeCodeProcess(Process process)
    {
        _process = process;
        // Drain diagnostics without retaining prompts, paths, or credentials in logs.
        _errors = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    if (Diagnostic is null && !string.IsNullOrWhiteSpace(line)) Diagnostic = line.Length > 300 ? line[..300] + "…" : line;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
        });
    }
    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        try { await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct); await _process.StandardInput.FlushAsync(ct); }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        { throw new AiGenerationException("Claude Code exited before accepting the request. Check the connection and retry explicitly."); }
    }
    public void CloseInput()
    {
        try { _process.StandardInput.Close(); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
    }
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            string? line;
            try { line = await _process.StandardOutput.ReadLineAsync(ct); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { yield break; }
            if (line is null) yield break;
            yield return line;
        }
    }
    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }
    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        try { await _errors.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (TimeoutException) { }
        return _process.ExitCode;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        try { await _errors.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        _process.Dispose();
    }
}
