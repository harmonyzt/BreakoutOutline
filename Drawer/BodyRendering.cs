using Comfort.Common;
using EFT;
using UnityEngine;

namespace ABI_H.Drawer
{
    public partial class LootOutlineController
    {
        // Dead bodies, sliced over the ever-seen snapshot (the live RegisteredPlayers list drops some boss/scav corpses after death)
        private void StepBodiesPhase()
        {
            if (!Plugin.DrawDeadBodies.Value || _seenSnapshot.Count == 0)
            {
                _buildPhase = BuildPhase.Finalize;
                return;
            }

            var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
            int end = Mathf.Min(_buildCursor + BodiesPerFrame, _seenSnapshot.Count);
            for (int i = _buildCursor; i < end; i++)
            {
                var p = _seenSnapshot[i];
                if (p == null || ReferenceEquals(p, mainPlayer)) continue;
                var hc = p.HealthController;
                if (hc != null && hc.IsAlive) continue;
                if (p.gameObject == null || p.Transform == null) continue;

                Vector3 bodyPos = p.Transform.position;
                if ((bodyPos - _passPlayerPos).sqrMagnitude > _passPrefilterSqr) continue;

                GatherTarget(p.gameObject, _passPlayerPos, _passCamPos, _passRange,
                             applyHeldExclusion: false,
                             expandToPrefabRoot: false,
                             isContainer: true,
                             bodyOnly: true,
                             worldPosOverride: bodyPos);
            }

            _buildCursor = end;
            if (_buildCursor >= _seenSnapshot.Count)
                _buildPhase = BuildPhase.Finalize;
        }

        // Undo updateWhenOffscreen=true when a corpse cache entry is dropped
        private static void ReleaseCorpseSmrs(in RendererData rd)
        {
            if (!rd.IsBody) return;
            var entries = rd.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                var smr = entries[i].Smr;
                if (smr != null) smr.updateWhenOffscreen = false;
            }
        }
    }
}