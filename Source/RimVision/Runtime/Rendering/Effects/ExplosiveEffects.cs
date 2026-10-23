using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        sealed partial class CombatEffectsRenderer
        {
            static readonly HarmonyLib.AccessTools.FieldRef<Projectile, Vector3> _projectileDestination =
                HarmonyLib.AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
            static readonly HarmonyLib.AccessTools.FieldRef<Projectile, bool> _projectileLanded =
                HarmonyLib.AccessTools.FieldRefAccess<Projectile, bool>("landed");
            int _projectileObservedTick = -1;
            float _projectileObservedTime, _projectileTickFraction;

            float ProjectileTickFraction(int tick)
            {
                if (_projectileObservedTick != tick)
                {
                    _projectileObservedTick = tick;
                    _projectileObservedTime = Time.unscaledTime;
                    _projectileTickFraction = 0;
                }
                float rate = Rim3DMultiplayer.Active ? Rim3DMultiplayer.MinigameTickRate?.Invoke(_view.map) ?? 0 :
                    Find.TickManager.Paused ? 0 : Find.TickManager.TickRateMultiplier;
                float elapsed = Mathf.Max(0, Time.unscaledTime - _projectileObservedTime);
                _projectileObservedTime = Time.unscaledTime;
                if (rate > 0)
                    _projectileTickFraction = Mathf.Clamp01(_projectileTickFraction + elapsed * 60 * rate);
                return _projectileTickFraction;
            }
            sealed class ExplosionVisual
            {
                public GameObject Flash;
                public readonly List<GameObject> Clouds = new List<GameObject>();
                public readonly List<LineRenderer> Sparks = new List<LineRenderer>();
                public LineRenderer Wave;
                public Vector3 Center;
                public float Radius;
                public int StartTick;
                public bool Started;
                public bool SmokeOnly;
                public Color Color;
            }

            readonly Dictionary<Explosion, ExplosionVisual> _explosionVisuals = new Dictionary<Explosion, ExplosionVisual>();
            readonly List<Explosion> _expiredExplosions = new List<Explosion>();
            readonly Dictionary<ThingDef, Material> _projectileBodyMaterials = new Dictionary<ThingDef, Material>();
            readonly MaterialPropertyBlock _effectProperties = new MaterialPropertyBlock();
            Mesh _effectQuad;
            Material _smokeEffectMaterial, _flashEffectMaterial;
            Material _blastCloudMaterial, _blastTrailMaterial;
            static readonly int _blastAgeId = Shader.PropertyToID("_BlastAge");
            static readonly int _blastSeedId = Shader.PropertyToID("_BlastSeed");

            LineRenderer CreateBlastLine(string name, int points)
            {
                if (_blastTrailMaterial == null)
                {
                    var shader = ModAssets.LoadShader("Rim3D/CombatTrail");
                    if (shader == null) return null;
                    _blastTrailMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
                    _view.materials.Add(_blastTrailMaterial);
                }
                var obj = new GameObject(name) { layer = Layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(_view.root.transform, false);
                var line = obj.AddComponent<LineRenderer>();
                line.sharedMaterial = _blastTrailMaterial;
                line.useWorldSpace = false;
                line.positionCount = points;
                line.alignment = LineAlignment.View;
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.receiveShadows = false;
                return line;
            }

            Material EffectMaterial(Texture texture, Color color)
            {
                var effectShader = ModAssets.LoadShader("Rim3D/CombatBillboard");
                if (effectShader == null) return null;
                var material = new Material(effectShader) { hideFlags = HideFlags.DontSave, mainTexture = texture, color = color };
                _view.materials.Add(material);
                return material;
            }

            GameObject EffectCard(string name, Material material)
            {
                if (material == null) return null;
                if (_effectQuad == null)
                {
                    _effectQuad = new Mesh { name = "RimVision shared combat card", hideFlags = HideFlags.DontSave };
                    _effectQuad.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0),
                        new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0) };
                    _effectQuad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                    _effectQuad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    _effectQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 1.5f);
                    _view.meshes.Add(_effectQuad);
                }
                var obj = new GameObject(name) { layer = Layer, hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(_view.root.transform, false);
                obj.AddComponent<MeshFilter>().sharedMesh = _effectQuad;
                var renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                return obj;
            }

            void TintEffect(GameObject obj, Color color)
            {
                if (obj == null) return;
                _effectProperties.Clear();
                _effectProperties.SetColor("_Color", color);
                obj.GetComponent<MeshRenderer>().SetPropertyBlock(_effectProperties);
            }

            Material SmokeEffectMaterial()
            {
                if (_smokeEffectMaterial == null)
                    _smokeEffectMaterial = EffectMaterial(ModAssets.LoadTexture("textures/things/mote/Smoke"), Color.white);
                return _smokeEffectMaterial;
            }

            void UpdateProjectileBody(Projectile projectile, ShotVisual shot)
            {
                if (projectile.Destroyed || !projectile.Spawned || projectile.Map != _view.map) return;
                if (projectile.Launcher == null) return;
                bool overhead = projectile.def.projectile.flyOverhead;
                if (shot.Body == null)
                {
                    if (!shot.BodyOriginAssigned)
                    {
                        var origin = _projectileOrigin(projectile);
                        shot.Origin = new Vector3(origin.x, 0, origin.z);
                        shot.OriginHeight = !overhead && projectile.Launcher == _view.ControlledPawn && !_view.MortarAiming ? _view.CameraMapPosition.y : .95f;
                        shot.BodyOriginAssigned = true;
                    }
                    if (!_projectileBodyMaterials.TryGetValue(projectile.def, out var material))
                    {
                        var source = projectile.Graphic?.MatSingleFor(projectile);
                        if (source == null || source.mainTexture == null) return;
                        material = EffectMaterial(source.mainTexture, projectile.DrawColor);
                        _projectileBodyMaterials.Add(projectile.def, material);
                    }
                    shot.Body = EffectCard("RimVision projectile: " + projectile.def.defName, material);
                    if (shot.Body == null) return;
                }
                var head = projectile.ExactPosition;
                int tick = overhead && Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
                    ? Rim3DMultiplayer.MapTicks(_view.map) : Find.TickManager.TicksGame;
                if (shot.BodySampleTick < 0 || (head - shot.LastHead).sqrMagnitude >= .000001f)
                {
                    shot.BodyLanded = false;
                    shot.LastHead = head;
                    shot.BodySampleTick = tick;
                }
                else if (tick - shot.BodySampleTick > projectile.UpdateRateTicks)
                    shot.BodyLanded = true;
                if (overhead) shot.BodyLanded = _projectileLanded(projectile);
                var target = overhead ? _projectileDestination(projectile) : projectile.usedTarget.Cell.ToVector3Shifted();
                target.y = 0;
                var offset = head - shot.Origin;
                offset.y = 0;
                float distance = Mathf.Max(.1f, (target - shot.Origin).magnitude);
                float progress = Mathf.Clamp01(offset.magnitude / distance);
                if (overhead && !shot.BodyLanded)
                {
                    float betweenSamples = Mathf.Clamp(tick - shot.BodySampleTick + ProjectileTickFraction(tick), 0, projectile.UpdateRateTicks);
                    progress = Mathf.Clamp01(progress + betweenSamples * projectile.def.projectile.SpeedTilesPerTick / distance);
                    head = Vector3.Lerp(shot.Origin, target, progress);
                }
                float arc = Mathf.Min(projectile.def.projectile.arcHeightFactor, distance * .2f);
                if (overhead) arc = Mathf.Max(arc, distance * .25f);
                // Match the native flight duration and horizontal position. Grenades
                // keep their visible body on the ground while the native fuse runs.
                head.y = Mathf.Lerp(shot.OriginHeight, .12f, progress) + 4 * progress * (1 - progress) * arc;
                if (shot.BodyLanded) head.y = .12f;
                shot.Body.transform.localPosition = head;
                var size = projectile.Graphic.drawSize;
                float scale = projectile.def.projectile.arcHeightFactor >= 1 ? .4f : .25f;
                shot.Body.transform.localScale = new Vector3(Mathf.Clamp(size.x * scale, .15f, .7f),
                    Mathf.Clamp(size.y * scale, .15f, .7f), 1);
                float pixelSize = overhead && _view.camera.orthographic
                    ? _view.camera.orthographicSize * 2 / Mathf.Max(1, _view.camera.pixelHeight) : 0;
                if (overhead)
                {
                    var bodySize = shot.Body.transform.localScale;
                    bodySize.x = Mathf.Max(bodySize.x, pixelSize * 7);
                    bodySize.y = Mathf.Max(bodySize.y, pixelSize * 7);
                    shot.Body.transform.localScale = bodySize;
                    if (shot.Trail == null)
                    {
                        shot.Trail = CreateBlastLine("RimVision mortar shell trail", 12);
                        if (shot.Trail != null)
                        {
                            shot.Trail.startColor = new Color(1, .8f, .25f, 0);
                            shot.Trail.endColor = new Color(1, .95f, .6f, .9f);
                        }
                    }
                    if (shot.Trail != null)
                    {
                        shot.Trail.startWidth = Mathf.Max(.04f, pixelSize);
                        shot.Trail.endWidth = Mathf.Max(.1f, pixelSize * 2);
                        for (int i = 0; i < 12; i++)
                        {
                            float p = Mathf.Lerp(Mathf.Max(0, progress - .1f), progress, i / 11f);
                            var tail = Vector3.Lerp(shot.Origin, target, p);
                            tail.y = Mathf.Lerp(shot.OriginHeight, .12f, p) + 4 * p * (1 - p) * arc;
                            shot.Trail.SetPosition(i, tail);
                        }
                    }
                }
                var cell = head.ToIntVec3();
                bool visible = cell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(cell);
                shot.Body.SetActive(visible);
                if (overhead && shot.Trail != null) shot.Trail.gameObject.SetActive(visible && !shot.BodyLanded);
            }

            void UpdateExplosionVisuals()
            {
                _expiredExplosions.Clear();
                foreach (var entry in _explosionVisuals)
                {
                    var explosion = entry.Key;
                    var visual = entry.Value;
                    if (!visual.Started)
                    {
                        // Registration precedes initialization of the explosion fields.
                        if (explosion.radius <= 0) { if (explosion.Destroyed) _expiredExplosions.Add(explosion); continue; }
                        visual.Started = true;
                        visual.StartTick = Find.TickManager.TicksGame;
                        visual.Center = explosion.Position.ToVector3Shifted();
                        visual.Center.y = .35f;
                        visual.Radius = Mathf.Clamp(explosion.radius, .4f, 12);
                        visual.SmokeOnly = explosion.damType?.defName == "Smoke";
                        visual.Color = explosion.damType?.defName == "EMP" ? new Color(.3f, .65f, 1, 1) : new Color(1, .72f, .3f, 1);
                        if (explosion.doVisualEffects && !visual.SmokeOnly)
                        {
                            if (_flashEffectMaterial == null)
                            {
                                _flashEffectMaterial = EffectMaterial(null, Color.white);
                                if (_flashEffectMaterial != null)
                                {
                                    _flashEffectMaterial.SetFloat("_BlastMode", 2);
                                    _flashEffectMaterial.SetFloat("_DstBlend", (float)BlendMode.One);
                                }
                            }
                            visual.Flash = EffectCard("RimVision explosion flash", _flashEffectMaterial);
                            bool emp = explosion.damType?.defName == "EMP";
                            visual.Wave = CreateBlastLine("RimVision blast wave", 48);
                            if (visual.Wave != null)
                            {
                                visual.Wave.loop = true;
                                visual.Wave.transform.localPosition = new Vector3(visual.Center.x, .06f, visual.Center.z);
                                for (int i = 0; i < 48; i++)
                                {
                                    float angle = i * Mathf.PI * 2 / 48;
                                    visual.Wave.SetPosition(i, new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)));
                                }
                            }
                            if (!emp && _blastCloudMaterial == null)
                            {
                                _blastCloudMaterial = EffectMaterial(null, Color.white);
                                if (_blastCloudMaterial != null) _blastCloudMaterial.SetFloat("_BlastMode", 1);
                            }
                            int cloudCount = emp ? 0 : Mathf.Clamp(Mathf.CeilToInt(visual.Radius * 3) + 6, 8, 20);
                            for (int i = 0; i < cloudCount; i++)
                            {
                                var cloud = EffectCard("RimVision blast fireball", _blastCloudMaterial);
                                if (cloud != null) visual.Clouds.Add(cloud);
                            }
                            int sparkCount = emp ? 0 : Mathf.Clamp(Mathf.CeilToInt(visual.Radius * 4), 8, 24);
                            for (int i = 0; i < sparkCount; i++)
                            {
                                var spark = CreateBlastLine("RimVision blast spark", 2);
                                if (spark != null) visual.Sparks.Add(spark);
                            }
                        }
                    }
                    float age = (Find.TickManager.TicksGame - visual.StartTick) / 60f;
                    bool visible = explosion.Position.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(explosion.Position);
                    if (visual.Flash != null)
                    {
                        visual.Flash.SetActive(visible && age < .18f);
                        visual.Flash.transform.localPosition = visual.Center;
                        visual.Flash.transform.localScale = Vector3.one * visual.Radius * Mathf.Lerp(.8f, 2.4f, Mathf.Clamp01(age / .12f));
                        var color = Color.Lerp(Color.white, visual.Color, Mathf.Clamp01(age / .12f));
                        color.a = Mathf.Pow(Mathf.Clamp01(1 - age / .18f), 2);
                        TintEffect(visual.Flash, color);
                    }
                    if (visual.Wave != null)
                    {
                        float progress = Mathf.Clamp01(age / .35f);
                        float radius = visual.Radius * Mathf.Sqrt(progress);
                        visual.Wave.transform.localScale = new Vector3(radius, 1, radius);
                        visual.Wave.startWidth = visual.Wave.endWidth = Mathf.Lerp(.12f, .025f, progress);
                        var color = visual.Color;
                        color.a = (1 - progress) * .6f;
                        visual.Wave.startColor = visual.Wave.endColor = color;
                        visual.Wave.gameObject.SetActive(visible && age < .35f);
                    }
                    bool roofed = explosion.Position.InBounds(_view.map) && _view.map.roofGrid.Roofed(explosion.Position);
                    for (int i = 0; i < visual.Clouds.Count; i++)
                    {
                        var cloud = visual.Clouds[i];
                        float puffAge = Mathf.Max(0, age - (i % 4) * .025f);
                        float angle = i * 2.399963f + (explosion.thingIDNumber & 1023) * .37f;
                        float variation = .75f + Mathf.Sin(i * 7.31f) * .2f;
                        float expansion = visual.Radius * (1 - Mathf.Exp(-puffAge * 10)) *
                            (i % 3 == 0 ? .18f : .55f) * variation;
                        float size = visual.Radius * variation * (.25f + .6f * (1 - Mathf.Exp(-puffAge * 12)) + puffAge * .18f);
                        float height = size * .45f + puffAge * .55f * variation;
                        if (roofed) { size = Mathf.Min(size, 2.1f); height = Mathf.Min(height, 1.15f); }
                        cloud.transform.localPosition = visual.Center + new Vector3(Mathf.Cos(angle) * expansion,
                            height, Mathf.Sin(angle) * expansion);
                        cloud.transform.localScale = new Vector3(size, size * (1 + (i % 3) * .12f), 1);
                        _effectProperties.Clear();
                        _effectProperties.SetColor("_Color", new Color(1, 1, 1,
                            Mathf.Clamp01(puffAge / .035f) * Mathf.Clamp01((2.4f - puffAge) / .9f)));
                        _effectProperties.SetFloat(_blastAgeId, puffAge);
                        _effectProperties.SetFloat(_blastSeedId, (explosion.thingIDNumber & 1023) * .13f + i * 3.7f);
                        cloud.GetComponent<MeshRenderer>().SetPropertyBlock(_effectProperties);
                        cloud.SetActive(visible && age < 2.5f);
                    }
                    for (int i = 0; i < visual.Sparks.Count; i++)
                    {
                        var spark = visual.Sparks[i];
                        float variation = .6f + Mathf.Abs(Mathf.Sin(i * 7.31f)) * .4f;
                        float lifetime = .45f + variation * .45f;
                        if (!visible || age >= lifetime) { spark.gameObject.SetActive(false); continue; }
                        float angle = i * 2.399963f + (explosion.thingIDNumber & 1023) * .37f;
                        float speed = visual.Radius * (1.8f + variation);
                        var velocity = new Vector3(Mathf.Cos(angle) * speed, 2 + variation * 3, Mathf.Sin(angle) * speed);
                        float tailAge = Mathf.Max(0, age - .035f);
                        var head = visual.Center + velocity * age + Vector3.down * (5 * age * age);
                        var tail = visual.Center + velocity * tailAge + Vector3.down * (5 * tailAge * tailAge);
                        head.y = Mathf.Max(.08f, head.y); tail.y = Mathf.Max(.08f, tail.y);
                        if (roofed) { head.y = Mathf.Min(head.y, 1.8f); tail.y = Mathf.Min(tail.y, 1.8f); }
                        spark.SetPosition(0, tail); spark.SetPosition(1, head);
                        spark.startWidth = .015f; spark.endWidth = .035f;
                        float alpha = Mathf.Clamp01(1 - age / lifetime);
                        spark.startColor = new Color(1, .22f, .02f, alpha * .3f);
                        spark.endColor = new Color(1, .8f, .25f, alpha);
                        spark.gameObject.SetActive(true);
                    }
                    if (age >= 2.5f && (explosion.Destroyed || !explosion.Spawned)) _expiredExplosions.Add(explosion);
                }
                foreach (var explosion in _expiredExplosions)
                {
                    var visual = _explosionVisuals[explosion];
                    if (visual.Flash != null) UnityEngine.Object.Destroy(visual.Flash);
                    foreach (var cloud in visual.Clouds) UnityEngine.Object.Destroy(cloud);
                    foreach (var spark in visual.Sparks) UnityEngine.Object.Destroy(spark.gameObject);
                    if (visual.Wave != null) UnityEngine.Object.Destroy(visual.Wave.gameObject);
                    _explosionVisuals.Remove(explosion);
                }
            }

            void DisposeExplosiveEffects()
            {
                _projectileObservedTick = -1;
                _explosionVisuals.Clear(); _expiredExplosions.Clear();
                DisposeGasVisuals();
                _projectileBodyMaterials.Clear();
                _effectQuad = null; _smokeEffectMaterial = null; _flashEffectMaterial = null;
                _blastCloudMaterial = null; _blastTrailMaterial = null;
            }
        }
    }
}
