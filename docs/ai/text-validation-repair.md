# Text-only validation repair

## Use

For a completed AI response that failed validation, choose **Fix validation** in
its request details/review, or **AI activity → Inspect response**. The latter
also exposes this action for queued Script responses. This is a new request to
the same captured text model; normal provider charges apply. It is never started
just by opening a response.

The application sends the complete failed response, the current validation error,
and a compact output schema/rules block with authoritative textual facts. It does
not resend the original messages, scene/nearby-shot history, image attachments,
RefMod inspection PNGs, video, audio, settings object or global LoRA catalog.
The source response itself and relevant authored labels/triggers are not redacted.

Supported in v1:

- Shot prompt composition: H3 prompt + reference-use explanation.
- Character/environment reel composition: H3 prompt + use guidance.
- Image prompt enhancement and guidance suggestions.
- Structured Script responses, including focused edits. Discussion is excluded.

Shot breakdown and asset extraction are not covered in v1. Media generation,
model setup/network failures, arbitrary manually edited drafts and imported local
Script history without the original queue request do not use this action.

## Context and constraints

The original typed task stays in the new queue snapshot for the *existing local
parser* and the normal application guards. Only the new `Messages` are sent to
the provider. Thus a repair can validate against source-image identities without
sending their bytes or opening those image files again.

H3 repair facts contain the exact dialogue/language/order, stable speaker/audio
mapping, selected Picture/Video/Audio labels, actual generated duration and sound
directions. Sparse RefMods keep Video labels. Script repairs additionally need
the captured TARGET blocks, IDs and offsets; they omit the separate whole-script
rendering and attached discussion. This context can still be substantial.
Protected image-prompt triggers are retained exactly.

Repair instructions require minimal corrections and prohibit new visual analysis,
new story decisions, changed reference selections and discarded proposed edits.
This is instruction, not a guarantee of semantic equivalence: inspect the result.
The same production validators run afterwards. No validator is relaxed to make
repair output pass.

If the original raw output now passes the current parser (for example, a formerly
invalid focused-edit pair is handled by the local normalizer), the action directs
the user to local reprocessing instead of paying for another request. Existing
manual response editing and local H3 sound-section recovery remain available.

## Results and review

Each repair gets a new queue/request ID, with a parent and root repair link. Both
raw outputs, the submitted contract and normal provider usage remain available.
Script repairs also get a new proposal/run identity while retaining their exact
captured script target.

Repairs NEVER automatically apply on completion or recovery, including initial
shot/reel prompts that ordinary requests may apply automatically. **Review repair**
opens the usual studio review. Applying still checks current targets and media.

Shot/reel repair admission connects only the current review pointer. It does not
change prompt text, guidance, selected references or generation settings. Changed
or dismissed reviews cannot be replaced. If the target changed, the correction
remains inspectable in AI activity; copy it manually or compose for current inputs.
An acknowledged failed intermediate repair may be replaced only by explicit action.

## Submission, cancellation and recovery

Capture reads the original queue snapshot/result rather than accepting arbitrary
raw text/error/schema supplied by a browser. It refuses active, cancelled,
remote-unconfirmed, interrupted, empty or output-limited sources. Nonempty H3
`needsInput` questions must be answered, not "repaired" into invented facts.
OpenRouter/Codex require a normal `stop`; old completed ComfyUI receipts may omit
the finish reason. Only validation failures with `GenerateAgain` recovery qualify;
output-save/download recovery is handled by the existing no-inference retry path.

Enqueue and review-pointer saving are separate. A failed/uncertain enqueue keeps
the same request ID and snapshot. **Retry connecting repair** retries a local save,
not a second provider request. Reopening the source discovers an existing repair.
An active/successful repair cannot be silently duplicated. After a terminal failed
or cancelled child, **Try another text-only repair** explicitly starts a new attempt
against the original failed response. Alternatively, review a completed invalid
correction and fix that new error. There are no automatic repair loops.

Normal queue leases, cancellation, durable output receipts and text-worker recovery
remain in use. Recovering a saved correction never repeats image inspection or
paid inference. No video/image generation is triggered by this feature.

## Compatibility and bounds

No new dependency, provider, ComfyUI node or storage migration is required. The
optional `AiTextJobRequest.Repair` field is omitted from legacy serialized requests.
Ordinary vision requests still require their complete original image payload.
Only a validated repair envelope permits a text-only transport with a vision task.
Captured repair rule prose is retained; authoritative schema/facts are checked
against the saved local task, not a newly read project or a new original prompt.

Responses and required repair contracts are each limited to 500,000 characters;
validation reports to 20,000. Oversized requests are rejected, never truncated.
The captured model/provider and usual generation limits/defaults remain in force.
Removing vision input is expected to reduce input work, but latency, cost and
repair success have not been benchmarked by this patch.

## Validation checklist

Build and run tests in an isolated checkout/library:

```powershell
dotnet build src/Lumibelle.Web/Lumibelle.Web.csproj -c Release
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj -c Release --filter "FullyQualifiedName~Repair|FullyQualifiedName~AiTextJob|FullyQualifiedName~AiActivity"
```

1. Open a saved invalid H3 prompt with image inputs. Merely opening must not enqueue.
   Fix it; inspect the child request and verify zero media attachments plus the
   expected dialogue, reference labels, schema and parent identity.
2. Correct a missing speaker mapping and a malformed reel JSON response. Review
   both repaired fields, then explicitly Apply. Check that the source raw text is
   unchanged and the normal strict validators still reject altered dialogue.
3. Edit the current prompt/recipe while inspecting an older failure. Fixing its
   saved text may complete, but review/application must not overwrite newer edits.
4. Interrupt a completed repair's result save; retry/recover it without another
   provider call. Simulate a review-pointer save failure; reconnect the same child.
5. Cancel a repair. No automatic retry should occur. Explicitly request another
   attempt and verify a new ID only for that conscious new request.
6. In AI activity, Review repair must close the drawer before navigation. In the
   generic Assist request dialog, it must close that dialog before opening review.
7. Verify ordinary (non-repair) vision requests, local Script reprocessing, ordinary
   initial prompt application and existing cancellation/recovery remain unchanged.

The delivered source fixture checks are not a .NET build or an application runtime
round trip. See the accompanying bundle validation report for executed checks.
