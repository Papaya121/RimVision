using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed partial class CombatEffectsRenderer
        {
            sealed class OrbitalVisual
            {
                public CompOrbitalBeam Beam;
                public LineRenderer Halo;
                public LineRenderer Core;
                public GameObject Impact;
            }

            static readonly AccessTools.FieldRef<CompOrbitalBeam, int> _orbitalStartTick =
                AccessTools.FieldRefAccess<CompOrbitalBeam, int>("startTick");

            static readonly AccessTools.FieldRef<CompOrbitalBeam, int> _orbitalDuration =
                AccessTools.FieldRefAccess<CompOrbitalBeam, int>("totalDuration");

            static readonly AccessTools.FieldRef<CompOrbitalBeam, int> _orbitalFadeOut =
                AccessTools.FieldRefAccess<CompOrbitalBeam, int>("fadeOutDuration");

            static readonly AccessTools.FieldRef<CompOrbitalBeam, float> _orbitalAngle =
                AccessTools.FieldRefAccess<CompOrbitalBeam, float>("angle");

            static readonly AccessTools.FieldRef<Bombardment, List<Bombardment.BombardmentProjectile>> _bombardmentProjectiles =
                AccessTools.FieldRefAccess<Bombardment, List<Bombardment.BombardmentProjectile>>("projectiles");

            static readonly AccessTools.FieldRef<Bombardment.BombardmentProjectile, int> _bombardmentLifetime =
                AccessTools.FieldRefAccess<Bombardment.BombardmentProjectile, int>("maxLifeTime");
            readonly Dictionary<OrbitalStrike, OrbitalVisual> _orbitalVisuals = new Dictionary<OrbitalStrike, OrbitalVisual>();
            readonly List<OrbitalStrike> _removedOrbitalStrikes = new List<OrbitalStrike>();
            readonly Dictionary<Bombardment.BombardmentProjectile, LineRenderer> _orbitalProjectiles =
                new Dictionary<Bombardment.BombardmentProjectile, LineRenderer>();
            readonly HashSet<Bombardment.BombardmentProjectile> _visibleOrbitalProjectiles =
                new HashSet<Bombardment.BombardmentProjectile>();
            readonly List<Bombardment.BombardmentProjectile> _removedOrbitalProjectiles =
                new List<Bombardment.BombardmentProjectile>();
            readonly Stack<LineRenderer> _orbitalProjectilePool = new Stack<LineRenderer>();
            Material _orbitalImpactMaterial;

            void TrackOrbitalThing(Thing thing)
            {
                if (thing is OrbitalStrike strike && !_orbitalVisuals.ContainsKey(strike))
                    _orbitalVisuals.Add(strike, new OrbitalVisual());
            }

            void UpdateOrbitalVisuals()
            {
                int tick = Find.TickManager.TicksGame;
                _removedOrbitalStrikes.Clear();
                _visibleOrbitalProjectiles.Clear();
                foreach (var entry in _orbitalVisuals)
                {
                    var strike = entry.Key;
                    var visual = entry.Value;
                    if (strike.Destroyed || !strike.Spawned || strike.Map != _view.map)
                    {
                        if (visual.Halo != null) UnityEngine.Object.Destroy(visual.Halo.gameObject);
                        if (visual.Core != null) UnityEngine.Object.Destroy(visual.Core.gameObject);
                        if (visual.Impact != null) UnityEngine.Object.Destroy(visual.Impact);
                        _removedOrbitalStrikes.Add(strike);
                        continue;
                    }
                    if (strike is Bombardment bombardment) UpdateOrbitalProjectiles(bombardment);
                    if (visual.Beam == null) visual.Beam = strike.GetComp<CompOrbitalBeam>();
                    var beam = visual.Beam;
                    int age = beam != null ? tick - _orbitalStartTick(beam) : -1;
                    int remaining = beam != null ? _orbitalDuration(beam) - age : 0;
                    bool visible = beam != null && age >= 0 && remaining > 0 && strike.Position.InBounds(_view.map) &&
                        (strike.def.seeThroughFog || !_view.map.fogGrid.IsFogged(strike.Position));
                    if (!visible)
                    {
                        if (visual.Halo != null) visual.Halo.gameObject.SetActive(false);
                        if (visual.Core != null) visual.Core.gameObject.SetActive(false);
                        if (visual.Impact != null) visual.Impact.SetActive(false);
                        continue;
                    }
                    if (visual.Halo == null) visual.Halo = CreateBlastLine("RimVision orbital beam halo", 2);
                    if (visual.Core == null) visual.Core = CreateBlastLine("RimVision orbital beam core", 2);
                    if (visual.Impact == null)
                    {
                        if (_orbitalImpactMaterial == null)
                        {
                            _orbitalImpactMaterial = EffectMaterial(null, Color.white);
                            if (_orbitalImpactMaterial != null)
                            {
                                _orbitalImpactMaterial.SetFloat("_BlastMode", 2);
                                _orbitalImpactMaterial.SetFloat("_DstBlend", (float)BlendMode.One);
                            }
                        }
                        visual.Impact = EffectCard("RimVision orbital beam impact", _orbitalImpactMaterial);
                    }
                    float fadeIn = Mathf.Clamp01(age / 10f);
                    float alpha = (.975f + Mathf.Sin(age * .3f) * .025f) *
                        Mathf.Clamp01(remaining / (float)Mathf.Max(1, _orbitalFadeOut(beam)));
                    float height = Mathf.Max(80, _view.map.Size.z);
                    var impact = strike.DrawPos;
                    impact.y = .12f;
                    var top = impact + new Vector3(Mathf.Tan(_orbitalAngle(beam) * Mathf.Deg2Rad) * height, height, 0);
                    var bottom = Vector3.Lerp(top, impact, fadeIn);
                    float width = Mathf.Max(.05f, beam.Props.width);
                    var color = beam.Props.color;
                    color.a *= alpha;
                    SetOrbitalLine(visual.Halo, top, bottom, width, color);
                    var coreColor = Color.Lerp(color, Color.white, .8f);
                    coreColor.a = color.a;
                    SetOrbitalLine(visual.Core, top, bottom, width * .28f, coreColor);
                    if (visual.Impact != null)
                    {
                        visual.Impact.transform.localPosition = impact + Vector3.up * Mathf.Min(width * .12f, .6f);
                        visual.Impact.transform.localScale = Vector3.one * width * 1.8f;
                        color.a *= fadeIn;
                        TintEffect(visual.Impact, color);
                        visual.Impact.SetActive(true);
                    }
                }
                foreach (var strike in _removedOrbitalStrikes) _orbitalVisuals.Remove(strike);
                _removedOrbitalProjectiles.Clear();
                foreach (var entry in _orbitalProjectiles)
                    if (!_visibleOrbitalProjectiles.Contains(entry.Key)) _removedOrbitalProjectiles.Add(entry.Key);
                foreach (var projectile in _removedOrbitalProjectiles)
                {
                    var line = _orbitalProjectiles[projectile];
                    line.gameObject.SetActive(false);
                    _orbitalProjectilePool.Push(line);
                    _orbitalProjectiles.Remove(projectile);
                }
            }

            static void SetOrbitalLine(LineRenderer line, Vector3 start, Vector3 end, float width, Color color)
            {
                if (line == null) return;
                line.SetPosition(0, start); line.SetPosition(1, end);
                line.startWidth = line.endWidth = width;
                line.startColor = line.endColor = color;
                line.gameObject.SetActive(true);
            }

            void UpdateOrbitalProjectiles(Bombardment bombardment)
            {
                var projectiles = _bombardmentProjectiles(bombardment);
                if (projectiles == null) return;
                foreach (var projectile in projectiles)
                {
                    if (projectile.LifeTime <= 0 || !projectile.targetCell.InBounds(_view.map) ||
                        !bombardment.def.seeThroughFog && _view.map.fogGrid.IsFogged(projectile.targetCell)) continue;
                    _visibleOrbitalProjectiles.Add(projectile);
                    if (!_orbitalProjectiles.TryGetValue(projectile, out var line))
                    {
                        line = _orbitalProjectilePool.Count > 0 ? _orbitalProjectilePool.Pop() :
                            CreateBlastLine("RimVision orbital bombardment projectile", 2);
                        if (line == null) continue;
                        _orbitalProjectiles.Add(projectile, line);
                    }
                    var head = projectile.targetCell.ToVector3Shifted();
                    head.y = .12f + 60 * Mathf.Clamp01(projectile.LifeTime / (float)Mathf.Max(1, _bombardmentLifetime(projectile)));
                    line.SetPosition(0, head + Vector3.up * 5);
                    line.SetPosition(1, head);
                    line.startWidth = .04f; line.endWidth = .35f;
                    line.startColor = new Color(1, .25f, .04f, .08f);
                    line.endColor = new Color(1, .85f, .5f, 1);
                    line.gameObject.SetActive(true);
                }
            }

            void DisposeOrbitalVisuals()
            {
                _orbitalVisuals.Clear(); _removedOrbitalStrikes.Clear();
                _orbitalProjectiles.Clear(); _visibleOrbitalProjectiles.Clear(); _removedOrbitalProjectiles.Clear();
                _orbitalProjectilePool.Clear(); _orbitalImpactMaterial = null;
            }
        }
    }
}
