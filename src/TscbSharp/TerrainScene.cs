using System.Buffers.Binary;
using ZstdSharp;

namespace TscbSharp;

public sealed class TerrainScene
{
    private const int Grid = 260;
    private const int Usable = 256;
    private const int Border = 2;

    private const int Plane = Grid * Grid;

    public readonly record struct Tile(
        float MinX, float MinZ, float Size, string Name, float HeightMin, float HeightMax);

    private sealed class Level
    {
        public required float TileSize { get; init; }
        public required float OriginX { get; init; }
        public required float OriginZ { get; init; }
        public required Dictionary<(int X, int Z), Tile> ByCell { get; init; }
    }

    private readonly List<Level> _levels = [];
    private readonly Dictionary<string, (byte[] A, byte[] B, byte[] Blend)> _materials = [];
    private readonly Dictionary<string, ushort[]> _heights = [];

    private readonly HashSet<string> _opened = [];
    private readonly string _archiveDir;

    public float HeightRange { get; private set; }
    public (float MinX, float MinZ, float MaxX, float MaxZ) Bounds { get; private set; }

    public IReadOnlyList<float> LevelSizes => [.. _levels.Select(l => l.TileSize)];

    public int LevelCount => _levels.Count;

    public List<(float U, float V)> MaterialScales { get; private set; } = [];

    public List<float> MaterialMicro { get; private set; } = [];

    private TerrainScene(string archiveDir) => _archiveDir = archiveDir;

    private static string KeyOf(string tileName)
        => tileName.Length <= 9 ? tileName : tileName[^9..];

    public static TerrainScene? Open(string terrainArcDir, string sceneName)
    {
        string tscb = Path.Combine(terrainArcDir, sceneName + ".tscb");
        if (!File.Exists(tscb)) return null;

        TerrainScene scene = new(Path.Combine(terrainArcDir, sceneName));
        scene.ReadScene(tscb);
        return scene.LevelCount > 0 ? scene : null;
    }

    public static TerrainScene? TryLoad(string sceneName, params string?[] starts)
    {
        foreach (string? start in starts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;

            DirectoryInfo? dir = Directory.Exists(start)
                ? new DirectoryInfo(start)
                : new FileInfo(start).Directory;

            while (dir is not null)
            {
                string arc = Path.Combine(dir.FullName, "TerrainArc");
                if (Open(arc, sceneName) is { } scene) return scene;
                dir = dir.Parent;
            }
        }
        return null;
    }

    private void ReadScene(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 0x80 || d[0] != 'T' || d[1] != 'S' || d[2] != 'C' || d[3] != 'B') return;

