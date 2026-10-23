using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D.API
{
    public enum RimVisionRequestStatus { Pending, Succeeded, Rejected, Cancelled, Detached }
    public enum RimVisionViewEventKind { WeaponChanged, HeldItemChanged, AimStarted, AimEnded, Shot, Melee, MovementChanged, CameraChanged }
    public enum RimVisionAttachment { Body, Weapon, RightHand, LeftHand, Muzzle }

    public readonly struct RimVisionViewEvent
    {
        public RimVisionViewEventKind Kind { get; }
        public Pawn Pawn { get; }
        public Thing Item { get; }
        public int Tick { get; }
        internal RimVisionViewEvent(RimVisionViewEventKind kind, Pawn pawn, Thing item, int tick)
        { Kind = kind; Pawn = pawn; Item = item; Tick = tick; }
    }

    public sealed class RimVisionActionRequest
    {
        public int RequestId { get; }
        public string ActionId { get; }
        public RimVisionRequestStatus Status { get; private set; }
        public string Reason { get; private set; }
        public RimVisionJobStatus JobStatus { get; private set; }
        public int JobId { get; private set; } = -1;
        internal void UpdateJob(RimVisionJobStatus status, int jobId)
        { if (JobStatus < RimVisionJobStatus.Succeeded) { JobStatus = status; JobId = jobId; } }
        public bool Completed => Status != RimVisionRequestStatus.Pending;
        internal RimVisionActionRequest(int requestId, string id) { RequestId = requestId; ActionId = id; }
        internal void Complete(RimVisionRequestStatus status, string reason)
        { if (!Completed) { Status = status; Reason = reason; } }
    }

    public readonly struct RimVisionAttachmentPose
    {
        public RimVisionPose Pose { get; }
        public Vector3 Scale { get; }
        public bool Visible { get; }
        public bool CanDraw { get; }
        internal RimVisionAttachmentPose(Transform transform, bool visible, bool canDraw)
        { Pose = new RimVisionPose(transform.localPosition, transform.localRotation); Scale = transform.localScale; Visible = visible; CanDraw = canDraw; }
        internal RimVisionAttachmentPose(RimVisionPose pose, Vector3 scale, bool visible, bool canDraw)
        { Pose = pose; Scale = scale; Visible = visible; CanDraw = canDraw; }
    }

    internal readonly struct ExtensionViewOperations
    {
        internal readonly Func<Pawn, bool, bool> Navigate;
        internal readonly Func<Pawn, RimVisionAttachment, RimVisionAttachmentPose?> Attachment;
        internal readonly Func<string, Map, Thing, IntVec3, int, RimVisionActionRequest> Submit;
        internal readonly Func<RimVisionHudElement, RimVisionHudLayout?> HudLayout;
        internal readonly SpatialFilteredPick Pick;
        internal ExtensionViewOperations(Func<Pawn, bool, bool> navigate,
            Func<Pawn, RimVisionAttachment, RimVisionAttachmentPose?> attachment,
            Func<string, Map, Thing, IntVec3, int, RimVisionActionRequest> submit,
            Func<RimVisionHudElement, RimVisionHudLayout?> hudLayout, SpatialFilteredPick pick)
        { Navigate = navigate; Attachment = attachment; Submit = submit; HudLayout = hudLayout; Pick = pick; }
    }

    public sealed partial class RimVisionView
    {
        ExtensionViewOperations _operations;
        readonly List<PawnAttachment> _attachments = new List<PawnAttachment>();
        sealed class PawnAttachment
        {
            internal Pawn Pawn;
            internal RimVisionAttachment Socket;
            internal GameObject Object;
            internal RimVisionPose Offset;
            internal bool FollowVisibility;
        }

        public bool RequestControl(Pawn pawn) => IsOpen && pawn != null && _operations.Navigate(pawn, true);
        public bool RequestOverview() => IsOpen && _operations.Navigate(null, false);
        public RimVisionActionRequest SubmitAction(string id, Thing target, IntVec3 cell, int value = 0) =>
            SubmitLayerAction(id, null, target, cell, value);
        public RimVisionActionRequest SubmitLayerAction(string id, Map targetMap, Thing target, IntVec3 cell, int value = 0)
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(RimVisionView));
            return _operations.Submit(id, targetMap, target, cell, value);
        }

        public bool TryGetHudLayout(RimVisionHudElement element, out RimVisionHudLayout layout)
        {
            var value = IsOpen ? _operations.HudLayout(element) : null;
            layout = value.GetValueOrDefault();
            return value.HasValue;
        }

        public bool TryGetAttachment(Pawn pawn, RimVisionAttachment socket, out RimVisionAttachmentPose pose)
        {
            var value = IsOpen ? _operations.Attachment(pawn, socket) : null;
            pose = value.GetValueOrDefault();
            return value.HasValue;
        }

        public bool TryPick(Ray ray, float maximumDistance, RimVisionPickFilter filter, out RimVisionSpatialHit hit)
        {
            hit = default;
            return IsOpen && _operations.Pick(ray, maximumDistance, filter, out hit);
        }

        public bool TryCreateRay(Vector2 guiPosition, Rect viewport, out Ray ray)
        {
            ray = default;
            if (!IsOpen || !RimVisionControl.Finite(guiPosition.x) || !RimVisionControl.Finite(guiPosition.y) ||
                !RimVisionControl.Finite(viewport.x) || !RimVisionControl.Finite(viewport.y) || !RimVisionControl.Finite(viewport.width) ||
                !RimVisionControl.Finite(viewport.height) || viewport.width <= 0 || viewport.height <= 0 || !viewport.Contains(guiPosition)) return false;
            var world = _camera.ViewportPointToRay(new Vector3((guiPosition.x - viewport.x) / viewport.width,
                1 - (guiPosition.y - viewport.y) / viewport.height, 0));
            ray = new Ray(_parent.InverseTransformPoint(world.origin), _parent.InverseTransformDirection(world.direction));
            return true;
        }

        public GameObject CreatePawnAttachment(Pawn pawn, RimVisionAttachment socket, string name, RimVisionPose offset, bool followDefaultVisibility = true)
        {
            if (pawn == null || !offset.Valid) throw new ArgumentException("Use a pawn and a finite attachment pose.");
            var result = CreateSceneObject(name);
            _attachments.Add(new PawnAttachment { Pawn = pawn, Socket = socket, Object = result, Offset = offset, FollowVisibility = followDefaultVisibility });
            UpdateAttachments();
            return result;
        }

        internal void UpdateAttachments()
        {
            for (int i = _attachments.Count - 1; i >= 0; i--)
            {
                var attachment = _attachments[i];
                if (attachment.Object == null) { _attachments.RemoveAt(i); continue; }
                bool found = TryGetAttachment(attachment.Pawn, attachment.Socket, out var pose);
                attachment.Object.SetActive(found && pose.CanDraw && (!attachment.FollowVisibility || pose.Visible));
                if (!found) continue;
                var transform = attachment.Object.transform;
                transform.localPosition = pose.Pose.Position + pose.Pose.Rotation * Vector3.Scale(attachment.Offset.Position, pose.Scale);
                transform.localRotation = pose.Pose.Rotation * attachment.Offset.Rotation;
                transform.localScale = pose.Scale;
            }
        }
    }
}
