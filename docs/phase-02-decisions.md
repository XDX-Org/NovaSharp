# Phase 2 decisions and verification

## Architecture decisions

- Browser-native `textarea` input owns caret, selection, clipboard, composition, and IME. Its text is transparent over a C#-owned virtualized presentation; JavaScript only synchronizes scrolling and translates native Tab/bracket input.
- C# owns document text, versions, undo history, search results, lexical classification, encoding, line endings, and conflict policy.
- Undo stores versioned buffer snapshots for this single-document phase. Selection replacements and replace-all operations form one undo unit.
- Files without a BOM must be valid UTF-8. Invalid input is rejected without replacing the current buffer. UTF-8 BOM and UTF-16 LE/BE are preserved.
- Settings use schema-versioned JSON at `%LOCALAPPDATA%/NovaSharp/settings.json`, `$XDG_DATA_HOME/NovaSharp/settings.json` when the runtime maps LocalApplicationData there, or the macOS LocalApplicationData equivalent. Writes use the same atomic sibling-file path as documents.

## Supported platform matrix

| Platform | Minimum | CI image | Package/smoke route |
|---|---|---|---|
| Windows x64 | Windows 10 1809 | `windows-2025` | framework-dependent publish; launch/open/edit/save/close smoke |
| Linux x64 | Ubuntu 24.04 | `ubuntu-24.04` | framework-dependent publish with GTK/WebKit prerequisites; Xvfb smoke |
| macOS arm64 | macOS 14 | `macos-14` | framework-dependent publish; native-host smoke |
| macOS x64 | macOS 14 | `macos-15-intel` | framework-dependent publish; native-host smoke |

Other operating systems and architectures are best effort.

## Phase 2 budgets

Measured budgets use a release build, a 100,000-line C# fixture, and a 4-core/8 GB CI runner:

- cold start to interactive: 2.5 seconds; idle working set: 220 MB;
- p95 input-to-presentation latency: 50 ms;
- large-file working set: 350 MB;
- rendered source rows: visible rows plus at most 16 overscan rows.

Automated unit coverage verifies document state, Unicode edits, encoding and line endings, atomic configuration and document writes, conflict rejection, watcher debouncing, search/replace, tokenization, command enablement, cancellation, and bounded logging. The `Phase 2 verification` workflow builds, tests, and publishes every supported target. Its Linux Xvfb route also launches the packaged native host and verifies browser input, selection replacement, bracket and Tab edits, IME commit grouping, bounded DOM rows, and document loading. Hosted Windows and macOS runners have no interactive desktop session, so their native launch, system-clipboard shortcuts, and unsaved-close dialog remain manual release checks.

`MSB3277` is suppressed through phase 3 because PhotinoXDX `0.1.0-preview.6` exposes its Windows-only WebView2 WPF reference on non-Windows builds. Remove the suppression when upgrading PhotinoXDX; all other warnings remain errors in the verification workflow.
