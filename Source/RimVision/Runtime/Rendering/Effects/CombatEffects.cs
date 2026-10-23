using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    [HarmonyPatch(typeof(FleckMaker), nameof(FleckMaker.ThrowHorseshoe))]
    static class HorseshoeThrowVisualPatch
    {
        static void Postfix(Pawn __0, IntVec3 __1) =>
            Rim3DPreview.Instance?.ActiveWindow?.View?.StartHorseshoeThrow(__0, __1);
    }

    public sealed partial class MapView
    {
        readonly CombatEffectsRenderer _combatEffects;

        internal void StartHorseshoeThrow(Pawn pawn, IntVec3 target)
        {
            if (simulationOnly || root == null || pawn.Map != map || !target.InBounds(map) ||
                map.fogGrid.IsFogged(pawn.Position) || map.fogGrid.IsFogged(target)) return;
            _combatEffects.StartHorseshoeThrow(pawn, target);
        }

        void TrackCombatThing(Thing thing) => _combatEffects.TrackCombatThing(thing);

        void UpdateCombatEffects() => _combatEffects.UpdateCombatEffects();

        void DisposeCombatEffects()
        {
            _combatEffects?.DisposeCombatEffects();
            lastPlayerShot = -100;
            lastConfirmedHit = -100;
        }

        float lastPlayerShot = -100;

        public void DrawCombatFeedback(Rect viewport)
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            float strength = Mathf.Clamp01(1 - (Time.unscaledTime - lastPlayerShot) / .14f);
            var center = viewport.center;
            if (strength > 0)
            {
                float spread = 7 + strength * 6;
                var color = new Color(1, .8f, .3f, strength);
                Widgets.DrawBoxSolid(new Rect(center.x - spread - 4, center.y - 1, 4, 2), color);
                Widgets.DrawBoxSolid(new Rect(center.x + spread, center.y - 1, 4, 2), color);
                Widgets.DrawBoxSolid(new Rect(center.x - 1, center.y - spread - 4, 2, 4), color);
                Widgets.DrawBoxSolid(new Rect(center.x - 1, center.y + spread, 2, 4), color);
            }
            float hitStrength = Mathf.Clamp01(1 - (Time.unscaledTime - lastConfirmedHit) / .28f);
            if (hitStrength <= 0) return;
            var hitColor = lastHitKilled ? new Color(1, .25f, .2f, hitStrength) : new Color(1, 1, 1, hitStrength);
            var oldMatrix = GUI.matrix;
            try
            {
                GUIUtility.RotateAroundPivot(45, oldMatrix.MultiplyPoint3x4(center));
                Widgets.DrawBoxSolid(new Rect(center.x - 13, center.y - 1, 7, 2), hitColor);
                Widgets.DrawBoxSolid(new Rect(center.x + 6, center.y - 1, 7, 2), hitColor);
                Widgets.DrawBoxSolid(new Rect(center.x - 1, center.y - 13, 2, 7), hitColor);
                Widgets.DrawBoxSolid(new Rect(center.x - 1, center.y + 6, 2, 7), hitColor);
            }
            finally { GUI.matrix = oldMatrix; }
        }

        sealed partial class CombatEffectsRenderer
        {
            readonly MapView _view;

            internal CombatEffectsRenderer(MapView view)
            {
                _view = view;
            }

            sealed class HorseshoeVisual
            {
                public GameObject Object;
                public Vector3 Origin, Target;
                public int StartTick;
                public float Duration;
            }

            readonly List<HorseshoeVisual> _horseshoes = new List<HorseshoeVisual>();
            readonly Stack<HorseshoeVisual> _horseshoePool = new Stack<HorseshoeVisual>();
            Material _horseshoeMaterial;

            internal void StartHorseshoeThrow(Pawn pawn, IntVec3 target)
            {
                if (_horseshoeMaterial == null)
                {
                    var texture = ModAssets.LoadTexture("textures/things/mote/Horseshoe");
                    if (texture == null) return;
                    _horseshoeMaterial = _view.NewMaterial(texture, Color.white, false);
                }
                var visual = _horseshoePool.Count > 0 ? _horseshoePool.Pop() :
                    new HorseshoeVisual { Object = EffectCard("RimVision horseshoe", _horseshoeMaterial) };
                if (visual.Object == null) return;
                visual.Origin = pawn.DrawPos;
                visual.Target = target.ToVector3Shifted();
                var direction = visual.Target - visual.Origin;
                direction.y = 0;
                visual.Origin += direction.normalized * .35f;
                visual.Origin.y = .95f * PawnVisualScale;
                visual.Target.y = .08f;
                visual.Duration = Mathf.Max(.25f, direction.magnitude / 5f);
                visual.StartTick = HorseshoeTick;
                visual.Object.transform.localScale = Vector3.one * .25f;
                visual.Object.transform.localPosition = visual.Origin;
                visual.Object.SetActive(true);
                _horseshoes.Add(visual);
            }

            void UpdateHorseshoeThrows()
            {
                if (_horseshoes.Count == 0) return;
                int tick = HorseshoeTick;
                for (int i = _horseshoes.Count - 1; i >= 0; i--)
                {
                    var visual = _horseshoes[i];
                    float age = (tick - visual.StartTick) / (60f * visual.Duration);
                    if (age >= 1)
                    {
                        visual.Object.SetActive(false);
                        _horseshoePool.Push(visual);
                        _horseshoes.RemoveAt(i);
                        continue;
                    }
                    var position = Vector3.Lerp(visual.Origin, visual.Target, age);
                    position.y += 4f * age * (1f - age) * .8f;
                    var cell = position.ToIntVec3();
                    visual.Object.SetActive(cell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(cell));
                    visual.Object.transform.localPosition = position;
                    visual.Object.transform.rotation = _view.camera.transform.rotation * Quaternion.Euler(0, 0, age * 540f);
                }
            }

            int HorseshoeTick => Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
                ? Rim3DMultiplayer.MapTicks(_view.map) : Find.TickManager.TicksGame;

            sealed class ShotVisual
            {
                public LineRenderer Trail;
                public Vector3 Origin;
                public Vector3 MuzzleOffset;
                public Vector3 LastHead;
                public float OriginHeight;
                public float TracerDistance;
                public float TargetHeight;
                public bool Ended;
                public float EndTime;
                public GameObject Body;
                public bool BodyOriginAssigned;
                public bool BodyLanded;
                public int BodySampleTick = -1;
                public bool MuzzleStarted;
            }

            sealed class MuzzleVisual
            {
                public GameObject Object;
                public MeshRenderer Renderer;
                public Thing Launcher;
                public float StartTime;
                public float Size;
            }

            readonly List<MuzzleVisual> _muzzleFlashes = new List<MuzzleVisual>();

            static readonly AccessTools.FieldRef<Projectile, Vector3> _projectileOrigin =
                AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
            readonly Stack<MuzzleVisual> _muzzleFlashPool = new Stack<MuzzleVisual>();
            Material _muzzleFlashMaterial;
            readonly Dictionary<Projectile, ShotVisual> _shots = new Dictionary<Projectile, ShotVisual>();
            readonly List<Projectile> _expiredShots = new List<Projectile>();
            Material _tracerMaterial;

            internal void TrackCombatThing(Thing thing)
            {
                TrackGeyserThing(thing);
                TrackOrbitalThing(thing);
                if (thing is Projectile projectile && !_shots.ContainsKey(projectile))
                    _shots.Add(projectile, new ShotVisual());
                if (thing is Explosion explosion && !_explosionVisuals.ContainsKey(explosion))
                    _explosionVisuals.Add(explosion, new ExplosionVisual());
            }

            internal void SetProjectileOrigin(Projectile projectile, Vector3 start)
            {
                if (!_shots.TryGetValue(projectile, out var visual)) return;
                visual.Origin = new Vector3(start.x, 0, start.z);
                visual.OriginHeight = start.y;
                visual.BodyOriginAssigned = true;
            }

            internal void UpdateCombatEffects()
            {
                UpdateHorseshoeThrows();
                UpdateExplosionVisuals();
                UpdateGasVisuals();
                UpdateGeyserVisuals();
                UpdateOrbitalVisuals();
                _expiredShots.Clear();
                foreach (var entry in _shots)
                {
                    var projectile = entry.Key;
                    var shot = entry.Value;
                    if (!shot.MuzzleStarted && projectile.Launcher != null)
                    {
                        shot.MuzzleStarted = true;
                        StartMuzzleFlash(projectile);
                    }
                    if (!(projectile is Bullet) || projectile.def.projectile.explosionRadius > 0 || projectile.def.projectile.flyOverhead)
                    {
                        UpdateProjectileBody(projectile, shot);
                        if (projectile.Destroyed || !projectile.Spawned || projectile.Map != _view.map)
                            _expiredShots.Add(projectile);
                        continue;
                    }
                    if (shot.Trail == null)
                    {
                        // ThingGrid registration occurs before Projectile.Launch.
                        // Wait until the real launcher and trajectory are assigned.
                        if (projectile.Launcher == null)
                        {
                            if (projectile.Destroyed) _expiredShots.Add(projectile);
                            continue;
                        }
                        var origin = _projectileOrigin(projectile);
                        shot.Origin = projectile is FPVManualBullet manual ? manual.FlightOrigin : new Vector3(origin.x, 0, origin.z);
                        shot.OriginHeight = projectile.Launcher == _view.ControlledPawn ? _view.CameraMapPosition.y :
                            projectile.Launcher is Pawn ? .95f : .75f;
                        if (projectile.Launcher == _view.ControlledPawn && _view.FirstPerson)
                        {
                            _view.UpdateFirstPersonWeapon();
                            if (_view.firstPersonWeaponVisible)
                                shot.MuzzleOffset = _view.CameraMapPosition + _view.camera.transform.rotation * _view.firstPersonMuzzle -
                                    (shot.Origin + (projectile is FPVManualBullet ? Vector3.zero : Vector3.up * shot.OriginHeight));
                        }
                        else if (projectile.Launcher is Pawn shooter && _view._heldThingPresentation.TryGetWeaponMuzzle(shooter, out var muzzle))
                            shot.MuzzleOffset = muzzle - (shot.Origin +
                                (projectile is FPVManualBullet ? Vector3.zero : Vector3.up * shot.OriginHeight));
                        shot.LastHead = shot.Origin + (projectile is FPVManualBullet ? Vector3.zero : Vector3.up * shot.OriginHeight);
                        var target = projectile.usedTarget.HasThing && !projectile.usedTarget.Thing.Destroyed
                            ? projectile.usedTarget.Thing.DrawPos : projectile.usedTarget.Cell.ToVector3Shifted();
                        var direction = projectile.ExactRotation * Vector3.forward;
                        if (!(projectile is FPVManualBullet)) direction.y = 0;
                        shot.TracerDistance = Mathf.Max(.1f, Vector3.Dot(target - shot.Origin, direction.normalized));
                        shot.TargetHeight = projectile.usedTarget.Thing is Pawn targetPawn ? (targetPawn.Downed ? .25f : .95f) : .12f;
                        if (_tracerMaterial == null)
                        {
                            var trailShader = ModAssets.LoadShader("Rim3D/CombatTrail");
                            if (trailShader == null) trailShader = Shader.Find("Sprites/Default");
                            if (trailShader == null) { _expiredShots.Add(projectile); continue; }
                            _tracerMaterial = new Material(trailShader) { hideFlags = HideFlags.DontSave };
                            _view.materials.Add(_tracerMaterial);
                        }
                        var obj = new GameObject("RimVision bullet tracer") { layer = Layer, hideFlags = HideFlags.DontSave };
                        obj.transform.SetParent(_view.root.transform, false);
                        shot.Trail = obj.AddComponent<LineRenderer>();
                        shot.Trail.sharedMaterial = _tracerMaterial;
                        shot.Trail.useWorldSpace = false;
                        shot.Trail.positionCount = 2;
                        shot.Trail.alignment = LineAlignment.View;
                        shot.Trail.textureMode = LineTextureMode.Stretch;
                        shot.Trail.startWidth = .012f;
                        shot.Trail.endWidth = .045f;
                        shot.Trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        shot.Trail.receiveShadows = false;
                        var originCell = shot.Origin.ToIntVec3();
                        if (projectile.Launcher == _view.ControlledPawn && _view.FirstPerson && originCell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(originCell))
                            _view.lastPlayerShot = Time.unscaledTime;
                    }

                    if (!shot.Ended)
                    {
                        var head = projectile.ExactPosition;
                        var direction = projectile.ExactRotation * Vector3.forward;
                        if (!(projectile is FPVManualBullet)) direction.y = 0;
                        direction.Normalize();
                        float distance = shot.TracerDistance;
                        float travel = Mathf.Max(0, Vector3.Dot(head - shot.Origin, direction));
                        float targetHeight = shot.TargetHeight;
                        if (!(projectile is FPVManualBullet))
                            head.y = Mathf.Lerp(shot.OriginHeight, targetHeight, Mathf.Clamp01(travel / distance));
                        float tailLength = Mathf.Min(travel, Mathf.Clamp(projectile.def.projectile.SpeedTilesPerTick * 5, .5f, 2.2f));
                        var tail = head - direction * tailLength;
                        if (!(projectile is FPVManualBullet))
                            tail.y = Mathf.Lerp(shot.OriginHeight, targetHeight, Mathf.Clamp01((travel - tailLength) / distance));
                        head += shot.MuzzleOffset * (1 - Mathf.Clamp01(travel / distance));
                        tail += shot.MuzzleOffset * (1 - Mathf.Clamp01((travel - tailLength) / distance));
                        shot.Trail.SetPosition(0, tail);
                        shot.Trail.SetPosition(1, head);
                        shot.LastHead = head;
                        var headCell = head.ToIntVec3();
                        var originCell = shot.Origin.ToIntVec3();
                        bool visible = headCell.InBounds(_view.map) && originCell.InBounds(_view.map) &&
                            !_view.map.fogGrid.IsFogged(headCell) && !_view.map.fogGrid.IsFogged(originCell);
                        shot.Trail.gameObject.SetActive(visible);
                        if (projectile.Destroyed || !projectile.Spawned || projectile.Map != _view.map)
                        {
                            shot.Ended = true;
                            shot.EndTime = Time.unscaledTime;
                        }
                    }
                    float alpha = shot.Ended ? Mathf.Clamp01(1 - (Time.unscaledTime - shot.EndTime) / .12f) : 1;
                    shot.Trail.startColor = new Color(1, .4f, .08f, alpha * .25f);
                    shot.Trail.endColor = new Color(1, .92f, .55f, alpha);
                    if (shot.Ended && alpha <= 0) _expiredShots.Add(projectile);
                }
                foreach (var projectile in _expiredShots)
                {
                    var shot = _shots[projectile];
                    if (shot.Trail != null)
                    {
                        UnityEngine.Object.Destroy(shot.Trail.gameObject);
                    }
                    if (shot.Body != null) UnityEngine.Object.Destroy(shot.Body);
                    _shots.Remove(projectile);
                }
                UpdateMuzzleFlashes();
            }

            void StartMuzzleFlash(Projectile projectile)
            {
                var launcher = projectile.Launcher;
                if (launcher == _view.ControlledPawn && _view.FirstPerson) return;
                var verb = launcher is Pawn pawn ? pawn.equipment?.Primary?.TryGetComp<CompEquippable>()?.PrimaryVerb ?? pawn.CurrentEffectiveVerb :
                    launcher is Building_TurretGun turret ? turret.AttackVerb : null;
                float scale = verb?.verbProps.muzzleFlashScale ?? 0;
                if (scale <= 0) return;
                var cell = launcher.Position;
                if (!cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(cell)) return;
                if (_muzzleFlashMaterial == null)
                {
                    _muzzleFlashMaterial = EffectMaterial(ModAssets.LoadTexture("textures/things/mote/ShotFlash"), Color.white);
                    if (_muzzleFlashMaterial != null) _muzzleFlashMaterial.SetFloat("_AlphaBoost", 8);
                }
                if (_muzzleFlashMaterial == null) return;

                MuzzleVisual flash = null;
                for (int i = 0; i < _muzzleFlashes.Count; i++)
                    if (_muzzleFlashes[i].Launcher == launcher) { flash = _muzzleFlashes[i]; break; }
                if (flash == null)
                {
                    if (_muzzleFlashPool.Count > 0) flash = _muzzleFlashPool.Pop();
                    else
                    {
                        var obj = EffectCard("RimVision muzzle flash", _muzzleFlashMaterial);
                        if (obj == null) return;
                        flash = new MuzzleVisual { Object = obj, Renderer = obj.GetComponent<MeshRenderer>() };
                    }
                    _muzzleFlashes.Add(flash);
                }
                var origin = _projectileOrigin(projectile);
                origin.y = launcher is Pawn ? .95f : .75f;
                var direction = projectile.ExactRotation * Vector3.forward;
                direction.y = 0;
                direction.Normalize();
                var muzzle = origin + direction * .65f;
                if (launcher is Pawn shooter && _view._heldThingPresentation.TryGetWeaponMuzzle(shooter, out var weaponMuzzle))
                    muzzle = weaponMuzzle;
                flash.Launcher = launcher;
                flash.StartTime = Time.unscaledTime;
                flash.Size = Mathf.Clamp(scale * .1f, .4f, 1.5f);
                flash.Object.transform.localPosition = muzzle;
                flash.Object.SetActive(true);
            }

            void UpdateMuzzleFlashes()
            {
                for (int i = _muzzleFlashes.Count - 1; i >= 0; i--)
                {
                    var flash = _muzzleFlashes[i];
                    float age = (Time.unscaledTime - flash.StartTime) / .12f;
                    if (age >= 1)
                    {
                        flash.Object.SetActive(false);
                        flash.Launcher = null;
                        _muzzleFlashPool.Push(flash);
                        _muzzleFlashes.RemoveAt(i);
                        continue;
                    }
                    var cell = flash.Object.transform.localPosition.ToIntVec3();
                    flash.Object.SetActive(cell.InBounds(_view.map) && !_view.map.fogGrid.IsFogged(cell));
                    flash.Object.transform.localScale = Vector3.one * flash.Size * Mathf.Lerp(1, .55f, age);
                    _effectProperties.Clear();
                    _effectProperties.SetColor("_Color", new Color(1, .85f, .5f, 1 - age));
                    flash.Renderer.SetPropertyBlock(_effectProperties);
                }
            }

            internal void DisposeCombatEffects()
            {
                DisposeExplosiveEffects();
                DisposeOrbitalVisuals();
                _horseshoes.Clear(); _horseshoePool.Clear(); _horseshoeMaterial = null;
                _muzzleFlashes.Clear();
                _muzzleFlashPool.Clear();
                _muzzleFlashMaterial = null;
                _shots.Clear(); _expiredShots.Clear();
            }
        }
    }
}
