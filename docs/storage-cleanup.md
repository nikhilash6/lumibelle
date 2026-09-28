# Storage cleanup

Open **Trash** in the global navigation. The two views clean different media:

- **Discarded media** restores items or permanently deletes them. Filter by project to limit **Empty Trash** to that project. Confirmation captures the listed items; later discards are excluded. Existing 30-day automatic expiry remains unchanged.
- **Lossless archives** lists active takes with saved WebP frame archives, largest first. Select individual takes or every page within the selected project scope, then confirm removal. The displayed space is an estimate; archive sizes use distinct existing files, not the number of logical frames.

Archive removal keeps the MP4, take identity, original generation snapshot, publication receipt, Cut edits, already saved Assets, and independent refinement packages. Frame selection and saving continue through FFmpeg extraction from compressed video. These new frame copies are labelled as MP4-derived. Original lossless pixels cannot be recovered from the MP4.

Before removal, Lumibelle probes the MP4's dimensions, frame count, frame rate and audio, then extracts one frame to check FFmpeg access. If those checks fail, the selected project's archives are retained. A concurrent project save also requires refresh before retrying.

Lumibelle saves a removal record and switches frame access to MP4 before deleting only the captured archive files. A failed save deletes nothing. An interrupted removal retains its record, is shown for manual retry, and resumes on startup or the hourly cleanup pass. A missing MP4 pauses further deletion. Finished removals preserve historical generation and archive timing information; they do not change queued jobs or the shot's setting for future takes.

Discarded takes belong to the Trash view: permanently deleting one removes its video, remaining archives and refinement package together. No active take's archive is removed automatically without a previously confirmed removal. These controls do not clean AI job history, benchmark artifacts, model checkpoints or unrelated files.

Verification uses synthetic MP4/WebP media and an isolated browser host. No GPU generation or deletion of production media is needed.
