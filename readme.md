# TscbSharp

A C# reader for the terrain scenes under `TerrainArc` in *Tears of the Kingdom* — the `.tscb`
quadtree and the `.hght` and `.mate` archives it indexes.

This is the surface: a quadtree of tiles, each holding a 256×256 height grid and the two
materials blended across it. It is also where the Depths' materials come from, since the quad
mesh under `Cave/cave017` has the better geometry but carries no blend weights of its own.

```csharp
TerrainScene scene = TerrainScene.Open(@"romfs\TerrainArc", "MainField")!;

foreach (TerrainScene.Tile tile in scene.TilesForRegion(level: 8, -1000, -1000, 1000, 1000))
{
    if (scene.HeightsOf(tile) is not { } heights) continue;
    float y = scene.HeightAt(tile, heights, x: 128, z: 128);
}

if (scene.SampleMaterial(x: 0f, z: 0f) is (int layerA, int layerB, float blend))
{
    // Layers index the shared material texture array directly.
}
```

The library depends only on `ZstdSharp.Port`.

## What the formats do that is not obvious

**Heights use one scale for the whole scene, not each tile's own band.** A tile's
`mMinHeight`/`mMaxHeight` are the range its samples happen to span — useful for culling —
and remapping through them steps neighbouring tiles apart by hundreds of metres. Two tiles
meeting at an edge store identical samples there, which only a shared scale preserves.

**Both archives are row-delta encoded**: a running sum along each row that wraps at 0xff.
Heights come as three planes — two nibble planes carrying the low bits and a signed byte
plane carrying the high ones — accumulated into an unsigned 16-bit value spanning the
scene's height scale from zero.

**A material's stored index is not its texture layer.** `MaterialLayers.IndexToLayer` is the
identity only as far as index 28; past that it shifts, and several indices alias onto a layer
already in use. Layer 120 means "no material here".

**The scene declares a UV scale per material** in its `mMatInfo` array, and they vary by a
factor of ten — grass tiles every three metres where rock tiles every thirty. Assuming one
scale for all of them draws most ground at the wrong size.

**The quadtree is not proper.** Tiles have one, two or three children as often as none or
four, so a tile cannot be treated as wholly covered or wholly uncovered by its children.

## Build

```bash
dotnet build TscbSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
