using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        private readonly SpatialAudioListener _audioListener = new SpatialAudioListener();
        internal bool HasAudioListener => _audioListener.IsActive;
        internal Vector3 AudioMapPosition => _viewCamera.HasFrame && _viewCamera.Frame.OverrideListener ? _viewCamera.Frame.ListenerPose.Position : MortarAiming ? controller.Position + Vector3.up * 1.35f : CameraMapPosition;

        void UpdateAudioListener(Vector3 mapPosition, Quaternion rotation)
        {
            if (_viewCamera.HasFrame && _viewCamera.Frame.OverrideListener)
                _audioListener.Update(_viewCamera.Frame.ListenerPose.Position, _viewCamera.Frame.ListenerPose.Rotation.normalized, root.transform);
            else _audioListener.Update(MortarAiming ? AudioMapPosition : mapPosition,
                MortarAiming ? Quaternion.Euler(0, rotation.eulerAngles.y, 0) : rotation, root.transform);
        }

        void DisposeAudioListener()
        {
            _audioListener.Dispose();
        }
    }

    internal sealed class SpatialAudioListener
    {
        private AudioListener _listener;
        private readonly List<AudioListener> _suspendedListeners = new List<AudioListener>();

        internal bool IsActive => _listener != null && _listener.enabled;

        internal void Update(Vector3 mapPosition, Quaternion rotation, Transform parent)
        {
            if (_listener == null)
            {
                // Cache once per view. Leave already disabled listeners untouched.
                foreach (var listener in UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                {
                    if (!listener.enabled) continue;
                    _suspendedListeners.Add(listener);
                    listener.enabled = false;
                }
                var host = new GameObject("RimVision map audio listener") { hideFlags = HideFlags.DontSave };
                host.transform.SetParent(parent, false);
                _listener = host.AddComponent<AudioListener>();
            }
            // The visual clone is at y = -10000, but native sound sources remain
            // on the original map. Listen there, with the actual view direction.
            _listener.transform.SetPositionAndRotation(mapPosition, rotation);
        }

        internal void Dispose()
        {
            if (_listener != null)
            {
                _listener.enabled = false;
                UnityEngine.Object.Destroy(_listener.gameObject);
                _listener = null;
            }
            foreach (var listener in _suspendedListeners)
                if (listener != null) listener.enabled = true;
            _suspendedListeners.Clear();
        }
    }

    [HarmonyPatch(typeof(Sustainer), "CameraDistanceSquared", MethodType.Getter)]
    static class SpatialSustainerDistance
    {
        static bool Prefix(Sustainer __instance, GameObject ___worldRootObject, ref float __result)
        {
            var window = Rim3DPreview.Instance?.ActiveWindow;
            var view = window?.View;
            if (view?.HasAudioListener != true || window.Map != Find.CurrentMap ||
                __instance.info.IsOnCamera || ___worldRootObject == null) return true;
            var delta = ___worldRootObject.transform.position - view.AudioMapPosition;
            __result = delta.x * delta.x + delta.z * delta.z;
            return false;
        }
    }

    [HarmonyPatch(typeof(Sample), "Update")]
    static class SpatialCameraSound
    {
        static void Prefix(Sample __instance)
        {
            // Native sustained on-camera sources are attached to the 2D camera
            // with spatialBlend = 1. Keep them audible from the relocated listener.
            if (__instance is SampleSustainer && __instance.subDef.onCamera && __instance.source != null)
                __instance.source.spatialBlend = Rim3DPreview.Instance?.ActiveWindow?.View?.HasAudioListener == true ? 0 : 1;
        }
    }
}

