# Ex04.JsonRoundTrip (Intermediate)

Serialize a set of records to JSON with `JsonSerializerOptions.Web`, deserialize them
back, and assert a clean round-trip. Demonstrates the serialization features Chapter 2
introduces: `[JsonPropertyName]` to rename a property on the wire, and a string-valued
enum via `[JsonConverter(typeof(JsonStringEnumConverter))]` — the exact shape MAF uses
for tool calls.

Because the record's members are scalar, records' built-in value equality makes
`restored == original` the whole assertion: every member must survive the round-trip.

## Run it

```
dotnet run
```

The program prints each record's JSON and an OK/MISMATCH verdict, then exits with code
0 if every record round-tripped cleanly (1 otherwise) — a CI-friendly check.

Full worked solution and discussion are in Appendix E.
