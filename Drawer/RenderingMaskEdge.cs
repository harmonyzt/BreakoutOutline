using UnityEngine;
using UnityEngine.Rendering;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        // Mask + Edge render

        private void RebuildMaskEdgeCommandBuffer()
        {
            if (_cmd == null) return;
            _cmd.Clear();

            var cam = _attachedCam;
            if (cam == null) return;
            RecordRenderDimensions(cam);

            bool losOn = Plugin.LineOfSightCheck.Value;
            ApplyDepthMode(cam, losOn);
            if (_drawObjects.Count == 0) return;

            // Cache frequently accessed values for the frame
            CacheFrameRenderData(cam, losOn);
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            // Cull objects outside frustum and behind camera
            _visibleIdxScratch.Clear();
            int count = _drawObjects.Count;
            for (int oi = 0; oi < count; oi++)
            {
                var d = _drawObjects[oi];

                // Back-face cull using cached camera forward
                float fwdDist = Vector3.Dot(_cachedCamFwd, d.Bounds.center - _cachedCamPos);
                if (fwdDist < 0f) continue;

                // Frustum cull with live bounds
                if (!ComputeLiveBounds(d.Entries, out var liveBounds)) liveBounds = d.Bounds;
                if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, liveBounds)) continue;

                _visibleIdxScratch.Add(oi);
            }
            if (_visibleIdxScratch.Count == 0)
            {
                InvalidateFrameCache();
                return;
            }

            // Allocate mask RT: RGB = colour, A = signed eye-depth
            int w = cam.targetTexture != null
                ? Mathf.Max(1, cam.targetTexture.width)
                : Mathf.Max(1, cam.pixelWidth);
            int h = cam.targetTexture != null
                ? Mathf.Max(1, cam.targetTexture.height)
                : Mathf.Max(1, cam.pixelHeight);
            _cmd.GetTemporaryRT(_maskRtId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            _cmd.GetTemporaryRT(_fadeRtId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.RHalf);
            _cmd.SetRenderTarget(_maskRtId);
            _cmd.ClearRenderTarget(false, true, Color.clear);

            // Render all silhouettes to mask RT
            int visCount = _visibleIdxScratch.Count;
            for (int vi = 0; vi < visCount; vi++)
            {
                var d = _drawObjects[_visibleIdxScratch[vi]];

                Color color = d.IsContainer ? _cachedContColor : _cachedItemColor;
                float bias = d.IsBody ? _cachedBodyBias : ItemDepthBias;

                int n = d.Entries.Length;
                if (_matrixBuffer.Length < n)
                    _matrixBuffer = new Matrix4x4[Mathf.Max(n, _matrixBuffer.Length * 2)];

                // Precompute matrices for static meshes once
                PrecomputeMatrices(d.Entries, _matrixBuffer);

                // Batch static meshes with MaterialPropertyBlock
                _mpb.Clear();
                _mpb.SetColor(PROP_OutlineColor, color);
                _mpb.SetFloat(PROP_DepthOcclude, _cachedOccl);
                _mpb.SetFloat(PROP_DepthBias, bias);
                _mpb.SetFloat(PROP_MaskBodyFlag, d.IsBody ? 1f : 0f);
                _mpb.SetFloat(PROP_OutlineFade, d.Fade);
                _mpb.SetFloat(PROP_MaskFadeOnly, 0f);

                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    int sc = e.Mesh.subMeshCount;
                    for (int s = 0; s < sc; s++)
                        _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], _maskMat, s, 0, _mpb);
                }

                // Render skinned bodies (check once using helper)
                if (HasSkinnedMeshRenderers(d.Entries))
                {
                    _cmd.SetGlobalColor(PROP_OutlineColor, color);
                    _cmd.SetGlobalFloat(PROP_DepthOcclude, _cachedOccl);
                    _cmd.SetGlobalFloat(PROP_DepthBias, bias);
                    _cmd.SetGlobalFloat(PROP_MaskBodyFlag, d.IsBody ? 1f : 0f);
                    _cmd.SetGlobalFloat(PROP_OutlineFade, d.Fade);
                    _cmd.SetGlobalFloat(PROP_MaskFadeOnly, 0f);

                    for (int i = 0; i < n; i++)
                    {
                        var smr = d.Entries[i].Smr;
                        if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                        var mesh = smr.sharedMesh;
                        if (mesh == null) continue;
                        int subCount = mesh.subMeshCount;
                        for (int s = 0; s < subCount; s++)
                            _cmd.DrawRenderer(smr, _maskMat, s, 0);
                    }
                }
            }

            // Render fade mask (per-object opacity)
            _cmd.SetRenderTarget(_fadeRtId);
            _cmd.ClearRenderTarget(false, true, Color.clear);
            RecordFadeMaskPass();

            // Single fullscreen edge detection pass
            float widthPx = Plugin.OutlineWidth.Value;
            _edgeMat.SetFloat(PROP_OutlineWidthPx, widthPx);
            _edgeMat.SetFloat(PROP_EdgeOcclude, _cachedOccl);
            _edgeMat.SetFloat(PROP_EdgeDepthBiasItem, EdgeItemDepthBias);
            _edgeMat.SetFloat(PROP_EdgeDepthBiasBody, _cachedBodyBias);
            _edgeMat.SetFloat(PROP_OutlineAlpha, 1f);
            _cmd.SetGlobalTexture("_FadeTex", _fadeRtId);

            _cmd.Blit(_maskRtId, BuiltinRenderTextureType.CameraTarget, _edgeMat);
            _cmd.ReleaseTemporaryRT(_maskRtId);
            _cmd.ReleaseTemporaryRT(_fadeRtId);

            InvalidateFrameCache();
        }

        private void RecordFadeMaskPass()
        {
            for (int vi = 0; vi < _visibleIdxScratch.Count; vi++)
            {
                var d = _drawObjects[_visibleIdxScratch[vi]];
                Color objectColor = d.IsContainer ? _cachedContColor : _cachedItemColor;
                float opacity = objectColor.a * d.Fade;
                int n = d.Entries.Length;

                // Precompute matrices for static meshes
                if (_matrixBuffer.Length < n)
                    _matrixBuffer = new Matrix4x4[Mathf.Max(n, _matrixBuffer.Length * 2)];
                PrecomputeMatrices(d.Entries, _matrixBuffer);

                // Batch static meshes
                _mpb.Clear();
                _mpb.SetFloat(PROP_OutlineFade, opacity);
                _mpb.SetFloat(PROP_MaskFadeOnly, 1f);
                _mpb.SetFloat(PROP_DepthOcclude, _cachedOccl);
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    for (int s = 0; s < e.Mesh.subMeshCount; s++)
                        _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], _maskMat, s, 0, _mpb);
                }

                // Render skinned meshes if present
                if (!HasSkinnedMeshRenderers(d.Entries)) continue;

                _cmd.SetGlobalFloat(PROP_OutlineFade, opacity);
                _cmd.SetGlobalFloat(PROP_MaskFadeOnly, 1f);
                _cmd.SetGlobalFloat(PROP_DepthOcclude, _cachedOccl);
                for (int i = 0; i < n; i++)
                {
                    var smr = d.Entries[i].Smr;
                    if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh == null)
                        continue;
                    for (int s = 0; s < smr.sharedMesh.subMeshCount; s++)
                        _cmd.DrawRenderer(smr, _maskMat, s, 0);
                }
            }
        }
    }
}
