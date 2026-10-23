using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed partial class CombatEffectsRenderer
        {
            sealed class GeyserVisual
            {
                public readonly List<GameObject> Clouds = new List<GameObject>();
                public bool Spraying;
                public int StartTick;
                public int EndTick = -1;
            }

            static readonly AccessTools.FieldRef<Building_SteamGeyser, IntermittentSteamSprayer> _geyserSprayer =
                AccessTools.FieldRefAccess<Building_SteamGeyser, IntermittentSteamSprayer>("steamSprayer");

            static readonly AccessTools.FieldRef<IntermittentSteamSprayer, int> _sprayTicksLeft =
                AccessTools.FieldRefAccess<IntermittentSteamSprayer, int>("sprayTicksLeft");
            readonly Dictionary<(IntVec3, GasType), GameObject> _gasClouds = new Dictionary<(IntVec3, GasType), GameObject>();
            readonly HashSet<(IntVec3, GasType)> _visibleGasCells = new HashSet<(IntVec3, GasType)>();
            readonly List<(IntVec3, GasType)> _expiredGasCells = new List<(IntVec3, GasType)>();
            readonly Dictionary<Building_SteamGeyser, GeyserVisual> _geyserVisuals = new Dictionary<Building_SteamGeyser, GeyserVisual>();
            readonly List<Building_SteamGeyser> _removedGeysers = new List<Building_SteamGeyser>();
            Material _thickGasMaterial, _deadlifeGasMaterial, _steamMaterial;
            float _nextGasScan;

            void UpdateGasVisuals()
            {
                if (Time.unscaledTime < _nextGasScan) return;
                _nextGasScan = Time.unscaledTime + .2f;
                if (!_view.map.gasGrid.CalculateGasEffects && _gasClouds.Count == 0) return;
                _visibleGasCells.Clear();
                var center = _view.CameraMapPosition.ToIntVec3();
                int radius = _view.FirstPerson ? 24 : 32;
                int minX = Mathf.Max(0, center.x - radius) / 2 * 2;
                int minZ = Mathf.Max(0, center.z - radius) / 2 * 2;
                int maxX = Mathf.Min(_view.map.Size.x - 1, center.x + radius);
                int maxZ = Mathf.Min(_view.map.Size.z - 1, center.z + radius);
                for (int z = minZ; z <= maxZ; z += 2)
                for (int x = minX; x <= maxX; x += 2)
                {
                    var density = Vector4.zero;
                    for (int dz = 0; dz < 2; dz++) for (int dx = 0; dx < 2; dx++)
                    {
                        var sample = new IntVec3(x + dx, 0, z + dz);
                        if (sample.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(sample))
                            density += _view.map.gasGrid.DensitiesAt(sample);
                    }
                    var cell = new IntVec3(x, 0, z);
                    UpdateGasCloud(cell, GasType.BlindSmoke, density.x);
                    UpdateGasCloud(cell, GasType.ToxGas, density.y);
                    UpdateGasCloud(cell, GasType.RotStink, density.z);
                    UpdateGasCloud(cell, GasType.DeadlifeDust, density.w);
                }
                _expiredGasCells.Clear();
                foreach (var entry in _gasClouds)
                    if (!_visibleGasCells.Contains(entry.Key)) _expiredGasCells.Add(entry.Key);
                foreach (var key in _expiredGasCells)
                {
                    UnityEngine.Object.Destroy(_gasClouds[key]);
                    _gasClouds.Remove(key);
                }
            }

            void UpdateGasCloud(IntVec3 cell, GasType type, float density)
            {
                if (density <= 0) return;
                var key = (cell, type);
                _visibleGasCells.Add(key);
                if (!_gasClouds.TryGetValue(key, out var cloud))
                {
                    Material material;
                    if (type == GasType.BlindSmoke) material = SmokeEffectMaterial();
                    else if (type == GasType.DeadlifeDust)
                    {
                        if (_deadlifeGasMaterial == null)
                            _deadlifeGasMaterial = EffectMaterial(ModAssets.LoadTexture("textures/things/gas/DeadlifeDust"), Color.white);
                        material = _deadlifeGasMaterial;
                    }
                    else
                    {
                        if (_thickGasMaterial == null)
                            _thickGasMaterial = EffectMaterial(ModAssets.LoadTexture("textures/things/gas/GasCloudThickA"), Color.white);
                        material = _thickGasMaterial;
                    }
                    cloud = EffectCard("RimVision gas: " + type, material);
                    if (cloud == null) return;
                    _gasClouds.Add(key, cloud);
                }
                bool smoke = type == GasType.BlindSmoke;
                float phase = (cell.x * 13 + cell.z * 7) * .1f + Find.TickManager.TicksGame / 120f + (int)type;
                cloud.transform.localPosition = new Vector3(cell.x + 1 + Mathf.Sin(phase) * .15f,
                    (smoke ? 1.15f : .65f) + Mathf.Sin(phase * .7f) * .15f, cell.z + 1);
                cloud.transform.localScale = smoke ? new Vector3(3.2f, 2.8f, 1) : new Vector3(2.8f, 1.7f, 1);
                var color = type == GasType.ToxGas ? new Color(.65f, .8f, .25f) :
                    type == GasType.RotStink ? new Color(.65f, .57f, .32f) :
                    type == GasType.DeadlifeDust ? new Color(.7f, .45f, .65f) : new Color(.55f, .56f, .57f);
                color.a = Mathf.Clamp01(density / 600f) * .85f;
                TintEffect(cloud, color);
            }

            void TrackGeyserThing(Thing thing)
            {
                if (thing is Building_SteamGeyser geyser && !_geyserVisuals.ContainsKey(geyser))
                    _geyserVisuals.Add(geyser, new GeyserVisual());
            }

            void UpdateGeyserVisuals()
            {
                int tick = Find.TickManager.TicksGame;
                var cameraPosition = _view.CameraMapPosition;
                _removedGeysers.Clear();
                foreach (var entry in _geyserVisuals)
                {
                    var geyser = entry.Key;
                    var visual = entry.Value;
                    if (geyser.Destroyed || !geyser.Spawned || geyser.Map != _view.map)
                    {
                        foreach (var cloud in visual.Clouds) UnityEngine.Object.Destroy(cloud);
                        _removedGeysers.Add(geyser);
                        continue;
                    }
                    var sprayer = _geyserSprayer(geyser);
                    bool spraying = geyser.harvester == null && sprayer != null && _sprayTicksLeft(sprayer) > 0;
                    if (spraying && !visual.Spraying)
                    {
                        visual.StartTick = tick;
                        visual.EndTick = -1;
                    }
                    else if (!spraying && visual.Spraying) visual.EndTick = tick;
                    visual.Spraying = spraying;
                    var position = geyser.TrueCenter();
                    float dx = position.x - cameraPosition.x, dz = position.z - cameraPosition.z;
                    bool visible = !_view.map.fogGrid.IsFogged(geyser.Position) && geyser.harvester == null &&
                        dx * dx + dz * dz <= 48 * 48 &&
                        (spraying || visual.EndTick >= 0 && tick - visual.EndTick < 90);
                    if (!visible)
                    {
                        foreach (var cloud in visual.Clouds) cloud.SetActive(false);
                        continue;
                    }
                    if (visual.Clouds.Count == 0)
                    {
                        if (_steamMaterial == null)
                            _steamMaterial = EffectMaterial(ModAssets.LoadTexture("textures/things/mote/AirPuff"), Color.white);
                        for (int i = 0; i < 6; i++)
                        {
                            var cloud = EffectCard("RimVision geyser steam", _steamMaterial);
                            if (cloud != null) visual.Clouds.Add(cloud);
                        }
                    }
                    int emissionTick = spraying ? tick : visual.EndTick - 1;
                    bool roofed = _view.map.roofGrid.Roofed(geyser.Position);
                    for (int i = 0; i < visual.Clouds.Count; i++)
                    {
                        var cloud = visual.Clouds[i];
                        int firstTick = visual.StartTick + i * 15;
                        if (emissionTick < firstTick) { cloud.SetActive(false); continue; }
                        int birthTick = firstTick + (emissionTick - firstTick) / 90 * 90;
                        float age = (tick - birthTick) / 90f;
                        float phase = geyser.thingIDNumber * .37f + i * 2.4f;
                        float size = .6f + age * 1.5f;
                        position.y = Mathf.Min(.25f + age * 3.2f, roofed ? 1.35f : 3.5f);
                        cloud.transform.localPosition = position + new Vector3(Mathf.Sin(phase) * age * .45f,
                            0, Mathf.Cos(phase) * age * .45f);
                        cloud.transform.localScale = new Vector3(size, roofed ? Mathf.Min(size, 1.1f) : size, 1);
                        TintEffect(cloud, new Color(.9f, .92f, .94f,
                            Mathf.Clamp01(age * 8) * Mathf.Clamp01(1 - age) * .65f));
                        cloud.SetActive(age < 1);
                    }
                }
                foreach (var geyser in _removedGeysers) _geyserVisuals.Remove(geyser);
            }

            void DisposeGasVisuals()
            {
                _gasClouds.Clear(); _visibleGasCells.Clear(); _expiredGasCells.Clear();
                _geyserVisuals.Clear(); _removedGeysers.Clear();
                _thickGasMaterial = null; _deadlifeGasMaterial = null; _steamMaterial = null;
            }
        }
    }
}
