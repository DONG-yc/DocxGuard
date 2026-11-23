namespace DocxGuard.Core;

/// <summary>
/// Defines the document formats admitted by the first supported capability set.
/// This is a product boundary, not a file-content security check.
/// </summary>
public static class DocumentFormatPolicy
{
    public const string SupportedExtension = ".docx";

    public static bool IsSupportedInput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return string.Equals(
            Path.GetExtension(path),
            SupportedExtension,
            StringComparison.OrdinalIgnoreCase);
    }
}

