# Exercise 02 — Resume Parser (Intermediate)

## Prompt

Parse a resume/CV text into structured data: Name, Email, Skills (list), Experience (list of Company/Role/Duration), Education (list). Handle missing fields gracefully.

## Hints

- Use `RunAsync<ParsedResume>()` for typed output.
- Use nullable properties (`string?`) for fields that might not be present.
- Use `List<T>?` initialized to empty for collection fields.

## Expected Outcome

Structured extraction of name, email, skills, experience entries, and education from unstructured resume text.

## Key Concepts

- Complex nested structured output with `RunAsync<T>()`
- Nullable reference types for optional data
- Nested object extraction (ExperienceEntry)
- Graceful handling of missing information
