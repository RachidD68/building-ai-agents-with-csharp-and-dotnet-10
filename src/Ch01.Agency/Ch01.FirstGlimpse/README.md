# Ch01.FirstGlimpse

The book's very first taste of code — a four-step agent: wire up a provider-agnostic
chat client, create a `ChatClientAgent` with a persona, open a session, and ask it one
question. You are **not** expected to understand every line yet; it's here to make the
abstract concrete. Every piece is explained in full by Chapter 5 (the host and DI come
in Chapter 4).

The provider is chosen in `appsettings.json` (or User Secrets) and works across Azure
OpenAI, OpenAI, and Ollama without touching the code — see Chapter 4.

## Run it

```
dotnet run
```

from this project folder. The default configuration targets a local Ollama model
(`llama3.2`); set a different provider in `appsettings.json` or User Secrets to use a
cloud model. Output wording will vary every run — language-model responses are
non-deterministic (Chapter 1 explains why).
