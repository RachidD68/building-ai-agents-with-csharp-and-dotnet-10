# Exercise 02 — Async File Processor (Intermediate)

## Prompt

Build an async file processor that:

1. Creates sample text files in a temp directory.
2. Reads **multiple files concurrently** using `Task.WhenAll`.
3. Aggregates word counts across all files into a single sorted result.
4. Uses `IAsyncEnumerable<string>` to **stream lines** from all files.

## Hints

- Use `File.ReadAllTextAsync()` for async file I/O.
- `Task.WhenAll()` runs multiple tasks concurrently and awaits them all.
- `IAsyncEnumerable<T>` with `yield return` enables lazy async streaming.
- Use `[EnumeratorCancellation]` attribute for the `CancellationToken` in async iterators.

## Expected Outcome

The program creates sample files, processes them concurrently, and displays:
- Per-file processing stats
- Top 20 words sorted by frequency
- Total unique words and occurrences
- All lines streamed through `IAsyncEnumerable`

## Key Concepts

- `Task.WhenAll` for concurrent async operations
- `IAsyncEnumerable<T>` and `await foreach`
- `CancellationToken` propagation
- File I/O with async patterns
