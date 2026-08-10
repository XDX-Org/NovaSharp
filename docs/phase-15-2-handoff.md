# Phase 15.2 handoff

Date: 2026-08-10  
Branch: `phase15-2`  
Base implementation: `d38db1a Implement Phase 15.2 language servers`

## Current state

The real Roslyn/Razor, HTML, and CSS language servers are pinned, packaged, and are the only Release language-intelligence route. The working tree is committed and pushed.

Verified locally:

- Roslyn survives rapid incremental edits without restarting.
- Roslyn opens the solution/project and returns compiler diagnostics in the real-server test.
- Restored documents are replayed with their current in-memory text.
- Ordinary `.cs` saves no longer trigger a project/explorer reload.
- Dynamic diagnostic and semantic-token registrations are retained.
- Roslyn's dynamically registered semantic-token legend is decoded.
- Hover ranges use the server's camel-case JSON fields and empty hovers are suppressed.
- Release builds contain no `Microsoft.CodeAnalysis.*Features` dependency.
- Razor delegates HTML features through its synchronized virtual document.
- Completion preserves snippets, insert/replace ranges, additional edits, resolve data, and negotiated commands.
- Code actions preserve resolve/commands and transactional workspace resource operations require preview confirmation.
- Watched files, request timeouts, restart replay, redacted failures, notices, hashes, and SPDX inventory are covered.
- The local release qualification passes 118 tests with a warning-free build.

Current verification command:

```sh
tools/qualify-release.sh
```

## Resolved blocker: live Problems

Live Problems and editor squiggles now retain pull diagnostics across LSP `unchanged` reports. Pull state is tracked per server, document, and diagnostic identifier, and `previousResultId` is sent on subsequent requests. Responses for superseded editor versions are rejected instead of being published against the latest snapshot.

Roslyn's `workspace/diagnostic/refresh` request now triggers analysis through the workbench/editor path. The timing retries previously in `CodeEditor` were removed.

Regression coverage verifies retained `unchanged` reports, stale editor-version rejection, and rapid edits against the packaged Roslyn server.

## Semantic colouring decision

The editor uses an immediate lexical presentation baseline and atomically overlays server semantic tokens only when they belong to the exact current document version. Stale semantic spans are never translated across edits. The baseline and semantic categories share the same CSS theme variables, so typing remains stable while Roslyn remains authoritative for semantic distinctions.

The local tokenizer now includes Rider-style categories for namespaces, types, enum names/members, records, parameters, events, accessors, attributes, and embedded `GeneratedRegex` patterns. Theme colours are CSS variables in `wwwroot/app.css`.

This resolves the temporary deviation from `phase-15-2-LSPs.md`: the server is the semantic authority, while the local tokenizer is the latency-safe syntax presentation layer. Colours remain theme-controlled rather than server-controlled.

## Important implementation changes

- `LanguageDocumentCoordinator`: ordered incremental edits, CRLF/surrogate-safe ranges, synchronization before dependent requests.
- `LanguageServerManager`: Roslyn `solution/open`/`project/open`, crash stderr, safer cleanup, diagnostic capability negotiation.
- `LspClient`: null omission, dynamic registration storage, configuration responses.
- `LspLanguageProvider`: registered pull-diagnostic streams, merged diagnostics, dynamic semantic legend.
- `WorkbenchPanel`: restored-document replay and analysis trigger, persistent server indicators.
- `ProjectSystem`: source saves no longer count as project inputs.
- `CodeEditor`: cancellable language refreshes, local stable colouring, diagnostic retry attempt.
- `EditorCore`/`app.css`: local Rider-style tokenizer and theme palette.

## Remaining release evidence

Do not mark Phase 15.2 complete until the final four-platform workflow is green. Native UI interaction remains automated under Linux/Xvfb; Windows and macOS run the same Release build, tests, real-server integration, and package checks without an interactive desktop session.
