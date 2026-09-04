# Chapter 20 — Your Next Steps

Congratulations on completing the book! You now have a solid foundation in
building agentic AI applications with .NET. Here are resources and areas
to explore as you continue your journey.

## Emerging Protocols and Standards

### Agent-to-Agent (A2A) Protocol
Standardizes how AI agents discover and communicate with each other over HTTP.
- Specification: https://a2a-protocol.org/latest/
- See Chapter 14 (Ch14.A2APreview) for integration using the official SDK

### Model Context Protocol (MCP)
Enables AI agents to access external tools, data sources, and services.
- Specification: https://modelcontextprotocol.io/
- See Chapter 9 (Ch09.MCP) for server and client implementations

### AG-UI (Agent-Generated User Interfaces)
An emerging pattern where agents dynamically generate UI components
based on context, enabling richer human-agent interactions beyond text.

## Enterprise Governance Patterns

As you bring agents to production, consider these governance areas:

- **Cost management** — Token budgets, rate limiting, model tiering
- **Audit logging** — Record all agent interactions for compliance
- **Access control** — Role-based permissions for agent capabilities
- **Content policies** — Automated moderation and safety guardrails (Chapter 15)
- **Model versioning** — Pin model versions, test before upgrading
- **Data residency** — Ensure data stays in required geographic regions
- **Responsible AI** — Fairness testing, bias detection, transparency

## Contributing to MAF Open Source

The Microsoft Agent Framework is open source and welcomes contributions:
- GitHub: https://github.com/microsoft/agent-framework
- Issues and feature requests
- Documentation improvements
- Sample projects and extensions

## Community Resources

- **Discord** — Join the MAF community for discussions and support
- **Stack Overflow** — Tag: `microsoft-agents-framework`
- **Microsoft Learn** — Official documentation and tutorials
  https://learn.microsoft.com/en-us/dotnet/ai/

## Further Reading

### Books and Documentation
- Microsoft.Extensions.AI documentation
- Azure AI Services documentation
- OpenTelemetry .NET documentation

### Key NuGet Packages
- `Microsoft.Agents.AI` — Core agent framework
- `Microsoft.Agents.AI.Hosting` — ASP.NET Core hosting
- `Microsoft.Agents.AI.Workflows` — Workflow orchestration
- `Microsoft.Extensions.AI` — AI abstractions and middleware
- `ModelContextProtocol` — MCP client and server

### Architecture Patterns
- Retrieval Augmented Generation (RAG) — Chapter 10
- Multi-agent orchestration — Chapter 11
- Workflow-based agents — Chapter 12
- Middleware pipelines — Chapter 15
- Observability and tracing — Chapter 14

## What's Next for You

1. **Build something real** — Pick a problem at work and prototype an agent
2. **Start small** — Single agent, one tool, one data source
3. **Add guardrails early** — Safety and observability from day one
4. **Iterate** — Use the critic agent pattern to improve quality
5. **Share** — Blog about your experience, contribute to open source
