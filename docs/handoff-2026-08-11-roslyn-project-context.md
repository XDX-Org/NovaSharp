# Roslyn project-context handoff

Date: 2026-08-11  
Branch: `phase-12-14`  
Baseline: `a62f000 Synchronize editor presentation with Roslyn readiness`

## Completed

- [x] Removed semantic-presentation caching and provisional token reuse.
- [x] Made typing, undo, redo, and external document changes request fresh semantics and diagnostics.
- [x] Preserved existing colours while undo/redo semantic requests are running.
- [x] Wait for Roslyn's `workspace/projectInitializationComplete` notification before marking the server ready.
- [x] Send `didOpen` and request semantic tokens only after Roslyn project initialization completes.
- [x] Restore solution-local build configuration from `.Nova` when explicitly loading a solution.
- [x] Add multiline brace guides that avoid drawing through opening and closing brace glyphs.
- [x] Verify the baseline with 156 passing tests and a warning-free build.
- [x] Commit and push the baseline to `origin/phase-12-14`.

## Next task: `_vs_projectContext`

Status: Not implemented.

Add Roslyn project-context support so linked files and multi-targeted projects use the selected compilation context instead of Roslyn's default context.

### Required protocol flow

1. Advertise `workspace._vs_projectContext.refreshSupport: true` during initialization.
2. Support dynamic/static `_vs_projectContext` registration options and their document selector.
3. Handle Roslyn's `workspace/projectContext/_vs_refresh` server request.
4. Query `textDocument/_vs_getProjectContexts` with:

   ```json
   { "_vs_textDocument": { "uri": "file:///..." } }
   ```

5. Store the returned context list per document URI. It contains:
   - `_vs_projectContexts`
   - `_vs_defaultIndex`
   - `_vs_key`
6. Select the default context initially and retain a user selection by `_vs_key` when contexts refresh.
7. Attach the complete selected context object as `_vs_projectContext` inside `textDocument` for Roslyn requests. Do not send Nova's internal Roslyn `ProjectId` GUID as the LSP context.
8. Refresh contexts after `workspace/projectInitializationComplete`, when Roslyn requests `_vs_refresh`, and when the active document changes.
9. Re-request semantics, diagnostics, completion, hover, navigation, rename, symbols, formatting, and code actions when a document's selected context changes.

### Nova integration points

- `LanguageServerManager`: advertise the capability and expose project-context refresh.
- `LspClient`: handle `workspace/projectContext/_vs_refresh`.
- `LspLanguageProvider`: query/cache context lists and add `_vs_projectContext` to every applicable `textDocument` identifier.
- `WorkbenchPanel`: refresh contexts after project initialization and expose context selection for documents with multiple contexts.
- `CodeEditor`: use the selected LSP context rather than passing the internal `Microsoft.CodeAnalysis.ProjectId` string to the LSP provider.

### Completion criteria

- [ ] A file included by two projects can switch context and gets context-correct semantics, diagnostics, completion, hover, and navigation.
- [ ] A multi-targeted project can select each target-framework context.
- [ ] Context selections survive Roslyn refresh notifications when `_vs_key` remains stable.
- [ ] Miscellaneous context is not selected when a real project context is available.
- [ ] Single-context projects retain current behaviour.
- [ ] Unit tests cover protocol serialization, default selection, refresh, stale-context removal, and request enrichment.
- [ ] A packaged Roslyn integration test proves that switching context changes an observable language result.
- [ ] The full test suite and warning-free build pass.

## References

- Microsoft C# extension protocol: <https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/server/roslynProtocol.ts>
- Microsoft project-context feature: <https://github.com/dotnet/vscode-csharp/tree/main/src/lsptoolshost/projectContext>
- Microsoft request enrichment: <https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/server/roslynLanguageServer.ts>

