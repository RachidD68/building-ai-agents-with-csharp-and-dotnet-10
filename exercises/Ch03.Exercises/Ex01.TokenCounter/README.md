# Exercise 01 — Token Counter (Beginner)

## Prompt

Build a **token count estimator** that takes user input text and:

1. Estimates the token count using the **4-characters-per-token** heuristic.
2. Estimates output tokens (assume 2x input as a rough guide).
3. Calculates the **estimated cost** for GPT-4o pricing.
4. Visualizes context window usage as a progress bar.

## Hints

- Token estimation: `Math.Ceiling(text.Length / 4.0)`
- GPT-4o pricing: ~$2.50 per 1M input tokens, ~$10.00 per 1M output tokens
- GPT-4o context window: 128,000 tokens
- Use a simple loop with `Console.ReadLine()` for interactive input

## Expected Outcome

```
You: Explain dependency injection in C# with an example

  Input text length : 49 characters
  Input tokens (est): 13
  Output tokens (est): 26 (assuming 2x input)
  Total tokens (est) : 39

  Estimated cost (GPT-4o):
    Input  : $0.000033
    Output : $0.000260
    Total  : $0.000293

  Context window usage (GPT-4o 128K):
    [--------------------------------------------------] 0.0305%
```

## Key Concepts

- Token estimation heuristics
- LLM pricing models
- Context window limits
- Interactive console applications
