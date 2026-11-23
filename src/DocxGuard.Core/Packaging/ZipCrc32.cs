namespace DocxGuard.Core.Packaging;

/// <summary>
/// Incremental CRC-32 used by PKZIP (IEEE polynomial, reflected form).
/// CRC detects accidental corruption; it is not an authenticity mechanism.
/// </summary>
internal sealed class ZipCrc32
{
    private const uint ReflectedPolynomial = 0xEDB88320u;
    private static readonly uint[] Table = CreateTable();

    private uint state = uint.MaxValue;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            int tableIndex = (int)((state ^ value) & 0xFFu);
            state = Table[tableIndex] ^ (state >> 8);
        }
    }

    public uint GetCurrentHash() => ~state;

    private static uint[] CreateTable()
    {
        uint[] table = new uint[256];

        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 1
                    ? (value >> 1) ^ ReflectedPolynomial
                    : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
