using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    // Optional adapter: no Multiplayer assemblies are required by RimVision itself.
    public static class Rim3DMultiplayer
    {
        public static Func<bool> IsActive;
        public static Func<bool> NativeSimulation;
        internal static bool GameExecution => !Active || Simulating || NativeSimulation?.Invoke() == true;
        public static Func<PawnControl, Pawn, bool> Acquire;
        public static Func<Pawn, bool> AvailableForLocal;
        public static Action<PawnControl> Release;
        public static Action<PawnControl, Vector2, float> Move;
        public static Action<PawnControl, bool> Combat;
        public static Action<PawnControl> Cancel;
        public static Action<MapView, Rim3DCommand> Send;
        public static Action<PawnControl, string, Map, Thing, IntVec3, int> SendLayerAction;
        public static Action<PawnControl, RimVisionApiRequestCommand> SendApiRequest;
        public static Action<PawnControl, RimVisionCombatInputCommand> SendCombatInput;
        public static Action<Map, BuildableDef, ThingDef, IntVec3, Rot4> SendOverviewPlace;
        public static Action<PawnControl, ColonyOrder> SendColonyOrder;
        public static Func<Pawn, Vector3> GetPosition;
        public static Func<Map, int> MapTicks;
        public static Func<Pawn, PawnControl> GetController;
        public static Func<Pawn, Vector3?> GetAim;
        public static Func<Pawn, bool> GetWeaponAiming;
        public static Action<Pawn, string, bool> Notice;
        public static Action<Pawn, Zone_Growing> GrowingZoneCreated;
        public static Action<Pawn, IntVec3, int, bool> SowingResult;
        public static Action TogglePause;
        public static Func<Map, bool> ActionAccelerationEnabled, ActionAccelerationActive;
        public static Action<Map> ToggleActionAcceleration;
        public static Func<bool> PlantSlowdownDisabled;
        public static Func<bool> ShootWhileRunningAllowed;
        public static Func<bool> CompassDisabled;
        public static Func<bool> MinigameRewardsAllowed;
        public static Func<WorkMinigameKind, float> MinigameRewardMultiplier;
        public static Action<PawnControl, int, int, int> MinigameReward;
        public static Func<Map, float> MinigameTickRate;
        public static Func<bool> IsHost, PawnLockEnabled;
        public static Action<Map> PrepareNeighborMaps;
        public static Func<Pawn, bool> PawnAllowed;
        public static bool PawnLocked => Active && IsHost?.Invoke() != true && PawnLockEnabled?.Invoke() == true;
        public static Action<Pawn, bool> ConfirmHit;
        public static Action<API.RimVisionAnimationEvent> AnimationEvent;
        public static Action<Pawn> Shot;
        public static Action<Pawn, SoundDef, SoundDef> ShotSound;
        static int simulationDepth;
        public static bool Active => IsActive?.Invoke() == true;
        public static bool Simulating => simulationDepth > 0;
        public static bool LocalInput => Active && !Simulating;
        public static void Simulate(Action action)
        {
            simulationDepth++;
            try { action(); }
            finally { simulationDepth--; }
        }
        public static SimulationScope EnterSimulation() => new SimulationScope(true);
        public readonly struct SimulationScope : IDisposable
        {
            internal SimulationScope(bool enter) { simulationDepth++; }
            public void Dispose() { simulationDepth--; }
        }
        public static Vector3 PawnPosition(Pawn pawn) => Simulating
            ? GetPosition?.Invoke(pawn) ?? pawn.Position.ToVector3Shifted() : pawn.DrawPos;
    }

    public enum Rim3DAction
    {
        Interact, Work, Designate, Equip, Build, Melee, Take, Drop, Store,
        TakeInventory, DropInventory, DropWeapon, Swap, Deposit, Ingest,
        BillWork, AddBill, Place, CancelPlan, Fire, BuildRoof, RemoveRoof, UseHeldItem, RemoveApparel, Arrest, Tend, Zone,
        CancelConstruction, MergeInventory, ReplacePlan, ClaimBuilding, FireControl, Reinstall
    }

    public sealed class Rim3DCommand
    {
        public Rim3DAction Action;
        public Thing Target;
        public IntVec3 Cell = IntVec3.Invalid;
        public int Count;
        public string Kind;
        public BuildableDef BuildDef;
        public ThingDef Stuff;
        public Rot4 Rotation;
        public RecipeDef Recipe;
        public Precept_ThingStyle Style;
        public bool StartWork;
        public Vector3 Origin, Direction;
        public float AimPawnHeight;
    }

    public sealed partial class MapView
    {
        public float ViewPitch => pitch;
        public string CameraProviderId => _viewCamera.ProviderId;
        public bool CustomCamera => _viewCamera.Custom;
        public Vector3 ViewAimDirection => _viewCamera.AimRay.direction;
        readonly bool simulationOnly;
        // Simulation-only context. Field initializers run, but no camera, meshes,
        // shader bundles or Unity scene objects are created on remote clients.
        public MapView(Map map, PawnControl controller)
        {
            _worldOverlays = new WorldOverlays(this);
            _heldThingPresentation = new HeldThingPresentation(this);
            _objectPresentation = new ObjectPresentation(this);
            _worldGeometry = new WorldGeometry(this);
            _synchronizedActions = new SynchronizedActions(this);
            _zoneActions = new ZoneActions(this);
            _combatActions = new CombatActions(this);
            _ingestActions = new IngestActions(this);
            _arrestActions = new ArrestActions(this);
            _medicalActions = new MedicalActions(this);
            _workActions = new WorkActions(this);
            _carryActions = new CarryActions(this);
            _constructionActions = new ConstructionActions(this);
            _viewTargeting = new ViewTargeting(this);
            _actionMenus = new ActionMenus(this);
            _hudPresentation = new HudPresentation(this);
            this.map = map;
            this.controller = controller;
            simulationOnly = true;
            controller.ItemNotice = SayFloatingItem;
        }

        bool SyncAction(Rim3DAction action, Thing target = null, int count = 0, string kind = null)
        {
            if (!Rim3DMultiplayer.LocalInput) return false;
            Rim3DMultiplayer.Send?.Invoke(this, new Rim3DCommand
            {
                Action = action, Target = target ?? aimedThing, Cell = aimedCell, Count = count, Kind = kind
            });
            return true;
        }

        public void ExecuteSynchronized(Rim3DCommand command) => _synchronizedActions.ExecuteSynchronized(command);

        public void SimulationTick()
        {
            if (Rim3DMultiplayer.Simulating) TickNativeCombatInput();
            if (Rim3DMultiplayer.Simulating && controller.Active)
            {
                TickSynchronizedManualFire();
                ContinueConstruction();
            }
        }

        public void ExposeSimulationData()
        {
            var thingDef = constructionDef as ThingDef;
            var terrainDef = constructionDef as TerrainDef;
            Scribe_Defs.Look(ref thingDef, "constructionThingDef");
            Scribe_Defs.Look(ref terrainDef, "constructionTerrainDef");
            constructionDef = (BuildableDef)thingDef ?? terrainDef;
            Scribe_Values.Look(ref constructionCell, "constructionCell", IntVec3.Invalid);
            Scribe_Values.Look(ref nextManualShotTick, "nextManualShotTick");
            ExposeManualFireControl();
        }

        public void CancelSimulationConstruction() { constructionDef = null; }
        public void ShowMultiplayerNotice(string text, bool floating)
        {
            if (floating) SayFloatingItem(text); else SayAction(text);
        }

        public void ShowMultiplayerShot(bool hit, bool killed)
        {
            if (hit) { lastConfirmedHit = Time.unscaledTime; lastHitKilled = killed; }
            else { lastPlayerShot = Time.unscaledTime; AddWeaponRecoil(); }
        }

        void AddFPVBill(Building_WorkTable table, RecipeDef recipe, Precept_ThingStyle style, bool startWork)
        {
            if (Rim3DMultiplayer.LocalInput)
            {
                Rim3DMultiplayer.Send?.Invoke(this, new Rim3DCommand
                { Action = Rim3DAction.AddBill, Target = table, Recipe = recipe, Style = style, StartWork = startWork });
                return;
            }
            if (recipe == null || table == null || !table.Spawned || table.Map != map ||
                table.Faction != ControlledPawn?.Faction || table.BillStack.Count >= 15 ||
                !table.def.AllRecipes.Contains(recipe) || !recipe.AvailableNow || !recipe.AvailableOnNow(table, null)) return;
            if (startWork && RecipeFailure(recipe) != null) { SayAction(RecipeFailure(recipe)); return; }
            if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe)
            {
                bool mechanitor = false;
                foreach (var pawn in map.mapPawns.FreeColonists)
                    if (MechanitorUtility.IsMechanitor(pawn)) { mechanitor = true; break; }
                if (!mechanitor) { SayAction("Rim3D_RequiresMechanitor".Translate().ToString()); return; }
            }
            table.BillStack.AddBill(BillUtility.MakeNewBill(recipe, style));
            if (startWork) StartFPVBillWork(table);
        }

        readonly SynchronizedActions _synchronizedActions;

        sealed partial class SynchronizedActions
        {
            readonly MapView _view;

            internal SynchronizedActions(MapView view)
            {
                _view = view;
            }

            internal void ExecuteSynchronized(Rim3DCommand command)
            {
                if (!Rim3DMultiplayer.Simulating || !_view.controller.Active) return;
                if (command.Target != null && (command.Target.Destroyed ||
                    (command.Target.Spawned && command.Target.Map != _view.map))) return;
                _view.aimedThing = command.Target;
                _view.aimedCell = command.Cell;
                switch (command.Action)
                {
                    case Rim3DAction.Reinstall: _view.PlaceFPVReinstall(command.Target, command.Cell, command.Rotation); break;
                    case Rim3DAction.CancelConstruction: _view.CancelSimulationConstruction(); break;
                    case Rim3DAction.Interact: _view.InteractFPV(); break;
                    case Rim3DAction.ClaimBuilding: _view.ClaimFPVBuilding(); break;
                    case Rim3DAction.Arrest: _view.ArrestFPV(command.Target as Pawn); break;
                    case Rim3DAction.Tend: _view.TendFPV(command.Target as Pawn); break;
                    case Rim3DAction.Work:
                        if (command.Kind != null && command.Kind.StartsWith(API.RimVisionApi.CommandPrefix, StringComparison.Ordinal))
                            _view.ExecuteExtensionAction(command.Kind.Substring(API.RimVisionApi.CommandPrefix.Length), command.Target, command.Cell, command.Count);
                        else if (command.Kind == "MortarTarget")
                            MortarTargeting.OrderTarget(_view.controller, _view.map, command.Target as Building_TurretGun, command.Cell);
                        else if (command.Kind == "MortarCancelTarget")
                            MortarTargeting.CancelTarget(_view.controller, _view.map, command.Target as Building_TurretGun);
                        else if (command.Kind == "Sow" || command.Kind == "StopSow")
                            _view._workActions.Sow(command.Cell, command.Count, command.Kind == "StopSow");
                        else _view.WorkOnTarget(command.Kind);
                        break;
                    case Rim3DAction.Designate:
                        _view.DesignateAndWork(command.Kind == "Mine" ? (Designator)new Designator_Mine() :
                            command.Kind == "SmoothWall" ? new Designator_SmoothWalls() :
                            command.Kind == "Deconstruct" ? new Designator_Deconstruct() :
                            command.Kind == "Harvest" ? new Designator_PlantsHarvest() : new Designator_PlantsCut(), command.Kind);
                        break;
                    case Rim3DAction.Equip: _view.EquipFPVWeapon(); break;
                    case Rim3DAction.Build: _view.BeginConstruction(command.Target); break;
                    case Rim3DAction.Melee: _view.MeleeFPV(); break;
                    case Rim3DAction.Take:
                        if (command.Kind == "Storage") _view._carryActions.TakeStoredItem(command.Target, command.Count);
                        else _view.TakeFPVCarry(command.Target, command.Count);
                        break;
                    case Rim3DAction.Drop:
                        // A delayed/repeated command must not drop a different item.
                        if (command.Target == null || _view.controller.Pawn?.carryTracker?.CarriedThing == command.Target)
                            _view.DropFPVCarry(command.Cell);
                        break;
                    case Rim3DAction.Store: _view.StoreFPVCarry(); break;
                    case Rim3DAction.MergeInventory: _view.MergeFPVInventory(); break;
                    case Rim3DAction.TakeInventory: _view.TakeInventoryItem(command.Target); break;
                    case Rim3DAction.DropInventory: _view.DropInventoryItem(command.Target); break;
                    case Rim3DAction.RemoveApparel: _view.RemoveFPVApparel(command.Target); break;
                    case Rim3DAction.DropWeapon: _view.DropEquippedFPVWeapon(); break;
                    case Rim3DAction.Swap: _view.SwapHandsAndEquipment(); break;
                    case Rim3DAction.Deposit: _view.DepositFPVResource(command.Target); break;
                    case Rim3DAction.Ingest: _view.IngestFPV(command.Target); break;
                    case Rim3DAction.UseHeldItem: _view.ExecuteHeldItemAction(command.Target, command.Count); break;
                    case Rim3DAction.BillWork:
                        if (command.Target is Building_WorkTable table) _view.StartFPVBillWork(table);
                        break;
                    case Rim3DAction.AddBill:
                        if (command.Target is Building_WorkTable billTable)
                            _view.AddFPVBill(billTable, command.Recipe, command.Style, command.StartWork);
                        break;
                    case Rim3DAction.Place:
                        if (command.BuildDef == null || !command.BuildDef.BuildableByPlayer ||
                            !command.Cell.InBounds(_view.map) || _view.map.fogGrid.IsFogged(command.Cell)) break;
                        var tool = new FPVBuildDesignator(command.BuildDef) { isOrder = true };
                        tool.SetRotation(command.Rotation);
                        if (command.BuildDef.MadeFromStuff)
                        {
                            if (command.Stuff?.IsStuff != true || !command.Stuff.stuffProps.CanMake(command.BuildDef)) break;
                            tool.SetStuffDef(command.Stuff);
                        }
                        if (tool.CanDesignateCell(command.Cell).Accepted) tool.DesignateSingleCell(command.Cell);
                        break;
                    case Rim3DAction.CancelPlan: _view.CancelFPVPlan(); break;
                    case Rim3DAction.ReplacePlan:
                        _view.ReplaceFPVBlueprint(command.Target as Blueprint_Build, command.BuildDef, command.Stuff, command.Rotation);
                        break;
                    case Rim3DAction.Fire: _view.FireManualBullet(command.Origin, command.Direction, command.Cell, command.Count, command.StartWork,
                        command.Target as Pawn, command.AimPawnHeight); break;
                    case Rim3DAction.FireControl: _view.ReceiveManualFireControl(command); break;
                    case Rim3DAction.BuildRoof: _view.BuildFPVRoof(command.Cell, command.Count == 1); break;
                    case Rim3DAction.RemoveRoof: _view.RemoveFPVRoof(command.Cell); break;
                    case Rim3DAction.Zone:
                        _view.ApplyZoneRectangle(command.Kind, command.Cell, command.Origin.ToIntVec3(), command.Count != 0);
                        break;
                }
            }

        }
    }
}
