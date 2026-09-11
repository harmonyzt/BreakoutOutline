using System;
using EFT.CameraControl;
using UnityEngine;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        private Camera ResolveFpsCamera()
        {
            if (_mainCam != null) return _mainCam;

            try
            {
                var inst = CameraManager.Instance;
                if (inst != null) _mainCam = inst.Camera;
            }
            catch { }

            return _mainCam;
        }

        private void EnsureCommandBufferAttached()
        {
            if (_cmd == null) return;
            var cam = _mainCam;
            if (cam == _attachedCam) return;
            DetachCommandBuffer();
            _recordedCameraWidth = -1;
            _recordedCameraHeight = -1;
            _recordedScreenWidth = -1;
            _recordedScreenHeight = -1;
            _recordedTargetTextureId = -1;
            _recordedTargetWidth = -1;
            _recordedTargetHeight = -1;
            if (cam != null)
            {
                cam.AddCommandBuffer(OutlineEvent, _cmd);
                _eftProvidesDepth = (cam.depthTextureMode & DepthTextureMode.Depth) != 0;
                _attachedCam = cam;
                ApplyDepthMode(cam, Plugin.LineOfSightCheck.Value);
                if (Plugin.DebugLogging.Value) LogOcclusionDiag(cam);
            }
        }

        private void DetachCommandBuffer()
        {
            if (_cmd == null || _attachedCam == null) { _attachedCam = null; return; }
            try { _attachedCam.RemoveCommandBuffer(OutlineEvent, _cmd); }
            catch { }
            _attachedCam = null;
            _recordedCameraWidth = -1;
            _recordedCameraHeight = -1;
            _recordedScreenWidth = -1;
            _recordedScreenHeight = -1;
            _recordedTargetTextureId = -1;
            _recordedTargetWidth = -1;
            _recordedTargetHeight = -1;
        }

        private bool RenderDimensionsChanged()
        {
            var cam = _attachedCam;
            if (cam == null) return false;

            return cam.pixelWidth != _recordedCameraWidth
                || cam.pixelHeight != _recordedCameraHeight
                || Screen.width != _recordedScreenWidth
                || Screen.height != _recordedScreenHeight
                || (cam.targetTexture != null ? cam.targetTexture.GetInstanceID() : 0) != _recordedTargetTextureId
                || (cam.targetTexture != null ? cam.targetTexture.width : 0) != _recordedTargetWidth
                || (cam.targetTexture != null ? cam.targetTexture.height : 0) != _recordedTargetHeight;
        }

        private void RecordRenderDimensions(Camera cam)
        {
            _recordedCameraWidth = cam.pixelWidth;
            _recordedCameraHeight = cam.pixelHeight;
            _recordedScreenWidth = Screen.width;
            _recordedScreenHeight = Screen.height;
            var target = cam.targetTexture;
            _recordedTargetTextureId = target != null ? target.GetInstanceID() : 0;
            _recordedTargetWidth = target != null ? target.width : 0;
            _recordedTargetHeight = target != null ? target.height : 0;
        }

        private void ApplyDepthMode(Camera cam, bool losOn)
        {
            if (cam == null) return;
            if (losOn) cam.depthTextureMode |= DepthTextureMode.Depth;
            else if (!_eftProvidesDepth) cam.depthTextureMode &= ~DepthTextureMode.Depth;
        }

        private void LogOcclusionDiag(Camera cam)
        {
            try
            {
                var depthTex = Shader.GetGlobalTexture("_CameraDepthTexture");
                string depthDesc = depthTex != null
                    ? $"{depthTex.name} {depthTex.width}x{depthTex.height}"
                    : "NULL(at-Update; may still bind during render)";

                float itemOcc = _itemDrawMat != null ? _itemDrawMat.GetFloat("_DepthOcclude") : -1f;
                int itemZ = _itemDrawMat != null ? _itemDrawMat.GetInt("_ZTest") : -1;

                Plugin.LogSource?.LogInfo(
                    "[BreakoutOutline][OCCLUSION DIAG] " +
                    $"cam='{cam.name}' renderPath={cam.actualRenderingPath} " +
                    $"hdr={cam.allowHDR} msaa={cam.allowMSAA} depthMode={cam.depthTextureMode} " +
                    $"eftProvidesDepth={_eftProvidesDepth} " +
                    $"event={OutlineEvent} | _CameraDepthTexture={depthDesc} | " +
                    $"losCfg={Plugin.LineOfSightCheck.Value} itemDepthOcclude={itemOcc} itemZTest={itemZ} " +
                    $"| maskEdge={_useMaskEdge} threePass={_useThreePass}");
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogWarning($"[LootOutline][OCCLUSION DIAG] failed: {e.Message}");
            }
        }
    }
}
