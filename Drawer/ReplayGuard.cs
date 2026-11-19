using System;
using System.Collections.Generic;
using UnityEngine;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        // The command buffer is rebuilt at ~30Hz and replayed in between. A corpse renderer that
        // gets disabled or pooled in that gap (LOD swap, gear taken off) would be drawn from a
        // released skin buffer, which faults the GPU and hangs the game.
        private readonly struct RecordedSmr
        {
            public readonly SkinnedMeshRenderer Smr;
            public readonly Mesh Mesh;
            public RecordedSmr(SkinnedMeshRenderer smr, Mesh mesh) { Smr = smr; Mesh = mesh; }
        }

        private readonly List<RecordedSmr> _recordedSmrs = new List<RecordedSmr>();

        private void OnCameraPreCull(Camera cam)
        {
            if (_cmd == null || cam != _attachedCam || _recordedSmrs.Count == 0 || !RecordedSmrsStale())
                return;
            try
            {
                if (_useMaskEdge) RebuildMaskEdgeCommandBuffer();
                else if (_useThreePass) RebuildThreePassCommandBuffer();
                else { _cmd.Clear(); _recordedSmrs.Clear(); }
                _lastRebuildTime = Time.realtimeSinceStartup;
            }
            catch (Exception e)
            {
                // Skipping a frame of outlines beats replaying a dead renderer.
                _cmd.Clear();
                _recordedSmrs.Clear();
                Plugin.LogSource?.LogError("[BreakoutOutline] pre-render rebuild failed: " + e);
            }
        }

        private bool RecordedSmrsStale()
        {
            for (int i = 0; i < _recordedSmrs.Count; i++)
            {
                var r = _recordedSmrs[i];
                var smr = r.Smr;
                if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh != r.Mesh)
                    return true;
            }
            return false;
        }
    }
}
