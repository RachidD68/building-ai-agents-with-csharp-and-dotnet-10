# Ch06.PersistentSession — Surviving App Restarts

Serializes conversation sessions to a JSON file so conversations persist across application restarts.

## What You'll Learn

- `SerializeSessionAsync()` / `DeserializeSessionAsync()` for session persistence
- Loading and resuming a previous conversation
- Practical "remember me" pattern for chat applications

## Run

```bash
# First run — starts a new conversation
dotnet run

# Second run — resumes the previous conversation
dotnet run
```
