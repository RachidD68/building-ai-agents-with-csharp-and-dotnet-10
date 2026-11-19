# Exercise 02 — Traced Pipeline (Intermediate)

## Prompt

Take Chapter 12's Content Pipeline (Researcher → Writer → Editor). Wrap each stage in
a custom `Activity` span named `pipeline.researcher`, `pipeline.writer`,
`pipeline.editor`. Tag each with the stage's input length and output length. Run a
sample topic through the pipeline and export the trace to the console. Verify the
parent-child relationships are correct.

## Key Concepts

- One outer `pipeline.run` Activity, three stage spans created inside it
- `Activity.Current` + `using` block ordering = parent-child structure; the stage
  spans are siblings at the same level, not nested inside each other
- `input.length` / `output.length` tags on every stage span
- Chained stages: the Writer consumes the Researcher's output, the Editor consumes
  the Writer's output
- OpenTelemetry console exporter — check each stage's `ParentSpanId` against the
  `pipeline.run` span id
