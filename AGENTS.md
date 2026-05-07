# DH Project Agent Notes

This file is the handoff note for future Codex sessions. Read it before making changes.

## Collaboration Rules

- Communicate with the user in Chinese.
- The user can run real hardware and long-running Windows tests. Ask the user when a task needs SDK hardware, GUI confirmation, large disk writes, or long storage/replay validation.
- Proceed in small, verifiable steps. Prefer one focused code change at a time, then build/test, then commit.
- After finishing a change, commit only the files changed for that task. Do not include unrelated dirty files.
- If a file contains both user edits and agent edits, stop and confirm before committing.
- Do not create new requirement docs unless the user asks. Update the existing docs under `docs/`.
- Default data root resolves to the current repository's `data` directory unless `DH_DATA_ROOT` is set for the machine.
- Avoid blocking the user with huge plans. State the next concrete step and keep moving.

## Git Workflow

For each completed small change:

1. Check status.
2. Stage only the files touched for that change.
3. Commit with a concise message.

Example:

```powershell
git status
git add <files changed by this task>
git commit -m "Add fast segment preview builder"
```

Never revert or overwrite unrelated changes.

## Current Architecture Context

- The project is in an architecture migration for data acquisition, storage, preview/query, and replay.
- Product requirement is physical NI `.tdms` raw storage. The user accepts segmented/source-sharded `.tdms` files as long as replay can read them and compression algorithms can process them later.
- Current high-speed acquisition storage is being switched to physical NI `.tdms` files. `.dhseg` is only a historical/diagnostic transition format, not the final storage target.
- The current SDK TDMS write path is source-sharded async segment TDMS: source workers deinterleave SDK blocks into about 5s chunks, then dedicated TDMS writers serialize `raw/source_0000_seg000001.tdms` files. Query/replay has a manifest-driven L0 bridge for this shape; high-performance partial TDMS replay is still pending.
- `.sdkraw.bin` is legacy/debug/conversion path, not the new primary storage path.
- `.dhseg` validation must use fast segment header/payload structure checks. Do not send `.dhseg` through old TDMS channel readback validation.

## Important Real Test Results

- Windows native storage probe on Samsung SSD 990 PRO:
  - Command shape: `StorageThroughputProbe --mode raw-source-segment --sources 10 --channels 16 --sample-rate 1000000 --seconds 10 --chunk-ms 100 --segment-seconds 2 --file-buffer-mb 4 --preallocate false --flush-to-disk true`
  - Result: `PayloadMBps=1765.7`, `ExpectedPayloadBytes=6400000000`, `Result=Completed`.
  - This is the useful disk baseline. Do not use WSL `/mnt/c` write speed as the SSD baseline.
- Real capture `data/session_20260501_161243_411`:
  - About 30s, 10 sources * 16 channels * 1MHz.
  - Wrote `150` `.dhseg` files, about `18G` directory size, `17.029GiB` payload.
  - Capture log ended with `integrity=True protection=False rejected=0 faults=0`.
  - Stop segment writer drain was about `137.792ms`.
- Offline full preview build for that session:
  - `FastSegmentPreviewBuilder` generated full `L1-L4` preview in `43s`.
  - `preview_levels` size was about `521MB`.
  - `L1` alone was about `512MB`; `L2` was about `8.2MB`; `L3` about `640KB`; `L4` tiny/empty.
  - `PersistedPreviewQuerySmokeTest` returned `0` afterwards.
  - Conclusion: full preview generation works structurally but must not synchronously block "stop capture".
- Coarse preview build for that same session:
  - `FastSegmentPreviewBuilder --levels L2,L3,L4` still scanned all `17.029GiB` and took about `33s`.
  - Conclusion: preview conversion optimization is not the main path for TDMS direct storage; first measure and implement physical `.tdms` direct writing.
