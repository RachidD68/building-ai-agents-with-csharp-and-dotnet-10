# AgenticAI.Testing

Test doubles for `IChatClient`, shared by the `--self-test` modes that need a model.

- `FakeChatClient` replays a script of `FakeReply` values: a complete answer, a stream of chunks with a finish reason (`Stop`, `Length`), an exception after some chunks, a hang that ends only when the caller's token is cancelled, or a hang that then ends quietly on cancellation (no exception, no finish reason), as a provider that swallows the cancellation does. It records every call (`Calls`: the messages, a copy of the `ChatOptions`, streaming or not, the token) and marks a call that ended on cancellation (`SawCancellation`). A call with no scripted reply left throws, so an unexpected model call fails the test.
- `RecordingChatClient` wraps a real client and records the same `FakeCall` data, so a live run can check the same contracts.

A fake proves wiring and contracts: the instructions sent, the token honoured, the error type kept, a truncation flagged. It says nothing about the quality of an answer.

No network, no model, GA packages only (`Microsoft.Extensions.AI.Abstractions`).
