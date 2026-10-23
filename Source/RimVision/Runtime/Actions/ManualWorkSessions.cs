using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    static class ManualWorkSessions
    {
        internal static bool ContinuousRecreation(JobDriver driver) => driver is JobDriver_WatchBuilding ||
            driver is JobDriver_SitFacingBuilding || driver is JobDriver_PlayBilliards;

        static bool Controlled(JobDriver driver)
        {
            if (driver?.job?.playerForced != true || driver.pawn?.CurJob != driver.job) return false;
            return PawnControl.ForPawn(driver.pawn)?.IsActing == true;
        }

        static bool RecreationExpired(int tick, int deadline, JobDriver driver) => tick > deadline &&
            (driver.job.doUntilGatheringEnded || !Controlled(driver));

        [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.DriverTick))]
        static class SessionDurationPatch
        {
            static void Prefix(JobDriver __instance, List<Toil> ___toils)
            {
                bool reading = __instance is JobDriver_Reading;
                bool working = __instance is JobDriver_ConstructFinishFrame || __instance is JobDriver_Research;
                if (!reading && !working && (__instance is JobDriver_PlayBilliards || !ContinuousRecreation(__instance))) return;
                if (__instance.job?.playerForced != true) return;
                int index = __instance.CurToilIndex;
                if (index < 0 || index >= ___toils.Count || __instance.job.doUntilGatheringEnded) return;
                var toil = ___toils[index];
                bool session = reading ? toil.debugName == "Reading" : working ? toil.activeSkill != null :
                    toil.defaultDuration == __instance.job.def.joyDuration;
                if (toil.defaultCompleteMode != ToilCompleteMode.Delay ||
                    !session || !Controlled(__instance)) return;
                toil.defaultCompleteMode = ToilCompleteMode.Never;
            }
        }

        [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.CheckForJobOverride))]
        static class WorkOverridePatch
        {
            static bool Prefix(Pawn ___pawn)
            {
                var driver = ___pawn.jobs.curDriver;
                if (driver?.job?.doUntilGatheringEnded == true) return true;
                return !(driver is JobDriver_DoBill) && !(driver is JobDriver_Reading) || !Controlled(driver);
            }
        }

        [HarmonyPatch]
        static class BilliardsDurationPatch
        {
            static MethodBase TargetMethod() => AccessTools.GetDeclaredMethods(typeof(JobDriver_PlayBilliards)).Find(method =>
            {
                var parameters = method.GetParameters();
                return !method.IsStatic && method.Name.StartsWith("<MakeNewToils>", StringComparison.Ordinal) &&
                    method.ReturnType == typeof(void) && parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
            });

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = new List<CodeInstruction>(instructions);
                var duration = AccessTools.Field(typeof(JobDef), nameof(JobDef.joyDuration));
                for (int i = 0; i + 2 < code.Count; i++)
                {
                    if (!code[i].LoadsField(duration) || code[i + 1].opcode != OpCodes.Add ||
                        code[i + 2].opcode != OpCodes.Ble && code[i + 2].opcode != OpCodes.Ble_S) continue;
                    var branch = code[i + 2];
                    var target = branch.operand;
                    branch.opcode = OpCodes.Ldarg_0;
                    branch.operand = null;
                    code.Insert(i + 3, new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(ManualWorkSessions), nameof(RecreationExpired))));
                    code.Insert(i + 4, new CodeInstruction(OpCodes.Brfalse, target));
                    return code;
                }
                throw new InvalidOperationException("RimVision: billiards session duration check was not found.");
            }
        }
    }
}
