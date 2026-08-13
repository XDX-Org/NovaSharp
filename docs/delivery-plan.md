# Delivery plan

## Product boundary

The preview target is a cross-platform IDE for local, SDK-style .NET development with C#, Razor/Blazor, HTML, and CSS. It includes editing, project loading, language services, search, build/run, terminal, managed debugging, durable settings/layouts, and a minimal extension SDK.

Source control UI, remote development, collaboration, notebooks, AI completion, designers, profiling, multiple native windows, non-.NET debugging, and non-SDK project systems are not preview requirements.

## Status

| Phase | Status | Exit evidence |
|---|---|---|
| 1. Single-file editor shell | Complete | Phase 2 verification subsumes its build, test, package, and native-launch gates |
| 2. Editor and file lifecycle | Complete | Four-platform verification passed; see `phase-02-decisions.md` |
| 3. Workspace explorer | Complete | Four-platform build/test/package evidence and Linux native interaction gates pass across runs 31394546585 and 31310379518 |
| 4. Documents and movable tabs | Complete | Four-platform build/test/package and Linux interaction gates pass in run 30750653723 |
| 5. Editor groups and split views | Complete | Four-platform build/test/package and Linux Phase 2–5 interaction gates pass in run 30760456811 |
| 6. Solution model and Roslyn | Complete | Four-platform build/test/package and Linux Phase 2–6 interaction gates pass in run 30764773602 |
| 7. C# IntelliSense | Complete | Phase 7 handoff records 65 tests and four-platform verification run 31266586068 |
| 8. Diagnostics and code navigation | Complete | 68 tests, four-platform build/test/package, and Linux Phase 2–8 native gates pass in run 31268021847 |
| 9. Workspace search and replace | Complete | Four-platform build/test/package and packaged Linux Phase 9 gates pass in run 31310379518 |
| 10. Build, run, and output | Complete | Four-platform build/test/package and packaged Linux build/run gates pass in run 31310379518 |
| 11. Integrated terminal | Complete | Four-platform build/test/package and packaged Linux terminal gates pass in run 31310379518 |
| 15. Razor, HTML, and CSS | Complete | 99 tests, four-platform build/test/package, and Linux Phase 2–15 interaction gates pass in run 31310379518 |
| 15.2. Real language servers | Complete | 128 tests, four-platform Release build/test/package, and packaged Linux Phase 2–15 interaction gates pass in run 31484488987 |
| 12. Debug adapter foundation | Complete | Cross-platform adapter launch, source binding, inspection, ownership, packaging, and recovery pass in run 31503017795 |
| 13. Debugging experience | Complete | Async/threaded source-breakpoint, evaluation, scope, exception, bounded inspection, and keyboard-operable UI coverage pass in run 31503017795 |
| 14. Durable workbench | Complete | Validated atomic state, exact recovery, safe mode, scoped settings/keybindings, budgets, and packaged Linux workbench gates pass in run 31503017795 |
| 16 | In progress | Versioned manifest, compatibility, permission/trust boundary, and sample manifest pass focused tests; isolated host remains |
| 17. Build configurator | Complete | Evaluated configuration, exact execution, persistence, accessibility, 143 tests, five-platform build/package, and Linux x64/arm64 interaction gates pass in run 31726942957 |
| 17.5. Debugger refinement | Planned | Editor breakpoint creation, live binding, diagnostics, refined debugger UI, and packaged end-to-end debugging remain |
| 18 | In progress | Preview identity, local qualification script, SPDX language-server inventory, privacy/support/limitations/release notes exist; signing, installers, update/rollback, provenance, and clean-image gates remain |

Status values are `planned`, `in progress`, `blocked`, and `complete`. Update this table only from test or release evidence; documentation or partial UI alone does not complete a phase.

## Cross-cutting foundations

These are not separate feature phases. Introduce each boundary by the indicated phase and retain it thereafter.

