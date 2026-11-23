using System.Text.Json;
using System.Text.Json.Serialization;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Planning;

public sealed record BoundPlanFinding(string Code, string Message);

public sealed class BoundPlanResult
{
    internal BoundPlanResult(BoundPatchPlan? plan, TextPatchPlanningResult? planning,
        params BoundPlanFinding[] findings)
    {
        Plan = plan;
        Planning = planning;
        Findings = Array.AsReadOnly(findings.ToArray());
    }

    public BoundPatchPlan? Plan { get; }
    public TextPatchPlanningResult? Planning { get; }
    public IReadOnlyList<BoundPlanFinding> Findings { get; }
    public bool Passed => Plan is not null;
}

/// <summary>Reviewable source-bound intent. This is not a signature or proof of approval.</summary>
public sealed class BoundPatchPlan
{
    public const int MaxSerializedBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private BoundPatchPlan(DocxIndexInspectionResult source, TextReplacementRequest request, PatchPlan plan)
    {
        SourcePackageSha256 = source.SourcePackageSha256!;
        MainDocumentPartName = source.DocumentInspection.MainDocument!.Part.PartName.Value;
        Conformance = source.DocumentInspection.MainDocument.Conformance.ToString();
        Request = request;
        Plan = plan;
    }

    public string SchemaVersion => "1.0";
    public string Profile => "docxguard.bound-plan.v1";
    public string SourcePackageSha256 { get; }
    public string MainDocumentPartName { get; }
    public string Conformance { get; }
    public TextReplacementRequest Request { get; }
    public PatchPlan Plan { get; }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static BoundPlanResult Create(DocxIndexInspectionResult source, TextReplacementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Status != PackageInspectionStatus.Passed)
            return Reject("BPLAN_001", "A passed document inspection is required.");
        TextPatchPlanningResult planning = new TextPatchPlanner().CreatePlan(source.Index!, request,
            cancellationToken: cancellationToken);
        if (planning.Plan is null) return new(null, planning);
        BoundPatchPlan bound = new(source, request, planning.Plan);
        if (JsonSerializer.SerializeToUtf8Bytes(bound, Options).Length > MaxSerializedBytes)
            return Reject("BPLAN_002", "Serialized plan exceeds the 4 MiB limit.");
        return new(bound, planning);
    }

    public static BoundPlanResult Parse(ReadOnlyMemory<byte> json, DocxIndexInspectionResult source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (json.Length > MaxSerializedBytes)
            return Reject("BPLAN_002", "Serialized plan exceeds the 4 MiB limit.");
        if (source.Status != PackageInspectionStatus.Passed)
            return Reject("BPLAN_001", "A passed document inspection is required.");
        if (json.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement, cancellationToken);
            JsonElement root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetString() != "1.0"
                || root.GetProperty("profile").GetString() != "docxguard.bound-plan.v1")
                return Reject("BPLAN_003", "Unsupported plan schema version or profile.");
            if (root.GetProperty("sourcePackageSha256").GetString() != source.SourcePackageSha256)
                return Reject("BPLAN_004", "The plan is bound to different source DOCX bytes. Generate a new plan.");
            JsonElement request = root.GetProperty("request");
            string? expected = request.GetProperty("expectedText").GetString();
            string? replacement = request.GetProperty("replacementText").GetString();
            string? scope = request.GetProperty("paragraphScopeId").GetString();
            if (expected is null || replacement is null)
                return Reject("BPLAN_005", "Plan text fields cannot be null.");
            BoundPlanResult regenerated = Create(source, new(expected, replacement, scope), cancellationToken);
            if (!regenerated.Passed)
                return new(null, regenerated.Planning,
                    new BoundPlanFinding("BPLAN_005", "The serialized request cannot produce a unique supported plan."));
            using JsonDocument canonical = JsonDocument.Parse(regenerated.Plan!.ToJson());
            if (!JsonElement.DeepEquals(root, canonical.RootElement))
                return Reject("BPLAN_005", "Plan fields differ from the source-derived plan, or contain unknown fields.");
            return regenerated;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException)
        {
            return Reject("BPLAN_006", "Invalid plan JSON: required fields, types, UTF-8 and unique property names must be valid.");
        }
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

    private static BoundPlanResult Reject(string code, string message) => new(null, null, new BoundPlanFinding(code, message));
}
