# Exercise 02 — Prompt Template Engine (Intermediate)

## Prompt

Build a simple **prompt template engine** that:

1. Defines templates with `{placeholder}` variables.
2. Extracts placeholder names using regex.
3. Fills templates with user-provided values.
4. Displays the final prompt with estimated token count.
5. Includes an interactive mode where users can pick a template and fill it.

## Hints

- Use `Regex` to find `{placeholder}` patterns: `\{(\w+)\}`
- Use `GeneratedRegex` for compile-time regex (C# 14 source generator).
- `string.Replace` for simple placeholder substitution.
- Token estimation: `Math.Ceiling(text.Length / 4.0)`

## Expected Outcome

The program defines three templates (CodeReview, Summarizer, Translator), shows their placeholders, fills them with sample data, and allows interactive filling.

## Key Concepts

- Prompt engineering and template design
- Regex with source generators (`GeneratedRegex`)
- Records with methods
- Token estimation for cost planning
