# Contoso v0: agent and fake model

The Contoso expense assistant at the end of the first-agent chapter. It answers employees' questions about the expense policy and does nothing else: no tools, no effects, no stored state.

```
question -> PolicyDesk (validation) -> ChatClientAgent (policy instructions)
         -> IChatClient (configured provider, or FakeChatClient in the self-test)
         -> streamed answer -> one terminal state: completed | cancelled | truncated | failed
```

`PolicyDesk` composes the chapter's code as it is: `ContosoPolicy.cs` from `Ch04.BasicAgent` (the agent and its instructions) and `AgentRuns.cs` from `Ch04.StreamingAgent` (the terminal-state classifier), compiled in as linked files. The fake model is `AgenticAI.Testing.FakeChatClient`.

The policy facts in the instructions are the ones the later snapshots enforce in code: 50.00 EUR low-value limit, a finance reviewer who is not the requester, one payment per claim, EUR only. Here the model only explains them.

The self-test checks that the instructions and the question reach the model, that answers come back Completed, that a second question carries no history (single-turn), that Stop after the first chunk is Cancelled and the token reached the model call, that a model outage is Failed with its exception kept, that a cut-off answer is Truncated, and that an empty question is refused before any model call. A fake's success proves wiring and contracts, not answer quality.

## Running

```bash
dotnet run --project contoso/v0-agent-fake-model                  # Ollama llama3.2 by default
dotnet run --project contoso/v0-agent-fake-model -- --self-test   # fake model, no network
```

## What later snapshots take from this one

- `AgenticAI.Testing.FakeChatClient` as the scripted model of every Contoso self-test. v2 still has its own `ScriptedProposalClient` (keyed by claim text, with an outage counter); folding it onto `FakeChatClient` needs a reply chosen by the message, which the fake does not do yet.
- `RunOutcome` / `RunTracker` for any streamed answer the API and interface snapshot shows to a user, so a cancelled or cut-off answer is never displayed as a finished one.
- The policy facts above, unchanged: v2's `ReimbursementPolicy.LowValueLimit` (50.00) and `finance-reviewer` role are the same rules.
- v1 (owned conversation) replaces the single-turn desk with a conversation the application owns and stores.