        float F32(int o) => BitConverter.ToSingle(d, o);
        int U32(int o) => (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));

        float worldScale = F32(0x10);
        HeightRange = F32(0x14);
        int areaCount = U32(0x38);
        int matCount = U32(0x34);

        int areaArray = 0x78 + U32(0x78);
        int matArray = 0x74 + U32(0x74);

        MaterialScales = new List<(float U, float V)>(matCount);
        MaterialMicro = new List<float>(matCount);
        for (int i = 0; i < matCount; i++)
        {
            int field = matArray + i * 4;
            if (field + 4 > d.Length) break;
            int info = field + U32(field);
            if (info < 0 || info + 0x40 > d.Length)
            {
                MaterialScales.Add((1f, 1f));
                MaterialMicro.Add(0f);
                continue;
            }

            MaterialScales.Add((F32(info + 0x10), F32(info + 0x14)));
            MaterialMicro.Add(F32(info + 0x20));
        }

        List<(float X, float Z, float Scale, string Name, float HMin, float HMax)> areas = new(areaCount);
        for (int i = 0; i < areaCount; i++)
        {
            int field = areaArray + i * 4;
            if (field + 4 > d.Length) break;
            int area = field + U32(field);
            if (area < 0 || area + 0x64 > d.Length) continue;

            int nameField = area + 0x60;
            int nameAt = nameField + U32(nameField);
            if (nameAt < 0 || nameAt >= d.Length) continue;
            int end = Array.IndexOf(d, (byte)0, nameAt);
            if (end < 0) continue;

            float hMin = 0f, hMax = 1f;
            int fileCount = U32(area + 0x20);
            int fileArray = area + 0x64 + U32(area + 0x64);
            for (int f = 0; f < fileCount; f++)
            {
                int fp = fileArray + f * 4;
                if (fp + 4 > d.Length) break;
                int file = fp + U32(fp);
                if (file < 0 || file + 0x28 > d.Length) continue;
                if (U32(file) != 0) continue;
                hMin = F32(file + 0x20);
                hMax = F32(file + 0x24);
                break;
            }

            areas.Add((F32(area), F32(area + 4), F32(area + 8),
                       System.Text.Encoding.ASCII.GetString(d, nameAt, end - nameAt), hMin, hMax));
        }
        if (areas.Count == 0) return;

        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

        foreach (float scale in areas.Select(a => a.Scale).Distinct().OrderByDescending(s => s))
        {
            Dictionary<(int, int), Tile> byCell = [];
            float size = scale * worldScale;
            float ox = float.MaxValue, oz = float.MaxValue;

            List<Tile> tiles = [];
            foreach (var a in areas)
            {
                if (MathF.Abs(a.Scale - scale) > 1e-6f) continue;
                Tile t = new(a.X * worldScale - size * 0.5f, a.Z * worldScale - size * 0.5f, size,
                             a.Name, a.HMin, a.HMax);
                tiles.Add(t);
                ox = MathF.Min(ox, t.MinX);
                oz = MathF.Min(oz, t.MinZ);
            }
            if (tiles.Count == 0) continue;

            foreach (Tile t in tiles)
            {
                byCell[((int)MathF.Round((t.MinX - ox) / size), (int)MathF.Round((t.MinZ - oz) / size))] = t;
                minX = MathF.Min(minX, t.MinX);
                minZ = MathF.Min(minZ, t.MinZ);
                maxX = MathF.Max(maxX, t.MinX + size);
                maxZ = MathF.Max(maxZ, t.MinZ + size);
            }

            _levels.Add(new Level { TileSize = size, OriginX = ox, OriginZ = oz, ByCell = byCell });
        }

        Bounds = (minX, minZ, maxX, maxZ);
    }

    public List<Tile> TilesIn(int level, float minX, float minZ, float maxX, float maxZ)
    {
        List<Tile> hit = [];
        if (level < 0 || level >= _levels.Count) return hit;

        Level l = _levels[level];
        int x0 = (int)MathF.Floor((minX - l.OriginX) / l.TileSize);
        int x1 = (int)MathF.Floor((maxX - l.OriginX) / l.TileSize);
        int z0 = (int)MathF.Floor((minZ - l.OriginZ) / l.TileSize);
        int z1 = (int)MathF.Floor((maxZ - l.OriginZ) / l.TileSize);

        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                if (l.ByCell.TryGetValue((x, z), out Tile t)) hit.Add(t);

        return hit;
    }

    public List<Tile> TilesForRegion(int maxLevel, float minX, float minZ, float maxX, float maxZ)
    {
        List<Tile> tiles = [];
        maxLevel = Math.Clamp(maxLevel, 0, _levels.Count - 1);

        for (int level = maxLevel; level >= 0; level--)
            foreach (Tile t in TilesIn(level, minX, minZ, maxX, maxZ))
                if (HeightsOf(t) is not null) tiles.Add(t);

        return tiles;
    }

    private (int Level, int X, int Z) _lastCell = (-1, 0, 0);
    private Tile _lastTile;

    public Tile? CoarseTileWithHeights(int maxLevel, float x, float z)
    {
        for (int l = Math.Min(maxLevel, _levels.Count - 1); l >= 0; l--)
            if (TryTileAt(l, x, z, out Tile t) && HeightsOf(t) is not null) return t;
        return null;
    }

    private bool TryTileAt(int level, float x, float z, out Tile tile)
    {
        tile = default;
        if (level < 0 || level >= _levels.Count) return false;

        Level l = _levels[level];
        int cx = (int)MathF.Floor((x - l.OriginX) / l.TileSize);
        int cz = (int)MathF.Floor((z - l.OriginZ) / l.TileSize);

        if (_lastCell == (level, cx, cz)) { tile = _lastTile; return true; }

        if (!l.ByCell.TryGetValue((cx, cz), out tile)) return false;
        _lastCell = (level, cx, cz);
        _lastTile = tile;
        return true;
    }

    public Tile? HeightSourceAt(int level, float x, float z)
    {
        for (int l = Math.Min(level, _levels.Count - 1); l >= 0; l--)
            if (TryTileAt(l, x, z, out Tile t) && HeightsOf(t) is not null) return t;
        return null;
    }

    public float HeightFrom(Tile source, float x, float z)
        => HeightsOf(source) is { } h ? ToWorld(source, h[TexelOf(source, x, z)]) : 0f;

    public (int LayerA, int LayerB, float Blend)? SampleMaterial(int maxLevel, float x, float z)
    {
        for (int level = Math.Clamp(maxLevel, 0, _levels.Count - 1); level >= 0; level--)
        {
            if (!TryTileAt(level, x, z, out Tile tile)) continue;

            string key = KeyOf(tile.Name);
            if (!_materials.TryGetValue(key, out var m))
            {
                if (!Load(tile.Name, "mate") || !_materials.TryGetValue(key, out m)) continue;
            }

            int o = TexelOf(tile, x, z);

            if (m.A[o] == MaterialLayers.None && m.B[o] == MaterialLayers.None) continue;

            return (m.A[o], m.B[o], m.Blend[o] / 255f);
        }
        return null;
    }

    public (int LayerA, int LayerB, float Blend)? SampleMaterial(float x, float z)
        => SampleMaterial(_levels.Count - 1, x, z);

    public (byte[] A, byte[] B, byte[] Blend)? MaterialsOf(Tile tile)
    {
        string key = KeyOf(tile.Name);
        if (_materials.TryGetValue(key, out var m)) return m;
        return Load(tile.Name, "mate") && _materials.TryGetValue(key, out m) ? m : null;
    }

    public ushort[]? HeightsOf(Tile tile)
    {
        string key = KeyOf(tile.Name);
        if (_heights.TryGetValue(key, out ushort[]? h)) return h;
        return Load(tile.Name, "hght") && _heights.TryGetValue(key, out h) ? h : null;
    }

    public float HeightAt(Tile tile, ushort[] heights, int x, int z)
    {
        int o = (Math.Clamp(z, -Border, Usable + 1) + Border) * Grid
              + Math.Clamp(x, -Border, Usable + 1) + Border;
        return ToWorld(tile, heights[o]);
    }

    private float ToWorld(Tile tile, ushort raw) => raw / 65535f * HeightRange;

    public float? HeightAtWorld(int level, float x, float z)
    {
        for (int l = Math.Min(level, _levels.Count - 1); l >= 0; l--)
        {
            if (!TryTileAt(l, x, z, out Tile tile)) continue;
            if (HeightsOf(tile) is not ushort[] h) continue;
            return ToWorld(tile, h[TexelOf(tile, x, z)]);
        }
        return null;
    }

    public bool Covers(int level, float x, float z) => HeightAtWorld(level, x, z) is not null;

    private static int TexelOf(Tile tile, float x, float z)
    {
        int tx = Math.Clamp((int)((x - tile.MinX) / tile.Size * Usable), 0, Usable - 1);
        int tz = Math.Clamp((int)((z - tile.MinZ) / tile.Size * Usable), 0, Usable - 1);
        return (tz + Border) * Grid + tx + Border;
    }

    private bool Load(string tileName, string kind)
    {
        if (tileName.Length < 9) return false;
        ReadOnlySpan<char> id = KeyOf(tileName).AsSpan();

        char level = id[0];
        if (!int.TryParse(id[1..], System.Globalization.NumberStyles.HexNumber,
                          null, out int index)) return false;

        string archive = Path.Combine(_archiveDir, $"{level}{index & ~3:X8}.{kind}.ta.zs");
        if (!_opened.Add(archive)) return false;
        if (!File.Exists(archive)) return false;

        byte[] raw;
        try
        {
            using Decompressor dec = new();
            raw = dec.Unwrap(File.ReadAllBytes(archive)).ToArray();
        }
        catch
        {
            return false;
        }

        foreach (var (name, bytes) in Sarc.Read(raw))
        {
            int dot = name.IndexOf('.');
            string key = KeyOf(dot > 0 ? name[..dot] : name);

            if (kind == "mate")
            {
                if (bytes.Length == Plane * 4) _materials[key] = DecodeMaterials(bytes);
            }
            else if (bytes.Length >= Plane * 2)
            {
                _heights[key] = DecodeHeights(bytes);
            }
        }

        string want = KeyOf(tileName);
        return kind == "mate" ? _materials.ContainsKey(want) : _heights.ContainsKey(want);
    }

    private static (byte[] A, byte[] B, byte[] Blend) DecodeMaterials(byte[] d)
    {
        byte[] a = new byte[Plane], b = new byte[Plane], bl = new byte[Plane];
        for (int y = 0; y < Grid; y++)
        {
            int ra = 0, rb = 0, rbl = 0, row = y * Grid;
            for (int x = 0; x < Grid; x++)
            {
                ra = (ra + d[row + x]) & 0xFF;
                rb = (rb + d[row + x + Plane]) & 0xFF;
                rbl = (rbl + d[row + x + Plane * 2]) & 0xFF;
                a[row + x] = MaterialLayers.IndexToLayer[Math.Min(ra, MaterialLayers.IndexToLayer.Length - 1)];
                b[row + x] = MaterialLayers.IndexToLayer[Math.Min(rb, MaterialLayers.IndexToLayer.Length - 1)];
                bl[row + x] = (byte)rbl;
            }
        }
        return (a, b, bl);
    }

    private static ushort[] DecodeHeights(byte[] d)
    {
        ushort[] h = new ushort[Plane];
        int lod0 = 0, lod1 = Plane / 2, lod2 = Plane, o = 0;

        for (int y = 0; y < Grid; y++)
        {
            short cur = 0;
            for (int x = 0; x < Grid; x++)
            {
                int shift = (x & 1) != 0 ? 0 : 4;
                int v0 = ((d[lod0] >> shift) & 0xF) - 8;
                int v1 = ((d[lod1] >> shift) & 0xF) - 7;
                int v2 = (sbyte)d[lod2++];
                cur += (short)(v2 * 0x100 + v1 * 0x10 + v0);
                h[o++] = (ushort)cur;
                if ((x & 1) != 0) { lod0++; lod1++; }
            }
        }
        return h;
    }

    public byte[]? BuildMaterialMap(float minX, float minZ, float maxX, float maxZ,
                                    int maxSide, out int width, out int height,
                                    float spacing = 1f)
    {
        spacing = MathF.Max(spacing, 0.01f);
        width = Math.Clamp((int)MathF.Ceiling((maxX - minX) / spacing), 1, maxSide);
        height = Math.Clamp((int)MathF.Ceiling((maxZ - minZ) / spacing), 1, maxSide);

        byte[] map = new byte[width * height * 3];
        float sx = (maxX - minX) / width, sz = (maxZ - minZ) / height;
        bool any = false;

        for (int y = 0; y < height; y++)
        {
            float wz = minZ + (y + 0.5f) * sz;
            for (int x = 0; x < width; x++)
            {
                if (SampleMaterial(minX + (x + 0.5f) * sx, wz) is not (int a, int b, float blend)) continue;
                int o = (y * width + x) * 3;
                map[o] = (byte)a;
                map[o + 1] = (byte)b;
                map[o + 2] = (byte)Math.Clamp(blend * 255f, 0f, 255f);
                any = true;
            }
        }
        return any ? map : null;
    }
}
