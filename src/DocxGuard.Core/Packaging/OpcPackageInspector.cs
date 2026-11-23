using System.IO.Compression;
using System.Text;

namespace DocxGuard.Core.Packaging;

/// <summary>
/// Performs package preflight, validates OPC Content Types, and builds a
/// complete internal Relationship Graph from one immutable package snapshot.
/// </summary>
public sealed class OpcPackageInspector
{
    private const string ContentTypesEntryName = "[Content_Types].xml";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly SafePackageReader packageReader = new();

    public OpcInspectionResult Inspect(
        string path,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            options: FileOptions.RandomAccess);

        return Inspect(stream, packageLimits, opcLimits, cancellationToken);
    }

    public OpcInspectionResult Inspect(
        Stream packageStream,
        PackageLimits? packageLimits = null,
        OpcInspectionLimits? opcLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        if (!packageStream.CanRead)
        {
            throw new ArgumentException("The package stream must be readable.", nameof(packageStream));
        }

        if (!packageStream.CanSeek)
        {
            throw new ArgumentException("The package stream must be seekable.", nameof(packageStream));
        }

        packageLimits ??= PackageLimits.Default;
        opcLimits ??= OpcInspectionLimits.Default;
        packageLimits.Validate();
        opcLimits.Validate();

        long initialPosition = packageStream.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            long packageLength = packageStream.Length;
            long snapshotLimit = Math.Min(
                packageLimits.MaxArchiveBytes,
                opcLimits.MaxPackageSnapshotBytes);
            if (packageLength > snapshotLimit)
            {
                PackageInspectionResult oversizedPackageInspection = packageReader.Inspect(
                    packageStream,
                    packageLimits,
                    cancellationToken);
                if (oversizedPackageInspection.Status == PackageInspectionStatus.Failed)
                {
                    return new OpcInspectionResult(
                        oversizedPackageInspection,
                        null,
                        [],
                        []);
                }

                return Failure(
                    oversizedPackageInspection,
                    new ContentTypeFinding(
                        ContentTypeFindingCodes.ResourceLimitExceeded,
                        FindingSeverity.Error,
                        $"The package is {packageLength} bytes; the immutable OPC snapshot limit is {opcLimits.MaxPackageSnapshotBytes} bytes."));
            }

            byte[] snapshotBytes = ReadStableSnapshot(
                packageStream,
                checked((int)packageLength),
                cancellationToken);
            using MemoryStream snapshot = new(snapshotBytes, writable: false);
            return InspectSnapshot(
                snapshot,
                packageLimits,
                opcLimits,
                cancellationToken);
        }
        finally
        {
            packageStream.Position = initialPosition;
        }
    }

    private OpcInspectionResult InspectSnapshot(
        Stream packageSnapshot,
        PackageLimits packageLimits,
        OpcInspectionLimits opcLimits,
        CancellationToken cancellationToken)
    {
        PackageInspectionResult packageInspection = packageReader.Inspect(
            packageSnapshot,
            packageLimits,
            cancellationToken);
        if (packageInspection.Status == PackageInspectionStatus.Failed)
        {
            return new OpcInspectionResult(packageInspection, null, [], []);
        }

        try
        {
            PackageEntryInfo contentTypesEntry = packageInspection.Entries.Single(
                entry => string.Equals(
                    entry.Name,
                    ContentTypesEntryName,
                    StringComparison.Ordinal));

            if (contentTypesEntry.UncompressedLength > opcLimits.MaxContentTypesXmlBytes)
            {
                return Failure(
                    packageInspection,
                    new ContentTypeFinding(
                        ContentTypeFindingCodes.ResourceLimitExceeded,
                        FindingSeverity.Error,
                        $"The Content Types stream is {contentTypesEntry.UncompressedLength} bytes; the configured limit is {opcLimits.MaxContentTypesXmlBytes} bytes.",
                        ContentTypesEntryName));
            }

            byte[] xml = ReadContentTypesEntry(
                packageSnapshot,
                checked((int)contentTypesEntry.UncompressedLength),
                cancellationToken);
            ManifestParseResult parsed = ContentTypeManifestParser.Parse(
                xml,
                opcLimits,
                cancellationToken);
            if (parsed.Manifest is null)
            {
                return new OpcInspectionResult(
                    packageInspection,
                    null,
                    [],
                    parsed.Findings);
            }

            OpcInspectionResult contentTypeInspection = BuildPartInventory(
                packageInspection,
                parsed.Manifest,
                opcLimits);
            if (contentTypeInspection.Status == PackageInspectionStatus.Failed)
            {
                return contentTypeInspection;
            }

            return BuildRelationshipGraph(
                packageSnapshot,
                contentTypeInspection,
                opcLimits,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or DecoderFallbackException)
        {
            return Failure(
                packageInspection,
                new ContentTypeFinding(
                    ContentTypeFindingCodes.XmlReaderRejected,
                    FindingSeverity.Error,
                    "The verified Content Types entry could not be read from the package.",
                    ContentTypesEntryName));
        }
    }

    private static byte[] ReadStableSnapshot(
        Stream packageStream,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        packageStream.Position = 0;
        byte[] snapshot = new byte[expectedLength];
        int offset = 0;
        while (offset < snapshot.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = packageStream.Read(snapshot, offset, snapshot.Length - offset);
            if (read == 0)
            {
                throw new IOException(
                    "The package stream ended while its immutable snapshot was being captured.");
            }

            offset += read;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (packageStream.ReadByte() != -1)
        {
            throw new IOException(
                "The package stream length changed while its immutable snapshot was being captured.");
        }

        return snapshot;
    }

    private static byte[] ReadContentTypesEntry(
        Stream packageStream,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        packageStream.Position = 0;
        using ZipArchive archive = new(
            packageStream,
            ZipArchiveMode.Read,
            leaveOpen: true,
            entryNameEncoding: StrictUtf8);
        ZipArchiveEntry entry = archive.Entries.Single(
            candidate => string.Equals(
                candidate.FullName,
                ContentTypesEntryName,
                StringComparison.Ordinal));

        byte[] content = new byte[expectedLength];
        using Stream entryStream = entry.Open();
        int offset = 0;
        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = entryStream.Read(content, offset, content.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    "The Content Types entry ended before its verified length.");
            }

            offset += read;
        }

        if (entryStream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "The Content Types entry exceeded its verified length.");
        }

        return content;
    }

    private static OpcInspectionResult BuildPartInventory(
        PackageInspectionResult packageInspection,
        ContentTypeManifest manifest,
        OpcInspectionLimits limits)
    {
        List<ContentTypeFinding> findings = [];
        List<(string EntryName, OpcPartName PartName)> logicalParts = [];
        HashSet<OpcPartName> logicalPartNames = [];

        foreach (PackageEntryInfo entry in packageInspection.Entries
            .Where(entry => !entry.IsDirectory
                && !string.Equals(
                    entry.Name,
                    ContentTypesEntryName,
                    StringComparison.Ordinal))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            if (!OpcPartName.TryFromPackageItem(entry.Name, out OpcPartName? partName))
            {
                findings.Add(new ContentTypeFinding(
                    ContentTypeFindingCodes.InvalidPhysicalPartName,
                    FindingSeverity.Error,
                    "The physical ZIP item cannot be mapped to a valid logical OPC part name.",
                    entry.Name));
                continue;
            }

            int segmentCount = partName.Value.Count(character => character == '/');
            if (segmentCount > limits.MaxPartNameSegments)
            {
                findings.Add(new ContentTypeFinding(
                    ContentTypeFindingCodes.ResourceLimitExceeded,
                    FindingSeverity.Error,
                    $"The part name has {segmentCount} segments; the configured limit is {limits.MaxPartNameSegments}.",
                    entry.Name,
                    partName.Value));
                continue;
            }

            if (!logicalPartNames.Add(partName))
            {
                findings.Add(new ContentTypeFinding(
                    ContentTypeFindingCodes.InvalidPhysicalPartName,
                    FindingSeverity.Error,
                    "More than one physical ZIP item maps to the same logical OPC part name.",
                    entry.Name,
                    partName.Value));
                continue;
            }

            logicalParts.Add((entry.Name, partName));
        }

        if (findings.Any(finding => finding.Severity == FindingSeverity.Error))
        {
            return new OpcInspectionResult(packageInspection, null, [], findings);
        }

        (string EntryName, OpcPartName PartName)[] directoryOrder = logicalParts
            .OrderBy(
                part => part.PartName.Value,
                AsciiCaseInsensitiveComparer.Instance)
            .ToArray();
        for (int index = 1; index < directoryOrder.Length; index++)
        {
            (string _, OpcPartName possibleParent) = directoryOrder[index - 1];
            (string entryName, OpcPartName partName) = directoryOrder[index];
            if (AsciiCaseInsensitiveComparer.Instance.IsPrefixAtSegmentBoundary(
                possibleParent.Value,
                partName.Value))
            {
                findings.Add(new ContentTypeFinding(
                    ContentTypeFindingCodes.InvalidPhysicalPartName,
                    FindingSeverity.Error,
                    "An OPC part name cannot also act as a directory for another part.",
                    entryName,
                    partName.Value));
            }
        }

        if (findings.Any(finding => finding.Severity == FindingSeverity.Error))
        {
            return new OpcInspectionResult(packageInspection, null, [], findings);
        }

        List<OpcPartInfo> resolvedParts = [];
        HashSet<OpcPartName> usedOverrides = [];

        foreach ((string entryName, OpcPartName partName) in logicalParts
            .OrderBy(part => part.PartName.Value, StringComparer.Ordinal))
        {
            bool hasDeclaredMapping = manifest.TryResolveDeclared(
                partName,
                out ResolvedContentType? declaredContentType);
            if (hasDeclaredMapping
                && declaredContentType is not null
                && declaredContentType.Source == ContentTypeSource.Override)
            {
                usedOverrides.Add(partName);
            }

            if (partName.IsRelationshipPart())
            {
                if (hasDeclaredMapping
                    && declaredContentType is not null
                    && !AsciiCaseInsensitiveComparer.Instance.Equals(
                        declaredContentType.Value,
                        ContentTypeManifest.RelationshipsContentType))
                {
                    findings.Add(new ContentTypeFinding(
                        ContentTypeFindingCodes.RelationshipContentTypeConflict,
                        FindingSeverity.Error,
                        "A Relationships part has a manifest mapping that conflicts with its fixed OPC content type.",
                        entryName,
                        partName.Value));
                    continue;
                }

                manifest.TryResolve(partName, out ResolvedContentType? relationshipType);
                resolvedParts.Add(new OpcPartInfo(entryName, partName, relationshipType!));
                continue;
            }

            if (!manifest.TryResolve(partName, out ResolvedContentType? resolvedContentType))
            {
                findings.Add(new ContentTypeFinding(
                    ContentTypeFindingCodes.UnmappedPart,
                    FindingSeverity.Error,
                    "The part has neither an Override nor a matching Default content type.",
                    entryName,
                    partName.Value));
                continue;
            }

            resolvedParts.Add(new OpcPartInfo(entryName, partName, resolvedContentType));
        }

        foreach (OverrideContentType mapping in manifest.Overrides
            .Where(mapping => !usedOverrides.Contains(mapping.PartName))
            .OrderBy(mapping => mapping.PartName.Value, StringComparer.Ordinal))
        {
            findings.Add(new ContentTypeFinding(
                ContentTypeFindingCodes.UnusedOverride,
                FindingSeverity.Error,
                "The Override does not target an existing package part.",
                ContentTypesEntryName,
                mapping.PartName.Value));
        }

        if (findings.Any(finding => finding.Severity == FindingSeverity.Error))
        {
            return new OpcInspectionResult(packageInspection, null, [], findings);
        }

        return new OpcInspectionResult(
            packageInspection,
            manifest,
            resolvedParts,
            findings);
    }

    private static OpcInspectionResult BuildRelationshipGraph(
        Stream packageSnapshot,
        OpcInspectionResult contentTypeInspection,
        OpcInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        List<RelationshipFinding> findings = [];
        List<OpcRelationshipSet> relationshipSets = [];
        HashSet<OpcPartName> partNames = contentTypeInspection.Parts
            .Select(part => part.PartName)
            .ToHashSet();
        OpcPartInfo[] relationshipParts = contentTypeInspection.Parts
            .Where(part => part.PartName.IsRelationshipPart())
            .OrderBy(part => part.PartName.Value, StringComparer.Ordinal)
            .ToArray();

        long totalRelationshipXmlBytes = 0;
        foreach (OpcPartInfo relationshipPart in relationshipParts)
        {
            PackageEntryInfo entry = contentTypeInspection.PackageInspection.Entries.Single(
                candidate => string.Equals(
                    candidate.Name,
                    relationshipPart.PackageEntryName,
                    StringComparison.Ordinal));
            if (entry.UncompressedLength > limits.MaxRelationshipPartXmlBytes)
            {
                findings.Add(new RelationshipFinding(
                    RelationshipFindingCodes.ResourceLimitExceeded,
                    FindingSeverity.Error,
                    $"The Relationships part is {entry.UncompressedLength} bytes; the per-part limit is {limits.MaxRelationshipPartXmlBytes} bytes.",
                    relationshipPart.PartName.Value));
                continue;
            }

            totalRelationshipXmlBytes += entry.UncompressedLength;
            if (totalRelationshipXmlBytes > limits.MaxTotalRelationshipXmlBytes)
            {
                findings.Add(new RelationshipFinding(
                    RelationshipFindingCodes.ResourceLimitExceeded,
                    FindingSeverity.Error,
                    $"Relationships XML exceeds the total limit of {limits.MaxTotalRelationshipXmlBytes} bytes.",
                    relationshipPart.PartName.Value));
                break;
            }
        }

        if (findings.Count != 0)
        {
            return RelationshipFailure(contentTypeInspection, findings);
        }

        packageSnapshot.Position = 0;
        try
        {
            using ZipArchive archive = new(
                packageSnapshot,
                ZipArchiveMode.Read,
                leaveOpen: true,
                entryNameEncoding: StrictUtf8);
            int totalRelationships = 0;

            foreach (OpcPartInfo relationshipPart in relationshipParts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!relationshipPart.PartName.TryGetRelationshipSource(
                    out bool isPackageSource,
                    out OpcPartName? sourcePartName))
                {
                    findings.Add(new RelationshipFinding(
                        RelationshipFindingCodes.SourcePartMissing,
                        FindingSeverity.Error,
                        "The Relationships part name cannot be mapped to a relationship source.",
                        relationshipPart.PartName.Value));
                    continue;
                }

                if (!isPackageSource
                    && sourcePartName is not null
                    && sourcePartName.IsRelationshipPart())
                {
                    findings.Add(new RelationshipFinding(
                        RelationshipFindingCodes.RelationshipPartReferenceProhibited,
                        FindingSeverity.Error,
                        "A Relationships part cannot be the source of relationships.",
                        relationshipPart.PartName.Value,
                        sourcePartName.Value));
                    continue;
                }

                if (!isPackageSource
                    && (sourcePartName is null || !partNames.Contains(sourcePartName)))
                {
                    findings.Add(new RelationshipFinding(
                        RelationshipFindingCodes.SourcePartMissing,
                        FindingSeverity.Error,
                        "The source Part for this Relationships part does not exist.",
                        relationshipPart.PartName.Value,
                        sourcePartName?.Value));
                    continue;
                }

                PackageEntryInfo verifiedEntry =
                    contentTypeInspection.PackageInspection.Entries.Single(
                        candidate => string.Equals(
                            candidate.Name,
                            relationshipPart.PackageEntryName,
                            StringComparison.Ordinal));
                ZipArchiveEntry archiveEntry = archive.Entries.Single(
                    candidate => string.Equals(
                        candidate.FullName,
                        relationshipPart.PackageEntryName,
                        StringComparison.Ordinal));
                byte[] xml = ReadVerifiedEntry(
                    archiveEntry,
                    checked((int)verifiedEntry.UncompressedLength),
                    cancellationToken);
                RelationshipParseResult parsed = RelationshipPartParser.Parse(
                    xml,
                    relationshipPart.PartName,
                    sourcePartName,
                    limits,
                    cancellationToken);
                if (parsed.Relationships is null)
                {
                    findings.AddRange(parsed.Findings);
                    continue;
                }

                totalRelationships += parsed.Relationships.Count;
                if (totalRelationships > limits.MaxTotalRelationships)
                {
                    findings.Add(new RelationshipFinding(
                        RelationshipFindingCodes.ResourceLimitExceeded,
                        FindingSeverity.Error,
                        $"The package exceeds the configured limit of {limits.MaxTotalRelationships} relationships.",
                        relationshipPart.PartName.Value,
                        sourcePartName?.Value));
                    continue;
                }

                List<OpcRelationship> resolvedRelationships = [];
                foreach (ParsedRelationship relationship in parsed.Relationships)
                {
                    if (relationship.TargetMode == RelationshipTargetMode.External)
                    {
                        findings.Add(new RelationshipFinding(
                            RelationshipFindingCodes.ExternalRelationshipProhibited,
                            FindingSeverity.Error,
                            "External relationships are outside the controlled V1 profile.",
                            relationshipPart.PartName.Value,
                            sourcePartName?.Value,
                            relationship.Id,
                            relationship.Target));
                        continue;
                    }

                    if (!RelationshipTargetResolver.TryResolve(
                        sourcePartName,
                        relationship.Target,
                        out OpcPartName? targetPartName))
                    {
                        findings.Add(new RelationshipFinding(
                            RelationshipFindingCodes.InvalidInternalTarget,
                            FindingSeverity.Error,
                            "The internal relationship target is not a valid OPC Part reference.",
                            relationshipPart.PartName.Value,
                            sourcePartName?.Value,
                            relationship.Id,
                            relationship.Target));
                        continue;
                    }

                    if (targetPartName.IsRelationshipPart())
                    {
                        findings.Add(new RelationshipFinding(
                            RelationshipFindingCodes.RelationshipPartReferenceProhibited,
                            FindingSeverity.Error,
                            "A relationship cannot target a Relationships part.",
                            relationshipPart.PartName.Value,
                            sourcePartName?.Value,
                            relationship.Id,
                            relationship.Target));
                        continue;
                    }

                    if (!partNames.Contains(targetPartName))
                    {
                        findings.Add(new RelationshipFinding(
                            RelationshipFindingCodes.TargetPartMissing,
                            FindingSeverity.Error,
                            $"The internal target '{targetPartName.Value}' does not exist.",
                            relationshipPart.PartName.Value,
                            sourcePartName?.Value,
                            relationship.Id,
                            relationship.Target));
                        continue;
                    }

                    resolvedRelationships.Add(new OpcRelationship(
                        relationship.Id,
                        relationship.Type,
                        relationship.Target,
                        relationship.TargetMode,
                        targetPartName));
                }

                relationshipSets.Add(new OpcRelationshipSet(
                    relationshipPart.PartName,
                    sourcePartName,
                    resolvedRelationships));
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or DecoderFallbackException
                or InvalidOperationException)
        {
            findings.Add(new RelationshipFinding(
                RelationshipFindingCodes.XmlReaderRejected,
                FindingSeverity.Error,
                "A verified Relationships entry could not be read from the package.",
                "/_rels/.rels"));
        }

        if (findings.Count != 0)
        {
            return RelationshipFailure(contentTypeInspection, findings);
        }

        return new OpcInspectionResult(
            contentTypeInspection.PackageInspection,
            contentTypeInspection.ContentTypes,
            contentTypeInspection.Parts,
            contentTypeInspection.ContentTypeFindings,
            new RelationshipGraph(relationshipSets),
            []);
    }

    private static byte[] ReadVerifiedEntry(
        ZipArchiveEntry entry,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        byte[] content = new byte[expectedLength];
        using Stream entryStream = entry.Open();
        int offset = 0;
        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = entryStream.Read(content, offset, content.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    "A package entry ended before its verified length.");
            }

            offset += read;
        }

        if (entryStream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "A package entry exceeded its verified length.");
        }

        return content;
    }

    private static OpcInspectionResult RelationshipFailure(
        OpcInspectionResult contentTypeInspection,
        IEnumerable<RelationshipFinding> findings) =>
        new(
            contentTypeInspection.PackageInspection,
            contentTypeInspection.ContentTypes,
            contentTypeInspection.Parts,
            contentTypeInspection.ContentTypeFindings,
            null,
            findings);

    private static OpcInspectionResult Failure(
        PackageInspectionResult packageInspection,
        ContentTypeFinding finding) =>
        new(packageInspection, null, [], [finding]);
}
