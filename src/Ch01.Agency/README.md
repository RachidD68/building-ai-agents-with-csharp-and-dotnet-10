# The AI Revolution in .NET

This chapter is mostly theory; its one sample is `src/Ch01.Agency/Ch01.FirstGlimpse`. Use the checklist below as a study guide while reading.

## Study Checklist

### Traditional Software vs. Agentic AI
- [ ] Understand deterministic (rule-based) programming vs. probabilistic (LLM-driven) responses
- [ ] Compare request/response architectures with autonomous agent loops
- [ ] Identify where traditional software fails and agents excel (ambiguity, natural language, reasoning)

### The Microsoft AI Ecosystem
- [ ] **Azure AI Services** -- managed cloud APIs for vision, speech, language, and generative AI
- [ ] **OpenAI integration** -- first-party partnership, Azure OpenAI Service
- [ ] **Semantic Kernel (SK)** -- the orchestration SDK for building AI agents in .NET
- [ ] **AutoGen** -- multi-agent conversation framework originally from Microsoft Research
- [ ] **Microsoft.Extensions.AI** -- the vendor-neutral abstraction layer (`IChatClient`, `IEmbeddingGenerator`)

### The Consolidation Story: SK + AutoGen to Microsoft Agent Framework
- [ ] Why two frameworks existed and the confusion it caused
- [ ] The strategic merge: one unified Agent Framework
- [ ] `Microsoft.Agents.AI` as the new foundation
- [ ] How `Microsoft.Extensions.AI` remains the low-level abstraction underneath

### Where Agents Fit in Your Career and the Industry
- [ ] The shift from "AI-assisted coding" to "AI-native applications"
- [ ] Roles that agents create: prompt engineer, agent architect, AI safety engineer
- [ ] Industries already adopting agents: customer support, DevOps, healthcare triage, legal research
- [ ] The competitive advantage of understanding agents as a .NET developer

### What You Will Build in This Book
- [ ] **Understand and Start** -- the right amount of agency, models and context, a reproducible environment, your first agent and its first test
- [ ] **Contracts and Authorized Actions** -- typed responses, conversations and memory, bounded tools, authorizing and committing agent actions
- [ ] **Knowledge and Documents** -- MCP across trust boundaries, a grounded RAG baseline, RAG with permissions and freshness, multimodal document processing
- [ ] **Composition and Durable Execution** -- multi-agent topologies, workflows with human review, durable execution, skills and harnesses
- [ ] **Interfaces, Security and Observation** -- hosting and identity, streaming and approval interfaces, observability, security policies and governance
- [ ] **Qualification and Operations** -- evaluation, cost and capacity, qualifying the Contoso agent system, operating and evolving agents
- [ ] **Appendices** -- C# essentials, Semantic Kernel and AutoGen migration, exercise solutions and the first-edition project mapping

## Key Takeaways
1. Agentic AI is not a fad -- it is a fundamental shift in how software is designed.
2. Microsoft has consolidated its AI frameworks into a single, coherent stack.
3. .NET developers are uniquely positioned because the entire stack (abstractions, agents, hosting) is first-party.
4. Most chapters have runnable code under `src/` and `exercises/`. This chapter gives you the mental map to navigate them.
