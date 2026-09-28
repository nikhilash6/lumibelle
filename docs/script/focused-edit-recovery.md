# Focused-edit parsing and recovery

`Revise · focused edits` uses `script-focused-v3` instructions for new requests. The
wire format remains version 1. Previously captured prompts and model responses are
not rewritten.

## Instructions

All IDs and offsets address the original TARGET simultaneously, not the result of
an earlier operation. To rewrite one block and add text immediately after it, use
one `replace` operation whose `blocks` contains the rewritten block followed by
the new blocks. An insertion or move must otherwise anchor to a surviving,
unedited original block. `after A` and `before B` name the same gap when A and B
are adjacent, so do not emit separate additions for both.

The format instructions include an example and a final conflict check. They do
not impose H3 timing or shot-generation constraints on screenplay writing.

## Supported normalization

After parsing the blocks, and before strict materialization, the parser can combine
one whole, single-source-block replacement with one insertion **after the same
original block**. It works independently of those operations' order in the JSON
array. Full offsets may be omitted or explicitly cover the complete original text.
A replacement may contain several output blocks; the inserted blocks follow all
of them. Text, whitespace, Unicode and emphasis are copied, not rewritten.

The parser examines the entire original operation set first. It does not normalize:

- Passage edits, partial replacements or multi-source-block replacements.
- Insertions before replaced blocks, or anchors deleted/moved by another edit.
- Competing insertions, including equivalent before/after boundaries, overlapping
  source ranges, or another operation producing output at the same boundary.
- Unknown IDs, invalid block shapes, unsupported operations, malformed JSON,
  duplicate JSON properties (including case variants), or incomplete/multiple
  fenced responses.

These cases remain errors. Operations are not dropped, anchor IDs are not guessed,
and malformed JSON is not reconstructed. The original 1,000-operation limit is
checked before normalization; existing final screenplay size, scope, boundary and
identity validation still applies. `Materialize` and `Apply` remain strict and do
not normalize already saved proposals.

A successful combination adds locally generated `NormalizationNotes` to the
parsed proposal. The field is omitted when unused, and notes supplied by a model
are not trusted. The review displays the original operation numbers that were
combined. The raw response remains available verbatim; normalized operations are
used for preview, comparison and explicit application.

## Previously saved failures

For a completed focused-edit response with the edited-anchor or duplicate-insertion
error, open **Requests → Open response → Reprocess saved response**. This parses the
original raw response again and saves a separate local proposal through the existing
JSON-correction path. No model call is made. A parsing failure saves no partial
proposal. Saved raw job output and the original failed history entry stay unchanged.

A failed history save can be retried without reparsing or submitting a model request.
An unsaved manual JSON draft is kept separately and is not silently substituted for
the original response. The existing correction-source label may say **Pasted JSON**
for this local proposal as well.

Cancelled, interrupted, failed and output-limited streams are not automatically
promoted through the reprocess action. Other response errors keep **Paste / edit
JSON** for explicit correction. Opening a response alone does not reprocess it.

The script changes only after **Apply changes**. A changed/deleted captured target
still blocks application, and an already applied proposal cannot be applied twice.

## Verification

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj -c Release --filter "FullyQualifiedName~ScriptEditNormalization|FullyQualifiedName~ScriptPolish|FullyQualifiedName~ScreenplayJson|FullyQualifiedName~ScriptComponentTests"
```

The new parser tests cover the reported replace/insert pair, scope/offset limits,
competing edits, Unicode/formatting preservation, raw-output retention, finished
versus truncated requests, and unchanged-target checks. Component tests cover the
reprocess action, notices, explicit application, stale targets, unsaved JSON drafts,
save failure/retry, and refusal to reprocess incomplete streams.
