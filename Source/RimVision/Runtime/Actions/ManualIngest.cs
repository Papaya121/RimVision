using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D
{
    // Preserve stock ingestion, effects and nutrition; manual meals only
    // replace destination selection with the pawn's occupied cell.
    public sealed class JobDriver_Rim3DIngest : JobDriver_Ingest
    {
        bool OnChair => pawn.Position.GetEdifice(pawn.Map)?.def.building?.isSittable == true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            var toils = new List<Toil>(base.MakeNewToils());
            // Vanilla's tool-user preparation goes to a spawned pickup target,
            // even when that target is already carried. Keep its toils and jump
            // past pickup only for food that is already in this pawn's hands.
            var mealSpot = toils.Find(toil => toil.debugName == "CarryIngestibleToChewSpot")
                ?? toils.Find(toil => toil.debugName == "FindAdjacentEatSurface");
            if (mealSpot != null)
                yield return Toils_Jump.JumpIf(mealSpot, () =>
                    pawn.carryTracker?.CarriedThing != null && pawn.carryTracker.CarriedThing == job.targetA.Thing);
            foreach (var toil in toils)
            {
                if (toil.debugName == "CarryIngestibleToChewSpot")
                {
                    toil.initAction = () =>
                    {
                        pawn.pather.StopDead();
                        pawn.ReserveSittableOrSpot(pawn.Position, job, errorOnFailed: false);
                    };
                    toil.defaultCompleteMode = ToilCompleteMode.Instant;
                }
                else if (toil.debugName == "FindAdjacentEatSurface")
                {
                    var findSurface = toil.initAction;
                    toil.initAction = () =>
                    {
                        // Stock ingestion uses A for food and B for the table.
                        job.SetTarget(TargetIndex.B, LocalTargetInfo.Invalid);
                        rotateToFace = TargetIndex.B;
                        if (OnChair) findSurface();
                    };
                }
                else if (toil.debugName == "FinalizeIngest")
                {
                    var finalize = toil.initAction;
                    toil.initAction = () =>
                    {
                        var food = job.targetA.Thing;
                        // Vanilla counts the surface in front even when standing.
                        // Manual eating at a table requires occupying a chair.
                        if (!OnChair && pawn.needs.mood != null && food.def.IsNutritionGivingIngestible &&
                            food.def.ingestible.chairSearchRadius > 10f && food.def.ingestible.tableDesired &&
                            pawn.GetPosture() == PawnPosture.Standing && !pawn.IsWildMan() &&
                            (pawn.Position + pawn.Rotation.FacingCell).HasEatSurface(pawn.Map))
                            pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.AteWithoutTable);
                        finalize();
                    };
                }
                yield return toil;
            }
        }
    }
}

