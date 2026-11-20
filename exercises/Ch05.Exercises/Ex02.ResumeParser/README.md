# Exercise 02 — Resume Parser (Intermediate)

## Prompt

Extract a `Resume` record from a pasted resume text: candidate name, contact info (email, phone), skills (list of string), experiences (list of `WorkExperience` with company, title, start/end dates as `DateTime?`, description), and education (list of `Education` entries). Use `DateTime?` for fields that may be missing (current job has no end date).

## Hints

- `DateTime?` in a record deserializes from ISO-8601 strings.
- Under strict mode that nullable doesn't mean "the key may be absent" — it means the key is always present and may be null (a `["string","null"]` union in the schema), so instruct the model to set `endDate` to `null` for ongoing jobs rather than omitting it.
- Put `[JsonRequired]` on `EndDate` so your own parser enforces the same rule: a reply that omits the key, or writes `"Present"` or `"June 2021"`, fails to deserialize.
- Use the chapter's `TryRunStructuredAsync<T>()`: a malformed date then ends as a `Failure`, never as a default date.
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
- `[JsonRequired]` on a nullable: the key is present, the value may be null
- `TryRunStructuredAsync<T>()`: one retry, then an explicit `Failure`

## Run

```bash
dotnet run
dotnet run -- --self-test   # offline: canned replies, fake chat client, RESULT n/n
```
