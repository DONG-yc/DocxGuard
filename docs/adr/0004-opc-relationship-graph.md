# ADR 0004: Atomic OPC relationship graph

- Status: Accepted
- Date: 2026-08-28

## Context

Day 3 can name and type every package Part, but it cannot tell which Parts are
connected. Opening `.rels` files naively would reintroduce unsafe XML,
unbounded allocations, URI ambiguity, missing-target errors, and a risk that
callers traverse a graph built from different bytes than the verified package.

The V1 product is a controlled gold-template pipeline. It needs deterministic
internal traversal before WordprocessingML inspection or editing begins, and it
does not need live web links or other external targets.

## Decision

1. Extend `OpcPackageInspector`; do not expose a separate relationship parser
   that can be paired with an arbitrary preflight result.
2. Parse every Relationships part from the same bounded immutable snapshot used
   by package and Content Types inspection.
3. Reuse the shared forward-only XML security policy: DTD prohibited, resolver
   null, UTF-8/UTF-16 only, character checking, and explicit character, node,
   depth, attribute, byte, and relationship-count limits.
4. Match expanded XML names and accept only the OPC Relationships root,
   direct Relationship children, and the schema-defined unqualified
   attributes. Reject `xml:base`, foreign attributes, and foreign elements.
5. Use a controlled ASCII xsd:ID profile and require ordinal uniqueness within
   each Relationships part.
6. Require Relationship Type to be a well-formed absolute URI. Preserve its raw
   value and compare it case-sensitively; do not unescape or normalize it.
7. Interpret omitted TargetMode as Internal and accept only the exact values
   Internal and External.
8. Parse External relationships but reject them with a stable policy finding.
9. Derive each source from its Relationships part name. The package source is
   `/_rels/.rels`; a Part source follows the sibling `_rels/name.rels`
   convention. Non-package sources must exist.
10. Resolve internal targets with package-root/source-directory semantics and
    dot-segment normalization, then require a valid existing `OpcPartName`.
11. Prohibit relationships from or to Relationships parts.
12. Allow distinct IDs to target the same Part.
13. Publish the immutable graph only if all Relationships parts and targets
    pass. On relationship failure, preserve the already verified manifest and
    Part inventory but publish no partial graph.
14. Keep the layer read-only. WordprocessingML semantic validation, editing,
    rollback, diffing, and repacking remain later milestones.

## Consequences

Later inspectors receive a deterministic graph whose nodes are the exact typed
Parts already verified by Day 3. They do not reopen paths, interpret external
resources, or guess how relative targets should be resolved.

A normal document containing a hyperlink with TargetMode External fails the V1
controlled profile even if Word can open it. This is intentional and visible as
`REL_009`; broader compatibility requires a policy decision rather than a
silent parser change.

Relationship failures retain verified Day 3 metadata for diagnostics. This is
different from a Content Types failure, where no manifest or Part inventory is
trusted.

## Alternatives rejected

- `System.IO.Packaging` traversal before the custom gate: hides target
  resolution and resource work behind a higher-level API and does not preserve
  the project's stable diagnostics.
- `XDocument` or `XmlDocument`: makes bounded forward progress and
  node/depth accounting less explicit.
- `System.Uri` as the only target resolver: its generic URI behavior and
  platform conversions do not directly express the controlled OPC Part-name
  policy.
- Keeping a partially valid graph: invites callers to treat incomplete
  relationship reachability as authoritative.
- Following external targets: expands a local document inspection into network
  access and violates the V1 trust boundary.
