# Exercise 03 — Conversation Cost Simulator (Challenge)

## Prompt

Build a **conversation cost simulator** that takes a message array and a daily
volume, then prints the monthly cost three ways:

1. **Uncached** — the whole history re-sent fresh on every turn.
2. **Stable-prefix cached** — the unchanging prefix (the system message) billed
   at a configurable cached-input discount.
3. **Windowed + cached** — a sliding window caps the history to the system
   message plus the last N turns, then caching applies on top.

Then print a **comparative table** of every model, windowed + cached, sorted
from cheapest to most expensive. Model reasoning models with a hidden
reasoning-token multiplier (extra tokens billed at the output rate).

## Hints

- A chat request is an ordered message array; "history" is that array re-sent
  every call. Re-counting it each turn is what makes an unbounded conversation
  expensive.
- The cached prefix is the part that does not change between calls — the system
  message and any fixed context. Put volatile content last so the prefix caches.
- Model reasoning cost as `ReasoningMultiplier x visibleOutput`, billed at the
  output rate. Use `0.0` for ordinary models and `~5.0` for a reasoning model.
- The sliding window keeps the system message plus the last N user/assistant
  pairs; drop everything older.

## Expected Outcome

```
  Monthly cost on GPT-4.1 (visible output 120 tok/turn):

    Strategy                      Monthly $
    ----------------------------------------
    Uncached                         $23.28
    Stable-prefix cached             $21.95
    Windowed + cached                $17.33

    Caching alone saves 6%.
    Windowing + caching saves 26% vs uncached.

  All models, windowed + cached, sorted cheapest first:

    Model             Monthly $    Reasoning x
    --------------------------------------------
    GPT-4o-mini           $1.33            0.0
    GPT-4.1-mini          $3.47            0.0
    GPT-4.1              $17.33            0.0
    GPT-4o               $22.22            0.0
    o4-mini              $49.13            5.0
```

> Rates are illustrative mid-2026 figures — verify against the live rate card
> before relying on them.

## Key Concepts

- The message array and why history re-sends grow cost
- Prompt caching: stable prefix at a cached-input discount
- Sliding-window history caps
- Reasoning models and hidden reasoning tokens
- Model choice as the biggest cost lever
