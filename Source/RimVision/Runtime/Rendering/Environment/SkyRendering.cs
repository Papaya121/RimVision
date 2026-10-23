using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        private readonly SkyRenderer _skyRenderer = new SkyRenderer();

        void UpdateSkyRendering(Color horizon, Vector3 sunDirection)
        {
            _skyRenderer.Update(map, skyMaterial, horizon, biomeZenith, biomeClouds, weatherSkyTint,
                weatherDirectSun, biomeSkyDay, sunDirection);
        }

        void DisposeSkyRendering()
        {
            _skyRenderer.Dispose();
        }
    }

    internal sealed class SkyRenderer
    {
        private static readonly Func<float, int, float, Vector3> _sunPosition =
            AccessTools.MethodDelegate<Func<float, int, float, Vector3>>(AccessTools.Method(typeof(GenCelestial),
                "SunPosition", new[] { typeof(float), typeof(int), typeof(float) }));
        private static readonly Func<float, Vector3> _surfaceNormal =
            AccessTools.MethodDelegate<Func<float, Vector3>>(AccessTools.Method(typeof(GenCelestial),
                "SurfaceNormal", new[] { typeof(float) }));

        private readonly struct PlanetDefinition
        {
            internal readonly Vector4 Orbit;
            internal readonly Vector4 Surface;
            internal readonly Vector4 Primary;
            internal readonly Vector4 Secondary;
            internal readonly Vector4 Rings;

            internal PlanetDefinition(Vector4 orbit, Vector4 surface, Vector4 primary, Vector4 secondary, Vector4 rings)
            {
                Orbit = orbit;
                Surface = surface;
                Primary = primary;
                Secondary = secondary;
                Rings = rings;
            }
        }

        private static readonly PlanetDefinition[] _planets =
        {
            new PlanetDefinition(new Vector4(2.7f, .12f, 48, 15), new Vector4(.027f, 0, 3.1f, 1.8f),
                new Vector4(.45f, .19f, .12f, .025f), new Vector4(.24f, .09f, .055f, 0), Vector4.zero),
            new PlanetDefinition(new Vector4(4.3f, .43f, 63, 105), new Vector4(.066f, 1, 7.7f, 1.2f),
                new Vector4(.88f, .69f, .39f, .3f), new Vector4(.44f, .24f, .12f, 0), new Vector4(2.05f, 1.28f, 62, -28)),
            new PlanetDefinition(new Vector4(6.1f, .76f, 72, 210), new Vector4(.043f, 2, 13.2f, 2.4f),
                new Vector4(.06f, .28f, .65f, .6f), new Vector4(.22f, .48f, .26f, 0), Vector4.zero),
            new PlanetDefinition(new Vector4(8.7f, .29f, 54, 280), new Vector4(.038f, 4, 21.4f, 3.1f),
                new Vector4(.49f, .83f, .91f, .45f), new Vector4(.15f, .37f, .54f, 0), new Vector4(1.7f, 1.22f, 48, 35)),
            new PlanetDefinition(new Vector4(11.3f, .62f, 68, 55), new Vector4(.031f, 3, 34.6f, 1.6f),
                new Vector4(.13f, .09f, .12f, .06f), new Vector4(.65f, .12f, .015f, 0), Vector4.zero),
            new PlanetDefinition(new Vector4(14.9f, .91f, 44, 165), new Vector4(.052f, 1, 48.3f, 1.5f),
                new Vector4(.59f, .48f, .81f, .4f), new Vector4(.23f, .17f, .43f, 0), new Vector4(2.2f, 1.35f, 70, -48)),
            new PlanetDefinition(new Vector4(18.1f, .54f, 59, 325), new Vector4(.035f, 0, 59.8f, 3.8f),
                new Vector4(.84f, .65f, .35f, .18f), new Vector4(.48f, .31f, .16f, 0), Vector4.zero),
            new PlanetDefinition(new Vector4(23.7f, .03f, 77, 245), new Vector4(.019f, 0, 73.5f, 4.7f),
                new Vector4(.59f, .62f, .69f, 0), new Vector4(.23f, .25f, .31f, 0), Vector4.zero)
        };

        private readonly Vector4[] _planetDirections = new Vector4[_planets.Length];
        private readonly Vector4[] _planetSurfaces = new Vector4[_planets.Length];
        private readonly Vector4[] _planetPrimary = new Vector4[_planets.Length];
        private readonly Vector4[] _planetSecondary = new Vector4[_planets.Length];
        private readonly Vector4[] _planetRings = new Vector4[_planets.Length];
        private Texture2D _skyCloudNoise;
        private Texture2D _orbitalClouds;
        private static readonly int _spaceId = Shader.PropertyToID("_Space");
        private static readonly int _orbitalCloudsId = Shader.PropertyToID("_OrbitalClouds");
        private static readonly int _orbitalFrameId = Shader.PropertyToID("_OrbitalFrame");
        private static readonly int _planetDirectionsId = Shader.PropertyToID("_PlanetDirections");
        private static readonly int _planetSurfacesId = Shader.PropertyToID("_PlanetSurfaces");
        private static readonly int _planetPrimaryId = Shader.PropertyToID("_PlanetPrimary");
        private static readonly int _planetSecondaryId = Shader.PropertyToID("_PlanetSecondary");
        private static readonly int _planetRingsId = Shader.PropertyToID("_PlanetRings");
        private static readonly int _skyHorizonId = Shader.PropertyToID("_Horizon");
        private static readonly int _skyZenithId = Shader.PropertyToID("_Zenith");
        private static readonly int _skySunId = Shader.PropertyToID("_SunDirection");
        private static readonly int _skyDaylightId = Shader.PropertyToID("_Daylight");
        private static readonly int _skyNightId = Shader.PropertyToID("_Night");
        private static readonly int _skyCloudColorId = Shader.PropertyToID("_CloudColor");
        private static readonly int _skyCloudOffsetId = Shader.PropertyToID("_CloudOffset");
        private static readonly int _skyCloudNoiseId = Shader.PropertyToID("_CloudNoise");
        private static readonly int _skyOvercastId = Shader.PropertyToID("_Overcast");
        private static readonly int _skyMoonTextureId = Shader.PropertyToID("_MoonTex");
        private static readonly int _skyMoonLightId = Shader.PropertyToID("_MoonLight");

        internal static bool IsSpaceMap(Map map) => ModsConfig.OdysseyActive && map?.Biome?.inVacuum == true;

        internal static Vector3 SunDirection(Map map)
        {
            float latitude = Find.WorldGrid.LongLatOf(map.Tile).y;
            var sun = _sunPosition(latitude, GenLocalDate.DayOfYear(map), GenLocalDate.DayPercent(map));
            var up = _surfaceNormal(latitude);
            var north = new Vector3(-up.y, up.x, 0);
            return new Vector3(sun.z, Vector3.Dot(sun, up), Vector3.Dot(sun, north));
        }

        private void UpdateSpace(Map map, Material skyMaterial, int ticks)
        {
            if (_orbitalClouds == null)
            {
                _orbitalClouds = ModAssets.LoadTexture("textures/world/CloudMap") ?? Texture2D.blackTexture;
                skyMaterial.SetTexture(_orbitalCloudsId, _orbitalClouds);
            }
            var frame = Quaternion.identity;
            if (map.Tile.Valid)
            {
                var up = Find.WorldGrid.GetTileCenter(map.Tile).normalized;
                var north = Vector3.ProjectOnPlane(Vector3.up, up);
                if (north.sqrMagnitude < .0001f) north = Vector3.ProjectOnPlane(Vector3.forward, up);
                frame = Quaternion.LookRotation(north.normalized, up);
            }
            skyMaterial.SetMatrix(_orbitalFrameId, Matrix4x4.Rotate(frame));
            skyMaterial.SetVector(_skySunId, Quaternion.Inverse(frame) * GenCelestial.CurSunPositionInWorldSpace());
            double seconds = ticks / 60.0;
            skyMaterial.SetVector(_skyCloudOffsetId, new Vector4((float)(seconds * .000015 % 1), 0, 0, 0));
        }

        private static float SkyNoiseHash(int x, int y, int period)
        {
            unchecked
            {
                uint h = (uint)((x % period) * 374761393) ^ (uint)((y % period) * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h ^ (h >> 16)) / (float)uint.MaxValue;
            }
        }

        private static float SkyNoise(float x, float y, int period)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            return Mathf.Lerp(Mathf.Lerp(SkyNoiseHash(ix, iy, period), SkyNoiseHash(ix + 1, iy, period), fx),
                Mathf.Lerp(SkyNoiseHash(ix, iy + 1, period), SkyNoiseHash(ix + 1, iy + 1, period), fx), fy);
        }

        private void InitializeSkyTextures(Material skyMaterial)
        {
            const int size = 128;
            _skyCloudNoise = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "RimVision seamless cloud noise", hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float value = 0, weight = .5333333f;
                for (int octave = 0; octave < 4; octave++)
                {
                    int period = 4 << octave;
                    value += SkyNoise(x * period / (float)size, y * period / (float)size, period) * weight;
                    weight *= .5f;
                }
                byte v = (byte)Mathf.RoundToInt(value * 255);
                pixels[y * size + x] = new Color32(v, v, v, 255);
            }
            _skyCloudNoise.SetPixels32(pixels);
            _skyCloudNoise.Apply(true, true);
            skyMaterial.SetTexture(_skyCloudNoiseId, _skyCloudNoise);
            skyMaterial.SetTexture(_skyMoonTextureId, ModAssets.LoadTexture("textures/RimVision/Sky/FullMoon"));
        }

        internal void SetOvercast(Material skyMaterial, float overcast)
        {
            if (skyMaterial != null) skyMaterial.SetFloat(_skyOvercastId, overcast);
        }

        private void UpdatePlanets(Material skyMaterial, int ticks)
        {
            for (int i = 0; i < _planets.Length; i++)
            {
                PlanetDefinition planet = _planets[i];
                double orbitTicks = planet.Orbit.x * 60000.0;
                float angle = (float)(ticks % orbitTicks / orbitTicks + planet.Orbit.y) * Mathf.PI * 2;
                float inclination = planet.Orbit.z * Mathf.Deg2Rad;
                Vector3 direction = Quaternion.Euler(0, planet.Orbit.w, 0) * new Vector3(Mathf.Cos(angle),
                    (Mathf.Sin(angle) - .82f) * Mathf.Sin(inclination), Mathf.Sin(angle) * Mathf.Cos(inclination)).normalized;
                _planetDirections[i] = new Vector4(direction.x, direction.y, direction.z, planet.Surface.x);
                double spinTicks = planet.Surface.w * 60000.0;
                _planetSurfaces[i] = new Vector4(planet.Surface.y, planet.Surface.z,
                    (float)(ticks % spinTicks / spinTicks) * Mathf.PI * 2, planet.Primary.w);
                _planetPrimary[i] = planet.Primary;
                _planetSecondary[i] = planet.Secondary;
                _planetRings[i] = planet.Rings;
            }
            skyMaterial.SetVectorArray(_planetDirectionsId, _planetDirections);
            skyMaterial.SetVectorArray(_planetSurfacesId, _planetSurfaces);
            skyMaterial.SetVectorArray(_planetPrimaryId, _planetPrimary);
            skyMaterial.SetVectorArray(_planetSecondaryId, _planetSecondary);
            skyMaterial.SetVectorArray(_planetRingsId, _planetRings);
        }

        internal void Update(Map map, Material skyMaterial, Color horizon, Color biomeZenith, Color biomeClouds,
            Color weatherSkyTint, float weatherDirectSun, float biomeSkyDay, Vector3 sunDirection)
        {
            if (skyMaterial == null) return;
            if (_skyCloudNoise == null) InitializeSkyTextures(skyMaterial);
            int ticks = Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
                ? Rim3DMultiplayer.MapTicks(map) : Find.TickManager.TicksGame;
            bool space = IsSpaceMap(map);
            skyMaterial.SetFloat(_spaceId, space ? 1 : 0);
            if (space)
            {
                UpdateSpace(map, skyMaterial, ticks);
                return;
            }
            UpdatePlanets(skyMaterial, ticks);
            const int moonCycleTicks = 15 * 60000;
            float phaseAngle = (ticks % moonCycleTicks) / (float)moonCycleTicks * Mathf.PI * 2;
            skyMaterial.SetVector(_skyMoonLightId, new Vector4(Mathf.Sin(phaseAngle), 0, Mathf.Cos(phaseAngle), 0));
            float elevation = sunDirection.y;
            skyMaterial.SetColor(_skyHorizonId, horizon);
            skyMaterial.SetColor(_skyZenithId, Color.Lerp(horizon * .82f, biomeZenith * weatherSkyTint, weatherDirectSun));
            skyMaterial.SetColor(_skyCloudColorId, Color.Lerp(horizon, biomeClouds * weatherSkyTint, weatherDirectSun));
            skyMaterial.SetVector(_skySunId, sunDirection);
            skyMaterial.SetFloat(_skyDaylightId, biomeSkyDay);
            skyMaterial.SetFloat(_skyNightId, 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-.25f, .05f, elevation)));
            // Double precision keeps long-running colonies stable. Wrapped offsets are seamless.
            double seconds = Find.TickManager.TicksGame / 60.0;
            skyMaterial.SetVector(_skyCloudOffsetId, new Vector4((float)(seconds * .0006 % 1),
                (float)(seconds * .00025 % 1), (float)(seconds * -.00035 % 1), (float)(seconds * .00045 % 1)));
        }

        internal void Dispose()
        {
            if (_skyCloudNoise != null) UnityEngine.Object.Destroy(_skyCloudNoise);
            _skyCloudNoise = null;
            _orbitalClouds = null;
        }
    }
}
