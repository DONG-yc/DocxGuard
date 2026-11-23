using System.Buffers.Binary;
using System.Text;

namespace DocxGuard.Core.Packaging;

/// <summary>
/// Reads the small ZIP records needed to establish safe bounds before ZipArchive
/// materializes its central-directory objects.
/// </summary>
internal static class ZipStructureInspector
{
    private const uint LocalHeaderSignature = 0x04034B50u;
    private const uint CentralDirectorySignature = 0x02014B50u;
    private const uint EndOfCentralDirectorySignature = 0x06054B50u;
    private const uint DataDescriptorSignature = 0x08074B50u;
    private const ushort Zip64ExtraFieldIdentifier = 0x0001;
    private const ushort InfoZipUnicodePathExtraFieldIdentifier = 0x7075;
    private const ushort Utf8Flag = 1 << 11;
    private const ushort DataDescriptorFlag = 1 << 3;
    private const int LocalHeaderLength = 30;
    private const int CentralDirectoryHeaderLength = 46;
    private const int EndOfCentralDirectoryLength = 22;
    private const int MaximumZipCommentLength = ushort.MaxValue;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static ZipDirectoryInfo Inspect(
        Stream stream,
        PackageLimits limits,
        CancellationToken cancellationToken)
    {
        EndOfCentralDirectory endRecord = FindEndOfCentralDirectory(stream);
        ValidateEndRecord(endRecord, stream.Length, limits);

        List<RawZipEntry> entries = ReadCentralDirectory(
            stream,
            endRecord,
            limits,
            cancellationToken);

        ValidateLocalRecords(stream, entries, endRecord.CentralDirectoryOffset, cancellationToken);
        return new ZipDirectoryInfo(entries);
    }

