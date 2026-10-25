namespace Recovery.Rim3D
{
    public sealed partial class MapView
    {
        bool _sceneRetained;

        internal void RetainScene()
        {
            _sceneRetained = true;
            root.SetActive(false);
        }

        internal void SuspendView()
        {
            _viewInput.UpdateExtensions(this, true);
            CancelNativeCombatInput();
            _viewCamera.CloseApi(API.RimVisionCameraExitReason.ViewClosed);
            _apiRequests.Clear();
            CloseApiRequests();
            ClearOverviewPreview();
            _overviewPlacements.Clear();
            _apiExtensions?.Dispose();
            _apiExtensions = null;
            _workMinigame.Dispose();
            controller.Dispose();
            EndFPVSession();
            ResetManualFireInput();
            ReleaseMouse();
            DisposeAudioListener();
            _weatherParticles?.Hide();
            _lightningRenderer?.ClearVisuals();
            LightningStrikePatch.Struck -= OnLightningStrike;
            _viewCamera.StopFlight();
            _viewCamera.SetPitchAndDistance(48, 24);
            eventCamera = returningFromEvent = returnTo2D = CloseAfterCameraReturn = false;
            returnPawn = null;
            returnCompletedFrame = -1;
            falling = false;
            overviewRevealStarted = -1;
            constructionMenu = actionMenu = statusMenu = PausedByPlayer = false;
            UpdateFirstPersonWeapon();
            _aimBlend = _weaponRecoil = _weaponRecoilVelocity = 0;
            RetainScene();
            _spatialMaps?.RetainScenes();
        }

        internal void ResumeView()
        {
            _viewInput.ResetMousePosition();
            UpdateSpatialMaps();
            RefreshRetainedGeometry();
            _spatialMaps?.RefreshRetainedGeometry();
            root.SetActive(true);
            LightningStrikePatch.Struck += OnLightningStrike;
            OpenApiExtensions();
        }

        internal void RefreshRetainedGeometry()
        {
            _sceneRetained = false;
            if (!_built) return;
            foreach (var thing in map.listerThings.AllThings)
            {
                TrackActivityThing(thing);
                TrackFireThing(thing);
            }
            UpdateWorldGeometry();
            _worldGeometry.FlushDirtyChunks();
            UpdatePawnAppearances();
        }
    }
}
