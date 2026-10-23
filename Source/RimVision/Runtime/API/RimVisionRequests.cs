using System;
using System.Collections.Generic;
using Recovery.Rim3D.API;
using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    public sealed class RimVisionApiRequestCommand
    {
        public int RequestId;
        public string Id;
        public Map TargetMap;
        public Thing Target;
        public IntVec3 Cell;
        public int Value;
    }

    public sealed partial class MapView
    {
        static int _nextApiRequestId;
        readonly Dictionary<int, RimVisionActionRequest> _pendingApiRequests = new Dictionary<int, RimVisionActionRequest>();
        readonly Queue<RimVisionActionRequest> _completedApiRequests = new Queue<RimVisionActionRequest>();
        readonly Dictionary<int, RimVisionActionRequest> _apiJobRequests = new Dictionary<int, RimVisionActionRequest>();
        readonly Queue<RimVisionJobEvent> _completedApiJobs = new Queue<RimVisionJobEvent>();
        (Pawn Pawn, bool Control)? _apiNavigation;
        public void NotifyApiJobResult(RimVisionJobEvent notification)
        {
            if (_apiDisposed) return;
            if (!_pendingApiRequests.TryGetValue(notification.RequestId, out var request) && !_apiJobRequests.TryGetValue(notification.RequestId, out request)) return;
            request.UpdateJob(notification.Status, notification.JobId);
            if (notification.Status == RimVisionJobStatus.Queued || notification.Status == RimVisionJobStatus.Running) _apiJobRequests[notification.RequestId] = request;
            else _apiJobRequests.Remove(notification.RequestId);
            if (_completedApiJobs.Count < 512) _completedApiJobs.Enqueue(notification);
        }

        internal bool RequestApiNavigation(Pawn pawn, bool control)
        {
            if (_apiDisposed || simulationOnly || _visualLayer || Rim3DMultiplayer.Simulating || FallPresentationActive ||
                Rim3DMultiplayer.PawnLocked && (!control || pawn != ControlledPawn)) return false;
            if (control && (pawn == null || !PawnControl.CanAcquire(pawn))) return false;
            _apiNavigation = (pawn, control);
            return true;
        }

        internal RimVisionActionRequest SubmitApiAction(string id, Map targetMap, Thing target, IntVec3 cell, int value)
        {
            int requestId = _nextApiRequestId = _nextApiRequestId == int.MaxValue ? 1 : _nextApiRequestId + 1;
            var request = new RimVisionActionRequest(requestId, id);
            _pendingApiRequests.Add(requestId, request);
            if (_apiDisposed || simulationOnly || _visualLayer || Rim3DMultiplayer.Simulating || !FirstPerson ||
                !controller.Active || _pendingApiRequests.Count + _apiJobRequests.Count > 256)
            {
                NotifyApiResult(requestId, false, "Rim3D_ActionUnavailable".Translate().ToString());
                return request;
            }
            string validationReason;
            bool valid = targetMap != null
                ? RimVisionSpatial.ValidateAction(id, new RimVisionLayerActionContext(ControlledPawn, targetMap, target, cell, value), out _, out validationReason)
                : RimVisionActionExecution.TryValidate(id, new RimVisionActionContext(map, ControlledPawn, target, cell, value), out _, out validationReason);
            if (!valid) { NotifyApiResult(requestId, false, validationReason); return request; }
            var command = new RimVisionApiRequestCommand { RequestId = requestId, Id = id, TargetMap = targetMap,
                Target = target, Cell = cell, Value = value };
            if (Rim3DMultiplayer.Active)
            {
                if (Rim3DMultiplayer.SendApiRequest == null) NotifyApiResult(requestId, false, "Multiplayer API request bridge is unavailable.");
                else Rim3DMultiplayer.SendApiRequest(controller, command);
            }
            else _apiRequests.Enqueue((ControlledPawn, new Rim3DCommand { Kind = id, Target = target, Cell = cell, Count = value }, targetMap, requestId));
            return request;
        }

        public bool ExecuteApiRequest(RimVisionApiRequestCommand command, out string reason) => ExecuteApiRequest(command, out reason, null);
        public bool ExecuteApiRequest(RimVisionApiRequestCommand command, out string reason, Action<RimVisionJobEvent> jobResult)
        {
            reason = null;
            if (command == null || _apiDisposed || !controller.Active || Rim3DMultiplayer.LocalInput) return false;
            return command.TargetMap != null
                ? TryApplyLayerAction(command.Id, command.TargetMap, command.Target, command.Cell, command.Value, out reason, command.RequestId, jobResult)
                : TryExecuteExtensionAction(command.Id, command.Target, command.Cell, command.Value, out reason, command.RequestId, jobResult);
        }

        public void NotifyApiResult(int requestId, bool succeeded, string reason)
        {
            if (!_pendingApiRequests.TryGetValue(requestId, out var request)) return;
            _pendingApiRequests.Remove(requestId);
            request.Complete(succeeded ? RimVisionRequestStatus.Succeeded : RimVisionRequestStatus.Rejected, reason);
            if (_completedApiRequests.Count < 256) _completedApiRequests.Enqueue(request);
        }

        void FlushApiRequests()
        {
            int count = _completedApiRequests.Count;
            while (count-- > 0 && _completedApiRequests.Count > 0)
            {
                var completed = _completedApiRequests.Dequeue();
                _apiExtensions?.Complete(completed);
            }
            int jobCount = _completedApiJobs.Count;
            while (jobCount-- > 0 && _completedApiJobs.Count > 0) _apiExtensions?.JobChanged(_completedApiJobs.Dequeue());
            var navigation = _apiNavigation;
            _apiNavigation = null;
            if (!navigation.HasValue || _apiDisposed) return;
            if (navigation.Value.Control) EnterFirstPerson(navigation.Value.Pawn);
            else if (FirstPerson) TogglePerspective();
        }

        void CloseApiRequests()
        {
            foreach (var request in _pendingApiRequests.Values)
                request.Complete(Rim3DMultiplayer.Active ? RimVisionRequestStatus.Detached : RimVisionRequestStatus.Cancelled, null);
            foreach (var request in _apiJobRequests.Values) request.UpdateJob(RimVisionJobStatus.Detached, request.JobId);
            _apiJobRequests.Clear(); _completedApiJobs.Clear();
            _pendingApiRequests.Clear(); _completedApiRequests.Clear(); _apiNavigation = null;
        }

        RimVisionAttachmentPose? GetApiAttachment(Pawn pawn, RimVisionAttachment socket)
        {
            if (pawn == null || simulationOnly || _visualLayer || pawn.Map != map) return null;
            bool canDraw = pawn.Spawned && pawn.Map == map && !map.fogGrid.IsFogged(pawn.Position) && DrawSpatialThing(pawn);
            if (socket == RimVisionAttachment.Body)
            {
                if (!pawnCards.TryGetValue(pawn, out var body) || body == null) return null;
                return new RimVisionAttachmentPose(body, body.gameObject.activeSelf, canDraw);
            }
            if (!weapons.TryGetValue(pawn, out var card) || card.Object == null) return null;
            var transform = card.Object.transform;
            bool visible = card.Object.activeSelf;
            if (socket == RimVisionAttachment.Weapon) return new RimVisionAttachmentPose(transform, visible, canDraw);
            if (socket == RimVisionAttachment.Muzzle)
            {
                var point = transform.localPosition + transform.localRotation * Vector3.Scale(card.VisualMuzzle, transform.localScale);
                return new RimVisionAttachmentPose(new RimVisionPose(point, transform.localRotation), Vector3.one, visible, canDraw);
            }
            var hand = socket == RimVisionAttachment.RightHand ? card.RightHand : socket == RimVisionAttachment.LeftHand ? card.LeftHand : null;
            if (hand == null) return null;
            return new RimVisionAttachmentPose(new RimVisionPose(transform.localPosition + transform.localRotation *
                Vector3.Scale(hand.localPosition, transform.localScale), transform.localRotation * hand.localRotation),
                Vector3.Scale(transform.localScale, hand.localScale), visible && hand.gameObject.activeSelf, canDraw);
        }
    }
}
