# Exercise 02: Traced Pipeline (intermediate)

## Prompt

Take the multi-agent chapter's sequential pipeline (Researcher, Writer, Editor) and give each
stage its own span under one run span. Accepted when the exported trace shows three siblings
under one parent, with length tags and no text content.

## This solution

- `pipeline.run` is the parent; `pipeline.researcher`, `pipeline.writer` and `pipeline.editor`
  start while it is `Activity.Current` and each ends before the next starts, so they are
  siblings, not a chain.
- Each stage span carries `input.length` and `output.length`. No span carries text.
- The first edition's solution also put the user's topic on `pipeline.run` as `pipeline.topic`.
  The self-test reproduces that first: user text does not belong in a span attribute.

## Running

```bash
dotnet run                  # one topic through the configured model, console exporter
dotnet run -- --self-test   # offline: scripted stages, the real TracerProvider, RESULT n/n
```
