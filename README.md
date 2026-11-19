# Building AI Agents with C# and .NET 10

<p align="center">
  <img src="assets/cover-front.png" alt="Building AI Agents with C# and .NET 10 — cover" width="420">
</p>

<p align="center"><strong>A Developer's First Guide to the Microsoft Agent Framework</strong></p>

<p align="center"><em>From your first call to an LLM to a production-hosted, observable, safety-filtered multi-agent system — in C# 14 and .NET 10.</em></p>

<p align="center"><strong>📖 <a href="https://leanpub.com/buildingAIAgentsWithCSandNet10">Get the book on Leanpub</a></strong></p>

<p align="center"><sub>This repository hosts the companion code, errata, and reader feedback for the book.</sub></p>

---

## Errata

Corrections in the companion code, with what to change in the printed listings, are in **[ERRATA.md](./ERRATA.md)**. Severity A means "correct before publication or announce as an erratum" in the technical review (or, outside it, a fresh clone did not build or a sample returned a wrong result); B means "correct in the next update". ERRATA.md explains the IDs.

These rows are first-edition errata, so their chapter numbers and project names are the first edition's. The projects now carry second-edition names (for example `Ch08.ShellTool` is `Ch07.ShellTool`); Appendix F of the second edition maps every first-edition project to its new folder.

| Area / chapter | Severity | What was wrong | Fix | Fixed in |
|---|---|---|---|---|
| Ch10, Ch14, Ch16 · RAG provider (B1) | A | `RagContextProvider` needed a mutable `CurrentQuery`: Chapter 14 never set it (no retrieval), Chapter 16 set it on a shared singleton (requests could swap context) | Reads the question from the call's `InvokingContext`; `CurrentQuery` removed | v1.1.0 (companion code) |
| Ch08 · `Ch08.ShellTool` (R02) | A | Allow-list let `dotnet` and `find` run arbitrary code; "sandbox" wording; on Windows every viewer failed to start and the model invented results | Read-only viewers in a scratch folder; not called a sandbox; a missing program is reported, `dir`/`type` run through cmd with metacharacters refused | v1.1.0 (companion code) |
| Ch13 · `Ch13.ApprovalWorkflow` (R03) | A | Empty input, typos or end of input approved the document | Only `approve`, `revise` or `reject`; anything else stays pending | v1.1.0 (companion code) |
| Ch08 · `Ch08.ToolApproval` (R04) | A | An approved refund could run twice | Idempotent per order | v1.1.0 (companion code) |
| Ch16 · `Ch16.ToolApproval` (R05) | A | Refunds of -10 and 0 were auto-approved; culture-dependent parsing | Only `0 < amount <= 50`, invariant parsing | v1.1.0 (companion code) |
| Ch04 · configuration (R06) | B | User secrets, Azure key, `ANTHROPIC_API_KEY` and README keys did not work as described | Working recipe and `--config-check` | v1.1.0 (companion code) |
| Ch18 · evals (R07) | A | `Contains("15")` passed "115 days" and was called a grounding check | Exact value check with counterexamples | v1.1.0 (companion code) |
| Ch13 · checkpoint resume (R08) | B | "Restart" ran in one process with in-memory checkpoints; demos approved implicitly, even an empty draft | `prepare`/`resume` across two processes on a file store; simulated decisions labelled, empty drafts never approved | v1.1.0 (companion code) |
| Ch14 · hosted routes (R09) | B | Routes were single-turn without saying so | Documented as single-turn | v1.1.0 (companion code) |
| Common · retries (R10) | B | SDK and HTTP handler both retried: up to 16 requests per call | One retry owner | v1.1.0 (companion code) |
| Ch14 · rate limiting (R11) | B | Every authenticated user fell back to the IP partition | Partition by user id, then IP | v1.1.0 (companion code) |
| Ch10, Ch18 · vector size (R13b) | B | 1536 dimensions declared, 768 produced | 768, checked at startup | v1.1.0 (companion code) |
| Ch07 · confidence (R14) | B | Model confidence trusted and used as a gate | Self-reported, range-checked, never a gate on its own | v1.1.0 (companion code) |
| Repository · Aspire, CI (R15) | B | Aspire behind Chapter 14, `ServiceDefaults` unused, no CI, no lock files | Aligned and wired; lock files and CI workflow | v1.1.0 (companion code) |
| Security pins | A | Aspire < 13.6.5, MessagePack 3.1.7 and System.Text.Json 11 previews failed NuGet audit, so restore failed | Aspire 13.6.5, MessagePack 3.1.12, Microsoft.Extensions.* 10.0.13 | MessagePack in v1.0.1; Aspire and System.Text.Json in v1.0.2 (with Microsoft.Extensions.* 11.0.0); v1.1.0 uses Microsoft.Extensions.* 10.0.13 |
| `global.json` | A | A machine with only a newer SDK could not build | `rollForward: latestMajor` | v1.0.2 (companion code); carried into v1.1.0 |
| All · MAF 1.28 | B | Pinned to an earlier Agent Framework release | MAF 1.28.0, MCP 2.4.0 | v1.1.0 (companion code) |
| README · exercise solutions | B | Claimed a worked answer for every exercise | Says which exercises have solutions | v1.1.0 (companion code) |
| Ch04 · project count | B | "50+ projects"; README sub-totals ambiguous | 127 projects: 125 runnable + 2 libraries | v1.1.0 (companion code) |

