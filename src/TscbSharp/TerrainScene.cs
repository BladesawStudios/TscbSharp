using System.Buffers.Binary;
using ZstdSharp;

namespace TscbSharp;

/// <summary>
/// A terrain scene under <c>TerrainArc</c>: a quadtree of tiles, each holding a height grid
/// and the two materials blended across it.
/// </summary>
/// <remarks>
/// This is how the game stores the surface, and it is also where the Depths' materials come
/// from - the quad mesh under <c>Cave/cave017</c> has the better geometry but its per-vertex
/// blend weights are in none of its pages, so the archives supply what it cannot.
/// </remarks>
public sealed class TerrainScene
{
    private const int Grid = 260;      // 256 usable samples inside a 2-texel border
    private const int Usable = 256;
    private const int Border = 2;

    private const int Plane = Grid * Grid;

    /// <summary>One tile: the world square it covers and the name its files carry.</summary>
    public readonly record struct Tile(
        float MinX, float MinZ, float Size, string Name, float HeightMin, float HeightMax);

    /// <summary>
    /// The tiles of one quadtree level. They sit on a regular grid, so they are indexed by
    /// cell - a scene has thousands and a map covers millions of samples, which a linear scan
    /// turns into minutes of loading.
    /// </summary>
    private sealed class Level
    {
        public required float TileSize { get; init; }
        public required float OriginX { get; init; }
        public required float OriginZ { get; init; }
        public required Dictionary<(int X, int Z), Tile> ByCell { get; init; }
    }

    private readonly List<Level> _levels = [];      // coarsest first
    private readonly Dictionary<string, (byte[] A, byte[] B, byte[] Blend)> _materials = [];
    private readonly Dictionary<string, ushort[]> _heights = [];

    // Archives already opened. A tile listed in the scene file is not always inside the
    // archive its name points at, and without this every miss re-reads and re-decompresses
    // that archive - once per sample, which turns a load into a hang.
    private readonly HashSet<string> _opened = [];
    private readonly string _archiveDir;

    /// <summary>The scene's full vertical range in metres; a tile uses a band of it.</summary>
    public float HeightRange { get; private set; }
    public (float MinX, float MinZ, float MaxX, float MaxZ) Bounds { get; private set; }

    /// <summary>Tile edge length in metres for each level, coarsest first.</summary>
    public IReadOnlyList<float> LevelSizes => [.. _levels.Select(l => l.TileSize)];

    public int LevelCount => _levels.Count;

    /// <summary>
    /// Per-material UV scales from the scene, indexed by material-info index rather than by
    /// layer - <see cref="MaterialLayers.LayerToIndex"/> converts.
    /// </summary>
    public List<(float U, float V)> MaterialScales { get; private set; } = [];

    /// <summary>
    /// Per material, how strongly the scene asks for its fine detail layer - zero for the
    /// twenty-two that want none. Indexed like <see cref="MaterialScales"/>.
    /// </summary>
    public List<float> MaterialMicro { get; private set; } = [];

    private TerrainScene(string archiveDir) => _archiveDir = archiveDir;

    /// <summary>
    /// A tile's identity: the last nine characters of its name, a level digit and an eight
    /// digit hex index.
    /// </summary>
    /// <remarks>
    /// The character in front of those is a prefix that is not part of the identity - it
    /// differs between the scene file and the archives, and it differs between scenes. Keying
    /// on the whole name leaves tiles unmatched even when their data is right there in the
    /// archive that was just opened. The level digit is not the quadtree level either: a
    /// level-4 area of StartIsland holds tiles whose digit is 3.
    /// </remarks>
    private static string KeyOf(string tileName)
        => tileName.Length <= 9 ? tileName : tileName[^9..];

    /// <summary>Opens a scene by name, given the directory holding the .tscb.</summary>
    public static TerrainScene? Open(string terrainArcDir, string sceneName)
    {
        string tscb = Path.Combine(terrainArcDir, sceneName + ".tscb");
        if (!File.Exists(tscb)) return null;

        TerrainScene scene = new(Path.Combine(terrainArcDir, sceneName));
        scene.ReadScene(tscb);
        return scene.LevelCount > 0 ? scene : null;
    }

    /// <summary>
    /// Finds a scene by walking up from a path to a romfs root holding TerrainArc. Cave
    /// resources are usually a decompressed working copy well outside romfs, so several
    /// starting points can be given.
    /// </summary>
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

        // Relative offsets are measured from the position of the field that holds them.
        int areaArray = 0x78 + U32(0x78);
        int matArray = 0x74 + U32(0x74);

