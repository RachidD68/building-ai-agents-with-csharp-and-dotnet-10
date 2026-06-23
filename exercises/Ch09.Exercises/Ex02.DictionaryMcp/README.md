# Exercise 02 — Dictionary MCP (Intermediate)

## Prompt

Build an MCP server that reads a JSON dictionary file and exposes `DefineWord` and `FindSynonyms` tools. Include a consumer that uses these tools through an agent.

## Structure

- `Server/` — MCP server with dictionary tools and `dictionary.json`
- `Consumer/` — MCP consumer + agent

## Key Concepts

- MCP server with file-based data
- JSON data loading in MCP tools
- Tool discovery and integration
- Content-copyable output for dictionary entries
