# SQLite media lookup index

Large galleries previously loaded, deserialized, validated and copied the complete
`assets.json` or `shots.json` for every individual image/video/frame request.
`SqliteMediaIndex` now provides keyed lookups for active asset images and shot takes.
It is used by the shared Core stores, including the web and desktop media handlers.

This is an incremental performance refactor, not a conversion of all project storage
to SQLite. Project JSON remains authoritative; revision checks, writes, recovery,
Trash expiry, package import/export, and actual media files retain their existing
behavior. Full document loading and editing still use the existing stores. Reels
already have separate small media manifests and are unaffected.

## Storage and invalidation

- Database: `<data-directory>/cache/media-index-v1.sqlite`, outside project folders.
- Rows: compact image locations/content types/timestamps, and individual captured
  take records needed for video and lossless frame reads. Media bytes stay in files.
- A changed manifest replaces all its indexed rows in one transaction, so moved,
  trashed or removed items cannot remain available through their previous active URLs.
- Each lookup checks source length, last-write time and creation time. The first
  lookup of each source version in a process also verifies SHA-256, including when
  reopening a restored file with preserved timestamps. A changed Core assembly
  causes domain validation to run again before rebuilding rows.
- Manual in-place edits that preserve *all* file metadata while the app is running
  require restarting the app. Normal atomic app saves change the file version.
- Rebuilding reads through the existing validation rules; malformed source JSON
  still raises the usual store error. It is never repaired from the cache.
- Simultaneous cold requests share a rebuild lock. SQLite entries and their source
  version commit together. Canceled rebuilds roll back.
- Damaged databases/JSON payloads rebuild automatically. A locked or unwritable
  cache logs a warning and falls back to the original reads until app restart.
- The database is disposable. With the app closed, its `.sqlite`, `-wal` and `-shm`
  files can be removed together; the next media request recreates the index. Include
  project JSON and media in backups; the index is not required for restoration.

SQLite uses private, short-lived connections and WAL mode. The provider's operations
are synchronous; SQL transactions do not span external media work. See Microsoft's
[SQLite async guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)
and [concurrency guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors).

## Validation and measured effect

2026-09-22, Windows, Debug builds, a local project: 16 assets, 51 images,
38 shots, 170 takes. `assets.json` was 2.62 MiB; `shots.json` was 12.74 MiB.
The benchmark warms each operation once, then reports the median of five samples.

| Operation | Original JSON lookups | SQLite lookups |
| --- | ---: | ---: |
| Open 40 image streams sequentially | 1,135.9 ms | 87.9 ms |
| Open 40 take video streams sequentially | 6,137.1 ms | 111.0 ms |
| Load complete asset document | 44.7 ms | 50.3 ms |
| Load complete shot document | 340.7 ms | 313.7 ms |

These measure metadata lookup plus opening/closing streams, **not** full page load,
media transfer, decoding, frame extraction, or cold index creation. The complete
document timings fluctuate; those paths were not optimized by this change. Source
JSON is still parsed once on a cold rebuild after a save; subsequent requests use
the index. Do not infer a 13x/55x speedup for the whole application.

Run the repeatable, non-editing benchmark from the repository root:

```powershell
dotnet run --project tools/storage_benchmark --no-restore -- App_Data <project-guid>
```

The benchmark may build the disposable index, but does not save project content,
read media payloads, extract frames, or queue generation. It fails if a sampled
image or take is unavailable instead of treating missing media as a faster lookup.
Initial measurement logs are in ignored `artifacts/storage-before.log` and
`artifacts/storage-after.log`.

Validation: 10 focused index/lifecycle tests passed; 845 relevant existing and new
tests passed across asset storage, shots, frame archives and project packages.
The final cancellation/source-read hardening passed the 10 focused tests again.
Browser checks verified Assets thumbnails, the Takes gallery, and an
existing take's review player (8 seconds, 608 × 352, ready state 4, no video error).
Offscreen images retain lazy loading. No generation was submitted.
