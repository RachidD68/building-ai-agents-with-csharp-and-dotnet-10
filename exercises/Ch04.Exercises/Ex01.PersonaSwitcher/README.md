# Exercise 01: Persona Switcher (Basic)

## Prompt

Build a console app that reads a persona description and a question, and answers each question with a fresh `ChatClientAgent` whose instructions are that persona. Reuse the `IChatClient`; only the agent changes.

## Acceptance

- A fake-client test asserts which instructions reached the model for each call.
- Switching persona creates no shared mutable state: building a second agent does not change what the first one sends, and no history passes between calls.

## Hints

- The instructions go into `ChatOptions.Instructions` of each model call; `FakeChatClient.Calls` shows them.
- The persona text goes into the instructions verbatim. That is an injection vector; the security chapter treats it.

## Running

```bash
dotnet run --project exercises/Ch04.Exercises/Ex01.PersonaSwitcher
dotnet run --project exercises/Ch04.Exercises/Ex01.PersonaSwitcher -- --self-test
```