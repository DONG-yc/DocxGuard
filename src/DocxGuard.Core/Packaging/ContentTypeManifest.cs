using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DocxGuard.Core.Packaging;

public enum ContentTypeSource
{
    Override,
    Default,
    RelationshipFixed,
}

public sealed record DefaultContentType(string Extension, string ContentType);

public sealed record OverrideContentType(OpcPartName PartName, string ContentType);

public sealed record ResolvedContentType(string Value, ContentTypeSource Source);

/// <summary>
/// An immutable, validated Content Types manifest. Its internal indexes use
/// OPC's ASCII-only case-insensitive comparison rules.
/// </summary>
public sealed class ContentTypeManifest
{
    public const string RelationshipsContentType =
        "application/vnd.openxmlformats-package.relationships+xml";

    private readonly IReadOnlyDictionary<string, string> defaultsByExtension;
    private readonly IReadOnlyDictionary<OpcPartName, string> overridesByPart;

    internal ContentTypeManifest(
        IEnumerable<DefaultContentType> defaults,
        IEnumerable<OverrideContentType> overrides)
    {
        DefaultContentType[] defaultArray = defaults.ToArray();
        OverrideContentType[] overrideArray = overrides.ToArray();

        Defaults = new ReadOnlyCollection<DefaultContentType>(defaultArray);
        Overrides = new ReadOnlyCollection<OverrideContentType>(overrideArray);

        Dictionary<string, string> defaultIndex = new(
            AsciiCaseInsensitiveComparer.Instance);
        foreach (DefaultContentType mapping in defaultArray)
        {
            defaultIndex.Add(mapping.Extension, mapping.ContentType);
        }

        Dictionary<OpcPartName, string> overrideIndex = [];
        foreach (OverrideContentType mapping in overrideArray)
        {
            overrideIndex.Add(mapping.PartName, mapping.ContentType);
        }

        defaultsByExtension = new ReadOnlyDictionary<string, string>(defaultIndex);
        overridesByPart = new ReadOnlyDictionary<OpcPartName, string>(overrideIndex);
    }

    public IReadOnlyList<DefaultContentType> Defaults { get; }

    public IReadOnlyList<OverrideContentType> Overrides { get; }

    public bool TryResolve(
        OpcPartName partName,
        [NotNullWhen(true)] out ResolvedContentType? contentType)
    {
        ArgumentNullException.ThrowIfNull(partName);

        if (partName.IsRelationshipPart())
        {
            contentType = new ResolvedContentType(
                RelationshipsContentType,
                ContentTypeSource.RelationshipFixed);
            return true;
        }

        if (TryResolveDeclared(partName, out contentType))
        {
            return true;
        }

        contentType = null;
        return false;
    }

    internal bool TryResolveDeclared(
        OpcPartName partName,
        [NotNullWhen(true)] out ResolvedContentType? contentType)
    {
        if (overridesByPart.TryGetValue(partName, out string? overrideValue))
        {
            contentType = new ResolvedContentType(
                overrideValue,
                ContentTypeSource.Override);
            return true;
        }

        string? extension = partName.GetExtension();
        if (extension is not null
            && defaultsByExtension.TryGetValue(extension, out string? defaultValue))
        {
            contentType = new ResolvedContentType(
                defaultValue,
                ContentTypeSource.Default);
            return true;
        }

        contentType = null;
        return false;
    }
}
