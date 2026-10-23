using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    [Flags]
    public enum RimVisionCameraMode { Overview = 1, Pawn = 2, Mortar = 4, Falling = 8, Transition = 16 }

    public readonly struct RimVisionCameraContext
    {
        public Map Map { get; }
        public Pawn Pawn { get; }
        public RimVisionCameraMode Mode { get; }
        public Vector3 ActorPosition { get; }
        public Vector3 Focus { get; }
        public float Yaw { get; }
        public float Pitch { get; }
        public float Distance { get; }
        public int ViewId { get; }
        public float DeltaSeconds => Mathf.Min(Time.unscaledDeltaTime, .1f);
        internal RimVisionCameraContext(Map map, Pawn pawn, RimVisionCameraMode mode, Vector3 actor, Vector3 focus,
            float yaw, float pitch, float distance, int viewId = 0)
        { ViewId = viewId; Map = map; Pawn = pawn; Mode = mode; ActorPosition = actor; Focus = focus; Yaw = yaw; Pitch = pitch; Distance = distance; }
    }

    public struct RimVisionCameraFrame
    {
        public RimVisionPose Pose { get; set; }
        public float FieldOfView { get; set; }
        public float NearClip { get; set; }
        public float FarClip { get; set; }
        public bool Orthographic { get; set; }
        public float OrthographicSize { get; set; }
        public bool CaptureMouse { get; set; }
        public bool OverrideListener { get; set; }
        public RimVisionPose ListenerPose { get; set; }
        public float OrbitDistance { get; set; }
        public Vector2 ZoomLimits { get; set; }
        public bool ShowBody { get; set; }
        public bool ShowFirstPersonItems { get; set; }
        public bool AimFromActor { get; set; }
        public bool AvoidObstacles { get; set; }
        public Vector3 Pivot { get; set; }
        public float ObstaclePadding { get; set; }
        public float CollisionRadius { get; set; }
        public float MovementYaw { get; set; }
        public Vector2 PitchLimits { get; set; }
        public float LookSensitivity { get; set; }
        public bool AllowLook { get; set; }
        public bool AllowZoom { get; set; }
        internal bool Valid => RimVisionControl.Finite(CollisionRadius) && CollisionRadius >= 0 && CollisionRadius <= 10 && Pose.Valid && (!OverrideListener || ListenerPose.Valid) &&
            RimVisionControl.Finite(OrbitDistance) && OrbitDistance > 0 && RimVisionControl.Finite(ZoomLimits.x) &&
            RimVisionControl.Finite(ZoomLimits.y) && ZoomLimits.x > 0 && ZoomLimits.y >= ZoomLimits.x && RimVisionControl.Finite(Pivot) && RimVisionControl.Finite(FieldOfView) &&
            FieldOfView >= 1 && FieldOfView <= 179 && RimVisionControl.Finite(NearClip) && NearClip > 0 &&
            RimVisionControl.Finite(FarClip) && FarClip > NearClip && RimVisionControl.Finite(OrthographicSize) && OrthographicSize > 0 &&
            RimVisionControl.Finite(MovementYaw) && RimVisionControl.Finite(PitchLimits.x) && RimVisionControl.Finite(PitchLimits.y) &&
            PitchLimits.x >= -89.9f && PitchLimits.y <= 89.9f && PitchLimits.x <= PitchLimits.y &&
            RimVisionControl.Finite(LookSensitivity) && LookSensitivity >= 0 && RimVisionControl.Finite(ObstaclePadding) && ObstaclePadding >= 0;
    }

    public enum RimVisionCameraExitReason { Replaced, Unavailable, ViewClosed, MapChanged, CallbackFailed }

    public abstract class RimVisionCameraProvider
    {
        public virtual float TransitionSeconds => 0;
        public virtual void Activated(RimVisionCameraContext context) { }
        public virtual void Deactivated(RimVisionCameraContext context, RimVisionCameraExitReason reason) { }
        public abstract bool Handles(RimVisionCameraContext context);
        public abstract void Evaluate(RimVisionCameraContext context, ref RimVisionCameraFrame frame);
    }

    public static class RimVisionCamera
    {
        static readonly ApiRegistry<RimVisionCameraProvider> Providers = new ApiRegistry<RimVisionCameraProvider>("camera");
        static readonly ApiRegistry<RimVisionCameraProvider> Modifiers = new ApiRegistry<RimVisionCameraProvider>("camera.modifiers");
        public static IDisposable RegisterProvider(string id, RimVisionCameraProvider provider, int priority = 0) =>
            Providers.Add(id, provider ?? throw new ArgumentNullException(nameof(provider)), priority);
        public static IDisposable RegisterModifier(string id, RimVisionCameraProvider modifier, int priority = 0) =>
            Modifiers.Add(id, modifier ?? throw new ArgumentNullException(nameof(modifier)), priority);

        public static void ThirdPerson(RimVisionCameraContext context, ref RimVisionCameraFrame frame,
            Vector3 shoulderOffset, float distance)
        {
            if (context.Pawn == null || !RimVisionControl.Finite(shoulderOffset) || !RimVisionControl.Finite(distance) || distance <= 0) return;
            var rotation = Quaternion.Euler(context.Pitch, context.Yaw, 0);
            frame.Pivot = frame.Pose.Position;
            frame.Pose = new RimVisionPose(frame.Pivot + rotation * (shoulderOffset + Vector3.back * distance), rotation);
            frame.OrbitDistance = distance;
            frame.ZoomLimits = new Vector2(.75f, 20);
            frame.OverrideListener = true;
            frame.ListenerPose = new RimVisionPose(frame.Pivot, rotation);
            frame.AllowZoom = true;
            frame.ShowBody = true;
            frame.ShowFirstPersonItems = false;
            frame.AimFromActor = frame.AvoidObstacles = true;
            frame.CollisionRadius = .2f;
        }

        internal static bool Apply(RimVisionCameraContext context, ref RimVisionCameraFrame frame, Session session)
        {
            if (Providers.Entries.Length == 0 && Modifiers.Entries.Length == 0 && session.IsEmpty) { session.NoteFrame(frame); return false; }
            session.Begin();
            bool primary = Apply(Providers, context, ref frame, true, session);
            bool modifiers = Apply(Modifiers, context, ref frame, false, session);
            session.End(context, ref frame);
            return primary || modifiers;
        }

        internal sealed class Session
        {
            static int _nextViewId;
            internal readonly int ViewId = ++_nextViewId;
            readonly HashSet<ApiRegistry<RimVisionCameraProvider>.Entry> _failed = new HashSet<ApiRegistry<RimVisionCameraProvider>.Entry>();
            internal bool Failed(ApiRegistry<RimVisionCameraProvider>.Entry entry) => _failed.Contains(entry);
            internal void Fail(ApiRegistry<RimVisionCameraProvider>.Entry entry)
            { _failed.Add(entry); if (_active.ContainsKey(entry)) Exit(entry, RimVisionCameraExitReason.CallbackFailed); }
            readonly Dictionary<ApiRegistry<RimVisionCameraProvider>.Entry, float> _distances = new Dictionary<ApiRegistry<RimVisionCameraProvider>.Entry, float>();
            ApiRegistry<RimVisionCameraProvider>.Entry _selected, _distanceOwner;
            internal bool IsSelected(ApiRegistry<RimVisionCameraProvider>.Entry entry) => _selected == entry;
            float? _nativeDistance;
            internal bool IsEmpty => _active.Count == 0 && (!_hasPrevious || _blendSeconds <= 0 || Time.unscaledTime - _blendStarted >= _blendSeconds);
            internal void Prepare(RimVisionCameraContext context, ref float distance)
            {
                _selected = null;
                Rand.PushState();
                try
                {
                    foreach (var entry in Providers.Entries)
                    {
                        if (!entry.Active || _failed.Contains(entry)) continue;
                        try { if (entry.Value.Handles(context)) { _selected = entry; break; } }
                        catch (Exception error) { Fail(entry); RimVisionDiagnostics.ReportFault("camera", entry.Id, error, true); }
                    }
                }
                finally { Rand.PopState(); }
                if (_selected == _distanceOwner) return;
                if (_distanceOwner != null && _distanceOwner.Active) _distances[_distanceOwner] = distance;
                else if (_distanceOwner == null) _nativeDistance = distance;
                if (_selected == null)
                { if (_nativeDistance.HasValue) distance = _nativeDistance.Value; _nativeDistance = null; }
                else if (_distances.TryGetValue(_selected, out float saved))
                { distance = saved; _distances.Remove(_selected); }
                _distanceOwner = _selected;
            }
            readonly Dictionary<ApiRegistry<RimVisionCameraProvider>.Entry, RimVisionCameraContext> _active = new Dictionary<ApiRegistry<RimVisionCameraProvider>.Entry, RimVisionCameraContext>();
            readonly List<ApiRegistry<RimVisionCameraProvider>.Entry> _seen = new List<ApiRegistry<RimVisionCameraProvider>.Entry>();
            readonly List<ApiRegistry<RimVisionCameraProvider>.Entry> _removed = new List<ApiRegistry<RimVisionCameraProvider>.Entry>();
            ApiRegistry<RimVisionCameraProvider>.Entry _primary, _previousPrimary;
            RimVisionCameraFrame _previous, _from;
            bool _hasPrevious;
            float _blendStarted, _blendSeconds;
            internal string PrimaryId => _primary?.Id;
            internal void NoteFrame(RimVisionCameraFrame frame) { _previous = frame; _hasPrevious = true; }
            internal void Begin() { _seen.Clear(); _primary = null; }
            internal void Enter(ApiRegistry<RimVisionCameraProvider>.Entry entry, RimVisionCameraContext context, bool primary)
            {
                if (primary) _primary = entry;
                _seen.Add(entry);
                bool added = !_active.ContainsKey(entry);
                _active[entry] = context;
                if (added) entry.Value.Activated(context);
            }
            internal void End(RimVisionCameraContext context, ref RimVisionCameraFrame frame)
            {
                _removed.Clear();
                foreach (var entry in _active.Keys) if (!_seen.Contains(entry)) _removed.Add(entry);
                foreach (var entry in _removed) Exit(entry, _primary != null ? RimVisionCameraExitReason.Replaced : RimVisionCameraExitReason.Unavailable);
                if (_primary != _previousPrimary)
                {
                    _from = _previous; _blendStarted = Time.unscaledTime; _blendSeconds = 0;
                    Rand.PushState();
                    try
                    {
                        float seconds = (_primary ?? _previousPrimary)?.Value.TransitionSeconds ?? 0;
                        if (RimVisionControl.Finite(seconds)) _blendSeconds = Mathf.Clamp(seconds, 0, 10);
                    }
                    catch (Exception error) { RimVisionDiagnostics.ReportFault("camera", (_primary ?? _previousPrimary).Id, error); }
                    finally { Rand.PopState(); }
                    _previousPrimary = _primary;
                }
                if (_hasPrevious && _blendSeconds > 0)
                {
                    float t = Mathf.SmoothStep(0, 1, (Time.unscaledTime - _blendStarted) / _blendSeconds);
                    frame.Pose = new RimVisionPose(Vector3.Lerp(_from.Pose.Position, frame.Pose.Position, t), Quaternion.Slerp(_from.Pose.Rotation, frame.Pose.Rotation, t));
                    if (_from.Orthographic == frame.Orthographic) frame.FieldOfView = Mathf.Lerp(_from.FieldOfView, frame.FieldOfView, t);
                }
                _previous = frame; _hasPrevious = true;
            }
            void Exit(ApiRegistry<RimVisionCameraProvider>.Entry entry, RimVisionCameraExitReason reason)
            {
                var context = _active[entry];
                _active.Remove(entry);
                Rand.PushState();
                try { entry.Value.Deactivated(context, reason); }
                catch (Exception error) { RimVisionDiagnostics.ReportFault("camera", entry.Id, error); }
                finally { Rand.PopState(); }
            }
            internal float? Close(RimVisionCameraExitReason reason)
            {
                _removed.Clear(); _removed.AddRange(_active.Keys);
                foreach (var entry in _removed) Exit(entry, reason);
                _removed.Clear(); _seen.Clear(); _primary = _previousPrimary = _selected = _distanceOwner = null; _hasPrevious = false; _blendSeconds = 0;
                var restore = _nativeDistance; _nativeDistance = null; _distances.Clear(); _failed.Clear(); return restore;
            }
        }

        static bool Apply(ApiRegistry<RimVisionCameraProvider> registry, RimVisionCameraContext context,
            ref RimVisionCameraFrame frame, bool first, Session session)
        {
            bool applied = false;
            Rand.PushState();
            try
            {
                foreach (var entry in registry.Entries)
                {
                    if (!entry.Active || session.Failed(entry)) continue;
                    bool selected = false;
                    try
                    {
                        if (!(selected = first ? session.IsSelected(entry) : entry.Value.Handles(context))) continue;
                        session.Enter(entry, context, first);
                        var candidate = frame;
                        entry.Value.Evaluate(context, ref candidate);
                        if (!candidate.Valid) throw new InvalidOperationException("Invalid camera frame.");
                        frame = candidate; applied = true;
                    }
                    catch (Exception error) { session.Fail(entry); RimVisionDiagnostics.ReportFault(registry.Area, entry.Id, error, true); Log.ErrorOnce("RimVision camera " + entry.Id + ": " + error, entry.Id.GetHashCode()); }
                    if (first && selected) break;
                }
            }
            finally { Rand.PopState(); }
            return applied;
        }
    }
}
