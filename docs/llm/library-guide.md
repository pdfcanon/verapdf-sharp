# VeraPdfSharp LLM Guide

## What This Library Is

VeraPdfSharp is a profile-driven PDF validator. It does not hardcode most validation rules in C#. Instead, it:

1. loads XML validation profiles,
2. traverses a runtime object graph representing a PDF,
3. evaluates JavaScript rule expressions against each object,
4. records failed assertions in a structured result.

This means the most important parts of the library are not just classes and methods. They are the contracts between:

- profile XML
- runtime model objects
- script evaluation
- validator traversal

## Mental Model

### Core

`VeraPdfSharp.Core` owns data contracts.

Key concepts:

- `PDFAFlavour`
- `ValidationProfile`
- `Rule`
- `Variable`
- `ValidationResult`
- `TestAssertion`

If you need to understand what the validator is supposed to do, start here.

### Model

`VeraPdfSharp.Model` owns the object graph that rules run against.

The validator expects each model object to expose:

- `ObjectType`
- `SuperTypes`
- `Properties`
- `Links`
- `GetPropertyValue(name)`
- `GetLinkedObjects(name)`

Rules refer to property names directly in JavaScript. The scripting layer rewrites those names against the current model object.

If a rule fails because a property or linked object is missing, the fix usually belongs in `VeraPdfSharp.Model`, not in the validator.

### Scripting

`VeraPdfSharp.Scripting` is the bridge between profile text and model objects.

Responsibilities:

- bind `obj` into the JavaScript runtime
- rewrite bare property names to `obj.GetPropertyValue(...)`
- rewrite `<link>_size` to linked-object counts
- evaluate rule expressions, variable expressions, and error arguments

If scripts start behaving differently from upstream, this layer is one of the first places to inspect.

### Validation

`VeraPdfSharp.Validation` orchestrates traversal and evaluation.

Responsibilities:

- initialize variables from profile defaults
- visit model objects once
- evaluate rules by object type and super type
- handle deferred rules after traversal
- aggregate failures into `ValidationResult`

This layer should remain generic. Avoid putting flavour-specific logic here.

## Extension Workflow

When adding support for a new failing rule:

1. Identify the rule's object type and property names from the profile XML.
2. Confirm whether the object type already exists in `VeraPdfSharp.Model`.
3. If not, add a new model object or extend an existing adapter.
4. Surface the exact property names expected by the rule.
5. Add or update tests that exercise the new property surface.
6. Only change `VeraPdfSharp.Scripting` if the failure is due to evaluation semantics, not missing model data.

## Current Limits

The current codebase has a working validation engine and broad model coverage for PDF/UA-1 and the PDF/A family, but it does not yet reach full veraPDF parity.

The remaining gaps are:

- **CMap types** (`PDCMap`, `CMapFile`, `PDReferencedCMap`): font CMap-level rules are not yet executed. These are needed for some PDF/A font-encoding assertions.
- **Form XObject MCID tracking**: `_formXObjectsWithMcids` cache is declared but not yet populated, so MCID-in-form-XObject checks are incomplete.
- **`IsUniqueSemanticParent`**: implemented as a heuristic (checks for a `StructParents` key). The upstream logic is more precise.
- **PDF/UA-2 rules**: the profile loads and rules execute, but the model surface for PDF/UA-2-specific checks has not been verified against the corpus.
- **Differential parity testing**: there is no automated comparison of results against the upstream Java veraPDF output.

## Safe Assumptions

- Upstream XML profile files are the source of truth for validation semantics.
- The validator should be read-only.
- Parser and model code may evolve significantly while the core profile and result contracts stay comparatively stable.
