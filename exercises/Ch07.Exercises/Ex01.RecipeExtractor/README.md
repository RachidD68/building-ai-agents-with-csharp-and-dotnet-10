# Exercise 01 — Recipe Extractor (Beginner)

## Prompt

Given free text describing a recipe, extract structured data using `RunAsync<T>()`:
- RecipeName, Ingredients (list), Steps (list), PrepTime, CookTime

## Hints

- Use `agent.RunAsync<Recipe>(...)` for typed deserialization.
- The agent instructions should tell the LLM to return only valid JSON.
- Handle missing fields gracefully with nullable properties.

## Expected Outcome

The program extracts structured recipes from two sample texts and displays them in a formatted way.

## Key Concepts

- `RunAsync<T>()` for structured output
- JSON schema enforcement via instructions
- Nullable property handling
- Data extraction from unstructured text
