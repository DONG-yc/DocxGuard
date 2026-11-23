# ADR 0002: Bounded read-only package preflight

- Status: Accepted
- Date: 2026-08-16

## Context

A DOCX is an OPC package stored in a ZIP container. Opening an untrusted package directly in the future Open XML layer would grant malformed paths, duplicate part names, excessive decompression, encryption, and corrupt entry data access to a more complex parser.

ZIP central-directory metadata is attacker-controlled. The selected .NET 10 runtime also does not validate an entry's declared CRC-32 merely because `ZipArchiveEntry.Open()` is read to end. A meaningful preflight must therefore enforce both declared-metadata limits and independently measured stream integrity.

## Decision

1. Add `SafePackageReader` to `DocxGuard.Core.Packaging`; adapters must not implement their own package safety rules.
2. Accept a readable, seekable caller-owned stream or a path opened read-only. Restore the original stream position and never close a caller-owned stream.
3. Reject an archive over the raw byte limit before ZIP parsing.
4. Parse the terminal EOCD first, reject ZIP64 and multi-disk profiles, and enforce entry count before constructing `ZipArchive`.
5. Parse central and local records with bounded reads. Strictly decode raw UTF-8 names and require matching names, flags, methods, CRC values, sizes, data descriptors, and contiguous physical ranges.
6. Apply configurable limits to entry-name length, individual and total uncompressed sizes, and compression ratio against the validated compressed span.
7. Validate names without OS filesystem APIs. Reject absolute paths, backslashes, empty segments, dot segments, drive markers, control characters, queries, fragments, malformed escapes, encoded separators, and recursively encoded escapes.
8. Detect duplicates after percent decoding with an ordinal case-insensitive comparer.
9. Reject encrypted entries and require exact `[Content_Types].xml` and `_rels/.rels` entries.
10. Cross-check the metadata exposed by `ZipArchive` with the records already validated by DocxGuard.
11. If resource metadata permits reading, bypass `ZipArchive`'s declaration-based output cap and read validated raw spans sequentially through one fixed-size buffer. Read method 0 directly and use SharpZipLib's headerless `Inflater` for method 8; reject all other compression methods.
12. Require the inflater to report `IsFinished`, consume the entire validated compressed span with no remaining input, re-enforce actual byte and compression-ratio limits, compare actual length, and compute PKZIP CRC-32 independently.
13. Return immutable entry inventories and stable structured findings. Caller misuse, invalid policy, filesystem failures, and cancellation remain exceptions.
14. Do not extract entries, cache entry bodies, parse XML, follow relationships, or mutate the package in this layer.

## Consequences

Later OPC and Open XML inspectors receive input only after a deterministic, testable container gate. Memory used while reading entry bodies is constant, and malicious fixtures can remain small in-memory test data rather than dangerous repository artifacts.

The stricter gold-template profile can reject unusually large but otherwise valid Word files. Callers may provide an explicitly reviewed `PackageLimits` profile when their controlled templates need different bounds.

## Residual risks

The controlled V1 profile rejects ZIP64, multi-disk archives, self-extracting prefixes, central-directory digital-signature records, alternate Unicode-path extra fields, and physical gaps. Supporting any of them later requires dedicated fixtures rather than silently broadening the parser.

The result does not prove full ZIP specification conformance or authenticity. DocxGuard independently consumes validated Stored/Deflate spans for resource and integrity checks, then cross-checks the selected runtime's metadata view. CRC-32 is an accidental-corruption check, not a cryptographic guarantee. XML safety, external relationships, macros, signatures, and OOXML semantics remain outside this layer.

SharpZipLib 1.4.2 is a locked Core dependency used only for raw Deflate completion state. This is a deliberate dependency tradeoff: .NET 10 `DeflateStream` does not expose whether the RFC 1951 final block was reached and can return partial output for a truncated stream.

## Alternatives rejected

- Extracting to a temporary directory: creates a filesystem attack surface and is unnecessary for inspection.
- Trusting `ZipArchiveEntry.Length` alone: central-directory lengths are attacker-controlled.
- Relying on full entry reads for CRC validation: the selected .NET 10 runtime does not provide that guarantee.
- Loading every entry into memory: violates the bounded-memory objective.
- Adding an interface and dependency-injection layer now: there is one concrete package policy and no second implementation requirement.
