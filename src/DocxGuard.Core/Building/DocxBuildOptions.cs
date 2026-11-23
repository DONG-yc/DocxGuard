using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Building;

public sealed record DocxBuildOptions
{
    public PackageLimits Package { get; init; } = PackageLimits.Default;
    public OpcInspectionLimits Opc { get; init; } = OpcInspectionLimits.Default;
    public WordprocessingInspectionLimits Word { get; init; } = WordprocessingInspectionLimits.Default;
    public DocumentIndexLimits Index { get; init; } = DocumentIndexLimits.Default;
    public TextPatchExecutionLimits Execution { get; init; } = TextPatchExecutionLimits.Default;
    public DocxPackageRebuildLimits Rebuild { get; init; } = DocxPackageRebuildLimits.Default;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Package);
        ArgumentNullException.ThrowIfNull(Opc);
        ArgumentNullException.ThrowIfNull(Word);
        ArgumentNullException.ThrowIfNull(Index);
        ArgumentNullException.ThrowIfNull(Execution);
        ArgumentNullException.ThrowIfNull(Rebuild);
        Package.Validate();
        Opc.Validate();
        Word.Validate();
        Index.Validate();
        Execution.Validate();
        Rebuild.Validate();
    }
}
