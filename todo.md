# Current Phase TODO

Use this file to track things that are broken, forgotten, or need investigation during the current phase.

## To Do

- Git integration.
- NuGet explorer.
- Performance window for the currently debugging/running app.
- TODO viewer that collects TODO comments and displays them in one convenient, clickable list.
- Investigate eager initialization of the whole solution on load so syntax highlighting, diagnostics, and related language features become available sooner.
- Allow each LSP to be disabled independently, persist the setting per project, and avoid loading language servers for languages the project does not use (for example, JavaScript).
- Database integration: connect the IDE to databases and browse, add, edit, and delete data.
- Proper brace-pair guide lines in the editor.
- Provide contextual fix actions when right-clicking warnings, errors, and other diagnostics.
- Code navigation: Ctrl+click a member to go to its definition.
- Add member context-menu actions, including Find Usages and Rename.
- Investigate and implement Rider-style "pencils."
- ILSpy integration for decompiling external modules and debugging into them.
- Configurable indentation: tabs or spaces, tab width (such as 2 or 4 spaces), and convert indentation entirely to tabs or spaces.
- AI integration.
- Improve the IDE's icons.
- Configurable fonts and colours.
- Theme support.
- Investigate Postman-style API testing: compose, send, save, and inspect HTTP requests and responses within the IDE.
- Remote development over SSH: connect to and work with projects on another machine.
- Profiling tools comparable to dotTrace and dotMemory: capture performance snapshots, inspect CPU hot paths, and analyze memory usage and allocations.
- Option to pin `obj` and `bin` directories to the top of their project folders.
- Show files with uncommitted changes in a distinct colour in the project explorer.
- Show the cursor line and column, current file encoding, and line-ending type in the bottom status bar.
- Investigate Unity and Unreal Engine integration, including project discovery, launching, debugging, and engine-aware tooling.
- Support multiple IDE instances concurrently, with safe LSP sharing or isolated per-instance language servers.
- Localization support for translating the IDE into different languages and selecting the display locale.
- Support left-to-right and right-to-left text direction across the editor and IDE interface.
- Handle systems with no .NET SDK/runtime installed: detect the condition, remain usable, explain unavailable features, and guide the user through installation and configuration.
- Docker integration for building, running, debugging, and managing containerized projects.
- Hot Reload support while running and debugging applications.
- Kubernetes integration for viewing, deploying, debugging, and managing workloads and clusters.
- Tests panel for discovering, filtering, running, debugging, and reviewing test results.
- Code formatting commands and configurable format-on-save support.
- Side-by-side diff and merge tool for comparing files, revisions, and Git changes.
- Optional configurable background images for the editor and IDE interface, including opacity and layout controls.
- Configurable hard wrapping at a selected column, with commands to reflow existing text.
- Explorer option to highlight files that are currently open in editor tabs.
- Group all Nova child processes under the main Nova process in the operating system's process monitor.
- Fix the Problems panel's solution-wide scope: it does not currently show problems across the whole solution as its dropdown indicates.
- Add explorer context actions to reveal an item in the OS file manager (such as Dolphin or Windows Explorer) and open files with external applications, including images in the system image viewer.
- OS shell integration: context-menu actions to open folders and solutions in Nova, plus configurable file associations.
- Investigate why the selected build configuration resets unexpectedly, then fix its persistence or rework build-configuration handling.
- Improve handling of multiple concurrent and restored IDE sessions.

## Investigating

- None yet.

## Done

- None yet.
