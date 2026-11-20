# Ch01.FirstGlimpse

The book's very first taste of code — a four-step agent: wire up a provider-agnostic
chat client, create a `ChatClientAgent` with a persona, open a session, and ask it one
question. You are **not** expected to understand every line yet; it's here to make the
abstract concrete. Every piece is explained in full by the first-agent chapter (the host and DI come
in the development-environment chapter).

The provider is chosen in `appsettings.json` (or User Secrets): openai or ollama;
Azure OpenAI through `AgenticAI.Common.Azure` (see the development-environment chapter).
`--offline` registers no provider, so it runs with no model settings at all.

## Run it

From this project folder:

```
dotnet run                    # the configured model (default: local Ollama, llama3.2)
dotnet run -- --offline       # no model yet: a canned client answers, labelled "[offline: ...]"
dotnet run -- --self-test     # offline checks of the agent's shape; prints RESULT n/n
```

With a real model, the output wording will vary every run — language-model responses
are non-deterministic (the chapter explains why). The `--offline` answer is always the
same canned sentence; it shows the shape of the code, not what a model would say.

The self-test checks that one `RunAsync` makes one model call, that the persona travels
with every call, that a session sends the earlier turns again, and that a new session
starts empty. It checks the shape, not the quality of any answer.