- TDMS direct write probe:
  - `20260501-170836`: `10 sources * 16 channels * 1MHz * 30s`, `segment-seconds=5`, physical `.tdms`, payload `19.2GB`, `PayloadMiBps=638.9`.
  - `20260501-171034`: same scale with `segment-seconds=10`, `PayloadMiBps=594.3`.
  - This proved physical TDMS can write, but source/segment files were not sufficient for long real SDK capture.
- Real SDK TDMS capture `data/session_20260501_172530_870` stopped after about `16s` because the TDMS segment queue hit the old `4GB` protection limit (`14` pending segments, `4.48GB`). Raw SDK block queues were healthy (`pendingBlocks=0`).
- Real SDK TDMS capture `data/session_20260501_200738_906` later hit peak pending TDMS segment `57` / `17.28GB`. Do not keep solving this by raising RAM thresholds; source/segment queueing only delays the failure and increases stop wait.
- Real SDK TDMS capture `data/session_20260501_205729_346` tested persistent per-source TDMS stream append and was worse: about `14s` to protection, `tdms-stream-chunk-appended` round 1 took about `2.2-2.5s` per source and round 2 took about `4.8-5.3s`. Conclusion: concurrent long-lived DDC append across 10 open source files is not viable.
- Real SDK TDMS capture `data/session_20260501_211539_073` ran about `7m32s` and then hit async segment pending protection: source block queues were healthy (`pendingBlocks=0`, peak only `20` blocks / `128MB`), but one TDMS segment writer could not sustain the required `~640MiB/s`; `pendingSegments` climbed to `161` / `51.52GB`. The writer was changed to `2` TDMS segment workers and the stop path now drains segment writers even after protection.
- Real SDK TDMS captures `data/session_20260501_214250_023` and `data/session_20260501_221919_722` showed NI DDC remained the bottleneck: `2` writers lasted about `9m13s` but hit `201` pending segments / `64.32GB`; `3` writers degraded and showed a real about `5.9s` source block stall. Do not keep raising writer count or pending thresholds as the primary fix.
- Manual TDMS probe `data/tdms-direct-probe/tdms-direct-write-20260501-225025` wrote the same `10 sources * 16 channels * 1MHz * 10s` physical `.tdms` source/segment shape at `3681.5MiB/s`. A small `--validate-first-read true` run successfully read back `source_0000/AI0000` through `TdmsReaderUtil`. This strongly indicates NI DDC write/save/close is the bottleneck, not disk.
- Real SDK manual TDMS capture `data/session_20260501_225658_246` ran about `68min`, wrote `7285` `.tdms` files / about `2.2T`, and completed with `integrity=True protection=False rejected=0 faults=0`; `peakPendingSegments=8`, `peakPendingSegmentBytes=2.56GB`, `segmentDrainMs=360.959`. One-hour TDMS direct save is now structurally proven.
- Offline TDMS preview build for `data/session_20260501_225658_246`:
  - `FastSegmentPreviewBuilder --levels L2,L3,L4` completed with `TdmsSegmentFiles=7285`, `Channels=160`, `PayloadGiB=2169.818`, `Elapsed=01:06:59`.
  - `preview_levels` size is about `1.009GiB`.
  - Conclusion: offline TDMS sidecar generation works, but it is effectively 1:1 with recording duration because it scans 2.2T after the fact. Treat it as recovery/backfill, not the final stop-capture path; next design should generate coarse L2-L4 incrementally from in-memory source chunks during capture/segment writing.

## Current Code Landmarks

- SDK TDMS capture writer:
  - `src/DH.Client.App/Services/Storage/SdkTdmsCaptureWriter.cs`
  - Current target shape is async source/segment TDMS: `raw/source_{SourceId:D4}_seg{SegmentIndex:D6}.tdms`.
  - Source workers must not call TDMS DDC append directly. They only deinterleave SDK blocks into chunks and enqueue `PendingTdmsSegment`; two dedicated TDMS segment workers currently consume the physical `.tdms` write queue.
  - `session.manifest.json` now includes `TdmsSegments` timeline entries (`Path`, `SourceId`, `SegmentIndex`, `StartSample`, `SamplesPerChannel`, `ChannelIds`) so replay/query does not infer timing by scanning all files.
