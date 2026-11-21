# Migration guides

The full migration guides of *Building AI Agents with C# and .NET 10*, second edition. The book prints a short appendix, "Migrating from Semantic Kernel and AutoGen", with the most important rows of each concept map; the guides here are the complete versions (they were Appendices C and D of the book's drafts).

| Guide | You have | It covers |
|---|---|---|
| [semantic-kernel-to-maf.md](./semantic-kernel-to-maf.md) | Semantic Kernel code in C# | The concept map, a plugin rewritten as a tool, keeping a plugin through the bridge, filters as middleware, a phased migration and the pitfalls |
| [autogen-to-maf.md](./autogen-to-maf.md) | AutoGen code, usually Python on the AgentChat API | The concept map, agents and tools, teams as workflows (round-robin, selector, swarm), user proxies and graphs as one workflow, and a migration order |

## What was checked

- **MAF side:** the 1.28 package set this repository pins (`Directory.Packages.props`). The C# listings in the AutoGen guide and the rewritten tool in the Semantic Kernel guide are copied verbatim from the projects named beside them; their outputs are lines of the captures in [`docs/outputs`](../outputs/README.md).
- **Semantic Kernel side:** text from the first edition. The names were checked against Semantic Kernel 1.77.0, except the planner and process rows.
- **AutoGen side:** text from the first edition, named in words only. The repository has no Python, so no AutoGen listing could be checked.

## The Semantic Kernel bridge check

The bridge and function-middleware listings in the Semantic Kernel guide are not part of a companion project: this repository references no Semantic Kernel package. They come from a small compile-and-run check kept with the book's build (Microsoft.Agents.AI 1.28.0, Microsoft.Extensions.AI 10.11.0, Microsoft.SemanticKernel.Core 1.77.0; a scripted `IChatClient`, no model, no network). Its captured output is [outputs/semantic-kernel-bridge.txt](./outputs/semantic-kernel-bridge.txt); the first line records the packages, the SDK and the platform.
