using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace Recovery.Rim3D.Multiplayer
{
    [HarmonyPatch]
    static class DebugActionIdentity
    {
        static readonly ConditionalWeakTable<DebugActionNode, string> _identities =
            new ConditionalWeakTable<DebugActionNode, string>();

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DebugToolsGeneral), "PushGas");
            yield return AccessTools.Method(typeof(DebugToolsGeneral), "Explosion");
        }

        static void Postfix(MethodBase __originalMethod, List<DebugActionNode> __result)
        {
            for (int i = 0; i < __result.Count; i++)
                _identities.Add(__result[i], "RimVision.Debug." + __originalMethod.Name + "." +
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        internal static bool TryGet(DebugActionNode node, out string identity) =>
            _identities.TryGetValue(node, out identity);
    }

    [HarmonyPatch]
    static class DebugActionSyncLabel
    {
        static MethodBase TargetMethod() => AccessTools.Method(
            AccessTools.TypeByName("Multiplayer.Client.DebugSync"), "LabelAndCategory");

        static bool Prefix(DebugActionNode __0, ref string __result)
        {
            if (!DebugActionIdentity.TryGet(__0, out var identity)) return true;
            __result = identity + "@" + __0.category;
            return false;
        }
    }
}
