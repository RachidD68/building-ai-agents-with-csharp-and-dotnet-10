# Ch15.OutputHandling — The model's output is untrusted input (OWASP LLM05)

The chapter redacts PII **out** of a response. This demo covers the other
direction: an LLM's output is **untrusted input** to whatever consumes it. The
model can be steered into emitting a string that is harmless as text but
dangerous the moment it is interpolated into HTML (XSS), SQL (injection), a
shell command, or a file path.

## What It Shows

- **HTML sink** — a model answer carrying an `<img onerror=…>` payload. Raw
  interpolation is stored/reflected XSS; `HtmlEncoder.Default.Encode(...)` makes
  it inert.
- **SQL sink** — an extracted "customer name" of `Robert'); DROP TABLE
  Customers;--`. String concatenation is classic injection; a parameterized
  command binds the value as data, never parsed as SQL.

No LLM call — the output is fully deterministic.

## Key takeaway

Never trust, never interpolate — **encode or parameterize at every boundary the
answer crosses.**

## Running

```bash
dotnet run
```
