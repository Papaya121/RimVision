using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Recovery.Rim3D
{
    [HarmonyPatch]
    static class BurnerMapBoundsPatch
    {
        static MethodBase TargetMethod() => AccessTools.GetDeclaredMethods(typeof(CompAbilityEffect_Burner)).Find(method =>
        {
            var parameters = method.GetParameters();
            return !method.IsStatic && method.Name.StartsWith("<GetPreCastActions>", StringComparison.Ordinal) &&
                method.ReturnType == typeof(void) && parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(LocalTargetInfo) && parameters[1].ParameterType == typeof(LocalTargetInfo);
        });

        static IntVec3 LastPointInsideMap(IntVec3 start, IntVec3 end, Func<IntVec3, bool> validator,
            bool skipFirstCell, CompAbilityEffect_Burner effect)
        {
            var map = effect.parent.pawn.Map;
            var result = GenSight.LastPointOnLineOfSight(start, end,
                cell => cell.InBounds(map) && validator(cell), skipFirstCell);
            return result.IsValid ? result.ClampInsideMap(map) : result;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var lastPoint = AccessTools.Method(typeof(GenSight), nameof(GenSight.LastPointOnLineOfSight));
            var boundedLastPoint = AccessTools.Method(typeof(BurnerMapBoundsPatch), nameof(LastPointInsideMap));
            bool replaced = false;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(lastPoint))
                {
                    var loadEffect = new CodeInstruction(OpCodes.Ldarg_0);
                    loadEffect.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    yield return loadEffect;
                    instruction.operand = boundedLastPoint;
                    replaced = true;
                }
                yield return instruction;
            }
            if (!replaced) throw new InvalidOperationException("RimVision: burner line-of-sight calculation was not found.");
        }
    }
}
