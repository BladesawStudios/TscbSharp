using System.Buffers.Binary;
using ZstdSharp;

namespace TscbSharp;

/// <summary>
/// Writes a tile's materials back into its <c>.mate.ta.zs</c> archive.
/// </summary>
/// <remarks>
/// The archive is a zstd frame around a SARC of four tiles, each 270,400 bytes: four planes of
/// the 260 x 260 grid. The first two hold material indices and the third the blend, each row
/// stored as running deltas from zero; the fourth is not decoded and is left exactly as it was.
///
/// Entries are patched where they lie. Every entry is the same size before and after, so the
/// SARC's node and name tables stay valid and no SARC writer is needed, and the decompressed
/// size does not change, so the resource size table needs no edit either. The game reads a
/// plain zstd frame without the dictionary, which is what is written.
///
/// A layer is not an index: several indices name the same layer. A texel whose layer is
/// unchanged keeps the index it had, so rewriting a tile with nothing painted on it gives back
/// the same bytes.
/// </remarks>
public static class MateArchive
{
    public const int Grid = 260;
    private const int Plane = Grid * Grid;
    public const int EntrySize = Plane * 4;

    public static byte[] Decompress(byte[] file)
    {
        using Decompressor dec = new();
        return dec.Unwrap(file).ToArray();
    }

    public static byte[] Compress(byte[] sarc, int level = 16)
    {
        using Compressor c = new(level);
        return c.Wrap(sarc).ToArray();
    }

    /// <summary>Where each tile's entry starts in a decompressed archive, by its key.</summary>
    public static Dictionary<string, int> Entries(byte[] sarc)
    {
        Dictionary<string, int> found = [];
        if (sarc.Length < 0x20 || sarc[0] != 'S' || sarc[1] != 'A' || sarc[2] != 'R' || sarc[3] != 'C')
            return found;

        int U32(int o) => (int)BinaryPrimitives.ReadUInt32LittleEndian(sarc.AsSpan(o));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(sarc.AsSpan(o));

        int dataOffset = U32(0x0C);
        int sfat = U16(0x04);
        int nodeCount = U16(sfat + 0x06);
        int nodes = sfat + 0x0C;
        int names = nodes + nodeCount * 0x10 + 0x08;

        for (int i = 0; i < nodeCount; i++)
        {
            int n = nodes + i * 0x10;
            int nameOffset = names + (U32(n + 4) & 0xFFFFFF) * 4;
            int start = dataOffset + U32(n + 8);
            int end = dataOffset + U32(n + 12);
            if (end - start != EntrySize || end > sarc.Length || nameOffset >= sarc.Length) continue;

            int z = Array.IndexOf(sarc, (byte)0, nameOffset);
            if (z < 0) continue;

            string name = System.Text.Encoding.ASCII.GetString(sarc, nameOffset, z - nameOffset);
            found[TerrainScene.EntryKey(name)] = start;
        }
        return found;
    }

    /// <summary>
    /// Rewrites one entry texel by texel. <paramref name="map"/> is given each texel's
    /// position and its layers and blend as stored, and returns what it should hold.
    /// </summary>
    /// <returns>How many texels changed.</returns>
    public static int Rewrite(
        Span<byte> entry,
        Func<int, int, (byte A, byte B, byte Blend), (byte A, byte B, byte Blend)> map)
    {
        if (entry.Length != EntrySize) throw new ArgumentException("Not a material entry.", nameof(entry));

        int changed = 0;
        byte[] ra = new byte[Grid], rb = new byte[Grid], rbl = new byte[Grid];

        for (int y = 0; y < Grid; y++)
        {
            int row = y * Grid;

            // Undo the row's running deltas into raw values.
            int a = 0, b = 0, bl = 0;
            for (int x = 0; x < Grid; x++)
            {
                a = (a + entry[row + x]) & 0xFF;
                b = (b + entry[row + x + Plane]) & 0xFF;
                bl = (bl + entry[row + x + Plane * 2]) & 0xFF;
                ra[x] = (byte)a; rb[x] = (byte)b; rbl[x] = (byte)bl;
            }

            for (int x = 0; x < Grid; x++)
            {
                var stored = (LayerOf(ra[x]), LayerOf(rb[x]), rbl[x]);
                var want = map(x, y, stored);
                if (want == stored) continue;

                ra[x] = IndexFor(want.A, ra[x]);
                rb[x] = IndexFor(want.B, rb[x]);
                rbl[x] = want.Blend;
                changed++;
            }

            // And back into deltas.
            int pa = 0, pb = 0, pbl = 0;
            for (int x = 0; x < Grid; x++)
            {
                entry[row + x] = (byte)(ra[x] - pa);
                entry[row + x + Plane] = (byte)(rb[x] - pb);
                entry[row + x + Plane * 2] = (byte)(rbl[x] - pbl);
                pa = ra[x]; pb = rb[x]; pbl = rbl[x];
            }
        }

        return changed;
    }

    private static byte LayerOf(byte index) =>
        index < MaterialLayers.IndexToLayer.Length ? MaterialLayers.IndexToLayer[index] : MaterialLayers.NoMaterial;

    /// <summary>The index to store for a layer, keeping the one already there if it names it.</summary>
    private static byte IndexFor(byte layer, byte had)
    {
        if (LayerOf(had) == layer) return had;
        if (layer == MaterialLayers.NoMaterial) return 0xFF;
        return layer <= MaterialLayers.MaxLayer ? MaterialLayers.LayerToIndex[layer] : had;
    }
}
