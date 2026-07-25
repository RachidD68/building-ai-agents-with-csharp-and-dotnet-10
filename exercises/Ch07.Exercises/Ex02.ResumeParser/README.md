# Exercise 02 — Resume Parser (Intermediate)

## Prompt

Extract a `Resume` record from a pasted resume text: candidate name, contact info (email, phone), skills (list of string), experiences (list of `WorkExperience` with company, title, start/end dates as `DateTime?`, description), and education (list of `Education` entries). Use `DateTime?` for fields that may be missing (current job has no end date).

## Hints

- `DateTime?` in a record deserializes from ISO-8601 strings.
- Under strict mode that nullable doesn't mean "the key may be absent" — it means the key is always present and may be null (a `["string","null"]` union in the schema), so instruct the model to set `endDate` to `null` for ongoing jobs rather than omitting it.
- Run on a real-ish resume and examine which fields the model gets wrong.

## Expected Outcome

```
--- Parsed Resume ---

  Name  : Jane Smith
  Email : jane.smith@techcorp.dev
  Phone : +1 (206) 555-0142
  ...
  Experience (3):
    - Senior Software Engineer at TechCorp Inc. (2021-06 to Present)
    ...
  Education (2):
    - M.Sc. Computer Science, MIT (2016)
    ...
```

## Key Concepts

- Nested records: `Resume` / `WorkExperience` / `Education`
- `DateTime?` for optional dates — `null` end date means an ongoing job
- `[Description]` schema hints on every property
- The chapter's `JsonException` retry-once pattern
