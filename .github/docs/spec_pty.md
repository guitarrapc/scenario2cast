# PTY Recording Specification

Status: **Implemented**

## Motivation

Some CLI tools and terminal UIs behave differently when stdout is not a TTY: they disable color, skip animations, or refuse to run. scenetake records demos as asciinema casts; for commands that need a real terminal session, a step can opt into pseudo-terminal capture without changing the rest of the scenario format.

PTY mode stays opt-in so ordinary steps keep predictable pipe-based stdout/stderr capture and declarative highlighting. YAML keys (`pty`, `pty-continue`, `width`, `height`): [spec_scenario.md](spec_scenario.md).

## Scope

### In scope

| Layer | Responsibility |
|---|---|
| **MiniPty** / **MiniPty.Capture** | Spawn child in PTY, timestamped byte capture ([MiniPty spec](https://github.com/guitarrapc/MiniPty/blob/main/.github/docs/spec.md)) |
| **scenetake** | Map capture chunks to cast `o` events; shell launch; failure handling |

### Out of scope (v1)

- Long-lived interactive sessions (vim, REPLs)
- Step-level stdin beyond what the shell command provides
- YAML keys beyond those defined in [spec_scenario.md](spec_scenario.md)
- MiniPty library API details — [MiniPty docs](https://github.com/guitarrapc/MiniPty/blob/main/.github/docs/spec_index.md)

## Recording behavior

When a map-form step has `pty: true` (default `false`):

- **Simulated typing** — scenetake emits the same prompt, per-character typed command, optional `run-highlight`, and synthetic Enter used by non-PTY steps before the PTY output begins. The command is still executed as a PTY shell command; typing is a recording effect, not PTY stdin.
- **PTY geometry** — uses scenario `width` × `height` (same as cast header terminal size).
- **Shell interpretation** — the `run` string is passed to the scenario `shell` the same way as non-PTY execution (`pwsh -Command`, `cmd /c`, `bash -lc`, etc.). PTY does not bypass the shell.
- **Merged output** — stdout and stderr are one byte stream. `stderr-color` and pipe-style `highlight` do not apply to PTY output for that step.
- **Timestamped chunks** — output is read while the child runs; cast `o` events use `command_start + chunk_time` on the scenario timeline.
- **Raw byte stream by default** — no newline normalization; ANSI sequences may span chunk boundaries. When `pty-continue: true` is set, shell-startup cleanup and immediate alternate-screen exit cleanup are filtered before cast events are written.
- **No PTY fallback** — if a PTY cannot be created, the run fails fatally. scenetake does not fall back to pipe redirect execution.

### Continuing from previous screen state

When a map-form step sets both `pty: true` and `pty-continue: true`, scenetake removes shell initialization sequences from the beginning of that PTY capture before writing cast `o` events. This supports mixed scenarios where an ordinary step is followed by a PTY command and the PTY shell startup would otherwise clear the accumulated terminal state. It applies both to lightweight commands such as `echo` and alternate-screen TUI commands such as `matrix`, where the TUI should run full-screen and then return to the previous main-screen history. It is valid on the first step; in that case there may simply be no prior screen state to preserve.

The filter applies first to the leading initialization phase, before the first meaningful user output. It strips common shell startup controls such as full-screen erase (`CSI 2 J`), erase-to-screen-end (`CSI J`, `CSI 0 J`, `CSI 1 J`), cursor home / position (`CSI H`, `CSI row;col H`, `CSI row;col f`), cursor visibility toggles, SGR reset, OSC title updates, and platform mode toggles such as ConPTY/focus/bracketed-paste private modes.

The leading phase ends as soon as printable output appears, an alternate-screen mode (`?1049`, `?1047`, or `?47`) appears, or a 4096-byte safety limit is reached. Alternate-screen enter/exit sequences are not stripped, so TUI commands retain their own terminal behavior. When an alternate-screen exit is followed immediately by main-screen cleanup controls such as cursor home, line erase, blank-line advancement, cursor visibility toggles, SGR reset, or platform private-mode toggles, those cleanup controls are stripped so the restored main-screen history remains visible. Other bytes after the leading phase are recorded unchanged. `pty-continue` is ignored with a warning when `pty: true` is not set.

When `pty: false` (default):

- stdout and stderr are captured separately via pipe redirect.
- Typing animation and prompt are simulated in the cast.
- Coloring keys apply per [spec_highlight.md](spec_highlight.md).

PTY capture records bytes from the child session. Terminal rendering (ANSI parsing, SVG) is separate: [spec_svg.md](spec_svg.md). OS implementation notes: [MiniPty references/pty_crossplatform.md](https://github.com/guitarrapc/MiniPty/blob/main/.github/docs/references/pty_crossplatform.md).

## Platform support

| OS | Backend | Minimum |
|---|---|---|
| Windows | ConPTY (`CreatePseudoConsole`) | Windows 10 1809+, Windows 11 |
| Linux | `openpty` + `fork` + `execvp` | Common glibc/musl targets |
| macOS | `openpty` + `fork` + `execvp` | Supported runners |
| FreeBSD | `openpty` + `fork` + `execvp` | `libutil` + BSD `TIOCSCTTY` |

Pipe redirect without ConPTY is **not** a PTY. TUI tools (`matrix`, `vim`, etc.) require `pty: true` on Windows.

## Failure behavior

PTY steps follow the same **recorded step** exit-code rules as pipe steps. See [spec_scenario.md](spec_scenario.md) → Step exit codes.

| Condition | Behavior |
|---|---|
| Child non-zero exit | Warning on stderr; recording continues; scenetake exits `0` (same as pipe `steps`) |
| PTY spawn / ConPTY / `openpty` failure | Fatal error; scenario run aborts |
| Output drain timeout | Fatal error (`TimeoutException` from capture) |
| Cancel during interactive API use | Not used by scenetake scenario path in v1 |

## Verification

| Test | Location |
|---|---|
| MiniPty core + Capture | [MiniPty/tests](https://github.com/guitarrapc/MiniPty) |
| PTY layer | `scenetake/tests/pty_test.cs` |
| PTY continue filter | `scenetake/tests/pty_continue_test.cs` |
| Fixture scenarios | `scenetake/tests/fixtures/pty-*.yaml` |

Integration tests require `SCENETAKE_BIN` pointing at a published scenetake binary.

## Cross-Document Notes

- [spec_scenario.md](spec_scenario.md) — `pty`, `pty-continue`, `width`, `height` keys and defaults
- [spec_cast.md](spec_cast.md) — cast event format
- [spec_cli.md](spec_cli.md) — stderr warnings for non-zero step exits
- [spec_pre_post.md](spec_pre_post.md) — contrast with `pre`/`post` fail-fast

## Lessons Learned

- Pipe redirect is not a PTY; ConPTY is required on Windows for TUI tools.
- Keeping `pty` opt-in preserves simpler pipe behavior for ordinary commands.
- Keeping `pty-continue` opt-in preserves raw PTY streams by default while allowing PTY commands, including alternate-screen TUI demos, to coexist with pipe-recorded steps without erasing prior history after startup or TUI exit cleanup.
- PTY command typing should be emitted by default for visual continuity with pipe steps, but it remains a recording effect. The command still runs through the configured shell in the PTY; scenetake does not inject the typed characters into PTY stdin.
- Alternate-screen TUI tools may restore the main screen and then emit extra cleanup controls. Preserving the alternate-screen enter/exit while filtering that immediate main-screen cleanup keeps the TUI behavior intact and returns the viewer to the previous history.
- Recorded step failures belong in the cast as demo content; only infrastructure failures (spawn, drain timeout) should abort the run. See [spec_scenario.md](spec_scenario.md) → Step exit codes.
