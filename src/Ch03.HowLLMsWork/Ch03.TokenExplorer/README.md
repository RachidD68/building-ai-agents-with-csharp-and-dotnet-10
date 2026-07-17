# Chapter 3 -- How LLMs Work: A Developer's Mental Model (Token Explorer)

An interactive console app that builds intuition for how large language models work under the hood. No API keys required -- everything is simulated locally.

## What You Will Learn

| Section | Concept |
|---------|---------|
| Tokenization Simulation | How text is split into tokens (BPE-like) |
| Token Count Estimation | The ~4 chars/token rule and context window sizing |
| Real Tokenizer (Microsoft.ML.Tokenizers) | Real `o200k_base` counts vs the length/4 estimate vs % error |
| Message Array & Cost Growth | A `List<ChatMessage>` conversation; running input-token cost per turn |
| Temperature Simulation | How temperature controls randomness in token selection |
| Cosine Similarity | How embeddings represent meaning as vectors |
| Token Cost Calculator | Four cost dimensions: input, output, cached input, and batch |

## Running

```bash
dotnet run --project src/Ch03.HowLLMsWork/Ch03.TokenExplorer
```

## Key Takeaways

1. Tokens are not words -- they are subword units. Punctuation and casing create boundaries.
2. The length/4 estimate is fine for prose but drifts badly on code, JSON, and emoji -- use the real `Microsoft.ML.Tokenizers` count when precision matters.
3. A conversation is a re-sent message array; input tokens grow every turn, which is how a chatbot's bill quietly balloons.
4. Temperature 0 is near-deterministic; temperature > 1 increases creativity (and randomness).
5. Cosine similarity measures semantic closeness between embeddings.
6. There are four cost dimensions, not two -- input, output, cached input, and batch -- and a stable prefix is the part that caches.
