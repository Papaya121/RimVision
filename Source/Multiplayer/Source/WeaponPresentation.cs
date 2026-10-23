using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.Multiplayer
{
    // Reuse vanilla aiming in the normal 2D view, without replacing apparel
    // extras or creating a gameplay stance that could block manual shooting.
    [HarmonyPatch(typeof(PawnRenderUtility), "DrawEquipmentAndApparelExtras")]
    static class WeaponPresentation
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var carried = AccessTools.Method(typeof(PawnRenderUtility), "DrawCarriedWeapon");
            var aimed = AccessTools.Method(typeof(WeaponPresentation), nameof(DrawWeapon));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(carried))
                {
                    var flags = new CodeInstruction(OpCodes.Ldarg_3);
                    flags.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    yield return flags;
                    instruction.operand = aimed;
                }
                yield return instruction;
            }
        }

        static void DrawWeapon(ThingWithComps weapon, Vector3 position, Rot4 rotation, float factor, PawnRenderFlags flags)
        {
            var pawn = (weapon.ParentHolder as Pawn_EquipmentTracker)?.pawn;
            var aim = flags.HasFlag(PawnRenderFlags.Portrait) ? null : Adapter.Aim(pawn);
            if (aim.HasValue)
            {
                float angle = aim.Value.AngleFlat();
                position += new Vector3(0, 0, .4f + weapon.def.equippedDistanceOffset).RotatedBy(angle) * factor;
                PawnRenderUtility.DrawEquipmentAiming(weapon, position, angle);
            }
            else PawnRenderUtility.DrawCarriedWeapon(weapon, position, rotation, factor);
        }
    }
}
