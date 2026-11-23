# ADR 0009: Rebuild a new bounded DOCX from an execution-bound patch

- Status: Accepted
- Date: 2026-09-06

## Context

Day 8 publishes verified main XML but no complete document. Repacking must retain
the source package and avoid mixing an executed patch with unrelated resources.
Logical OPC names can differ from physical ZIP names, and archive finalization
itself writes bytes that must remain within output budgets.

## Decision

1. Add the captured source package SHA-256 to passed index inspections and XML
   execution artifacts. Retain the immutable plan internally for later diff.
2. Accept a read-only source plus executor artifact; capture/re-inspect the source
   and require exact execution-source package and original-main hash matches.
3. Replace the inspected physical main entry; copy all other payloads in order.
4. Use no compression, enforce payload limits and bound every archive write,
   including central-directory finalization.
5. Re-inspect the output fully and publish a private-copy in-memory package only
   on success. Day 10 adds structural diff to the same publication gate.
6. Keep disk output, report serialization and CLI outside this milestone.

## Consequences

Output is a complete package but can grow due to no compression. Entry timestamps
and external attributes are preserved; ZIP comments, extra fields and compressed
binary identity are not promised. Existing configured limits still apply.

The artifact binds the execution source. The earlier index-only PatchPlan API
still needs its own source/version contract before external serialization and
approval. Snapshots and defensive copies have additional bounded memory costs.

## Alternatives rejected

- Update the source ZIP: violates immutable input and complicates failure recovery.
- Match only original main XML: permits reuse with different source resources.
- Derive physical entry names by stripping the leading slash of a logical name:
  breaks encoded Part names.
- Check archive size after writing everything: misses the write-time memory bound.
