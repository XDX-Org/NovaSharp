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
* **Cross-platform** — designed to run across major desktop operating systems
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

Expect:

* Missing features
* Incomplete language support
* Debugging issues
* Breaking changes
* Limited documentation
* Frequent architectural changes

## Local development

### Requirements

All platforms require:

* Git
* A [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); `global.json` accepts the latest installed .NET 10 feature band
* Internet access for the initial dependency bootstrap
* About 1 GB of free space for NuGet packages, Monaco, Node.js, Roslyn/Razor, and web-language services

Node.js and npm are **not** separate prerequisites. The bootstrap downloads a pinned Node.js runtime, verifies its SHA-256, and uses it
for Monaco and the web-language servers.

Platform requirements:

| Platform | Additional requirements |
|---|---|
| Windows 10/11 x64 | Windows PowerShell 5.1+ or PowerShell 7; [WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/consumer/) |
| Linux x64/arm64 | GTK 4 and WebKitGTK 6 runtime libraries; Bash, `curl`, `jq`, `unzip`, `tar`, and XZ support |
| macOS Intel/Apple Silicon | Bash, `curl`, `jq`, `unzip`, and `tar`; the system WebKit runtime |

PhotinoXDX defines the host requirements as .NET 10, WebView2 on Windows, GTK 4 plus WebKitGTK 6 on Linux, and Intel or Apple Silicon
on macOS ([PhotinoXDX requirements](https://github.com/XDX-Org/PhotinoXDX#requirements)). Package names vary by Linux distribution.
For Ubuntu/Debian-family systems, the usual development setup is:

```bash
sudo apt-get update
sudo apt-get install curl jq tar unzip xz-utils libgtk-4-1 libwebkitgtk-6.0-4
```

On macOS, install `jq` if it is not already available, for example with `brew install jq`.

### Clone, bootstrap, and run

Windows PowerShell:

```powershell
git clone https://github.com/XDX-Org/NovaSharp.git
Set-Location NovaSharp
powershell -ExecutionPolicy Bypass -File tools/setup.ps1
dotnet run --project src/NovaSharp/NovaSharp.csproj --no-build
```

Linux or macOS:

```bash
git clone https://github.com/XDX-Org/NovaSharp.git
cd NovaSharp
bash tools/setup.sh
dotnet run --project src/NovaSharp/NovaSharp.csproj --no-build
```

The setup command is idempotent. It automatically:

1. Detects the local runtime identifier.
2. Downloads and SHA-256 verifies the pinned Roslyn C# language server, matching Razor cohost, and Node.js runtime.
3. Installs the lockfile-pinned HTML, CSS, JavaScript, and TypeScript language-server packages with the downloaded Node.js runtime.
4. Restores the lockfile-pinned Monaco Editor and builds its ESM bundle, C# / HTML / CSS definitions, editor worker, fonts, and licenses.
5. Restores the pinned Roslyn/MSBuild NuGet packages and the remaining .NET dependencies.
6. Verifies the Monaco asset manifest and builds `NovaSharp.slnx`.

Downloaded and generated assets remain local and are ignored by Git. NovaSharp does not require a CDN or download language servers at
runtime. Exact versions, sources, hashes, licenses, and update rules are in [language-server assets](docs/language-server-assets.md).

To force a clean dependency reacquisition:

```powershell
powershell -ExecutionPolicy Bypass -File tools/setup.ps1 -ForceAssets
```

```bash
bash tools/setup.sh --force
```

Never bypass a hash or lockfile failure. Update the relevant manifest and notices as one reviewed dependency change.

> [!NOTE]
> The current branch is still an early editor-shell prototype. Bootstrap makes every planned editor/language dependency reproducibly
> available now; feature integration remains governed by the phase roadmap.

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

The [architecture notes](docs/ide-roadmap-research.md) define the editor boundary and required async/concurrency model. UI callbacks must remain short: file I/O, project evaluation, Roslyn work, search, process streams, and debugger traffic may not block Monaco or the Blazor renderer.

The roadmap will evolve as the foundations of the IDE mature.

## Contributing

Contributions, testing, bug reports, and technical feedback are welcome.

Because NovaSharp is still at an early stage, it is recommended that contributors open an issue before beginning a large change. This helps avoid duplicated work and ensures that proposed changes fit the current architecture.

When reporting a bug, include:

* Your operating system
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
