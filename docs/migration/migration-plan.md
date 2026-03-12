# Picea Ecosystem Migration Plan

> **Created:** 2026-03-09
> **Status:** IN PROGRESS
> **Source:** MCGPPeters/Automaton (branch: feat/ssr)
> **Target:** Picea GitHub Organization

---

## Overview

Consolidate MCGPPeters/Automaton into the Picea GitHub organization as multiple repos, preserving git history by rebasing Automaton commits on top of existing Picea/Abies history.

## Taxonomy

| Repo | Species | Common Name | Purpose | NuGet Packages |
|------|---------|-------------|---------|----------------|
| `Picea/Picea` | — (genus) | Spruce | Core kernel | `Picea` |
| `Picea/Abies` | — (sister genus, fir) | Fir | MVU framework | `Abies`, `Abies.Browser`, `Abies.Server`, `Abies.Server.Kestrel`, `Abies.Analyzers`, `Abies.Templates` |
| `Picea/Glauca` | *Picea glauca* | White spruce | Event Sourcing patterns | `Picea.Glauca`, `Picea.Glauca.KurrentDB` |
| `Picea/Mariana` | *Picea mariana* | Black spruce | Resilience patterns | `Picea.Mariana` |
| `Picea/Rubens` | *Picea rubens* | Red spruce | Actor model | `Picea.Rubens` |

## Dependency Graph

```
Picea (kernel) ← no framework dependency
    ↑
    ├── Picea/Mariana    — Resilience (depends on Picea NuGet)
    ├── Picea/Abies      — MVU framework (depends on Picea NuGet)
    │     ├── Abies.Browser
    │     ├── Abies.Server → Abies.Server.Kestrel
    │     └── Abies.Analyzers
    ├── Picea/Glauca     — Event Sourcing (depends on Picea NuGet)
    │     └── Picea.Glauca.KurrentDB
    └── Picea/Rubens     — Actor model (depends on Picea NuGet)
```

## Version Strategy

| Repo | Starting Version | Rationale |
|------|-----------------|-----------|
| Picea/Picea | `1.0-rc.1` | New repo, first release candidate |
| Picea/Abies | `1.0-rc.2` (continue) | Existing repo, tag `v1.0.0-rc.1` exists |
| Picea/Glauca | `0.1` | Experimental |
| Picea/Mariana | `0.1` | Experimental |
| Picea/Rubens | `0.1` | Experimental |

## Git History Strategy

- **Picea/Picea**: Create new repo → add Automaton as remote → rebase kernel commits
- **Picea/Abies**: Clone existing → add Automaton as remote → rebase MVU branch on top
- **Picea/Glauca**: Create new repo → `git filter-repo` from Automaton.Patterns/EventSourcing
- **Picea/Mariana**: Create new repo → `git filter-repo` from Automaton.Resilience
- **Picea/Rubens**: Create new repo → `git filter-repo` from Automaton/Actor

---

## Phase 1: Picea/Picea — The Kernel Repo 🏗️

*Creates the foundational package that everything depends on.*

### 1.1 Repository Setup
- [ ] Create `Picea/Picea` GitHub repo
- [ ] Clone locally
- [ ] Add MCGPPeters/Automaton as remote
- [ ] Rebase/cherry-pick kernel-relevant commits

### 1.2 Project Structure
- [ ] Initialize `global.json` (.NET 10)
- [ ] Initialize `Directory.Build.props` (version, metadata, copyright)
- [ ] Initialize `.editorconfig`
- [ ] Initialize `version.json` (Nerdbank.GitVersioning, `1.0-rc.1`)
- [ ] Create `Picea.sln`
- [ ] Set up `Picea/` project:
  - [ ] `Picea.csproj`
  - [ ] `Automaton.cs` (kernel interface)
  - [ ] `Runtime.cs` (AutomatonRuntime)
  - [ ] `Decider.cs` (Decider interface + DecidingRuntime)
  - [ ] `Result.cs` (Result<TSuccess, TError>)
  - [ ] `Option.cs` (Option<T>)
  - [ ] `Diagnostics.cs` (AutomatonDiagnostics)
  - [ ] `Command.cs`, `Message.cs` (if applicable)
  - [ ] Update all namespaces: `Automaton` → `Picea`
- [ ] Set up `Picea.Tests/` project:
  - [ ] Copy from `Automaton.Tests/`
  - [ ] Update namespaces
- [ ] Set up `Picea.Benchmarks/` project:
  - [ ] Copy from `Automaton.Benchmarks/`
  - [ ] Update namespaces

