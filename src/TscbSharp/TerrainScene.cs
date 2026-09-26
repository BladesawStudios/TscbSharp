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

    /// <summary>The tile the last material sample came from, and its grids. See SampleMaterial.</summary>
    private string? _lastMateName;
    private (byte[] A, byte[] B, byte[] Blend)? _lastMate;
    private readonly Dictionary<string, ushort[]> _heights = [];

    /// <summary>
    /// Per-tile baked lighting, one byte a sample on the same grid as the heights.
    /// </summary>
    private readonly Dictionary<string, byte[]> _bakes = [];

    /// <summary>
    /// Per-tile water, as the game's own scene table describes it.
    /// </summary>
    private readonly Dictionary<string, byte[]> _water = [];

    /// <summary>Each tile's whole water surface as one min and max, raw.</summary>
    private readonly Dictionary<string, (ushort Min, ushort Max)> _waterRange = [];

    /// <summary>Side of the water grid, and the border inside it.</summary>
    public const int WaterGrid = 68;
    public const int WaterBorder = 2;

    private const int WaterStride = 8;
    private const int WaterBytes = WaterGrid * WaterGrid * WaterStride;

    /// <summary>
    /// The bounding pyramid stored after the water grid.
    /// </summary>
    private const int WaterBounding = 4 * (32 * 32 + 16 * 16 + 8 * 8 + 4 * 4 + 2 * 2 + 1);

    private const int WaterHeader = 0;

    private readonly HashSet<string> _opened = [];
    private readonly string _archiveDir;

    public float HeightRange { get; private set; }
    public (float MinX, float MinZ, float MaxX, float MaxZ) Bounds { get; private set; }

    public IReadOnlyList<float> LevelSizes => [.. _levels.Select(l => l.TileSize)];

    public int LevelCount => _levels.Count;

    public List<(float U, float V)> MaterialScales { get; private set; } = [];

    public List<float> MaterialMicro { get; private set; } = [];

    /// <summary>
    /// Maps a file the scene would read to the file to actually read - a mod's copy where it
    /// has one. Paths go in as the stock romfs has them, so the scene never has to know a mod
    /// exists.
    /// </summary>
    private readonly Func<string, string> _resolve;

    private TerrainScene(string archiveDir, Func<string, string>? resolve)
    {
        _archiveDir = archiveDir;
        _resolve = resolve ?? (p => p);
    }

    private static string KeyOf(string tileName)
        => tileName.Length <= 9 ? tileName : tileName[^9..];

    /// <summary>The name a tile's data goes by inside its archive, and in the caches here.</summary>
    public static string EntryKey(string name)
    {
        int dot = name.IndexOf('.');
        return KeyOf(dot > 0 ? name[..dot] : name);
    }

    /// <summary>
    /// The archive holding one kind of a tile's data - <c>mate</c>, <c>hght</c>,
    /// <c>bake.extm</c> - as the stock romfs has it, or null for a name that is not a tile's.
    /// Four sibling tiles share an archive.
    /// </summary>
    public string? ArchivePathOf(Tile tile, string kind)
    {
        if (tile.Name.Length < 9) return null;
        ReadOnlySpan<char> id = KeyOf(tile.Name).AsSpan();
        if (!int.TryParse(id[1..], System.Globalization.NumberStyles.HexNumber, null, out int index))
            return null;

        return Path.Combine(_archiveDir, $"{id[0]}{index & ~3:X8}.{kind}.ta.zs");
    }

    /// <summary>The file a path is read from, after the resolver has had its say.</summary>
    public string Resolve(string path) => _resolve(path);

    /// <summary>The tile of exactly this square, if the scene has one.</summary>
    public Tile? TileAt(float minX, float minZ, float size)
    {
        for (int l = 0; l < _levels.Count; l++)
        {
            if (MathF.Abs(_levels[l].TileSize - size) > size * 1e-4f) continue;

            float half = size * 0.5f;
            if (TryTileAt(l, minX + half, minZ + half, out Tile t)
                && MathF.Abs(t.MinX - minX) < half * 0.01f && MathF.Abs(t.MinZ - minZ) < half * 0.01f)
                return t;
        }
        return null;
    }

    /// <param name="resolve">
    /// Where each file is really read from; see <see cref="_resolve"/>. Null reads the paths
    /// as given.
    /// </param>
    public static TerrainScene? Open(string terrainArcDir, string sceneName, Func<string, string>? resolve = null)
    {
        resolve ??= p => p;
        string tscb = resolve(Path.Combine(terrainArcDir, sceneName + ".tscb"));
        if (!File.Exists(tscb)) return null;

        TerrainScene scene = new(Path.Combine(terrainArcDir, sceneName), resolve);
        scene.ReadScene(tscb);
        return scene.LevelCount > 0 ? scene : null;
    }

    public static TerrainScene? TryLoad(string sceneName, params string?[] starts)
        => TryLoad(sceneName, null, starts);

    public static TerrainScene? TryLoad(string sceneName, Func<string, string>? resolve, params string?[] starts)
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
                if (Open(arc, sceneName, resolve) is { } scene) return scene;
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

    /// <summary>Which level of the quadtree a tile belongs to, by its size.</summary>
    public int LevelOf(Tile tile)
    {
        for (int l = 0; l < _levels.Count; l++)
            if (_levels[l].TileSize == tile.Size) return l;
        return _levels.Count - 1;
    }

    /// <summary>
    /// The finest tile at or above <paramref name="level"/> that carries water.
    /// </summary>
    public Tile? WaterSourceAt(int level, float x, float z)
    {
        for (int l = Math.Min(level, _levels.Count - 1); l >= 0; l--)
            if (TryTileAt(l, x, z, out Tile t) && WaterOf(t) is not null) return t;
        return null;
    }

    /// <summary>
    /// <paramref name="tile"/>'s water grid, with anything it does not describe itself filled
    /// in from its ancestors.
    /// </summary>

    public byte[]? WaterGridFor(Tile tile) => WaterGridFor(tile, null);

    /// <param name="heights">Unused; kept so callers that have them need not drop them.</param>
    /// <remarks>
    /// Only where the tile says nothing itself - no water file, or one without a single wet
    /// texel - and then from the nearest ancestor that does say something, taken whole: its dry
    /// texels are as much an answer as its wet ones.
    ///
    /// It used to be filled texel by texel, climbing past any ancestor that was dry at that
    /// point to the next one up, and blending in whichever of the four source texels around a
    /// point were wet. Between them those spread every lake's surface outward by a texel of
    /// whatever level supplied it - 250 m at the root - so a hillside 16 m above anything any
    /// level calls water came out under a sheet at 349.6 m, and tile-shaped pools of plain
    /// water stood all over the Gerudo dunes. A texel is wet now only where the source texel
    /// nearest it is.
    /// </remarks>
    public byte[]? WaterGridFor(Tile tile, ushort[]? heights)
    {
        byte[]? own = WaterOf(tile);
        if (own is not null && AnyWet(own)) return own;

        float cx = tile.MinX + tile.Size * 0.5f, cz = tile.MinZ + tile.Size * 0.5f;

        for (int l = Math.Min(LevelOf(tile) - 1, _levels.Count - 1); l >= 0; l--)
        {
            if (!TryTileAt(l, cx, cz, out Tile src)) continue;
            if (WaterOf(src) is not { } from || !AnyWet(from)) continue;

            byte[] into = new byte[WaterBytes];
            Fill(into, tile, from, src);
            return into;
        }

        return own;
    }

    /// <summary>Whether a grid holds water anywhere inside its border.</summary>
    private static bool AnyWet(byte[] water)
    {
        for (int z = WaterBorder; z < WaterGrid - WaterBorder; z++)
            for (int x = WaterBorder; x < WaterGrid - WaterBorder; x++)
                if (Wet(water, (z * WaterGrid + x) * WaterStride)) return true;
        return false;
    }

    private static bool Wet(byte[] water, int o)
        => (water[o] | water[o + 1] << 8) >= 2 && (water[o + 6] & 0x80) == 0;

    /// <summary>Resamples <paramref name="src"/>'s grid onto <paramref name="tile"/>'s footprint.</summary>
    private static void Fill(byte[] into, Tile tile, byte[] from, Tile src)
    {
        int usable = WaterGrid - 2 * WaterBorder;
        float step = tile.Size / usable, srcStep = src.Size / usable;

        for (int z = 0; z < WaterGrid; z++)
        {
            float wz = tile.MinZ + (z - WaterBorder) * step;
            float v = (wz - src.MinZ) / srcStep + WaterBorder;

            for (int x = 0; x < WaterGrid; x++)
            {
                float wx = tile.MinX + (x - WaterBorder) * step;
                float u = (wx - src.MinX) / srcStep + WaterBorder;

                Sample(from, u, v, into.AsSpan((z * WaterGrid + x) * WaterStride, WaterStride));
            }
        }
    }

    /// <summary>
    /// The source texel nearest (<paramref name="u"/>, <paramref name="v"/>) decides whether
    /// there is water; where there is, its height and normal are blended across whichever of
    /// the four around it are wet too, so neighbouring tiles agree along their seam.
    /// </summary>
    private static void Sample(byte[] from, float u, float v, Span<byte> texel)
    {
        u = Math.Clamp(u, 0f, WaterGrid - 1.001f);
        v = Math.Clamp(v, 0f, WaterGrid - 1.001f);

        int x0 = (int)u, y0 = (int)v;
        float fx = u - x0, fy = v - y0;

        int nearest = ((y0 + (fy < 0.5f ? 0 : 1)) * WaterGrid + x0 + (fx < 0.5f ? 0 : 1)) * WaterStride;
        if (!Wet(from, nearest))
        {
            from.AsSpan(nearest, WaterStride).CopyTo(texel);
            return;
        }

        double weight = 0, height = 0, a = 0, b = 0;

        for (int c = 0; c < 4; c++)
        {
            int sx = x0 + (c & 1), sy = y0 + (c >> 1);
            int f = (sy * WaterGrid + sx) * WaterStride;
            if (!Wet(from, f)) continue;

            double k = ((c & 1) == 0 ? 1 - fx : fx) * ((c >> 1) == 0 ? 1 - fy : fy);
            if (k <= 0) continue;

            weight += k;
            height += k * (from[f] | from[f + 1] << 8);
            a += k * (from[f + 2] | from[f + 3] << 8);
            b += k * (from[f + 4] | from[f + 5] << 8);
        }

        // The nearest is wet, so it always contributes; this is only for rounding.
        if (weight <= 0)
        {
            from.AsSpan(nearest, WaterStride).CopyTo(texel);
            return;
        }

        Write(texel, 0, (int)Math.Round(height / weight));
        Write(texel, 2, (int)Math.Round(a / weight));
        Write(texel, 4, (int)Math.Round(b / weight));
        Write(texel, 6, from[nearest + 6] | from[nearest + 7] << 8);

        static void Write(Span<byte> into, int at, int value)
        {
            into[at] = (byte)value;
            into[at + 1] = (byte)(value >> 8);
        }
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

            // Neighbouring samples land in the same tile almost every time, and the lookup
            // below cuts a fresh key string out of the tile's name and hashes it for each
            // one - which, over a material map of some sixteen million texels, is most of
            // what building it costs. The tile names come from the level tables and are the
            // same instances every time, so a reference check settles it.
            (byte[] A, byte[] B, byte[] Blend) m;
            if (ReferenceEquals(tile.Name, _lastMateName) && _lastMate is { } memo)
            {
                m = memo;
            }
            else
            {
                string key = KeyOf(tile.Name);
                if (!_materials.TryGetValue(key, out m))
                {
                    if (!Load(tile.Name, "mate") || !_materials.TryGetValue(key, out m)) continue;
                }

                _lastMateName = tile.Name;
                _lastMate = m;
            }

            int o = TexelOf(tile, x, z);

            if (m.A[o] == MaterialLayers.NoMaterial && m.B[o] == MaterialLayers.NoMaterial) continue;

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

    /// <summary>This tile's baked lighting, or null where none ships for it.</summary>
    public byte[]? BakeOf(Tile tile)
    {
        string key = KeyOf(tile.Name);
        if (_bakes.TryGetValue(key, out byte[]? b)) return b;
        return Load(tile.Name, "bake.extm") && _bakes.TryGetValue(key, out b) ? b : null;
    }

    /// <summary>
    /// The lowest and highest the water gets on this tile, in world units, or null where the
    /// tile ships none.
    /// </summary>
    public (float Min, float Max)? WaterRangeOf(Tile tile)
    {
        string key = KeyOf(tile.Name);
        if (!_waterRange.TryGetValue(key, out var r))
        {
            if (!Load(tile.Name, "water.extm") || !_waterRange.TryGetValue(key, out r)) return null;
        }
        return (r.Min / 65535f * HeightRange, r.Max / 65535f * HeightRange);
    }

    /// <summary>This tile's water grid, or null where none ships for it.</summary>
    public byte[]? WaterOf(Tile tile)
    {
        string key = KeyOf(tile.Name);
        if (_water.TryGetValue(key, out byte[]? w)) return w;
        return Load(tile.Name, "water.extm") && _water.TryGetValue(key, out w) ? w : null;
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

        string archive = _resolve(Path.Combine(_archiveDir, $"{level}{index & ~3:X8}.{kind}.ta.zs"));
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
            else if (kind == "bake.extm")
            {
                // One byte a sample, stored flat - no row deltas, unlike the materials.
                if (bytes.Length == Plane) _bakes[key] = bytes;
            }
            else if (kind == "water.extm")
            {
                if (bytes.Length >= WaterHeader + WaterBytes + WaterBounding)
                {
                    _water[key] = bytes[WaterHeader..(WaterHeader + WaterBytes)];

                    // The root of the trailing pyramid: this tile's whole surface in one node.
                    int root = WaterHeader + WaterBytes + WaterBounding - 4;
                    _waterRange[key] = (
                        (ushort)(bytes[root + 2] | bytes[root + 3] << 8),
                        (ushort)(bytes[root] | bytes[root + 1] << 8));
                }
            }
            else if (bytes.Length >= Plane * 2)
            {
                _heights[key] = DecodeHeights(bytes);
            }
        }

        string want = KeyOf(tileName);
        return kind switch
        {
            "mate" => _materials.ContainsKey(want),
            "bake.extm" => _bakes.ContainsKey(want),
            "water.extm" => _water.ContainsKey(want),
            _ => _heights.ContainsKey(want),
        };
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
                // Past the end of the table is ground the archives name no material for, and
                // it is rare - a tenth of a per cent. Clamping it onto the last entry instead,
                // which is what this did, merged it with material 125 and lost both.
                a[row + x] = ra < MaterialLayers.IndexToLayer.Length
                    ? MaterialLayers.IndexToLayer[ra]
                    : MaterialLayers.NoMaterial;
                b[row + x] = rb < MaterialLayers.IndexToLayer.Length
                    ? MaterialLayers.IndexToLayer[rb]
                    : MaterialLayers.NoMaterial;
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
