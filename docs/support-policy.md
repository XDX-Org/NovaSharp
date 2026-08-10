# Preview support policy

NovaSharp `0.x` builds are previews. Only the newest preview receives fixes; persisted schemas may migrate forward and downgrade is not guaranteed. Release notes identify migrations and rollback limits.

Supported targets are Windows x64, Linux x64, macOS arm64, and macOS x64 on the CI images recorded in the delivery plan. SDK-style .NET projects, C#, Razor/Blazor, HTML, and CSS are in scope. Source-control UI, remote workspaces, collaboration, notebooks, designers, profiling, and non-.NET debugging are unsupported.

Report reproducible defects in the repository issue tracker. Security defects belong in private vulnerability reporting. Include the NovaSharp version, OS, .NET SDK version, and sanitized diagnostic export.