### 1.3 Documentation
- [ ] Create `README.md` (adapted from Automaton README)
- [ ] Port `docs/adr/` (ADR-001 through ADR-013)
- [ ] Port `docs/concepts/` (composition, glossary, runtimes-compared, the-decider, the-kernel, the-runtime)
- [ ] Port `docs/guides/` (building-custom-runtimes, error-handling-patterns, observer-composition, testing-strategies, upgrading-to-decider)
- [ ] Port `docs/patterns/`
- [ ] Port `docs/getting-started/`
- [ ] Port `docs/reference/`
- [ ] Port `docs/tutorials/`
- [ ] Create `CONTRIBUTING.md`
- [ ] Create `SECURITY.md`
- [ ] Create `CHANGELOG.md`
- [ ] Update `LICENSE` (Apache 2.0, Maurice Peters)

### 1.4 GitHub Configuration
- [ ] `.github/workflows/pr-validation.yml`
- [ ] `.github/workflows/cd.yml` (NuGet publish on merge to main)
- [ ] `.github/workflows/codeql.yml`
- [ ] `.github/workflows/benchmarks.yml`
- [ ] `.github/CODEOWNERS`
- [ ] `.github/dependabot.yml`
- [ ] `.github/pull_request_template.md`
- [ ] `.github/ISSUE_TEMPLATE/bug_report.md`
- [ ] `.github/ISSUE_TEMPLATE/feature_request.md`
- [ ] `.github/instructions/csharp.instructions.md`
- [ ] `.github/instructions/ddd.instructions.md`
- [ ] `.github/instructions/pr.instructions.md`
- [ ] `.github/instructions/memory.instructions.md`

### 1.5 Templates
- [ ] Add `dotnet new picea-automaton` template

### 1.6 Validation
- [ ] `dotnet build` passes
- [ ] `dotnet test` passes (all unit tests)
- [ ] `dotnet format --verify-no-changes` passes
- [ ] Benchmarks run without regressions
- [ ] NuGet package builds correctly

### 1.7 Post-Phase-1
- [ ] Deprecation notice on MCGPPeters/Automaton → points to Picea/Picea

---

## Phase 2: Picea/Abies — The MVU Framework 🌲

*Overwrites old Abies with Automaton's Abies code, rebasing history.*

### 2.1 Git History
- [ ] Clone Picea/Abies
- [ ] Create backup branch `archive/pre-automaton`
- [ ] Add MCGPPeters/Automaton as remote
- [ ] Rebase Automaton MVU commits on top of Abies history
- [ ] Force-push to main

### 2.2 Project Structure
- [ ] Update `version.json` (continue from `1.0-rc.2`)
- [ ] Replace solution with Automaton's Abies structure
- [ ] Change project references to Picea NuGet: `<PackageReference Include="Picea" />`
- [ ] Port Analyzers (Abies.Analyzers/ + Abies.Analyzers.Tests/) from old Picea/Abies
- [ ] Port/merge templates: `abies-wasm`, `abies-server`
- [ ] Add OTLP proxy endpoint to Abies.Server.Kestrel
- [ ] Configure js-framework-benchmark as git submodule
- [ ] Port Global/ (suppressions)

### 2.3 Documentation
- [ ] Merge old Picea docs + Automaton Abies docs
- [ ] ADRs (continue numbering: ADR-022+)
- [ ] New ADR: "Migration from Automaton to Picea ecosystem"
- [ ] Concepts, guides, tutorials, deep-dives, investigations, reference
- [ ] Port CONTRIBUTING.md, SECURITY.md (adapt from old)
- [ ] Update CHANGELOG.md — new section for v2.0 (Automaton kernel)
- [ ] Update README.md

### 2.4 GitHub Configuration
- [ ] Merge workflows
- [ ] Keep benchmark result history → GH Pages
- [ ] Port instructions/
- [ ] Port scripts/

### 2.5 Validation
- [ ] All tests pass (unit, integration, E2E)
- [ ] Benchmarks: no regressions
- [ ] Lint passes
- [ ] Documentation current

---

## Phase 3: Picea/Glauca — Event Sourcing 🌿

### 3.1 Repository Setup
- [ ] Create `Picea/Glauca` GitHub repo
- [ ] Initialize project scaffold
- [ ] Extract history from Automaton via `git filter-repo`

