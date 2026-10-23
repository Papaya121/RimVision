using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        struct WeatherVisualProfile
        {
            public float Rain, Snow, Dust, Fog, Cloudiness;
            public Color Tint;
        }

        private WeatherParticlesRenderer _weatherParticles;
        readonly Dictionary<WeatherDef, WeatherVisualProfile> weatherProfiles = new Dictionary<WeatherDef, WeatherVisualProfile>();
        float weatherDirectSun = 1;
        Color weatherSkyTint = Color.white;
        static readonly int WeatherFogId = Shader.PropertyToID("_Rim3DWeatherFog");
        static readonly int PlantWindId = Shader.PropertyToID("_Rim3DPlantWind");
        static readonly int IndoorAmbientId = Shader.PropertyToID("_Rim3DIndoorAmbientColor");
        static readonly int IndoorFogColorId = Shader.PropertyToID("_Rim3DIndoorFogColor");

        private LightningRenderer _lightningRenderer;

        void OnLightningStrike(Map strikeMap, IntVec3 cell, Mesh boltMesh)
        {
            if (strikeMap != map || !cell.IsValid || boltMesh == null) return;
            _lightningRenderer.TrackStrike(map, cell, boltMesh);
        }

        float UpdateLightningRendering()
        {
            return _lightningRenderer.Update(map);
        }

        WeatherVisualProfile WeatherProfile(WeatherDef def)
        {
            if (def == null) return new WeatherVisualProfile { Tint = Color.white };
            if (weatherProfiles.TryGetValue(def, out var profile)) return profile;
            // Snow definitions also have rainRate for game mechanics, not rainfall visuals.
            profile = new WeatherVisualProfile { Rain = def.snowRate > 0 ? 0 : def.rainRate,
                Snow = def.snowRate, Dust = def.sandRate, Tint = Color.white };
            // Includes dry overcast/custom weather, even when no precipitation is present.
            profile.Cloudiness = Mathf.Clamp01((1 - def.skyColorsDay.sky.grayscale) * 3);
            if (def.overlayClasses != null)
                foreach (var overlay in def.overlayClasses)
                {
                    string name = overlay.Name;
                    if (name.IndexOf("Fog", System.StringComparison.OrdinalIgnoreCase) >= 0) profile.Fog = 1;
                    if (name.IndexOf("Ash", System.StringComparison.OrdinalIgnoreCase) >= 0) profile.Dust = Mathf.Max(profile.Dust, .5f);
                    if (name.IndexOf("Blood", System.StringComparison.OrdinalIgnoreCase) >= 0) profile.Tint = new Color(.65f, .12f, .10f);
                    if (name.IndexOf("Tox", System.StringComparison.OrdinalIgnoreCase) >= 0) profile.Tint = new Color(.65f, .80f, .30f);
                }
            weatherProfiles.Add(def, profile);
            return profile;
        }

        void UpdateWeatherRendering(ref Color ambient, ref Color horizon, float daylight)
            => UpdateWeatherPresentation(ref ambient, ref horizon, daylight, true);

        void UpdateWeatherPresentation(ref Color ambient, ref Color horizon, float daylight, bool renderEffects)
        {
            if (SkyRenderer.IsSpaceMap(map))
            {
                Shader.SetGlobalVector(PlantWindId, Vector4.zero);
                weatherSkyTint = Color.white;
                weatherDirectSun = 1;
                Shader.SetGlobalColor(IndoorAmbientId, ambient);
                Shader.SetGlobalColor(IndoorFogColorId, horizon);
                Shader.SetGlobalVector(WeatherFogId, new Vector4(0, 0, 8, 1));
                if (renderEffects)
                {
                    _skyRenderer.SetOvercast(skyMaterial, 0);
                    _weatherParticles.Hide();
                }
                return;
            }
            var manager = map.weatherManager;
            var previous = WeatherProfile(manager.lastWeather);
            var current = WeatherProfile(manager.curWeather);
            float blend = manager.TransitionLerpFactor;
            float rain = Mathf.Lerp(previous.Rain, current.Rain, blend);
            float snow = Mathf.Lerp(previous.Snow, current.Snow, blend);
            float dust = Mathf.Lerp(previous.Dust, current.Dust, blend);
            float wind = Mathf.Clamp(map.windManager.WindSpeed, 0, 4);
            float storm = Mathf.Clamp01(Mathf.Max(rain * .8f, Mathf.Max(snow, dust)));
            float turbulence = Mathf.Max(storm, Mathf.Clamp01((wind - 1) * .5f));
            Shader.SetGlobalVector(PlantWindId, new Vector4(.9230769f, .3846154f,
                Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.PlantWind)
                    ? Mathf.Clamp01(wind * .55f + storm * .45f) : 0,
                turbulence));
            float fog = Mathf.Lerp(previous.Fog, current.Fog, blend);
            float haze = Mathf.Clamp01(fog + snow * .25f + dust * .7f + rain * .12f);
            var tint = Color.Lerp(previous.Tint, current.Tint, blend);
            weatherSkyTint = tint;
            horizon = Color.Lerp(horizon, horizon * tint, haze * .65f);
            ambient = Color.Lerp(ambient, ambient * tint, haze * .25f);
            weatherDirectSun = 1 - Mathf.Clamp01(rain * .75f + snow * .65f + fog * .9f + dust * .8f);
            sunlight.intensity *= weatherDirectSun;
            float overcast = Mathf.Clamp01(Mathf.Max(Mathf.Lerp(previous.Cloudiness, current.Cloudiness, blend),
                haze + rain * .5f + snow * .5f));
            ambient = Color.Lerp(ambient, ambient * new Color(.86f, .93f, 1f), overcast * daylight * .55f);
            horizon = Color.Lerp(horizon, ambient * 1.35f, overcast * .55f);
            Shader.SetGlobalColor(IndoorAmbientId, ambient);
            Shader.SetGlobalColor(IndoorFogColorId, horizon);
            float lightning = renderEffects ? UpdateLightningRendering() : 0;
            var lightningColor = new Color(.68f, .79f, 1);
            ambient = Color.Lerp(ambient, lightningColor, lightning * .65f);
            horizon = Color.Lerp(horizon, lightningColor, lightning * .8f);
            biomeZenith = Color.Lerp(biomeZenith, lightningColor, lightning * .7f);
            biomeClouds = Color.Lerp(biomeClouds, lightningColor, lightning * .85f);
            var eye = CameraMapPosition;
            var cell = new IntVec3(Mathf.FloorToInt(eye.x), 0, Mathf.FloorToInt(eye.z));
            float outdoors = eye.y < 2.06f && cell.InBounds(map) && map.roofGrid.Roofed(cell) ? 0 : 1;
            Shader.SetGlobalVector(WeatherFogId, Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.WeatherFog)
                ? new Vector4(biomeFogDensity + haze * .085f, biomeFogFalloff + haze * .0008f, Mathf.Lerp(8, 1, haze), outdoors)
                : new Vector4(0, 0, 8, outdoors));
            if (!renderEffects) return;
            _skyRenderer.SetOvercast(skyMaterial, overcast);
            if (!Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.WeatherParticles) || rain + snow + dust < .001f)
            {
                _weatherParticles.Hide();
                return;
            }
            _weatherParticles.Update(map, eye, FirstPerson, rain, snow, dust, daylight, tint);
        }

        void DisposeWeatherRendering()
        {
            LightningStrikePatch.Struck -= OnLightningStrike;
            _lightningRenderer?.ClearStrikeTracking();
            weatherProfiles.Clear();
            Shader.SetGlobalVector(PlantWindId, Vector4.zero);
            _lightningRenderer?.ClearVisuals();
            _weatherParticles?.Dispose();
            Shader.SetGlobalVector(WeatherFogId, new Vector4(.012f, .00012f, 8, 1));
        }
    }

    internal sealed class LightningRenderer
    {
        private readonly Transform _parent;
        private readonly int _layer;
        private readonly List<Material> _materials;
        private readonly Camera _camera;

        internal LightningRenderer(Transform parent, int layer, List<Material> materials, Camera camera)
        {
            _parent = parent;
            _layer = layer;
            _materials = materials;
            _camera = camera;
        }

        sealed class LightningVisual
        {
            public GameObject Object;
            public LineRenderer[] Lines;
            public WeatherEvent_LightningFlash Event;
            public int StartTick;
        }

        private readonly List<LightningVisual> _lightningVisuals = new List<LightningVisual>(4);
        private readonly Stack<LightningVisual> _lightningPool = new Stack<LightningVisual>(4);
        private readonly Vector3[] _lightningPath = new Vector3[33];
        private readonly Vector3[] _lightningBranch = new Vector3[9];
        private readonly Dictionary<WeatherEvent_LightningFlash, IntVec3> _lightningStrikeCells =
            new Dictionary<WeatherEvent_LightningFlash, IntVec3>();
        private readonly List<WeatherEvent_LightningFlash> _expiredLightningStrikes = new List<WeatherEvent_LightningFlash>();
        private Material _lightningMaterial;
        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrike, IntVec3> _lightningStrikeCell =
            AccessTools.FieldRefAccess<WeatherEvent_LightningStrike, IntVec3>("strikeLoc");
        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrike, Mesh> _lightningStrikeMesh =
            AccessTools.FieldRefAccess<WeatherEvent_LightningStrike, Mesh>("boltMesh");
        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrikeDelayed, IntVec3> _delayedLightningStrikeCell =
            AccessTools.FieldRefAccess<WeatherEvent_LightningStrikeDelayed, IntVec3>("strikeLoc");
        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrikeDelayed, Mesh> _delayedLightningStrikeMesh =
            AccessTools.FieldRefAccess<WeatherEvent_LightningStrikeDelayed, Mesh>("boltMesh");

        internal void TrackStrike(Map map, IntVec3 cell, Mesh boltMesh)
        {
            var events = map.weatherManager.eventHandler.LiveEventsListForReading;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (!(events[i] is WeatherEvent_LightningFlash flash) || flash.Expired ||
                    _lightningStrikeCells.ContainsKey(flash)) continue;
                var mesh = flash is WeatherEvent_LightningStrike strike ? _lightningStrikeMesh(strike) :
                    flash is WeatherEvent_LightningStrikeDelayed delayed ? _delayedLightningStrikeMesh(delayed) : null;
                if (mesh != boltMesh) continue;
                _lightningStrikeCells.Add(flash, cell);
                return;
            }
        }

        LightningVisual CreateLightningVisual()
        {
            if (_lightningPool.Count > 0) return _lightningPool.Pop();
            if (_lightningMaterial == null)
            {
                var lightningShader = ModAssets.LoadShader("Rim3D/Lightning");
                if (lightningShader == null || !lightningShader.isSupported) return null;
                _lightningMaterial = new Material(lightningShader) { hideFlags = HideFlags.DontSave };
                _materials.Add(_lightningMaterial);
            }
            var visual = new LightningVisual
            {
                Object = new GameObject("RimVision lightning") { layer = _layer, hideFlags = HideFlags.DontSave },
                Lines = new LineRenderer[4]
            };
            visual.Object.transform.SetParent(_parent, false);
            for (int i = 0; i < visual.Lines.Length; i++)
            {
                var obj = new GameObject("Lightning channel") { layer = _layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(visual.Object.transform, false);
                var line = obj.AddComponent<LineRenderer>();
                line.sharedMaterial = _lightningMaterial;
                line.useWorldSpace = false;
                line.alignment = LineAlignment.View;
                line.textureMode = LineTextureMode.Stretch;
                line.positionCount = i == 0 ? _lightningPath.Length : _lightningBranch.Length;
                line.startWidth = i == 0 ? .75f : .38f;
                line.endWidth = i == 0 ? .32f : .025f;
                line.numCornerVertices = 2;
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.lightProbeUsage = LightProbeUsage.Off;
                line.reflectionProbeUsage = ReflectionProbeUsage.Off;
                visual.Lines[i] = line;
            }
            return visual;
        }

        void ShapeLightning(LightningVisual visual, IntVec3 strikeCell, int seed)
        {
            var random = new System.Random(seed);
            var eye = _camera.transform.localPosition;
            Vector3 end;
            if (strikeCell.IsValid)
                end = strikeCell.ToVector3Shifted() + Vector3.up * .15f;
            else
            {
                var forward = _camera.transform.forward;
                forward.y = 0;
                if (forward.sqrMagnitude < .001f) forward = Vector3.forward;
                forward.Normalize();
                var right = new Vector3(forward.z, 0, -forward.x);
                end = eye + forward * (55 + (float)random.NextDouble() * 45)
                    + right * ((float)random.NextDouble() - .5f) * 100;
                end.y = Mathf.Max(12, eye.y + 8);
            }
            var start = end + new Vector3(((float)random.NextDouble() - .5f) * 22,
                40 + (float)random.NextDouble() * 25, ((float)random.NextDouble() - .5f) * 22);
            for (int i = 0; i < _lightningPath.Length; i++)
            {
                float t = i / (float)(_lightningPath.Length - 1);
                float spread = Mathf.Sin(t * Mathf.PI) * 3.5f;
                _lightningPath[i] = Vector3.Lerp(start, end, t) + new Vector3(
                    ((float)random.NextDouble() - .5f) * spread, 0,
                    ((float)random.NextDouble() - .5f) * spread);
            }
            visual.Lines[0].SetPositions(_lightningPath);
            for (int branch = 1; branch < visual.Lines.Length; branch++)
            {
                var origin = _lightningPath[random.Next(5, 23)];
                float angle = (float)random.NextDouble() * Mathf.PI * 2;
                float length = 7 + (float)random.NextDouble() * 10;
                var tip = origin + new Vector3(Mathf.Cos(angle) * length, -length * .65f, Mathf.Sin(angle) * length);
                for (int i = 0; i < _lightningBranch.Length; i++)
                {
                    float t = i / (float)(_lightningBranch.Length - 1);
                    float spread = Mathf.Sin(t * Mathf.PI) * 2;
                    _lightningBranch[i] = Vector3.Lerp(origin, tip, t) + new Vector3(
                        ((float)random.NextDouble() - .5f) * spread, 0,
                        ((float)random.NextDouble() - .5f) * spread);
                }
                visual.Lines[branch].SetPositions(_lightningBranch);
            }
        }

        internal float Update(Map map)
        {
            bool enabled = Rim3DSettings.Current.EffectsEnabled(Rim3DSettings.Current.WeatherParticles);
            var events = map.weatherManager.eventHandler.LiveEventsListForReading;
            _expiredLightningStrikes.Clear();
            foreach (var entry in _lightningStrikeCells)
                if (entry.Key.Expired || !events.Contains(entry.Key)) _expiredLightningStrikes.Add(entry.Key);
            for (int i = 0; i < _expiredLightningStrikes.Count; i++)
                _lightningStrikeCells.Remove(_expiredLightningStrikes[i]);
            _expiredLightningStrikes.Clear();
            for (int i = _lightningVisuals.Count - 1; i >= 0; i--)
            {
                var visual = _lightningVisuals[i];
                if (enabled && !visual.Event.Expired && events.Contains(visual.Event)) continue;
                visual.Object.SetActive(false);
                visual.Event = null;
                _lightningPool.Push(visual);
                _lightningVisuals.RemoveAt(i);
            }
            if (!enabled) return 0;
            int tick = Find.TickManager.TicksGame;
            for (int i = 0; i < events.Count && _lightningVisuals.Count < 4; i++)
            {
                if (!(events[i] is WeatherEvent_LightningFlash flash) || flash.Expired || flash.SkyTargetLerpFactor < 0) continue;
                bool tracked = false;
                for (int j = 0; j < _lightningVisuals.Count; j++)
                    if (_lightningVisuals[j].Event == flash) { tracked = true; break; }
                if (tracked) continue;
                if (!_lightningStrikeCells.TryGetValue(flash, out var cell))
                    cell = flash is WeatherEvent_LightningStrike strike ? _lightningStrikeCell(strike) :
                        flash is WeatherEvent_LightningStrikeDelayed delayed ? _delayedLightningStrikeCell(delayed) : IntVec3.Invalid;
                if (cell.IsValid && (!cell.InBounds(map) || map.fogGrid.IsFogged(cell))) continue;
                var visual = CreateLightningVisual();
                if (visual == null) break;
                visual.Event = flash;
                visual.StartTick = tick;
                ShapeLightning(visual, cell, unchecked(tick * 397 ^ i * 7919 ^ map.uniqueID));
                visual.Object.SetActive(true);
                _lightningVisuals.Add(visual);
            }
            float illumination = 0;
            for (int i = 0; i < _lightningVisuals.Count; i++)
            {
                var visual = _lightningVisuals[i];
                float fade = Mathf.Clamp01(visual.Event.SkyTargetLerpFactor);
                int age = tick - visual.StartTick;
                float pulse = age < 4 ? 1 : .3f + .7f * Mathf.Pow(Mathf.Max(0, Mathf.Cos(age * 1.15f)), 6);
                float brightness = fade * fade * pulse;
                for (int j = 0; j < visual.Lines.Length; j++)
                {
                    var color = new Color(1, 1, 1, brightness * (j == 0 ? 1 : .65f));
                    visual.Lines[j].startColor = color;
                    visual.Lines[j].endColor = color;
                }
                illumination = Mathf.Max(illumination, brightness);
            }
            return illumination;
        }

        internal void ClearStrikeTracking()
        {
            _lightningStrikeCells.Clear();
            _expiredLightningStrikes.Clear();
        }

        internal void ClearVisuals()
        {
            _lightningVisuals.Clear();
            _lightningPool.Clear();
            _lightningMaterial = null;
        }
    }

    internal sealed class WeatherParticlesRenderer
    {
        private const int WeatherParticleCount = 3072;
        private readonly Transform _parent;
        private readonly int _layer;
        private readonly List<Mesh> _meshes;
        private readonly List<Material> _materials;
        private GameObject _object;
        private Mesh _mesh;
        private Material _material;
        private int _lastTicks = -1;
        private double _animationTime;
        private double _windTravel;
        private static readonly int _volumeId = Shader.PropertyToID("_Volume");
        private static readonly int _ratesId = Shader.PropertyToID("_Rates");
        private static readonly int _timeId = Shader.PropertyToID("_WeatherTime");
        private static readonly int _windId = Shader.PropertyToID("_Wind");
        private static readonly int _driftId = Shader.PropertyToID("_WeatherDrift");
        private static readonly int _tintId = Shader.PropertyToID("_Tint");

        internal WeatherParticlesRenderer(Transform parent, int layer, List<Mesh> meshes, List<Material> materials)
        {
            _parent = parent;
            _layer = layer;
            _meshes = meshes;
            _materials = materials;
        }

        private void InitializeWeatherParticles()
        {
            var weatherShader = ModAssets.LoadShader("Rim3D/WeatherParticles");
            if (weatherShader == null || !weatherShader.isSupported) return;
            _material = new Material(weatherShader) { hideFlags = HideFlags.DontSave };
            _materials.Add(_material);
            var vertices = new Vector3[WeatherParticleCount * 12];
            var seeds = new Vector4[vertices.Length];
            var triangles = new int[WeatherParticleCount * 18];
            var random = new System.Random(7319);
            for (int i = 0; i < WeatherParticleCount; i++)
            {
                var seed = new Vector4((float)random.NextDouble(), (float)random.NextDouble(),
                    (float)random.NextDouble(), (i + .5f) / WeatherParticleCount);
                for (int effect = 0; effect < 3; effect++)
                {
                    int v = (i * 3 + effect) * 4, t = (i * 3 + effect) * 6;
                    vertices[v] = new Vector3(-.5f, -.5f, effect); vertices[v + 1] = new Vector3(.5f, -.5f, effect);
                    vertices[v + 2] = new Vector3(.5f, .5f, effect); vertices[v + 3] = new Vector3(-.5f, .5f, effect);
                    for (int k = 0; k < 4; k++) seeds[v + k] = seed;
                    triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                    triangles[t + 3] = v; triangles[t + 4] = v + 2; triangles[t + 5] = v + 3;
                }
            }
            _mesh = new Mesh { name = "RimVision GPU precipitation", hideFlags = HideFlags.DontSave };
            _mesh.vertices = vertices;
            _mesh.SetUVs(0, new List<Vector4>(seeds));
            _mesh.triangles = triangles;
            _mesh.UploadMeshData(true);
            _meshes.Add(_mesh);
            _object = new GameObject("RimVision weather") { layer = _layer, hideFlags = HideFlags.DontSave };
            _object.transform.SetParent(_parent, false);
            _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _object.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        internal void Update(Map map, Vector3 eye, bool firstPerson, float rain, float snow, float dust, float daylight, Color tint)
        {
            if (_object == null) InitializeWeatherParticles();
            if (_object == null) return;
            _object.SetActive(true);
            float rainShare = rain / Mathf.Max(.001f, rain + snow + dust);
            float radius = firstPerson ? Mathf.Lerp(18, 14, rainShare) : 55;
            float height = firstPerson ? Mathf.Lerp(12, 8, rainShare) : 28;
            float bottom = Mathf.Max(0, eye.y - height * .5f);
            _object.transform.localPosition = new Vector3(eye.x, bottom, eye.z);
            _mesh.bounds = new Bounds(new Vector3(0, (height - bottom) * .5f, 0), new Vector3(radius * 2 + 4, height + bottom + 4, radius * 2 + 4));
            _material.SetVector(_volumeId, new Vector4(radius, height, bottom, firstPerson ? 1 : 2.4f));
            _material.SetVector(_ratesId, new Vector4(rain, snow, dust, 0));
            int ticks = Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
                ? Rim3DMultiplayer.MapTicks(map) : Find.TickManager.TicksGame;
            double elapsed = _lastTicks >= 0 && ticks >= _lastTicks ? (ticks - _lastTicks) / 60.0 : 0;
            _lastTicks = ticks;
            float wind = Mathf.Clamp(map.windManager.WindSpeed, 0, 4);
            _animationTime += elapsed;
            _windTravel += elapsed * wind;
            _material.SetFloat(_timeId, (float)_animationTime);
            _material.SetVector(_windId, new Vector4(.6f, .25f, wind, 0));
            _material.SetVector(_driftId, new Vector4((float)(_windTravel * .6 % (radius * 2)),
                (float)(_windTravel * .25 % (radius * 2)), 0, 0));
            _material.SetColor(_tintId, Color.Lerp(new Color(.32f, .38f, .48f), Color.white, daylight) * tint);
        }

        internal void Hide()
        {
            if (_object != null) _object.SetActive(false);
            _lastTicks = -1;
        }

        internal void Dispose()
        {
            _object = null; _mesh = null; _material = null;
            _lastTicks = -1;
            _animationTime = 0;
            _windTravel = 0;
        }
    }

    [HarmonyPatch(typeof(WeatherEvent_LightningStrike), nameof(WeatherEvent_LightningStrike.DoStrike))]
    static class LightningStrikePatch
    {
        internal static event System.Action<Map, IntVec3, Mesh> Struck;

        static void Notify(Map map, IntVec3 cell, Mesh boltMesh) => Struck?.Invoke(map, cell, boltMesh);

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var notify = AccessTools.Method(typeof(LightningStrikePatch), nameof(Notify));
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ret)
                {
                    instruction.opcode = OpCodes.Ldarg_1;
                    instruction.operand = null;
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return new CodeInstruction(OpCodes.Ldind_Ref);
                    yield return new CodeInstruction(OpCodes.Call, notify);
                    yield return new CodeInstruction(OpCodes.Ret);
                    continue;
                }
                yield return instruction;
            }
        }
    }
}

