# Choosing the Right Amount of Agency

This chapter is mostly design; its one sample is `src/Ch01.Agency/Ch01.FirstGlimpse`. Use the checklist below as a study guide while reading.

## Study Checklist

### Start with the job
- [ ] Take one support request and place it on the spectrum: plain C#, one model call, a workflow, or an agent
- [ ] Say who chooses the next step in each design: your code or the model
- [ ] Keep known rules in code, where they still work with the model offline

### Inside the agent loop
- [ ] A tool request from the model is data, not permission: the application validates, executes, and decides when to stop
- [ ] Separate content (what the model reads) from authority (what the application lets it do)
- [ ] Name the exits: an answer, a refusal, a budget reached, a question back to the user

### Where .NET fits
- [ ] **Microsoft.Extensions.AI** -- the vendor-neutral abstraction layer (`IChatClient`, `IEmbeddingGenerator`)
- [ ] **Microsoft Agent Framework** (`Microsoft.Agents.AI`) -- agents, sessions, tools and workflows on top of it
- [ ] **Providers** -- Azure OpenAI, OpenAI or a local Ollama model behind the same interface

### Where it fails
- [ ] Set an autonomy budget: model calls, time, tokens, retries
- [ ] Propose, don't commit: the model proposes an action, the application commits it
- [ ] An approval is not idempotency: an approved action can still run twice

### The Contoso Journey
- [ ] The Contoso expense assistant grows one milestone at a time through the book, with simulated effects only

## Run the sample

```
dotnet run --project src/Ch01.Agency/Ch01.FirstGlimpse -- --offline
```

runs without any model; see the project README for the configured-model run and the self-test.
