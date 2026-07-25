# Building AI Agents with C# and .NET 10

<p align="center">
  <img src="assets/cover-front.png" alt="Building AI Agents with C# and .NET 10 — cover" width="420">
</p>

<p align="center"><strong>A Developer's First Guide to the Microsoft Agent Framework</strong></p>

<p align="center"><em>From your first call to an LLM to a production-hosted, observable, safety-filtered multi-agent system — in C# 14 and .NET 10.</em></p>

<p align="center"><strong>📖 <a href="https://leanpub.com/buildingAIAgentsWithCSandNet10">Get the book on Leanpub</a></strong></p>

<p align="center"><sub>This repository hosts the companion code, errata, and reader feedback for the book.</sub></p>

---

## What's in this book

The **Microsoft Agent Framework (MAF)** is Microsoft's unified, production-minded SDK for building AI agents in .NET — the consolidation of Semantic Kernel and AutoGen into one stack. This is the developer's first guide to it, built entirely on the **stable 1.14.0 release** and **.NET 10 / C# 14**.

Across nineteen chapters in five parts you go from your first call to an LLM, through tools and function calling, the Model Context Protocol, retrieval-augmented generation, and **multimodal agents** (vision), into multi-agent orchestration and workflows, all the way to hosting, observability, safety guardrails, agent skills, and evaluation. Every concept is anchored in a runnable companion project in this repository, and every code listing targets **MAF 1.14.0** — no preview ceremony, no alpha-only attributes, no shifting namespaces.

By the final chapter you will have built the Contoso FAQ agent — a small but real production system — and developed the intuition to design, build, and ship your own.

## Who this book is for

This book is written for .NET developers who want to build AI-powered agent systems without abandoning the tools and patterns they already know. You should be comfortable with C# fundamentals, have written at least one ASP.NET Core service or console application, and have a working relationship with dependency injection. If you can write a class, use LINQ, and navigate your IDE without a tutorial, you're ready.

No prior machine-learning background is assumed. The book explains what you need to know about how LLMs behave, from a developer's point of view, as you go.

## Table of Contents

| # | Chapter | Part |
|---|---------|------|
| 1 | The AI Revolution in .NET | **I — Foundations** |
| 2 | C# Essentials for AI Development | I |
| 3 | How LLMs Work: A Developer's Mental Model | I |
| 4 | Setting Up Your Dev Environment | I |
| 5 | Hello, Agent! | **II — Your First Agents** |
| 6 | Conversations and Memory | II |
| 7 | Structured Output and Typed Responses | II |
| 8 | Tools and Function Calling | **III — Giving Agents Superpowers** |
| 9 | The Model Context Protocol | III |
| 10 | Introduction to RAG | III |
| 11 | Multimodal Agents | III |
| 12 | Multi-Agent Systems | **IV — Multi-Agent Collaboration** |
| 13 | Introduction to Workflows | IV |
| 14 | Hosting and Deployment | **V — Going to Production** |
| 15 | Observability and Debugging | V |
| 16 | Safety and Guardrails | V |
| 17 | Agent Skills | V |
| 18 | Evaluation | V |
| 19 | Your Next Steps | V |

Plus five appendices — a themed package reference, a Semantic Kernel and AutoGen migration guide, a troubleshooting catalogue, a glossary, and worked solutions to every exercise — followed by a keyword index.

---

## Companion code

**122 runnable projects**: 77 chapter samples under [`src/`](./src) and 45 worked exercise solutions under [`exercises/`](./exercises). Each one is self-contained — you don't need to run everything to follow along, so pick the project for the chapter you're reading.

### Target stack

- **.NET 10** (LTS) / **C# 14**
- **Microsoft Agent Framework 1.14.0**
- **Microsoft.Extensions.AI 10.7.0**
- Visual Studio 2026, or VS Code with the C# Dev Kit

Every package version is pinned centrally in `Directory.Packages.props` (Central Package Management); individual `.csproj` files reference packages without version numbers. The full, themed package list is **Appendix A** of the book.

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A model provider. The samples default to **Ollama** running locally — zero cost, no API key — using **llama3.2**. They also support Azure OpenAI, OpenAI, GitHub Models, Anthropic, and Microsoft Foundry. Chapter 4 walks through each one.
- For the **multimodal** samples (Chapter 11), a vision-capable model — `ollama pull llava` locally, or a cloud `gpt-4o`-class model.

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

