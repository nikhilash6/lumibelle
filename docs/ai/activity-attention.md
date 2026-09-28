# AI activity: attention rather than completion count

The navigation badge and **Needs attention** tab share `AiActivityPolicy.NeedsAttention`.
They are an exception inbox, not a count of all generated work awaiting a second review.

## Counts

- An unread `NeedsAttention` request counts, including failed text assistance, generation,
  model checks, and output-transfer/recovery errors.
- An unread `Completed` request with a nonblank error also counts. A successful completed
  request does not count, even when its persisted `Unread` flag is true.
- `RemoteUnconfirmed` always counts, including a request cancelled locally or already
  marked read. Only remote-state reconciliation can clear this condition.
- Running/queued progress, successful results, confirmed cancellations, acknowledged
  errors, and cleared history do not count otherwise.

The existing Active and blocked indicators, provider reservations, cancellation,
recovery actions, generated media, raw responses, and request routing are unchanged.

## Review and acknowledgement

Existing studio reviews still use the visibility- and version-aware result observer.
Reading an error there, inspecting its response in activity, or choosing **Mark read**
removes its notification once the read acknowledgement has been saved. This does not
resolve the error, apply a proposal, delete anything, or cancel the remote job.
Acknowledged issues remain available in History with their recovery actions.

**Acknowledge alerts** in Needs attention affects the selected project scope across
pages. It does not mark successful results read. The History tab retains **Mark all
read**, **Clear history**, **Show cleared**, and restore/undo behavior. Merely opening
AI activity or following a Review link does not acknowledge a result.

An expanded response stays visible until collapsed even if its acknowledgement removes
it from the attention count; this is the existing inspection behavior.

## Existing installations

No migration or bulk flag reset is needed. Historical successful requests disappear from
the badge immediately after the new UI loads. Their `Unread` flags are intentionally
retained for the studios' existing result-discovery and review flows. The success path
may still set that flag; it no longer creates a global attention alert.

A later failure or remote uncertainty on a new job version can raise a new alert. The
unchanged store's observed-version check prevents an old review from clearing it.

## Validation

Run the policy/component regressions and existing activity/acknowledgement/store tests:

```powershell
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj -c Release --filter "FullyQualifiedName~AiActivity|FullyQualifiedName~AiJobCoordinator"
```

Browser smoke check: open a previously busy project, confirm normal completions are in
History without an attention badge, fail one request, review its visible error in its
studio, and verify the alert disappears. Keep a locally cancelled/unconfirmed ComfyUI
request visible until Check status confirms it stopped. Hidden reviews must not mark
results read. Confirm proposal Apply/Discard and generation review still work normally.
