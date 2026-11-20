# Exercise 01 — Translation Chain (Basic)

## Prompt

Run three agents in sequence (Translator FR -> EN, Formalizer, Summarizer) and one agent that does all three jobs in a single prompt, on the same three French memos.

**Accepted when** the report prints the model calls and the latency of the chain and of the single prompt, and the chain's output passes the same typed check as the single prompt's (`SummaryCheck`: 1 to 3 sentences, at most 80 words, every key fact of the memo present).

## Running

```bash
dotnet run --project exercises/Ch13.Exercises/Ex01.TranslationChain                  # configured model
dotnet run --project exercises/Ch13.Exercises/Ex01.TranslationChain -- --self-test   # scripted model, RESULT n/n
```
