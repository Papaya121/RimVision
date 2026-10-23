using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.GetBedSleepingSlotPosFor))]
    static class ManualSleepingSlotPatch
    {
        static bool Prefix(Pawn __0, Building_Bed __1, ref IntVec3 __result)
        {
            var job = __0?.CurJob;
            if (!(__0?.jobs?.curDriver is JobDriver_Rim3DSleep) || job.targetA.Thing != __1 ||
                !job.targetB.IsValid || !__1.OccupiedRect().Contains(job.targetB.Cell)) return true;
            __result = job.targetB.Cell;
            return false;
        }
    }
    public sealed class Rim3DMod : Mod
    {
        public override string SettingsCategory() => "RimVision";
        public override void DoSettingsWindowContents(Rect inRect) => Rim3DSettings.Current.Draw(inRect);

        public Rim3DMod(ModContentPack content) : base(content)
        {
#if UNITY_EDITOR
            // The editor bootstrap already owns initialization when the mod is disabled.
            if (content == null) return;
#endif
            Rim3DSettings.Current = GetSettings<Rim3DSettings>();
            ModAssets.RootDir = content.RootDir;
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    // Harmony can run target static constructors while detouring.
                    // Wait until content is loaded and Verse is on its main thread.
                    if (!Harmony.HasAnyPatches("papaya.rimvision"))
                    {
                        var harmony = new Harmony("papaya.rimvision");
#if UNITY_EDITOR
                        Rim3DEditorRuntime.ApplyGameplayPatches(harmony);
#else
                        harmony.PatchAll(typeof(Rim3DMod).Assembly);
#endif
                    }
                    ModAssets.InitializeShaders();
                    Rim3DPreview.Bootstrap();
                }
                catch (Exception error) { Log.Error("[RimVision Port] Runtime initialization failed on the main thread: " + error); }
            });
            Log.Message("[RimVision Port] Existing RimVision runtime loaded with Harmony. O: FPV. Original RimVision GPU shaders: Unity 2022.3.35.");
        }
    }

    [HarmonyPatch(typeof(ModMetaData), "Description", MethodType.Getter)]
    static class ModDescriptionPatch
    {
        static void Postfix(ModMetaData __instance, ref string __result)
        {
            if (__instance.PackageIdNonUnique != "papaya.rimvision" || LanguageDatabase.activeLanguage == null) return;
            if ("RimVision_ModDescription".TryTranslate(out var description))
                __result = description.ToString();
        }
    }

    public static class ModHooks
    {
        public static event Action<Map, IntVec3, ulong> Rim3DMeshChanged;
        public static event Action<Map, Thing> Rim3DThingChanged;
        public static event Action<Pawn> Rim3DPawnChanged;
        internal static void Mesh(Map map, IntVec3 cell, ulong flags) => Rim3DMeshChanged?.Invoke(map, cell, flags);
        internal static void Thing(Map map, Thing thing) => Rim3DThingChanged?.Invoke(map, thing);
        internal static void Pawn(Pawn pawn) => Rim3DPawnChanged?.Invoke(pawn);
    }

    [HarmonyPatch]
    static class MeshPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(MapDrawer), "MapMeshDirty",
            new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) });
        static void Prefix(Map ___map, IntVec3 __0, ulong __1) => ModHooks.Mesh(___map, __0, __1);
    }

    [HarmonyPatch]
    static class ThingPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ThingGrid), "Register", new[] { typeof(Thing) });
            yield return AccessTools.Method(typeof(ThingGrid), "Deregister", new[] { typeof(Thing), typeof(bool) });
        }
        static void Prefix(Map ___map, Thing __0) => ModHooks.Thing(___map, __0);
    }

    [HarmonyPatch(typeof(PortraitsCache), "SetDirty", new[] { typeof(Pawn) })]
    static class PawnPatch
    {
        static void Prefix(Pawn __0) => ModHooks.Pawn(__0);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ProcessPostTickVisuals))]
    static class PawnLean3DPatch
    {
        static void Postfix(Pawn __instance, int __0, CellRect __1)
        {
            var window = Rim3DPreview.Instance?.ActiveWindow;
            if (window?.IsOpen != true || window.View == null || __instance.Map != window.Map ||
                !__instance.Spawned || __instance.Suspended || Current.ProgramState != ProgramState.Playing ||
                __1.Contains(__instance.Position)) return;
            __instance.Drawer.leaner.ProcessPostTickVisuals(__0);
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play), "UIRootOnGUI")]
    static class InputPatch
    {
        static void Prefix()
        {
            var preview = Rim3DPreview.Instance;
            var e = Event.current;
            if (preview == null || e == null || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent) return;
            if (!ModsConfig.IsActive("papaya.rimvision")) return;
            foreach (var openWindow in Find.WindowStack.Windows)
                if (openWindow is Rim3DSettings.BindingWindow) return;
            var window = preview.ActiveWindow;
            var view = window?.IsOpen == true ? window.View : null;
            if (view != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F11)
            {
                Rim3DSettings.Current.ToggleInterfaceVisibility();
                e.Use();
                return;
            }
            if (view != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F1)
            {
                view.ToggleControlsHelp();
                e.Use();
                return;
            }
            if (view?.HandleCameraReturnInput(e) == true) return;
            view?.HandleFPVTargetInput(e);
            view?.ConsumeFPVInput(e);
            view?.HandleMouseEvent(e);
            if (view?.TextInputFocused == true) return;
            if (e.type != EventType.KeyDown || !ViewBindings.Matches(ViewAction.Toggle3D, e.keyCode)) return;
            if (preview.ActiveWindow == null && (Find.WindowStack.WindowsForcePause || Find.WindowStack.WindowsPreventCameraMotion)) return;
            e.Use();
            preview.Toggle();
        }
    }

    [HarmonyPatch(typeof(CameraJumper), "JumpLocalInternal")]
    static class CameraJump3DPatch
    {
        static void Prefix(IntVec3 __0)
        {
            if (Current.ProgramState != ProgramState.Playing ||
                !ModsConfig.IsActive("papaya.rimvision")) return;
            // Defer the flight until the letter's GUI action has finished.
            Rim3DPreview.Instance?.QueueCameraJump(Find.CurrentMap, __0);
        }
    }

    [HarmonyPatch(typeof(FloatMenuContext), MethodType.Constructor,
        new[] { typeof(List<Pawn>), typeof(Vector3), typeof(Map) })]
    static class FPVFloatMenuTargetPatch
    {
        internal static Thing Target;
        static void Postfix(FloatMenuContext __instance)
        {
            var target = Target;
            if (target == null) return;
            __instance.ClickedThings.Clear();
            __instance.ClickedThings.Add(target);
            __instance.ClickedPawns.Clear();
            if (target is Pawn pawn) __instance.ClickedPawns.Add(pawn);
        }
    }

    [HarmonyPatch(typeof(FloatMenuOptionProvider_WorkGivers), "GetWorkGiverOption")]
    static class FPVManualWorkAssignmentPatch
    {
        internal static Pawn Pawn;

        static int ManualPriority(Pawn_WorkSettings settings, WorkTypeDef workType)
        {
            int priority = settings.GetPriority(workType);
            var pawn = Pawn;
            return priority == 0 && pawn != null && pawn.workSettings == settings &&
                !pawn.WorkTypeIsDisabled(workType) ? 1 : priority;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getPriority = AccessTools.Method(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.GetPriority));
            var manualPriority = AccessTools.Method(typeof(FPVManualWorkAssignmentPatch), nameof(ManualPriority));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(getPriority))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = manualPriority;
                }
                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), nameof(ReservationManager.Reserve))]
    static class FPVRespectReservationsPatch
    {
        internal static bool CanPrioritize(Pawn pawn, LocalTargetInfo target, ReservationLayerDef layer = null)
        {
            if (pawn?.Map == null || !target.HasThing ||
                !(target.Thing is Plant || target.Thing.def.category == ThingCategory.Item)) return false;
            var physicalOwner = pawn.Map.physicalInteractionReservationManager.FirstReserverOf(target);
            if (physicalOwner != null && physicalOwner != pawn && Protected(physicalOwner)) return false;
            var reservations = pawn.Map.reservationManager.ReservationsReadOnly;
            for (int i = 0; i < reservations.Count; i++)
            {
                var reservation = reservations[i];
                if (reservation.Target == target && reservation.Layer == layer &&
                    reservation.Claimant != pawn &&
                    (Protected(reservation.Claimant) || Rim3DMultiplayer.Active && reservation.Job?.playerForced == true)) return false;
            }
            return true;
        }

        static bool Protected(Pawn pawn) => Rim3DMultiplayer.Active &&
            (PawnControl.ForPawn(pawn) != null || pawn?.CurJob?.playerForced == true);

        internal static bool CanReservePlant(Pawn pawn, Thing target) => pawn.CanReserve(target) ||
            target is Plant && CanPrioritize(pawn, target) && pawn.CanReserve(target, ignoreOtherReservations: true);

        internal static bool ReservePickup(Pawn pawn, Thing target)
        {
            if (!CanPrioritize(pawn, target)) return false;
            var job = JobMaker.MakeJob(JobDefOf.HaulToCell, target);
            job.playerForced = true;
            try
            {
                return pawn.Map.reservationManager.Reserve(pawn, job, target,
                    errorOnFailed: false, canReserversStartJobs: false);
            }
            finally
            {
                pawn.ClearReservationsForJob(job);
                JobMaker.ReturnToPool(job);
            }
        }

        static bool Prefix(ReservationManager __instance, Pawn __0, Job __1, LocalTargetInfo __2,
            int __3, int __4, ReservationLayerDef __5, ref bool ignoreOtherReservations, ref bool __result)
        {
            if (PawnControl.ForPawn(__0)?.Active != true) return true;
            ignoreOtherReservations = false;
            if (__1 != null && (__1.def == JobDefOf.CutPlant || __1.def == JobDefOf.Harvest ||
                __1.def == JobDefOf.HaulToCell) && __1.targetA == __2 && CanPrioritize(__0, __2, __5)) return true;
            if (__instance.CanReserve(__0, __2, __3, __4, __5, false)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "StartJob")]
    static class FPVNativeOrderPatch
    {
        static void Postfix(Pawn ___pawn, Job __0)
        {
            if (__0 == null || !__0.playerForced || ___pawn.CurJob != __0) return;
            // Multiplayer resolves the authoritative controller on every client;
            // its native menu callbacks retain Multiplayer's existing synchronization.
            var control = PawnControl.ForPawn(___pawn);
            if (control?.Pawn == ___pawn && control.Active) control.FollowNativeJob(__0);
        }
    }

    [HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.CarryIngestibleToChewSpot))]
    static class FPVManualMealSpotPatch
    {
        static void Postfix(Pawn __0, Toil __result)
        {
            if (PawnControl.ForPawn(__0) == null) return;
            // Native context-menu meals also run under manual movement. They
            // must finish this step without waiting for a path to a dining spot.
            __result.initAction = () =>
            {
                __0.pather.StopDead();
                __0.ReserveSittableOrSpot(__0.Position, __0.CurJob, errorOnFailed: false);
            };
            __result.defaultCompleteMode = ToilCompleteMode.Instant;
        }
    }

    [HarmonyPatch(typeof(GenPath), "ResolvePathMode")]
    static class FPVCampfireWorkSpotPatch
    {
        static bool Prefix(Pawn __0, TargetInfo __1, ref PathEndMode __2, ref TargetInfo __result)
        {
            // A manually controlled cook can work beside the fire. Resolve both
            // native goto toils and their reach checks identically, so collecting
            // ingredients cannot leave the job waiting for the fixed south cell.
            if (__2 != PathEndMode.InteractionCell || !__1.HasThing ||
                __1.Thing.def.defName != "Campfire" || __0?.CurJob?.def != JobDefOf.DoBill ||
                __0.CurJob.targetA.Thing != __1.Thing || PawnControl.ForPawn(__0) == null)
                return true;
            __2 = PathEndMode.Touch;
            __result = __1;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "StartPath")]
    static class FPVManualPathPatch
    {
        static bool Prefix(Pawn ___pawn, LocalTargetInfo __0, PathEndMode __1,
            ref LocalTargetInfo ___destination)
        {
            var control = PawnControl.ForPawn(___pawn);
            if (control == null || control.AutomaticActionPath) return true;
            // FollowAndMeleeAttack requires StartPath to record its destination
            // before it can strike. Keep that bookkeeping without starting a
            // path request or allowing automatic movement.
            if (___pawn.CurJob?.def == JobDefOf.AttackMelee) ___destination = __0;
            control.WaitForManualApproach(__0, __1);
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "PatherTick")]
    static class FPVManualPathTickPatch
    {
        static bool Prefix(Pawn_PathFollower __instance, Pawn ___pawn, LocalTargetInfo ___destination, PathEndMode ___peMode)
        {
            var control = PawnControl.ForPawn(___pawn);
            if (control == null || control.AutomaticActionPath) return true;
            // Also intercept a path restored from a save or started before control.
            if (__instance.Moving) control.WaitForManualApproach(___destination, ___peMode);
            return false;
        }
    }

    [HarmonyPatch(typeof(Targeter), "CurrentTargetUnderMouse")]
    static class FPVTargetUnderCrosshairPatch
    {
        static bool Prefix(TargetingParameters ___targetParams, ITargetingSource ___targetingSource,
            ref LocalTargetInfo __result)
        {
            var view = Rim3DPreview.Instance?.ActiveWindow?.View;
            if (view?.SelectingFPVTarget != true) return true;
            __result = view.TargetUnderCrosshair(___targetingSource?.targetParams ?? ___targetParams, ___targetingSource);
            return false;
        }
    }

    [HarmonyPatch(typeof(Targeter), "ConfirmStillValid")]
    static class FPVTargetSelectionPatch
    {
        static void Prefix(ref bool ___requiresCastedSelected, ref bool ___allowNonSelectedTargetingSource)
        {
            if (Rim3DPreview.Instance?.ActiveWindow?.View?.SelectingFPVTarget != true) return;
            // The controlled pawn need not also be selected in the hidden 2D UI.
            ___requiresCastedSelected = false;
            ___allowNonSelectedTargetingSource = true;
        }
    }

    internal static class ModAssets
    {
        internal static string RootDir;
        internal static float GetFloat(Material material, string property) => material.HasProperty(property) ? material.GetFloat(property) : 0;
        #if !UNITY_EDITOR
        static AssetBundle shaderBundle;
#endif
        static readonly Dictionary<string, Shader> shaders = new Dictionary<string, Shader>();
        internal static void InitializeShaders()
        {
            #if UNITY_EDITOR
            foreach (string asset in UnityEditor.AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Rim3DShaders" }))
            {
                var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(UnityEditor.AssetDatabase.GUIDToAssetPath(asset));
                shaders[shader.name] = shader;
            }
#else
            if (shaderBundle != null) return;
            string path = Path.Combine(RootDir, "ShaderBundles", "rim3d-shaders");
            shaderBundle = AssetBundle.LoadFromFile(path);
            if (shaderBundle == null) throw new InvalidOperationException("RimVision: could not load shader bundle " + path);
            foreach (var shader in shaderBundle.LoadAllAssets<Shader>()) shaders[shader.name] = shader;
#endif
            foreach (string required in new[] { "PreviewSurface", "GroundShadow", "PreviewSky", "WeatherParticles", "Lightning", "Fire", "FirstPersonWeapon", "CombatTrail", "CombatBillboard", "Water", "AmbientOcclusion" })
            {
                var shader = LoadShader("Rim3D/" + required);
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("RimVision shader missing or unsupported: " + required);
            }
            Log.Message("[RimVision Port] Loaded " + shaders.Count + " original GPU shaders.");
        }
        internal static Shader LoadShader(string path)
        {
            shaders.TryGetValue("Recovery/" + path, out var shader);
            return shader;
        }
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        internal static Texture2D LoadTexture(string path)
        {
            if (textures.TryGetValue(path, out var cached)) return cached;
            string contentPath = path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ? path.Substring(9) : path;
            string[] segments = contentPath.Split('/');
            for (int i = 0; i < segments.Length - 1; i++)
                if (segments[i].Length != 0) segments[i] = char.ToUpperInvariant(segments[i][0]) + segments[i].Substring(1);
            contentPath = string.Join("/", segments);
            var texture = ContentFinder<Texture2D>.Get(contentPath, false);
            if (texture == null) texture = Resources.Load<Texture2D>(path);
            textures[path] = texture;
            return texture;
        }
    }
}










