# ADR 0003: Bounded OPC Content Types inspection

- Status: Accepted
- Date: 2026-08-16

## Context

A structurally sound ZIP is not yet a trustworthy OPC package. The Content
Types stream controls how every physical item is interpreted, and ambiguous
case matching, namespace spoofing, unsafe XML, malformed media types, or missing
coverage would give later relationship and WordprocessingML layers inconsistent
views of the same bytes.

Day 2 intentionally did not parse XML. Day 3 therefore needs a second gate that
keeps package safety and OPC semantics separate without allowing a caller to
pair a preflight result with another file.

## Decision

1. Add `OpcPackageInspector` as the only public end-to-end entry point for this
   layer. For snapshot-eligible input, it captures one bounded immutable package
   snapshot, then runs `SafePackageReader` and parses metadata from those exact
   bytes. Oversized input can run Day 2 only to report an earlier package
   failure; it never reaches XML parsing or publishes metadata.
2. Do not accept a caller-supplied `PackageInspectionResult` as proof for a
   separate stream.
3. Read only the already verified Content Types entry into a bounded byte array,
   then use a forward-only `XmlReader`. Do not build an XML DOM.
4. Explicitly prohibit DTD processing, set the resolver to null, enable XML
   character checks, and apply non-zero byte, character, depth, node, mapping,
   and attribute limits.
5. Support UTF-8 and UTF-16 only. Reject UTF-32, EBCDIC signatures, and other
   declared encodings.
6. Match XML by expanded name (`LocalName` plus `NamespaceURI`), not prefix.
   Require the exact OPC Content Types namespace and schema shape. Enforce the
   additional normative requirement that `Types` contains at least one Default
   or Override, even though the supplied XSD uses `minOccurs="0"`.
7. Validate `ST_Extension`, logical OPC part names, and RFC-style media types
   independently. Parameters remain legal on ordinary media types, while
   parameters on OPC-defined fixed types are rejected.
8. Introduce `OpcPartName` rather than reusing Day 2's decoded safety key. ZIP
   path safety and OPC logical part identity are different concepts.
9. Implement a dedicated ASCII-only case comparer for part names, extensions,
   and media-type identifiers. Do not use culture rules or Unicode case folding.
10. Resolve Override before Default for non-Relationships parts. Relationships
    parts use the fixed OPC Relationships content type without requiring a
    mapping. As a controlled-profile rule, an applicable conflicting declaration
    still fails.
11. Require every non-Relationships part to resolve. Reject two physical items
    mapping to one logical part and a part name that is also another part's
    directory.
12. Treat unused Defaults as valid. Reject an unused Override because its
    normative contract requires it to match a package Part.
13. Return immutable models, stable findings, and no manifest or part inventory
    when any Content Types error exists.
14. Require physical Part ZIP item names to be ASCII and decode their
    percent-encoded non-ASCII characters when constructing a logical Unicode
    `OpcPartName`.
15. Continue to perform no `.rels` XML parsing, relationship traversal or target
    resolution, WordprocessingML validation, or mutation in this layer.

## Consequences

Day 4 can consume typed, unambiguous `OpcPartName` values and a complete
Part-to-Content-Type inventory instead of reopening arbitrary ZIP entries or
reimplementing URI matching. The same ASCII comparer and part-name rules can be
reused when relationship targets are resolved.

The controlled URI and media-type profiles can reject unusual but theoretically
valid packages. This is an intentional compatibility tradeoff for gold
templates, documented by fixtures and stable diagnostics.

## Alternatives rejected

- `XDocument`/`XmlDocument`: convenient but does not make depth, node, and
  mapping work as explicit as the streaming state machine.
- Prefix-based XML checks: accept namespace spoofing and reject valid alternate
  prefixes.
- `StringComparer.OrdinalIgnoreCase`: folds non-ASCII characters beyond the OPC
  ASCII comparison rule.
- Treating ZIP entry names as part names: confuses a physical safety key with a
  logical OPC identifier.
- Parsing Content Types before package preflight: exposes the XML layer to
  unbounded or corrupt compressed data.
- Reopening a path after preflight: creates a result-to-bytes replacement window.
