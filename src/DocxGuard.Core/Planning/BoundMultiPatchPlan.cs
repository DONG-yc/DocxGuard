using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Planning;

public static class MultiPlanFindingCodes
{
    public const string Empty = "MPLAN_001";
    public const string TooManyOperations = "MPLAN_002";
    public const string PlanningRejected = "MPLAN_003";
    public const string OverlappingTargets = "MPLAN_004";
    public const string SourceMismatch = "MPLAN_005";
    public const string InvalidSchema = "MPLAN_006";
    public const string InvalidJson = "MPLAN_007";
}

public sealed record MultiPlanFinding(
    string Code,
    string Message,
    int? RequestIndex = null,
    string? ParagraphId = null,
    int? ConflictingRequestIndex = null);

public sealed class MultiPatchPlanResult
{
    internal MultiPatchPlanResult(
        BoundMultiPatchPlan? plan,
        IEnumerable<TextPatchPlanningResult> planning,
        IEnumerable<MultiPlanFinding> findings)
    {
        Plan = plan;
        Planning = new ReadOnlyCollection<TextPatchPlanningResult>(planning.ToArray());
        Findings = new ReadOnlyCollection<MultiPlanFinding>(findings.ToArray());
    }

    public BoundMultiPatchPlan? Plan { get; }
    public IReadOnlyList<TextPatchPlanningResult> Planning { get; }
    public IReadOnlyList<MultiPlanFinding> Findings { get; }
    public bool Passed => Plan is not null;
}

/// <summary>Source-bound, reviewable intent for a batch of text replacements.</summary>
public sealed class BoundMultiPatchPlan
{
    public const int MaxOperations = 64;
    public const string ProfileId = "docxguard.multi-bound-plan.v1";
    public const int MaxSerializedBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private BoundMultiPatchPlan(
        DocxIndexInspectionResult source,
        IReadOnlyList<TextReplacementRequest> requests,
        PatchPlan plan)
    {
        SourcePackageSha256 = source.SourcePackageSha256!;
        MainDocumentPartName = source.DocumentInspection.MainDocument!.Part.PartName.Value;
        Conformance = source.DocumentInspection.MainDocument.Conformance.ToString();
        Requests = new ReadOnlyCollection<TextReplacementRequest>(requests.ToArray());
        Plan = plan;
    }

    public string SchemaVersion => "1.0";
    public string Profile => ProfileId;
    public string SourcePackageSha256 { get; }
    public string MainDocumentPartName { get; }
    public string Conformance { get; }
    public IReadOnlyList<TextReplacementRequest> Requests { get; }
    public PatchPlan Plan { get; }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static MultiPatchPlanResult Create(
        DocxIndexInspectionResult source,
        IReadOnlyList<TextReplacementRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Status != PackageInspectionStatus.Passed)
            return Reject(MultiPlanFindingCodes.PlanningRejected, "A passed document inspection is required.");
        if (requests.Count == 0)
            return Reject(MultiPlanFindingCodes.Empty, "At least one replacement request is required.");
        if (requests.Count > MaxOperations)
            return Reject(MultiPlanFindingCodes.TooManyOperations, $"A multi-plan may contain at most {MaxOperations} requests.");
        if (requests.Any(request => request is null))
            return Reject(MultiPlanFindingCodes.PlanningRejected, "Replacement requests cannot contain null entries.");

        List<TextPatchPlanningResult> planning = [];
        List<(TextPatchOperation Operation, int RequestIndex)> operations = [];
        List<MultiPlanFinding> findings = [];
        TextPatchPlanner planner = new();
        for (int index = 0; index < requests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextReplacementRequest request = requests[index];
            TextPatchPlanningResult result = planner.CreatePlan(source.Index!, request,
                cancellationToken: cancellationToken);
            planning.Add(result);
            if (result.Plan is null)
            {
                findings.AddRange(result.Findings.Select(f => new MultiPlanFinding(
                    f.Code,
                    f.Message,
                    index,
                    f.ParagraphId)));
                findings.AddRange(result.Location.Findings.Select(f => new MultiPlanFinding(
                    f.Code,
                    f.Message,
                    index)));
                if (result.Findings.Count == 0 && result.Location.Findings.Count == 0)
                    findings.Add(new(MultiPlanFindingCodes.PlanningRejected,
                        "The replacement request did not produce a supported operation.", index));
                continue;
            }

            TextPatchOperation operation = result.Plan.Operations.Single();
            operations.Add((new TextPatchOperation(
                $"op{index + 1:000000}",
                operation.SourceMatchId,
                operation.ReplacementText,
                operation.Precondition), index));
        }

