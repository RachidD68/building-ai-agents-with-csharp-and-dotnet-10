# Exercise 01 — Translation Chain (Beginner)

## Prompt

Extend Ch12.SequentialPipeline from two agents to three: Translator (FR → EN) →
Formalizer (rewrite in formal business tone) → Summarizer. Run a sample French
memo through all three stages and compare to a single-agent version that does
all three jobs in one prompt. Note differences in quality and latency.

## Key Concepts

- Sequential multi-agent pipeline: output of one agent feeds into the next
- Single-purpose personas (the Formalizer is one sentence of instructions)
- `Stopwatch` instrumentation per stage vs. the single-prompt baseline
- Latency/quality trade-off: three round-trips vs. one
