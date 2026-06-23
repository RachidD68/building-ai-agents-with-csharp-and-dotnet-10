# Ch15.InputSanitization — Input Sanitization and PII Detection

This project demonstrates pre-processing and post-processing guardrails
for AI agents: prompt injection detection and PII redaction.

## What It Shows

### Prompt Injection Detection
Regex-based heuristic rules that detect common injection patterns:
- "Ignore previous instructions" attacks
- System prompt injection attempts
- Role-play and persona manipulation
- Jailbreak attempts (DAN mode, developer mode)
- Data exfiltration attempts
- Encoded/obfuscated payloads

### PII Detection and Redaction
Scans text for Personally Identifiable Information:
- Email addresses
- Phone numbers (US format)
- Credit card numbers
- Social Security Numbers
- IP addresses

## Architecture

```
User Input
  → PromptInjectionDetector.Scan()    [pre-processing]
  → Agent (LLM call)
  → PiiDetector.Scan()                [post-processing]
  → PiiDetector.Redact()              [if PII found]
  → Safe output to user
```

## Running

```bash
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-token"
dotnet run
```

## Production Considerations

- Combine regex heuristics with ML-based classifiers
- Use Azure AI Content Safety for comprehensive content moderation
- Use Microsoft Presidio or Azure AI Language for production PII detection
- Log blocked attempts for security monitoring
