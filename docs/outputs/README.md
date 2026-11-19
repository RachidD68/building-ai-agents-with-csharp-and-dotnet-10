# Captured outputs

Full console outputs of the samples whose excerpts are printed in the book. Each file is a
verbatim copy of a capture made with the book's smoke runner; the first line records the
code commit, the .NET SDK, the platform and the configured LLM provider. The runner's
STDERR block (a "Using launch settings from <local path>" line) is omitted.

Self-test outputs (`*.self-test.txt`) use no model or a scripted model, so they are
reproducible: `dotnet run -- --self-test` in the project folder. The other files are single
live runs against a local Ollama llama3.2; a re-run will word its answers differently.
Some of those runs show the model inventing facts that no tool returned. The book prints
them as failures, on purpose.