Open `AgenticAI.slnx` once and every chapter's projects load together.

### Run a sample

```bash
dotnet run --project src/Ch05.HelloAgent/Ch05.BasicAgent
```

### Configuration

Provider settings live in each project's `appsettings.json`; secrets belong in a gitignored `appsettings.local.json` or in [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) — never in source control. For example, to use Azure OpenAI:

```bash
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey" "YOUR-KEY"
```

GitHub Models uses the endpoint `https://models.github.ai/inference` with publisher-prefixed model ids (e.g. `openai/gpt-4o-mini`). Azure/Foundry auth uses `DefaultAzureCredential` (run `az login` once for local dev; scope the credential chain in production).

### Repository layout

| Folder | Chapter | Topic |
|--------|---------|-------|
| `src/Common/` | — | Shared configuration, DI, provider wiring, resilience |
| `src/Ch01.*` | 1 | The AI Revolution in .NET (concept + a first code glimpse) |
| `src/Ch02.CSharpEssentials/` | 2 | C# Essentials for AI Development |
| `src/Ch03.HowLLMsWork/` | 3 | How LLMs Work |
| `src/Ch04.DevEnvironment/` | 4 | Setting Up Your Dev Environment |
| `src/Ch05.HelloAgent/` | 5 | Hello, Agent! |
| `src/Ch06.ConversationsMemory/` | 6 | Conversations and Memory |
| `src/Ch07.StructuredOutput/` | 7 | Structured Output and Typed Responses |
| `src/Ch08.ToolsFunctionCalling/` | 8 | Tools and Function Calling (incl. `FileAccessProvider`) |
| `src/Ch09.MCP/` | 9 | The Model Context Protocol |
| `src/Ch10.IntroRAG/` | 10 | Introduction to RAG |
| `src/Ch11.Multimodal/` | 11 | Multimodal Agents — vision, image-to-typed-data |
| `src/Ch12.MultiAgent/` | 12 | Multi-Agent Systems |
| `src/Ch13.Workflows/` | 13 | Introduction to Workflows |
| `src/Ch14.Hosting/` | 14 | Hosting and Deployment |
| `src/Ch15.Observability/` | 15 | Observability and Debugging |
| `src/Ch16.SafetyGuardrails/` | 16 | Safety and Guardrails |
| `src/Ch17.AgentSkills/` | 17 | Agent Skills |
| `src/Ch18.EvalSuite/`, `Ch18.FaqEval/`, `Ch18.EvalTests/` | 18 | Evaluation |
| `src/Ch19.NextSteps/` | 19 | Your Next Steps |
| `exercises/` | 2–16 | Worked exercise solutions (also indexed in Appendix E) |

## Exercise solutions

The [`exercises/`](./exercises) folder holds the full worked answers to every Practice Exercise in the book — 45 projects across Chapters 2–16, one folder per chapter. Each solution matches the brief printed in its chapter and reproduced in **Appendix E**, and each runs on its own:

```bash
dotnet run --project exercises/Ch08.Exercises/Ex01.UnitConverterTool
```

Try the exercise yourself first — the chapter gives you hints, and Appendix E walks through the reasoning before you reach for the code here.

## Errata & feedback

Found a typo, an outdated API, a code sample that doesn't compile, or a factual mistake? Help make the next printing better:

- 📋 [**Open an errata report**](https://github.com/RachidD68/building-ai-agents-with-csharp-and-dotnet-10/issues/new?template=errata.yml) — guided form that asks for the page number, chapter, and what's wrong.
- 🔍 [**Browse known errata**](https://github.com/RachidD68/building-ai-agents-with-csharp-and-dotnet-10/issues?q=is%3Aissue+label%3Aerrata) — see what's already been reported before filing a duplicate.

For general feedback, questions about the companion code, or suggestions, use [the Issues tab](https://github.com/RachidD68/building-ai-agents-with-csharp-and-dotnet-10/issues) without the `errata` label.

## License

- The **companion code** in this repository is released under the [MIT License](./LICENSE) — use it, fork it, build on it freely.
- The **book** itself is sold separately — [available on Leanpub](https://leanpub.com/buildingAIAgentsWithCSandNet10); the full copyright page is inside the book.

---

<p align="center"><sub>Built with .NET 10 LTS · C# 14 · Microsoft Agent Framework 1.14.0 · Last updated 2026</sub></p>
