# ADR 0005: Separate Word main-document trust boundary

- Status: Accepted
- Date: 2026-08-28

## Context

A complete OPC Relationship Graph does not prove that a package is a supported
DOCX. A caller could otherwise assume `/word/document.xml`, accept multiple
main relationships, treat a template or macro-enabled Part as a normal
document, or hand unbounded XML directly to a higher-level object model.

At the same time, making Word-specific rules part of `OpcPackageInspector`
would incorrectly reject valid non-Word OPC packages and change the meaning of
the earlier trust boundary.

## Decision

1. Add `DocxDocumentInspector` above `OpcPackageInspector`; do not change the
   definition of a passed OPC result.
2. Capture one immutable snapshot before either layer. The OPC inspector reads
   a copy of those bytes, and main-document parsing uses the original immutable
   snapshot.
3. Continue no further when OPC inspection fails.
4. Discover the main Part through one package-level `officeDocument`
   relationship. Do not hard-code a Part path.
5. Support the exact Transitional and Strict relationship types. Require
   exactly one and bind its family to the corresponding root namespace.
6. Require the macro-free DOCX main-document content type. Reject macro-enabled
   documents and template content types.
7. Read only the already verified main entry into a bounded byte array and use
   a forward-only `XmlReader`.
8. Reuse the DTD-free, resolver-free UTF-8/UTF-16 XML policy, with larger
   document-specific resource limits.
9. Validate only the minimum safe structural envelope in this milestone:
   `document`, optional `background`, one `body`, bounded direct children,
   and final-position `sectPr`.
10. Allow direct body elements in the selected WordprocessingML namespace and
    Markup Compatibility `AlternateContent`; defer their full schemas.
11. Publish `WordMainDocumentInfo` atomically. Preserve a successful OPC
    result when the Word layer fails, but never publish a partial Word model.
12. Keep the layer read-only and keep it in Core until an inspect/report CLI
    contract is complete.

## Consequences

Later indexing and patch-planning layers receive a verified main Part rather
than guessing its location. They also know whether the document is Transitional
or Strict and can choose namespace-aware behavior explicitly.

The extra immutable copy inside OPC inspection costs bounded memory but keeps
the existing public inspector and its guarantees unchanged. It can be removed
later through an internal snapshot pipeline without changing public contracts.

The minimum structure check is deliberately weaker than full ISO schema
validation. A passed result says where the main document is and that its outer
envelope is safe and coherent; it does not say every descendant is valid.

## Alternatives rejected

- Extend `OpcPackageInspector` with mandatory Word rules: conflates OPC and
  DOCX semantics and breaks valid generic package inspection.
- Assume `/word/document.xml`: ignores relationship-defined Part identity.
- Load `WordprocessingDocument` immediately: moves resource and XML trust
  decisions behind a high-level object model.
- Support only Transitional documents by accident: makes Strict failures
  ambiguous instead of an explicit compatibility policy.
- Run a full schema validator now: expands Day 5 beyond main-entry discovery
  and the minimum structure needed by the next indexing milestone.