### 3.2 Project Structure
- [ ] `Picea.Glauca/` — Core ES abstractions
  - [ ] AggregateRunner.cs, EventStore.cs, Projection.cs, StoredEvent.cs
  - [ ] ConflictResolver.cs, ResolvingAggregateRunner.cs, InMemoryEventStore.cs
  - [ ] Diagnostics.cs
- [ ] `Picea.Glauca.KurrentDB/` — KurrentDB adapter
- [ ] `Picea.Glauca.KurrentDB.Tests/`
- [ ] `Picea.Glauca.Tests/`
- [ ] Saga/ (sub-namespace or separate project)
- [ ] Update namespaces
- [ ] NuGet dependency on `Picea` package

### 3.3 Documentation & CI
- [ ] Docs, README, CONTRIBUTING, LICENSE
- [ ] CI/CD: pr-validation, cd (NuGet publish on main)

### 3.4 Validation
- [ ] All tests pass
- [ ] NuGet package builds

---

## Phase 4: Picea/Mariana — Resilience 🌲

### 4.1 Repository Setup
- [ ] Create `Picea/Mariana` GitHub repo
- [ ] Initialize project scaffold
- [ ] Extract history from Automaton.Resilience via `git filter-repo`

### 4.2 Project Structure
- [ ] `Picea.Mariana/` — Resilience patterns
  - [ ] Retry/, CircuitBreaker/, RateLimiter/, Hedging/, Timeout/, Fallback/, Pipeline/
  - [ ] Backoff.cs, ResilienceError.cs, Diagnostics.cs
- [ ] `Picea.Mariana.Tests/`
- [ ] `Picea.Mariana.Benchmarks/` (if applicable)
- [ ] Update namespaces
- [ ] NuGet dependency on `Picea` package

### 4.3 Documentation & CI
- [ ] Docs, README, CONTRIBUTING, LICENSE
- [ ] CI/CD: pr-validation, cd (NuGet publish on main)

### 4.4 Validation
- [ ] All tests pass
- [ ] NuGet package builds

---

## Phase 5: Picea/Rubens — Actor Model 🌲

### 5.1 Repository Setup
- [ ] Create `Picea/Rubens` GitHub repo
- [ ] Initialize project scaffold
- [ ] Extract history from Automaton/Actor via `git filter-repo`

### 5.2 Project Structure
- [ ] `Picea.Rubens/` — Actor abstractions
  - [ ] Actor.cs, Address.cs, Envelope.cs, Reply.cs
- [ ] `Picea.Rubens.Tests/`
- [ ] Update namespaces
- [ ] NuGet dependency on `Picea` package

### 5.3 Documentation & CI
- [ ] Docs, README, CONTRIBUTING, LICENSE
- [ ] CI/CD: pr-validation, cd (NuGet publish on main)

### 5.4 Validation
- [ ] All tests pass
- [ ] NuGet package builds

---

## Post-Migration Cleanup

- [ ] MCGPPeters/Automaton: Archive repo, add deprecation notice → Picea org
- [ ] Create GitHub issue on Picea/Picea: Research `Option<T>` type design
- [ ] Document submodule setup for js-framework-benchmark in Abies CONTRIBUTING.md
- [ ] Add routing guidance document to Abies docs
- [ ] Set up NuGet org for consistent package naming
- [ ] Create Picea org README.md (`.github` repo with profile README)
- [ ] Add compared benchmark results for abies-browser with each publication

---

## Key Decisions Log

| # | Decision | Rationale |
|---|----------|-----------|
| D-001 | Picea org taxonomy: repos named after Picea species | Coherent naming — org is genus, repos are species |
| D-002 | Abies stays as-is (sister genus, not a species) | Already established, different genus = different concern |
| D-003 | Resilience in separate repo (Picea/Mariana) | Clean dependency boundaries |
| D-004 | Actor in separate repo (Picea/Rubens) | Even 4 files warrant separation for clean packaging |
| D-005 | Rebase history (not clean start) | Migration visibility in git history |
| D-006 | Continue from Picea/Abies version 1.0-rc.2 | Maintain version continuity |
| D-007 | Copyright: Maurice Peters, Apache 2.0 | Owner's preference |
| D-008 | NuGet publish only on merge to main | Prevent accidental releases |
| D-009 | Benchmark results only on main build | Consistent baseline |
| D-010 | Phase 1 first (kernel) | Everything depends on it |

---

## Current Progress

**Phase:** 1 — Picea/Picea (Kernel Repo)
**Step:** 1.1 — Repository Setup
**Last Updated:** 2026-03-09
