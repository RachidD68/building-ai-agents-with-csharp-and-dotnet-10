using System.Globalization;
using Microsoft.Extensions.AI;

namespace Ch22.CostModel;

public sealed record CostReport(
    string Currency, int Calls, int UnmeasuredCalls, int Tasks, int Accepted,
    decimal ModelCost, decimal ToolCost, decimal RuntimeCost)
{
    public decimal Total => ModelCost + ToolCost + RuntimeCost;

    public decimal? CostPerCall => Calls == 0 ? null : (ModelCost + ToolCost) / Calls;

    /// <summary>The unit Contoso delivers. Undefined, not zero, when nothing was accepted.</summary>
    public decimal? CostPerAcceptedTask => Accepted == 0 ? null : Total / Accepted;

    /// <summary>A call without usage makes the total a lower bound, not a measurement.</summary>
    public bool Complete => UnmeasuredCalls == 0;

    public static string Money(decimal? value, string currency) => value is { } v
        ? $"{v.ToString("0.000000", CultureInfo.InvariantCulture)} {currency}" : "undefined";
}

public static class CostCalculator
{
    public static CostReport Compute(PriceTable prices, UsageLog log)
    {
        if (log.Date < prices.EffectiveDate)
            throw new InvalidDataException($"{prices.Id} takes effect on {prices.EffectiveDate:yyyy-MM-dd}, after {log.Date:yyyy-MM-dd}.");

        decimal model = 0, tools = 0, runtime = 0;
        int unmeasured = 0;
        foreach (CallRecord call in log.Calls)
        {
            ModelRate rate = prices.RateFor(call.Model);   // an unpriced model is refused even when its usage is missing
            if (call.Usage is not { InputTokenCount: { } input, OutputTokenCount: { } output } usage)
            {
                unmeasured++;
            }
            else
            {
                model += TokenCost(rate, input, output, usage);
            }
            foreach (string tool in call.Tools ?? [])
                tools += prices.Tools.TryGetValue(tool, out decimal fee) ? fee
                    : throw new KeyNotFoundException($"{prices.Id}: no rate for tool '{tool}'.");
        }
        foreach ((string host, decimal hours) in log.RuntimeHours ?? new Dictionary<string, decimal>())
            runtime += hours * (prices.RuntimePerHour.TryGetValue(host, out decimal perHour) ? perHour
                : throw new KeyNotFoundException($"{prices.Id}: no runtime rate for '{host}'."));

        return new CostReport(prices.Currency, log.Calls.Count, unmeasured, log.Tasks.Count,
            log.Tasks.Count(t => t.Accepted), model, tools, runtime);
    }

    // Cached input is part of InputTokenCount and reasoning is part of
    // OutputTokenCount (the UsageDetails contract), so each is split out once.
    static decimal TokenCost(ModelRate rate, long input, long output, UsageDetails usage)
    {
        long cached = usage.CachedInputTokenCount ?? 0, reasoning = usage.ReasoningTokenCount ?? 0;
        if (cached > input || reasoning > output)
            throw new InvalidDataException("Cached or reasoning tokens exceed the count that contains them.");
        decimal cost = (input - cached) * rate.Input + cached * rate.CachedInput
                     + (output - reasoning) * rate.Output + reasoning * (rate.ReasoningOutput ?? rate.Output);
        return cost / 1_000_000m;
    }
}