        // Each entry is 0x40 bytes: the texture it draws, then how many times that texture
        // repeats across the scene in each axis. Without these every material tiles the same,
        // which is right for none of them.
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

            // Each area lists its files, and the height map's entry carries the band of the
            // scene's vertical range that its 16-bit samples span. Tiles do not share one
            // scale: the root covers 0..1 while a child may cover only 0..0.8, so applying a
            // single scale to all of them steps neighbouring tiles apart by hundreds of metres.
            float hMin = 0f, hMax = 1f;
            int fileCount = U32(area + 0x20);
            int fileArray = area + 0x64 + U32(area + 0x64);
            for (int f = 0; f < fileCount; f++)
            {
                int fp = fileArray + f * 4;
                if (fp + 4 > d.Length) break;
                int file = fp + U32(fp);
                if (file < 0 || file + 0x28 > d.Length) continue;
                if (U32(file) != 0) continue;              // 0 is the height map
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

    /// <summary>Every tile of a level that overlaps a world rectangle.</summary>
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

    /// <summary>
    /// The tiles that carry data for a region, finest first, no finer than
    /// <paramref name="maxLevel"/>.
    /// </summary>
    /// <remarks>
    /// The quadtree is sparse: MainField lists 3,689 tiles at level 7 where the level could
    /// hold 16,384, because ground needing no more detail stops at a coarser level. Drawing a
    /// single level therefore covers only the parts subdivided that far and leaves the rest as
    /// holes. Nor is subdivision uniform - a tile's detail can sit two levels finer, so
    /// checking only its immediate children marks it a leaf and draws it over its own
    /// descendants. Callers take these in order and skip whatever a finer tile already covers.
    /// </remarks>
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

    /// <summary>
    /// The finest tile at or below <paramref name="maxLevel"/> covering a position that has
    /// heights of its own. Used to reach a coarser, smoother version of the same ground.
    /// </summary>
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

        // Samples are taken in scan order, so one after another almost always falls in the
        // same tile. Without this every vertex pays a dictionary lookup, and a resource whose
        // tiles inherit their heights pays one per level as well.
        if (_lastCell == (level, cx, cz)) { tile = _lastTile; return true; }

        if (!l.ByCell.TryGetValue((cx, cz), out tile)) return false;
        _lastCell = (level, cx, cz);
        _lastTile = tile;
        return true;
    }

    /// <summary>
    /// The tile that supplies heights at a position: the finest at or above this level with
    /// data. Resolving it once per tile keeps the inheriting case off the per-vertex path.
    /// </summary>
    public Tile? HeightSourceAt(int level, float x, float z)
    {
        for (int l = Math.Min(level, _levels.Count - 1); l >= 0; l--)
            if (TryTileAt(l, x, z, out Tile t) && HeightsOf(t) is not null) return t;
        return null;
    }

    /// <summary>Reads a height from a known source tile, by world position.</summary>
    public float HeightFrom(Tile source, float x, float z)
        => HeightsOf(source) is { } h ? ToWorld(source, h[TexelOf(source, x, z)]) : 0f;

    /// <summary>
    /// The two materials at a world position and how they blend, from the finest tile at or
    /// above <paramref name="maxLevel"/> that has them.
    /// </summary>
    /// <remarks>
    /// The walk matters as much here as it does for heights. The quadtree is sparse - only
    /// 3,877 of MainField's 16,384 finest positions exist - so looking only at the finest
    /// level finds nothing across most of the map and leaves it all reading as layer 0, which
    /// is grass.
    /// </remarks>
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

            // 120 in both slots is the archives' "nothing here". A coarser level often does
            // describe the same ground - the walls in the far north read (30, 120) at the
            // root and (120, 120) at every level below it - so keep walking rather than
            // taking the gap at face value and punching a straight-edged hole in the map.
            if (m.A[o] == MaterialLayers.None && m.B[o] == MaterialLayers.None) continue;

