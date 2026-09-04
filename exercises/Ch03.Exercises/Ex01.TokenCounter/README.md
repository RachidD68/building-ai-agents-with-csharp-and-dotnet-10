# Exercise 01 — Token Counter (Basic)

## Prompt

Extend the companion `EstimateTokenCount` method to accept a **content type hint** (`"prose"`, `"code"`, `"json"`) and adjust the divisor accordingly (**4.0** for prose, **3.0** for code, **2.5** for JSON). Then process a sample folder of `.txt`, `.cs`, and `.json` files and print a table of file → estimated tokens → estimated GPT-5.6 Terra input cost.

## Hints

- You already have `File.ReadAllTextAsync` from Chapter 2.
- For the content-type hint, use the file extension (`.cs` → code, `.json` → json, everything else → prose).
- Keep it simple — a 20-line method beats a framework.
- GPT-5.6 Terra input pricing: ~$2.50 per 1M tokens (verify current rates before relying on them).

## Run it

```
dotnet run
```

Zero arguments scans the bundled `samples/` folder (one `.txt`, one `.cs`, one `.json`, copied next to the binary at build time). Pass a folder path to scan your own files:

```
dotnet run -- C:\path\to\folder
```

## Expected Outcome

```
=== Exercise: Token Counter — Content-Type-Aware Estimator ===

Scanning: ...\bin\Debug\net10.0\samples

  File             Type      Chars  Est. tokens  Input cost ($)
  ---------------- ------ -------- ------------ ---------------
  notes.txt        prose       685          172        0.000430
  request.json     json        260          104        0.000260
  snippet.cs       code        483          161        0.000403
  ---------------- ------ -------- ------------ ---------------
  TOTAL                                     437        0.001093

=== Exercise complete! ===
```

## Key Concepts

- The chars-per-token heuristic and why the divisor shifts by content type (code and JSON tokenize worse than prose)
- Estimating LLM input cost from token counts (tokens × rate / 1M)
- Async file processing with `File.ReadAllTextAsync` and a `CancellationToken`
- When to graduate from the heuristic to a real BPE tokenizer (see the Ch03.TokenExplorer companion)
