# Exercise 01 — Record Patterns (Beginner)

## Prompt

Create a **Shape hierarchy** using C# records (`Circle`, `Rectangle`, `Triangle`) that inherit from an abstract `Shape` record. Use **switch expressions** with **positional pattern matching** to:

1. Calculate the area of each shape.
2. Describe each shape as a human-readable string.
3. Classify shapes by area size (Small / Medium / Large / Extra Large).

## Hints

- Use `abstract record Shape;` as the base type.
- Positional patterns deconstruct records: `Circle(var r) => ...`
- Switch expressions can use relational patterns: `< 10 => "Small"`
- Records give you value equality and `ToString()` for free.

## Expected Outcome

```
=== Exercise: Record Patterns — Shape Area Calculator ===

  Circle (radius=5)                    Area =      78.54
  Rectangle (4 x 7)                    Area =      28.00
  Triangle (base=6, height=3)          Area =       9.00
  Circle (radius=2.5)                  Area =      19.63
  Rectangle (10 x 10)                  Area =     100.00
  Triangle (base=8, height=4.5)        Area =      18.00

  Total area                           =     253.17

--- Shape Size Classification ---

  Circle (radius=5)                    → Large
  Rectangle (4 x 7)                    → Medium
  Triangle (base=6, height=3)          → Small
  Circle (radius=2.5)                  → Medium
  Rectangle (10 x 10)                  → Extra Large
  Triangle (base=8, height=4.5)        → Medium

=== Exercise complete! ===
```

## Key Concepts

- Records and positional deconstruction
- Switch expressions with pattern matching
- Relational and combinatory patterns (`and`, `or`)
- Abstract record hierarchies