            return (m.A[o], m.B[o], m.Blend[o] / 255f);
        }
        return null;
    }

    /// <summary>The materials at a world position, searching from the finest level down.</summary>
    public (int LayerA, int LayerB, float Blend)? SampleMaterial(float x, float z)
        => SampleMaterial(_levels.Count - 1, x, z);

    /// <summary>The material grid of a tile, or null when it has none.</summary>
    public (byte[] A, byte[] B, byte[] Blend)? MaterialsOf(Tile tile)
    {
        string key = KeyOf(tile.Name);
        if (_materials.TryGetValue(key, out var m)) return m;
        return Load(tile.Name, "mate") && _materials.TryGetValue(key, out m) ? m : null;
    }

    /// <summary>The height grid of a tile, or null when it has none.</summary>
    public ushort[]? HeightsOf(Tile tile)
    {
        string key = KeyOf(tile.Name);
        if (_heights.TryGetValue(key, out ushort[]? h)) return h;
        return Load(tile.Name, "hght") && _heights.TryGetValue(key, out h) ? h : null;
    }

    /// <summary>Reads one sample of a tile's height grid, including its border.</summary>
    public float HeightAt(Tile tile, ushort[] heights, int x, int z)
    {
        int o = (Math.Clamp(z, -Border, Usable + 1) + Border) * Grid
              + Math.Clamp(x, -Border, Usable + 1) + Border;
        return ToWorld(tile, heights[o]);
    }

    /// <summary>Maps a stored sample into world height.</summary>
    /// <remarks>
    /// One scale for the whole scene, not each tile's own band. Two tiles meeting at an edge
    /// store identical samples there - 10204 on both sides of one seam - so the data means
    /// them to be the same height, which only a shared scale gives. A tile's HeightMin and
    /// HeightMax are the range its samples happen to span, useful for culling, and remapping
    /// through them steps neighbours apart by metres.
    /// </remarks>
    private float ToWorld(Tile tile, ushort raw) => raw / 65535f * HeightRange;

    /// <summary>
    /// The height at a world position, taken from the finest tile at or above
    /// <paramref name="level"/> that has data.
    /// </summary>
    /// <remarks>
    /// Not every area in the quadtree ships its own height tile - StartIsland's finest level
    /// lists 78 and only 21 have one - so an area without data inherits from its parent, as it
    /// does in game. Reading only the requested level would leave those as holes.
    /// </remarks>
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

    /// <summary>True when any tile at or above this level covers the position.</summary>
    public bool Covers(int level, float x, float z) => HeightAtWorld(level, x, z) is not null;

    private static int TexelOf(Tile tile, float x, float z)
    {
        int tx = Math.Clamp((int)((x - tile.MinX) / tile.Size * Usable), 0, Usable - 1);
        int tz = Math.Clamp((int)((z - tile.MinZ) / tile.Size * Usable), 0, Usable - 1);
        return (tz + Border) * Grid + tx + Border;
    }

    /// <summary>
    /// Loads the archive holding a tile. Archives group four tiles and are named for the first
    /// of them, so the index rounds down to a multiple of four. No zstd dictionary is involved.
    /// </summary>
    /// <remarks>
    /// An archive is named by the last nine characters of a tile's name - a level digit and an
    /// eight digit hex index. The character in front of those is a per-scene prefix and is not
    /// part of the name: MainField and MinusField both use '5', but StartIsland's tiles begin
    /// with '0' and '2', and requiring a '5' left every one of its tiles without data.
    /// </remarks>
    private bool Load(string tileName, string kind)
    {
        if (tileName.Length < 9) return false;
        ReadOnlySpan<char> id = KeyOf(tileName).AsSpan();

        char level = id[0];
        if (!int.TryParse(id[1..], System.Globalization.NumberStyles.HexNumber,
                          null, out int index)) return false;

        string archive = Path.Combine(_archiveDir, $"{level}{index & ~3:X8}.{kind}.ta.zs");
        if (!_opened.Add(archive)) return false;      // already tried; the tile is not in it
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

    /// <summary>
    /// Four planes, each delta encoded along its rows: a running sum that wraps at 255.
    /// Indices are clamped before the layer lookup, as the map defines only 126 of them.
    /// </summary>
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

    /// <summary>
    /// Heights come as three planes accumulated along each row: two nibble planes carrying the
    /// low bits and a signed byte plane carrying the high ones.
    ///
    /// The accumulated value is unsigned, spanning the scene's height scale from zero. It has
    /// to wrap through a 16-bit accumulator to get there, so the running sum is kept narrow on
    /// purpose - widening it puts every value above half the scale into the negatives, which
    /// reads as a wrapped cliff at exactly plus or minus half the range.
    /// </summary>
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

    /// <summary>
    /// Bakes the material data over a world rectangle into a texture, one texel per metre: red
    /// is the first layer, green the second, blue the blend between them.
    /// </summary>
    /// <remarks>
    /// The shader has to read this per fragment rather than per vertex. The two layers are a
    /// pair, and neighbouring vertices routinely carry different pairs, so interpolating one
    /// vertex's blend and applying it to another vertex's materials mixes the wrong two
    /// together - the weight for one material ends up driving a different one.
    /// </remarks>
    /// <param name="spacing">
    /// Metres per texel. The archives hold one material sample every 0.244 m at the finest
    /// level, so a texel per metre - what this used to assume - throws away sixteen of every
    /// seventeen of them and quantises every boundary to the metre grid.
    /// </param>
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
