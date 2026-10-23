using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Recovery.Rim3D.API
{
    public static class RimVisionApi
    {
        public static Version Version { get; } = new Version(1, 6, 0);
        public static RimVisionViewState? Current => ActiveView?.GetApiState();
        static MapView ActiveView => Rim3DPreview.Instance?.ActiveWindow?.View;

        public static bool RequestAction(string id, Thing target, IntVec3 cell, int value = 0) =>
            ActiveView?.RequestExtensionAction(id, target, cell, value) == true;

        public static RimVisionActionRequest SubmitAction(string id, Thing target, IntVec3 cell, int value = 0) =>
            ActiveView?.SubmitApiAction(id, null, target, cell, value);
        public static bool RequestControl(Pawn pawn) => pawn != null && ActiveView?.RequestApiNavigation(pawn, true) == true;
        public static bool RequestOverview() => ActiveView?.RequestApiNavigation(null, false) == true;

        public static IDisposable RegisterExtension(string id, Func<RimVisionExtension> create, int priority = 0) =>
            Extensions.Add(id, create ?? throw new ArgumentNullException(nameof(create)), priority);

        public static IDisposable RegisterAction(string id, Func<RimVisionActionContext, string> label,
            Func<RimVisionActionContext, bool> visible, Func<RimVisionActionContext, AcceptanceReport> validate,
            Func<RimVisionActionContext, bool> execute, int priority = 0) =>
            Actions.Add(id, new ActionRegistration(label, visible, validate, execute, null), priority);

        public static IDisposable RegisterJobAction(string id, Func<RimVisionActionContext, string> label,
            Func<RimVisionActionContext, bool> visible, Func<RimVisionActionContext, AcceptanceReport> validate,
            Func<RimVisionActionContext, Job> createJob, int priority = 0) =>
            Actions.Add(id, new ActionRegistration(label, visible, validate, null,
                createJob ?? throw new ArgumentNullException(nameof(createJob))), priority);

        internal const string CommandPrefix = "RimVisionAPI:";
        internal static readonly ApiRegistry<Func<RimVisionExtension>> Extensions = new ApiRegistry<Func<RimVisionExtension>>("extensions");
        internal static readonly ApiRegistry<ActionRegistration> Actions = new ApiRegistry<ActionRegistration>("actions", true);

        internal sealed class ActionRegistration
        {
            internal readonly Func<RimVisionActionContext, string> Label;
            internal readonly Func<RimVisionActionContext, bool> Visible;
            internal readonly Func<RimVisionActionContext, AcceptanceReport> Validate;
            internal readonly Func<RimVisionActionContext, bool> Execute;
            internal readonly Func<RimVisionActionContext, Job> CreateJob;

            internal ActionRegistration(Func<RimVisionActionContext, string> label,
                Func<RimVisionActionContext, bool> visible, Func<RimVisionActionContext, AcceptanceReport> validate,
                Func<RimVisionActionContext, bool> execute, Func<RimVisionActionContext, Job> createJob)
            {
                Label = label ?? throw new ArgumentNullException(nameof(label));
                Visible = visible ?? throw new ArgumentNullException(nameof(visible));
                Validate = validate ?? throw new ArgumentNullException(nameof(validate));
                if (execute == null && createJob == null) throw new ArgumentNullException(nameof(execute));
                Execute = execute;
                CreateJob = createJob;
            }
        }
    }

    public readonly struct RimVisionActionContext
    {
        public Map Map { get; }
        public Pawn Pawn { get; }
        public Thing Target { get; }
        public IntVec3 Cell { get; }
        public int Value { get; }

        internal RimVisionActionContext(Map map, Pawn pawn, Thing target, IntVec3 cell, int value)
        { Map = map; Pawn = pawn; Target = target; Cell = cell; Value = value; }
    }

    public readonly struct RimVisionViewState
    {
        public Map Map { get; }
        public Pawn Pawn { get; }
        public bool FirstPerson { get; }
        public bool CanControl { get; }
        public bool InputBlocked { get; }
        public bool MortarAiming { get; }
        public bool Multiplayer { get; }
        public bool Paused { get; }
        public Thing Carrier { get; }
        public Thing AimedThing { get; }
        public IntVec3 AimedCell { get; }
        public Vector3 CameraPosition { get; }
        public Quaternion CameraRotation { get; }
        public float FieldOfView { get; }
        public bool Orthographic { get; }
        public float OrthographicSize { get; }
        public bool BodyVisible { get; }
        public bool FirstPersonItemsVisible { get; }
        public float MovementYaw { get; }
        public Ray AimRay { get; }
        public bool CameraTransition { get; }
        public bool Falling { get; }
        public Thing Weapon { get; }
        public Thing HeldItem { get; }
        public float AimBlend { get; }
        public float Recoil { get; }
        public float Stamina { get; }
        public bool Sprinting { get; }
        public bool Sneaking { get; }
        public bool Moving { get; }
        public bool Acting { get; }
        public bool Resting { get; }
        public bool NativeControl { get; }
        public int LastMeleeAttackTick { get; }
        public RimVisionInputChannels BlockedInput { get; }

        internal RimVisionViewState(Map map, Pawn pawn, bool firstPerson, bool canControl, bool inputBlocked,
            bool mortarAiming, Thing aimedThing, IntVec3 aimedCell, Vector3 position, Quaternion rotation,
            RimVisionCameraFrame camera, Ray aimRay, bool transition, bool falling, PawnControl control,
            float aimBlend, float recoil, RimVisionInputChannels blockedInput)
        {
            Map = map; Pawn = pawn; FirstPerson = firstPerson; CanControl = canControl; InputBlocked = inputBlocked;
            MortarAiming = mortarAiming; AimedThing = aimedThing; AimedCell = aimedCell;
            Paused = map == null || (Rim3DMultiplayer.Active ? (Rim3DMultiplayer.MinigameTickRate?.Invoke(map) ?? 0) <= 0 : Find.TickManager.Paused);
            Carrier = RimVisionControl.TryGetContext(pawn, out var native) ? native.Carrier : pawn;
            CameraPosition = position; CameraRotation = rotation; Multiplayer = Rim3DMultiplayer.Active;
            FieldOfView = camera.FieldOfView; Orthographic = camera.Orthographic; OrthographicSize = camera.OrthographicSize;
            BodyVisible = camera.ShowBody; FirstPersonItemsVisible = camera.ShowFirstPersonItems; MovementYaw = camera.MovementYaw;
            AimRay = aimRay; CameraTransition = transition; Falling = falling;
            Weapon = pawn?.equipment?.Primary; HeldItem = pawn?.carryTracker?.CarriedThing;
            AimBlend = aimBlend; Recoil = recoil; Stamina = control.Stamina; Sprinting = control.Sprinting;
            Sneaking = control.Sneaking; Moving = pawn?.pather?.Moving == true || control.Moving;
            Acting = control.IsActing; Resting = control.IsResting; NativeControl = control.HasNativeControl;
            LastMeleeAttackTick = control.LastMeleeAttackTick; BlockedInput = blockedInput;
        }
    }

    internal sealed class ApiRegistry<T>
    {
        internal readonly string Area;
        internal ApiRegistry(string area, bool gameplay = false)
        {
            Area = area;
            RimVisionDiagnostics.Register(area, gameplay, () =>
            {
                var result = new RimVisionRegistrationInfo[Entries.Length];
                for (int i = 0; i < result.Length; i++)
                    result[i] = new RimVisionRegistrationInfo(area, Entries[i].Id, Entries[i].Priority, Entries[i].Value?.GetType().FullName ?? typeof(T).FullName);
                return result;
            });
        }
        internal sealed class Entry : IDisposable
        {
            readonly ApiRegistry<T> _owner;
            internal readonly string Id;
            internal readonly T Value;
            internal readonly int Priority;
            internal bool Active { get; private set; } = true;
            internal Entry(ApiRegistry<T> owner, string id, T value, int priority)
            { _owner = owner; Id = id; Value = value; Priority = priority; }
            public void Dispose()
            {
                if (!Active) return;
                Active = false;
                _owner._entries.Remove(Id);
                _owner.Refresh();
            }
        }

        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        internal int Revision { get; private set; }
        internal Entry[] Entries { get; private set; } = Array.Empty<Entry>();

        internal IDisposable Add(string id, T value, int priority)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160 || id.IndexOf(':') <= 0)
                throw new ArgumentException("Use a stable namespaced ID, for example author.mod:action (maximum 160 characters).", nameof(id));
            if (_entries.ContainsKey(id)) throw new InvalidOperationException("RimVision API ID already registered: " + id);
            var entry = new Entry(this, id, value, priority);
            _entries.Add(id, entry);
            Refresh();
            return entry;
        }

        internal bool TryGet(string id, out T value)
        {
            if (id != null && _entries.TryGetValue(id, out var entry)) { value = entry.Value; return true; }
            value = default;
            return false;
        }

        void Refresh()
        {
            Revision++;
            RimVisionDiagnostics.Changed();
            var entries = new Entry[_entries.Count];
            _entries.Values.CopyTo(entries, 0);
            Array.Sort(entries, (a, b) =>
            {
                int order = b.Priority.CompareTo(a.Priority);
                return order != 0 ? order : StringComparer.Ordinal.Compare(a.Id, b.Id);
            });
            Entries = entries;
        }
    }
}
