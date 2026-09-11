using UnityEngine;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        // Three-Pass rendering
        private void RebuildThreePassCommandBuffer()
        {
            if (_cmd == null) return;
            _cmd.Clear();
            ApplyDepthMode(_attachedCam, Plugin.LineOfSightCheck.Value);
            if (_drawObjects.Count == 0) return;

            // Convert outline width from screen pixels to NDC units
            float widthPx = Plugin.OutlineWidth.Value;
            float widthNdc = (Screen.height > 0)
                ? widthPx * 2f / Screen.height
                : widthPx * 0.003f;

            Color itemColor = GetOutlineColor(false);
            Color contColor = GetOutlineColor(true);

            var cam = _attachedCam;
            if (cam == null) return;

            RecordRenderDimensions(cam);
            Vector3 camPos = cam.transform.position;
            Vector3 camFwd = cam.transform.forward;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            bool losOn = Plugin.LineOfSightCheck.Value;
            _itemDrawMat.SetFloat("_DepthOcclude", losOn ? 1f : 0f);
            _contDrawMat.SetFloat("_DepthOcclude", losOn ? 1f : 0f);
            _bodyDrawMat.SetFloat("_DepthOcclude", losOn ? 1f : 0f);
            _bodyDrawMat.SetFloat("_DepthBias", Plugin.BodyDepthBias.Value);

            int count = _drawObjects.Count;
            for (int oi = 0; oi < count; oi++)
            {
                var d = _drawObjects[oi];

                float fwdDist = Vector3.Dot(camFwd, d.Bounds.center - camPos);
                if (fwdDist < 0f) continue;

                if (!ComputeLiveBounds(d.Entries, out var liveBounds)) liveBounds = d.Bounds;

                if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, liveBounds))
                    continue;

                var stencilMat = d.IsContainer ? _contStencilMat : _itemStencilMat;
                var drawMat = d.IsBody ? _bodyDrawMat
                                          : (d.IsContainer ? _contDrawMat : _itemDrawMat);
                var clearMat = d.IsContainer ? _contClearMat : _itemClearMat;
                var color = d.IsContainer ? contColor : itemColor;
                color.a *= d.Fade;

                int n = d.Entries.Length;
                if (_matrixBuffer.Length < n)
                    _matrixBuffer = new Matrix4x4[Mathf.Max(n, _matrixBuffer.Length * 2)];
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Smr != null) continue;
                    _matrixBuffer[i] = e.Tx != null
                        ? e.Tx.localToWorldMatrix * e.LocalOffset
                        : e.FixedMatrix;
                }

                Vector3 centre = liveBounds.center;

                _mpb.Clear();
                _mpb.SetColor(PROP_OutlineColor, color);
                _mpb.SetFloat(PROP_OutlineWidth, widthNdc);
                _mpb.SetVector(PROP_ObjectCenter, new Vector4(centre.x, centre.y, centre.z, 1f));

                // Stencil pass
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    int sc = e.Mesh.subMeshCount;
                    for (int s = 0; s < sc; s++) _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], stencilMat, s, 0);
                }
                // Draw pass
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    int sc = e.Mesh.subMeshCount;
                    for (int s = 0; s < sc; s++) _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], drawMat, s, 0, _mpb);
                }
                // Clear pass
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    int sc = e.Mesh.subMeshCount;
                    for (int s = 0; s < sc; s++) _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], clearMat, s, 0);
                }

                // Skinned (dead bodies)
                bool anySmr = false;
                for (int i = 0; i < n; i++) { if (d.Entries[i].Smr != null) { anySmr = true; break; } }
                if (anySmr)
                {
                    _cmd.SetGlobalVector(PROP_ObjectCenter,
                        new Vector4(centre.x, centre.y, centre.z, liveBounds.extents.magnitude));
                    _cmd.SetGlobalColor(PROP_OutlineColor, color);
                    _cmd.SetGlobalFloat(PROP_OutlineWidth, widthNdc);

                    for (int i = 0; i < n; i++)
                    {
                        var e = d.Entries[i];
                        if (e.Smr == null) continue;
                        var smr = e.Smr;
                        if (!smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                        var mesh = smr.sharedMesh;
                        if (mesh == null) continue;
                        int subCount = mesh.subMeshCount;
                        for (int s = 0; s < subCount; s++) _cmd.DrawRenderer(smr, stencilMat, s);
                    }
                    for (int i = 0; i < n; i++)
                    {
                        var e = d.Entries[i];
                        if (e.Smr == null) continue;
                        var smr = e.Smr;
                        if (!smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                        var mesh = smr.sharedMesh;
                        if (mesh == null) continue;
                        int subCount = mesh.subMeshCount;
                        for (int s = 0; s < subCount; s++) _cmd.DrawRenderer(smr, drawMat, s);
                    }
                    for (int i = 0; i < n; i++)
                    {
                        var e = d.Entries[i];
                        if (e.Smr == null) continue;
                        var smr = e.Smr;
                        if (!smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                        var mesh = smr.sharedMesh;
                        if (mesh == null) continue;
                        int subCount = mesh.subMeshCount;
                        for (int s = 0; s < subCount; s++) _cmd.DrawRenderer(smr, clearMat, s);
                    }
                }
            }
        }
    }
}
