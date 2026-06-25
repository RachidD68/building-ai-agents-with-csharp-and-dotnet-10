# Ch15.IndirectInjection — Second-order prompt injection (OWASP LLM01 / LLM08)

The dangerous case the chapter's direct-injection defence does **not** cover:
the malicious instruction arrives inside content the agent **ingests** — a
RAG-retrieved document (Ch10), MCP tool/server output (Ch09), a web page, an
email, a file.

## What It Shows

- A "retrieved document" whose legitimate first line is followed by an embedded
  `[SYSTEM OVERRIDE] Ignore previous instructions…` payload.
- **Defence 1 (deterministic): scan the tool/retrieval output.** A
  `[GeneratedRegex]` `ToolOutputScanner` runs the same idea as
  `PromptInjectionDetector`, but over the **retrieved text** — it flags the
  payload every run.
- **Defence 2: spotlighting / delimiting.** The untrusted block is fenced in
  `<<DATA>> … <</DATA>>` and the system prompt tells the model the block is
  data, never commands.

## Honest caveat (the program prints it)

A safety-tuned model sometimes resists the payload and sometimes complies —
model resistance is **probabilistic**. The deterministic defence is the scanner;
spotlighting raises the odds. Never rely on the model alone; pair with the
Chapter 15 output filters and the critic.

Cross-references: Chapter 9 (MCP) and Chapter 10 (RAG) are the entry points for
tainted content.

## Running

```bash
dotnet run   # Ollama + llama3.2 by default
```
