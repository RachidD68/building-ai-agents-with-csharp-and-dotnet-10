# Exercise 02: Prompt Template Engine (Intermediate)

## Prompt

Build a `{placeholder}` prompt template that renders under a token budget. The budget is a context window and an output reserve; the rendered prompt may use only what the reserve leaves free, and the reserve becomes the request's `MaxOutputTokens`. Test it with a system prompt, a user question and a few-shot classifier.

## Acceptance

- A missing variable fails loudly (an exception that names the template and the variable).
- An over-budget prompt is rejected before any model call.
- The output reserve is respected: a prompt that fits the window but eats the reserve is rejected too.

## Hints

- `Regex.Replace(template, @"\{(\w+)\}", m => ...)` does the substitution in one line; `[GeneratedRegex]` compiles it.
- Count with `TiktokenTokenizer.CreateForEncoding("o200k_base")`, not length/4.
- Normalize line endings before counting: a raw string literal takes the line endings of the checkout.

## Run it

```
dotnet run                   # three templates, then a blown budget and a missing variable
dotnet run -- --self-test    # the three rules, RESULT n/n
```

## Expected outcome (window 160, reserve 64)

```
Rejected before the call: 'UserQuestion' is 151 tokens; the limit is 96 (160 window - 64 output reserve).
Rejected before the call: Template 'UserQuestion' needs 'question', which was not given.
```

## Key concepts

- The window holds the prompt and the reply; plan the reply's share first.
- Reject before the call: a loud exception is cheaper than a truncated answer.
