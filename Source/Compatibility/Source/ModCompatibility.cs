using System;
using Verse;

namespace Recovery.Rim3D.Compatibility
{
    public sealed class ModCompatibility : Mod
    {
        public ModCompatibility(ModContentPack content) : base(content)
        {
            if (ModsConfig.IsActive("Nals.FacialAnimation"))
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try { FacialAnimationCompatibility.Initialize(); }
                    catch (Exception error) { Log.Error("[RimVision Compatibility] Facial Animation initialization failed: " + error); }
                });
            if (ModsConfig.IsActive("Jaxe.Bubbles"))
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try { InteractionBubblesCompatibility.Initialize(); }
                    catch (Exception error) { Log.Error("[RimVision Compatibility] Interaction Bubbles initialization failed: " + error); }
                });
        }
    }
}
