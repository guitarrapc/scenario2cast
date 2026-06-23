# Stream Output Timing Plan

Status: **Phase 1 implemented**

## Motivation

Long-running commands (e.g. `docker build`) produce output that feels instant in SVG/GIF playback because pipe steps buffer all stdout/stderr and emit a single cast `o` event. The goal is natural replay timing by recording stream arrival times at cast generation time, then applying a shared timing pipeline for pipe and PTY steps.

## Decisions (grill-me summary)

| Topic | Decision |
|---|---|
| Goal | Cast timeline at recording time; playback (SVG/GIF/asciinema) should feel natural |
| Pipe capture | Parallel stdout/stderr stream read; merge chronologically by wall-clock time |
| `stderr-color` | Applied per stderr chunk before cast emit |
| `highlight` | Raw capture → timing pipeline → apply to full merged text → split back to timed segments |
| Long steps | Linear compression when `max-duration` is set: `scale = min(1, max / actual)` |
| Compression knob (v1) | `max-duration` only (no `playback-speed`) |
| Idle gaps | Always on: gap > `silence-threshold` collapses to `max-idle` |
| Burst output | Keep real timing (instant is fine) |
| Off-screen output | Record everything (v1) |
| PTY | Same idle + compression pipeline after existing startup offset |
| YAML placement | `settings` defaults + per-step override (`execution-duration` pattern) |
| Cast emit | Coalesce chunks that share the same adjusted timestamp |
| Compatibility | Lenient defaults (`silence-threshold: 2.0`, `max-idle: 0.2`) so short demos stay mostly unchanged |

## Timing pipeline

```
capture chunks (pipe stream or PTY bytes) + wall-clock time
  → startup offset (PtyCastTiming; pipe + PTY)
  → idle collapse (silence-threshold / max-idle)
  → linear compression (max-duration when set)
  → stderr-color / highlight
  → coalesce same timestamp
  → cast o events
```

### Defaults

| Key | Default | Description |
|---|---|---|
| `silence-threshold` | `2.0` | Gaps longer than this are treated as idle |
| `max-idle` | `0.2` | Collapsed idle gap duration |
| `max-duration` | unset | When set, linearly scale step output timeline to this ceiling |

## Implementation plan

### Phase 1 — Core pipeline (this PR)

1. **`CastTimingPipeline.cs`**
   - `CollapseIdleGaps`
   - `LinearCompress`
   - `AdjustTimeline`
   - `CoalesceTimedText` / `CoalesceTimedUtf8`
   - `HighlightSplitter` (map highlighted full text back to timed segments)

2. **`PipeStreamCapture.cs`**
   - Async parallel `StandardOutput` / `StandardError` read
   - `PipeStreamChunk(TimeSpan Time, bool IsStderr, string Text)`
   - Chronological merge

3. **`scenetake.cs`**
   - `ScenarioSettings`: `SilenceThreshold`, `MaxIdle`, `MaxDuration`
   - `RunCommandCoreAsync`: pipe path uses stream capture
   - `GenerateAsync`: shared emit path for pipe + PTY through timing pipeline
   - Constants: `DefaultSilenceThreshold`, `DefaultMaxIdle`

4. **`tests/cast_timing_test.cs`**
   - Unit tests for pipeline, coalesce, startup + idle interaction

5. **CI** — register `tests/cast_timing_test.cs` in `build.yaml`

### Phase 2 — Docs (follow-up)

- Update `.github/docs/spec_scenario.md` with new keys
- Update README samples / `CreateInitialScenarioYaml` comments
- Lessons learned in spec if behavior surprises during dogfooding

### Idle collapse detail

Gaps are measured from **original** chunk timestamps before adjustment. A long idle followed by a short active gap (e.g. 30s then 0.5s) collapses only the 30s gap; the 0.5s gap is preserved.

### Out of scope (v1)

- Viewport-based output thinning
- `playback-speed` multiplier
- External cast files (asciinema recordings) — timing pipeline is scenetake scenario recording only

## Test plan

- [x] `CollapseIdleGaps` short gap unchanged, long gap collapsed to `max-idle`
- [x] `LinearCompress` only when actual > `max-duration`
- [x] Coalesce merges same-timestamp chunks
- [ ] Pipe stream: multi-chunk cast with distinct event times for staggered `echo` + `sleep`
- [x] PTY steps still pass existing `pty_timing_test` / `pty_test`
- [x] `samples/basic.yaml` smoke still passes