---

## What's in this book

The **Microsoft Agent Framework (MAF)** is Microsoft's unified, production-minded SDK for building AI agents in .NET — the consolidation of Semantic Kernel and AutoGen into one stack. This is the developer's first guide to it, built on **Microsoft Agent Framework 1.28.0** and **.NET 10 / C# 14**.

Across twenty-four chapters in six parts you go from your first call to an LLM, through tools and function calling, the Model Context Protocol, retrieval-augmented generation, and **multimodal agents** (vision), into multi-agent orchestration and workflows, all the way to hosting, observability, safety guardrails, agent skills, and evaluation. Every concept is anchored in a runnable companion project in this repository, and every code listing targets **MAF 1.28.0**. The core packages are generally available; where a chapter uses an API Microsoft still marks experimental, or a package published only as a preview, the book says so and the project opts in explicitly (Appendix B lists every package and its status).

By the final chapter you will have built the Contoso FAQ agent — a small but real production system — and developed the intuition to design, build, and ship your own.

## Who this book is for

This book is written for .NET developers who want to build AI-powered agent systems without abandoning the tools and patterns they already know. You should be comfortable with C# fundamentals, have written at least one ASP.NET Core service or console application, and have a working relationship with dependency injection. If you can write a class, use LINQ, and navigate your IDE without a tutorial, you're ready.

No prior machine-learning background is assumed. The book explains what you need to know about how LLMs behave, from a developer's point of view, as you go.

## Table of Contents

| # | Chapter | Part |
|---|---------|------|
| 1 | Choosing the Right Amount of Agency | **I — Understand and Start** |
| 2 | Models, Context and Capabilities | I |
| 3 | A Reproducible Development Environment | I |
| 4 | Your First Agent and Its First Test | I |
| 5 | Typed Responses and Domain Validation | **II — Contracts and Authorized Actions** |
| 6 | Conversations, Memory and Data Ownership | II |
| 7 | Tools and Bounded Execution | II |
| 8 | Authorizing and Committing Agent Actions | II |
| 9 | MCP Across Trust Boundaries | **III — Knowledge and Documents** |
| 10 | Building a Grounded RAG Baseline | III |
| 11 | RAG With Permissions, Freshness and Evidence | III |
| 12 | Reliable Multimodal Document Processing | III |
| 13 | Choosing a Multi-Agent Topology | **IV — Composition and Durable Execution** |
| 14 | Workflows, Branches and Human Review | IV |
| 15 | Durable Execution and External Effects | IV |
| 16 | Skills, Harnesses and Capability Lifecycle | IV |
| 17 | Hosting, Identity and Tenant Isolation | **V — Interfaces, Security and Observation** |
| 18 | Streaming Interfaces and Approval Experiences | V |
| 19 | Observability, Quality and Incident Diagnosis | V |
| 20 | Security Policies and Governance | V |
| 21 | Evaluation With Evidence and Uncertainty | **VI — Qualification and Operations** |
| 22 | Cost, Latency and Capacity Engineering | VI |
| 23 | Qualifying the Contoso Agent System | VI |
| 24 | Operating and Evolving Agents | VI |

