using System.Buffers.Binary;

namespace TscbSharp;

/// <summary>
/// The SARC archive the height and material tiles are packed into.
/// </summary>
/// <remarks>
/// Only what a terrain archive needs: the node table, the hash-ordered name table and the
/// file bytes. The containers are zstd compressed without a dictionary, so they decompress
/// with a plain frame read before reaching this.
/// </remarks>
internal static class Sarc
{
    public static List<(string Name, byte[] Bytes)> Read(byte[] d)
    {
        List<(string, byte[])> files = [];
        if (d.Length < 0x20 || d[0] != 'S' || d[1] != 'A' || d[2] != 'R' || d[3] != 'C') return files;

        int U32(int o) => (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));

        int dataOffset = U32(0x0C);
        int sfat = U16(0x04);                     // the SARC header's own length
        int nodeCount = U16(sfat + 0x06);
        int nodes = sfat + 0x0C;
        int names = nodes + nodeCount * 0x10 + 0x08;

        for (int i = 0; i < nodeCount; i++)
        {
            int n = nodes + i * 0x10;
            if (n + 0x10 > d.Length) break;

            int nameOffset = names + (U32(n + 4) & 0xFFFFFF) * 4;
            int start = dataOffset + U32(n + 8);
            int end = dataOffset + U32(n + 12);
            if (nameOffset < 0 || nameOffset >= d.Length) continue;
            if (start < 0 || end > d.Length || end < start) continue;

            int z = Array.IndexOf(d, (byte)0, nameOffset);
            if (z < 0) continue;

            files.Add((System.Text.Encoding.ASCII.GetString(d, nameOffset, z - nameOffset), d[start..end]));
        }
        return files;
    }
}
