# Chapter 2 -- C# Essentials for AI Development (Minimal API)

A minimal ASP.NET Core API demonstrating the patterns reused throughout the book: dependency injection, middleware, and endpoint routing.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/hello` | Returns a greeting message |
| POST | `/echo` | Accepts `{ "text": "..." }` and echoes it back with character count |
| GET | `/weather/{city}` | Returns simulated weather for Seattle, London, Tokyo, Nairobi, or Sydney |

## Running

```bash
dotnet run --project src/Ch02.CSharpEssentials/Ch02.MinimalApi
```

Then test with:

```bash
curl http://localhost:5000/hello
curl -X POST http://localhost:5000/echo -H "Content-Type: application/json" -d '{"text":"Hello AI"}'
curl http://localhost:5000/weather/Seattle
```
