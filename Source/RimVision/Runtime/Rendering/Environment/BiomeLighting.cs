using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        private readonly BiomeLightingProfiles _biomeLightingProfiles = new BiomeLightingProfiles();
        Color biomeZenith, biomeClouds;
        float biomeSkyDay, biomeFogDensity, biomeFogFalloff, biomeShadowStrength;

        void UpdateBiomeLighting(out float daylight, out Color ambient, out Color horizon, out Vector3 skySunDirection)
        {
            skySunDirection = Vector3.up;
            if (SkyRenderer.IsSpaceMap(map))
            {
                daylight = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.04f, .72f, map.skyManager.CurSkyGlow));
                ambient = Color.Lerp(new Color(.10f, .13f, .20f), new Color(.38f, .43f, .52f), daylight);
                horizon = new Color(.002f, .003f, .008f);
                biomeZenith = horizon;
                biomeClouds = Color.clear;
                biomeSkyDay = 0;
                biomeFogDensity = biomeFogFalloff = 0;
                biomeShadowStrength = .65f;
                sunlight.color = new Color(1f, .97f, .92f);
                sunlight.intensity = Mathf.Lerp(.08f, 1.15f, daylight);
                return;
            }
            var biomeLight = _biomeLightingProfiles.GetProfile(map.Biome);
            float localTime = GenLocalDate.DayPercent(map);
            skySunDirection = SkyRenderer.SunDirection(map);
            float elevation = skySunDirection.y;
            float nightAmount = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-.25f, .05f, elevation));
            float nightBrightness = Mathf.Lerp(1, Rim3DSettings.Current.NightBrightness, nightAmount);
            var night = biomeLight.Night * nightBrightness;
            biomeSkyDay = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-.18f, .30f, elevation));
            float twilight = (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.02f, .4f, Mathf.Abs(elevation)))) * biomeSkyDay;
            // Preserve weather darkness, eclipses and sun blockers from the simulation.
            daylight = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.04f, .72f, map.skyManager.CurSkyGlow));
            float skyVisibility = Mathf.Clamp01(map.skyManager.CurSkyGlow / Mathf.Max(.05f, biomeSkyDay));
            biomeSkyDay *= skyVisibility;
            twilight *= skyVisibility;
            ambient = Color.Lerp(night, biomeLight.Ambient, daylight);
            horizon = Color.Lerp(night * .60f, biomeLight.Horizon, biomeSkyDay);
            horizon = Color.Lerp(horizon, biomeLight.Sunset, twilight * .65f);
            biomeZenith = Color.Lerp(night * .16f, biomeLight.Zenith, biomeSkyDay);
            biomeClouds = Color.Lerp(night * .45f, Color.Lerp(Color.white, biomeLight.Horizon, .22f), biomeSkyDay);
            biomeClouds = Color.Lerp(biomeClouds, biomeLight.Sunset, twilight * .55f);
            sunlight.color = Color.Lerp(new Color(.49f, .63f, .85f),
                Color.Lerp(biomeLight.Sun, biomeLight.Sunset, twilight * .55f), daylight);
            sunlight.intensity = Mathf.Lerp(.025f * nightBrightness, biomeLight.SunStrength, daylight);
            biomeShadowStrength = biomeLight.Shadow;
            float morningMist = twilight * (localTime < .5f ? 1 : .25f);
            biomeFogDensity = .008f + biomeLight.Mist * (1 + morningMist * .8f);
            biomeFogFalloff = .00007f + biomeLight.Mist * .007f;
        }
    }

    internal sealed class BiomeLightingProfiles
    {
        internal struct BiomeLightProfile
        {
            internal Color Ambient, Horizon, Zenith, Sun, Sunset, Night;
            internal float SunStrength, Shadow, Mist;

            internal BiomeLightProfile(Color ambient, Color horizon, Color zenith, Color sun,
                Color sunset, Color night, float strength, float shadow, float mist)
            {
                Ambient = ambient; Horizon = horizon; Zenith = zenith; Sun = sun;
                Sunset = sunset; Night = night; SunStrength = strength; Shadow = shadow; Mist = mist;
            }
        }

        private BiomeDef _biome;
        private bool _initialized;
        private BiomeLightProfile _profile;

        internal BiomeLightProfile GetProfile(BiomeDef biome)
        {
            if (!_initialized || _biome != biome)
            {
                _biome = biome;
                _profile = ResolveBiomeLighting(_biome);
                _initialized = true;
            }
            return _profile;
        }

        private static bool BiomeNameContains(string name, string token)
        {
            return name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static BiomeLightProfile ResolveBiomeLighting(BiomeDef biome)
        {
            // Resolved once per biome; modded definitions can share these families.
            string name = biome == null ? "" : biome.defName ?? "";
            bool tropical = BiomeNameContains(name, "Tropical") || BiomeNameContains(name, "Jungle");
            bool swamp = BiomeNameContains(name, "Swamp") || BiomeNameContains(name, "Bog") || BiomeNameContains(name, "Marsh");
            var sunset = new Color(.96f, .57f, .34f);
            var night = new Color(.075f, .105f, .17f);
            if (BiomeNameContains(name, "Underground") || BiomeNameContains(name, "Cave"))
                return new BiomeLightProfile(new Color(.22f, .25f, .29f), new Color(.12f, .15f, .19f),
                    new Color(.06f, .08f, .12f), new Color(.68f, .76f, .86f), sunset, night, .12f, .12f, .004f);
            if (tropical)
                return new BiomeLightProfile(swamp ? new Color(.36f, .45f, .40f) : new Color(.40f, .50f, .49f),
                    new Color(.68f, .79f, .73f), new Color(.20f, .47f, .61f), new Color(1f, .93f, .77f),
                    new Color(.98f, .63f, .39f), new Color(.065f, .115f, .14f), swamp ? .83f : .96f,
                    swamp ? .29f : .36f, swamp ? .012f : .006f);
            if (BiomeNameContains(name, "Ice") || BiomeNameContains(name, "Glacial"))
                return new BiomeLightProfile(new Color(.48f, .57f, .68f), new Color(.77f, .86f, .93f),
                    new Color(.27f, .48f, .72f), new Color(.94f, .97f, 1f), new Color(.88f, .65f, .64f),
                    new Color(.10f, .15f, .24f), .80f, .34f, .0015f);
            if (BiomeNameContains(name, "Tundra"))
                return new BiomeLightProfile(new Color(.43f, .49f, .58f), new Color(.74f, .79f, .85f),
                    new Color(.28f, .43f, .62f), new Color(.98f, .96f, .90f), new Color(.90f, .60f, .51f),
                    new Color(.09f, .13f, .21f), .87f, .36f, .0025f);
            if (swamp && BiomeNameContains(name, "Cold"))
                return new BiomeLightProfile(new Color(.36f, .43f, .47f), new Color(.65f, .73f, .76f),
                    new Color(.27f, .39f, .51f), new Color(.93f, .96f, .94f), new Color(.84f, .61f, .52f),
                    new Color(.075f, .115f, .16f), .72f, .25f, .014f);
            if (swamp)
                return new BiomeLightProfile(new Color(.38f, .43f, .41f), new Color(.66f, .73f, .69f),
                    new Color(.29f, .43f, .49f), new Color(.94f, .94f, .83f), new Color(.85f, .59f, .42f),
                    new Color(.075f, .105f, .13f), .76f, .26f, .013f);
            if (BiomeNameContains(name, "Boreal") || BiomeNameContains(name, "Taiga"))
                return new BiomeLightProfile(new Color(.38f, .46f, .53f), new Color(.68f, .78f, .84f),
                    new Color(.20f, .38f, .60f), new Color(1f, .95f, .86f), new Color(.95f, .63f, .41f),
                    new Color(.075f, .12f, .19f), .93f, .40f, .004f);
            if (BiomeNameContains(name, "Desert"))
            {
                bool extreme = BiomeNameContains(name, "Extreme");
                return new BiomeLightProfile(new Color(.50f, .47f, .41f), new Color(.87f, .79f, .65f),
                    new Color(.24f, .46f, .69f), new Color(1f, .94f, .80f), new Color(1f, .51f, .27f),
                    new Color(.085f, .10f, .18f), extreme ? 1.17f : 1.08f, .48f, extreme ? .0025f : .001f);
            }
            if (BiomeNameContains(name, "Arid") || BiomeNameContains(name, "Savanna"))
                return new BiomeLightProfile(new Color(.47f, .48f, .42f), new Color(.82f, .80f, .68f),
                    new Color(.22f, .45f, .67f), new Color(1f, .95f, .82f), new Color(.98f, .58f, .31f),
                    night, 1.02f, .43f, .002f);
            if (BiomeNameContains(name, "Ocean") || BiomeNameContains(name, "Lake"))
                return new BiomeLightProfile(new Color(.43f, .53f, .60f), new Color(.71f, .84f, .88f),
                    new Color(.17f, .43f, .70f), new Color(1f, .97f, .90f), sunset, night, .98f, .36f, .003f);
            return new BiomeLightProfile(new Color(.43f, .50f, .55f), new Color(.73f, .82f, .85f),
                new Color(.20f, .43f, .69f), new Color(1f, .96f, .87f), sunset, night, .98f, .40f, .002f);
        }
    }
}
