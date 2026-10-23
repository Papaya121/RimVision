using System;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        private readonly ShorelineMaskRenderer _shorelineMask = new ShorelineMaskRenderer();

        static bool IsWaterTerrain(TerrainDef terrain) => ShorelineMaskRenderer.IsWaterTerrain(terrain);

        void UpdateShorelineMask(Vector2Int? chunk = null)
        {
            _shorelineMask.Update(map, ChunkSize, chunk);
        }

        void BuildShorelineGround(int x0, int z0, int x1, int z1) => _worldGeometry.BuildShorelineGround(x0, z0, x1, z1);

        void DisposeShoreline()
        {
            _shorelineMask.Dispose();
        }

        Material NewTerrainMaterial(TerrainDef terrain) => _worldGeometry.NewTerrainMaterial(terrain);

        sealed partial class WorldGeometry
        {
            bool _lastSimpleWater = Rim3DSettings.Current.SimpleWater;

            void UpdateWaterSettings()
            {
                bool simple = Rim3DSettings.Current.SimpleWater;
                if (_lastSimpleWater == simple) return;
                _lastSimpleWater = simple;
                foreach (var material in _view.terrainMaterials.Values)
                {
                    if (!material.HasProperty("_WaveHeight")) continue;
                    if (simple) material.EnableKeyword("RIM3D_SIMPLE_WATER");
                    else material.DisableKeyword("RIM3D_SIMPLE_WATER");
                }
                foreach (var key in _view.chunkRoots.Keys) _view.dirtyChunks.Add(key);
            }

            void BuildWaterSurface(Geometry geometry, int x0, int z0, int x1, int z1)
            {
                int columns = (x1 - x0) * 2 + 1;
                int rows = (z1 - z0) * 2 + 1;
                int start = geometry.Vertices.Count;
                for (int z = 0; z < rows; z++)
                for (int x = 0; x < columns; x++)
                {
                    var position = new Vector3(x0 + x * .5f, 0, z0 + z * .5f);
                    geometry.Vertices.Add(position);
                    geometry.Normals.Add(Vector3.up);
                    geometry.Uvs.Add(new Vector2(position.x / 8f, position.z / 8f));
                    geometry.CardCenters.Add(Vector3.zero);
                }
                for (int z = 0; z < rows - 1; z++)
                for (int x = 0; x < columns - 1; x++)
                {
                    int index = start + z * columns + x;
                    geometry.Indices.Add(index);
                    geometry.Indices.Add(index + columns);
                    geometry.Indices.Add(index + columns + 1);
                    geometry.Indices.Add(index);
                    geometry.Indices.Add(index + columns + 1);
                    geometry.Indices.Add(index + 1);
                }
            }

            internal void BuildShorelineGround(int x0, int z0, int x1, int z1)
            {
                // Only shore cells need an underlay. Interior water remains merged.
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!_view.DrawSpatialFloor(cell) || _view.map.fogGrid.IsFogged(cell) || !IsWaterTerrain(_view.map.terrainGrid.TerrainAt(cell))) continue;
                    TerrainDef shore = null;
                    int nearest = 3;
                    for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int distance = dx * dx + dz * dz;
                        if (distance == 0 || distance >= nearest) continue;
                        var neighbor = new IntVec3(x + dx, 0, z + dz);
                        if (!neighbor.InBounds(_view.map) || _view.map.fogGrid.IsFogged(neighbor)) continue;
                        var terrain = _view.map.terrainGrid.TerrainAt(neighbor);
                        if (terrain == null || terrain.dontRender || IsWaterTerrain(terrain)) continue;
                        shore = terrain;
                        nearest = distance;
                    }
                    if (shore == null) continue;
                    if (!_view.terrainMaterials.TryGetValue(shore, out var material))
                    {
                        material = _view.NewTerrainMaterial(shore);
                        _view.terrainMaterials.Add(shore, material);
                    }
                    _view.Batch(material, new Vector3(x, 0, z)).Quad(
                        new Vector3(x, -.01f, z), new Vector3(x, -.01f, z + 1),
                        new Vector3(x + 1, -.01f, z + 1), new Vector3(x + 1, -.01f, z), Vector3.up,
                        new Vector2(x / 8f, z / 8f), new Vector2(x / 8f, (z + 1) / 8f),
                        new Vector2((x + 1) / 8f, (z + 1) / 8f), new Vector2((x + 1) / 8f, z / 8f));
                }
            }

            internal Material NewTerrainMaterial(TerrainDef terrain)
            {
                // Marsh has no depth shader and keeps its original ground surface.
                var waterShader = !string.IsNullOrEmpty(terrain.waterDepthShader) ? ModAssets.LoadShader("Rim3D/Water") : null;
                if (waterShader != null && waterShader.isSupported)
                {
                    bool deep = terrain.defName.IndexOf("Deep", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool shallow = terrain.defName.IndexOf("Shallow", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool chestDeep = terrain.defName.IndexOf("ChestDeep", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool river = terrain.tags != null && terrain.tags.Contains("River");
                    var material = new Material(waterShader)
                    {
                        name = "RimVision water " + terrain.defName,
                        hideFlags = HideFlags.DontSave,
                        mainTexture = terrain.DrawMatSingle.mainTexture,
                        color = terrain.DrawColor
                    };
                    material.SetFloat("_Depth", shallow ? .18f : chestDeep ? .58f : deep ? .92f : .4f);
                    material.SetFloat("_FlowSpeed", river ? .65f : .16f);
                    if (Rim3DSettings.Current.SimpleWater) material.EnableKeyword("RIM3D_SIMPLE_WATER");
                    _view.materials.Add(material);
                    return material;
                }
                var ground = _view.NewMaterial(terrain.DrawMatSingle.mainTexture, terrain.DrawColor, true);
                ground.SetFloat("_Ground", 1);
                ground.SetInt("_StencilComp", (int)UnityEngine.Rendering.CompareFunction.Equal);
                ground.SetInt("_StencilReadMask", GroundCutoutStencil);
                ground.SetFloat("_SnowGround", terrain.holdSnowOrSand ? 1 : 0);
                _view.ConfigureGroundPollution(ground, terrain);
                return ground;
            }

        }
    }

    internal sealed class ShorelineMaskRenderer
    {
        private Texture2D _mask;
        private static readonly int _maskId = Shader.PropertyToID("_Rim3DShorelineMask");

        internal static bool IsWaterTerrain(TerrainDef terrain) => terrain != null && !string.IsNullOrEmpty(terrain.waterDepthShader);

        internal void Update(Map map, int chunkSize, Vector2Int? chunk)
        {
            if (_mask == null)
            {
                _mask = new Texture2D(map.Size.x, map.Size.z, TextureFormat.RGBA32, false, true)
                {
                    name = "RimVision shoreline", hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
                chunk = null;
            }
            int x0 = chunk.HasValue ? chunk.Value.x * chunkSize : 0;
            int z0 = chunk.HasValue ? chunk.Value.y * chunkSize : 0;
            int x1 = chunk.HasValue ? Mathf.Min(x0 + chunkSize, map.Size.x) : map.Size.x;
            int z1 = chunk.HasValue ? Mathf.Min(z0 + chunkSize, map.Size.z) : map.Size.z;
            var pixels = new Color32[(x1 - x0) * (z1 - z0)];
            for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++)
            {
                var cell = new IntVec3(x, 0, z);
                byte water = !map.fogGrid.IsFogged(cell) && IsWaterTerrain(map.terrainGrid.TerrainAt(cell)) ? (byte)255 : (byte)0;
                byte pollution = !map.fogGrid.IsFogged(cell) && cell.IsPolluted(map) ? (byte)255 : (byte)0;
                pixels[(z - z0) * (x1 - x0) + x - x0] = new Color32(water, pollution, 0, 255);
            }
            _mask.SetPixels32(x0, z0, x1 - x0, z1 - z0, pixels);
            _mask.Apply(false, false);
            SetShaderGlobals();
        }

        internal void SetShaderGlobals()
        {
            Shader.SetGlobalTexture(_maskId, _mask);
        }

        internal void Dispose()
        {
            if (_mask != null) UnityEngine.Object.Destroy(_mask);
            _mask = null;
            Shader.SetGlobalTexture(_maskId, Texture2D.whiteTexture);
        }
    }
}
