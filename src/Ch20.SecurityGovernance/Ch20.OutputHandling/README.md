# Ch20.OutputHandling: the model's output is untrusted input (OWASP LLM05:2025)

Three sinks, no model, the same result every run:

- **HTML**: `HtmlEncoder.Default.Encode` turns an `<img onerror=...>` answer into text.
- **Markdown**: `SafeMarkdown` keeps a link or image only when the URL is https
  and the host is on the allow-list (exact match or subdomain). Images to
  other hosts are removed whole: the browser fetches them on render. A
  substring host check accepts `contoso.example.evil.example`; the self-test
  reproduces it.
- **SQL** (in-memory SQLite): concatenation runs the model's `DROP TABLE`;
  a parameter stores it as text.

## Running

```bash
dotnet run -- --self-test   # RESULT n/n
```
