using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Recovery.Rim3D.Multiplayer
{
    [HarmonyPatch(typeof(Command_SetPlantToGrow), "WarnAsAppropriate")]
    static class PlantSelectionFeedback
    {
        static readonly FieldInfo _useLocalIds = AccessTools.Field(
            AccessTools.TypeByName("Multiplayer.Client.Patches.UniqueIdsPatch"), "useLocalIdsOverride");

        [HarmonyPriority(Priority.Last)]
        static void Prefix(bool __runOriginal, ref bool? __state)
        {
            if (!__runOriginal || !MP.IsInMultiplayer || !MP.IsExecutingSyncCommandIssuedBySelf) return;
            __state = (bool)_useLocalIds.GetValue(null);
            _useLocalIds.SetValue(null, true);
        }

        static Exception Finalizer(Exception __exception, bool? __state)
        {
            if (__state.HasValue) _useLocalIds.SetValue(null, __state.Value);
            return __exception;
        }
    }
}
