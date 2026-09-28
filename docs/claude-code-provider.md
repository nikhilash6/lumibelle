# Claude Code provider

Lumibelle can use an installed Claude Code CLI for text assistance, including optional image inspection. It runs the CLI you installed with the account you set it up with: a Claude plan sign-in (Pro, Max, Team, Enterprise) counts toward that plan's usage limits, while a Console API key, organization or cloud provider (Amazon Bedrock, Google Cloud, Microsoft Foundry) is billed to that account. Lumibelle never signs in, reads credentials or tokens, or modifies the CLI. Claude Code cannot generate images; reference images still use ComfyUI or Codex. See [Claude Code authentication](https://code.claude.com/docs/en/authentication) and [legal and compliance](https://code.claude.com/docs/en/legal-and-compliance).

## Setup

1. Install Claude Code 2.1.259 or later. The native installer is recommended; see [Claude Code setup](https://code.claude.com/docs/en/setup) for other options.
   - Windows (PowerShell): `irm https://claude.ai/install.ps1 | iex`
   - macOS, Linux, WSL: `curl -fsSL https://claude.ai/install.sh | bash`
   - Or `winget install Anthropic.ClaudeCode` (Windows), `brew install --cask claude-code` (macOS), or `npm install -g @anthropic-ai/claude-code`.

   Open a new terminal and run `claude` once to sign in, as the same operating-system user that runs Lumibelle, or configure a cloud provider as described in [Claude Code authentication](https://code.claude.com/docs/en/authentication). The free Claude plan does not include Claude Code. **Connections → Claude Code → Install the Claude Code CLI** shows the command for the current system.
2. Open **AI settings → Connections → Claude Code**, enable the provider, and **Check connection**. Leave the executable path empty for detection (`PATH`, then `~/.local/bin`), or select the installed executable. Windows npm shims are resolved to the package's native binary (or its script, for older packages) without a shell.
3. Save the connection. The check runs `claude --version` and `claude auth status`; it generates no content. It shows the API provider, account and sign-in method that requests will use. A cloud-provider setup is accepted without an Anthropic sign-in.
4. In **Text models**, star the models you want. The catalog lists the **Fable**, **Opus**, **Sonnet** and **Haiku** aliases, which Claude Code resolves for the configured provider (cloud providers may offer older versions or not every family). Each studio's model picker includes the starred choices and a reasoning-effort selector.
   Under **Claude Code defaults**, set **Default Claude Code reasoning effort** for text assistance across projects, or leave it on **Model default** so Claude Code chooses. Levels a model does not support fall back to the highest level it does support. Haiku has no effort selector.

LLM profiles take a named effort or **Model default**; temperature, output limits and thinking budgets are rejected, as for Codex.

## Queue and usage limits

Claude Code text requests use their own queue lane, with concurrency 1 by default and a configurable range of 1–4. Each request runs in its own CLI process with a fresh conversation; a process is never reused, because one process is one conversation.

To skip CLI startup (about a second), Lumibelle keeps spare processes started and waiting for input. Each spare is for one executable, model and effort, since those are fixed on its command line. A request takes a matching spare if one is still running, and otherwise starts cold; either way it starts a replacement spare for the next request. At launch, an enabled Claude Code gets one spare for its default model and effort. A spare unused for five minutes is closed, at most four are kept, and disabling Claude Code or shutting down closes them all. The safety check on the reported tools and MCP servers still runs on every request.

Claude Code does not report remaining usage to other applications, so Lumibelle shows no allowance meter and does not read undocumented usage endpoints. When a request ends because of a usage or rate limit (a plan's usage limit, or a rate limit that persists after Claude Code's own retries), it needs attention and the Claude Code lane pauses. **Resume** checks that the CLI is still set up; the next request shows whether the limit has reset. Other failures fail the request without pausing the lane.

Keep requests tied to work you are actively doing. Claude plan limits assume ordinary individual use of Claude Code.

## Process and data handling

Each request runs `claude -p` with streaming JSON input and output. Lumibelle writes the whole request as one user message: role-labelled sections and any explicitly selected, independently prepared images. It then closes the input. The command line fixes:

- `--tools ""` and `--strict-mcp-config`: no built-in tools and no MCP servers.
- `--safe-mode` and `--disable-slash-commands`: no CLAUDE.md, skills, plugins, hooks, custom agents or other customizations.
- `--no-session-persistence`: the conversation is not saved or resumable.
- `--permission-prompts none`: anything that would wait for an answer is denied.
- `--system-prompt`: a short Lumibelle instruction replaces Claude Code's coding-agent prompt.

Lumibelle checks the session's reported tools and MCP servers before accepting any text and stops the request if either is non-empty. The process runs in a per-request temporary directory that is removed afterwards.

The child process inherits Lumibelle's environment, so variables such as `ANTHROPIC_API_KEY` or `CLAUDE_CODE_USE_BEDROCK` apply exactly as they would in a terminal. The exception is Lumibelle itself running inside a Claude Code session (`CLAUDECODE=1`), for example from its terminal or a preview server. Then that session's own variables (`CLAUDECODE`, `CLAUDE_PID`, `CLAUDE_AGENT_SDK_VERSION`, and `CLAUDE_CODE_*` other than the Bedrock, Google Cloud and Foundry provider settings and `CLAUDE_CODE_OAUTH_TOKEN`) are removed, as is an `ANTHROPIC_BASE_URL` pointing at this machine, so requests do not attach to that session. The connection check runs with the same environment, so the provider and sign-in it reports are the ones requests use. Check again after changing the environment Lumibelle starts in.

Text is streamed from partial message events. The final result must extend the streamed text exactly; otherwise the request fails, and its saved partial response stays inspectable. The resolved model (for example `claude-sonnet-5`) is taken from the session's init event. Cancellation stops the whole process tree. As with other hosted providers, completed output is saved before parsing, and interrupted requests are never repeated automatically.

Standard error is read only for its first line, kept in memory, which explains a process that exits without a result. It is not written to application logs.

## Validation

Automated tests use a mocked CLI process and consume no subscription usage. Coverage includes the connection check (version, sign-in, cloud-provider setups, missing CLI, disabled), isolation flags, model and effort selection, the message format with images, streaming and final-result reconciliation, tool/MCP rejection, sign-in and usage-limit errors, unexpected exits, cancellation, effort inheritance through the chat client, settings and profile validation, the usage-limit lane pause and resume, spare reuse, expiry and the launch spare, and removal of a parent session's variables.

On 2026-09-26, Claude Code 2.1.281 was run signed out with the exact isolation flags. The init event reported no tools, no MCP servers and no skills, and aliases resolved to model IDs. The signed-out result was mapped to the sign-in message. Later that day the same CLI, by then signed in with a Claude plan, answered a few short test prompts through the client with Sonnet at low effort: text streamed and completed normally. The first request started cold and reached the init event after about 1,000 ms; later requests used a spare and reached it after about 40 ms. No spare processes remained after the client was disposed.

Persistent conversations, allowance display and image generation are outside this version.
