# Exercise 01 — Persona Switcher (Beginner)

## Prompt

Create **three agents** with different personas (Chef, Detective, Poet). Let the user:

1. Pick a persona at startup.
2. Chat with the selected agent using multi-turn conversation.
3. See the persona's unique personality in every response.

## Hints

- Use `ChatClientAgent` with different `instructions` for each persona.
- Use `AgentSession` for multi-turn conversation memory.
- Store personas in a dictionary for easy selection.

## Expected Outcome

```
Choose your persona:
  1. Chef Gordon — dramatic culinary artist
  2. Detective Holmes — deductive reasoning master
  3. Poet Luna — philosophical dreamer

Your choice (1-3): 2

You: What is recursion?
Detective Holmes: Ah, elementary! Recursion is a case most intriguing...
```

## Key Concepts

- Agent personas via system instructions
- `ChatClientAgent` constructor
- `AgentSession` for conversation state
- Interactive console loop