    private static EndOfCentralDirectory FindEndOfCentralDirectory(Stream stream)
    {
        if (stream.Length < EndOfCentralDirectoryLength)
        {
            throw InvalidZip("The archive is too short to contain an end-of-central-directory record.");
        }

        int searchLength = (int)Math.Min(
            stream.Length,
            EndOfCentralDirectoryLength + MaximumZipCommentLength);
        long searchOffset = stream.Length - searchLength;
        byte[] tail = new byte[searchLength];
        ReadExactlyAt(stream, searchOffset, tail);

        for (int index = tail.Length - EndOfCentralDirectoryLength; index >= 0; index--)
        {
            ReadOnlySpan<byte> candidate = tail.AsSpan(index);
            if (ReadUInt32(candidate) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            ushort commentLength = ReadUInt16(candidate[20..]);
            if (index + EndOfCentralDirectoryLength + commentLength != tail.Length)
            {
                continue;
            }

            return new EndOfCentralDirectory(
                Offset: searchOffset + index,
                DiskNumber: ReadUInt16(candidate[4..]),
                CentralDirectoryDiskNumber: ReadUInt16(candidate[6..]),
                EntryCountOnDisk: ReadUInt16(candidate[8..]),
                EntryCount: ReadUInt16(candidate[10..]),
                CentralDirectorySize: ReadUInt32(candidate[12..]),
                CentralDirectoryOffset: ReadUInt32(candidate[16..]));
        }

        throw InvalidZip("A terminal end-of-central-directory record was not found.");
    }

    private static void ValidateEndRecord(
        EndOfCentralDirectory endRecord,
        long archiveLength,
        PackageLimits limits)
    {
        if (endRecord.DiskNumber != 0
            || endRecord.CentralDirectoryDiskNumber != 0
            || endRecord.EntryCountOnDisk != endRecord.EntryCount)
        {
            throw Unsupported("Multi-disk ZIP archives are outside the V1 package profile.");
        }

        if (endRecord.EntryCount == ushort.MaxValue
            || endRecord.CentralDirectorySize == uint.MaxValue
            || endRecord.CentralDirectoryOffset == uint.MaxValue)
        {
            throw Unsupported("ZIP64 archives are outside the V1 package profile.");
        }

        if (endRecord.EntryCount > limits.MaxEntryCount)
        {
            throw new PackagePreflightException(
                PackageFindingCodes.EntryCountExceeded,
                $"The package declares {endRecord.EntryCount} entries; the configured limit is {limits.MaxEntryCount}.");
        }

        long centralDirectoryEnd =
            (long)endRecord.CentralDirectoryOffset + endRecord.CentralDirectorySize;
        if (centralDirectoryEnd != endRecord.Offset || endRecord.Offset > archiveLength)
        {
            throw InvalidZip("The central-directory bounds do not match the terminal ZIP record.");
        }

        if (endRecord.EntryCount > endRecord.CentralDirectorySize / CentralDirectoryHeaderLength)
        {
            throw InvalidZip("The central directory is too small for its declared entry count.");
        }
    }

    private static List<RawZipEntry> ReadCentralDirectory(
        Stream stream,
        EndOfCentralDirectory endRecord,
        PackageLimits limits,
        CancellationToken cancellationToken)
    {
        List<RawZipEntry> entries = new(endRecord.EntryCount);
        long cursor = endRecord.CentralDirectoryOffset;
        long centralDirectoryEnd = cursor + endRecord.CentralDirectorySize;
        byte[] header = new byte[CentralDirectoryHeaderLength];

        for (int index = 0; index < endRecord.EntryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRange(cursor, CentralDirectoryHeaderLength, centralDirectoryEnd, "central-directory header");
            ReadExactlyAt(stream, cursor, header);
            ReadOnlySpan<byte> fields = header;

            if (ReadUInt32(fields) != CentralDirectorySignature)
            {
                throw InvalidZip("A central-directory entry has an invalid signature.");
            }

            ushort flags = ReadUInt16(fields[8..]);
            ushort compressionMethod = ReadUInt16(fields[10..]);
            uint crc32 = ReadUInt32(fields[16..]);
            uint compressedLength = ReadUInt32(fields[20..]);
            uint uncompressedLength = ReadUInt32(fields[24..]);
            ushort nameLength = ReadUInt16(fields[28..]);
            ushort extraLength = ReadUInt16(fields[30..]);
            ushort commentLength = ReadUInt16(fields[32..]);
            ushort diskStart = ReadUInt16(fields[34..]);
            uint localHeaderOffset = ReadUInt32(fields[42..]);

            if (compressedLength == uint.MaxValue
                || uncompressedLength == uint.MaxValue
                || localHeaderOffset == uint.MaxValue)
            {
                throw Unsupported("ZIP64 entry metadata is outside the V1 package profile.");
            }

            if (diskStart != 0)
            {
                throw Unsupported("Multi-disk ZIP entries are outside the V1 package profile.");
            }

            long recordLength = CentralDirectoryHeaderLength
                + (long)nameLength
                + extraLength
                + commentLength;
            EnsureRange(cursor, recordLength, centralDirectoryEnd, "central-directory entry");

            if (nameLength > limits.MaxEntryNameCharacters * 4L)
            {
                throw new PackagePreflightException(
                    PackageFindingCodes.InvalidEntryPath,
                    $"An entry name exceeds the configured limit of {limits.MaxEntryNameCharacters} characters.");
            }

            byte[] rawName = new byte[nameLength];
            ReadExactlyAt(stream, cursor + CentralDirectoryHeaderLength, rawName);
            string name = DecodeEntryName(rawName, flags);
            if (name.Length > limits.MaxEntryNameCharacters)
            {
                throw new PackagePreflightException(
                    PackageFindingCodes.InvalidEntryPath,
                    $"The entry name exceeds the configured limit of {limits.MaxEntryNameCharacters} characters.");
            }

            byte[] extra = new byte[extraLength];
            ReadExactlyAt(
                stream,
                cursor + CentralDirectoryHeaderLength + nameLength,
                extra);
            ValidateExtraFields(extra);

            entries.Add(new RawZipEntry(
                name,
                rawName,
                flags,
                compressionMethod,
                crc32,
                compressedLength,
                uncompressedLength,
                localHeaderOffset));

            cursor += recordLength;
        }

        if (cursor != centralDirectoryEnd)
        {
            throw Unsupported("Additional central-directory records are outside the V1 package profile.");
        }

        return entries;
    }

    private static void ValidateLocalRecords(
        Stream stream,
        IReadOnlyCollection<RawZipEntry> entries,
        long centralDirectoryOffset,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[LocalHeaderLength];

        foreach (RawZipEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRange(entry.LocalHeaderOffset, LocalHeaderLength, centralDirectoryOffset, "local header");
            ReadExactlyAt(stream, entry.LocalHeaderOffset, header);
            ReadOnlySpan<byte> fields = header;

            if (ReadUInt32(fields) != LocalHeaderSignature)
            {
                throw HeaderMismatch("A central-directory entry does not point to a local ZIP header.");
            }

            ushort localFlags = ReadUInt16(fields[6..]);
            ushort localCompressionMethod = ReadUInt16(fields[8..]);
            uint localCrc32 = ReadUInt32(fields[14..]);
            uint localCompressedLength = ReadUInt32(fields[18..]);
            uint localUncompressedLength = ReadUInt32(fields[22..]);
            ushort localNameLength = ReadUInt16(fields[26..]);
            ushort localExtraLength = ReadUInt16(fields[28..]);

            if (localFlags != entry.Flags || localCompressionMethod != entry.CompressionMethod)
            {
                throw HeaderMismatch("Local and central ZIP flags or compression methods do not match.");
            }

            long variableHeaderLength = (long)localNameLength + localExtraLength;
            EnsureRange(
                entry.LocalHeaderOffset + LocalHeaderLength,
                variableHeaderLength,
                centralDirectoryOffset,
                "local entry name and extra fields");

            byte[] localRawName = new byte[localNameLength];
            ReadExactlyAt(stream, entry.LocalHeaderOffset + LocalHeaderLength, localRawName);
            if (!localRawName.AsSpan().SequenceEqual(entry.RawName))
            {
                throw HeaderMismatch("Local and central ZIP entry names do not match byte-for-byte.");
            }

            byte[] localExtra = new byte[localExtraLength];
            ReadExactlyAt(
                stream,
                entry.LocalHeaderOffset + LocalHeaderLength + localNameLength,
                localExtra);
            ValidateExtraFields(localExtra);

            long dataOffset = entry.LocalHeaderOffset
                + LocalHeaderLength
                + variableHeaderLength;
            long dataEnd = dataOffset + entry.CompressedLength;
            EnsureRange(dataOffset, entry.CompressedLength, centralDirectoryOffset, "compressed entry data");
            entry.DataOffset = dataOffset;

            if ((entry.Flags & DataDescriptorFlag) == 0)
            {
                if (localCrc32 != entry.Crc32
                    || localCompressedLength != entry.CompressedLength
                    || localUncompressedLength != entry.UncompressedLength)
                {
                    throw HeaderMismatch("Local and central ZIP CRC or size metadata do not match.");
                }

                entry.RecordEnd = dataEnd;
                continue;
            }

            if ((localCrc32 != 0 && localCrc32 != entry.Crc32)
                || (localCompressedLength != 0 && localCompressedLength != entry.CompressedLength)
                || (localUncompressedLength != 0 && localUncompressedLength != entry.UncompressedLength))
            {
                throw HeaderMismatch("A local data-descriptor placeholder conflicts with central metadata.");
            }

            entry.RecordEnd = ValidateDataDescriptor(stream, entry, dataEnd, centralDirectoryOffset);
        }

        RawZipEntry[] physicalOrder = entries
            .OrderBy(entry => entry.LocalHeaderOffset)
            .ToArray();

        if (physicalOrder.Length == 0)
        {
            if (centralDirectoryOffset != 0)
            {
                throw HeaderMismatch("An empty archive contains data before its central directory.");
            }

            return;
        }

        if (physicalOrder[0].LocalHeaderOffset != 0)
        {
            throw Unsupported("Prefixed or self-extracting ZIP archives are outside the V1 package profile.");
        }

        for (int index = 0; index < physicalOrder.Length - 1; index++)
        {
            if (physicalOrder[index].RecordEnd != physicalOrder[index + 1].LocalHeaderOffset)
            {
                throw HeaderMismatch("Local ZIP records overlap or contain unaccounted gaps.");
            }
        }

        if (physicalOrder[^1].RecordEnd != centralDirectoryOffset)
        {
            throw HeaderMismatch("The final local ZIP record does not end at the central directory.");
        }
    }

    private static long ValidateDataDescriptor(
        Stream stream,
        RawZipEntry entry,
        long descriptorOffset,
        long centralDirectoryOffset)
    {
        byte[] firstFields = new byte[12];
        EnsureRange(descriptorOffset, firstFields.Length, centralDirectoryOffset, "ZIP data descriptor");
        ReadExactlyAt(stream, descriptorOffset, firstFields);

        uint firstValue = ReadUInt32(firstFields);
        uint secondValue = ReadUInt32(firstFields.AsSpan(4));
        uint thirdValue = ReadUInt32(firstFields.AsSpan(8));

        // The signature is optional and has the same shape as a possible CRC value.
        // Prefer the unsigned interpretation when all three fields match.
        if (firstValue == entry.Crc32
            && secondValue == entry.CompressedLength
            && thirdValue == entry.UncompressedLength)
        {
            return descriptorOffset + firstFields.Length;
        }

        if (firstValue == DataDescriptorSignature)
        {
            byte[] signedDescriptor = new byte[16];
            EnsureRange(descriptorOffset, signedDescriptor.Length, centralDirectoryOffset, "ZIP data descriptor");
            ReadExactlyAt(stream, descriptorOffset, signedDescriptor);

            if (ReadUInt32(signedDescriptor.AsSpan(4)) == entry.Crc32
                && ReadUInt32(signedDescriptor.AsSpan(8)) == entry.CompressedLength
                && ReadUInt32(signedDescriptor.AsSpan(12)) == entry.UncompressedLength)
            {
                return descriptorOffset + signedDescriptor.Length;
            }
        }

        throw HeaderMismatch("A ZIP data descriptor does not match its central-directory entry.");
    }

    private static string DecodeEntryName(byte[] rawName, ushort flags)
    {
        try
        {
            // OPC part names are UTF-8. V1 also requires UTF-8 for legacy-flagged ZIP names
            // so decoding is deterministic and never replaces invalid bytes silently.
            return StrictUtf8.GetString(rawName);
        }
        catch (DecoderFallbackException exception)
        {
            string flagDescription = (flags & Utf8Flag) == 0
                ? "without the UTF-8 flag"
                : "with the UTF-8 flag";
            throw new PackagePreflightException(
                PackageFindingCodes.InvalidEntryEncoding,
                $"An entry name contains invalid UTF-8 bytes {flagDescription}.",
                exception);
        }
    }

    private static void ValidateExtraFields(ReadOnlySpan<byte> extraFields)
    {
        int offset = 0;
        while (offset < extraFields.Length)
        {
            if (extraFields.Length - offset < 4)
            {
                throw InvalidZip("A ZIP extra-field header is truncated.");
            }

            ushort identifier = ReadUInt16(extraFields[offset..]);
            ushort dataLength = ReadUInt16(extraFields[(offset + 2)..]);
            if (dataLength > extraFields.Length - offset - 4)
            {
                throw InvalidZip("A ZIP extra field extends beyond its containing record.");
            }

            if (identifier == Zip64ExtraFieldIdentifier)
            {
                throw Unsupported("ZIP64 extra fields are outside the V1 package profile.");
            }

            if (identifier == InfoZipUnicodePathExtraFieldIdentifier)
            {
                throw Unsupported(
                    "Alternate Unicode path extra fields are outside the single-name V1 package profile.");
            }

            offset += 4 + dataLength;
        }
    }

    private static void EnsureRange(long offset, long length, long upperBound, string description)
    {
        if (offset < 0 || length < 0 || offset > upperBound || length > upperBound - offset)
        {
            throw InvalidZip($"The {description} extends outside its allowed ZIP region.");
        }
    }

    private static void ReadExactlyAt(Stream stream, long offset, Span<byte> destination)
    {
        stream.Position = offset;
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int bytesRead = stream.Read(destination[totalRead..]);
            if (bytesRead == 0)
            {
                throw InvalidZip("The ZIP stream ended before a declared record was complete.");
            }

            totalRead += bytesRead;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes);

    private static PackagePreflightException InvalidZip(string message) =>
        new(PackageFindingCodes.InvalidZip, message);

    private static PackagePreflightException Unsupported(string message) =>
        new(PackageFindingCodes.UnsupportedZipFeature, message);

    private static PackagePreflightException HeaderMismatch(string message) =>
        new(PackageFindingCodes.HeaderMismatch, message);

    private sealed record EndOfCentralDirectory(
        long Offset,
        ushort DiskNumber,
        ushort CentralDirectoryDiskNumber,
        ushort EntryCountOnDisk,
        ushort EntryCount,
        uint CentralDirectorySize,
        uint CentralDirectoryOffset);
}

internal sealed class ZipDirectoryInfo(IEnumerable<RawZipEntry> entries)
{
    public IReadOnlyList<RawZipEntry> Entries { get; } = entries.ToArray();
}

internal sealed class RawZipEntry(
    string name,
    byte[] rawName,
    ushort flags,
    ushort compressionMethod,
    uint crc32,
    uint compressedLength,
    uint uncompressedLength,
    uint localHeaderOffset)
{
    public string Name { get; } = name;

    public byte[] RawName { get; } = rawName;

    public ushort Flags { get; } = flags;

    public ushort CompressionMethod { get; } = compressionMethod;

    public uint Crc32 { get; } = crc32;

    public long CompressedLength { get; } = compressedLength;

    public long UncompressedLength { get; } = uncompressedLength;

    public long LocalHeaderOffset { get; } = localHeaderOffset;

    public bool IsEncrypted => (Flags & 1) != 0;

    public long DataOffset { get; set; }

    public long RecordEnd { get; set; }
}

internal sealed class PackagePreflightException : Exception
{
    public PackagePreflightException(string findingCode, string message)
        : base(message)
    {
        FindingCode = findingCode;
    }

    public PackagePreflightException(
        string findingCode,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        FindingCode = findingCode;
    }

    public string FindingCode { get; }
}
