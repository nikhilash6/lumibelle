# Codex provider

Lumibelle can use an installed Codex CLI for text assistance and image Create/Edit. It uses the account the CLI is set up with: a ChatGPT sign-in uses the plan's included allowance, while an OpenAI API key, Amazon Bedrock or another configured provider is billed to that account. Lumibelle does not force a sign-in method or provider, and it does not buy credits or consume usage resets. Usage remains subject to the account's limits; see [Codex authentication](https://learn.chatgpt.com/docs/auth) and [pricing](https://learn.chatgpt.com/docs/pricing).

## Setup

1. Install Codex CLI 0.153.4 or later; see [Codex CLI](https://learn.chatgpt.com/codex/cli).
   - Windows: `npm install -g @openai/codex` (requires Node.js)
   - macOS, Linux: `curl -fsSL https://chatgpt.com/codex/install.sh | sh`, or the npm command above.

   Then run `codex login` (ChatGPT or an API key), or configure another provider in Codex, as the same operating-system user that runs Lumibelle. **Connections → Codex → Install the Codex CLI** shows the command for the current system.
2. Open **AI settings → Connections → Codex**, enable the provider, and **Check connection**. Leave the executable path empty for detection, or select the installed executable. Windows npm shims are resolved to the packaged native binary without a shell.
3. Save the connection. The check reports the CLI version, the account or provider in use, model capabilities, and for ChatGPT sign-ins the plan allowance; it generates no content. It fails only when Codex needs an OpenAI sign-in and has none.
4. In **Text models**, refresh Codex and star the models you want. Each studio's existing model picker includes these choices, **Use global default**, and a Codex reasoning-effort selector.
   Under **Codex defaults**, set **Default Codex reasoning effort** for text assistance across projects, or leave it on **Model default**. Studio pickers follow this setting until you choose an explicit effort; **Use settings default** clears that override. Unsupported efforts block requests until corrected. Image generation keeps its separate effort setting.
5. In **Image models**, select **Codex Images**, refresh models, choose the **Codex agent model** and **Agent reasoning effort**, and save. To use Codex for new image requests by default, choose it under **Image models → Defaults**. Codex selects the underlying image model; the App Server integration does not expose a Flare/Sunburst selector. These image settings are independent of text-assistance preferences.

For agent selection, start with Luna for clear image prompts and simple edits, Terra for more involved reference instructions, and Sol or Astra when interpreting the request needs substantial judgment. This is a workload-based recommendation using [OpenAI's general model guidance](https://learn.chatgpt.com/docs/models#choosing-astra-sol-terra-and-luna), not an image-quality benchmark. Start with the model's default reasoning effort. A stronger agent does not select a different image model.

Images support one base plus up to seven additional references for edits. Each reference retains its own crop. The requested aspect is sent as an instruction; actual returned dimensions are saved and shown without stretching or cropping the result. ComfyUI-only controls are hidden, with their drafts retained when switching back.

Optional image inspection in text assistance is still opt-in. The selected model must advertise image input. Only the explicitly selected, independently prepared images are attached; local media URLs are never sent.

## Queue and allowance

Text and images share one Codex lane, with concurrency 1 by default and a configurable range of 1–4. Each request or image candidate starts a fresh conversation, including **One more take**. The model, reasoning effort, account identity, prompt, destination, references and crops are captured before execution.

For ChatGPT sign-ins, Connections shows named usage buckets with used/remaining percentages, reset times and last check; AI activity provides a compact summary of every reported window. Windows use readable labels such as **Weekly** and **5-hour**. Only reported windows are shown, with no empty second row when an account has just one. Missing reset times and unavailable usage data have separate explanations. These percentages are not a count of remaining requests. Visible usage displays refresh every minute; connection checks, completed requests and provider notifications also update them. Other sign-ins have no plan allowance, so none is read or shown.

The allowance is shown for information only; Lumibelle does not block or pause requests based on it. Codex reports an exhausted limit itself by failing the turn with `usageLimitExceeded`. That request then needs attention and the Codex lane pauses. **Resume** confirms Codex is still connected, and the next request shows whether the limit has reset. Lumibelle never selects a different model automatically.

## Process and data handling

Image progress distinguishes agent preparation, image-tool execution, and agent follow-up. A ticking elapsed breakdown is also saved in each image's metadata. It uses App Server item start/end timestamps and turn duration when available, falling back to local observation when a timestamp is absent. Preparation includes prompt work and coordination; follow-up includes work between image calls and after the final call. Image time is the union of active image-call intervals, so concurrent calls are not double-counted. These are approximate elapsed times including waiting, not model compute measurements, and exclude Lumibelle queueing and saving. Codex does not provide a completion percentage or ETA through this integration. Missing lifecycle boundaries leave the split unavailable; older images retain their existing metadata without invented timings. Completion receipts preserve timing through publication recovery.

The adapter uses structured [App Server](https://learn.chatgpt.com/docs/app-server) messages over private stdio. When Codex is enabled, Lumibelle starts one child at launch with a connection check, so the first request does not wait for it. Any check, usage read or request keeps it open; after five minutes without use and with no request running, Lumibelle closes it, and the next use starts it again. It is also closed when Codex is disabled or Lumibelle shuts down. It does not manage the desktop app or other CLI processes.

Each operation uses a job staging directory, dedicated writing instructions, a read-only sandbox, no interactive approvals, and `ephemeral: true`. Lumibelle verifies that the returned thread is ephemeral with no persisted thread path. Shell, browser, MCP, plugins, apps, memory, agent and unrelated tool capabilities are disabled. Image generation and its Code Mode tool host are enabled only on image jobs. GPT-5.6 and later require that host to invoke the image tool. These three feature flags are set per thread and are omitted from process command-line overrides so text and image threads can use different tool settings. The MCP table is replaced as a whole; Lumibelle does not copy inherited server credentials into overrides.

Credentials stay managed by the CLI. Captures retain a hashed account identity to detect account changes, not authentication tokens. Native diagnostic stderr is drained without being retained in application logs.

Lumibelle's durable job history owns request receipts, partial text, native image outputs and publication records. Temporary Codex conversations are not recovery storage and do not populate its task history. Cancellation interrupts the owned turn and waits for completion; if its state is uncertain, Lumibelle stops its own child before releasing the slot. Other affected jobs fail visibly and require explicit retry.

Native image-generation events supply the output bytes. If an agent refines its result within a turn, each native image is saved separately and the final image is published as one take only after the turn completes successfully. The completion receipt identifies the final native image so interrupted publication can recover it without another generation. A prose claim that an image was saved is not accepted. Completed image outputs are staged and validated before idempotent publication to Assets. Failed publication can retry saving without generating again; interrupted submissions never automatically repeat inference. Existing review, comparison, Trash, Undo and source relationships remain in use. Saved image details identify Codex, its agent model/version, reasoning effort, requested aspect, actual dimensions and revised prompt when provided. The underlying image model is shown as not reported by Codex; the native result does not identify Flare or Sunburst. Failed image turns flush the final text checkpoint so their diagnostic response is not truncated by streaming throttling. ComfyUI sampler/seed fields are not presented as Codex attribution.

## Validation

Automated tests use a mocked App Server transport and isolated fixtures; they consume no subscription allowance. Coverage includes protocol framing, ephemeral configuration, all five text operations, commentary/final-answer separation, reasoning preferences, model availability, account changes, quotas, cancellation, process failure, Create/Edit, independent crops, native-output validation, publication recovery, immutable additional candidates and Trash.

Mocked browser walkthroughs cover connection, model selection, enhancement and explicit Apply, image editing/inspection, comparison, additional takes, discard/Undo, quota pause/resume, navigation and mobile layouts. Existing queue, image, crop, Trash and studio regressions remain covered.

On 2026-09-09, live validation with CLI **0.153.4**, **gpt-5.6-luna**, and **medium** reasoning completed in **Lumibelle QA · studio overhaul**. A blue mug was generated, saved, and then edited to yellow using the saved image as the base. Both published images are **1254 × 1254**; their source relationship, prompts, agent attribution, and comparison view were verified in the rebuilt app.

The live test exposed and corrected disabled Code Mode image tools and rejection of an agent's intermediate refinements. Regression coverage checks per-thread tool isolation, complete failure text, preservation of native intermediates, publication only after a successful turn, and recovery of both legacy and numbered native outputs. Final validation passed **857 .NET tests** and **4 Codex browser scenarios**. Multiple independently cropped references remain covered by mocked tests; the live edit used one base image.

Timing validation on the same day used the selected **gpt-5.6-sol / low** agent in the QA project. A new blue-mug image completed with approximately **10 seconds preparation, 25 seconds in the image tool, and 2 seconds follow-up**. The live stage and timer were inspected, and the saved breakdown was verified after refreshing. Automated validation passed **862 .NET tests** on recheck and **4 Codex browser scenarios**, with the timing panel inspected on desktop and mobile. The first full-suite run encountered the existing intermittent image-review discard timeout; the complete recheck passed.

On 2026-09-26, after removing the forced ChatGPT sign-in and `openai` provider, a connection check against CLI **0.157.0** with a ChatGPT sign-in reported the account type, 7 models, image generation and the Codex allowance bucket. API-key, Bedrock and custom-provider setups, and allowance that no longer blocks requests, are covered by mocked tests; no live request was made with those setups.

Persistent Codex conversations, remote CLI hosts, account switching, automatic quota resumption and video generation are outside this version.
