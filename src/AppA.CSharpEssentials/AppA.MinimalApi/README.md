# C# Essentials for AI Development (Minimal API)

A minimal ASP.NET Core API demonstrating the patterns reused throughout the book: dependency injection, middleware, and endpoint routing.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/hello` | Returns a greeting message |
| POST | `/echo` | Accepts `{ "text": "..." }` and echoes it back with character count |
| GET | `/weather/{city}` | Returns simulated weather for Seattle, London, Tokyo, Nairobi, or Sydney |

Request JSON honours the nullable annotations (`RespectNullableAnnotations` and
`RespectRequiredConstructorParameters` in `ConfigureHttpJsonOptions`), so `POST /echo` with `{}`
or `{"text":null}` is a 400 instead of a `NullReferenceException` (500).

## Running

```bash
dotnet run --project src/AppA.CSharpEssentials/AppA.MinimalApi
```

Offline check (the real app on a loopback port, every endpoint called over HTTP, exit 0 when all pass):

```bash
dotnet run --project src/AppA.CSharpEssentials/AppA.MinimalApi -- --self-test
```

Then test with:

```bash
curl http://localhost:5000/hello
curl -X POST http://localhost:5000/echo -H "Content-Type: application/json" -d '{"text":"Hello AI"}'
curl http://localhost:5000/weather/Seattle
```
