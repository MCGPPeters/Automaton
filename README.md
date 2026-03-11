# ⚠️ This repository has moved

**Automaton has been restructured into the [Picea ecosystem](https://github.com/picea).**

All active development now happens across the Picea organization repos:

| Package | New Location | Description |
|---------|-------------|-------------|
| **Picea** | [picea/picea](https://github.com/picea/picea) | Core kernel: `Automaton<>`, `Result<>`, `Decider<>`, Runtime, Diagnostics |
| **Picea.Abies** | [picea/abies](https://github.com/picea/abies) | MVU framework for Blazor (Browser, Server, Kestrel, Analyzers, Templates) |
| **Picea.Mariana** | [picea/mariana](https://github.com/picea/mariana) | Resilience patterns: Retry, Circuit Breaker, Rate Limiter, Hedging, Timeout, Fallback |
| **Picea.Glauca** | [picea/glauca](https://github.com/picea/glauca) | Event Sourcing: AggregateRunner, EventStore, Projections, KurrentDB adapter |
| **Picea.Rubens** | [picea/rubens](https://github.com/picea/rubens) | Actor model: Actor, Address, Envelope, Reply |

## Migration Guide

Replace your NuGet package references:

```xml
<!-- Before -->
<PackageReference Include="Automaton" Version="..." />

<!-- After -->
<PackageReference Include="Picea" Version="1.0.0-rc.1" />
```

For the MVU framework:

```xml
<!-- Before (Abies 1.x) -->
<PackageReference Include="Abies" Version="..." />

<!-- After (Picea.Abies 2.x) -->
<PackageReference Include="Picea.Abies.Browser" Version="2.0.0-rc.1" />
<!-- or -->
<PackageReference Include="Picea.Abies.Server" Version="2.0.0-rc.1" />
```

Backward-compatible metapackages (`Abies`, `Abies.Browser`, `Abies.Server`) are available that forward to the new `Picea.Abies.*` packages.

## Why the move?

The monorepo was split into focused repos with independent release cycles:

- **Each pattern library ships independently** — resilience, event sourcing, and actor model evolve at their own pace
- **The kernel is the stable foundation** — `Picea` is the genus, everything else is a species
- **Namespace-as-bounded-context** — package names reflect domain boundaries (`Picea.Mariana` for resilience, `Picea.Glauca` for event sourcing)

---

> This repository is archived and read-only. No further updates will be made here.
> All issues, PRs, and discussions should be directed to the appropriate [Picea org](https://github.com/picea) repo.
