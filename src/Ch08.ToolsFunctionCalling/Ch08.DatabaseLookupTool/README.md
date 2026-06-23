# Chapter 8 -- Tools and Function Calling (Database Lookup Tool)

Demonstrates tools backed by dependency-injected services. The tool class receives an `IProductRepository` via constructor injection, showing how real-world tools connect to data stores.

## Key Concepts

| Concept | Description |
|---------|-------------|
| DI for tools | `IProductRepository` injected into `ProductTools` via constructor |
| Instance methods | `AIFunctionFactory.Create` works with instance methods, not just static |
| Multi-step workflow | Agent chains: search -> lookup -> place order |
| Separation of concerns | Repository pattern separates data access from tool logic |

## Running

```bash
dotnet run --project src/Ch08.ToolsFunctionCalling/Ch08.DatabaseLookupTool
```
