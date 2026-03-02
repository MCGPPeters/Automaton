# Installation

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- A C# editor (Visual Studio, Rider, VS Code with C# Dev Kit)

## Install the Package

```bash
dotnet add package Automaton
```

Or add it to your `.csproj`:

```xml
<PackageReference Include="Automaton" Version="1.*" />
```

## Verify

Create a new console project and confirm it builds:

```bash
dotnet new console -n MyAutomaton
cd MyAutomaton
dotnet add package Automaton
dotnet build
```

## Project Configuration

Automaton targets `net10.0` and uses the latest C# language features. Ensure your project targets .NET 10.0:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

## What's Included

The `Automaton` package contains everything you need:

| Type | Purpose |
| ---- | ------- |
| `Automaton<TState, TEvent, TEffect>` | The kernel interface |
| `AutomatonRuntime<...>` | Thread-safe async runtime |
| `Observer<...>` | Transition observer delegate |
| `Interpreter<...>` | Effect interpreter delegate |
| `ObserverExtensions.Then` | Observer composition |
| `Decider<...>` | Command validation interface |
| `DecidingRuntime<...>` | Command-validating runtime |
| `Result<TSuccess, TError>` | Discriminated union for error handling |
| `AutomatonDiagnostics` | OpenTelemetry-compatible tracing |

No additional packages are required. The library has **zero dependencies** beyond the .NET runtime.

## Coming Soon: Automaton.Patterns

The `Automaton.Patterns` package will provide production-ready patterns built on the kernel:

- **Event Sourcing** — EventStore, AggregateRunner, Projections, ConflictResolver
- **Saga** — Multi-aggregate coordination

This package is currently in development. See the [Patterns placeholder](../patterns/index.md) for details.

## Next Steps

- [**Quick Start**](index.md) — Build a counter in 5 minutes
- [**The Kernel**](../concepts/the-kernel.md) — Understand the core abstraction
- [**Tutorial 01**](../tutorials/01-getting-started.md) — Your first real automaton
