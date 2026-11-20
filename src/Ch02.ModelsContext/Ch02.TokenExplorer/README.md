# Ch02.TokenExplorer

Tokens, context budgets, sampling and similarity, computed offline. No model is called and no key is needed.

| Section | What it shows |
|---------|---------------|
| Estimate, then verify | Real `o200k_base` and `cl100k_base` counts (Microsoft.ML.Tokenizers) next to the length/4 estimate, for the same Contoso rule in English, Arabic and C# |
| The conversation is a list of messages | A `List<ChatMessage>` re-sent every turn; input tokens grow with the history |
| Context budget | `ContextBudget`: instructions, history, evidence, tool results and the output reserve must fit the window together |
| Temperature and top-p | Softmax arithmetic on a toy distribution (not a model, and not a determinism guarantee) |
| Cosine similarity | Toy vectors; the retrieval chapter computes real embeddings |

Token economics, prompt caching and cost estimates live in the cost chapter, not here.

## Running

```bash
dotnet run --project src/Ch02.ModelsContext/Ch02.TokenExplorer
dotnet run --project src/Ch02.ModelsContext/Ch02.TokenExplorer -- --self-test
```

The self-test pins the token counts read from Microsoft.ML.Tokenizers 2.0.0, so a package bump that changes a vocabulary fails it. It prints `RESULT n/n` and exits non-zero on any failure.

## What the numbers say

- len/4 is close for the English and C# samples and undercounts the Arabic one by about a quarter (`o200k_base`).
- `cl100k_base` needs about twice as many tokens as `o200k_base` for the Arabic sample; the two agree on the English and C# samples.
- Which vendor model uses which encoding is a dated fact. Check the vendor's documentation before you map one to the other.
