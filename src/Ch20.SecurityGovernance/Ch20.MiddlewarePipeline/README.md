# Ch20.MiddlewarePipeline: the detection layer around an IChatClient

Three `DelegatingChatClient`s composed with `ChatClientBuilder.Use()`:

```
Logging -> ContentFilter -> Timing -> model
```

The **first** `.Use()` is the **outermost** wrapper. Logging sees every request,
including blocked ones; the content filter short-circuits before the model on
both `GetResponseAsync` and `GetStreamingResponseAsync`; Timing, innermost,
times the model call alone. The logging middleware records shapes (message
count, roles, character counts), never message text.

The keyword filter is a signal. The self-test prints what it misses (spacing,
a Cyrillic letter, a digit, a polite request for a forbidden action) and one
false positive ("hackathon"). The decision about an action is made at the
effect: see `Ch20.PolicyEnforcement`.

## Running

```bash
dotnet run -- --self-test   # offline, scripted model: RESULT n/n
dotnet run                  # live, against the configured provider (Ollama by default)
```
