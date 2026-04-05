# VeraPdfSharp Agent Notes

## Purpose

This repository ports the Java `veraPDF` validator to C# on .NET 10. The important constraint is fidelity of profile semantics, not literal Java implementation details.

## Architecture

- `src/VeraPdfSharp.Core`: flavours, profiles, rules, variables, results, resource loading
- `src/VeraPdfSharp.Model`: validation object contract and parser adapters
- `src/VeraPdfSharp.Scripting`: Jint-based JavaScript execution
- `src/VeraPdfSharp.Validation`: traversal engine and validator orchestration
- `src/VeraPdfSharp.Cli`: command-line runner
- `tests/VeraPdfSharp.Tests`: engine-focused tests

## Invariants

- Do not rewrite upstream rule logic into C#.
- Prefer reusing upstream XML validation profiles unchanged.
- Keep the validator read-only.
- Preserve the separation between model extraction and rule execution.
- Avoid Java-based, AGPL, or commercial-only dependencies.

## Practical Extension Path

- Add missing model objects and properties in `VeraPdfSharp.Model`.
- Only expand the object surface needed by the next failing profile rules.
- Keep rule evaluation generic in `VeraPdfSharp.Scripting`.
- Add regression tests for each new object/property surfaced to the engine.
