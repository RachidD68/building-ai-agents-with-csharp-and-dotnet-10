# Exercise 01 — Recipe Extractor (Basic)

## Prompt

Build a typed agent that extracts a `Recipe` record (title, ingredients as a list of `Ingredient` records with name/quantity/unit, steps as a list of strings, total time in minutes) from a pasted-in recipe text.

## Hints

- Start from `Ch07.TypedResponses/Models.cs` and define two records. The nested list shape is exactly `TaskList`/`TaskItem`.
- Put `[Description]` attributes on ingredient names and units — they survive into the schema whether or not the project generates a documentation file — and the model will return much cleaner results.
- On `JsonException`, use the chapter's recovery pattern: retry once with a stricter prompt on a fresh session; never loop.

## Expected Outcome

```
--- Extracted Recipe ---
  Title      : Spaghetti Bolognese
  Total time : 60 minutes
  Ingredients (9):
    - ground beef          500 g
    - onion                1
    ...
  Steps (6):
    1. Brown the beef in a large pan.
    ...
```

## Key Concepts

- `RunAsync<T>()` with a nested record shape (`Recipe` / `Ingredient`)
- `[Description]` schema hints on every property
- Total time as an `int` in minutes — numbers, not prose
- The chapter's `JsonException` retry-once pattern
