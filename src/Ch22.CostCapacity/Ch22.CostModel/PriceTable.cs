using System.Text.Json;

namespace Ch22.CostModel;

/// <summary>
/// A price table is data, never code: a currency, the date it takes effect,
/// the unit its token rates use, and one rate per model, tool and runtime.
/// The calculator contains no provider name and no price literal.
/// </summary>
public sealed record PriceTable(
    string Id,
    string Currency,
    DateOnly EffectiveDate,
    string Unit,
    IReadOnlyDictionary<string, ModelRate> Models,
    IReadOnlyDictionary<string, decimal> Tools,
    IReadOnlyDictionary<string, decimal> RuntimePerHour)
{
    public const string PerMillionTokens = "per-million-tokens";

    static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static PriceTable Load(string path) => Parse(File.ReadAllText(path));

    public static PriceTable Parse(string json)
    {
        PriceTable table = JsonSerializer.Deserialize<PriceTable>(json, s_json)
            ?? throw new InvalidDataException("Empty price table.");
        if (string.IsNullOrWhiteSpace(table.Id)) throw new InvalidDataException("Price table has no id.");
        if (table.Currency is not { Length: 3 } c || !c.All(char.IsAsciiLetterUpper))
            throw new InvalidDataException($"{table.Id}: currency must be an ISO 4217 code (three capital letters).");
        if (table.EffectiveDate == default) throw new InvalidDataException($"{table.Id}: no effective date.");
        if (table.Unit != PerMillionTokens) throw new InvalidDataException($"{table.Id}: unit '{table.Unit}' is not '{PerMillionTokens}'.");
        if (table.Models is null || table.Models.Count == 0) throw new InvalidDataException($"{table.Id}: no model rates.");
        if (table.Models.Values.Any(r => r is null || r.Input < 0 || r.CachedInput < 0 || r.Output < 0 || r.ReasoningOutput < 0)
            || (table.Tools?.Values.Any(v => v < 0) ?? false) || (table.RuntimePerHour?.Values.Any(v => v < 0) ?? false))
            throw new InvalidDataException($"{table.Id}: a negative rate would pay you for the work.");
        return table with { Tools = table.Tools ?? new Dictionary<string, decimal>(),
                            RuntimePerHour = table.RuntimePerHour ?? new Dictionary<string, decimal>() };
    }

    public ModelRate RateFor(string model) => Models.TryGetValue(model, out ModelRate? rate)
        ? rate : throw new KeyNotFoundException($"{Id}: no rate for model '{model}'.");
}

/// <summary>
/// Rates per million tokens. Reasoning tokens are part of the output count
/// (UsageDetails says so); a table may price them apart with ReasoningOutput.
/// </summary>
public sealed record ModelRate(decimal Input, decimal CachedInput, decimal Output, decimal? ReasoningOutput = null);
