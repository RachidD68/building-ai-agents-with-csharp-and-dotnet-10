# Exercise 02 — Prompt Template Engine (Intermediate)

## Prompt

Build a small **prompt-template class** that supports `{placeholder}` substitution from a dictionary, and **tracks the estimated token count** of the rendered output. Add a **hard cap**: if the rendered prompt exceeds the cap, the method **throws**. Test with three templates — a **system prompt**, a **user question template**, and a **few-shot classification template**.

## Hints

- The `System.Text.RegularExpressions` namespace gives you the substitution in one line: `Regex.Replace(template, @"\{(\w+)\}", m => values[m.Groups[1].Value])`.
- The token estimate is the same length / 4 trick from Chapter 3.
- Don't over-engineer; this is about feeling how easily a prompt blows its budget.
- Use `[GeneratedRegex]` for the compile-time regex source generator.

## Expected Outcome

```
--- Rendered Prompts ---

  [SystemPrompt] ~56/120 tokens (221 chars):
  ...
  [UserQuestion] ~14/80 tokens (56 chars):
  ...
  [FewShotClassifier] ~89/160 tokens (354 chars):
  ...

--- Hard Cap ---

  Caught: Template 'UserQuestion' rendered to ~433 tokens — over its hard cap of 80.
```

All three templates render within budget; then a pasted wall of text in the `question` placeholder blows the `UserQuestion` cap and `Render` throws `InvalidOperationException`.

## Key Concepts

- Prompt templates as data: system, user, and few-shot shapes
- One-line placeholder substitution with `Regex.Replace` and a match evaluator
- Token estimation (length / 4) as a render-time budget check
- Hard caps that fail fast — a loud exception beats a silent cost overrun
