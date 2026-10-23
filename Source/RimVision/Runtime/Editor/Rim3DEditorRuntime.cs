#if UNITY_EDITOR
using System.IO;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    static class Rim3DEditorRuntime
    {
        static readonly Type[] BootstrapPatches =
        {
            typeof(EditorModClassDiscovery), typeof(EditorModHandles),
            typeof(EditorModContentRegistration), typeof(EditorModAssemblyDiscovery),
            typeof(EditorModAssemblyResolver),
            typeof(EditorModTypeDiscovery), typeof(EditorSteamFallback),
            typeof(EditorHarmonyContent), typeof(EditorComputeFallback)
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize()
        {
            // This callback precedes Root.CheckGlobalInit. Establish Verse's main
            // thread before Harmony can trigger any target's static constructor.
            UnityDataInitializer.CopyUnityData();
            ModAssets.RootDir = Path.Combine(Application.dataPath,"Mods/RimVision");
            const string editorId = "papaya.rimvision.editor";
            if (Harmony.HasAnyPatches(editorId)) return;
            var harmony = new Harmony(editorId);
            // Only type discovery and editor compatibility are needed before
            // loading content. Gameplay targets can initialize UI materials.
            foreach (var type in BootstrapPatches)
                harmony.CreateClassProcessor(type).Patch();
        }

        internal static void ApplyGameplayPatches(Harmony harmony)
        {
            foreach (var type in typeof(Rim3DMod).Assembly.GetTypes())
            {
                // Ordinary runtime classes can inherit methods named Cleanup,
                // which Harmony interprets as patch lifecycle callbacks.
                if (!type.IsDefined(typeof(HarmonyPatch), false) ||
                    Array.IndexOf(BootstrapPatches, type) >= 0) continue;
                harmony.CreateClassProcessor(type).Patch();
            }
        }
    }
    [HarmonyPatch(typeof(ModAssemblyHandler), "ReloadAll")]
    static class EditorModAssemblyResolver
    {
        private static bool _resolverRegistered;

        static void Prefix(ref bool ___globalResolverIsSet)
        {
            if (!_resolverRegistered)
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += RemoveResolver;
                _resolverRegistered = true;
            }
            ___globalResolverIsSet = true;
        }

        static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name == "Assembly-CSharp" || name == "RimWorld.Game")
                return typeof(Mod).Assembly;
            if (name == "Assembly-CSharp-firstpass" || name == "RimWorld.Firstpass")
                return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                    assembly.GetName().Name == "RimWorld.Firstpass");
            return null;
        }

        static void RemoveResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= RemoveResolver;
            _resolverRegistered = false;
        }
    }
    // The editor assembly remains discoverable even when the mod is disabled.
    // Do not let vanilla create a Mod handle without a matching content pack.
    [HarmonyPatch(typeof(GenTypes), "InstantiableDescendantsAndSelf")]
    static class EditorModClassDiscovery
    {
        static void Postfix(Type __0, ref IEnumerable<Type> __result)
        {
            if (__0 == typeof(Mod) && !LoadedModManager.RunningMods.Any(mod =>
                mod.PackageIdPlayerFacing == "papaya.rimvision"))
                __result = __result.Where(type => type != typeof(Rim3DMod));
        }
    }
    [HarmonyPatch(typeof(LoadedModManager), "get_ModHandles")]
    static class EditorModHandles
    {
        static void Postfix(ref IEnumerable<Mod> __result)
        {
            // Also tolerate a stale handle from an earlier editor Play session.
            __result = __result.Where(mod => !(mod is Rim3DMod) || mod.Content != null);
        }
    }
    [HarmonyPatch(typeof(LoadedModManager),"CreateModClasses")]
    static class EditorModContentRegistration
    {
        static void Prefix(Dictionary<Type, Mod> ___runningModClasses)
        {
            if (___runningModClasses.TryGetValue(typeof(Rim3DMod), out var handle) && handle.Content == null)
                ___runningModClasses.Remove(typeof(Rim3DMod));
            var content=LoadedModManager.RunningMods.FirstOrDefault(mod=>mod.PackageIdPlayerFacing=="papaya.rimvision");
            if(content==null)return;
            var assembly=typeof(Rim3DMod).Assembly;
            if(content.assemblies.loadedAssemblies.Contains(assembly))return;
            content.assemblies.loadedAssemblies.Add(assembly);
            GenTypes.ClearCache();
        }
    }
    // Unity compiles the mod into a project assembly, outside ModContentPack.assemblies.
    // Include it in RimWorld's normal type discovery for defs and saved job drivers.
    [HarmonyPatch(typeof(GenTypes),"get_AllActiveAssemblies")]
    static class EditorModAssemblyDiscovery
    {
        static void Postfix(ref IEnumerable<Assembly> __result)
        {
            __result=__result.Concat(new[]{typeof(Rim3DMod).Assembly}).Distinct();
        }
    }
    [HarmonyPatch(typeof(GenTypes),"GetTypeInAnyAssembly",new[]{typeof(string),typeof(string)})]
    static class EditorModTypeDiscovery
    {
        static bool Prefix(string __0,ref Type __result)
        {
            if(string.IsNullOrEmpty(__0) || !__0.StartsWith("Recovery.Rim3D.",StringComparison.Ordinal))return true;
            var type=typeof(Rim3DMod).Assembly.GetType(__0.Split(',')[0].Trim(),false);
            if(type==null)return true;
            __result=type;return false;
        }
    }
    // Steam integration is unnecessary in the editor and no native Steam runtime is shipped here.
    [HarmonyPatch(typeof(Verse.Steam.SteamManager),"InitIfNeeded")]
    static class EditorSteamFallback
    {
        static bool Prefix() => false;
    }
    // Harmony is already loaded from Assets/Plugins rather than the placeholder mod directory.
    [HarmonyPatch(typeof(ModContentPack),"AnyContentLoaded")]
    static class EditorHarmonyContent
    {
        static bool Prefix(ModContentPack __instance,ref bool __result)
        {
            if(__instance.PackageIdPlayerFacing!="brrainz.harmony")return true;
            __result=true;return false;
        }
    }
    // Recovered compute shader assets are unavailable; use RimWorld's built-in CPU atlas path in the editor.
    [HarmonyPatch(typeof(UnityData),"get_ComputeShadersSupported")]
    static class EditorComputeFallback
    {
        static bool Prefix(ref bool __result){__result=false;return false;}
    }
}
#endif



