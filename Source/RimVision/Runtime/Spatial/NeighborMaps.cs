using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Recovery.Rim3D
{
    public sealed class NeighborMaps : GameComponent
    {
        readonly Game _game;
        bool _pending;
        float _nextCheck;
        readonly HashSet<int> _failedSources = new HashSet<int>();

        public NeighborMaps(Game game) { _game = game; }

        static bool CanPrepare(Map map) => map != null && !map.Disposed &&
            !(map.Parent is NeighborMapParent) && !map.IsPocketMap && map.Tile.Valid &&
            map.Tile.Layer.IsRootSurface && !map.generatorDef.isUnderground;

        static NeighborMapParent Find(Map source, int x, int z)
        {
            foreach (var parent in Verse.Find.World.pocketMaps)
                if (parent is NeighborMapParent neighbor && neighbor.sourceMap == source &&
                    neighbor.OffsetX == x && neighbor.OffsetZ == z) return neighbor;
            return null;
        }

        static bool Complete(Map source)
        {
            for (int z = -1; z <= 1; z++)
                for (int x = -1; x <= 1; x++)
                    if ((x != 0 || z != 0) && Find(source, x, z)?.Map == null) return false;
            return true;
        }

        public override void GameComponentUpdate()
        {
            if (_pending || !Rim3DSettings.Current.NeighborMaps ||
                UnityEngine.Time.unscaledTime < _nextCheck ||
                Current.ProgramState != ProgramState.Playing || LongEventHandler.ShouldWaitForEvent) return;
            _nextCheck = UnityEngine.Time.unscaledTime + 1;
            var source = Verse.Find.CurrentMap;
            if (!CanPrepare(source) || _failedSources.Contains(source.uniqueID) || Complete(source)) return;
            if (Rim3DMultiplayer.Active)
            {
                if (Rim3DMultiplayer.IsHost?.Invoke() == true)
                    Rim3DMultiplayer.PrepareNeighborMaps?.Invoke(source);
            }
            else Prepare(source);
        }

        public void Prepare(Map source)
        {
            if (_pending || Current.Game != _game || !CanPrepare(source) ||
                !_game.Maps.Contains(source) || Complete(source) || Rim3DMultiplayer.LocalInput) return;
            if (_game.Maps.Count > 119)
            {
                _failedSources.Add(source.uniqueID);
                Log.Warning("[RimVision] Cannot prepare eight neighboring maps: map count limit.");
                return;
            }
            _pending = true;
            LongEventHandler.QueueLongEvent(() =>
            {
                try
                {
                    if (Current.Game != _game || !_game.Maps.Contains(source) || source.Disposed) return;
                    for (int z = -1; z <= 1; z++)
                        for (int x = -1; x <= 1; x++)
                            if (x != 0 || z != 0) Generate(source, x, z);
                }
                catch (Exception error)
                {
                    _failedSources.Add(source.uniqueID);
                    Log.Error("[RimVision] Could not prepare neighboring maps: " + error);
                }
                finally { _pending = false; }
            }, "Rim3D_LoadingNeighbors", false, error =>
            {
                _pending = false;
                _failedSources.Add(source.uniqueID);
                Log.Error("[RimVision] Could not prepare neighboring maps: " + error);
            });
        }

        static void Generate(Map source, int x, int z)
        {
            var parent = Find(source, x, z);
            if (parent?.HasMap == true) return;
            if (parent == null)
            {
                parent = (NeighborMapParent)WorldObjectMaker.MakeWorldObject(
                    DefDatabase<WorldObjectDef>.GetNamed("Rim3D_NeighborMap"));
                parent.sourceMap = source;
                parent.OffsetX = x;
                parent.OffsetZ = z;
                parent.Tile = source.Tile;
                parent.mapGenerator = DefDatabase<MapGeneratorDef>.GetNamed("Rim3D_NeighborMap");
                Verse.Find.World.pocketMaps.Add(parent);
            }
            try
            {
                MapGenerator.GenerateMap(source.Size, parent, parent.MapGeneratorDef);
            }
            catch
            {
                var partial = parent.Map;
                try
                {
                    if (partial != null) Current.Game.DeinitAndRemoveMap(partial, false);
                }
                finally { Verse.Find.World.pocketMaps.Remove(parent); }
                throw;
            }
        }
    }

    public sealed class NeighborMapParent : PocketMapParent
    {
        internal int OffsetX;
        internal int OffsetZ;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref OffsetX, "rim3DNeighborX");
            Scribe_Values.Look(ref OffsetZ, "rim3DNeighborZ");
        }
    }

    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateContentsIntoMap))]
    static class NeighborMapSeed
    {
        static void Prefix(Map map, ref int seed)
        {
            if (map.Parent is NeighborMapParent parent)
                seed = Gen.HashCombineInt(seed, (parent.OffsetZ + 1) * 3 + parent.OffsetX + 1);
        }
    }

    [HarmonyPatch]
    static class DormantNeighborMapUpdates
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Map), nameof(Map.MapPreTick));
            yield return AccessTools.Method(typeof(Map), nameof(Map.MapPostTick));
            yield return AccessTools.Method(typeof(Map), nameof(Map.MapUpdate));
        }

        [HarmonyPriority(Priority.Last)]
        static bool Prefix(Map __instance) => !(__instance.Parent is NeighborMapParent);
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.RegisterAllTickabilityFor))]
    static class DormantNeighborThings
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(Thing t) => !(t.Map?.Parent is NeighborMapParent);
    }
}
