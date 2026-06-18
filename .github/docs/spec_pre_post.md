# Pre/Post Command Specification

Status: **Implemented**

## Motivation

scenario2cast records the visible command flow described by `steps`. Scenarios often need setup and teardown around that flow—preparing state, starting helpers, cleaning up—without polluting the cast. Top-level `pre` and `post` run those commands outside the recording.

Failed recorded steps can still be legitimate demo content; setup and teardown failures should instead fail the scenario run.

## Scope

### In scope

- Top-level `pre` (before `steps`) and `post` (after cast write) as string arrays.
- Same resolved `shell` and `cwd` as `steps`.
- stdout/stderr visible in the CLI; command text and output never written to the cast.
- Fail-fast for `pre` and `post`.
- `--verbose` for optional `pre`/`post` execution labels and phase markers.

### Out of scope

- Step-level `pre` or `post`; map/object entries.
- Coloring, timing, or display metadata on `pre`/`post` commands.
- Recording, retrying, or continuing past a failed `pre`/`post` command.

## YAML Contract

`pre` and `post` are top-level string arrays. Templates and docs should show `pre` before `steps` and `post` after for readability; YAML key order does not affect behavior.

```yaml
pre:
  - dotnet build

steps:
  - run: dotnet test

post:
  - git clean -fd
```

Each item is one shell command string (same as `steps[].run`). Block scalars are allowed. Empty or whitespace-only entries are skipped.

## Execution Order

1. Resolve scenario settings, shell, cwd, deterministic seed, and timestamp.
2. Execute `pre` commands.
3. Execute and record `steps`.
4. Write the cast file.
5. Execute `post` commands.
6. Report success or failure.

## Failure Behavior

| Phase | On failure |
|---|---|
| `pre` | Fail-fast; `steps`, cast write, and `post` are skipped; exit with the command's code. |
| `steps` | Recording continues; step exit codes do not stop later steps or affect scenario2cast's exit code. |
| `post` | Fail-fast; remaining `post` commands skipped; cast file retained; exit with the command's code. |

If no `pre` or `post` command fails, scenario2cast exits `0` regardless of individual step exit codes.

## CLI

`--verbose` may appear in any argument position on the scenario path. It enables successful `pre`/`post` command labels and phase markers; `steps` `running:` logs are always visible. Failure details (phase, full command text, exit code) are always printed. Output is emitted after each command exits (live streaming not required in v1).

Unknown `-` / `--` options are explicit errors. `init` does not accept `--verbose`.

## Init Template

`scenario2cast init` should include commented `pre` and `post` examples so users discover the feature without enabling it by default.

## Determinism

Deterministic seed and timestamp are derived from the whole YAML file. Adding or changing `pre` or `post` may change cast metadata and typing jitter even though their output is not recorded.

## Lessons Learned

- Cleanup belongs after cast write so users keep the recording when teardown fails.
- Default logs should stay focused on cast content; verbose mode is the right place for successful `pre`/`post` labels.
