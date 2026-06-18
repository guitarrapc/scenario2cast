# Specification Index

scenario2cast behavior is split into focused specs under `.github/docs/`. Each spec covers **what** and **why**; implementation details live in code.

## Specs at a Glance

| Spec | Covers |
|---|---|
| [spec_scenario.md](spec_scenario.md) | Scenario YAML structure — keys, defaults, `steps` forms, execution order, determinism |
| [spec_cli.md](spec_cli.md) | Commands, flags, output paths, logging, exit codes, `init` |
| [spec_highlight.md](spec_highlight.md) | Coloring — `highlight`, `run-highlight`, `stderr-color`, `name` prefix, style strings, `at` ranges |
| [spec_pre_post.md](spec_pre_post.md) | `pre` / `post` — recording exclusion, fail-fast, exit-code semantics |
| [spec_svg.md](spec_svg.md) | SVG output — cast header `theme`, renderer, `svg` subcommand event handling |

All listed specs are **Implemented**.

## Where to Look

Use this when you know the goal but not the document.

| I want to… | Read |
|---|---|
| Write or review a scenario YAML file | [spec_scenario.md](spec_scenario.md) |
| Know defaults for `typing-speed`, `prompt`, etc. | [spec_scenario.md](spec_scenario.md) → `settings` |
| Add setup/teardown commands | [spec_scenario.md](spec_scenario.md) → `pre`/`post`, then [spec_pre_post.md](spec_pre_post.md) for behavior |
| Color command output, typed text, or stderr | [spec_highlight.md](spec_highlight.md); key placement in [spec_scenario.md](spec_scenario.md) → `steps` |
| Set terminal theme or font size for SVG | [spec_scenario.md](spec_scenario.md) → `render`; cast header mapping in [spec_svg.md](spec_svg.md) |
| Run scenario2cast from the command line | [spec_cli.md](spec_cli.md) |
| Produce `.svg` alongside or from a `.cast` | [spec_cli.md](spec_cli.md) → `--format svg` / `svg` subcommand; rendering in [spec_svg.md](spec_svg.md) |
| Understand exit codes or stderr output | [spec_cli.md](spec_cli.md) |
| Know what happens when `pre`, SVG, or `post` fails | [spec_pre_post.md](spec_pre_post.md) + [spec_svg.md](spec_svg.md) failure tables |
| Convert an existing cast (resize events, unsupported codes) | [spec_svg.md](spec_svg.md) → `svg` subcommand |
| Understand why cast metadata changes when YAML edits don't affect recording | [spec_scenario.md](spec_scenario.md) → Determinism |

## How Specs Relate

```
spec_scenario.md          ← YAML input (start here for file format)
    ├── spec_highlight.md     coloring keys on settings / steps
    ├── spec_pre_post.md      pre/post runtime behavior
    └── render: (in spec_scenario) ─┐
                                    ▼
                               spec_svg.md    cast header + SVG renderer
                                    ▲
spec_cli.md ────────────────────────┘    invokes everything above
```

- **YAML keys** live in [spec_scenario.md](spec_scenario.md).
- **Value semantics** for coloring live in [spec_highlight.md](spec_highlight.md).
- **CLI surface** lives in [spec_cli.md](spec_cli.md); it references the others for flag effects.
- **Cast / SVG artifacts** live in [spec_svg.md](spec_svg.md); scenario `render:` feeds the cast header defined there.

## Suggested Reading Order

1. **New to scenario2cast** — [spec_scenario.md](spec_scenario.md), then [spec_cli.md](spec_cli.md)
2. **Polishing demo appearance** — [spec_highlight.md](spec_highlight.md), then [spec_svg.md](spec_svg.md)
3. **CI / automation around runs** — [spec_cli.md](spec_cli.md), then [spec_pre_post.md](spec_pre_post.md)
