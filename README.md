# VeraPdfSharp

VeraPdfSharp is a .NET 10 port-in-progress of the Java `veraPDF` validation library, focused on PDF/A and PDF/UA validation.

## Current Scope

Implemented architecture:

- profile loading from upstream veraPDF XML resources
- rule, variable, and validation result models
- Jint-based JavaScript rule execution
- validator traversal engine with deferred rules and shared variables
- PdfLexer-backed parser seam covering document, trailer, pages, annotations, fonts, structure tree, content streams, XMP, and ICC profiles
- CLI entry point for validating a file against a selected flavour

Supported flavours:

- PDF/A-1B, PDF/A-1A
- PDF/A-2B, PDF/A-2A, PDF/A-2U
- PDF/A-3B
- PDF/A-4, PDF/A-4E, PDF/A-4F
- PDF/UA-1, PDF/UA-2

## Status

This repository provides a working validation engine with broad PDF/UA-1 model coverage. It is not yet at full parity with the Java veraPDF implementation.

What works well:

- loading upstream validation profiles unchanged
- executing upstream JavaScript rule expressions against model objects
- validating over a real PDF-backed parser seam
- full PDF structure-tree coverage: all standard structure element types, table geometry, heading nesting, list semantics, annotations, links
- content-stream objects: text items, marked content, glyphs, effective language propagation
- XMP metadata parsing and XMP property exposure
- annotations, form fields, AcroForms, output intents, encryption, file specifications, ExtGState, optional content
- font objects: Type 0, Type 1, TrueType, Type 3, CID fonts with program, widths, and encoding properties
- corpus-level integration tests with a baseline exception file

What is still incomplete:

- CMap-related model types (`PDCMap`, `CMapFile`, `PDReferencedCMap`) — font CMap rules not yet covered
- `IsUniqueSemanticParent` uses a simplified heuristic
- Form XObject MCID tracking (`_formXObjectsWithMcids`) not yet populated
- PDF/UA-2 and some PDF/A font-encoding rules still produce deviations from upstream

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

## Testing

Run unit tests:

```powershell
dotnet test tests/VeraPdfSharp.Tests --filter "FullyQualifiedName!~CorpusTests"
```

Run corpus integration tests (requires `veraPDF-corpus-staging/` alongside the repo root):

```powershell
dotnet test tests/VeraPdfSharp.Tests --filter "FullyQualifiedName~CorpusTests"
```

Diagnose individual corpus failures:

```powershell
dotnet build tools/CorpusDiag -v q
dotnet run --project tools/CorpusDiag --no-build
```

## Key Dependencies

- `PdfLexer` for native .NET PDF parsing
- `Jint` for native .NET JavaScript execution

## Documentation

- Design: [docs/plans/2026-04-04-verapdfsharp-design.md](/e:/dev/VeraPdfSharp/docs/plans/2026-04-04-verapdfsharp-design.md)
- Implementation plan: [docs/plans/2026-04-04-verapdfsharp-implementation-plan.md](/e:/dev/VeraPdfSharp/docs/plans/2026-04-04-verapdfsharp-implementation-plan.md)
- LLM guide: [docs/llm/library-guide.md](/e:/dev/VeraPdfSharp/docs/llm/library-guide.md)
