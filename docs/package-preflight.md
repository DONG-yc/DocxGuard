# Package preflight contract

`SafePackageReader` is the first trust boundary for an untrusted DOCX file. It treats the input as a ZIP/OPC container and returns either a verified package inventory or structured findings. It does not extract files or parse XML.

## Guarantee

A `Passed` result means that, under the configured policy:

1. EOCD and central-directory bounds were internally consistent, and entry count was within policy before `ZipArchive` materialized entries;
2. raw entry names were valid UTF-8 and local headers matched central names, flags, methods, CRC values, and sizes;
3. local records and optional data descriptors formed one contiguous, non-overlapping physical region;
4. every entry name passed platform-independent lexical, decoded-path, and recursive-escape checks;
5. no two entries mapped to the same case-insensitive decoded name;
6. no entry was encrypted;
7. sizes, total size, and compression ratios against validated physical spans were within limits;
8. both `[Content_Types].xml` and `_rels/.rels` existed with exact names;
9. every Stored or Deflate physical span was independently read or decompressed to a confirmed final block within actual byte and compression-ratio limits;
10. every actual uncompressed length and independently calculated ZIP CRC-32 matched the validated ZIP records.

This guarantee does not mean that XML is well-formed, relationships are safe, OOXML markup is valid, or the document is authentic. Those belong to later inspection layers. CRC-32 detects corruption; it is not a signature or cryptographic integrity proof.

## Default gold-template profile

| Limit | Default |
|---|---:|
| Raw archive bytes | 50 MiB |
| Entry count | 2,048 |
| Entry-name characters | 1,024 |
| Single uncompressed entry | 64 MiB |
| Total uncompressed bytes | 256 MiB |
| Compression ratio per entry | 100:1 |

These values are configurable product policy for controlled templates, not general Microsoft Word format limits.

## API behavior

```csharp
SafePackageReader reader = new();
PackageInspectionResult result = reader.Inspect(stream, PackageLimits.Default);

if (result.Status == PackageInspectionStatus.Failed)
{
    foreach (PackageFinding finding in result.Findings)
    {
        Console.Error.WriteLine($"{finding.Code}: {finding.Message}");
    }
}
```

- Stream inputs must be readable and seekable. Caller contract violations throw.
- Invalid limit configuration throws before the archive is inspected.
- Cancellation propagates as `OperationCanceledException`.
- Untrusted package-format, path, resource, and integrity failures become findings.
- The reader leaves caller-owned streams open and restores their original position.
- Files are opened read-only with shared-read access. No entry is written to disk.

## Stable finding codes

| Code | Meaning |
|---|---|
| `DOCXGUARD_PKG_001` | Raw archive byte limit exceeded |
| `DOCXGUARD_PKG_002` | Invalid or unreadable ZIP container |
| `DOCXGUARD_PKG_003` | Entry-count limit exceeded |
| `DOCXGUARD_PKG_004` | Invalid or unsafe entry path |
| `DOCXGUARD_PKG_005` | Duplicate logical entry name |
| `DOCXGUARD_PKG_006` | Encrypted entry is unsupported |
| `DOCXGUARD_PKG_007` | Per-entry uncompressed limit exceeded |
| `DOCXGUARD_PKG_008` | Total uncompressed limit exceeded |
| `DOCXGUARD_PKG_009` | Compression-ratio limit exceeded |
| `DOCXGUARD_PKG_010` | Entry data or compression method is invalid |
| `DOCXGUARD_PKG_011` | Actual and declared entry lengths differ |
| `DOCXGUARD_PKG_012` | Actual and declared ZIP CRC-32 differ |
| `DOCXGUARD_PKG_013` | `[Content_Types].xml` is missing |
| `DOCXGUARD_PKG_014` | `_rels/.rels` is missing |
| `DOCXGUARD_PKG_015` | ZIP feature is outside the controlled V1 profile |
| `DOCXGUARD_PKG_016` | Local and central ZIP records disagree |
| `DOCXGUARD_PKG_017` | Entry name contains invalid raw UTF-8 |

## Residual risk

The V1 structural profile deliberately rejects ZIP64, multi-disk archives, self-extracting prefixes, digital-signature records, alternate Unicode-path extra fields, and unaccounted gaps between physical records. These can be valid ZIP features but are unnecessary for controlled DOCX templates; supporting them later requires explicit fixtures and policy decisions.

The preflight does not claim exhaustive conformance with every ZIP specification field. Its narrower claim is that bounded records passed DocxGuard's structural profile, Stored bytes or Deflate output were independently consumed within policy, and the same records were accepted by the selected runtime. V1 supports ZIP compression methods 0 (Stored) and 8 (Deflate); other methods fail closed.

Raw Deflate verification uses SharpZipLib's headerless `Inflater` because it exposes `IsFinished`, processed input, and remaining input. DocxGuard requires a final block exactly at the validated compressed-span boundary; this closes the silent-truncation behavior observed with .NET `DeflateStream`.
