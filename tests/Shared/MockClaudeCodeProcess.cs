using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Services.AI;

namespace Lumibelle.Testing;

// Process mock: no CLI, credentials, network or subscription usage.
public sealed class MockClaudeCodeProcess : IClaudeCodeProcessFactory
{
    public string Version = "2.1.281", Email = "qa@example.test", AuthMethod = "claude.ai", ApiProvider = "firstParty", ResolvedModel = "claude-sonnet-5";
    public bool LoggedIn = true, StartFails;
    public string[] Chunks = ["Hello ", "there."];
    public object[]? Tools, McpServers;
    // Replaces the generated stream after system/init when set.
    public Func<IReadOnlyList<string>, IEnumerable<object>>? Script;
    public int? ExitCode;
    public string? Diagnostic;
    public TaskCompletionSource? Hold;
    public List<IReadOnlyList<string>> Starts { get; } = [];
    public List<string> Directories { get; } = [];
    public List<string> Inputs { get; } = [];
    public int Disposed;
    public bool AllExited;

    public IClaudeCodeProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (StartFails) throw new AiGenerationException("Could not start Claude Code. Install the CLI or choose its executable in Connections.");
        lock (Starts) { Starts.Add(arguments.ToArray()); Directories.Add(workingDirectory); }
        return new Run(this, arguments, Starts.Count - 1);
    }
    public static string Line(object value) => JsonSerializer.Serialize(value);
    public static object Delta(string text) => new { type = "stream_event", parent_tool_use_id = (string?)null, @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } } };
    public static object Thinking() => new { type = "stream_event", parent_tool_use_id = (string?)null, @event = new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "…" } } };
    public static object Result(string text, bool error = false, string stopReason = "end_turn", int? status = null) =>
        new { type = "result", subtype = "success", is_error = error, result = text, stop_reason = stopReason, api_error_status = status };

    private IEnumerable<string> Output(IReadOnlyList<string> arguments)
    {
        if (arguments.SequenceEqual(["--version"])) { yield return Version + " (Claude Code)"; yield break; }
        if (arguments.SequenceEqual(["auth", "status", "--json"]))
        { yield return Line(new { loggedIn = LoggedIn, authMethod = LoggedIn ? AuthMethod : "none", email = LoggedIn ? Email : null, apiProvider = ApiProvider }); yield break; }
        yield return Line(new { type = "system", subtype = "init", model = ResolvedModel, tools = Tools ?? [], mcp_servers = McpServers ?? [] });
        var events = Script?.Invoke(arguments) ?? Chunks.Select(Delta).Append(Result(string.Concat(Chunks)));
        foreach (var item in events) yield return item as string ?? Line(item);
    }

    // Index into Starts of each process that received a request.
    public List<int> Used { get; } = [];
    private sealed class Run(MockClaudeCodeProcess owner, IReadOnlyList<string> arguments, int index) : IClaudeCodeProcess
    {
        public string? Diagnostic => owner.Diagnostic;
        public Task WriteLineAsync(string line, CancellationToken ct) { lock (owner.Inputs) { owner.Inputs.Add(line); owner.Used.Add(index); } return Task.CompletedTask; }
        public void CloseInput() { }
        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var first = true;
            foreach (var line in owner.Output(arguments))
            {
                ct.ThrowIfCancellationRequested();
                yield return line;
                if (first && owner.Hold is { } hold) await hold.Task.WaitAsync(ct);
                first = false;
            }
        }
        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(owner.ExitCode ?? (owner.LoggedIn ? 0 : 1));
        public bool Exited;
        public bool HasExited => Exited || owner.AllExited;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposed); Exited = true; return ValueTask.CompletedTask; }
    }
}