        for (int left = 0; left < operations.Count; left++)
        {
            (TextPatchOperation operation, int requestIndex) = operations[left];
            for (int right = left + 1; right < operations.Count; right++)
            {
                (TextPatchOperation other, int otherRequestIndex) = operations[right];
                if (!string.Equals(operation.Precondition.ParagraphId,
                        other.Precondition.ParagraphId, StringComparison.Ordinal))
                    continue;
                if (operation.Precondition.StartOffset >= other.Precondition.EndOffset
                    || other.Precondition.StartOffset >= operation.Precondition.EndOffset)
                    continue;
                findings.Add(new(
                    MultiPlanFindingCodes.OverlappingTargets,
                    $"Requests {requestIndex} and {otherRequestIndex} target overlapping text in paragraph {operation.Precondition.ParagraphId}.",
                    requestIndex,
                    operation.Precondition.ParagraphId,
                    otherRequestIndex));
            }
        }

        if (findings.Count != 0)
            return new(null, planning, findings);

        TextPatchOperation[] ordered = operations
            .OrderByDescending(item => item.Operation.Precondition.ParagraphId, StringComparer.Ordinal)
            .ThenByDescending(item => item.Operation.Precondition.StartOffset)
            .Select(item => item.Operation)
            .ToArray();
        PatchPlan plan = PatchPlan.CreateInternal(CreatePlanId(ordered), ordered);
        BoundMultiPatchPlan bound = new(source, requests, plan);
        if (Encoding.UTF8.GetByteCount(bound.ToJson()) > MaxSerializedBytes)
            return Reject(MultiPlanFindingCodes.TooManyOperations, "Serialized plan exceeds the 4 MiB limit.");
        return new(bound, planning, []);
    }

    public static MultiPatchPlanResult Parse(
        ReadOnlyMemory<byte> json,
        DocxIndexInspectionResult source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (json.Length > MaxSerializedBytes)
            return Reject(MultiPlanFindingCodes.TooManyOperations, "Serialized plan exceeds the 4 MiB limit.");
        if (source.Status != PackageInspectionStatus.Passed)
            return Reject(MultiPlanFindingCodes.PlanningRejected, "A passed document inspection is required.");
        if (json.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
            RejectDuplicateProperties(document.RootElement, cancellationToken);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty("schemaVersion").GetString() != "1.0"
                || root.GetProperty("profile").GetString() != ProfileId)
                return Reject(MultiPlanFindingCodes.InvalidSchema, "Unsupported plan schema version or profile.");
            if (root.GetProperty("sourcePackageSha256").GetString() != source.SourcePackageSha256)
                return Reject(MultiPlanFindingCodes.SourceMismatch, "The plan is bound to different source DOCX bytes. Generate a new plan.");
            List<TextReplacementRequest>? requests = root.GetProperty("requests")
                .Deserialize<List<TextReplacementRequest>>(Options);
            if (requests is null)
                return Reject(MultiPlanFindingCodes.InvalidJson, "The requests array cannot be null.");
            MultiPatchPlanResult regenerated = Create(source, requests, cancellationToken);
            if (!regenerated.Passed)
                return regenerated;
            using JsonDocument canonical = JsonDocument.Parse(regenerated.Plan!.ToJson());
            if (!JsonElement.DeepEquals(root, canonical.RootElement))
                return Reject(MultiPlanFindingCodes.InvalidJson, "Plan fields differ from the source-derived plan, or contain unknown fields.");
            return regenerated;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Reject(MultiPlanFindingCodes.InvalidJson,
                "Invalid plan JSON: required fields, types, UTF-8 and unique property names must be valid.");
        }
    }

    private static string CreatePlanId(IEnumerable<TextPatchOperation> operations)
    {
        StringBuilder material = new();
        foreach (TextPatchOperation operation in operations)
        {
            material.Append(operation.Id).Append('|')
                .Append(operation.Precondition.ParagraphId).Append('|')
                .Append(operation.Precondition.StartOffset).Append('|')
                .Append(operation.Precondition.ExpectedText).Append('|')
                .Append(operation.ReplacementText).Append('|')
                .Append(operation.Precondition.ExpectedParagraphTextSha256).Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return $"multi-plan-{Convert.ToHexString(hash[..12]).ToLowerInvariant()}";
    }

    private static void RejectDuplicateProperties(JsonElement root, CancellationToken cancellationToken)
    {
        Stack<JsonElement> pending = new();
        pending.Push(root);
        while (pending.TryPop(out JsonElement element))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate property.");
                    pending.Push(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) pending.Push(item);
            }
        }
    }

    private static MultiPatchPlanResult Reject(string code, string message) =>
        new(null, [], [new(code, message)]);
}