| Foundation | Required by | Minimum contract |
|---|---:|---|
| Command registry | 2 | Stable command ID, handler, enablement, keybinding, menu/palette metadata |
| Configuration service | 2 | Typed defaults, user/workspace scopes, validation, atomic versioned storage |
| Notification and logging | 2 | Structured severity, actionable errors, bounded local logs, source-text redaction |
| Lifetime/task coordinator | 2 | Ownership, cancellation, stale-result rejection, disposal diagnostics |
| Persistence service | 3 | Versioned schemas, portable workspace paths, atomic writes, corruption fallback |
| Diagnostic store | 6 | Results keyed by producer, context, document version, and stable identity |
| Process service | 10 | Argument arrays, explicit environment/working directory, process-tree ownership |
| Capability/extension boundary | 7 | Internal provider contracts that can later be exposed selectively by phase 16 |

Record decisions that constrain multiple phases as short ADRs under `docs/decisions/`. At minimum, decide editor input architecture before phase 2, project-system strategy before phase 6, terminal engine before phase 11, and debug adapter before phase 12.

## Supported platform matrix

Before phase 2 completes, record exact minimum OS versions and CI images for Windows x64, Linux x64, and macOS arm64/x64. A platform is supported only when its native host prerequisites, packaging format, and automated smoke-test route are documented. Other architectures are best effort until added to this matrix.

## Quality gates

Every phase must pass:

- A clean build with warnings either fixed or linked to an accepted, time-bounded issue.
- Unit tests for state and algorithms; integration tests for service boundaries; interaction tests for the phase's primary user flow.
- Cancellation, disposal, error recovery, keyboard access, and restored-state tests where applicable.
- No secrets, source text, or absolute paths in telemetry; telemetry remains opt-in.
- Updated status, user documentation, known limitations, and migration notes for changed persisted schemas.

Budgets must be measured on named fixture hardware and repositories. Set numeric budgets before implementing the affected phase:

| Budget | Set no later than |
|---|---:|
| Startup time and idle memory | 2 |
| Typing/render latency and large-file memory | 2 |
| Explorer expansion and watcher recovery | 3 |
| Solution load, Roslyn snapshot count, completion first result | 6 |
| Search throughput/result memory | 9 |
| Build cancellation/process cleanup | 10 |
| Terminal input/resize latency | 11 |
| Debug step/evaluate latency | 12 |
| Crash recovery and full-workbench memory | 14 |

## Delivery records

For an active phase, track an owner, target release, dependencies, risks, and links to implementation issues. Each implementation issue should be small enough to review independently and name the completion criterion it advances.

## Principal risks

| Risk | Required mitigation |
|---|---|
| Editable virtualized Blazor surface mishandles IME or selection | Prototype and test the native input geometry before expanding editor features |
| PhotinoXDX/WebView platform differences | Run host smoke tests on every supported OS from phase 1 onward |
| Roslyn/MSBuild state diverges from dirty buffers | One workspace coordinator with versioned mappings and fixture solutions |
| Terminal/debug child processes leak or target unrelated processes | Explicit process-tree ownership and adversarial cleanup tests |
| Razor projections map edits or diagnostics incorrectly | Versioned host/projected ranges and round-trip mapping fixtures |
| Extension API freezes internal design too early | Expose a small capability API only after internal providers have shipped |
| Late workbench persistence causes incompatible state | Version schemas from their first introduction and test migrations |

## Open decisions

Resolve these before the named phase starts:

1. Phase 2: browser input mechanism, text-buffer/undo representation, encoding fallback, and settings format/location.
2. Phase 6: MSBuild discovery/evaluation library, supported SDK/project types, and multi-target context policy.
3. Phase 11: terminal emulator implementation or dependency, licensing, and PTY/conpty strategy.
4. Phase 12: debug adapter/engine, protocol transport, redistribution/licensing, attach permissions, and capability fallback.
5. Phase 15: Razor language-service integration and projection ownership.
6. Phase 16: in-process versus isolated extension host, trust model, permissions, compatibility, and signing policy.
7. Phase 17: build-configuration ownership, launch-profile editing boundaries, validation, and secret storage.
8. Phase 18: application identity, versioning, package formats, signing/notarization, update channel, and support lifetime.

## Preview definition

Preview is reached only when phases 1–18, including phase 17.5, are complete, the supported platform matrix is green, clean install/update/uninstall paths pass, persisted-state migration and crash recovery pass, security and license reviews have no release blockers, and known limitations are published.
