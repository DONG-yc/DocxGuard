using System.Xml;

namespace DocxGuard.Core.Packaging;

internal static class OpcXmlSecurity
{
    public static XmlReaderSettings CreateReaderSettings(OpcInspectionLimits limits) =>
        CreateReaderSettings(limits.MaxXmlCharacters);

    public static XmlReaderSettings CreateReaderSettings(long maxXmlCharacters) => new()
    {
        CheckCharacters = true,
        CloseInput = false,
        ConformanceLevel = ConformanceLevel.Document,
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = false,
        IgnoreProcessingInstructions = false,
        IgnoreWhitespace = false,
        MaxCharactersFromEntities = maxXmlCharacters,
        MaxCharactersInDocument = maxXmlCharacters,
        ValidationType = ValidationType.None,
        XmlResolver = null,
    };

    public static bool IsSupportedEncodingName(string value) =>
        value.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
        || value.Equals("utf8", StringComparison.OrdinalIgnoreCase)
        || value.Equals("utf-16", StringComparison.OrdinalIgnoreCase)
        || value.Equals("utf-16le", StringComparison.OrdinalIgnoreCase)
        || value.Equals("utf-16be", StringComparison.OrdinalIgnoreCase);

    public static bool HasSupportedEncodingSignature(ReadOnlySpan<byte> xml)
    {
        if (xml.Length < 4)
        {
            return true;
        }

        bool utf32BigEndian = xml[..4].SequenceEqual(
            new byte[] { 0x00, 0x00, 0xFE, 0xFF })
            || xml[..4].SequenceEqual(new byte[] { 0x00, 0x00, 0x00, 0x3C });
        bool utf32LittleEndian = xml[..4].SequenceEqual(
            new byte[] { 0xFF, 0xFE, 0x00, 0x00 })
            || xml[..4].SequenceEqual(new byte[] { 0x3C, 0x00, 0x00, 0x00 });
        bool ebcdic = xml[..4].SequenceEqual(
            new byte[] { 0x4C, 0x6F, 0xA7, 0x94 });
        return !utf32BigEndian && !utf32LittleEndian && !ebcdic;
    }
}
