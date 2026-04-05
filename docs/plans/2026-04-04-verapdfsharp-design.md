# VeraPdfSharp Design

Date: 2026-04-04

## Goal

Port the veraPDF validation library from Java to C# on .NET 10, focusing only on PDF/A and PDF/UA validation. GUI, policy-only workflows, fixer flows, and unrelated modules are out of scope.

The first supported flavour set is:

- PDF/A-1B
- PDF/A-2B
- PDF/A-4
- PDF/UA-1

The remaining PDF/A and PDF/UA flavours will be added on the same architecture.

## Constraints

- The port should be faithful to veraPDF behaviour, especially around validation profiles, rule execution, and result reporting.
- External .NET dependencies are allowed if they are not Java-based.
- AGPL and commercial-only dependencies are not acceptable.
- The implementation should be read-only for validation. Save or rewrite support is not required.

## Recommended Architecture

The solution is split into the following layers:

### VeraPdfSharp.Core

Responsibilities:

- Flavour enums and identifiers
- Validation profiles
- Rules, variables, references, error details
- Validation result models
- XML serialization and embedded resource loading

This layer ports the veraPDF abstractions that are independent of Java runtime internals.

### VeraPdfSharp.Scripting

Responsibilities:

- JavaScript rule execution using Jint
- Compiled script caching
- Variable evaluation
- Error argument evaluation
- Execution limits such as timeouts and memory caps

This layer replaces Rhino while preserving the observable behaviour the validator expects.

### VeraPdfSharp.Model

Responsibilities:

- Runtime object graph contract consumed by the validator
- Adapters over a .NET PDF parser
- XMP and metadata-derived objects required by the selected flavours

Instead of porting veraPDF's generated Java model interfaces literally, this layer defines a C# runtime contract with the same behaviour the validation engine depends on:

- `ObjectType`
- `SuperTypes`
- `Properties`
- `Links`
- `GetLinkedObjects(name)`
- `Id`
- `Context`
- `ExtraContext`

For milestone one this model is backed by PdfLexer and internal XMP adapters.

### VeraPdfSharp.Validation

Responsibilities:

- Validator factories and configuration
- Validation traversal engine
- Deferred-rule handling
- Fast-fail behaviour
- Result aggregation

This layer ports the veraPDF engine structure built around profiles and runtime model objects.

## Dependency Strategy

### PdfLexer

Chosen as the initial PDF parser because it is a native .NET implementation and exposes low-level PDF structures directly enough to support a veraPDF-style model adapter.

### Jint

Chosen as the JavaScript engine because it is a native .NET interpreter with permissive licensing and supports binding .NET objects and values into scripts.

## Behavioural Fidelity

The following should remain as close to upstream as practical:

- Upstream XML profiles reused unchanged
- Flavour IDs and rule identifiers
- Validation traversal semantics
- Deferred rule evaluation
- Result structure and failed rule reporting
- Variable and error argument resolution

The following will be equivalent rather than literal:

- Java generated model interfaces become C# interfaces and classes
- Rhino execution becomes Jint execution
- Parsing internals come from PdfLexer, not the upstream Java parser/model stack

## Risks

### Model Coverage

The largest risk is not the validation engine but the breadth of model objects and properties required by the profiles.

- PDF/A-1B, PDF/A-2B, and PDF/A-4 rely heavily on syntax, metadata, resources, and document structure.
- PDF/UA-1 adds significant pressure around structure tree, marked content, role maps, alt text, and metadata relationships.

To reduce risk, milestone one will implement the engine first and then expand model coverage only as needed for the selected flavours.

### Script Compatibility

veraPDF profiles rely on JavaScript expressions over model properties. Jint is expected to be compatible for the required expressions, but script conversion and value coercion must be tested carefully.

## Testing Strategy

- Unit tests for profile parsing, rule evaluation, variable resolution, and result serialization
- Snapshot tests for profile loading and selected rules
- Integration tests with valid and invalid PDFs for the initial flavour set
- Differential tests against upstream veraPDF Java where possible, comparing compliance outcomes and failed rule IDs
- Focused regression tests for PDF/UA structure-tree and XMP-heavy documents

## Non-Goals

- GUI
- Document fixing
- Features reporting
- General policy checking outside PDF/A and PDF/UA
- Full package parity with all upstream Maven modules

## LLM Documentation Requirement

The repository should include documentation written for both human developers and LLM-based agents. This documentation must explain:

- project layout
- validation pipeline
- core abstractions
- extension points
- dependency choices
- invariants that must be preserved when porting additional flavours
