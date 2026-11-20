# Exercise 01: Metrics Collector (basic)

## Prompt

Add a cache hit rate to the Contoso assistant's metrics. Run 100 simulated requests and export
hit and miss counts. The only tag is `cache.name`, and it takes at most three values.

## This solution

| Instrument | Kind | Tag |
|---|---|---|
| `agent.cache.hits` | `Counter<long>` | `cache.name` |
| `agent.cache.misses` | `Counter<long>` | `cache.name` |
| `agent.cache.hit_rate` | `ObservableGauge<double>`, hits / (hits + misses) | `cache.name` |

`cache.name` comes from a fixed list (`response`, `policy`, `embedding`); any other name is
refused when the cache is built, so the number of series cannot grow.

The first edition's solution exported a single counter named `agent.cache.hit_rate` that
counted hits only. With no miss count beside it, a dashboard cannot compute a rate from it.
The self-test reproduces that first.

## Running

```bash
dotnet run                  # 100 requests (10 distinct) to the configured model, console exporter
dotnet run -- --self-test   # offline: echo model, the real MeterProvider, RESULT n/n
```
