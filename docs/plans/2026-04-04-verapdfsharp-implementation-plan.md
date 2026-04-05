# VeraPdfSharp Implementation Plan

Date: 2026-04-04

## Phase 1

- Scaffold a .NET 10 solution and project layout.
- Add dependency wiring for Jint and PdfLexer.
- Embed upstream validation profiles and supporting schemas.
- Port flavour, profile, rule, variable, and result models.
- Implement XML profile loading.

## Phase 2

- Implement the Jint-based scripting engine.
- Port validator pipeline types such as validator factory, base validator, flavour validator, and fast-fail behaviour.
- Build the C# model object contract consumed by the validator.

## Phase 3

- Implement PdfLexer-backed document adapters.
- Add XMP-backed metadata objects needed by PDF/A and PDF/UA.
- Cover the object/property/link surface required for PDF/A-1B, PDF/A-2B, PDF/A-4, and PDF/UA-1.

## Phase 4

- Add CLI entry point for validating files.
- Add unit and integration tests.
- Add differential parity harnesses against upstream Java output where practical.
- Add developer and LLM-oriented repository documentation.

## Deliverable Shape

The initial deliverable should include:

- compilable solution
- reusable core validation infrastructure
- read-only validation flow
- at least partial execution of the selected upstream profiles
- clear documentation of remaining model gaps

## Invariants

- Preserve upstream profile semantics.
- Keep the validator read-only.
- Keep model and engine layers separated.
- Avoid Java-based dependencies.
- Prefer permissive dependency licensing.
