using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        readonly MortarTargeting _mortarTargeting = new MortarTargeting();
        internal bool MortarAiming => _mortarTargeting.Active;

        bool UpdateMortarTargeting()
        {
            _mortarTargeting.Refresh(FirstPerson && !FallPresentationActive && !controller.InputBlocked && !eventCamera && !cameraFlight ? ControlledPawn : null);
            if (!MortarAiming) return false;
            ResetManualFireInput();
            ResetCarryInput();
            _workActions.ResetSowingInput();
            controller.Sneaking = controller.Sprinting = false;
            controller.Move(Vector2.zero, yaw);
            ReleaseMouse();
            if (!ExternalInputBlocked && Application.isFocused)
            {
                if (_mortarTargeting.CancelRequested() && !SyncAction(Rim3DAction.Work, _mortarTargeting.Turret, kind: "MortarCancelTarget"))
                    MortarTargeting.CancelTarget(controller, map, _mortarTargeting.Turret);
                if (!Rim3DMultiplayer.Active && ViewBindings.Pressed(ViewAction.Pause)) PausedByPlayer = !PausedByPlayer;
                _viewInput.ReadMovement(false, out float dx, out float dz);
                _mortarTargeting.Move(new Vector3(dx, 0, dz), Input.mouseScrollDelta.y, Time.unscaledDeltaTime, map);
                if (ViewBindings.Pressed(ViewAction.Interact)) _mortarTargeting.Center();
            }
            PositionCamera();
            UpdateCombatEffects();
            UpdateDropPods();
            return true;
        }

        internal void DrawMortarTargeting(Rect viewport)
        {
            if (!MortarAiming) return;
            if (_mortarTargeting.Draw(viewport, camera, root.transform, map, shader, ExternalInputBlocked, out var cell))
            {
                aimedCell = cell;
                aimedThing = _mortarTargeting.Turret;
                if (!SyncAction(Rim3DAction.Work, kind: "MortarTarget"))
                    MortarTargeting.OrderTarget(controller, map, _mortarTargeting.Turret, cell);
            }
        }

        sealed class MortarTargeting
        {
            static readonly System.Func<Building_TurretGun, bool> _canSetForcedTarget =
                HarmonyLib.AccessTools.MethodDelegate<System.Func<Building_TurretGun, bool>>(
                    HarmonyLib.AccessTools.PropertyGetter(typeof(Building_TurretGun), "CanSetForcedTarget"));
            static readonly HarmonyLib.AccessTools.FieldRef<Building_TurretGun, int> _cooldownTicks =
                HarmonyLib.AccessTools.FieldRefAccess<Building_TurretGun, int>("burstCooldownTicksLeft");
            static readonly HarmonyLib.AccessTools.FieldRef<Building_TurretGun, int> _warmupTicks =
                HarmonyLib.AccessTools.FieldRefAccess<Building_TurretGun, int>("burstWarmupTicksLeft");
            static readonly HarmonyLib.AccessTools.FieldRef<Building_TurretGun, bool> _holdFire =
                HarmonyLib.AccessTools.FieldRefAccess<Building_TurretGun, bool>("holdFire");
            static readonly HarmonyLib.AccessTools.FieldRef<Verb, int> _lastShotTick =
                HarmonyLib.AccessTools.FieldRefAccess<Verb, int>("lastShotTick");
            static readonly System.Action<Building_TurretGun> _resetCurrentTarget =
                HarmonyLib.AccessTools.MethodDelegate<System.Action<Building_TurretGun>>(
                    HarmonyLib.AccessTools.Method(typeof(Building_TurretGun), "ResetCurrentTarget"));
            static readonly System.Func<Pawn, Building_TurretGun, Thing> _findAmmo =
                HarmonyLib.AccessTools.MethodDelegate<System.Func<Pawn, Building_TurretGun, Thing>>(
                    HarmonyLib.AccessTools.Method(typeof(JobDriver_ManTurret), "FindAmmoForTurret"));
            static readonly float _ammoRadius = (float)HarmonyLib.AccessTools.Field(typeof(JobDriver_ManTurret), "SearchRadius").GetRawConstantValue();
            Building_TurretGun _turret;
            Pawn _pawn;
            Job _dismissedJob;
            Vector3 _focus;
            float _size;
            GameObject _overlay;
            Mesh _mesh;
            Material _material;
            Material _blockedMaterial;
            float _nextOverlay;
            string _notice;
            float _noticeUntil;
            readonly List<Vector3> _vertices = new List<Vector3>();
            readonly List<int> _triangles = new List<int>();
            readonly List<int> _blockedTriangles = new List<int>();
            readonly Dictionary<ThingDef, int> _shellCounts = new Dictionary<ThingDef, int>();
            float _nextAmmoUpdate;
            ThingDef _nextShell;
            internal bool Active => _turret != null;
            internal Building_TurretGun Turret => _turret;

            internal static bool IsMortar(Thing target) => target is Building_TurretGun turret &&
                turret.def.building?.IsMortar == true && turret.TryGetComp<CompMannable>() != null;

            internal static bool CanMan(Pawn pawn, Map map, Thing target) => IsMortar(target) &&
                PawnControl.Eligible(pawn) && target.Spawned && target.Map == map && target.Faction == pawn.Faction &&
                !map.fogGrid.IsFogged(target.Position) && !target.IsForbidden(pawn) && !target.IsBurning() &&
                PawnControl.CanUseCombatSkill(pawn, SkillDefOf.Shooting) &&
                pawn.RaceProps.ToolUser && !pawn.WorkTagIsDisabled(target.TryGetComp<CompMannable>().Props.manWorkType) &&
                (target.TryGetComp<CompMannable>().Props.planetLayerWhitelist.NullOrEmpty() ||
                    target.TryGetComp<CompMannable>().Props.planetLayerWhitelist.Contains(map.Tile.LayerDef)) &&
                pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
                (!target.TryGetComp<CompMannable>().MannedNow || target.TryGetComp<CompMannable>().ManningPawn == pawn) &&
                pawn.CanReserve(target) && pawn.CanReach(target.InteractionCell, PathEndMode.OnCell, Danger.Deadly);

            internal static bool CanTarget(Building_TurretGun turret, Map map, IntVec3 cell)
            {
                if (turret == null || !turret.Spawned || turret.Map != map || !cell.InBounds(map) || map.fogGrid.IsFogged(cell)) return false;
                var verb = turret.AttackVerb;
                float distance = (cell - turret.Position).LengthHorizontal;
                return verb != null && distance >= verb.verbProps.EffectiveMinRange(new LocalTargetInfo(cell), turret) &&
                    distance <= verb.EffectiveRange && verb.CanHitTargetFrom(turret.Position, new LocalTargetInfo(cell));
            }

            internal static void OrderTarget(PawnControl controller, Map map, Building_TurretGun turret, IntVec3 cell)
            {
                if (!CanControl(controller, map, turret) || !_canSetForcedTarget(turret) || !CanTarget(turret, map, cell)) return;
                _holdFire(turret) = false;
                turret.OrderAttack(new LocalTargetInfo(cell));
            }

            static bool CanControl(PawnControl controller, Map map, Building_TurretGun turret) =>
                controller.Active && !controller.InputBlocked && IsMortar(turret) &&
                !turret.Destroyed && turret.Spawned && turret.Map == map && turret.Faction == controller.Pawn.Faction &&
                controller.Pawn.CurJob?.def == JobDefOf.ManTurret && controller.Pawn.CurJob.targetA.Thing == turret;

            internal static void CancelTarget(PawnControl controller, Map map, Building_TurretGun turret)
            {
                if (!CanControl(controller, map, turret)) return;
                _holdFire(turret) = true;
                turret.OrderAttack(LocalTargetInfo.Invalid);
                _resetCurrentTarget(turret);
                turret.AttackVerb?.Reset();
            }

            internal void Refresh(Pawn pawn)
            {
                var job = pawn?.CurJob;
                var turret = job?.def == JobDefOf.ManTurret ? job.targetA.Thing as Building_TurretGun : null;
                if (!IsMortar(turret) || turret.Destroyed || !turret.Spawned || turret.Map != pawn.Map || turret.Faction != pawn.Faction)
                {
                    Close();
                    if (job != _dismissedJob) _dismissedJob = null;
                    return;
                }
                if (Active && (_pawn != pawn || _turret != turret)) Close();
                if (Active || job == _dismissedJob) return;
                _pawn = pawn;
                _turret = turret;
                _size = 65;
                _notice = null;
                Center();
            }

            internal void Center() { if (Active) _focus = _turret.Position.ToVector3Shifted(); }

            internal void Move(Vector3 movement, float scroll, float dt, Map map)
            {
                _size = Mathf.Clamp(_size * Mathf.Exp(-scroll * .13f), 12, Mathf.Max(map.Size.x, map.Size.z));
                _focus += Quaternion.Euler(0, 45, 0) * Vector3.ClampMagnitude(movement, 1) * _size * Mathf.Min(dt, .1f);
                _focus.x = Mathf.Clamp(_focus.x, 0, map.Size.x);
                _focus.z = Mathf.Clamp(_focus.z, 0, map.Size.z);
            }

            internal void Pose(Camera camera, out Vector3 position, out Quaternion rotation)
            {
                camera.orthographic = true;
                camera.orthographicSize = _size;
                rotation = Quaternion.Euler(55, 45, 0);
                position = _focus - rotation * Vector3.forward * (Mathf.Max(_size * 2, 120));
                camera.nearClipPlane = .15f;
                camera.farClipPlane += _size * 2;
            }

            internal bool CancelRequested()
            {
                if (!ViewBindings.MouseDown(1)) return false;
                _notice = "Rim3D_MortarCancelled".Translate().ToString();
                _noticeUntil = Time.unscaledTime + 3;
                return true;
            }

            internal bool HandleInput(Event current, bool blocked)
            {
                if (!Active || blocked) return false;
                if (current.type == EventType.MouseDown && current.button == 1)
                {
                    current.Use();
                    return true;
                }
                if (current.type == EventType.KeyDown && (current.keyCode == KeyCode.Escape ||
                    ViewBindings.Matches(ViewAction.CancelAction, current.keyCode) || ViewBindings.Matches(ViewAction.Perspective, current.keyCode)))
                {
                    _dismissedJob = _pawn?.CurJob;
                    Close();
                    current.Use();
                    return true;
                }
                return false;
            }

            internal bool Draw(Rect viewport, Camera camera, Transform root, Map map, Shader shader, bool blocked, out IntVec3 cell)
            {
                cell = IntVec3.Invalid;
                if (!Active) return false;
                if (Time.unscaledTime >= _nextOverlay)
                {
                    UpdateOverlay(root, map, shader);
                    _nextOverlay = Time.unscaledTime + 1;
                }
                if (Time.unscaledTime >= _nextAmmoUpdate)
                {
                    UpdateAmmo(map);
                    _nextAmmoUpdate = Time.unscaledTime + 1;
                }
                DrawRange(viewport, camera, root);
                bool atTurret = (_pawn.Position - _turret.Position).LengthHorizontalSquared <= 2;
                DrawMarker(viewport, camera, root, _turret.Position.ToVector3Shifted(),
                    (atTurret ? "Rim3D_MortarHere" : "Rim3D_MortarLocation").Translate().ToString(), new Color(.25f, .85f, 1));
                if (!atTurret)
                    DrawMarker(viewport, camera, root, _pawn.DrawPos, "Rim3D_MortarPawnLocation".Translate(_pawn.LabelShortCap), Color.white);
                float panelWidth = Mathf.Min(680, viewport.width - 24);
                int columns = Mathf.Max(1, Mathf.FloorToInt((panelWidth - 20) / 100));
                float ammoHeight = Mathf.Max(1, Mathf.CeilToInt(_shellCounts.Count / (float)columns)) * 32;
                var panel = new Rect(viewport.x + 12, viewport.y + 12, panelWidth, 229 + ammoHeight);
                Widgets.DrawWindowBackground(panel);
                Widgets.Label(new Rect(panel.x + 10, panel.y + 5, panel.width - 20, 25), "Rim3D_MortarTitle".Translate(_turret.LabelCap));
                Widgets.Label(new Rect(panel.x + 10, panel.y + 30, panel.width - 20, 45),
                    "Rim3D_MortarHelp".TranslateBindings().ToString());
                string targetStatus = _turret.ForcedTarget.IsValid
                    ? "Rim3D_MortarTargetCell".Translate(_turret.ForcedTarget.Cell.x, _turret.ForcedTarget.Cell.z).ToString()
                    : "Rim3D_MortarChooseTarget".Translate().ToString();
                Widgets.Label(new Rect(panel.x + 10, panel.y + 77, panel.width - 20, 48), targetStatus + "\n" + FireStatus(map));
                if (Time.unscaledTime < _noticeUntil)
                    Widgets.Label(new Rect(panel.x + 10, panel.y + 127, panel.width - 20, 40), _notice);
                TooltipHandler.TipRegion(new Rect(panel.x + 10, panel.y + 77, panel.width - 20, 48), _turret.GetInspectString());
                Widgets.Label(new Rect(panel.x + 10, panel.y + 169, panel.width - 20, 24), "Rim3D_MortarAmmoNearby".Translate(_ammoRadius));
                DrawAmmo(new Rect(panel.x + 10, panel.y + 194, panel.width - 20, ammoHeight), columns);
                float buttonY = panel.y + 198 + ammoHeight;
                if (Widgets.ButtonText(new Rect(panel.x + 10, buttonY, (panel.width - 24) * .5f, 27), "Rim3D_MortarCenter".TranslateBindings())) Center();
                if (Widgets.ButtonText(new Rect(panel.x + 14 + (panel.width - 24) * .5f, buttonY, (panel.width - 24) * .5f, 27), "Rim3D_MortarReturn".Translate()))
                {
                    _dismissedJob = _pawn.CurJob;
                    Close();
                    return false;
                }
                var current = Event.current;
                if (_turret.ForcedTarget.IsValid)
                {
                    var target = camera.WorldToViewportPoint(root.position + _turret.ForcedTarget.Cell.ToVector3Shifted());
                    var marker = new Vector2(viewport.x + target.x * viewport.width, viewport.y + (1 - target.y) * viewport.height);
                    if (target.z > 0 && viewport.Contains(marker) && !panel.Contains(marker))
                    {
                        var selected = new Color(1, .85f, .15f);
                        Widgets.DrawBoxSolid(new Rect(marker.x - 12, marker.y - 12, 24, 2), selected);
                        Widgets.DrawBoxSolid(new Rect(marker.x - 12, marker.y + 10, 24, 2), selected);
                        Widgets.DrawBoxSolid(new Rect(marker.x - 12, marker.y - 12, 2, 24), selected);
                        Widgets.DrawBoxSolid(new Rect(marker.x + 10, marker.y - 12, 2, 24), selected);
                    }
                }
                if (blocked || !Application.isFocused || !viewport.Contains(current.mousePosition) || panel.Contains(current.mousePosition)) return false;
                var mouse = current.mousePosition;
                var ray = camera.ViewportPointToRay(new Vector3((mouse.x - viewport.x) / viewport.width, 1 - (mouse.y - viewport.y) / viewport.height));
                var plane = new Plane(Vector3.up, root.position);
                if (!plane.Raycast(ray, out float enter)) return false;
                cell = (ray.GetPoint(enter) - root.position).ToIntVec3();
                bool valid = CanTarget(_turret, map, cell);
                var point = camera.WorldToViewportPoint(root.position + cell.ToVector3Shifted());
                var center = new Vector2(viewport.x + point.x * viewport.width, viewport.y + (1 - point.y) * viewport.height);
                Color color = valid ? new Color(.3f, 1, .4f) : new Color(1, .3f, .2f);
                Widgets.DrawBoxSolid(new Rect(center.x - 9, center.y - 1, 18, 2), color);
                Widgets.DrawBoxSolid(new Rect(center.x - 1, center.y - 9, 2, 18), color);
                if (current.type != EventType.MouseDown || current.button != 0) return false;
                current.Use();
                _notice = (valid ? "Rim3D_MortarOrdered" : "Rim3D_MortarInvalid").Translate().ToString();
                _noticeUntil = Time.unscaledTime + 3;
                return valid;
            }

            void UpdateAmmo(Map map)
            {
                _shellCounts.Clear();
                var ammo = _turret.GunCompEq?.parent.TryGetComp<CompChangeableProjectile>();
                if (ammo == null) return;
                var traverse = TraverseParms.For(_pawn, Danger.Deadly);
                foreach (var shell in map.listerThings.ThingsInGroup(ThingRequestGroup.Shell))
                {
                    if ((shell.Position - _turret.Position).LengthHorizontalSquared > _ammoRadius * _ammoRadius ||
                        shell.IsForbidden(_pawn) || !ammo.allowedShellsSettings.AllowedToAccept(shell) ||
                        !_pawn.CanReserve(shell, 10, 1) ||
                        !map.reachability.CanReach(_turret.Position, shell, PathEndMode.OnCell, traverse)) continue;
                    int count = Mathf.Min(shell.stackCount, map.reservationManager.CanReserveStack(_pawn, shell, 10, null, false));
                    if (count <= 0) continue;
                    _shellCounts.TryGetValue(shell.def, out int previous);
                    _shellCounts[shell.def] = previous + count;
                }
                Rand.PushState();
                try { _nextShell = _findAmmo(_pawn, _turret)?.def; }
                finally { Rand.PopState(); }
            }

            void DrawAmmo(Rect rect, int columns)
            {
                if (_shellCounts.Count == 0)
                {
                    Widgets.Label(rect, "Rim3D_MortarNoAmmo".Translate());
                    return;
                }
                int index = 0;
                foreach (var entry in _shellCounts)
                {
                    var slot = new Rect(rect.x + index % columns * 100, rect.y + index / columns * 32, 96, 30);
                    if (entry.Key == _nextShell) Widgets.DrawBoxSolid(slot, new Color(.2f, .7f, .35f, .25f));
                    Widgets.ThingIcon(new Rect(slot.x, slot.y, 28, 28), entry.Key);
                    Widgets.Label(new Rect(slot.x + 31, slot.y + 3, 62, 24), "× " + entry.Value);
                    TooltipHandler.TipRegion(slot, entry.Key.LabelCap.ToString() +
                        (entry.Key == _nextShell ? "\n" + "Rim3D_MortarNextAmmo".Translate().ToString() : ""));
                    index++;
                }
            }

            static Vector2 Project(Rect viewport, Camera camera, Transform root, Vector3 position)
            {
                var projected = camera.WorldToViewportPoint(root.position + position);
                return new Vector2(viewport.x + projected.x * viewport.width, viewport.y + (1 - projected.y) * viewport.height);
            }

            static void DrawMarker(Rect viewport, Camera camera, Transform root, Vector3 position, string label, Color color)
            {
                var point = Project(viewport, camera, root, position);
                var marker = new Vector2(Mathf.Clamp(point.x, viewport.x + 18, viewport.xMax - 160),
                    Mathf.Clamp(point.y, viewport.y + 18, viewport.yMax - 48));
                Widgets.DrawLine(marker + Vector2.left * 9, marker + Vector2.up * 9, color, 3);
                Widgets.DrawLine(marker + Vector2.up * 9, marker + Vector2.right * 9, color, 3);
                Widgets.DrawLine(marker + Vector2.right * 9, marker + Vector2.down * 9, color, 3);
                Widgets.DrawLine(marker + Vector2.down * 9, marker + Vector2.left * 9, color, 3);
                if ((marker - point).sqrMagnitude > 4)
                    Widgets.DrawLine(marker, marker + (point - marker).normalized * 24, color, 3);
                var text = new Rect(marker.x + 14, marker.y - 12, 140, 26);
                Widgets.DrawBoxSolid(text, new Color(.04f, .06f, .07f, .85f));
                Widgets.Label(text, label);
            }

            void DrawRange(Rect viewport, Camera camera, Transform root)
            {
                if (Event.current.type != EventType.Repaint) return;
                var verb = _turret.AttackVerb;
                if (verb == null) return;
                var center = _turret.Position.ToVector3Shifted();
                DrawRangeRing(viewport, camera, root, center, verb.verbProps.EffectiveMinRange(false), new Color(1, .25f, .15f));
                DrawRangeRing(viewport, camera, root, center, verb.EffectiveRange, new Color(.25f, 1, .4f));
                var legend = new Rect(viewport.x + 12, viewport.yMax - 38, Mathf.Min(520, viewport.width - 100), 28);
                Widgets.DrawBoxSolid(legend, new Color(.04f, .06f, .07f, .85f));
                Widgets.Label(legend, "Rim3D_MortarRangeLegend".Translate());
                var compass = new Vector2(viewport.xMax - 65, viewport.yMax - 65);
                var north = (Project(viewport, camera, root, _focus + Vector3.forward) - Project(viewport, camera, root, _focus)).normalized;
                Widgets.DrawLine(compass, compass + north * 30, Color.white, 3);
                Widgets.Label(new Rect(compass.x + north.x * 42 - 10, compass.y + north.y * 42 - 12, 24, 24), "N");
            }

            static void DrawRangeRing(Rect viewport, Camera camera, Transform root, Vector3 center, float radius, Color color)
            {
                const int segments = 192;
                var previous = Project(viewport, camera, root, center + Vector3.right * radius);
                for (int i = 1; i <= segments; i++)
                {
                    float angle = i * Mathf.PI * 2 / segments;
                    var point = Project(viewport, camera, root, center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius);
                    if (viewport.Contains(previous) && viewport.Contains(point)) Widgets.DrawLine(previous, point, color, 3);
                    previous = point;
                }
            }

            string FireStatus(Map map)
            {
                if (Find.TickManager.Paused) return "Rim3D_MortarPaused".Translate().ToString();
                if (_holdFire(_turret)) return "Rim3D_MortarHoldFire".Translate().ToString();
                if (map.roofGrid.Roofed(_turret.Position)) return "Rim3D_MortarRoofed".Translate().ToString();
                var verb = _turret.AttackVerb;
                if (verb != null && (verb.Bursting || _lastShotTick(verb) > 0 && Find.TickManager.TicksGame - _lastShotTick(verb) < 60))
                    return "Rim3D_MortarFired".Translate().ToString();
                var fuel = _turret.TryGetComp<CompRefuelable>();
                if (fuel != null && !fuel.HasFuel && (!fuel.Props.fuelIsMortarBarrel || !Find.Storyteller.difficulty.classicMortars))
                    return "Rim3D_MortarRefueling".Translate().ToString();
                var ammo = _turret.GunCompEq?.parent.TryGetComp<CompChangeableProjectile>();
                if (ammo != null && !ammo.Loaded) return "Rim3D_MortarLoading".Translate().ToString();
                if (_turret.TryGetComp<CompMannable>().ManningPawn != _pawn)
                    return "Rim3D_MortarApproaching".Translate().ToString();
                if (!_turret.Active) return "Rim3D_MortarUnavailable".Translate().ToString();
                if (_warmupTicks(_turret) > 0)
                    return "Rim3D_MortarWarmup".Translate((_warmupTicks(_turret) / 60f).ToString("F1")).ToString();
                if (_cooldownTicks(_turret) > 0)
                    return "Rim3D_MortarCooldown".Translate((_cooldownTicks(_turret) / 60f).ToString("F1")).ToString();
                return (_turret.ForcedTarget.IsValid ? "Rim3D_MortarPreparing" : "Rim3D_MortarReady").Translate().ToString();
            }

            void UpdateOverlay(Transform root, Map map, Shader shader)
            {
                if (_overlay == null)
                {
                    _overlay = new GameObject("RimVision mortar range") { layer = Layer, hideFlags = HideFlags.DontSave };
                    _overlay.transform.SetParent(root, false);
                    _mesh = new Mesh { name = "Mortar range", hideFlags = HideFlags.DontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    _material = new Material(shader) { hideFlags = HideFlags.DontSave, mainTexture = Texture2D.whiteTexture, color = new Color(.2f, .85f, .45f) };
                    _material.SetFloat("_SurfaceOpacity", .25f);
                    _material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _material.SetFloat("_ZWrite", 0);
                    _material.SetFloat("_Cull", 0);
                    _material.renderQueue = 3000;
                    _overlay.AddComponent<MeshFilter>().sharedMesh = _mesh;
                    var renderer = _overlay.AddComponent<MeshRenderer>();
                    _blockedMaterial = new Material(_material) { hideFlags = HideFlags.DontSave, color = new Color(1, .15f, .08f) };
                    _blockedMaterial.SetFloat("_SurfaceOpacity", .18f);
                    renderer.sharedMaterials = new[] { _material, _blockedMaterial };
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
                _vertices.Clear();
                _triangles.Clear();
                _blockedTriangles.Clear();
                for (int z = 0; z < map.Size.z; z++)
                {
                    int start = 0, run = 0;
                    for (int x = 0; x <= map.Size.x; x++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        int state = x == map.Size.x || map.fogGrid.IsFogged(cell) ? 0 : CanTarget(_turret, map, cell) ? 1 : 2;
                        if (state == run) continue;
                        if (run != 0)
                        {
                            int i = _vertices.Count;
                            _vertices.Add(new Vector3(start, .06f, z)); _vertices.Add(new Vector3(start, .06f, z + 1));
                            _vertices.Add(new Vector3(x, .06f, z + 1)); _vertices.Add(new Vector3(x, .06f, z));
                            var triangles = run == 1 ? _triangles : _blockedTriangles;
                            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
                            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 3);
                        }
                        start = x;
                        run = state;
                    }
                }
                _mesh.Clear();
                _mesh.SetVertices(_vertices);
                _mesh.subMeshCount = 2;
                _mesh.SetTriangles(_triangles, 0);
                _mesh.SetTriangles(_blockedTriangles, 1);
                _mesh.RecalculateNormals();
                _mesh.RecalculateBounds();
            }

            internal void Close()
            {
                _turret = null;
                _pawn = null;
                _nextOverlay = 0;
                _nextAmmoUpdate = 0;
                _shellCounts.Clear();
                _nextShell = null;
                if (_overlay != null) Object.Destroy(_overlay);
                if (_mesh != null) Object.Destroy(_mesh);
                if (_material != null) Object.Destroy(_material);
                if (_blockedMaterial != null) Object.Destroy(_blockedMaterial);
                _overlay = null; _mesh = null; _material = null; _blockedMaterial = null;
            }
        }
    }
}
