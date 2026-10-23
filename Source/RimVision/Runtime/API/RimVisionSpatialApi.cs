using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D.API
{
    [Flags]
    public enum RimVisionPickMask { None = 0, Floor = 1, Roof = 2, Pawns = 4, Buildings = 8, Items = 16, Plants = 32, Plans = 64, All = 127 }
    public readonly struct RimVisionPickFilter
    {
        public RimVisionPickMask Mask { get; }
        public Thing ExcludedThing { get; }
        public bool SolidOnly { get; }
        public RimVisionPickFilter(RimVisionPickMask mask, Thing excludedThing = null, bool solidOnly = false)
        { Mask = mask; ExcludedThing = excludedThing; SolidOnly = solidOnly; }
        public static RimVisionPickFilter All => new RimVisionPickFilter(RimVisionPickMask.All);
    }
    internal delegate bool SpatialFilteredPick(Ray ray, float maximumDistance, RimVisionPickFilter filter, out RimVisionSpatialHit hit);

    public readonly struct RimVisionSpatialHit
    {
        public Map Map { get; }
        public Thing Thing { get; }
        public IntVec3 Cell { get; }
        public Vector3 Point { get; }
        public float Distance { get; }
        internal RimVisionSpatialHit(Map map, Thing thing, IntVec3 cell, Vector3 point, float distance)
        { Map = map; Thing = thing; Cell = cell; Point = point; Distance = distance; }
    }

    public abstract class RimVisionSpatialProvider
    {
        public abstract bool Handles(Map map);
        public abstract float Elevation(Map map);
        public abstract void CollectLayers(Map activeMap, List<Map> maps);
        public virtual int Revision(Map map) => 0;
        public virtual void CollectMaps(Map map, List<Map> maps) => CollectLayers(map, maps);
        public virtual bool SurfaceDependsOn(Map map, Map changedMap) => false;
        public virtual bool DrawMountain(Map map, IntVec3 cell) => true;
        public virtual bool DrawRoofTop(Map map, IntVec3 cell) => true;
        public virtual float RoofHeight(Map map) => 2.06f;
        public virtual bool PickThing(Thing thing) => DrawThing(thing);
        public virtual bool CanShootAcross(Map source, Map destination) => false;
        public virtual bool BlocksProjectile(Map map, IntVec3 cell, bool roof) => roof ? DrawRoof(map, cell) : DrawFloor(map, cell);
        public virtual bool BlocksProjectile(Thing thing) => true;
        public virtual bool DrawFloor(Map map, IntVec3 cell) => true;
        public virtual bool DrawRoof(Map map, IntVec3 cell) => true;
        public virtual bool DrawThing(Thing thing) => true;
        public virtual bool CanStand(Pawn pawn, Vector3 position) => true;
        public virtual float SurfaceHeight(Pawn pawn, Vector3 position) => 0;
        public virtual bool IsTraversalJob(Pawn pawn, Job job) => false;
        public virtual bool TryGetTraversal(RimVisionTraversalContext context, out RimVisionTraversal traversal)
        { traversal = default; return false; }
        public virtual AcceptanceReport ValidateTraversal(RimVisionTraversalContext context, RimVisionTraversal traversal) => false;
        public virtual Job CreateTraversalJob(RimVisionTraversalContext context, RimVisionTraversal traversal) => null;
        public virtual bool CanInteract(RimVisionLayerActionContext context) => false;
    }

    public readonly struct RimVisionTraversalContext
    {
        public Pawn Pawn { get; }
        public Map Map { get; }
        public Vector3 Position { get; }
        public Vector3 Direction { get; }
        internal RimVisionTraversalContext(Pawn pawn, Vector3 position, Vector3 direction)
        { Pawn = pawn; Map = pawn.Map; Position = position; Direction = direction; }
    }

    public readonly struct RimVisionTraversal
    {
        public Map Destination { get; }
        public IntVec3 Exit { get; }
        public int Route { get; }
        public RimVisionTraversal(Map destination, IntVec3 exit, int route = 0)
        { Destination = destination; Exit = exit; Route = route; }
    }

    public readonly struct RimVisionLayerActionContext
    {
        public Pawn Pawn { get; }
        public Map SourceMap { get; }
        public Map TargetMap { get; }
        public Thing Target { get; }
        public IntVec3 Cell { get; }
        public int Value { get; }
        internal RimVisionLayerActionContext(Pawn pawn, Map targetMap, Thing target, IntVec3 cell, int value)
        { Pawn = pawn; SourceMap = RimVisionControl.MapFor(pawn); TargetMap = targetMap; Target = target; Cell = cell; Value = value; }
    }

    public static class RimVisionSpatial
    {
        internal static readonly ApiRegistry<RimVisionSpatialProvider> Providers = new ApiRegistry<RimVisionSpatialProvider>("spatial", true);
        internal static readonly ApiRegistry<LayerAction> Actions = new ApiRegistry<LayerAction>("spatial.actions", true);

        public static bool RequestAction(string id, Map targetMap, Thing target, IntVec3 cell, int value = 0) =>
            Rim3DPreview.Instance?.ActiveWindow?.View?.RequestLayerAction(id, targetMap, target, cell, value) == true;

        public static RimVisionActionRequest SubmitAction(string id, Map targetMap, Thing target, IntVec3 cell, int value = 0) =>
            Rim3DPreview.Instance?.ActiveWindow?.View?.SubmitApiAction(id, targetMap, target, cell, value);

        public static IDisposable RegisterProvider(string id, RimVisionSpatialProvider provider, int priority = 0) =>
            Providers.Add(id, provider ?? throw new ArgumentNullException(nameof(provider)), priority);

        public static IDisposable RegisterAction(string id, Func<RimVisionLayerActionContext, AcceptanceReport> validate,
            Func<RimVisionLayerActionContext, bool> execute, int priority = 0) =>
            Actions.Add(id, new LayerAction(validate, execute, null), priority);

        public static IDisposable RegisterJobAction(string id, Func<RimVisionLayerActionContext, AcceptanceReport> validate,
            Func<RimVisionLayerActionContext, Job> createJob, int priority = 0) =>
            Actions.Add(id, new LayerAction(validate, null, createJob ?? throw new ArgumentNullException(nameof(createJob))), priority);

        internal sealed class LayerAction
        {
            internal readonly Func<RimVisionLayerActionContext, AcceptanceReport> Validate;
            internal readonly Func<RimVisionLayerActionContext, bool> Execute;
            internal readonly Func<RimVisionLayerActionContext, Job> CreateJob;
            internal LayerAction(Func<RimVisionLayerActionContext, AcceptanceReport> validate,
                Func<RimVisionLayerActionContext, bool> execute, Func<RimVisionLayerActionContext, Job> createJob)
            {
                Validate = validate ?? throw new ArgumentNullException(nameof(validate));
                if (execute == null && createJob == null) throw new ArgumentNullException(nameof(execute));
                Execute = execute; CreateJob = createJob;
            }
        }

        internal static RimVisionSpatialProvider Resolve(Map map)
        {
            if (map != null)
                foreach (var entry in Providers.Entries)
                    if (entry.Active && entry.Value.Handles(map)) return entry.Value;
            return null;
        }

        internal static bool Connected(RimVisionSpatialProvider provider, Map source, Map destination)
        {
            if (provider == null || source == null || destination == null || Current.Game == null ||
                !Current.Game.Maps.Contains(source) || !Current.Game.Maps.Contains(destination) ||
                Resolve(destination) != provider) return false;
            var maps = new List<Map>();
            provider.CollectMaps(source, maps);
            return source == destination || maps.Contains(destination);
        }

        internal static float MapOffset(Map source, Map destination)
        {
            if (source == destination) return 0;
            var provider = Resolve(source);
            if (provider == null || Resolve(destination) != provider) return float.NaN;
            float offset = provider.Elevation(destination) - provider.Elevation(source);
            return Finite(offset) ? offset : float.NaN;
        }

        internal static void CollectGroup(Map source, List<Map> maps)
        {
            maps.Clear();
            if (source == null || Current.Game == null) return;
            var provider = Resolve(source);
            provider?.CollectMaps(source, maps);
            for (int i = maps.Count - 1; i >= 0; i--)
                if (maps[i] == null || !Current.Game.Maps.Contains(maps[i]) ||
                    provider == null || Resolve(maps[i]) != provider || maps.IndexOf(maps[i]) != i) maps.RemoveAt(i);
            if (Current.Game.Maps.Contains(source) && !maps.Contains(source)) maps.Add(source);
            maps.Sort((a, b) => a.uniqueID.CompareTo(b.uniqueID));
        }

        internal static float Height(Pawn pawn, Vector3 position)
        {
            var provider = Resolve(pawn?.Map);
            if (provider == null) return 0;
            float height = provider.SurfaceHeight(pawn, position);
            return Finite(height) ? height : 0;
        }

        internal static bool NativePath(Pawn pawn, Job job) =>
            job != null && Resolve(pawn?.Map)?.IsTraversalJob(pawn, job) == true;

        internal static bool TryStartTraversal(PawnControl control, Vector3 direction)
        {
            if (Rim3DMultiplayer.LocalInput || !control.Active || control.IsActing || control.IsResting) return false;
            var pawn = control.Pawn;
            var provider = Resolve(pawn.Map);
            var context = new RimVisionTraversalContext(pawn, control.Position, direction);
            if (provider == null || !provider.TryGetTraversal(context, out var traversal)) return false;
            if (!Connected(provider, pawn.Map, traversal.Destination) || !traversal.Exit.InBounds(traversal.Destination) ||
                !provider.ValidateTraversal(context, traversal).Accepted) return true;
            var job = provider.CreateTraversalJob(context, traversal);
            if (job == null) return true;
            if (!provider.IsTraversalJob(pawn, job))
            {
                JobMaker.ReturnToPool(job);
                return true;
            }
            control.OrderAction(job);
            return true;
        }

        internal static bool ValidateAction(string id, RimVisionLayerActionContext context, out LayerAction action) =>
            ValidateAction(id, context, out action, out _);

        internal static bool ValidateAction(string id, RimVisionLayerActionContext context, out LayerAction action, out string reason)
        {
            reason = null;
            action = null;
            Rand.PushState();
            try
            {
                var map = context.TargetMap;
                if (RimVisionControl.MapFor(context.Pawn) == null || map == null || !Actions.TryGet(id, out action) ||
                    !Connected(Resolve(context.SourceMap), context.SourceMap, map)) return false;
                if (context.Target != null && (context.Target.Destroyed || !context.Target.Spawned || context.Target.Map != map)) return false;
                if (!context.Cell.InBounds(map) || map.fogGrid.IsFogged(context.Cell) ||
                    context.Target != null && map.fogGrid.IsFogged(context.Target.Position)) return false;
                var provider = Resolve(context.SourceMap);
                if (!provider.CanInteract(context)) return false;
                var report = action.Validate(context);
                reason = report.Reason;
                return report.Accepted;
            }
            catch (Exception error)
            {
                Log.ErrorOnce("RimVision layer action validation " + id + ": " + error, id?.GetHashCode() ?? 0);
                reason = "Rim3D_ActionUnavailable".Translate().ToString();
                return false;
            }
            finally { Rand.PopState(); }
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal delegate bool SpatialPick(Ray ray, float maximumDistance, out RimVisionSpatialHit hit);

    internal readonly struct SpatialViewBindings
    {
        internal readonly Func<Map, float?> Offset;
        internal readonly Func<Map, Transform> Root;
        internal readonly SpatialPick Pick;
        internal readonly Func<string, Map, Thing, IntVec3, int, bool> Request;
        internal readonly Func<Texture, Color, Material> SurfaceMaterial;
        internal readonly Func<Map, bool> Prepare;
        internal SpatialViewBindings(Func<Map, float?> offset, Func<Map, Transform> root, SpatialPick pick,
            Func<string, Map, Thing, IntVec3, int, bool> request, Func<Texture, Color, Material> surfaceMaterial,
            Func<Map, bool> prepare)
        { Offset = offset; Root = root; Pick = pick; Request = request; SurfaceMaterial = surfaceMaterial; Prepare = prepare; }
    }
}
