# ADR 0001: V1 scope and engineering baseline

- Status: Accepted
- Date: 2026-08-16

## Context

DocxGuard is intended to compile verified business data into controlled changes against immutable DOCX templates. The long-term system includes template contracts, a typed PatchPlan, transactional Open XML execution, layered verification, and an optional agent adapter.

Starting with document mutation or an LLM would hide the package, safety, and failure-model work that must be trustworthy first. The initial milestone therefore establishes a narrow, testable foundation.

## Decision

1. Use a modular monolith and a CLI-first interface.
2. Target .NET 10 LTS and use `DocumentFormat.OpenXml` as the future authoritative Open XML backend.
3. V1 supports `.docx` gold templates only. `.docm`, encrypted documents, digitally signed documents, and arbitrary third-party documents are outside the supported scope.
4. Week 1 is read-only. Day 1 creates only the project skeleton, CLI behavior, and explicit format policy.
5. Treat every document as untrusted and immutable. Never modify the source path in place.
6. Before an Open XML package is opened, inspect its ZIP container in memory with configurable limits. Do not extract untrusted documents to disk.
7. XML parsing will prohibit DTD processing and external entity resolution.
8. External relationships are detected and rejected in V1.
9. Missing targets, ambiguous anchors, malformed structures, and unsupported objects fail closed with structured diagnostics.
10. GPT, MCP, GUI, databases, cloud services, and document mutation are not part of the current milestone.

## Consequences

The first release has a deliberately narrow feature set, but its trust boundary is explicit and testable. CLI, MCP, and future API adapters must depend on the same core rather than implementing their own document logic.

This choice also means the project will not claim general Word compatibility. Support will be expressed as a tested capability matrix.

## Alternatives rejected

- Microservices: unnecessary operational complexity for the initial project.
- Direct arbitrary XML or XPath editing: too much authority for agents and difficult to validate.
- `python-docx` as the authoritative writer: useful as a reference or test helper, but not the selected trusted execution backend.
- Natural-language instructions directly controlling document mutation: violates the intended contract and policy boundary.

