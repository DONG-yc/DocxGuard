using System.Collections.ObjectModel;

namespace DocxGuard.Core.Packaging;

public enum RelationshipTargetMode
{
    Internal,
    External,
}

public sealed record OpcRelationship(
    string Id,
    string Type,
    string Target,
    RelationshipTargetMode TargetMode,
    OpcPartName TargetPartName);

public sealed class OpcRelationshipSet
{
    internal OpcRelationshipSet(
        OpcPartName relationshipsPartName,
        OpcPartName? sourcePartName,
        IEnumerable<OpcRelationship> relationships)
    {
        RelationshipsPartName = relationshipsPartName;
        SourcePartName = sourcePartName;
        Relationships = new ReadOnlyCollection<OpcRelationship>(
            relationships.ToArray());
    }

    public OpcPartName RelationshipsPartName { get; }

    /// <summary>
    /// Null identifies the package itself as the relationship source.
    /// </summary>
    public OpcPartName? SourcePartName { get; }

    public IReadOnlyList<OpcRelationship> Relationships { get; }
}

public sealed class RelationshipGraph
{
    internal RelationshipGraph(IEnumerable<OpcRelationshipSet> relationshipSets)
    {
        RelationshipSets = new ReadOnlyCollection<OpcRelationshipSet>(
            relationshipSets.ToArray());
    }

    public IReadOnlyList<OpcRelationshipSet> RelationshipSets { get; }

    public OpcRelationshipSet? PackageRelationships =>
        RelationshipSets.SingleOrDefault(set => set.SourcePartName is null);

    public bool TryGetRelationships(
        OpcPartName sourcePartName,
        out OpcRelationshipSet? relationshipSet)
    {
        ArgumentNullException.ThrowIfNull(sourcePartName);
        relationshipSet = RelationshipSets.SingleOrDefault(
            set => sourcePartName.Equals(set.SourcePartName));
        return relationshipSet is not null;
    }
}
