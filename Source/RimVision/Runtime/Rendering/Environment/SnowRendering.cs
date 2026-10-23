using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        private readonly GroundSnowRenderer _groundSnow = new GroundSnowRenderer();

        void UpdateGroundSnow()
        {
            _groundSnow.Update(map, ChunkSize);
        }

        void DisposeGroundSnow()
        {
            _groundSnow.Dispose();
        }
    }

    internal sealed class GroundSnowRenderer
    {
        private Texture2D _snowDepthMap;
        private Texture2D _snowSurfaceTexture;
        private readonly HashSet<Vector2Int> _dirtySnowChunks = new HashSet<Vector2Int>();
        private static readonly int _snowDepthId = Shader.PropertyToID("_Rim3DSnowDepth");
        private static readonly int _snowTextureId = Shader.PropertyToID("_Rim3DSnowTexture");

        private static Color SnowDepthAt(Map map, int x, int z)
        {
            var cell = new IntVec3(x, 0, z);
            // Fogged terrain must retain its hidden appearance. Eligibility is
            // also checked on the material so bilinear edges cannot whiten water.
            float depth = map.fogGrid.IsFogged(cell) ? 0 : map.snowGrid.GetDepth(cell);
            var terrain = map.terrainGrid.TerrainAt(cell);
            if (terrain == null || !terrain.holdSnowOrSand) depth = 0;
            return new Color(Mathf.Clamp01(depth), 0, 0, 1);
        }

        internal void MarkDirty(Vector2Int chunk)
        {
            _dirtySnowChunks.Add(chunk);
        }

        internal void SetShaderGlobals()
        {
            Shader.SetGlobalTexture(_snowDepthId, Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.GroundSnow) && _snowDepthMap != null ? _snowDepthMap : Texture2D.blackTexture);
            Shader.SetGlobalTexture(_snowTextureId, _snowSurfaceTexture != null ? _snowSurfaceTexture : Texture2D.whiteTexture);
        }

        internal void Update(Map map, int chunkSize)
        {
            if (!Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.GroundSnow)) return;
            if (_snowDepthMap == null)
            {
                _snowDepthMap = new Texture2D(map.Size.x, map.Size.z, TextureFormat.RGBA32, false, true)
                {
                    name = "RimVision snow depth", hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
                var pixels = new Color32[map.Size.x * map.Size.z];
                for (int z = 0; z < map.Size.z; z++)
                for (int x = 0; x < map.Size.x; x++)
                    pixels[z * map.Size.x + x] = SnowDepthAt(map, x, z);
                _snowDepthMap.SetPixels32(pixels);
                _snowDepthMap.Apply(false, false);
                _snowSurfaceTexture = ModAssets.LoadTexture("textures/other/Snow");
                _dirtySnowChunks.Clear();
                return;
            }
            if (_dirtySnowChunks.Count == 0) return;
            // Snow notifications are intentionally sparse in the game. Refresh
            // the affected chunk to pick up gradual buildup in neighboring cells.
            foreach (var chunk in _dirtySnowChunks)
            {
                int startX = chunk.x * chunkSize, startZ = chunk.y * chunkSize;
                int endX = Mathf.Min(startX + chunkSize, map.Size.x);
                int endZ = Mathf.Min(startZ + chunkSize, map.Size.z);
                for (int z = startZ; z < endZ; z++)
                for (int x = startX; x < endX; x++)
                    _snowDepthMap.SetPixel(x, z, SnowDepthAt(map, x, z));
            }
            _dirtySnowChunks.Clear();
            _snowDepthMap.Apply(false, false);
        }

        internal void Dispose()
        {
            if (_snowDepthMap != null) UnityEngine.Object.Destroy(_snowDepthMap);
            _snowDepthMap = null; _snowSurfaceTexture = null;
            _dirtySnowChunks.Clear();
            Shader.SetGlobalTexture(_snowDepthId, Texture2D.blackTexture);
            Shader.SetGlobalTexture(_snowTextureId, Texture2D.whiteTexture);
        }
    }
}