- TDMS query/replay:
  - `src/DH.Client.App/Data/Query/PersistedPreviewQueryRuntime.cs`
  - L0 raw query can build a source/segment timeline from `TdmsSegments`, with file-name fallback for older test sessions.
  - Manual TDMS L0 query now uses partial seek reads: read the TDMS lead-in raw data offset, compute the channel byte offset from `ChannelIds`, read only the requested window payload, and apply `MaxPointsPerChannel` sampling/envelope. Unknown/old TDMS files still fall back to `TdmsReaderUtil.ReadChannelData()`.
- TDMS viewer:
  - `src/DH.Client.App/ViewModels/TdmsViewerViewModel.cs`
  - Direct-save sessions are logical sessions, not individual file-open targets. Prefer the `选择会话...` UI path and open `data/session_...` or `session.artifacts`; selecting a `raw/source_*.tdms` file is only a compatibility path and should resolve the sibling `session.artifacts` directory.
  - The viewer should build the full channel list from `session.manifest.json`, treating all `raw/source_*.tdms` files as one session.
  - If `preview.index.json` is missing, the initial plot must use a small L0 query window through `PersistedPreviewQueryRuntime`; do not fall back to whole-channel `TdmsReaderUtil.ReadChannelData()` for large direct-save sessions.
- TDMS source stream writer:
  - `src/DH.Client.App/Services/Storage/TdmsSourceStreamFileWriter.cs`
  - Creates group `source_0000`, float waveform channels, and file metadata `dh_storage_format=tdms-source-stream-v1`.
  - Keep as a failed/diagnostic experiment unless a later single-writer stream design proves better; do not use concurrent per-source stream append as the real SDK capture hot path.
- TDMS source segment writer:
  - `src/DH.Client.App/Services/Storage/TdmsSourceSegmentFileWriter.cs`
  - `src/DH.Client.App/Services/Storage/ManualTdmsSourceSegmentFileWriter.cs`
  - `tools/TdmsDirectWriteProbe/Program.cs`
  - Writes target-shape files like `source_0000_seg000000.tdms` with group `source_0000` and float channels.
  - The DDC writer is retained for comparison. The real SDK async segment queue is now being switched to the manual TDMS writer because the DDC path cannot sustain long real captures.
- Manifest writer:
  - `src/DH.Client.App/Services/Storage/PersistedPreviewSessionManifestWriter.cs`
  - Lists raw files and generated preview sidecars in `session.manifest.json`; TDMS direct-save sessions use `TdmsSegments` plus `PreviewFiles`.
- Capture-time preview sidecar:
  - `src/DH.Client.App/Services/Storage/SdkTdmsCaptureWriter.cs`
  - Capture-time preview sidecar is disabled by default (`EnableCapturePreviewSidecar=false`). The stable path is: record physical TDMS first, then run `FastSegmentPreviewBuilder` offline to build `preview.index.json` and `PreviewFiles`.
  - Real test `data/session_20260503_000722_932` proved synchronous `L1,L2,L3,L4` is not viable: protection fired at `201/200` pending TDMS segments / `64.32GB`, `writeMBps=577.5`, while `avgPreviewMs` was about `3.84s` per source segment and TDMS segment write itself stayed much faster. Treat this as preview generation bottleneck, not disk or manual TDMS write bottleneck.
  - Real test `data/session_20260503_091915_284` proved synchronous `L2,L3,L4` is still too slow with the current generic `PreviewSidecarWriter`: protection fired again at `201/200` pending segments / `64.32GB`, `avgPreviewMs` about `2.83s` per source segment, `segmentDrainMs` about `308s`, capture wall throughput about `199MiB/s`.
  - L1 is removed from the capture hot path. Keep L1 for offline/backfill or a later optimized/background strategy; do not re-enable L1 synchronously for high-rate capture without new measurements.
