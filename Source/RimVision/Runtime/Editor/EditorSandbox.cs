#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Recovery.Rim3D
{
    // Keep cheats out of the shipped mod and avoid modifying save data or global god mode.
    internal static class EditorSandbox
    {
        internal static bool UnlockAll => Current.ProgramState == ProgramState.Playing &&
            Rim3DPreview.Instance != null && Rim3DPreview.Instance.UnlockAllResearchAndBuildings;

        static bool InstantPlacement() => DebugSettings.godMode ||
            (Rim3DPreview.Instance != null && Rim3DPreview.Instance.InstantConstruction);

        internal static IEnumerable<CodeInstruction> ReplaceGodMode(IEnumerable<CodeInstruction> instructions)
        {
            var godMode = AccessTools.Field(typeof(DebugSettings), nameof(DebugSettings.godMode));
            var instantPlacement = AccessTools.Method(typeof(EditorSandbox), nameof(InstantPlacement));
            foreach (var instruction in instructions)
            {
                if (instruction.LoadsField(godMode))
                {
                    // Retain branch labels and exception blocks on the original instruction.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = instantPlacement;
                }
                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(ResearchProjectDef), "get_IsFinished")]
    static class EditorSandboxResearch
    {
        static bool Prefix(ref bool __result)
        {
            if (!EditorSandbox.UnlockAll) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    static class EditorSandboxBuildMenu
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Designator_Build), "Visible");
            yield return AccessTools.PropertyGetter(typeof(DesignationCategoryDef), "Visible");
        }

        static bool Prefix(ref bool __result)
        {
            if (!EditorSandbox.UnlockAll) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameRules), nameof(GameRules.DesignatorAllowed))]
    static class EditorSandboxBuildRules
    {
        static bool Prefix(Designator __0, ref bool __result)
        {
            if (!EditorSandbox.UnlockAll || !(__0 is Designator_Build)) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    static class EditorSandboxInstantConstruction
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell));
            yield return AccessTools.Method(typeof(Designator_Build), nameof(Designator_Build.CanDesignateCell));
            yield return AccessTools.Method(typeof(Designator_Build), nameof(Designator_Build.ProcessInput));
            yield return AccessTools.Method(typeof(MapView).GetNestedType("FPVBuildDesignator", BindingFlags.NonPublic), "CanReplaceBlueprint");
        }

        // Reuse vanilla instant placement, including terrain, styles, colors and placement hooks.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            EditorSandbox.ReplaceGodMode(instructions);
    }
}
#endif