Plus 8 appendices (A — C# Essentials for AI Development, B — Package Manifest and Qualification Scope, C — Semantic Kernel to MAF Migration, D — AutoGen to MAF Migration, E — Glossary, F — Exercise Solutions and Project Mapping, G — Troubleshooting by Symptom, H — References and Evidence), followed by a keyword index.

---

## Companion code

**129 projects** in `AgenticAI.slnx`: **124 runnable projects**, that is 79 chapter samples under [`src/`](./src) and 45 worked exercise solutions under [`exercises/`](./exercises), plus five shared libraries under `src/` (the four `AgenticAI.Common*` libraries described below and the Aspire `ServiceDefaults`). Each one is self-contained — you don't need to run everything to follow along, so pick the project for the chapter you're reading.

### Target stack

- **.NET 10** (LTS) / **C# 14**
- **Microsoft Agent Framework 1.28.0** (the first paperback printing was verified against 1.20.0; a few printed listings changed for 1.28.0, and [ERRATA.md](./ERRATA.md) says what to change in each)
- **Microsoft.Extensions.AI** (as pinned in `Directory.Packages.props`)
- Visual Studio 2026, or VS Code with the C# Dev Kit

Every package version is pinned centrally in `Directory.Packages.props` (Central Package Management); individual `.csproj` files reference packages without version numbers. The one exception is the Aspire AppHost (`src/Ch17.HostingIdentity/Ch17.AspireOrchestration/…AppHost`), which opts out because the Aspire SDK adds its own package references; it pins Aspire 13.6.5 and a few patched transitive packages itself. The full, themed package list is **Appendix B** of the book.

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later. The code targets .NET 10 (LTS), and any .NET 10+ SDK, including .NET 11, builds it: `global.json` asks for 10.0.100 with `rollForward: latestMajor`, so the newest installed SDK at or above 10.0.100 is used.
- A model provider. The samples default to **Ollama** running locally — zero cost, no API key — using **llama3.2**. They also support Azure OpenAI, OpenAI, Anthropic, and Microsoft Foundry. The development-environment chapter walks through each one.
- For the **multimodal** samples, a vision-capable model — `ollama pull llava` locally, or a cloud GPT-5.6-class model.

For the zero-config local default:

```bash
ollama pull llama3.2
```

### Clone & build

```bash
git clone https://github.com/RachidD68/building-ai-agents-with-csharp-and-dotnet-10.git
cd building-ai-agents-with-csharp-and-dotnet-10
dotnet build AgenticAI.slnx
```

On Windows, if the clone fails with "Filename too long", run `git config --global core.longpaths true` and clone again.

Open `AgenticAI.slnx` once and every chapter's projects load together.

Every project except the Aspire AppHost commits a `packages.lock.json`. `dotnet restore AgenticAI.slnx --locked-mode` restores exactly those versions and fails if the dependency graph has drifted; after changing a version in `Directory.Packages.props`, a plain `dotnet restore AgenticAI.slnx` refreshes the lock files. The [build workflow](.github/workflows/build.yml) does the same on Windows and Ubuntu, builds with warnings as errors, runs every offline `--self-test` and the console test harnesses, and fails on any vulnerable package. It also runs weekly, because a new NuGet advisory can break a build that nobody touched.

### Run a sample

```bash
dotnet run --project src/Ch04.FirstAgent/Ch04.BasicAgent
```

### Configuration

Provider settings live in each project's `appsettings.json`, under `LlmProvider`. Keys belong in [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) or environment variables — never in source control. Every sample shares one user-secrets store, so a value you set once works in every chapter, and no `dotnet user-secrets init` is needed. Run these from any project folder:

```bash
cd src/Ch03.DevEnvironment/Ch03.FirstLlmCall
dotnet user-secrets set "LlmProvider:Provider" "openai"
dotnet user-secrets set "LlmProvider:OpenAI:ApiKey" "YOUR-KEY"
```

Azure OpenAI works in the samples that reference `AgenticAI.Common.Azure` (see [Shared libraries](#shared-libraries) to add it to any other). It signs in with Microsoft Entra ID by default (run `az login` once; your account needs the *Cognitive Services OpenAI User* role on the resource). An API key is optional:

```bash
dotnet user-secrets set "LlmProvider:Provider" "azure"
dotnet user-secrets set "LlmProvider:AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/"
dotnet user-secrets set "LlmProvider:AzureOpenAI:Model" "YOUR-DEPLOYMENT-NAME"
```

User secrets load in the Development environment, which each sample's launch profile sets (`dotnet run` and Visual Studio both use it). Environment variables work everywhere: `LlmProvider__Provider`, `LlmProvider__OpenAI__ApiKey`, and for Anthropic either `LlmProvider__Anthropic__ApiKey` or `ANTHROPIC_API_KEY`. Microsoft Foundry also signs in through `DefaultAzureCredential`.

### Shared libraries

The provider wiring is split so the default path stays generally available:

| Library | What it adds | Package status |
|---|---|---|
| `src/Common/AgenticAI.Common` | `AddLlmChatClient` with the `openai` and `ollama` providers, settings, `--config-check`, the resilience handler | GA only, transitive packages included (`pwsh tools/check-ga.ps1` proves it from the lock file) |
| `src/Common/AgenticAI.Common.Azure` | the `azure` provider: `services.AddAzureOpenAIProvider()` | prerelease (`Azure.AI.OpenAI` is a beta) |
| `src/Common/AgenticAI.Common.Foundry` | `AddFoundryAgent(...)`, a Microsoft Foundry `AIAgent` | preview (`Microsoft.Agents.AI.Foundry`, `Azure.AI.Projects`) |
| `src/Common/AgenticAI.Common.Anthropic` | `AddAnthropicAgent(...)`, a Claude `AIAgent` | GA |

Each sample references only the libraries it uses. To run a sample that references only `AgenticAI.Common` against Azure OpenAI, add a project reference to `src/Common/AgenticAI.Common.Azure` and one line next to `AddLlmChatClient`:

```csharp
builder.Services.AddAzureOpenAIProvider();   // before or after AddLlmChatClient
builder.Services.AddLlmChatClient(builder.Configuration);
```

Without it, `"Provider": "azure"` stops at startup with a message that says exactly this.

Not sure which settings a sample will use? `dotnet run -- --config-check` lists each one and where it comes from — appsettings.json, user secrets or an environment variable — without printing values or calling a model.

### Repository layout

| Folder | Chapter | Topic |
|--------|---------|-------|
| `src/Common/` | — | Shared configuration, DI, provider wiring, resilience: a GA-only core plus the Azure, Foundry and Anthropic add-ons |
| `src/Ch01.Agency/` | 1 | Choosing the Right Amount of Agency |
| `src/Ch02.ModelsContext/` | 2 | Models, Context and Capabilities |
| `src/Ch03.DevEnvironment/` | 3 | A Reproducible Development Environment |
| `src/Ch04.FirstAgent/` | 4 | Your First Agent and Its First Test |
| `src/Ch05.TypedResponses/` | 5 | Typed Responses and Domain Validation |
| `src/Ch06.ConversationsMemory/` | 6 | Conversations, Memory and Data Ownership |
| `src/Ch07.Tools/` | 7 | Tools and Bounded Execution |
| `src/Ch08.AuthorizedActions/` | 8 | Authorizing and Committing Agent Actions |
| `src/Ch09.MCP/` | 9 | MCP Across Trust Boundaries |
| `src/Ch10.RAGBaseline/` | 10 | Building a Grounded RAG Baseline |
| `src/Ch12.MultimodalDocuments/` | 12 | Reliable Multimodal Document Processing |
| `src/Ch13.MultiAgentTopology/` | 13 | Choosing a Multi-Agent Topology (`Ch13.MagenticOrchestrator` is online only: kept here, not printed in the book) |
| `src/Ch14.Workflows/` | 14 | Workflows, Branches and Human Review |
| `src/Ch16.SkillsHarnesses/` | 16 | Skills, Harnesses and Capability Lifecycle |
| `src/Ch17.HostingIdentity/` | 17 | Hosting, Identity and Tenant Isolation |
| `src/Ch19.Observability/` | 19 | Observability, Quality and Incident Diagnosis |
| `src/Ch20.SecurityGovernance/` | 20 | Security Policies and Governance |
| `src/Ch21.Evaluation/` | 21 | Evaluation With Evidence and Uncertainty |
| `src/Ch24.Operations/` | 24 | Operating and Evolving Agents (a reading list, no project yet) |
| `src/AppA.CSharpEssentials/` | App. A | C# Essentials for AI Development |
| `exercises/` | — | Worked exercise solutions, one `ChNN.Exercises` folder per chapter plus `AppA.Exercises` (indexed in Appendix F) |

Chapters with no folder yet have no companion project in the repository so far. Folder and project names carry the second-edition chapter number; the book refers to them by name.

## Exercise solutions

The [`exercises/`](./exercises) folder holds 45 worked exercise solutions, grouped in one `ChNN.Exercises` folder per chapter (plus `AppA.Exercises` for Appendix A); each MCP-chapter solution is an MCP server plus a consumer, side by side. **Appendix F** indexes them by chapter and difficulty, and maps each first-edition exercise to its new folder.

Not every exercise in the book has a solution here: reflective exercises have no code, and Appendix F marks the exercises that have no companion project.

Each solution runs on its own:

```bash
dotnet run --project exercises/Ch07.Exercises/Ex01.UnitConverterTool
```

Try the exercise yourself first, from the brief in its chapter, before you reach for the code here.

## Errata & feedback

Found a typo, an outdated API, a code sample that doesn't compile, or a factual mistake? Help make the next printing better:

- 📋 [**Open an errata report**](https://github.com/RachidD68/building-ai-agents-with-csharp-and-dotnet-10/issues/new?template=errata.yml) — guided form that asks for the page number, chapter, and what's wrong.
- 🔍 [**Browse known errata**](https://github.com/RachidD68/building-ai-agents-with-csharp-and-dotnet-10/issues?q=is%3Aissue+label%3Aerrata) — see what's already been reported before filing a duplicate.

For general feedback, questions about the companion code, or suggestions, use [the Issues tab](https://github.com/RachidD68/building-ai-agents-with-csharp-and-dotnet-10/issues) without the `errata` label.

## License

- The **companion code** in this repository is released under the [MIT License](./LICENSE) — use it, fork it, build on it freely.
- The **book** itself is sold separately — [available on Leanpub](https://leanpub.com/buildingAIAgentsWithCSandNet10); the full copyright page is inside the book.

---

<p align="center"><sub>Built with .NET 10 LTS · C# 14 · Microsoft Agent Framework 1.28.0 · Last updated 2026</sub></p>
