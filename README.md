# VeraPdfSharp

VeraPdfSharp is a .NET 10 port-in-progress of the Java `veraPDF` validation library, focused on PDF/A and PDF/UA validation.

## Current Scope

Implemented architecture:

- profile loading from upstream veraPDF XML resources
- rule, variable, and validation result models
- Jint-based JavaScript rule execution
- validator traversal engine with deferred rules and shared variables
- PdfLexer-backed parser seam with initial document, trailer, and XMP metadata adapters
- CLI entry point for validating a file against a selected flavour

Initial flavour target:

- PDF/A-1B
- PDF/A-2B
- PDF/A-4
- PDF/UA-1

## Status

This repository currently provides the validation engine and a narrow first model slice. It is not yet a complete parity port of the Java implementation.

What works well already:

- loading upstream validation profiles unchanged
- executing upstream JavaScript rule expressions against model objects
- validating over a real PDF-backed parser seam
- testing the ported engine independently of parser breadth

What is still incomplete:

- broad object-model coverage for all upstream rule object types
- full PDF/UA structure-tree coverage
- complete XMP object model parity
- integration parity testing against the upstream Java validator

## CLI

List supported flavours:

```powershell
dotnet run --project src/VeraPdfSharp.Cli -- --list-flavours
```

Validate a file:

```powershell
dotnet run --project src/VeraPdfSharp.Cli -- sample.pdf 1b
dotnet run --project src/VeraPdfSharp.Cli -- sample.pdf ua1
```

## Key Dependencies

- `PdfLexer` for native .NET PDF parsing
- `Jint` for native .NET JavaScript execution

## Documentation

- Design: [docs/plans/2026-04-04-verapdfsharp-design.md](/e:/dev/VeraPdfSharp/docs/plans/2026-04-04-verapdfsharp-design.md)
- Implementation plan: [docs/plans/2026-04-04-verapdfsharp-implementation-plan.md](/e:/dev/VeraPdfSharp/docs/plans/2026-04-04-verapdfsharp-implementation-plan.md)
- LLM guide: [docs/llm/library-guide.md](/e:/dev/VeraPdfSharp/docs/llm/library-guide.md)
