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
* **Fast** — responsive during editing, navigation, building, and debugging
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

## Building NovaSharp

Clone and run NovaSharp:

```bash
git clone https://github.com/XDX-Org/NovaSharp.git
cd NovaSharp
dotnet build NovaSharp.slnx
dotnet run --project src/NovaSharp/NovaSharp.csproj
```

See the [phase documentation](docs/README.md) for the current scope and verification steps.

## Roadmap

Development follows the detailed [phase roadmap](docs/README.md) and [delivery gates](docs/delivery-plan.md). The preview roadmap covers:

* Improving the code editor
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