- Query runtime:
  - `src/DH.Client.App/Data/Query/PersistedPreviewQueryRuntime.cs`
  - Supports L1-L4 preview, old raw_index L0, and `.dhseg` L0 direct window reads.
- Offline preview builder:
  - `src/DH.Client.App/Services/Storage/FastSegmentPreviewSidecarBuilder.cs`
  - Tool entry: `tools/FastSegmentPreviewBuilder/Program.cs`.
  - Supports both historical `.dhseg` sessions and current TDMS direct-save sessions with `TdmsSegments`; use it to build `preview_levels/preview.index.json` from existing `raw/source_*.tdms` without re-recording.
- Smoke test:
  - `tools/PersistedPreviewQuerySmokeTest/Program.cs`
  - Accepts a session folder or artifacts folder and prints `StorageFormat`, `.dhseg` count, `.tdms` count.

## Useful Commands

Build key projects:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\src\DH.Client.App\DH.Client.App.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" build .\tools\TdmsDirectWriteProbe\TdmsDirectWriteProbe.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" build .\tools\FastSegmentPreviewBuilder\FastSegmentPreviewBuilder.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" build .\tools\PersistedPreviewQuerySmokeTest\PersistedPreviewQuerySmokeTest.csproj -c Release
```

Run TDMS raw-only query smoke on a session without preview sidecar:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\tools\PersistedPreviewQuerySmokeTest\PersistedPreviewQuerySmokeTest.csproj -c Release -- --session-path .\data\session_20260501_225658_246 --raw-only --repeat 3 --result-file .\data\raw-query-smoke.txt
```

Run physical TDMS direct-write probe:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\tools\TdmsDirectWriteProbe\TdmsDirectWriteProbe.csproj -c Release -- --output .\data\tdms-direct-probe --sources 10 --channels 16 --sample-rate 1000000 --seconds 10 --segment-seconds 2 --parallel-sources false
```

Build preview sidecar from a `.dhseg` or TDMS direct-save session:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\tools\FastSegmentPreviewBuilder\FastSegmentPreviewBuilder.csproj -c Release -- --session-path .\data\session_YYYYMMDD_HHMMSS_mmm
```

Build only coarse preview first:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\tools\FastSegmentPreviewBuilder\FastSegmentPreviewBuilder.csproj -c Release -- --session-path .\data\session_YYYYMMDD_HHMMSS_mmm --levels L2,L3,L4
```

Run persisted preview/query smoke:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\tools\PersistedPreviewQuerySmokeTest\PersistedPreviewQuerySmokeTest.csproj -c Release -- --session-path .\data\session_YYYYMMDD_HHMMSS_mmm
```

PowerShell requires `&` before `"C:\Program Files\dotnet\dotnet.exe"` because the path contains spaces.

## Next Priorities

1. Ask the user to reopen `data/session_20260501_225658_246` through `TDMS查看` -> `选择会话...`; verify the viewer now uses L2-L4 for overview/zoom instead of raw-only L0.
2. If preview-backed replay is still sluggish, optimize the preview reader/UI path before returning to raw L0 stride-seek improvements.
3. Run a real capture with capture-time `L1-L4` preview enabled; if it is stable, verify the resulting session opens with `preview.index.json` immediately. If pending TDMS segments climb, consider making L1 background-only while keeping L2-L4 always-on.
4. Revisit preview sidecar background generation policy, then resume high-FPS realtime rendering migration.

## Documentation To Keep Updated

- `docs/架构分阶段落地总览.md`
- `docs/阶段三_存储索引与回放集成规范.md`
- `docs/阶段四_UI迁移与旧链路下线规范.md`
- `docs/数据采集与实时存储架构方案（TDMS）.md`
- `docs/实时显示与回放查询架构方案（UI）.md`
