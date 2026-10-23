using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.Glow;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        private readonly GameLightingRenderer _gameLighting = new GameLightingRenderer();

        void RegisterModelLight(Thing thing, OptionalModelLibrary.EffectAnchor anchor, Vector3 position)
        {
            _gameLighting.RegisterModelLight(thing, anchor, position);
        }

        void UpdateGameLighting()
        {
            _gameLighting.Update(map);
        }
    }

    internal sealed class GameLightingRenderer
    {
        private Texture2D _gameLightMap;
        private Texture2D _modelLightPositions;
        private readonly Dictionary<int, ModelLight> _modelLights = new Dictionary<int, ModelLight>();
        private readonly HashSet<int> _glowingThingIds = new HashSet<int>();
        private bool _modelLightsDirty;

        sealed class ModelLight
        {
            public Thing Thing;
            public OptionalModelLibrary.EffectAnchor Anchor;
            public Vector3 Position;
        }

        internal void RegisterModelLight(Thing thing, OptionalModelLibrary.EffectAnchor anchor, Vector3 position)
        {
            if (_modelLights.TryGetValue(thing.thingIDNumber, out var existing) && existing.Anchor == anchor && existing.Position == position) return;
            _modelLights[thing.thingIDNumber] = new ModelLight { Thing = thing, Anchor = anchor, Position = position };
            _modelLightsDirty = true;
        }
        private readonly HashSet<IntVec3> _dirtyLightCells = new HashSet<IntVec3>();
        private static readonly FieldInfo _lightsField = AccessTools.Field(typeof(GlowGrid), "lights");
        private static readonly FieldInfo _glowPoolField = AccessTools.Field(typeof(GlowGrid), "glowPool");
        private static readonly FieldInfo _poolAreasField = AccessTools.Field(_glowPoolField.FieldType, "pool");
        private readonly List<VisualLightSource> _visualLightSources = new List<VisualLightSource>();
        private readonly List<CellRect> _campfireLightRects = new List<CellRect>();

        internal void RemoveModelLight(int thingId)
        {
            if (_modelLights.Remove(thingId)) _modelLightsDirty = true;
        }

        internal void MarkDirty(IntVec3 cell)
        {
            _dirtyLightCells.Add(cell);
        }

        internal bool HasGlowingThing(int thingId) => _glowingThingIds.Contains(thingId);

        internal void SetShaderGlobals(int lightMapId, int modelLightPositionsId)
        {
            Shader.SetGlobalTexture(lightMapId, _gameLightMap);
            Shader.SetGlobalTexture(modelLightPositionsId, _modelLightPositions);
        }

        struct VisualLightSource
        {
            public GlowLight Light;
            public LocalGlowArea Area;
            public CellRect Rect;
            public bool Campfire;
            public ModelLight Model;
        }

        void RefreshVisualLightSources(Map map)
        {
            _visualLightSources.Clear();
            _glowingThingIds.Clear();
            _campfireLightRects.Clear();
            // Snapshot native source buffers only when uploading lighting changes.
            var lights = (NativeList<GlowLight>)_lightsField.GetValue(map.glowGrid);
            var pool = (NativeList<LocalGlowArea>)_poolAreasField.GetValue(_glowPoolField.GetValue(map.glowGrid));
            if (!lights.IsCreated || !pool.IsCreated) return;
            for (int i = 0; i < lights.Length; i++)
            {
                var light = lights[i];
                int id = unchecked((int)light.id);
                if (!light.isTerrain) _glowingThingIds.Add(id);
                var building = light.isTerrain ? null : light.position.GetEdifice(map);
                bool campfire = building != null && building.thingIDNumber == light.id && building.def.defName == "Campfire";
                var rect = light.AffectedRect;
                _visualLightSources.Add(new VisualLightSource
                {
                    Light = light, Area = pool[light.localGlowPoolIndex], Rect = rect, Campfire = campfire,
                    Model = _modelLights.TryGetValue(id, out var model) && !model.Thing.Destroyed && model.Thing.Spawned && model.Thing.Map == map ? model : null
                });
                if (campfire) _campfireLightRects.Add(rect);
            }
        }

        Color32 VisualGlowAt(Map map, IntVec3 cell)
        {
            if (map.fogGrid.IsFogged(cell) || map.terrainGrid.TerrainAt(cell).exposesToVacuum)
                return new Color32(0, 0, 0, 0);
            bool nearCampfire = false;
            for (int i = 0; i < _campfireLightRects.Count; i++)
                if (_campfireLightRects[i].Contains(cell)) { nearCampfire = true; break; }
            if (!nearCampfire && _modelLights.Count == 0) return map.glowGrid.VisualGlowAt(cell);

            // Recombine actual flood-filled contributions: walls still block light,
            // and overlapping lamps retain their full contribution, even at saturation.
            var result = new ColorInt(0, 0, 0, 0);
            for (int i = 0; i < _visualLightSources.Count; i++)
            {
                var source = _visualLightSources[i];
                if (!source.Rect.Contains(cell)) continue;
                var color = SourceGlowAt(source, cell);
                if (color.r == 0 && color.g == 0 && color.b == 0) continue;
                result += new ColorInt(color.r, color.g, color.b, 0);
                if (color.a < source.Light.overlightRadius) result.a = 1;
                result.ProjectToColor32Fast(out var combined);
                result = combined.AsColorInt();
            }
            result.ProjectToColor32Fast(out var glow);
            return glow;
        }

        Color32 SourceGlowAt(VisualLightSource source, IntVec3 cell)
        {
            var color = source.Area.colors[source.Light.WorldToLocalIndex(in cell)];
            if (source.Campfire)
            {
                color.r = (byte)(color.r / 2); color.g = (byte)(color.g / 2); color.b = (byte)(color.b / 2);
            }
            if (source.Model != null)
            {
                var effect = source.Model.Anchor;
                var delta = cell.ToVector3Shifted() - source.Model.Position;
                float falloff = Mathf.Clamp01(1 - (delta.x * delta.x + delta.z * delta.z) / (effect.Radius * effect.Radius));
                float strength = effect.Intensity * falloff;
                color.r = (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * effect.Color.r * strength), 0, 255);
                color.g = (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * effect.Color.g * strength), 0, 255);
                color.b = (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * effect.Color.b * strength), 0, 255);
            }
            return color;
        }

        Color ModelLightPositionAt(Map map, IntVec3 cell)
        {
            if (_modelLights.Count == 0 || map.fogGrid.IsFogged(cell) || map.terrainGrid.TerrainAt(cell).exposesToVacuum) return Color.clear;
            float total = 0, anchored = 0;
            var position = Vector3.zero;
            foreach (var source in _visualLightSources)
            {
                if (!source.Rect.Contains(cell)) continue;
                var color = SourceGlowAt(source, cell);
                float weight = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
                total += weight;
                if (source.Model == null) continue;
                anchored += weight;
                position += source.Model.Position * weight;
            }
            return total > 0 ? new Color(position.x / total, position.y / total, position.z / total, anchored / total) : Color.clear;
        }

        void InitializeGameLighting(Map map)
        {
            _gameLightMap = new Texture2D(map.Size.x, map.Size.z, TextureFormat.RGBA32, false, true)
            {
                name = "RimVision game lighting", hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            _modelLightPositions = new Texture2D(map.Size.x, map.Size.z, TextureFormat.RGBAFloat, false, true)
            {
                name = "RimVision model light positions", hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            var colors = new Color32[map.Size.x * map.Size.z];
            var positions = new Color[colors.Length];
            RefreshVisualLightSources(map);
            for (int z = 0; z < map.Size.z; z++)
            for (int x = 0; x < map.Size.x; x++)
            {
                var cell = new IntVec3(x, 0, z);
                colors[z * map.Size.x + x] = VisualGlowAt(map, cell);
                positions[z * map.Size.x + x] = ModelLightPositionAt(map, cell);
            }
            _gameLightMap.SetPixels32(colors);
            _gameLightMap.Apply(false, false);
            _modelLightPositions.SetPixels(positions);
            _modelLightPositions.Apply(false, false);
            _modelLightsDirty = false;
        }

        internal void Update(Map map)
        {
            if (_gameLightMap == null) InitializeGameLighting(map);
            if (_dirtyLightCells.Count == 0 && !_modelLightsDirty) return;
            RefreshVisualLightSources(map);
            if (_modelLightsDirty)
            {
                for (int z = 0; z < map.Size.z; z++)
                for (int x = 0; x < map.Size.x; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    _gameLightMap.SetPixel(x, z, VisualGlowAt(map, cell));
                    _modelLightPositions.SetPixel(x, z, ModelLightPositionAt(map, cell));
                }
                _modelLightsDirty = false;
            }
            // The game completes its glow-grid update before the GUI camera renders.
            foreach (var cell in _dirtyLightCells)
            {
                _gameLightMap.SetPixel(cell.x, cell.z, VisualGlowAt(map, cell));
                _modelLightPositions.SetPixel(cell.x, cell.z, ModelLightPositionAt(map, cell));
            }
            _dirtyLightCells.Clear();
            _gameLightMap.Apply(false, false);
            _modelLightPositions.Apply(false, false);
        }

        internal void Dispose()
        {
            if (_gameLightMap != null) { UnityEngine.Object.Destroy(_gameLightMap); _gameLightMap = null; }
            if (_modelLightPositions != null) { UnityEngine.Object.Destroy(_modelLightPositions); _modelLightPositions = null; }
            _modelLights.Clear(); _modelLightsDirty = false;
            _glowingThingIds.Clear();
            _dirtyLightCells.Clear();
        }
    }
}

