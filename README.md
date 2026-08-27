# NovaSharp

**A modern, cross-platform IDE for C#, Razor, Blazor, HTML, and CSS.**

NovaSharp is an open-source development environment focused on modern .NET application development. It aims to provide a fast, clean, and focused alternative for developers building C# desktop, web, Razor, and Blazor projects.

> [!IMPORTANT]
> NovaSharp is currently an early prototype and is under active development. Features may be incomplete, unstable, or subject to significant change.

## Features

NovaSharp is being designed around the tools needed for productive .NET development:

* C# code editing
* Razor and Blazor support
* HTML and CSS editing
* Solution and project management
* Code completion and IntelliSense-style suggestions
* Syntax highlighting
* Code navigation
* Error and diagnostic reporting
* Integrated building and project execution
* Debugging support
* Breakpoints and stepping
* Local variable inspection
* Call stack inspection
* Extensible editor and tooling architecture
* Cross-platform support

Some features listed above are still in development and may not yet be fully available.

## Goals

NovaSharp aims to be:

* **Focused** — built specifically around C# and modern .NET workflows
* **Fast** — Monaco owns the latency-sensitive editing path; I/O and expensive analysis run asynchronously on bounded background workers
* **Cross-platform** — every target in the [supported platform matrix](docs/delivery-plan.md#supported-platform-matrix) is first-class; no operating system is the reference platform
* **Extensible** — structured to support additional languages, tools, and integrations
* **Open source** — developed openly with community contributions welcomed

## Supported Technologies

NovaSharp is intended to support development with:

* C#
* .NET
* Razor
* Blazor
* HTML
* CSS

Support for additional languages and project types may be introduced as the project develops.

## Project Status

NovaSharp is currently in the **early prototype stage**.

The core architecture and initial IDE functionality are being implemented. It is not yet recommended for production development or as a replacement for an established IDE.

The current workbench can open a workspace, keep multiple files in URI-deduplicated tabs, reuse a single preview tab,
reorder and close tabs by pointer or keyboard, and restore open files plus cursor, selection, and scroll state after an
orderly restart. Phases 1–6 are complete and qualified on every supported platform. Groups split in four directions,
tabs move or copy by command or drag/drop, duplicate editors share one document model and undo history, and layouts
restore safely.
Choose the optional Fast Mono font from View → Change Editor Font… or the command palette.

Phase 6 is complete and qualified. Open an SDK-style `.sln`, `.slnx`, or `.csproj` from
the Explorer, Workspace menu, command palette, or solution picker. NovaSharp displays evaluated project contexts, files,
and references; keeps dirty editor replicas synchronized with Roslyn; reloads project state after relevant file changes;
and offers a project-context selector for linked or multi-target documents. Phase 7 adds project-aware C# completion and
snippets, signature help, hover, formatting, and semantic tokens through Monaco's native provider UI. Its implementation
is awaiting six-row qualification.

Expect:

* Missing features
* Incomplete language support
* Debugging issues
* Breaking changes
* Limited documentation
* Frequent architectural changes

## Local development

### Requirements

Every supported platform requires:

* Git
* A [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); `global.json` accepts the latest installed .NET 10 feature band
* A shell that can run the bootstrap entry point for that platform
* Network access for the initial dependency bootstrap
* About 1 GB of free space for NuGet packages, Monaco, Node.js, Roslyn/Razor, and web-language services

Node.js and npm are **not** separate prerequisites. The bootstrap downloads a pinned Node.js runtime, verifies its SHA-256, and uses it
for Monaco and the web-language servers.

### Supported platforms

NovaSharp treats every runtime identifier below as a first-class target. The table is ordered alphabetically and that ordering carries no
priority: no operating system is the reference platform, and a feature is not finished while it works on only some of these rows.

| Runtime identifier | Host runtime prerequisites | Bootstrap prerequisites | Pinned assets | Automated smoke test |
|---|---|---|---|---|
| `linux-arm64` | GTK 3, libnotify, WebKitGTK 4.1 | POSIX shell, `curl`, `jq`, `tar`, `unzip`, XZ support | Yes | Passing in run 32575928633 |
| `linux-x64` | GTK 3, libnotify, WebKitGTK 4.1 | POSIX shell, `curl`, `jq`, `tar`, `unzip`, XZ support | Yes | Passing in run 32575928633 |
| `osx-arm64` | System WebKit | POSIX shell, `curl`, `jq`, `tar`, `unzip` | Yes | Passing in run 32575928633 |
| `osx-x64` | System WebKit | POSIX shell, `curl`, `jq`, `tar`, `unzip` | Yes | Passing in run 32575928633 |
| `win-arm64` | WebView2 Evergreen Runtime | PowerShell 5.1 or later | Yes | Passing in run 32575928633 |
| `win-x64` | WebView2 Evergreen Runtime | PowerShell 5.1 or later | Yes | Passing in run 32575928633 |

*Pinned assets* means the runtime identifier has verified SHA-256 entries in the language-server asset manifest. *Automated smoke test*
means a launch-and-edit check runs unattended on that platform in CI. No row is complete until both columns read yes; see the
[delivery plan](docs/delivery-plan.md) for the parity rule that governs them.

Host runtime prerequisites are inherited from
[Photino.NET](https://github.com/tryphotino/photino.NET#how-to-build-this-repo). Package names differ between operating
systems and between Linux distributions, so install the bootstrap prerequisites with whichever package manager the
platform uses. One example, for Debian and Ubuntu family systems:

```bash
sudo apt-get update
sudo apt-get install curl jq tar unzip xz-utils libnotify4 libwebkit2gtk-4.1-0
```

Equivalent packages exist for other distributions, for Homebrew or MacPorts on macOS, and through the Windows runtime installer linked
above. The bootstrap is meant to check its prerequisites before downloading anything; if a check for your platform is missing, that is a
bug worth reporting rather than a reason to install tools by trial and error.

### Clone, bootstrap, and run

```bash
git clone https://github.com/XDX-Org/NovaSharp.git
cd NovaSharp
```

Run the bootstrap entry point for your shell. The two entry points are equivalent and produce the same asset tree:

```bash
# POSIX shell
./tools/setup.sh
```

```powershell
# PowerShell
pwsh -File tools/setup.ps1
```

On Windows PowerShell 5.1, or wherever the execution policy blocks local scripts, prefix the invocation with
`-ExecutionPolicy Bypass`. Then, on every platform:

```bash
dotnet run --project src/NovaSharp/NovaSharp.csproj --no-build
```

The bootstrap is idempotent. It automatically:

1. Detects the local runtime identifier.
2. Downloads and SHA-256 verifies the pinned Roslyn C# language server, matching Razor cohost, and Node.js runtime.
3. Installs the lockfile-pinned HTML, CSS, JavaScript, and TypeScript language-server packages with the downloaded Node.js runtime.
4. Restores the lockfile-pinned Monaco Editor, Codicons, and Inter; builds their local bundles, workers, Fast Mono,
   fonts, brand asset, and licenses.
5. Restores the pinned Roslyn/MSBuild NuGet packages and the remaining .NET dependencies.
6. Verifies the Monaco and workbench asset manifests and builds `NovaSharp.slnx`.

Downloaded and generated assets remain local and are ignored by Git. NovaSharp does not require a CDN or download language servers at
runtime. Exact versions, sources, hashes, licenses, and update rules are in [language-server assets](docs/language-server-assets.md).

To force a clean dependency reacquisition:

```bash
# POSIX shell
./tools/setup.sh --force
```

```powershell
# PowerShell
pwsh -File tools/setup.ps1 -ForceAssets
```

Never bypass a hash or lockfile failure. Update the relevant manifest and notices as one reviewed dependency change.

### Publishing

Language-server assets are runtime-identifier specific, so a publish must name one:

```bash
dotnet publish src/NovaSharp/NovaSharp.csproj -r <runtime-identifier> -c Release
```

Publishing without `-r` produces an application with no Roslyn, Razor, Node, or web-language payload. Treat a runtime-identifier-less
publish as a packaging error rather than a supported configuration.

### Tests

`NovaSharp.slnx` contains `tests/NovaSharp.Tests`, which `dotnet test NovaSharp.slnx` runs. A second suite,
[`tests/editor-host`](tests/editor-host/README.md), drives the packaged editor in a real browser to assert what only a
browser can show: worker startup, no runtime network access, deterministic disposal, and that the edit batches the host
produces reconstruct Monaco's text exactly.

[CI](.github/workflows/ci.yml) runs both, plus bootstrap, RID-specific publish, the published native-host smoke, and
performance measurements on every runtime identifier from the same commit. Each row retains its application and JSON
evidence. The native verifier records disposable browser-profile provisioning separately before its repeatable process
startup measurements and gates the median of three warm launches. [Qualification run 33088968049](https://github.com/XDX-Org/NovaSharp/actions/runs/33088968049)
passes every gate through Phase 6 on all six supported runtime identifiers.

> [!NOTE]
> Monaco is mounted and is the only editor, and the document lifecycle around it — asynchronous edit replication, dirty
> state, safe save and reload, encoding and line-ending handling, and external-change resolution — is in place.
> The Phase 3 workspace Explorer implementation is also present, with lazy folders, bounded watcher recovery, file
> operations, accessible incremental tree rendering, and versioned state. Phase 4's multi-document tab implementation
> Phase 5's editor groups and shared-model split views, and Phase 6's solution/Roslyn implementation are complete and
> qualified. Phases 1–6 are complete: their implementation,
> foundations, native host, browser behavior, performance, cancellation, disposal, packaging, and retained-evidence
> gates pass on every supported runtime identifier.

See the [phase documentation](docs/README.md) for current scope and verification gates.

## Roadmap

Development follows the detailed [phase roadmap](docs/README.md) and [delivery gates](docs/delivery-plan.md). The preview roadmap covers:

* Shipping Monaco Editor as the only editor from the first phase
* Expanding C# language services
* Implementing reliable project and solution loading
* Adding project-aware Razor, Blazor, HTML, and CSS support after the C# workbench
* Integrating build and run workflows
* Expanding debugger functionality
* Correctly resolving source locations and symbols
* Improving local variable and call stack information
* Building a versioned, permission-aware extension architecture
* Improving performance and reliability
* Producing signed, tested, recoverable preview packages for supported platforms

Each item is delivered for every supported platform at once; a roadmap item is not finished while it works on some and not others. The [architecture notes](docs/ide-roadmap-research.md) define the editor boundary and required async/concurrency model. UI callbacks must remain short: file I/O, project evaluation, Roslyn work, search, process streams, and debugger traffic may not block Monaco or the Blazor renderer.

The roadmap will evolve as the foundations of the IDE mature.

## Contributing

Contributions, testing, bug reports, and technical feedback are welcome.

Because NovaSharp is still at an early stage, it is recommended that contributors open an issue before beginning a large change. This helps avoid duplicated work and ensures that proposed changes fit the current architecture.

When reporting a bug, include:

* Your operating system, version, and processor architecture
* Whether you have reproduced the issue on any other platform
* Your installed .NET SDK version
* Steps to reproduce the issue
* Expected behaviour
* Actual behaviour
* Relevant logs or screenshots

## Feedback

NovaSharp is being shaped around real-world .NET development workflows. Suggestions regarding editor behaviour, debugging, Razor support, project management, performance, and usability are encouraged.

Please use GitHub Issues for bug reports and feature requests.

## License

See the repository's `LICENSE` file for licensing information.
