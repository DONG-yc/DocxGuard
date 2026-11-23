using DocxGuard.Core;

namespace DocxGuard.Tests;

public sealed class DocumentFormatPolicyTests
{
    [Theory]
    [InlineData("template.docx")]
    [InlineData("TEMPLATE.DOCX")]
    [InlineData("D:\\documents\\contract.DocX")]
    public void IsSupportedInput_AcceptsDocxPaths(string path)
    {
        Assert.True(DocumentFormatPolicy.IsSupportedInput(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("legacy.doc")]
    [InlineData("macro.docm")]
    [InlineData("slides.pptx")]
    [InlineData("document")]
    public void IsSupportedInput_RejectsInputsOutsideV1(string? path)
    {
        Assert.False(DocumentFormatPolicy.IsSupportedInput(path));
    }
}

