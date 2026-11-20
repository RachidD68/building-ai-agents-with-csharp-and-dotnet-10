# Exercise 01: Token Counter (Basic)

## Prompt

Count the tokens of three inputs (English prose, Arabic prose and C#) with a real tokenizer, not an estimate. Print, for each input, its characters, its token count, the length/4 estimate and the chars-per-token ratio, and name the tokenizer in the report.

## Acceptance

- The counts come from the tokenizer (`TiktokenTokenizer.CreateForEncoding("o200k_base")`), not from length/4.
- The report shows the chars/token ratio per input and names the tokenizer.

## Hints

- `Microsoft.ML.Tokenizers` plus `Microsoft.ML.Tokenizers.Data.O200kBase` count offline; no key, no network.
- Normalize line endings before counting (`ReplaceLineEndings("\n")`), or a CRLF checkout counts more tokens than an LF one.
- `File.ReadAllTextAsync` from the C# essentials appendix reads each file.

## Run it

```
dotnet run                        # the bundled samples/ folder (arabic.txt, notes.txt, snippet.cs)
dotnet run -- C:\path\to\folder   # your own .txt and .cs files
dotnet run -- --self-test         # pinned counts for the bundled samples, RESULT n/n
```

## Expected outcome (Microsoft.ML.Tokenizers 2.0.0)

```
Tokenizer: o200k_base (Microsoft.ML.Tokenizers, offline)

  File          Chars  Tokens  len/4  Chars/token
  -----------------------------------------------
  arabic.txt      348     111     87         3.14
  notes.txt       685     142    172         4.82
  snippet.cs      483      88    121         5.49
  -----------------------------------------------
  TOTAL          1516     341    380
```

len/4 undercounts the Arabic file and overcounts the English and C# files.

## Key concepts

- A chars/token ratio is a property of a text and a tokenizer, not a constant.
- Estimate for a back-of-the-envelope plan; count with the tokenizer when a wrong number would overflow the window.
