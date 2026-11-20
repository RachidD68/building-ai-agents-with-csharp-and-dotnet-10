# Exercise 01 — Recipe Extractor (Basic)

## Prompt

Build a typed agent that extracts a `Recipe` record (title, ingredients as a list of `Ingredient` records with name/quantity/unit, steps as a list of strings, total time in minutes) from a pasted-in recipe text.

## Hints

- Start from `Ch05.TypedResponses/Models.cs` and define two records. The nested list shape is exactly `TaskList`/`TaskItem`.
- Put `[Description]` attributes on ingredient names and units — they survive into the schema whether or not the project generates a documentation file — and the model will return much cleaner results.
- Make `Unit` a `string?` with `[JsonRequired]`: every ingredient then has a unit or an explicit `null` ("3 avocados"), and a reply that leaves the key out fails to deserialize.
- Use the chapter's `TryRunStructuredAsync<T>()`: one stricter retry on a fresh session, then an explicit `Failure`; never loop.

## Expected Outcome

```
--- Extracted Recipe ---
  Title      : Spaghetti Bolognese
  Total time : 60 minutes
  Ingredients (9):
    - ground beef          500 g
    - onion                1 (no unit)
    ...
  Steps (6):
    1. Brown the beef in a large pan.
    ...
```

## Key Concepts

- `RunAsync<T>()` with a nested record shape (`Recipe` / `Ingredient`)
- `[Description]` schema hints on every property
- Total time as an `int` in minutes — numbers, not prose
- `[JsonRequired]` on a nullable: present key, possibly null value
- `TryRunStructuredAsync<T>()`: one retry, then an explicit `Failure`

## Run

```bash
dotnet run
dotnet run -- --self-test   # offline: three canned recipes, fake chat client, RESULT n/n
```
