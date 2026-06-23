# Ch15.CriticAgent — Critic Agent for Output Validation

This project demonstrates the "critic agent" pattern where a second agent
validates the output of a primary agent before it reaches the user.

## What It Shows

- **Two-agent pipeline** — Primary agent generates, critic agent validates
- **Structured validation** — `RunAsync<ValidationResult>()` for type-safe evaluation
- **Quality gates** — Pass/fail decisions based on critic scores
- **Multi-dimensional review** — Factual accuracy, tone, and PII detection

## Architecture

```
User Prompt
  → Primary Agent (ContentWriter)
    → generates response
  → Critic Agent (QualityCritic)
    → evaluates response → ValidationResult
  → Quality Gate
    → APPROVED (score >= 7) or REJECTED
```

## ValidationResult Schema

```json
{
  "passed": true,
  "score": 8,
  "factualAssessment": "Technically accurate explanation",
  "toneAssessment": "Professional and clear",
  "piiDetected": false,
  "issues": [],
  "suggestions": ["Could add a code example"]
}
```

## Running

```bash
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-token"
dotnet run
```

## When to Use This Pattern

- Content generation pipelines requiring quality assurance
- Customer-facing outputs needing tone and accuracy checks
- Compliance-sensitive domains (healthcare, finance, legal)
- Any scenario where LLM output needs programmatic validation
