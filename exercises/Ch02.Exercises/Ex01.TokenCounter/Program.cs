// ============================================================================
// Exercise 01: Token Counter (Basic)
// Counts the tokens of every .txt and .cs file in a folder with a real BPE
// tokenizer, next to the length/4 estimate, and reports chars per token for
// each file. The bundled samples are English prose, Arabic prose and C#.
//
//   dotnet run                       the bundled samples folder
//   dotnet run -- C:\path\to\folder  your own files
//   dotnet run -- --self-test        pinned counts for the samples, RESULT n/n
// ============================================================================

using System.Text;
using Microsoft.ML.Tokenizers;

Console.OutputEncoding = Encoding.UTF8;

const string Encoding200k = "o200k_base";
Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding(Encoding200k);
string samples = Path.Combine(AppContext.BaseDirectory, "samples");

if (args.Contains("--self-test")) return SelfTest(tokenizer, samples);

string folder = args.Length > 0 ? args[0] : samples;
if (!Directory.Exists(folder))
{
    Console.WriteLine($"Folder not found: {folder}");
    return 1;
}

List<FileCount> counts = await CountFolderAsync(folder, tokenizer);
if (counts.Count == 0)
{
    Console.WriteLine($"No .txt or .cs files found in: {folder}");
    return 1;
}

Console.WriteLine($"Tokenizer: {Encoding200k} (Microsoft.ML.Tokenizers, offline)\n");
Console.WriteLine($"  {"File",-12} {"Chars",6} {"Tokens",7} {"len/4",6} {"Chars/token",12}");
Console.WriteLine($"  {new string('-', 47)}");
foreach (FileCount c in counts)
{
    Console.WriteLine($"  {c.Name,-12} {c.Chars,6} {c.Tokens,7} {c.Estimate,6} {c.CharsPerToken,12:F2}");
}
Console.WriteLine($"  {new string('-', 47)}");
Console.WriteLine($"  {"TOTAL",-12} {counts.Sum(c => c.Chars),6} {counts.Sum(c => c.Tokens),7} {counts.Sum(c => c.Estimate),6}");
return 0;

static async Task<List<FileCount>> CountFolderAsync(string folder, Tokenizer tokenizer)
{
    List<FileCount> counts = [];
    foreach (string file in Directory.EnumerateFiles(folder)
                 .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".cs")
                 .Order(StringComparer.OrdinalIgnoreCase))
    {
        // Normalize line endings so a CRLF checkout counts the same as LF.
        string text = (await File.ReadAllTextAsync(file)).ReplaceLineEndings("\n");
        int tokens = tokenizer.CountTokens(text);
        counts.Add(new(Path.GetFileName(file), text.Length, tokens,
            (int)Math.Ceiling(text.Length / 4.0)));
    }
    return counts;
}

static int SelfTest(Tokenizer tokenizer, string samples)
{
    int passed = 0, total = 0;
    void Expect(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    Dictionary<string, FileCount> byName = CountFolderAsync(samples, tokenizer).GetAwaiter().GetResult()
        .ToDictionary(c => c.Name);
    // Pinned once from Microsoft.ML.Tokenizers 2.0.0 (o200k_base).
    (string File, int Tokens)[] pinned = [("arabic.txt", 111), ("notes.txt", 142), ("snippet.cs", 88)];
    foreach ((string file, int tokens) in pinned)
    {
        Expect($"{file} counts {tokens} tokens", byName.TryGetValue(file, out FileCount? c) && c.Tokens == tokens);
    }
    Expect("the count comes from the tokenizer: len/4 misses the Arabic file",
        byName["arabic.txt"].Tokens != byName["arabic.txt"].Estimate);
    Expect("Arabic packs fewer characters per token than English",
        byName["arabic.txt"].CharsPerToken < byName["notes.txt"].CharsPerToken);

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}

sealed record FileCount(string Name, int Chars, int Tokens, int Estimate)
{
    public double CharsPerToken => Tokens == 0 ? 0 : (double)Chars / Tokens;
}
