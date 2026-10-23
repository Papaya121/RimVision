using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        const float WeaponSpriteScale = .65f;

        sealed class HeldCard
        {
            public Thing Thing;
            public Material Source;
            public GameObject Object;
            public Building EatSurface;
            public float EatSurfaceHeight;
            public Vector2 Size;
            public Vector3 Muzzle, VisualMuzzle;
            public Vector3 DefaultRightGrip, DefaultLeftGrip, DefaultRightScale, DefaultLeftScale;
            public Quaternion DefaultRightRotation, DefaultLeftRotation;
            public bool GripsCaptured;
            public float RunBlend, AimBlend;
            public int RunFrame = -1;
            public bool Model;
            public float ModelHalfHeight;
            public Material HandMaterial;
            public Transform RightHand;
            public Transform LeftHand;
        }

        readonly HashSet<Thing> nearbyCountedItems = new HashSet<Thing>();
        readonly HashSet<MoteProgressBar> progressMotes = new HashSet<MoteProgressBar>();
        static readonly FieldInfo MoteMaintainTick = typeof(Mote).GetField("lastMaintainTick", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly Dictionary<Pawn, HeldCard> weapons = new Dictionary<Pawn, HeldCard>();

        readonly Dictionary<Thing, Building_WorkTable> workSurfaceIngredients = new Dictionary<Thing, Building_WorkTable>();

        readonly Dictionary<Thing, HeldCard> workIngredientCards = new Dictionary<Thing, HeldCard>();

        readonly HashSet<Pawn> pawnsWithProgress = new HashSet<Pawn>();

        GUIStyle activityStyle;
        GUIStyle countStyle;
        GUIStyle hudStyle;
        float nearbyAlpha = 1;
        static readonly Color PanelColor = new Color(.045f, .065f, .085f, .86f);
        static readonly Color ProgressColor = new Color(.9f, .82f, .25f);

        // Use native time speed so job ticks, costs and other pawns stay consistent.
        // Multiplayer time belongs to the shared session, not a local preference.
        bool ActionAccelerationEnabled => Rim3DMultiplayer.Active ?
            Rim3DMultiplayer.ActionAccelerationEnabled?.Invoke(map) == true : Rim3DSettings.Current.AccelerateActions;
        public bool AcceleratingAction => Rim3DMultiplayer.Active ?
            Rim3DMultiplayer.ActionAccelerationActive?.Invoke(map) == true : FirstPerson && Rim3DSettings.Current.AccelerateActions &&
            !Rim3DMultiplayer.Active && !PausedByPlayer && !FallPresentationActive &&
            controller.Active && controller.IsActing && !controller.WaitingForManualApproach &&
            ControlledPawn != null && !ControlledPawn.Drafted && !ControlledPawn.Downed &&
            !ControlledPawn.Dead && !ControlledPawn.pather.Moving &&
            ControlledPawn.CurJob.def != JobDefOf.AttackMelee && ControlledPawn.CurJob.def != JobDefOf.AttackStatic;

        void ToggleActionAcceleration()
        {
            if (Rim3DMultiplayer.Active) { Rim3DMultiplayer.ToggleActionAcceleration?.Invoke(map); return; }
            Rim3DSettings.Current.AccelerateActions = !Rim3DSettings.Current.AccelerateActions;
            LoadedModManager.GetMod<Rim3DMod>()?.WriteSettings();
        }

        public void DrawActionAcceleration(Rect viewport) => _hudPresentation.DrawActionAcceleration(viewport);
        void TrackActivityThing(Thing thing) => _heldThingPresentation.TrackActivityThing(thing);

        void UpdateHeldThings() => _heldThingPresentation.UpdateHeldThings();

        void UpdateWorkIngredientCards() => _heldThingPresentation.UpdateWorkIngredientCards();

        void RemoveAbsentCards(Dictionary<Pawn, HeldCard> cards) => _heldThingPresentation.RemoveAbsentCards(cards);

        void DestroyHeldCard(HeldCard card) => _heldThingPresentation.DestroyHeldCard(card);

        static bool IsHeldAnimal(Thing thing) =>
            (thing as Pawn ?? (thing as Corpse)?.InnerPawn)?.RaceProps.Animal == true;

        static Pawn HeldHumanPawn(Thing thing)
        {
            var pawn = thing as Pawn ?? (thing as Corpse)?.InnerPawn;
            return pawn?.RaceProps.Humanlike == true ? pawn : null;
        }

        sealed class HeldPawnPortrait
        {
            public RenderTexture Texture;
            public Material Material;
            public bool Valid;
        }
        readonly Dictionary<Pawn, HeldPawnPortrait> heldPawnPortraits = new Dictionary<Pawn, HeldPawnPortrait>();

        Material HeldThingMaterial(Thing thing, Graphic graphic, bool singleItem)
        {
            var human = HeldHumanPawn(thing);
            if (human == null)
                return thing is Pawn || thing is Corpse ? graphic.MatAt(Rot4.South, thing) : graphic is Graphic_StackCount stack
                    ? stack.SubGraphicForStackCount(singleItem ? 1 : thing.stackCount, thing.def).MatSingleFor(thing)
                    : graphic.MatSingleFor(thing);
            if (!heldPawnPortraits.TryGetValue(human, out var portrait))
            {
                portrait = new HeldPawnPortrait();
                EnsurePawnCaptureTexture(ref portrait.Texture, human, Rot4.South, false);
                portrait.Material = NewMaterial(portrait.Texture, Color.white, false);
                portrait.Material.SetFloat("_Unlit", 1);
                heldPawnPortraits.Add(human, portrait);
            }
            if (!portrait.Valid)
            {
                EnsurePawnCaptureTexture(ref portrait.Texture, human, Rot4.South, false);
                portrait.Material.mainTexture = portrait.Texture;
                // Render directly into our target: copying the shared portrait
                // during GUI drawing can capture the current map/UI framebuffer.
                var cacheCamera = Find.PawnCacheCamera;
                var previousTarget = RenderTexture.active;
                var previousClear = cacheCamera.clearFlags;
                var previousColor = cacheCamera.backgroundColor;
                int previousMask = cacheCamera.cullingMask;
                try
                {
                    cacheCamera.clearFlags = CameraClearFlags.SolidColor;
                    cacheCamera.backgroundColor = Color.clear;
                    cacheCamera.cullingMask = 0;
                    Find.PawnCacheRenderer.RenderPawn(human, portrait.Texture,
                        new Vector3(0, 0, .1f), .75f, 0, Rot4.South,
                        human.health.hediffSet.HasHead, true, true, true,
                        Vector3.zero, null, null, false);
                    portrait.Valid = true;
                }
                finally
                {
                    cacheCamera.clearFlags = previousClear;
                    cacheCamera.backgroundColor = previousColor;
                    cacheCamera.cullingMask = previousMask;
                    RenderTexture.active = previousTarget;
                }
            }
            return portrait.Material;
        }

        static Vector2 HeldThingSize(Thing thing, Graphic graphic) =>
            HeldHumanPawn(thing) != null ? new Vector2(1.28f, 1.6f) : graphic.drawSize;

        static Graphic HeldThingGraphic(Thing thing)
        {
            var pawn = thing as Pawn ?? (thing as Corpse)?.InnerPawn;
            if (pawn == null || pawn.RaceProps.Humanlike) return thing.Graphic;
            // Pawn.Graphic is a placeholder; the render tree owns the current
            // body graphic, including life stage, sex and alternate appearance.
            pawn.Drawer.renderer.EnsureGraphicsInitialized();
            return pawn.Drawer.renderer.BodyGraphic;
        }

        void UpdateHeldCard(Dictionary<Pawn, HeldCard> cards, Pawn pawn, Thing thing, bool carried) => _heldThingPresentation.UpdateHeldCard(cards, pawn, thing, carried);

        bool HeldThingOnEatSurface(Pawn pawn, Thing thing, out Building surface, out Vector3 position, out bool flip) => _heldThingPresentation.HeldThingOnEatSurface(pawn, thing, out surface, out position, out flip);

        float TableSurfaceHeight(Building table) => _heldThingPresentation.TableSurfaceHeight(table);

        void AimWeaponInWorld(HeldCard card, Pawn pawn, Thing weapon, Stance_Busy stance, Vector3? manualAim = null) => _heldThingPresentation.AimWeaponInWorld(card, pawn, weapon, stance, manualAim);

        Material WeaponVolumeMaterial(Material material) => _heldThingPresentation.WeaponVolumeMaterial(material);

        byte[] ReadVolumeMask(Material material, byte[] fallback, out int width, out int height) => _heldThingPresentation.ReadVolumeMask(material, fallback, out width, out height);

        void AppendVolumeContour(Geometry geometry, Material material, byte[] fallback,
            float width, float height, float halfDepth) => _heldThingPresentation.AppendVolumeContour(geometry, material, fallback, width, height, halfDepth);

        Geometry WeaponVolume(Material material, float width, float height, bool hasMuzzle) => _heldThingPresentation.WeaponVolume(material, width, height, hasMuzzle);

        static void MuzzleQuad(Geometry geometry, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            int start = geometry.Vertices.Count;
            geometry.Quad(a, b, c, d, Vector3.right, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            while (geometry.Colors.Count < geometry.Vertices.Count) geometry.Colors.Add(Color.white);
            for (int i = start; i < start + 4; i++) geometry.Colors[i] = color;
        }

        bool OverlayPoint(Vector3 position, Rect viewport, out Vector2 point, float maxDistance = 10f)
        {
            point = Vector2.zero;
            var cell = position.ToIntVec3();
            if (!cell.InBounds(map) || map.fogGrid.IsFogged(cell)) return false;
            var eye = CameraMapPosition;
            float distanceSquared = (position - eye).sqrMagnitude;
            if (distanceSquared >= maxDistance * maxDistance) return false;
            float distanceToEye = Mathf.Sqrt(distanceSquared);
            nearbyAlpha = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(maxDistance - 2, maxDistance, distanceToEye));
            var projected = camera.WorldToViewportPoint(root.transform.position + position);
            if (projected.z <= camera.nearClipPlane || projected.x < .02f || projected.x > .98f ||
                projected.y < .04f || projected.y > .96f) return false;
            // Text must not reveal a pawn or stack through a wall. This follows
            // the same building heights as the preview geometry, without colliders.
            int steps = Mathf.CeilToInt(distanceToEye * 2);
            var eyeCell = eye.ToIntVec3();
            var lastCell = eyeCell;
            for (int i = 1; i < steps; i++)
            {
                var sample = Vector3.Lerp(eye, position, (float)i / steps);
                var sampleCell = sample.ToIntVec3();
                if (sampleCell == cell || sampleCell == eyeCell || !sampleCell.InBounds(map)) continue;
                // Only cache a clear cell. A tall ray can enter a building cell
                // above its roof and drop into it at a later sample.
                if (sampleCell == lastCell) continue;
                var building = sampleCell.GetEdifice(map);
                if (building == null || (building is Building_Door door && door.Open))
                { lastCell = sampleCell; continue; }
                float height = building is Building_Door ? 1.7f :
                    building.def.Fillage == FillCategory.Full || building.def.mineable ? 2f : .65f;
                if (sample.y >= 0 && sample.y < height) return false;
            }
            point = new Vector2(viewport.x + projected.x * viewport.width,
                viewport.y + (1 - projected.y) * viewport.height);
            return true;
        }

        void CollectNearbyItemLabels() => _hudPresentation.CollectNearbyItemLabels();

        public void DrawActivityOverlay(Rect viewport)
        {
            if (activityStyle == null)
            {
                activityStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter,
                    wordWrap = false, clipping = TextClipping.Clip };
                activityStyle.normal.textColor = Color.white;
                countStyle = new GUIStyle(activityStyle) { fontStyle = FontStyle.Bold, fontSize = 13 };
                hudStyle = new GUIStyle(activityStyle) { alignment = TextAnchor.MiddleLeft, fontSize = 14 };
            }
            var oldColor = GUI.color;
            GUI.color = Color.white;
            GUI.BeginGroup(viewport);
            var localViewport = new Rect(0, 0, viewport.width, viewport.height);
            try
            {
                if (Event.current != null && Event.current.type != EventType.Repaint) return;
                CollectNearbyItemLabels();
                progressMotes.RemoveWhere(mote => mote.Destroyed || !mote.Spawned || mote.Map != map);
                pawnsWithProgress.Clear();
                DrawProjectOverlay(localViewport);
                foreach (var thing in nearbyCountedItems)
                {
                    if (thing.stackCount <= 1) continue;
                    var position = thing.DrawPos; position.y = .16f;
                    if (OverlayPoint(position, localViewport, out var point, 6f))
                        Badge(new Rect(point.x - 23, point.y - 10, 46, 20), thing.stackCount.ToString(), countStyle, nearbyAlpha);
                }
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn == ControlledPawn) continue;
                    var position = pawn.DrawPos; position.y = PawnSpriteTop(pawn) + .04f;
                    if (!OverlayPoint(position, localViewport, out var point)) continue;
                    var weapon = pawn.equipment?.Primary;
                    var carried = pawn.carryTracker?.CarriedThing;
                    float textTop = point.y - 4 - (weapon != null || carried != null ? 60 : 40);
                    Badge(new Rect(point.x - 125, textTop, 250, 20), pawn.LabelShort, activityStyle, nearbyAlpha);
                    Badge(new Rect(point.x - 125, textTop + 20, 250, 20), ActionReport(pawn), activityStyle, nearbyAlpha);
                    if (weapon != null || carried != null)
                        Badge(new Rect(point.x - 125, textTop + 40, 250, 20),
                            (weapon != null ? weapon.LabelShort : "") +
                            (carried != null ? (weapon != null ? " · " : "") + carried.LabelShort + (carried.stackCount > 1 ? " ×" + carried.stackCount : "") : ""), activityStyle, nearbyAlpha);
                }
                if (FirstPerson)
                {
                    DrawPawnHud(localViewport);
                    DrawControlledActionProgress(localViewport);
                }
                foreach (var mote in progressMotes)
                {
                    if (mote.Alpha <= 0 || (mote.def.mote.needsMaintenance && MoteMaintainTick != null &&
                        Find.TickManager.TicksGame - (int)MoteMaintainTick.GetValue(mote) > 1)) continue;
                    var target = mote.link1.Target;
                    if (_constructionPlansHidden && (target.Thing is Blueprint || target.Thing is Frame)) continue;
                    if (target.Thing is Plant && !PlantHasWorker(target.Thing)) continue;
                    var pawn = target.Thing as Pawn;
                    if (pawn != null) pawnsWithProgress.Add(pawn);
                    var position = target.HasThing ? target.Thing.DrawPos : mote.exactPosition;
                    position.y = pawn != null ? PawnSpriteTop(pawn) + .04f : .85f;
                    if (pawn == ControlledPawn && controller.IsActing) continue;
                    if (pawn == ControlledPawn)
                        ProgressBar(new Rect(localViewport.width - 292, localViewport.height - 34, 270, 20), mote.progress);
                    else if (OverlayPoint(position, localViewport, out var point))
                        ProgressBar(new Rect(point.x - 52, point.y - ProgressLabelOffset(pawn), 104, 18), mote.progress, nearbyAlpha);
                }
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn == ControlledPawn && controller.IsActing) continue;
                    if (pawn == ControlledPawn && FirstPerson && pawn.stances?.curStance is Stance_Warmup) continue;
                    if (pawnsWithProgress.Contains(pawn) || !(pawn.stances?.curStance is Stance_Busy busy) || busy.ticksLeft <= 0) continue;
                    int elapsed = Mathf.Max(0, Find.TickManager.TicksGame - busy.startedTick);
                    float progress = (float)elapsed / Mathf.Max(1, elapsed + busy.ticksLeft);
                    var position = pawn.DrawPos; position.y = PawnSpriteTop(pawn) + .04f;
                    if (pawn == ControlledPawn) ProgressBar(new Rect(localViewport.width - 292, localViewport.height - 34, 270, 20), progress);
                    else if (OverlayPoint(position, localViewport, out var point))
                        ProgressBar(new Rect(point.x - 52, point.y - ProgressLabelOffset(pawn), 104, 18), progress, nearbyAlpha);
                }
            }
            finally { GUI.EndGroup(); GUI.color = oldColor; }
        }

        float PawnSpriteTop(Pawn pawn)
        {
            if (pawnCards.TryGetValue(pawn, out var card))
            {
                if (pawnViews.TryGetValue(pawn, out var modelView) && modelView.AnimalModel != null)
                    return card.localPosition.y + (modelView.Prone ? modelView.AnimalBounds.max.x : modelView.AnimalBounds.max.y);
                if (pronePawns.Contains(pawn)) return card.localPosition.y + .045f;
                var material = card.GetComponent<MeshRenderer>().sharedMaterial;
                if (visibleSpriteHeights.TryGetValue(material, out var height))
                    return card.localPosition.y + pawnViews[pawn].Height * height;
            }
            return 1.6f * PawnVisualScale;
        }

        float PawnStandingSpriteTop(Pawn pawn)
        {
            float top = PawnSpriteTop(pawn);
            if (Rim3DMultiplayer.Active && pawnCards.ContainsKey(pawn) && pawnViews.TryGetValue(pawn, out var view) &&
                !view.Prone && view.Bed == null)
                top += PawnControl.CrouchDepth * view.CrouchBlend;
            return top;
        }

        bool PlantHasWorker(Thing plant) => _hudPresentation.PlantHasWorker(plant);

        float ProgressLabelOffset(Pawn pawn) => _hudPresentation.ProgressLabelOffset(pawn);

        string ActionReport(Pawn pawn) => _hudPresentation.ActionReport(pawn);

        void DrawPawnHud(Rect viewport) => _hudPresentation.DrawPawnHud(viewport);

        void HudThing(Rect rect, Thing thing, string emptyText) => _hudPresentation.HudThing(rect, thing, emptyText);

        void DrawHeldThingIcon(Rect rect, Thing thing) => _hudPresentation.DrawHeldThingIcon(rect, thing);

        void Badge(Rect rect, string text, GUIStyle style, float alpha = 1) => _hudPresentation.Badge(rect, text, style, alpha);

        void ProgressBar(Rect rect, float progress, float alpha = 1) => _hudPresentation.ProgressBar(rect, progress, alpha);

        void DisposeActivity() => _heldThingPresentation.DisposeActivity();

        readonly HudPresentation _hudPresentation;

        sealed partial class HudPresentation
        {
            readonly MapView _view;

            internal HudPresentation(MapView view)
            {
                _view = view;
            }

            internal void DrawActionAcceleration(Rect viewport)
            {
                if (_view.constructionMenu || _view.actionMenu || _view.statusMenu || _view.ExternalInputBlocked) return;
                bool showAcceleration = _view.FirstPerson && _view.ActionAccelerationEnabled;
                bool showPawnLock = Rim3DMultiplayer.Active && Rim3DMultiplayer.PawnLockEnabled?.Invoke() == true;
                if (!showAcceleration && !showPawnLock) return;

                bool active = _view.AcceleratingAction && !Find.TickManager.Paused && Find.TickManager.TickRateMultiplier > 1;
                var previousColor = GUI.color;
                var previousFont = Text.Font;
                var previousAnchor = Text.Anchor;
                bool previousWrap = Text.WordWrap;
                try
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Text.WordWrap = false;
                    string label = "Rim3D_ActionAcceleration".Translate().ToString();
                    float width = Mathf.Min(Text.CalcSize(label).x + 50f, viewport.width - 24f);
                    if (showPawnLock)
                    {
                        string lockLabel = "Rim3D_PawnLockActive".Translate().ToString();
                        float lockWidth = Mathf.Min(Text.CalcSize(lockLabel).x + 16f, viewport.width - 24f);
                        float lockX = viewport.xMax - 12f - lockWidth - (showAcceleration ? width + 8f : 0f);
                        float lockY = viewport.yMax - 182f;
                        if (lockX < viewport.x + 12f)
                        {
                            lockX = viewport.xMax - 12f - lockWidth;
                            lockY -= 32f;
                        }
                        var lockPanel = new Rect(lockX, lockY, lockWidth, 28f);
                        GUI.color = Color.white;
                        Widgets.DrawWindowBackground(lockPanel);
                        GUI.color = new Color(1f, .8f, .35f);
                        Widgets.Label(lockPanel.ContractedBy(6f, 0f), lockLabel);
                        if (!_view.mouseCaptured)
                            TooltipHandler.TipRegion(lockPanel, "Rim3D_SettingLockClientPawnsHint".Translate());
                    }
                    if (!showAcceleration) return;
                    // Above the pawn's equipment HUD, away from central action notices.
                    var panel = new Rect(viewport.xMax - width - 12f, viewport.yMax - 182f, width, 28f);
                    GUI.color = Color.white;
                    Widgets.DrawWindowBackground(panel);
                    GUI.color = active ? Color.white : new Color(.7f, .7f, .7f);
                    GUI.DrawTexture(new Rect(panel.x + 6f, panel.y + 2f, 32f, 24f),
                        TexButton.SpeedButtonTextures[(int)TimeSpeed.Superfast], ScaleMode.ScaleToFit);
                    Widgets.Label(new Rect(panel.x + 42f, panel.y, panel.width - 48f, panel.height), label);
                    GUI.color = Color.white;
                    if (!_view.mouseCaptured && !_view.TextInputFocused)
                    {
                        TooltipHandler.TipRegion(panel, (active ? "Rim3D_ActionAccelerationActive" :
                            "Rim3D_ActionAccelerationReady").TranslateBindings().ToString() +
                            (Rim3DMultiplayer.Active ? "\n" + "Rim3D_ActionAccelerationMultiplayer".TranslateBindings().ToString() : ""));
                        if (Widgets.ButtonInvisible(panel)) _view.ToggleActionAcceleration();
                    }
                }
                finally
                {
                    GUI.color = previousColor;
                    Text.Font = previousFont;
                    Text.Anchor = previousAnchor;
                    Text.WordWrap = previousWrap;
                }
            }

            internal void CollectNearbyItemLabels()
            {
                _view.nearbyCountedItems.Clear();
                _nearbyProjects.Clear();
                var eye = _view.CameraMapPosition;
                // Labels already disappear at six metres. Read the local ThingGrid
                // with a one-cell margin for draw offsets, rather than every stack
                // on the map. Deduplicate things occupying more than one cell.
                int minX = Mathf.Max(0, Mathf.FloorToInt(eye.x - 7));
                int maxX = Mathf.Min(_view.map.Size.x - 1, Mathf.FloorToInt(eye.x + 7));
                int minZ = Mathf.Max(0, Mathf.FloorToInt(eye.z - 7));
                int maxZ = Mathf.Min(_view.map.Size.z - 1, Mathf.FloorToInt(eye.z + 7));
                for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (_view.map.fogGrid.IsFogged(cell)) continue;
                    var things = _view.map.thingGrid.ThingsListAt(cell);
                    for (int i = 0; i < things.Count; i++)
                    {
                        var thing = things[i];
                        if (thing.def.category == ThingCategory.Item && thing.stackCount > 1 && thing.Spawned && !thing.Destroyed && thing.Map == _view.map &&
                            !(_view._worldOverlays.ShowBeauty && _view._worldOverlays.InspectionActive))
                            _view.nearbyCountedItems.Add(thing);
                        else if (!_view._constructionPlansHidden && (thing is Blueprint || thing is Frame)) _nearbyProjects.Add(thing);
                    }
                }
            }

            internal bool PlantHasWorker(Thing plant)
            {
                foreach (var pawn in _view.map.mapPawns.AllPawnsSpawned)
                {
                    var job = pawn.CurJob;
                    if (job == null || pawn.Dead || pawn.Downed || pawn.pather.Moving ||
                        (pawn == _view.ControlledPawn && !_view.controller.IsActing)) continue;
                    if (job.targetA.Thing == plant && pawn.Position.AdjacentTo8WayOrInside(plant.Position)) return true;
                }
                return false;
            }

            internal float ProgressLabelOffset(Pawn pawn) => pawn == null ? 10 :
                (pawn.equipment?.Primary != null || pawn.carryTracker?.CarriedThing != null ? 88 : 68);

            internal string ActionReport(Pawn pawn)
            {
                if (pawn.Dead) return "Rim3D_Dead".Translate().ToString();
                if (pawn.Downed) return "Rim3D_Downed".Translate().ToString();
                if (pawn == _view.ControlledPawn && !_view.controller.IsActing) return _view.Status.NullOrEmpty() ? "Rim3D_ManualControl".TranslateBindings().ToString() : _view.Status;
                return pawn.jobs?.curDriver?.GetReport() ?? "Rim3D_NoCurrentAction".Translate().ToString();
            }

            internal void DrawPawnHud(Rect viewport)
            {
                var pawn = _view.ControlledPawn;
                float width = Mathf.Min(370, viewport.width - 24);
                var panel = new Rect(viewport.xMax - width - 12, Mathf.Max(8, viewport.height - 146), width, 136);
                if (pawn.Dead || pawn.Downed || _view.controller.IsActing || !_view.Status.NullOrEmpty())
                    _view.Badge(new Rect(panel.x + 10, panel.y + 6, panel.width - 20, 24), _view.ActionReport(pawn), _view.hudStyle);
                var carried = pawn.carryTracker?.CarriedThing;
                if (carried == null) return;
                string carriedLabel = carried.LabelShort + (carried.stackCount > 1 ? " ×" + carried.stackCount : "");
                float carriedWidth = Mathf.Min(viewport.width - 24,
                    _view.hudStyle.CalcSize(new GUIContent(carriedLabel)).x + 36);
                _view.HudThing(new Rect(viewport.center.x - carriedWidth * .5f, viewport.yMax - 110,
                    carriedWidth, 30), carried, carriedLabel);
            }

            internal void HudThing(Rect rect, Thing thing, string emptyText)
            {
                if (thing == null) { _view.Badge(rect, emptyText, _view.hudStyle); return; }
                _view.DrawHeldThingIcon(new Rect(rect.x, rect.y, 28, 28), thing);
                _view.Badge(new Rect(rect.x + 36, rect.y, rect.width - 36, rect.height),
                    thing.LabelShort + (thing.stackCount > 1 ? " ×" + thing.stackCount : ""), _view.hudStyle);
            }

            internal void DrawHeldThingIcon(Rect rect, Thing thing)
            {
                if (HeldHumanPawn(thing) == null) { Widgets.ThingIcon(rect, thing); return; }
                GUI.DrawTexture(rect, _view.HeldThingMaterial(thing, null, false).mainTexture, ScaleMode.ScaleToFit, true);
            }

            internal void Badge(Rect rect, string text, GUIStyle style, float alpha = 1)
            {
                if (text.NullOrEmpty()) return;
                var oldColor = GUI.color;
                var oldTextColor = style.normal.textColor;
                GUI.color = new Color(1, 1, 1, alpha);
                style.normal.textColor = new Color(0, 0, 0, .75f);
                var shadow = rect;
                shadow.position += new Vector2(1, 1);
                GUI.Label(shadow, text, style);
                style.normal.textColor = Color.white;
                GUI.Label(rect, text, style);
                style.normal.textColor = oldTextColor;
                GUI.color = oldColor;
            }

            internal void ProgressBar(Rect rect, float progress, float alpha = 1)
            {
                if (float.IsNaN(progress) || float.IsInfinity(progress)) return;
                progress = Mathf.Clamp01(progress);
                var oldColor = GUI.color;
                GUI.color = new Color(1, 1, 1, alpha);
                Widgets.DrawBoxSolid(rect, PanelColor);
                var fill = new Rect(rect.x + 2, rect.y + 2, (rect.width - 4) * progress, rect.height - 4);
                Widgets.DrawBoxSolid(fill, ProgressColor);
                _view.Badge(rect, Mathf.RoundToInt(progress * 100) + "%", _view.countStyle, alpha);
                GUI.color = oldColor;
            }

        }

        readonly HeldThingPresentation _heldThingPresentation;

        sealed partial class HeldThingPresentation
        {
            readonly MapView _view;

            internal HeldThingPresentation(MapView view)
            {
                _view = view;
            }

            readonly Dictionary<Material, Material> _weaponVolumeMaterials = new Dictionary<Material, Material>();

            readonly Dictionary<(Material, float, float, bool), Geometry> _weaponVolumes = new Dictionary<(Material, float, float, bool), Geometry>();

            readonly Dictionary<Material, (byte[] Mask, int Width, int Height)> _volumeMasks = new Dictionary<Material, (byte[], int, int)>();

            readonly Dictionary<Pawn, HeldCard> _carriedItems = new Dictionary<Pawn, HeldCard>();

            readonly Dictionary<ThingDef, float> _tableSurfaceHeights = new Dictionary<ThingDef, float>();

            readonly List<Thing> _removedWorkIngredients = new List<Thing>();

            readonly List<Pawn> _removedPawns = new List<Pawn>();

            internal void TrackActivityThing(Thing thing)
            {
                _view.TrackCombatThing(thing);
                _view.TrackDropPod(thing);
                // Registration can notify before Spawned changes. Visibility is read
                // when drawing; don't depend on the ordering of ThingGrid callbacks.
                if (thing is MoteProgressBar progress) _view.progressMotes.Add(progress);
            }

            internal void UpdateHeldThings()
            {
                _view.workSurfaceIngredients.Clear();
                foreach (var pawn in _view.map.mapPawns.AllPawnsSpawned)
                {
                    var job = pawn.CurJob;
                    if (!(pawn.jobs?.curDriver is Verse.AI.JobDriver_DoBill) || pawn.pather.Moving ||
                        !(job.targetA.Thing is Building_WorkTable table) ||
                        (pawn.Position != table.InteractionCell && !(table.def.defName == "Campfire" &&
                            PawnControl.ForPawn(pawn) != null && Verse.ReachabilityImmediate.CanReachImmediate(
                                pawn, table, Verse.AI.PathEndMode.Touch))) ||
                        job.placedThings == null) continue;
                    foreach (var ingredient in job.placedThings)
                        if (ingredient.Count > 0 && ingredient.thing != null && ingredient.thing.Spawned)
                            _view.workSurfaceIngredients[ingredient.thing] = table;
                }
                foreach (var pawn in _view.map.mapPawns.AllPawnsSpawned)
                {
                    _view.UpdateHeldCard(_view.weapons, pawn, pawn.equipment?.Primary, false);
                    ApplyWeaponAnimation(_view.weapons, pawn, false);
                    _view.UpdateHeldCard(_carriedItems, pawn, pawn.carryTracker?.CarriedThing, true);
                    ApplyWeaponAnimation(_carriedItems, pawn, true);
                }
                _view.RemoveAbsentCards(_view.weapons);
                _view.RemoveAbsentCards(_carriedItems);
                _view.UpdateWorkIngredientCards();
            }

            void ApplyWeaponAnimation(Dictionary<Pawn, HeldCard> cards, Pawn pawn, bool carried)
            {
                if (!cards.TryGetValue(pawn, out var card) || card.Object == null || !card.Object.activeSelf) return;
                if (!API.RimVisionPresentation.HasProviders && !card.GripsCaptured) { card.VisualMuzzle = card.Muzzle + Vector3.right * .015f; return; }
                if (!card.GripsCaptured)
                {
                    if (card.RightHand != null) { card.DefaultRightGrip = card.RightHand.localPosition; card.DefaultRightRotation = card.RightHand.localRotation; card.DefaultRightScale = card.RightHand.localScale; }
                    if (card.LeftHand != null) { card.DefaultLeftGrip = card.LeftHand.localPosition; card.DefaultLeftRotation = card.LeftHand.localRotation; card.DefaultLeftScale = card.LeftHand.localScale; }
                    card.GripsCaptured = true;
                }
                var transform = card.Object.transform;
                var frame = new API.RimVisionWeaponAnimationFrame
                {
                    Pose = new API.RimVisionPose(transform.localPosition, transform.localRotation), Scale = transform.localScale,
                    RightHand = new API.RimVisionPose(card.DefaultRightGrip, card.RightHand != null ? card.DefaultRightRotation : Quaternion.identity),
                    LeftHand = new API.RimVisionPose(card.DefaultLeftGrip, card.LeftHand != null ? card.DefaultLeftRotation : Quaternion.identity),
                    RightHandScale = card.RightHand != null ? card.DefaultRightScale : Vector3.one,
                    LeftHandScale = card.LeftHand != null ? card.DefaultLeftScale : Vector3.one,
                    Muzzle = card.Muzzle + Vector3.right * .015f, DrawItem = true,
                    DrawRightHand = card.RightHand != null && card.RightHand.gameObject.activeSelf,
                    DrawLeftHand = card.LeftHand != null && card.LeftHand.gameObject.activeSelf
                };
                API.RimVisionPresentation.AnimateWeapon(new API.RimVisionAnimationContext(pawn, card.Thing, _view.map,
                    _view.ControlledPawn, false, carried, pawn == _view.ControlledPawn ? _view._aimBlend : 0,
                    pawn == _view.ControlledPawn ? _view._weaponRecoil : 0, pawn == _view.ControlledPawn ? _view.controller.LastMeleeAttackTick : -1000), ref frame);
                transform.localPosition = frame.Pose.Position;
                transform.localRotation = frame.Pose.Rotation.normalized;
                transform.localScale = frame.Scale;
                card.Object.SetActive(frame.DrawItem);
                card.VisualMuzzle = frame.Muzzle;
                if (card.RightHand != null)
                {
                    card.RightHand.localPosition = frame.RightHand.Position; card.RightHand.localRotation = frame.RightHand.Rotation.normalized;
                    card.RightHand.localScale = frame.RightHandScale; card.RightHand.gameObject.SetActive(frame.DrawRightHand);
                }
                if (card.LeftHand != null)
                {
                    card.LeftHand.localPosition = frame.LeftHand.Position; card.LeftHand.localRotation = frame.LeftHand.Rotation.normalized;
                    card.LeftHand.localScale = frame.LeftHandScale; card.LeftHand.gameObject.SetActive(frame.DrawLeftHand);
                }
            }

            internal void UpdateWorkIngredientCards()
            {
                _removedWorkIngredients.Clear();
                foreach (var entry in _view.workIngredientCards)
                    if (!_view.workSurfaceIngredients.ContainsKey(entry.Key) || entry.Key.Destroyed || !entry.Key.Spawned)
                        _removedWorkIngredients.Add(entry.Key);
                foreach (var thing in _removedWorkIngredients)
                {
                    _view.DestroyHeldCard(_view.workIngredientCards[thing]);
                    _view.workIngredientCards.Remove(thing);
                    if (thing.Spawned) _view.MarkNeighbors(thing.Position);
                }
                foreach (var entry in _view.workSurfaceIngredients)
                {
                    var thing = entry.Key;
                    if (thing is Corpse || thing.Graphic == null) continue;
                    var table = entry.Value;
                    var source = thing.Graphic.MatSingleFor(thing);
                    if (source?.mainTexture == null) continue;
                    if (!_view.workIngredientCards.TryGetValue(thing, out var card))
                    {
                        card = new HeldCard();
                        _view.workIngredientCards.Add(thing, card);
                        _view.MarkNeighbors(thing.Position);
                    }
                    if (card.Object == null || card.Source != source)
                    {
                        _view.DestroyHeldCard(card);
                        card.Source = source;
                        var size = thing.Graphic.drawSize;
                        var geometry = new Geometry();
                        geometry.FloorCard(Vector3.up * .025f, Mathf.Min(size.x, 1), Mathf.Min(size.y, 1));
                        var previousParent = _view.geometryParent;
                        _view.geometryParent = null;
                        try { card.Object = _view.CreateMesh(_view.ConvertMaterial(source, false), geometry, "Work ingredient: " + thing.LabelShort); }
                        finally { _view.geometryParent = previousParent; }
                    }
                    var position = thing.DrawPos;
                    var bounds = table.OccupiedRect();
                    position.x = Mathf.Clamp(position.x, bounds.minX + .35f, bounds.maxX + .65f);
                    position.z = Mathf.Clamp(position.z, bounds.minZ + .35f, bounds.maxZ + .65f);
                    position.y = _view.TableSurfaceHeight(table);
                    card.Object.transform.localPosition = position;
                    card.Object.transform.localRotation = Quaternion.Euler(0, table.Rotation.AsAngle, 0);
                    card.Object.SetActive(table.Spawned && !_view.map.fogGrid.IsFogged(table.Position));
                }
            }

            internal void RemoveAbsentCards(Dictionary<Pawn, HeldCard> cards)
            {
                _removedPawns.Clear();
                foreach (var entry in cards)
                    if (!entry.Key.Spawned || entry.Key.Map != _view.map) _removedPawns.Add(entry.Key);
                foreach (var pawn in _removedPawns)
                {
                    _view.DestroyHeldCard(cards[pawn]);
                    cards.Remove(pawn);
                }
            }

            internal void DestroyHeldCard(HeldCard card)
            {
                card.GripsCaptured = false;
                card.VisualMuzzle = Vector3.zero;
                if (card.HandMaterial != null)
                {
                    _view.materials.Remove(card.HandMaterial);
                    UnityEngine.Object.Destroy(card.HandMaterial);
                    card.HandMaterial = null;
                }
                card.RightHand = null;
                card.LeftHand = null;
                if (card.Object == null) return;
                _view.RemoveMovingShadows(card.Object.transform);
                foreach (var filter in card.Object.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = filter.sharedMesh;
                    _view.meshes.Remove(mesh);
                    UnityEngine.Object.Destroy(mesh);
                }
                UnityEngine.Object.Destroy(card.Object);
                card.Object = null;
                card.Model = false;
            }

            internal bool CreateCarriedModel(HeldCard card, Thing thing, Graphic graphic)
            {
                if (thing is Pawn || thing is Corpse || graphic == null) return false;
                var model = _view.ThingModel(thing, graphic);
                if (model == null) return false;
                var groups = new Dictionary<Material, Geometry>();
                if (!_view.TryDrawOptionalModel(thing, graphic, Vector3.zero, groups, model)) return false;
                var bounds = new Bounds();
                bool hasBounds = false;
                foreach (var group in groups)
                    foreach (var vertex in group.Value.Vertices)
                    {
                        if (!hasBounds) { bounds = new Bounds(vertex, Vector3.zero); hasBounds = true; }
                        else bounds.Encapsulate(vertex);
                    }
                if (!hasBounds) return false;
                float scale = .65f / Mathf.Max(.01f, Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)));
                card.Object = new GameObject("Carried model: " + thing.LabelShort) { layer = Layer, hideFlags = HideFlags.DontSave };
                card.Object.transform.SetParent(_view.root.transform, false);
                var previousParent = _view.geometryParent;
                _view.geometryParent = card.Object.transform;
                try
                {
                    foreach (var group in groups)
                    {
                        for (int i = 0; i < group.Value.Vertices.Count; i++)
                            group.Value.Vertices[i] = (group.Value.Vertices[i] - bounds.center) * scale;
                        group.Value.TransformOutlineCenters(bounds.center, scale);
                        _view.CreateMesh(group.Key, group.Value, "Carried model part");
                    }
                }
                finally { _view.geometryParent = previousParent; }
                card.Model = true;
                card.ModelHalfHeight = bounds.size.y * scale * .5f;
                return true;
            }

            internal void UpdateHeldCard(Dictionary<Pawn, HeldCard> cards, Pawn pawn, Thing thing, bool carried)
            {
                var graphic = thing != null && !thing.Destroyed ? HeldThingGraphic(thing) : null;
                if (thing == null || thing.Destroyed || graphic == null && HeldHumanPawn(thing) == null)
                {
                    if (cards.TryGetValue(pawn, out var old)) { _view.DestroyHeldCard(old); cards.Remove(pawn); }
                    return;
                }
                bool animal = IsHeldAnimal(thing);
                bool human = HeldHumanPawn(thing) != null;
                var source = _view.HeldThingMaterial(thing, graphic, !carried);
                if (source == null || source.mainTexture == null) return;
                if (!cards.TryGetValue(pawn, out var card)) { card = new HeldCard(); cards.Add(pawn, card); }
                var size = HeldThingSize(thing, graphic);
                if (card.Thing != thing || card.Source != source || card.Size != size || card.Object == null)
                {
                    _view.DestroyHeldCard(card);
                    card.RunBlend = card.AimBlend = 0;
                    card.RunFrame = -1;
                    card.Thing = thing;
                    card.Source = source;
                    card.Size = size;
                    if (!carried || !CreateCarriedModel(card, thing, graphic))
                    {
                        var material = human ? source : _view.ConvertMaterial(source, false);
                        // Held sprites aren't plants: their orientation follows their transform.
                        float width = animal || human ? size.x * PawnVisualScale : carried ? Mathf.Clamp(size.x, .25f, 1.25f) * .65f : size.x * PawnVisualScale * WeaponSpriteScale;
                        float height = animal || human ? size.y * PawnVisualScale : carried ? Mathf.Clamp(size.y, .25f, 1.25f) * .65f : size.y * PawnVisualScale * WeaponSpriteScale;
                        Geometry geometry;
                        if (!carried && (thing.def.IsRangedWeapon || thing.def.IsMeleeWeapon))
                        {
                            card.Muzzle = WeaponMuzzle(material, width, height);
                            geometry = _view.WeaponVolume(material, width, height, thing.def.IsRangedWeapon && thing.def.techLevel >= TechLevel.Industrial);
                            material = _view.WeaponVolumeMaterial(material);
                        }
                        else
                        {
                            geometry = new Geometry();
                            geometry.Upright(new Vector3(0, -height * .5f, 0), width, height, false);
                        }
                        _view.geometryParent = null;
                        if (!carried)
                        {
                            // Vanilla shaders may cull back faces. Keep both sides visible during world-space aiming.
                            var doubleSided = new Geometry(); doubleSided.Append(geometry, Vector3.zero, Quaternion.identity); geometry = doubleSided; int count = geometry.Indices.Count;
                            for (int t = 0; t < count; t += 3)
                            { geometry.Indices.Add(geometry.Indices[t+2]); geometry.Indices.Add(geometry.Indices[t+1]); geometry.Indices.Add(geometry.Indices[t]); }
                        }
                        card.Object = _view.CreateMesh(material, geometry, (carried ? "Carried: " : "Weapon: ") + thing.LabelShort);
                        if (!carried && pawn.RaceProps.Humanlike && (thing.def.IsRangedWeapon || thing.def.IsMeleeWeapon))
                            CreateWeaponHands(card, pawn, thing, width, height);
                    }
                }
                if (card.HandMaterial != null)
                {
                    card.HandMaterial.color = pawn.story?.SkinColor ?? new Color(.65f, .45f, .32f);
                    GetWeaponHands(pawn, out bool rightHand, out bool leftHand);
                    if (card.RightHand != null) card.RightHand.gameObject.SetActive(rightHand);
                    if (card.LeftHand != null) card.LeftHand.gameObject.SetActive(leftHand);
                }
                // Ask the same driver that positions carried food in the 2D renderer.
                // This also supports manual ingestion without inferring a table from proximity.
                _view.HeldThingOnEatSurface(pawn, carried ? thing : null, out var eatSurface, out var foodPosition, out var foodFlip);
                bool visible = pawn.Spawned && pawn.Map == _view.map && !_view.map.fogGrid.IsFogged(pawn.Position) &&
                    !PawnIsProne(pawn) && (pawn != _view.ControlledPawn || _view._viewCamera.HasFrame && _view._viewCamera.Frame.ShowBody || eatSurface != null);
                if (carried && pawn.Drafted && !human) visible = false;
                bool aiming = pawn.stances?.curStance is Stance_Busy stance &&
                    (!stance.neverAimWeapon || stance.verb?.IsMeleeAttack == true) && stance.focusTarg.IsValid;
                var manualAim = !carried && Rim3DMultiplayer.Active ? Rim3DMultiplayer.GetAim?.Invoke(pawn) : null;
                if (!carried) visible &= pawn.CurJob?.def.neverShowWeapon != true &&
                    (aiming || pawn.Drafted || PawnRenderUtility.CarryWeaponOpenly(pawn) || pawn.HostileTo(Faction.OfPlayer));
                card.Object.SetActive(visible);
                if (!visible) { card.RunBlend = card.AimBlend = 0; card.RunFrame = -1; return; }
                if (card.RunFrame != Time.frameCount)
                {
                    card.RunFrame = Time.frameCount;
                    card.AimBlend = Mathf.MoveTowards(card.AimBlend, !carried && Rim3DMultiplayer.Active &&
                        Rim3DMultiplayer.GetWeaponAiming?.Invoke(pawn) == true ? 1 : 0, Mathf.Min(Time.unscaledDeltaTime, .1f) * 9);
                    if (!Find.TickManager.Paused)
                        card.RunBlend = Mathf.MoveTowards(card.RunBlend, !Rim3DSettings.Current.ShootWhileRunningEnabled && !carried && thing.def.IsRangedWeapon &&
                            PawnControl.ForPawn(pawn)?.IsRunning == true ? 1 : 0, Mathf.Min(Time.unscaledDeltaTime, .1f) * 7);
                }
                if (eatSurface != null)
                {
                    if (card.EatSurface != eatSurface)
                    {
                        card.EatSurface = eatSurface;
                        card.EatSurfaceHeight = _view.TableSurfaceHeight(eatSurface);
                    }
                    foodPosition.y = card.EatSurfaceHeight + .025f;
                    card.Object.transform.localPosition = foodPosition;
                    card.Object.transform.localRotation = Quaternion.Euler(card.Model ? 0 : 90, thing.Rotation.AsAngle, 0);
                    if (card.Model) card.Object.transform.localPosition += Vector3.up * card.ModelHalfHeight;
                    card.Object.transform.localScale = new Vector3(foodFlip ? -1 : 1, 1, 1);
                    return;
                }
                if (!carried && (aiming || manualAim.HasValue || card.RunBlend > 0))
                {
                    _view.AimWeaponInWorld(card, pawn, thing, aiming ? (Stance_Busy)pawn.stances.curStance : null, manualAim);
                    return;
                }
                var position = pawn.DrawPos;
                position.y = carried ? .85f : .95f;
                position.y += _view.PawnCrouchOffset(pawn);
                var rotation = Quaternion.Euler(0, _view.yaw, 0);
                var offset = new Vector3(-.3f, 0, -.12f);
                float tilt = human && carried ? 90 : 0;
                bool flip = false;
                if (!carried)
                {
                    // The original DrawCarriedWeapon / DrawEquipmentAiming layout,
                    // lifted from the game's XZ sprite plane into our upright plane.
                    float factor = pawn.ageTracker.CurLifeStage.equipmentDrawDistanceFactor;
                    var facing = Rot4.FromAngleFlat(pawn.Rotation.AsAngle - _view.yaw);
                    float aimAngle = facing == Rot4.West ? 217 : 143;
                    Vector3 nativeOffset;
                    if (aiming)
                    {
                        var busy = (Stance_Busy)pawn.stances.curStance;
                        var target = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
                        float worldAngle = (target - pawn.DrawPos).MagnitudeHorizontalSquared() > .001f
                            ? (target - pawn.DrawPos).AngleFlat() : 0;
                        var verb = pawn.CurrentEffectiveVerb;
                        if (verb != null && verb.AimAngleOverride.HasValue) worldAngle = verb.AimAngleOverride.Value;
                        aimAngle = Mathf.Repeat(worldAngle - _view.yaw, 360);
                        nativeOffset = new Vector3(0, 0, .4f + thing.def.equippedDistanceOffset).RotatedBy(aimAngle) * factor;
                    }
                    else
                    {
                        nativeOffset = facing == Rot4.North ? new Vector3(0, 0, -.11f) :
                            facing == Rot4.East ? new Vector3(.22f, 0, -.22f) :
                            facing == Rot4.West ? new Vector3(-.22f, 0, -.22f) : new Vector3(0, 0, -.22f);
                        nativeOffset *= factor;
                    }
                    float angle = aimAngle - 90;
                    flip = aimAngle > 200 && aimAngle < 340;
                    angle += flip ? -180 - thing.def.equippedAngleOffset : thing.def.equippedAngleOffset;
                    var equippable = thing.TryGetComp<CompEquippable>();
                    if (equippable != null)
                    {
                        EquipmentUtility.Recoil(thing.def, EquipmentUtility.GetRecoilVerb(equippable.AllVerbs),
                            out var recoilOffset, out var recoilAngle, Mathf.Repeat(aimAngle + _view.yaw, 360));
                        nativeOffset += Quaternion.Euler(0, -_view.yaw, 0) * recoilOffset;
                        angle += recoilAngle;
                    }
                    offset = new Vector3(nativeOffset.x * PawnVisualScale, nativeOffset.z * PawnVisualScale, -.025f);
                    tilt = -angle;
                }
                card.Object.transform.localPosition = position + rotation * offset;
                card.Object.transform.localRotation = rotation * Quaternion.Euler(0, 0, tilt);
                card.Object.transform.localScale = new Vector3(flip ? -1 : 1, 1, 1);
            }

            internal bool HeldThingOnEatSurface(Pawn pawn, Thing thing, out Building surface, out Vector3 position, out bool flip)
            {
                surface = null;
                position = pawn.DrawPos;
                flip = false;
                if (thing?.def.ingestible?.ingestHoldUsesTable != true || pawn.jobs?.curDriver == null ||
                    pawn.pather.Moving || !pawn.jobs.curDriver.ModifyCarriedThingDrawPos(ref position, ref flip)) return false;
                var cell = position.ToIntVec3();
                if (!cell.InBounds(_view.map) || !cell.AdjacentToCardinal(pawn.Position)) return false;
                var building = cell.GetEdifice(_view.map);
                if (building?.def.surfaceType != SurfaceType.Eat) return false;
                surface = building;
                return true;
            }

            internal float TableSurfaceHeight(Building table)
            {
                if (_tableSurfaceHeights.TryGetValue(table.def, out var height)) return height;
                var model = _view.ConstructionModel(table.def, table.Graphic.path);
                height = .65f;
                float largestArea = 0;
                if (model != null)
                    foreach (var face in model.Faces)
                    {
                        if (face.Normal.y < .99f) continue;
                        var min = model.Vertices[face.Indices[0]];
                        var max = min;
                        foreach (int index in face.Indices)
                        {
                            min = Vector3.Min(min, model.Vertices[index]);
                            max = Vector3.Max(max, model.Vertices[index]);
                        }
                        float area = (max.x - min.x) * (max.z - min.z);
                        if (area <= largestArea) continue;
                        largestArea = area;
                        height = max.y + model.HeightOffset;
                    }
                _tableSurfaceHeights[table.def] = height;
                return height;
            }

            internal static void RunningWeaponPose(Pawn pawn, out Vector3 offset, out Quaternion rotation)
            {
                float phase = Find.TickManager.TicksGame * (17f / 60f) + pawn.thingIDNumber * .7f;
                float stride = Mathf.Sin(phase);
                float bounce = Mathf.Cos(phase * 2);
                offset = new Vector3(stride * .006f, bounce * .005f, stride * .004f);
                rotation = Quaternion.Euler(bounce * .8f, 180 + stride * .8f, 18 + stride * 1.2f);
            }

            Vector3 WeaponMuzzle(Material material, float width, float height)
            {
                _view.BottomPadding(material);
                if (!_view.spriteMasks.TryGetValue(material, out var mask)) return new Vector3(width * .5f, 0, 0);
                float alpha = material.color.a;
                for (int x = 63; x >= 0; x--)
                {
                    int bottom = 80, top = -1;
                    for (int y = 0; y < 80; y++)
                        if (mask[y * 64 + x] * alpha >= 64)
                        {
                            bottom = Mathf.Min(bottom, y);
                            top = Mathf.Max(top, y);
                        }
                    if (top >= bottom)
                        return new Vector3(((x + 1) / 64f - .5f) * width,
                            ((bottom + top + 1) / 160f - .5f) * height, 0);
                }
                return new Vector3(width * .5f, 0, 0);
            }

            internal bool TryGetWeaponMuzzle(Pawn pawn, out Vector3 muzzle)
            {
                muzzle = default;
                if (!_view.weapons.TryGetValue(pawn, out var card) || card.Object == null ||
                    !card.Object.activeSelf || card.Thing != pawn.equipment?.Primary) return false;
                var transform = card.Object.transform;
                muzzle = transform.localPosition + transform.localRotation *
                    Vector3.Scale(card.VisualMuzzle, transform.localScale);
                return true;
            }

            internal void AimWeaponInWorld(HeldCard card, Pawn pawn, Thing weapon, Stance_Busy stance, Vector3? manualAim = null)
            {
                var targetThing = stance != null && stance.focusTarg.HasThing ? stance.focusTarg.Thing : null;
                var target = targetThing != null ? targetThing.DrawPos : stance != null ? stance.focusTarg.Cell.ToVector3Shifted() : pawn.DrawPos;
                target.y = targetThing is Pawn targetPawn ? (targetPawn.Downed ? .25f : .95f) :
                    targetThing != null && targetThing.def.category == ThingCategory.Building ? .8f : .15f;
                bool firstPersonWeapon = pawn == _view.ControlledPawn && !(_view._viewCamera.HasFrame && _view._viewCamera.Frame.ShowBody);
                var origin = pawn.DrawPos;
                origin.y = .95f;
                if (!firstPersonWeapon) origin.y += _view.PawnCrouchOffset(pawn) + .23f * card.AimBlend;
                if (firstPersonWeapon)
                    origin = _view.CameraMapPosition + _view.camera.transform.right * .28f - _view.camera.transform.up * .25f;

                if (manualAim.HasValue)
                {
                    // The same world-space weapon volume used by native enemies,
                    // aimed along the player's synchronized camera ray.
                    target = pawn.DrawPos + Vector3.up * (1.35f + _view.PawnCrouchOffset(pawn)) + manualAim.Value * 50;
                }

                if (stance == null && !manualAim.HasValue && card.RunBlend > 0)
                    target = origin + pawn.Rotation.FacingCell.ToVector3() * 50;
                var direction = target - origin;
                if (direction.sqrMagnitude < .0001f) direction = pawn.Rotation.FacingCell.ToVector3();
                var verb = pawn.CurrentEffectiveVerb;
                if (!manualAim.HasValue && verb != null && verb.AimAngleOverride.HasValue)
                {
                    float horizontalLength = new Vector2(direction.x, direction.z).magnitude;
                    var horizontal = Vector3.forward.RotatedBy(verb.AimAngleOverride.Value) * horizontalLength;
                    direction = new Vector3(horizontal.x, direction.y, horizontal.z);
                }
                direction.Normalize();
                if (!firstPersonWeapon && weapon.def.IsRangedWeapon)
                {
                    // A small shoulder offset separates the muzzle from the torso.
                    // Re-aim from the new hand position, retaining verb overrides.
                    var shoulder = Vector3.Cross(Vector3.up, direction);
                    if (shoulder.sqrMagnitude > .0001f)
                    {
                        origin += shoulder.normalized * (Mathf.Lerp(.14f, .07f, card.AimBlend) * PawnVisualScale);
                        if (manualAim.HasValue || verb == null || !verb.AimAngleOverride.HasValue)
                            direction = (target - origin).normalized;
                    }
                }
                // Local X is the sprite's barrel axis. Keep that axis pointed at the
                // actual map target, independent of the viewer's camera orientation.
                var normal = Vector3.Cross(direction, Vector3.up);
                if (normal.sqrMagnitude < .0001f) normal = Vector3.Cross(direction, Vector3.forward);
                normal.Normalize();
                var weaponUp = Vector3.Cross(normal, direction).normalized;
                var rotation = Quaternion.LookRotation(normal, weaponUp);
                float angle = weapon.def.equippedAngleOffset;
                var recoil = Vector3.zero;
                var equippable = weapon.TryGetComp<CompEquippable>();
                if (equippable != null)
                {
                    EquipmentUtility.Recoil(weapon.def, EquipmentUtility.GetRecoilVerb(equippable.AllVerbs),
                        out recoil, out var recoilAngle, direction.AngleFlat());
                    angle += recoilAngle;
                }
                float distanceFromHands = firstPersonWeapon ? .45f :
                    (.4f + weapon.def.equippedDistanceOffset) * pawn.ageTracker.CurLifeStage.equipmentDrawDistanceFactor * PawnVisualScale;
                if (!firstPersonWeapon && weapon.def.IsRangedWeapon) distanceFromHands -= .15f * card.AimBlend * PawnVisualScale;
                float swing = _view.MeleeSwing(pawn);
                if (weapon.def.IsMeleeWeapon)
                {
                    distanceFromHands += swing * .3f;
                    angle += 65 * (1 - swing);
                }
                card.Object.transform.localPosition = origin + direction * distanceFromHands + recoil;
                card.Object.transform.localRotation = rotation * Quaternion.Euler(0, 0, -angle);
                card.Object.transform.localScale = Vector3.one * (firstPersonWeapon ? .22f : 1f);
                if (weapon.def.IsRangedWeapon && card.RunBlend > 0)
                {
                    RunningWeaponPose(pawn, out var runOffset, out var runRotation);
                    var facing = Quaternion.Euler(0, pawn.Rotation.AsAngle, 0);
                    var runOrigin = pawn.DrawPos;
                    runOrigin.y = .95f;
                    runOrigin.y += _view.PawnCrouchOffset(pawn);
                    var runHands = runOrigin + facing * new Vector3(0, 0, .18f * PawnVisualScale);
                    var runWeaponRotation = facing * runRotation *
                        Quaternion.Euler(0, 0, -weapon.def.equippedAngleOffset);
                    card.Object.transform.localPosition = Vector3.Lerp(card.Object.transform.localPosition,
                        runHands + facing * runOffset * PawnVisualScale, card.RunBlend);
                    card.Object.transform.localRotation = Quaternion.Slerp(card.Object.transform.localRotation,
                        runWeaponRotation, card.RunBlend);
                }
            }

            internal Material WeaponVolumeMaterial(Material material)
            {
                if (!_weaponVolumeMaterials.TryGetValue(material, out var volumeMaterial))
                {
                    volumeMaterial = new Material(material) { hideFlags = HideFlags.DontSave };
                    volumeMaterial.SetFloat("_WeaponVolume", 1);
                    _view.materials.Add(volumeMaterial);
                    _weaponVolumeMaterials.Add(material, volumeMaterial);
                }
                return volumeMaterial;
            }

            internal byte[] ReadVolumeMask(Material material, byte[] fallback, out int width, out int height)
            {
                if (_volumeMasks.TryGetValue(material, out var cached))
                {
                    width = cached.Width;
                    height = cached.Height;
                    return cached.Mask;
                }
                var texture = material.mainTexture;
                width = Mathf.Clamp(Mathf.CeilToInt(texture.width * Mathf.Abs(material.mainTextureScale.x)), 1, 256);
                height = Mathf.Clamp(Mathf.CeilToInt(texture.height * Mathf.Abs(material.mainTextureScale.y)), 1, 512);
                var previous = RenderTexture.active;
                RenderTexture sample = null;
                Texture2D pixels = null;
                try
                {
                    sample = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(texture, sample, material.mainTextureScale, material.mainTextureOffset);
                    RenderTexture.active = sample;
                    pixels = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    var colors = pixels.GetPixels32();
                    var mask = new byte[colors.Length];
                    for (int i = 0; i < colors.Length; i++) mask[i] = colors[i].a;
                    _volumeMasks.Add(material, (mask, width, height));
                    return mask;
                }
                catch (Exception error)
                {
                    Log.Warning("RimVision item contour: " + error.Message);
                    width = 64;
                    height = 80;
                    _volumeMasks.Add(material, (fallback, width, height));
                    return fallback;
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (sample != null) RenderTexture.ReleaseTemporary(sample);
                    if (pixels != null) UnityEngine.Object.Destroy(pixels);
                }
            }

            internal void AppendVolumeContour(Geometry geometry, Material material, byte[] fallback,
                float width, float height, float halfDepth)
            {
                var mask = _view.ReadVolumeMask(material, fallback, out int columns, out int rows);
                float alpha = material.color.a / 255f;
                float Sample(int x, int y) => x >= 0 && x < columns && y >= 0 && y < rows ? mask[y * columns + x] * alpha : 0;
                var points = new Vector2[4];
                var values = new float[4];
                var edges = new Vector2[4];
                for (int y = -1; y < rows; y++)
                    for (int x = -1; x < columns; x++)
                    {
                        values[0] = Sample(x, y); values[1] = Sample(x + 1, y);
                        values[2] = Sample(x + 1, y + 1); values[3] = Sample(x, y + 1);
                        int bits = 0;
                        for (int i = 0; i < 4; i++) if (values[i] >= .25f) bits |= 1 << i;
                        if (bits == 0 || bits == 15) continue;
                        points[0] = new Vector2((x + .5f) / columns, (y + .5f) / rows);
                        points[1] = points[0] + Vector2.right / columns;
                        points[2] = points[1] + Vector2.up / rows;
                        points[3] = points[0] + Vector2.up / rows;
                        for (int i = 0; i < 4; i++)
                        {
                            int next = (i + 1) % 4;
                            if ((values[i] >= .25f) != (values[next] >= .25f))
                            {
                                var edge = Vector2.Lerp(points[i], points[next], (.25f - values[i]) / (values[next] - values[i]));
                                edges[i] = new Vector2(Mathf.Clamp01(edge.x), Mathf.Clamp01(edge.y));
                            }
                        }
                        void Segment(int first, int second)
                        {
                            var uvA = edges[first]; var uvB = edges[second];
                            var middle = (uvA + uvB) * .5f;
                            int inside = -1;
                            float nearest = float.MaxValue;
                            for (int i = 0; i < 4; i++)
                                if (values[i] >= .25f && (points[i] - middle).sqrMagnitude < nearest)
                                { inside = i; nearest = (points[i] - middle).sqrMagnitude; }
                            var a = new Vector3((uvA.x - .5f) * width, (uvA.y - .5f) * height, -halfDepth);
                            var b = new Vector3((uvB.x - .5f) * width, (uvB.y - .5f) * height, -halfDepth);
                            var normal = new Vector3(b.y - a.y, a.x - b.x, 0).normalized;
                            var towardInside = new Vector3((points[inside].x - middle.x) * width,
                                (points[inside].y - middle.y) * height, 0);
                            if (Vector3.Dot(normal, towardInside) > 0)
                            { var swap = a; a = b; b = swap; normal = -normal; }
                            if ((b - a).sqrMagnitude < 1e-12f) return;
                            var uv = points[inside];
                            geometry.Quad(a, b, b + Vector3.forward * (2 * halfDepth), a + Vector3.forward * (2 * halfDepth),
                                normal, uv, uv, uv, uv);
                            for (int i = 0; i < 4; i++) geometry.Colors.Add(new Color(1, 1, 1, .5f));
                        }
                        switch (bits)
                        {
                            case 1: case 14: Segment(3, 0); break;
                            case 2: case 13: Segment(0, 1); break;
                            case 3: case 12: Segment(3, 1); break;
                            case 4: case 11: Segment(1, 2); break;
                            case 6: case 9: Segment(0, 2); break;
                            case 7: case 8: Segment(3, 2); break;
                            case 5: case 10:
                                bool centerFilled = (values[0] + values[1] + values[2] + values[3]) * .25f >= .25f;
                                if (centerFilled == (bits == 5)) { Segment(0, 1); Segment(2, 3); }
                                else { Segment(3, 0); Segment(1, 2); }
                                break;
                        }
                    }
            }

            internal static Mesh CreateHandMesh()
            {
                const int segments = 12, rings = 8;
                var vertices = new Vector3[(rings + 1) * (segments + 1)];
                var normals = new Vector3[vertices.Length];
                var triangles = new int[segments * (rings - 1) * 6];
                int index = 0;
                for (int y = 0; y <= rings; y++)
                {
                    float latitude = y * Mathf.PI / rings;
                    for (int x = 0; x <= segments; x++)
                    {
                        float longitude = x * Mathf.PI * 2 / segments;
                        int vertex = y * (segments + 1) + x;
                        var normal = new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude));
                        normals[vertex] = normal;
                        vertices[vertex] = normal * .5f;
                        if (y == rings || x == segments) continue;
                        int next = vertex + segments + 1;
                        if (y > 0)
                        {
                            triangles[index++] = vertex;
                            triangles[index++] = vertex + 1;
                            triangles[index++] = next;
                        }
                        if (y < rings - 1)
                        {
                            triangles[index++] = vertex + 1;
                            triangles[index++] = next + 1;
                            triangles[index++] = next;
                        }
                    }
                }
                var mesh = new Mesh { name = "RimVision smooth hand", hideFlags = HideFlags.DontSave };
                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.uv = new Vector2[vertices.Length];
                var centers = new System.Collections.Generic.List<Vector4>(vertices.Length);
                for (int i = 0; i < vertices.Length; i++) centers.Add(new Vector4(0, 0, 0, .5f));
                mesh.SetUVs(2, centers);
                mesh.triangles = triangles;
                mesh.RecalculateBounds();
                return mesh;
            }

            internal static void GetWeaponHands(Pawn pawn, out bool right, out bool left)
            {
                right = false;
                left = false;
                var health = pawn.health?.hediffSet;
                var parts = pawn.RaceProps.body.AllParts;
                if (health == null) return;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part.def != BodyPartDefOf.Hand) continue;
                    var representedPart = part;
                    for (var ancestor = part; ancestor != null; ancestor = ancestor.parent)
                        if (health.HasDirectlyAddedPartFor(ancestor))
                        {
                            representedPart = ancestor;
                            break;
                        }
                    if (health.PartIsMissing(representedPart)) continue;
                    if (part.flipGraphic) left = true;
                    else right = true;
                }
            }

            void CreateWeaponHands(HeldCard card, Pawn pawn, Thing weapon, float width, float height)
            {
                var material = _view.ConvertMaterial(card.Source, false);
                var right = new Vector3(-width * .18f, -height * .5f, 0);
                var left = new Vector3(width * .18f, -height * .5f, 0);
                if (weapon.def.IsRangedWeapon && _view.spriteMasks.TryGetValue(material, out var fallback))
                {
                    var mask = ReadVolumeMask(material, fallback, out int columns, out int rows);
                    int minX = columns, maxX = -1;
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < columns; x++)
                            if (mask[y * columns + x] * material.color.a >= 64)
                            {
                                minX = Mathf.Min(minX, x);
                                maxX = Mathf.Max(maxX, x);
                            }
                    if (maxX >= minX)
                    {
                        right = Vector3.Scale(WeaponGrip(mask, columns, rows, material.color.a, minX, maxX, .32f), new Vector3(width, height, 1));
                        left = Vector3.Scale(WeaponGrip(mask, columns, rows, material.color.a, minX, maxX, .68f), new Vector3(width, height, 1));
                    }
                }
                if (weapon.def.IsMeleeWeapon) right = new Vector3(-width * .3f, 0, 0);
                card.HandMaterial = _view.NewMaterial(Texture2D.whiteTexture, pawn.story?.SkinColor ?? new Color(.65f, .45f, .32f), true);
                card.RightHand = CreateWeaponHand(card, right, new Vector3(.16f, .14f, .14f) * PawnVisualScale);
                if (!weapon.def.IsRangedWeapon) return;
                left = UsesPistolGrip(weapon.def) ? right + new Vector3(.025f, -.025f, .065f) * PawnVisualScale
                    : left - Vector3.up * (.025f * PawnVisualScale);
                card.LeftHand = CreateWeaponHand(card, left, new Vector3(.17f, .13f, .15f) * PawnVisualScale);
            }

            Transform CreateWeaponHand(HeldCard card, Vector3 position, Vector3 scale)
            {
                var hand = new GameObject("Weapon hand") { layer = Layer, hideFlags = HideFlags.DontSave };
                hand.transform.SetParent(card.Object.transform, false);
                hand.transform.localPosition = position;
                hand.transform.localScale = scale;
                var mesh = CreateHandMesh();
                _view.meshes.Add(mesh);
                hand.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = hand.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = card.HandMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                _view.RegisterOutlineRenderer(renderer);
                return hand.transform;
            }

            internal static bool UsesPistolGrip(ThingDef weapon)
            {
                bool Matches(string name) => name != null &&
                    (name.IndexOf("Pistol", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("Revolver", StringComparison.OrdinalIgnoreCase) >= 0);
                if (Matches(weapon.defName)) return true;
                if (weapon.weaponTags != null)
                    for (int i = 0; i < weapon.weaponTags.Count; i++)
                        if (Matches(weapon.weaponTags[i])) return true;
                return false;
            }

            internal static Vector3 WeaponGrip(byte[] mask, int columns, int rows, float alpha, int minX, int maxX, float fraction)
            {
                int target = Mathf.RoundToInt(Mathf.Lerp(minX, maxX, fraction));
                for (int distance = 0; distance <= maxX - minX; distance++)
                    for (int side = 0; side < (distance == 0 ? 1 : 2); side++)
                    {
                        int x = target + (side == 0 ? distance : -distance);
                        if (x < minX || x > maxX) continue;
                        for (int y = 0; y < rows; y++)
                            if (mask[y * columns + x] * alpha >= 64)
                                return new Vector3((x + .5f) / columns - .5f, (float)y / rows - .5f, 0);
                    }
                return Vector3.zero;
            }

            internal Geometry WeaponVolume(Material material, float width, float height, bool hasMuzzle)
            {
                var key = (material, width, height, hasMuzzle);
                if (_weaponVolumes.TryGetValue(key, out var cached)) return cached;
                var geometry = new Geometry();
                // Reuse the existing cached GPU alpha readback, including atlas UVs.
                _view.BottomPadding(material);
                if (!_view.spriteMasks.TryGetValue(material, out var mask))
                {
                    geometry.Upright(new Vector3(0, -height * .5f, 0), width, height, false);
                    for (int i = 0; i < geometry.Vertices.Count; i++) geometry.Colors.Add(Color.white);
                    _weaponVolumes.Add(key, geometry);
                    return geometry;
                }
                float halfDepth = Mathf.Min(width * .03f, height * .042f);
                geometry.Upright(new Vector3(0, -height * .5f, -halfDepth), width, height, false);
                geometry.Upright(new Vector3(0, -height * .5f, halfDepth), width, height, false);
                for (int i = 4; i < 8; i++) geometry.Normals[i] = Vector3.forward;

                for (int i = 0; i < geometry.Vertices.Count; i++) geometry.Colors.Add(Color.white);
                _view.AppendVolumeContour(geometry, material, mask, width, height, halfDepth);
                float alpha = material.color.a;
                bool Filled(int x, int y) => x >= 0 && x < 64 && y >= 0 && y < 80 && mask[y * 64 + x] * alpha >= 64;
                int muzzleColumn = -1, muzzleBottom = 0, muzzleTop = 0;
                if (hasMuzzle)
                    for (int x = 0; x < 64; x++)
                        for (int y = 0; y < 80;)
                        {
                            if (!Filled(x, y) || Filled(x + 1, y)) { y++; continue; }
                            int start = y++;
                            while (y < 80 && Filled(x, y) && !Filled(x + 1, y)) y++;
                            // Local +X is the barrel end. Texture padding can place
                            // it below centre; use the furthest exposed end instead.
                            if (x > muzzleColumn ||
                                (x == muzzleColumn && y - start > muzzleTop - muzzleBottom))
                            { muzzleColumn = x; muzzleBottom = start; muzzleTop = y; }
                        }
                if (hasMuzzle && muzzleColumn >= 0)
                {
                    var center = new Vector3(((muzzleColumn + 1) / 64f - .5f) * width + .001f,
                        ((muzzleBottom + muzzleTop) / 160f - .5f) * height, 0);
                    float radiusY = Mathf.Min((muzzleTop - muzzleBottom) * height / 160f, height * .06f);
                    // A metal rim around a recessed dark bore, in the same mesh
                    // and material as the side texture (no extra renderers).
                    for (int segment = 0; segment < 12; segment++)
                    {
                        float a = segment * Mathf.PI / 6, b = (segment + 1) * Mathf.PI / 6;
                        var va = new Vector3(0, Mathf.Cos(a) * radiusY, Mathf.Sin(a) * halfDepth);
                        var vb = new Vector3(0, Mathf.Cos(b) * radiusY, Mathf.Sin(b) * halfDepth);
                        MuzzleQuad(geometry, center + va, center + vb, center + vb * .58f, center + va * .58f,
                            new Color(.38f, .40f, .42f, 0));
                        var recess = center - Vector3.right * .0005f;
                        MuzzleQuad(geometry, recess, recess + va * .58f, recess + vb * .58f, recess,
                            new Color(.025f, .028f, .03f, 0));
                    }
                }
                while (geometry.Colors.Count < geometry.Vertices.Count) geometry.Colors.Add(Color.white);
                _weaponVolumes.Add(key, geometry);
                return geometry;
            }

            internal void DisposeActivity()
            {
                _view.workIngredientCards.Clear(); _removedWorkIngredients.Clear(); _view.workSurfaceIngredients.Clear(); _tableSurfaceHeights.Clear();
                _view.nearbyCountedItems.Clear(); _view.progressMotes.Clear(); _view.pawnsWithProgress.Clear();
                _view.weapons.Clear(); _carriedItems.Clear(); _removedPawns.Clear();
                _view.heldPawnPortraits.Clear();
                _weaponVolumes.Clear(); _weaponVolumeMaterials.Clear();
                _volumeMasks.Clear();
                _view.dropPods.Clear(); _view.expiredDropPods.Clear(); _view.pendingPodObjects.Clear(); _view.chunkMaterials.Clear();
            }

        }
    }
}





