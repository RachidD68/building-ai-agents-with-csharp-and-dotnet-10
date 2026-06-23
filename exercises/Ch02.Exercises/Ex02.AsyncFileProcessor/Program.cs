// ============================================================================
// Exercise 02 — Async File Processor (Intermediate)
// Reads multiple text files concurrently, aggregates word counts, and streams
// lines using IAsyncEnumerable<string>.
// ============================================================================

Console.WriteLine("=== Exercise: Async File Processor — Concurrent Word Counter ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Create sample text files
// ---------------------------------------------------------------
string tempDir = Path.Combine(Path.GetTempPath(), "async-file-exercise");
Directory.CreateDirectory(tempDir);

Dictionary<string, string> sampleFiles = new()
{
    ["dotnet.txt"] = """
        C# is a modern object-oriented programming language.
        .NET is a free open-source development platform.
        C# supports async await patterns for concurrent programming.
        The .NET ecosystem includes ASP.NET Core for web development.
        """,
    ["ai.txt"] = """
        Artificial intelligence is transforming software development.
        Large language models power modern AI assistants.
        Machine learning enables pattern recognition at scale.
        AI agents can use tools to interact with external systems.
        """,
    ["cloud.txt"] = """
        Cloud computing provides on-demand computing resources.
        Azure is a comprehensive cloud platform from Microsoft.
        Containers and Kubernetes simplify cloud deployments.
        Serverless computing reduces operational overhead.
        """
};

foreach ((string name, string content) in sampleFiles)
{
    await File.WriteAllTextAsync(
        Path.Combine(tempDir, name), content, cts.Token);
}

Console.WriteLine($"Created {sampleFiles.Count} sample files in: {tempDir}\n");

// ---------------------------------------------------------------
// Step 2: Read all files concurrently using Task.WhenAll
// ---------------------------------------------------------------
Console.WriteLine("--- Reading files concurrently ---\n");

string[] filePaths = Directory.GetFiles(tempDir, "*.txt");

Task<Dictionary<string, int>>[] tasks = filePaths
    .Select(path => CountWordsInFileAsync(path, cts.Token))
    .ToArray();

Dictionary<string, int>[] results = await Task.WhenAll(tasks);

// ---------------------------------------------------------------
// Step 3: Aggregate word counts across all files
// ---------------------------------------------------------------
Dictionary<string, int> aggregated = new(StringComparer.OrdinalIgnoreCase);

foreach (Dictionary<string, int> fileResult in results)
{
    foreach ((string word, int count) in fileResult)
    {
        if (aggregated.TryGetValue(word, out int existing))
        {
            aggregated[word] = existing + count;
        }
        else
        {
            aggregated[word] = count;
        }
    }
}

// ---------------------------------------------------------------
// Step 4: Display sorted results (top 20 words)
// ---------------------------------------------------------------
Console.WriteLine("--- Top 20 Words by Frequency ---\n");
Console.WriteLine($"  {"Word",-25} {"Count",5}");
Console.WriteLine($"  {new string('-', 25)} {new string('-', 5)}");

IEnumerable<KeyValuePair<string, int>> sorted = aggregated
    .OrderByDescending(kvp => kvp.Value)
    .ThenBy(kvp => kvp.Key)
    .Take(20);

foreach ((string word, int count) in sorted)
{
    Console.WriteLine($"  {word,-25} {count,5}");
}

Console.WriteLine($"\n  Total unique words: {aggregated.Count}");
Console.WriteLine($"  Total word occurrences: {aggregated.Values.Sum()}");

// ---------------------------------------------------------------
// Step 5: Demonstrate IAsyncEnumerable streaming
// ---------------------------------------------------------------
Console.WriteLine("\n--- Streaming Lines from All Files ---\n");

int lineNumber = 0;
await foreach (string line in StreamAllLinesAsync(filePaths, cts.Token))
{
    lineNumber++;
    Console.WriteLine($"  [{lineNumber:D3}] {line}");
}

// Clean up
Directory.Delete(tempDir, recursive: true);

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helper methods
// ---------------------------------------------------------------

/// <summary>
/// Reads a file and returns a word frequency dictionary.
/// </summary>
/// <param name="filePath">Path to the text file to process.</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
/// <returns>A dictionary mapping words to their occurrence counts.</returns>
static async Task<Dictionary<string, int>> CountWordsInFileAsync(
    string filePath, CancellationToken cancellationToken)
{
    string content = await File.ReadAllTextAsync(filePath, cancellationToken);
    string fileName = Path.GetFileName(filePath);

    char[] separators = [' ', '\t', '\n', '\r', '.', ',', '!', '?', '(', ')', '-'];
    string[] words = content.Split(separators, StringSplitOptions.RemoveEmptyEntries);

    Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
    foreach (string word in words)
    {
        string trimmed = word.Trim();
        if (trimmed.Length > 1)
        {
            if (counts.TryGetValue(trimmed, out int existing))
            {
                counts[trimmed] = existing + 1;
            }
            else
            {
                counts[trimmed] = 1;
            }
        }
    }

    Console.WriteLine($"  Processed {fileName}: {words.Length} words, {counts.Count} unique");
    return counts;
}

/// <summary>
/// Streams non-empty lines from multiple files as an <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
/// <param name="filePaths">The file paths to read from.</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
/// <returns>An async enumerable of trimmed, non-empty lines.</returns>
static async IAsyncEnumerable<string> StreamAllLinesAsync(
    string[] filePaths,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    foreach (string path in filePaths)
    {
        string[] lines = await File.ReadAllLinesAsync(path, cancellationToken);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                yield return trimmed;
            }
        }
    }
}
