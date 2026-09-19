# TscbSharp

A C# reader for the terrain scenes under `TerrainArc` in *Tears of the Kingdom* - the `.tscb`
quadtree and the `.hght` and `.mate` archives it indexes.

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


## Build

```bash
dotnet build TscbSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
