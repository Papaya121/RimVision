using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Recovery.Rim3D
{
    // First-person shells must not drop to the native offscreen 15-tick rate.
    // All impact, fuse and explosion behavior remains in Projectile_Explosive.
    public sealed class FPVNativeExplosive : Projectile_Explosive
    {
        public override int UpdateRateTicks => 1;
    }

    // Native bullet damage/armor/shields, with camera-directed 3D flight instead
    // of ShotReport accuracy rolls or cell-center aiming.
    public sealed partial class FPVManualBullet : Bullet
    {
        Vector3 flightPosition;
        Vector3 flightDirection = Vector3.forward;
        float remainingDistance;
        readonly HashSet<Thing> candidates = new HashSet<Thing>();
        readonly List<Thing> orderedCandidates = new List<Thing>();
        readonly HashSet<IntVec3> visitedCells = new HashSet<IntVec3>();
        public Func<Pawn, float> PawnHeight;
        public Func<Pawn, Ray, float?> PawnRayHit;
        public Func<Building_Bed, Ray, float?> BedRayHit;
        public Action<Thing, bool> ConfirmHit;
        public bool DeterministicFlight;
        public Pawn AimPawn;
        public float AimPawnHeight;
        public override Vector3 ExactPosition => flightPosition;
        public Vector3 FlightOrigin => origin;
        public override int UpdateRateTicks => 1;
        public override Quaternion ExactRotation => Quaternion.LookRotation(flightDirection);

        public void Configure(Vector3 position, Vector3 direction, float range)
        {
            flightPosition = position;
            flightDirection = direction.normalized;
            remainingDistance = range;
            origin = position;
            destination = position + flightDirection * range;
        }

        protected override void TickInterval(int delta)
        {
            if (!Spawned || Destroyed) return;
            if (TickSpatialFlight(delta)) return;
            float distance = Mathf.Min(remainingDistance, def.projectile.SpeedTilesPerTick * delta);
            var next = flightPosition + flightDirection * distance;
            // Keep RimWorld's projectile interception shields active.
            foreach (var thing in Map.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor))
                if (thing.TryGetComp<CompProjectileInterceptor>()?.CheckIntercept(this, flightPosition, next) == true)
                { Impact(null, true); return; }
            float closest = distance;
            bool hitGround = false;
            if (flightDirection.y < 0 && next.y <= 0)
            {
                closest = Mathf.Max(0, -flightPosition.y / flightDirection.y);
                hitGround = true;
            }
            var ray = new Ray(flightPosition, flightDirection);
            candidates.Clear();
            visitedCells.Clear();
            for (float step = 0; step <= distance + .35f; step += .35f)
            {
                var cell = ray.GetPoint(Mathf.Min(step, distance)).ToIntVec3();
                for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
                {
                    var nearby = cell + new IntVec3(x, 0, z);
                    if (!nearby.InBounds(Map) || !visitedCells.Add(nearby)) continue;
                    foreach (var thing in nearby.GetThingList(Map)) candidates.Add(thing);
                }
            }
            Thing hit = null;
            // Stable tie-breaking is necessary for stacked/overlapping hitboxes.
            orderedCandidates.Clear();
            orderedCandidates.AddRange(candidates);
            orderedCandidates.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            foreach (var thing in orderedCandidates)
            {
                if (thing == launcher || !thing.Spawned || thing.Destroyed || thing is Projectile || thing is Blueprint || thing is Frame) continue;
                if (!(thing is Pawn) && thing.def.category != ThingCategory.Building && thing.def.category != ThingCategory.Plant) continue;
                if (MapView.IsGroundSpot(thing.def)) continue;
                if (thing is Building_Door door && door.Open) continue;
                if (!DeterministicFlight && thing is Building_Bed bedTarget && BedRayHit != null)
                {
                    var bedHit = BedRayHit(bedTarget, ray);
                    if (bedHit.HasValue && bedHit.Value <= closest)
                    { closest = bedHit.Value; hit = thing; hitGround = false; }
                    continue;
                }
                if (!DeterministicFlight && thing is Pawn silhouettePawn && PawnRayHit != null)
                {
                    var silhouetteHit = PawnRayHit(silhouettePawn, ray);
                    if (silhouetteHit.HasValue && silhouetteHit.Value <= closest)
                    { closest = silhouetteHit.Value; hit = thing; hitGround = false; }
                    continue;
                }
                var center = thing.TrueCenter();
                if (DeterministicFlight && thing is Pawn positionPawn)
                    center = Rim3DMultiplayer.GetPosition?.Invoke(positionPawn) ?? positionPawn.Position.ToVector3Shifted();
                var size = thing.RotatedSize;
                float height = thing is Pawn pawn ? (pawn.GetPosture() != PawnPosture.Standing || pawn.Downed ? .35f :
                    CollisionHeight(pawn)) :
                    thing is Plant plant ? Mathf.Clamp((DeterministicFlight ? thing.def.graphicData?.drawSize.y ?? 1 : thing.Graphic?.drawSize.y ?? 1) *
                        (thing.def.plant.IsTree ? 3.5f : .4f) * Mathf.Lerp(.35f, 1, plant.Growth), .08f, 9) :
                    thing is Building_Door ? 1.7f : thing.def.Fillage == FillCategory.Full || thing.def.mineable ? 2 : .65f;
                center.y = height * .5f;
                float width = thing is Pawn ? .8f : thing is Plant ? .35f : size.x;
                float depth = thing is Pawn ? .8f : thing is Plant ? .35f : size.z;
                if (DeterministicFlight && thing is Pawn pronePawn &&
                    (pronePawn.Downed || pronePawn.GetPosture() != PawnPosture.Standing))
                {
                    if (pronePawn.CurrentBed() is Building_Bed bed)
                    {
                        center = MapView.BedPawnPosition(pronePawn, bed, false);
                        center.y = (MapView.IsGroundSpot(bed.def) ? .025f : .65f) + .045f + height * .5f;
                        width = .8f;
                        depth = bed.def.size.z;
                        if (bed.Rotation.IsHorizontal) { width = depth; depth = .8f; }
                    }
                    else if (pronePawn.Rotation.IsHorizontal) width = 1.6f * MapView.PawnScaleForBullets;
                    else depth = 1.6f * MapView.PawnScaleForBullets;
                }
                if (new Bounds(center, new Vector3(width, height, depth)).IntersectRay(ray, out var impactDistance) && impactDistance <= closest)
                { closest = impactDistance; hit = thing; hitGround = false; }
            }
            flightPosition = ray.GetPoint(closest);
            remainingDistance -= closest;
            var position = flightPosition.ToIntVec3();
            if (!position.InBounds(Map)) { Destroy(); return; }
            Position = position;
            if (hit != null || hitGround || remainingDistance <= .001f) Impact(hit);
        }

        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            var pawn = hitThing as Pawn;
            float healthBefore = pawn != null ? pawn.health.summaryHealth.SummaryHealthPercent : hitThing?.HitPoints ?? 0;
            float woundsBefore = InjurySeverity(pawn);
            bool wasDead = pawn?.Dead ?? false;
            if (pawn != null && !blockedByShield) ImpactPawn(pawn);
            else base.Impact(hitThing, blockedByShield);
            if (hitThing == null || blockedByShield) return;
            bool killed = pawn != null && !wasDead && pawn.Dead;
            float healthAfter = pawn != null ? pawn.health.summaryHealth.SummaryHealthPercent : hitThing.HitPoints;
            if (killed || hitThing.Destroyed || healthAfter < healthBefore || InjurySeverity(pawn) > woundsBefore)
                ConfirmHit?.Invoke(hitThing, killed);
        }

        void ImpactPawn(Pawn pawn)
        {
            var part = HitBodyPart(pawn);
            var log = new BattleLogEntry_RangedImpact(launcher, pawn, pawn, equipmentDef, def, targetCoverDef);
            Find.BattleLog.Add(log);
            var data = new BulletImpactData { bullet = this, hitThing = pawn, impactPosition = Position };
            var impactMap = Map;
            // Native impact notifications retain nearby reaction, armor and personal shields.
            for (int i = 0; i < 9; i++)
            {
                var cell = Position + GenRadial.RadialPattern[i];
                if (!cell.InBounds(impactMap)) continue;
                foreach (var thing in cell.GetThingList(impactMap)) thing.Notify_BulletImpactNearby(data);
            }
            bool guilty = !(launcher is Pawn shooter) || !shooter.Drafted;
            var damage = new DamageInfo(DamageDef, DamageAmount, ArmorPenetration, ExactRotation.eulerAngles.y,
                launcher, part, equipmentDef, DamageInfo.SourceCategory.ThingOrUnknown, pawn, guilty);
            damage.SetWeaponQuality(equipmentQuality);
            pawn.TakeDamage(damage).AssociateWithLog(log);
            pawn.stances?.stagger.Notify_BulletImpact(this);
            if (ExtraDamages != null && !pawn.Dead)
                foreach (var extra in ExtraDamages)
                    if (Rand.Chance(extra.chance))
                        pawn.TakeDamage(new DamageInfo(extra.def, extra.amount, extra.AdjustedArmorPenetration(),
                            ExactRotation.eulerAngles.y, launcher, part, equipmentDef, DamageInfo.SourceCategory.ThingOrUnknown,
                            pawn, guilty)).AssociateWithLog(log);
            landed = true;
            Destroy();
        }

        float ImpactHeight(Pawn pawn) => DeterministicFlight
            ? pawn == AimPawn && ValidAimHeight(AimPawnHeight) ? AimPawnHeight : 1.6f * MapView.PawnScaleForBullets
            : PawnHeight?.Invoke(pawn) ?? 1.6f * MapView.PawnScaleForBullets;

        float CrouchOffset(Pawn pawn) => DeterministicFlight && Rim3DMultiplayer.Active &&
            PawnControl.ForPawn(pawn)?.IsCrouching == true ? -PawnControl.CrouchDepth : 0;

        float CollisionHeight(Pawn pawn) => Mathf.Max(.1f, ImpactHeight(pawn) + CrouchOffset(pawn));

        internal static bool ValidAimHeight(float height) => !float.IsNaN(height) && !float.IsInfinity(height) &&
            height >= .1f && height <= 16;

        BodyPartRecord HitBodyPart(Pawn pawn)
        {
            float height = ImpactHeight(pawn);
            float level = Mathf.Clamp01((flightPosition.y - API.RimVisionSpatial.Height(pawn, flightPosition) - CrouchOffset(pawn)) / Mathf.Max(.1f, height));
            // RimWorld portraits have a large head; include its chin, not just the crown.
            string wanted = pawn.GetPosture() != PawnPosture.Standing || pawn.Downed ? "Torso" :
                level >= .60f ? "Head" : level <= .34f ? "Leg" : "Torso";
            BodyPartRecord fallback = null;
            foreach (var part in pawn.health.hediffSet.GetNotMissingParts())
            {
                if (part.depth != BodyPartDepth.Outside) continue;
                if (part.def.defName == wanted)
                    return wanted == "Torso" ? part : RandomHitPart(pawn, part.def) ?? part;
                if (part.def.defName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) fallback = part;
            }
            if (fallback != null)
                return wanted == "Torso" ? fallback : RandomHitPart(pawn, fallback.def) ?? fallback;
            // Non-human anatomies use their exposed upper/lower/middle body region.
            var wantedHeight = level >= .60f ? BodyPartHeight.Top : level <= .34f ? BodyPartHeight.Bottom : BodyPartHeight.Middle;
            foreach (var part in pawn.health.hediffSet.GetNotMissingParts())
                if (part.depth == BodyPartDepth.Outside && part.height == wantedHeight &&
                    (fallback == null || part.coverageAbs > fallback.coverageAbs)) fallback = part;
            return fallback ?? pawn.RaceProps.body.corePart;
        }

        BodyPartRecord RandomHitPart(Pawn pawn, BodyPartDef region)
        {
            var parts = HitRegionParts(pawn, region);
            if (parts.TryRandomElementByWeight(part => part.coverageAbs * part.def.GetHitChanceFactorFor(DamageDef), out var hit))
                return hit;
            return parts.TryRandomElementByWeight(part => part.coverageAbs, out hit) ? hit : null;
        }

        static IEnumerable<BodyPartRecord> HitRegionParts(Pawn pawn, BodyPartDef region)
        {
            foreach (var part in pawn.health.hediffSet.GetNotMissingParts())
                for (var ancestor = part; ancestor != null; ancestor = ancestor.parent)
                    if (ancestor.def == region)
                    {
                        yield return part;
                        break;
                    }
        }

        static float InjurySeverity(Pawn pawn)
        {
            float total = 0;
            if (pawn != null)
                foreach (var hediff in pawn.health.hediffSet.hediffs)
                    if (hediff is Hediff_Injury) total += hediff.Severity;
            return total;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref flightPosition, "fpvPosition");
            Scribe_Values.Look(ref flightDirection, "fpvDirection");
            Scribe_Values.Look(ref remainingDistance, "fpvRemainingDistance");
            Scribe_Values.Look(ref DeterministicFlight, "fpvDeterministicFlight");
            Scribe_References.Look(ref AimPawn, "fpvAimPawn");
            Scribe_Values.Look(ref AimPawnHeight, "fpvAimPawnHeight");
        }
    }

    public sealed partial class MapView
    {
        internal const float PawnScaleForBullets = PawnVisualScale;
        int nextManualShotTick;
        float lastConfirmedHit = -100;
        bool lastHitKilled;
        int manualFireMode = 1; // 0 single, 1 burst, 2 automatic.

        void SendManualFireControl(bool held, int mode) => _combatActions.SendManualFireControl(held, mode);

        void ReceiveManualFireControl(Rim3DCommand command) => _combatActions.ReceiveManualFireControl(command);

        public void CancelSynchronizedManualFire() => _combatActions.CancelSynchronizedManualFire();

        void TickSynchronizedManualFire() => _combatActions.TickSynchronizedManualFire();

        void ExposeManualFireControl() => _combatActions.ExposeManualFireControl();

        static bool SupportsRepeatedFire(Verb_LaunchProjectile verb) => verb != null && verb.BurstShotCount > 1 &&
            verb.Projectile?.projectile != null && typeof(Bullet).IsAssignableFrom(verb.Projectile.thingClass) &&
            verb.Projectile.projectile.explosionRadius <= 0;

        static bool RequiresOneUseBurst(Verb_LaunchProjectile verb) => verb is Verb_ShootOneUse && verb.BurstShotCount > 1;

        FPVWeaponFireState FireState(ThingWithComps weapon, bool create = false) => _combatActions.FireState(weapon, create);

        void ResetManualFireInput() => _combatActions.ResetManualFireInput();

        void UpdateManualFireInput() => _combatActions.UpdateManualFireInput();

        static string FireModeLabel(int mode) => (mode == 0 ? "Rim3D_FireSingle" :
            mode == 1 ? "Rim3D_FireBurst" : "Rim3D_FireAuto").Translate().ToString();

        void DrawManualFireHud(Rect viewport) => _hudPresentation.DrawManualFireHud(viewport);

        bool FireManualBullet(int mode, bool begin) => _combatActions.FireManualBullet(mode, begin);

        bool FireManualBullet(Vector3 start, Vector3 direction, IntVec3 targetCell, int mode, bool begin,
            Pawn aimPawn = null, float aimPawnHeight = 0) => _combatActions.FireManualBullet(start, direction, targetCell, mode, begin, aimPawn, aimPawnHeight);

        static Vector3 ApplyShootingSpread(Pawn pawn, ThingWithComps weapon, Verb_LaunchProjectile verb,
            Vector3 direction, float distance, float stanceFactor, float instability)
        {
            float skill = Mathf.Clamp(pawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0, 0, 20);
            float inaccuracy = 1f - skill / 20f;
            float weaponFactor = Mathf.Lerp(2.5f, .5f, verb.verbProps.GetHitChanceFactor(weapon, distance));
            float sustainedSpread = Mathf.Clamp01((instability - .35f) / .65f) * Mathf.Lerp(.6f, 1f, inaccuracy);
            float radius = Mathf.Tan((.25f + 3.75f * inaccuracy * inaccuracy + sustainedSpread) *
                weaponFactor * stanceFactor * Mathf.Deg2Rad);
            // Uniform disk sampling; Verse.Rand also runs in the synchronized shot command.
            float offset = Mathf.Sqrt(Rand.Value) * radius;
            float angle = Rand.Value * (2f * Mathf.PI);
            direction.Normalize();
            var reference = Mathf.Abs(direction.y) > .99f ? Vector3.forward : Vector3.up;
            var right = Vector3.Cross(reference, direction).normalized;
            var up = Vector3.Cross(direction, right);
            return (direction + right * (Mathf.Cos(angle) * offset) +
                up * (Mathf.Sin(angle) * offset)).normalized;
        }

        static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        sealed partial class HudPresentation
        {
            internal void DrawManualFireHud(Rect viewport)
            {
                var weapon = _view.ControlledPawn?.equipment?.Primary;
                var verb = weapon?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                if (_view.ControlledPawn?.Drafted != true || !SupportsRepeatedFire(verb) || _view.constructionMenu || _view.actionMenu || _view.statusMenu) return;
                var state = _view.FireState(weapon);
                int tick = Rim3DMultiplayer.Active && Rim3DMultiplayer.MapTicks != null
                    ? Rim3DMultiplayer.MapTicks(_view.map) : Find.TickManager.TicksGame;
                float heat = state == null ? 0 : Mathf.Clamp01((float)state.HeatAt(tick) / state.Capacity);
                var rect = new Rect(viewport.center.x - 140, viewport.center.y + 112, 280, 22);
                bool hot = state?.Overheated(tick) == true;
                _view.Badge(rect, hot ? "Rim3D_WeaponOverheated".Translate().ToString() :
                    "Rim3D_FireModeHint".TranslateBindings(FireModeLabel(_view.manualFireMode)).ToString(), _view.activityStyle);
                rect.y += 24;
                rect.height = 5;
                Widgets.DrawBoxSolid(rect, new Color(.12f, .12f, .12f, .85f));
                rect.width *= heat;
                Widgets.DrawBoxSolid(rect, hot ? new Color(1f, .2f, .1f) : new Color(1f, .65f, .15f));
            }

        }

        sealed partial class CombatActions
        {
            ThingWithComps _inputFireWeapon;

            Pawn _inputFirePawn;

            int _inputBurstRemaining, _nextInputShotTick;

            bool _sentFireHeld;

            int _nextFireControlTick, _sentFireMode = -1, _releasedFireAimUntil;

            ThingWithComps _synchronizedFireWeapon;

            bool _synchronizedFireHeld;

            int _synchronizedFireMode, _synchronizedBurstRemaining, _fireControlUntil;

            Vector3 _synchronizedFireOffset, _synchronizedFireDirection;

            IntVec3 _synchronizedFireCell = IntVec3.Invalid;

            Pawn _synchronizedAimPawn;

            float _synchronizedAimPawnHeight;

            float _firePressUntil = -1;

            internal void SendManualFireControl(bool held, int mode)
            {
                if (_sentFireHeld && !held)
                {
                    var verb = _view.ControlledPawn?.equipment?.Primary?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                    _releasedFireAimUntil = Find.TickManager.TicksGame +
                        (verb == null ? 0 : verb.BurstShotCount * Mathf.Max(1, verb.TicksBetweenBurstShots)) + 120;
                }
                Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                {
                    Action = Rim3DAction.FireControl, Count = mode, StartWork = held,
                    Cell = ManualFireTargetCell(), Origin = _view._viewCamera.AimRay.origin, Direction = _view._viewCamera.AimRay.direction,
                    Target = _view.aimedThing as Pawn, AimPawnHeight = _view.aimedThing is Pawn target ? _view.PawnStandingSpriteTop(target) : 0
                });
                _sentFireHeld = held;
                _sentFireMode = mode;
                _nextFireControlTick = Find.TickManager.TicksGame + 6;
            }

            internal void ReceiveManualFireControl(Rim3DCommand command)
            {
                if (API.RimVisionCombat.Handles(_view.ControlledPawn, API.RimVisionAttackKind.Ranged))
                { _view.CancelSynchronizedManualFire(); return; }
                if (command.Count == 0) { _view.CancelSynchronizedManualFire(); return; }
                var weapon = _view.ControlledPawn?.equipment?.Primary;
                var verb = weapon?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                if (command.Count < 1 || command.Count > 2 ||
                    !(SupportsRepeatedFire(verb) || command.Count == 1 && RequiresOneUseBurst(verb)) ||
                    !Finite(command.Origin) || !Finite(command.Direction) || command.Direction.sqrMagnitude < .01f) return;
                var offset = command.Origin - _view.controller.Position;
                if (new Vector2(offset.x, offset.z).sqrMagnitude > 2.25f || command.Origin.y < 0 || command.Origin.y > 4) return;
                if (_synchronizedFireWeapon != weapon || _synchronizedFireMode != command.Count)
                {
                    _synchronizedBurstRemaining = 0;
                    _synchronizedFireWeapon = weapon;
                    _synchronizedFireMode = command.Count;
                }
                _synchronizedFireHeld = command.StartWork;
                _synchronizedFireOffset = offset;
                _synchronizedFireDirection = command.Direction.normalized;
                _synchronizedFireCell = command.Cell;
                _synchronizedAimPawn = command.Target is Pawn target && target.Map == _view.map &&
                    FPVManualBullet.ValidAimHeight(command.AimPawnHeight) ? target : null;
                _synchronizedAimPawnHeight = _synchronizedAimPawn != null ? command.AimPawnHeight : 0;
                _fireControlUntil = Find.TickManager.TicksGame + 120;
                // Preserve short clicks even if press and release arrive in the same tick.
                if (_synchronizedFireHeld) _view.TickSynchronizedManualFire();
            }

            internal void CancelSynchronizedManualFire()
            {
                var fire = _view.FireState(_synchronizedFireWeapon);
                if (fire?.BurstRemaining > 0)
                {
                    var verb = _synchronizedFireWeapon.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                    if (verb != null && _view.ControlledPawn != null) fire.NextShotTick = Mathf.Max(fire.NextShotTick,
                        Find.TickManager.TicksGame + verb.verbProps.AdjustedCooldownTicks(verb, _view.ControlledPawn));
                    fire.BurstRemaining = 0;
                }
                _synchronizedFireHeld = false;
                _synchronizedBurstRemaining = 0;
            }

            internal void TickSynchronizedManualFire()
            {
                int tick = Find.TickManager.TicksGame;
                var pawn = _view.ControlledPawn;
                var weapon = pawn?.equipment?.Primary;
                if (API.RimVisionCombat.Handles(pawn, API.RimVisionAttackKind.Ranged))
                { _view.CancelSynchronizedManualFire(); return; }
                if (_view.controller.InputBlocked || pawn?.Drafted != true || _view.controller.IsActing || _view.controller.CombatRecovering ||
                    _view.controller.IsRunning && !Rim3DSettings.Current.ShootWhileRunningEnabled ||
                    pawn.stances.FullBodyBusy || weapon != _synchronizedFireWeapon || tick >= _fireControlUntil)
                { _view.CancelSynchronizedManualFire(); return; }
                if (!_synchronizedFireHeld && _synchronizedBurstRemaining <= 0) return;
                var fire = _view.FireState(weapon);
                if (fire?.Overheated(tick) == true) { _synchronizedBurstRemaining = 0; return; }
                if (tick < (fire?.NextShotTick ?? 0)) return;
                var verb = weapon?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                if (!(SupportsRepeatedFire(verb) || _synchronizedFireMode == 1 && RequiresOneUseBurst(verb)) || !verb.Available())
                { _view.CancelSynchronizedManualFire(); return; }
                bool begin = _synchronizedBurstRemaining <= 0;
                if (!_view.FireManualBullet(_view.controller.Position + _synchronizedFireOffset, _synchronizedFireDirection,
                    _synchronizedFireCell, _synchronizedFireMode, begin, _synchronizedAimPawn, _synchronizedAimPawnHeight)) return;
                if (_synchronizedFireMode == 1)
                    _synchronizedBurstRemaining = _view.FireState(weapon)?.BurstRemaining ?? 0;
            }

            internal void ExposeManualFireControl()
            {
                Scribe_References.Look(ref _synchronizedFireWeapon, "manualFireWeapon");
                Scribe_Values.Look(ref _synchronizedFireHeld, "manualFireHeld");
                Scribe_Values.Look(ref _synchronizedFireMode, "manualFireMode");
                Scribe_Values.Look(ref _synchronizedBurstRemaining, "manualBurstRemaining");
                Scribe_Values.Look(ref _fireControlUntil, "manualFireUntil");
                Scribe_Values.Look(ref _synchronizedFireOffset, "manualFireOffset");
                Scribe_Values.Look(ref _synchronizedFireDirection, "manualFireDirection");
                Scribe_Values.Look(ref _synchronizedFireCell, "manualFireCell", IntVec3.Invalid);
                Scribe_References.Look(ref _synchronizedAimPawn, "manualAimPawn");
                Scribe_Values.Look(ref _synchronizedAimPawnHeight, "manualAimPawnHeight");
            }

            internal FPVWeaponFireState FireState(ThingWithComps weapon, bool create = false) =>
                Current.Game?.GetComponent<FPVFireState>()?.Get(weapon, create);

            internal void ResetManualFireInput()
            {
                ResetNativeInput();
                if (Rim3DMultiplayer.LocalInput && (_sentFireHeld || Find.TickManager.TicksGame < _releasedFireAimUntil))
                {
                    _view.SendManualFireControl(false, 0);
                    _releasedFireAimUntil = 0;
                }
                _inputBurstRemaining = 0;
                _firePressUntil = -1;
            }

            internal void UpdateManualFireInput()
            {
                if (_view.ApiInputBlocked(API.RimVisionInputChannels.Combat)) { _view.ResetManualFireInput(); return; }
                var pawn = _view.ControlledPawn;
                var weapon = pawn?.equipment?.Primary;
                var verb = weapon?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                if (verb != null && PawnControl.ForPawn(pawn)?.IsRunning == true && !Rim3DSettings.Current.ShootWhileRunningEnabled)
                {
                    _view.ResetManualFireInput();
                    return;
                }
                bool nativeRanged = API.RimVisionCombat.Handles(pawn, API.RimVisionAttackKind.Ranged);
                if (nativeRanged && API.RimVisionCombat.UsesInputPhases(pawn, API.RimVisionAttackKind.Ranged))
                { UpdateNativeInput(); return; }
                bool repeated = !nativeRanged && SupportsRepeatedFire(verb);
                if (_inputFireWeapon != weapon || _inputFirePawn != pawn)
                {
                    _view.ResetManualFireInput();
                    _inputFireWeapon = weapon;
                    _inputFirePawn = pawn;
                    _nextInputShotTick = 0;
                    // Sustained-fire weapons start in auto; rifles start in burst.
                    _view.manualFireMode = weapon?.def.defName == "Gun_Minigun" || weapon?.def.defName == "Gun_LMG" ||
                        weapon?.def.defName == "Gun_HeavySMG" || weapon?.def.defName == "Gun_ChainShotgun" ? 2 : 1;
                }
                if (repeated && ViewBindings.Pressed(ViewAction.FireMode))
                {
                    _view.manualFireMode = (_view.manualFireMode + 1) % 3;
                    _inputBurstRemaining = 0;
                    _view.SayAction(FireModeLabel(_view.manualFireMode));
                }
                bool pressed = ViewBindings.MouseDown(0);
                if (nativeRanged)
                {
                    _view.ResetManualFireInput();
                    if (pressed && !Find.TickManager.Paused)
                    {
                        _view.PickFPVTarget();
                        _view.FireManualBullet(0, true);
                    }
                    return;
                }
                if (pressed && !Find.TickManager.Paused) _firePressUntil = Time.unscaledTime + .12f;
                bool bufferedPress = Time.unscaledTime <= _firePressUntil;
                if (Rim3DMultiplayer.LocalInput && verb != null)
                {
                    int networkMode = RequiresOneUseBurst(verb) ? 1 : repeated ? _view.manualFireMode : 0;
                    bool held = networkMode != 0 && ViewBindings.MouseHeld(0) && !Find.TickManager.Paused;
                    // Simulation ticks own cadence, burst length, heat and random spread.
                    // Heartbeats also update aim while a released burst finishes.
                    if (held != _sentFireHeld || networkMode != _sentFireMode ||
                        networkMode != 0 && (held || Find.TickManager.TicksGame < _releasedFireAimUntil) &&
                        Find.TickManager.TicksGame >= _nextFireControlTick)
                    { _view.PickFPVTarget(); _view.SendManualFireControl(held, networkMode); }
                    if (networkMode == 0 && bufferedPress && !Find.TickManager.Paused &&
                        Find.TickManager.TicksGame >= (_view.FireState(weapon)?.NextShotTick ?? 0) && verb.Available())
                    {
                        _view.PickFPVTarget();
                        if (_view.FireManualBullet(0, true)) _firePressUntil = -1;
                    }
                    return;
                }
                if (verb == null)
                {
                    if (pressed) { _view.PickFPVTarget(); _view.MeleeFPV(); }
                    return;
                }
                if (Find.TickManager.Paused || _view.controller.IsActing || _view.controller.CombatRecovering || pawn.stances.FullBodyBusy)
                { _inputBurstRemaining = 0; _firePressUntil = -1; return; }
                int tick = Find.TickManager.TicksGame;
                var state = _view.FireState(weapon);
                if (state?.Overheated(tick) == true)
                {
                    _inputBurstRemaining = 0;
                    if (pressed) _view.SayAction("Rim3D_WeaponOverheated".Translate().ToString());
                    return;
                }
                int mode = RequiresOneUseBurst(verb) ? 1 : repeated ? _view.manualFireMode : 0;
                bool continuing = mode == 1 && _inputBurstRemaining > 0;
                bool requested = bufferedPress || continuing || mode != 0 && ViewBindings.MouseHeld(0);
                if (!requested || tick < _nextInputShotTick || tick < (state?.NextShotTick ?? 0) || !verb.Available()) return;
                _view.PickFPVTarget();
                bool begin = !continuing;
                if (!_view.FireManualBullet(mode, begin)) { _inputBurstRemaining = 0; return; }
                _firePressUntil = -1;
                if (mode == 1)
                {
                    if (RequiresOneUseBurst(verb)) _inputBurstRemaining = _view.FireState(weapon)?.BurstRemaining ?? 0;
                    else
                    {
                        if (begin) _inputBurstRemaining = verb.BurstShotCount;
                        _inputBurstRemaining--;
                    }
                }
                int interval = mode == 2 || mode == 1 && _inputBurstRemaining > 0 ? verb.TicksBetweenBurstShots :
                    verb.verbProps.AdjustedCooldownTicks(verb, pawn);
                _nextInputShotTick = tick + Mathf.Max(1, interval);
            }

            IntVec3 ManualFireTargetCell()
            {
                var verb = _view.ControlledPawn?.equipment?.Primary?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                var targetCell = _view.aimedCell;
                var projectileDef = verb?.Projectile;
                bool nativeFlight = projectileDef?.projectile != null &&
                    (!typeof(Bullet).IsAssignableFrom(projectileDef.thingClass) || projectileDef.projectile.explosionRadius > 0);
                if (nativeFlight && _view.aimingAtRoof && targetCell.IsValid && targetCell.InBounds(_view.map) &&
                    !_view.map.roofGrid.Roofed(targetCell))
                    targetCell = IntVec3.Invalid;
                return targetCell;
            }

            internal bool FireManualBullet(int mode, bool begin)
            {
                if (PawnControl.ForPawn(_view.ControlledPawn)?.IsRunning == true && !Rim3DSettings.Current.ShootWhileRunningEnabled) return false;
                var verb = _view.ControlledPawn?.equipment?.Primary?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                if (verb != null && !PawnControl.CanUseCombatSkill(_view.ControlledPawn, SkillDefOf.Shooting))
                { _view.SayAction("Rim3D_ShootingDisabled".Translate().ToString()); return false; }
                var targetCell = ManualFireTargetCell();
                if (Rim3DMultiplayer.LocalInput)
                {
                    if (verb != null || API.RimVisionCombat.Handles(_view.ControlledPawn, API.RimVisionAttackKind.Ranged))
                        Rim3DMultiplayer.Send?.Invoke(_view, new Rim3DCommand
                        {
                            Action = Rim3DAction.Fire, Cell = targetCell, Count = mode, StartWork = begin,
                            Origin = _view._viewCamera.AimRay.origin, Direction = _view._viewCamera.AimRay.direction,
                            Target = _view.aimedThing as Pawn, AimPawnHeight = _view.aimedThing is Pawn target ? _view.PawnStandingSpriteTop(target) : 0
                        });
                    else return false;
                    return true;
                }
                return _view.FireManualBullet(_view._viewCamera.AimRay.origin, _view._viewCamera.AimRay.direction, targetCell, mode, begin, _view.aimedThing as Pawn);
            }

            internal bool FireManualBullet(Vector3 start, Vector3 direction, IntVec3 targetCell, int mode, bool begin,
                Pawn aimPawn = null, float aimPawnHeight = 0)
            {
                var pawn = _view.ControlledPawn;
                if (pawn != null && API.RimVisionCombat.Handles(pawn, API.RimVisionAttackKind.Ranged))
                {
                    if (!_view.controller.Active || !pawn.Drafted || Find.TickManager.Paused ||
                        !PawnControl.CanUseCombatSkill(pawn, SkillDefOf.Shooting) || !Finite(start) ||
                        !Finite(direction) || direction.sqrMagnitude < .01f || mode < 0 || mode > 2) return false;
                    var context = new API.RimVisionCombatContext(pawn, API.RimVisionAttackKind.Ranged, aimPawn,
                        targetCell, start, direction.normalized, mode, begin);
                    if (API.RimVisionCombat.TryExecute(context, out var fired, out var reason))
                    {
                        if (!string.IsNullOrEmpty(reason)) _view.SayAction(reason);
                        return fired;
                    }
                    return false;
                }
                if (pawn == null || !pawn.Drafted || _view.controller.IsActing || _view.controller.CombatRecovering ||
                    _view.controller.IsRunning && !Rim3DSettings.Current.ShootWhileRunningEnabled ||
                    pawn.stances.FullBodyBusy || Find.TickManager.Paused) return false;
                var weapon = pawn.equipment?.Primary;
                var verb = weapon?.TryGetComp<CompEquippable>()?.PrimaryVerb as Verb_LaunchProjectile;
                if (verb == null || weapon.Destroyed) return false;
                if (CompBiocodable.IsBiocoded(weapon) && !CompBiocodable.IsBiocodedFor(weapon, pawn))
                { _view.SayAction("Rim3D_WeaponNotReady".Translate().ToString()); return false; }
                if (!PawnControl.CanUseCombatSkill(pawn, SkillDefOf.Shooting))
                { _view.SayAction("Rim3D_ShootingDisabled".Translate().ToString()); return false; }
                if (mode < 0 || mode > 2 || RequiresOneUseBurst(verb) && mode != 1 || mode != 0 &&
                    !(SupportsRepeatedFire(verb) || mode == 1 && RequiresOneUseBurst(verb))) return false;
                int tick = Find.TickManager.TicksGame;
                var fire = _view.FireState(weapon, true);
                if (fire == null || tick < fire.NextShotTick) return false;
                if (verb is Verb_ShootOneUse && fire.OneUseShotsFired >= verb.BurstShotCount) return false;
                if (fire.Overheated(tick))
                { _view.SayAction("Rim3D_WeaponOverheated".Translate().ToString()); return false; }
                if (mode == 1 && !begin && fire.BurstRemaining <= 0) return false;
                // A cancelled queue may be restarted only after the normal recovery time.
                if (mode == 1 && begin && fire.BurstRemaining > 0 &&
                    tick < fire.NextShotTick + verb.verbProps.AdjustedCooldownTicks(verb, pawn)) return false;
                if (!verb.Available()) { _view.SayAction("Rim3D_WeaponNotReady".Translate().ToString()); return false; }
                var projectileDef = verb.Projectile;
                if (projectileDef == null || projectileDef.projectile == null ||
                    !typeof(Projectile).IsAssignableFrom(projectileDef.thingClass))
                { _view.SayAction("Rim3D_NoProjectile".Translate().ToString()); return false; }
                if (Rim3DMultiplayer.Simulating)
                {
                    if (!Finite(start) || !Finite(direction) || direction.sqrMagnitude < .01f) return false;
                    var offset = start - _view.controller.Position;
                    if (new Vector2(offset.x, offset.z).sqrMagnitude > 2.25f || start.y < 0 || start.y > 4) return false;
                    direction.Normalize();
                }
                float range = Mathf.Max(1, verb.EffectiveRange);
                bool manualFlight = typeof(Bullet).IsAssignableFrom(projectileDef.thingClass) &&
                    projectileDef.projectile.explosionRadius <= 0;
                float aimDistance = targetCell.IsValid && targetCell.InBounds(_view.map)
                    ? Vector3.Distance(start, targetCell.ToVector3Shifted()) : range;
                if (manualFlight) direction = ApplyShootingSpread(pawn, weapon, verb, direction, aimDistance,
                    _view.controller.Sneaking ? .6f : 1, fire.InstabilityAt(tick));
                var end = start + direction * range;
                var endCell = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(end.x), 0, _view.map.Size.x - 1), 0,
                    Mathf.Clamp(Mathf.FloorToInt(end.z), 0, _view.map.Size.z - 1));
                Projectile bullet;
                if (manualFlight)
                {
                    var manual = new FPVManualBullet { def = projectileDef };
                    manual.PostMake();
                    manual.PostPostMake();
                    manual.Configure(start, direction, range);
                    manual.DeterministicFlight = Rim3DMultiplayer.Simulating;
                    if (manual.DeterministicFlight && aimPawn?.Map == _view.map && FPVManualBullet.ValidAimHeight(aimPawnHeight))
                    {
                        // Use the shooter's visible sprite height on every peer, including headless ones.
                        // The actual flight and spread still decide whether and where the bullet hits.
                        manual.AimPawn = aimPawn;
                        manual.AimPawnHeight = aimPawnHeight;
                    }
                    if (!manual.DeterministicFlight)
                    {
                        manual.PawnHeight = _view.PawnSpriteTop;
                        manual.PawnRayHit = _view.RaycastPawnSprite;
                        manual.BedRayHit = _view.RaycastBed;
                    }
                    bullet = manual;
                }
                else
                {
                    if (!targetCell.IsValid && direction.y > 0)
                    {
                        var horizontal = new Vector3(direction.x, 0, direction.z).normalized;
                        var skyEnd = _view.controller.Position + horizontal * Mathf.Max(0, range - .75f);
                        endCell = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(skyEnd.x), 0, _view.map.Size.x - 1), 0,
                            Mathf.Clamp(Mathf.FloorToInt(skyEnd.z), 0, _view.map.Size.z - 1));
                    }
                    // Keep the native projectile class: its fuse, gas, fire and explosion
                    // logic must run on a real spawned Thing, including after saving.
                    if (targetCell.IsValid && targetCell.InBounds(_view.map) &&
                        (targetCell.ToVector3Shifted() - pawn.Position.ToVector3Shifted()).magnitude <= range)
                        endCell = targetCell;
                    var offset = endCell.ToVector3Shifted() - pawn.Position.ToVector3Shifted();
                    if (offset.magnitude > range)
                        endCell = (pawn.Position.ToVector3Shifted() + offset.normalized * Mathf.Max(0, range - .75f)).ToIntVec3();
                    float targetDistance = (endCell.ToVector3Shifted() - pawn.Position.ToVector3Shifted()).magnitude;
                    if (targetDistance < verb.verbProps.EffectiveMinRange(new LocalTargetInfo(endCell), pawn))
                    { _view.SayAction("Rim3D_TargetOutOfRange".Translate().ToString()); return false; }
                    if (projectileDef.thingClass == typeof(Projectile_Explosive))
                    {
                        bullet = new FPVNativeExplosive { def = projectileDef };
                        bullet.PostMake();
                        bullet.PostPostMake();
                    }
                    else bullet = (Projectile)ThingMaker.MakeThing(projectileDef);
                }
                GenSpawn.Spawn(bullet, pawn.Position, _view.map);
                // Bullets use the camera ray with skill-based spread; native shells fly to the aimed cell.
                bullet.Launch(pawn, start, new LocalTargetInfo(endCell), new LocalTargetInfo(endCell),
                    ProjectileHitFlags.All, false, weapon);
                if (!manualFlight) _view._combatEffects?.SetProjectileOrigin(bullet, start);
                if (bullet is FPVManualBullet manualBullet)
                {
                    manualBullet.Configure(start, direction, range);
                    manualBullet.ConfirmHit = (hit, killed) =>
                    {
                        if (manualBullet.DeterministicFlight) { Rim3DMultiplayer.ConfirmHit?.Invoke(pawn, killed); return; }
                        if (!_view.FirstPerson || _view.ControlledPawn != pawn) return;
                        _view.lastConfirmedHit = Time.unscaledTime;
                        _view.lastHitKilled = killed;
                    };
                }
                var unique = weapon.TryGetComp<CompUniqueWeapon>();
                if (unique != null)
                    foreach (var trait in unique.TraitsListForReading)
                    {
                        if (trait.damageDefOverride != null) bullet.damageDefOverride = trait.damageDefOverride;
                        if (trait.extraDamages != null)
                        {
                            if (bullet.extraDamages == null) bullet.extraDamages = new List<ExtraDamage>();
                            bullet.extraDamages.AddRange(trait.extraDamages);
                        }
                    }
                weapon.TryGetComp<CompChangeableProjectile>()?.Notify_ProjectileLaunched();
                weapon.TryGetComp<CompApparelVerbOwner_Charged>()?.UsedOnce();
                verb.ReloadableCompSource?.UsedOnce();
                weapon.Notify_UsedWeapon(pawn);
                if (Rim3DMultiplayer.Simulating)
                    Rim3DMultiplayer.ShotSound?.Invoke(pawn, verb.verbProps.soundCast, verb.verbProps.soundCastTail);
                else
                {
                    verb.verbProps.soundCast?.PlayOneShot(new TargetInfo(pawn.Position, _view.map));
                    verb.verbProps.soundCastTail?.PlayOneShotOnCamera(_view.map);
                }
                if (mode == 1)
                {
                    if (begin) fire.BurstRemaining = verb.BurstShotCount - (verb is Verb_ShootOneUse ? fire.OneUseShotsFired : 0);
                    fire.BurstRemaining--;
                }
                else fire.BurstRemaining = 0;
                int interval = mode == 2 || mode == 1 && fire.BurstRemaining > 0 ? verb.TicksBetweenBurstShots :
                    verb.verbProps.AdjustedCooldownTicks(verb, pawn);
                fire.NextShotTick = tick + Mathf.Max(1, interval);
                if (SupportsRepeatedFire(verb))
                    fire.AddShot(tick, Mathf.Max(1, verb.TicksBetweenBurstShots), verb.BurstShotCount);
                if (aimPawn != null && aimPawn.Spawned && aimPawn.Map == pawn.Map && !aimPawn.Downed &&
                    !aimPawn.IsColonyMech && Vector3.Distance(start, aimPawn.TrueCenter()) <= range)
                {
                    float seconds = Mathf.Min(Mathf.Max(1, interval) / 60f,
                        verb.verbProps.AdjustedFullCycleTime(verb, pawn) / Mathf.Max(1, verb.BurstShotCount));
                    pawn.skills?.Learn(SkillDefOf.Shooting, (aimPawn.HostileTo(pawn) ? 170f : 20f) * seconds);
                }
                _view.nextManualShotTick = fire.NextShotTick;
                if (fire.Overheated(tick)) _view.SayAction("Rim3D_WeaponOverheated".Translate().ToString());
                if (Rim3DMultiplayer.Simulating) Rim3DMultiplayer.Shot?.Invoke(pawn);
                _view.lastPlayerShot = Time.unscaledTime;
                if (!Rim3DMultiplayer.Simulating) _view.AddWeaponRecoil();
                if (verb is Verb_ShootOneUse && ++fire.OneUseShotsFired >= verb.BurstShotCount)
                    weapon.Destroy();
                return true;
            }

        }
    }
}

