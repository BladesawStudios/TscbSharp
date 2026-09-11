namespace TscbSharp;

/// <summary>
/// Maps a material archive's stored index onto the layer of the shared material texture array.
/// </summary>
/// <remarks>
/// The mapping is the identity only as far as index 28; past that it shifts, and several
/// indices share a layer - 29 and 30 fall back onto 17 and 18, and 31, 76 and 125 all land on
/// something already used. Reading a stored index as a layer directly gives the wrong material
/// for most of the table.
/// </remarks>
public static class MaterialLayers
{
    /// <summary>The layer meaning "no material here"; the archives use it as a hole marker.</summary>
    public const byte None = 120;

    public static readonly byte[] IndexToLayer =
    [
          0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
         21, 22, 23, 24, 25, 26, 27, 28, 17, 18,  0, 29, 30, 31, 32, 33, 34, 35, 36,
         37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55,
         56, 57, 58, 59,  7, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71,  0, 72,
         73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91,
         92, 93, 94, 95, 96, 97, 98, 99,100,101,102,103,104,105,106,107,108,
        109,110,111,112,113,114,115,116,117,118,119,120
    ];

    /// <summary>The index a layer came from, inverting <see cref="IndexToLayer"/>.</summary>
    /// <remarks>Several indices share a layer, so the last one wins - as it does in the game's own lookup.</remarks>
    public static readonly byte[] LayerToIndex = BuildLayerToIndex();

    private static byte[] BuildLayerToIndex()
    {
        byte[] map = new byte[None + 1];
        for (int i = 0; i < IndexToLayer.Length; i++) map[IndexToLayer[i]] = (byte)i;
        return map;
    }
}
