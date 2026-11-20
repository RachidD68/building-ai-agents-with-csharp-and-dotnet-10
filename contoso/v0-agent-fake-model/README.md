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

- `AgenticAI.Testing.FakeChatClient`, reused by later chapters' self-tests (Ch16 and Ch20 use it directly; Ch21 and Ch24 use the same library's `ScriptedToolChatClient`). The later Contoso snapshots script their own models instead: v1 has `ScriptedExpenseModel`, v2 `ScriptedProposalClient`, and the later snapshots keep their own scripted proposals or readings.
- The split between a finished response and a finished operation. `RunOutcome` / `RunTracker` stay in this snapshot; the interface snapshot (v6) makes the same split with the operation states streamed by `Ch18.SseStatusStream`, so a closed connection is never shown as a finished operation.
- The policy facts above, unchanged: v2's `ReimbursementPolicy.LowValueLimit` (50.00) and `finance-reviewer` role are the same rules.
- v1 (owned conversation) replaces the single-turn desk with a conversation the application owns and stores.
