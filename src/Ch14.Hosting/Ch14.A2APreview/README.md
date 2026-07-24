# Ch14.A2APreview — Agent-to-Agent (A2A) Protocol Integration

This project demonstrates how to expose an AI agent via the A2A protocol
using the official `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` library.

## What is A2A?

The Agent-to-Agent (A2A) protocol, originally proposed by Google and now
maintained by the Linux Foundation, defines a standard way for agents to:

1. **Discover** each other via Agent Cards
2. **Communicate** by sending messages via HTTP
3. **Stream** responses using Server-Sent Events
4. **Interoperate** across different agent frameworks

## What This Shows

- **Agent Registration** — Using `AddAIAgent()` to register the agent in DI
- **A2A Mapping** — Using `MapA2A()` to expose the agent via the A2A protocol
- **Agent Card** — Automatic discovery endpoint for agent metadata
- **Session Management** — In-memory session store for multi-turn conversations

## Endpoints

| Method | Path                                          | Description                     |
|--------|-----------------------------------------------|---------------------------------|
| GET    | `/a2a/coding-assistant/v1/card`               | Agent Card (discovery)          |
| POST   | `/a2a/coding-assistant/v1/message`            | Send a message to the agent     |
| POST   | `/a2a/coding-assistant/v1/message:stream`     | Stream a response from the agent|
| GET    | `/`                                           | Service info                    |

## Example: Discover the Agent

```bash
curl http://localhost:5000/a2a/coding-assistant/v1/card
```

## Example: Send a Message

```bash
curl -X POST http://localhost:5000/a2a/coding-assistant/v1/message \
  -H "Content-Type: application/json" \
  -d '{
    "message": {
      "kind": "message",
      "role": "user",
      "parts": [{ "kind": "text", "text": "How do I use LINQ?" }],
      "messageId": null,
      "contextId": "session-1"
    }
  }'
```

## Key Packages

- `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` — A2A protocol integration
- `Microsoft.Agents.AI.Hosting` — Agent hosting and session management

## Full A2A Specification

See: https://a2a-protocol.org/latest/
Docs: https://learn.microsoft.com/en-us/agent-framework/integrations/a2a
