using System;
using System.Collections.Generic;
using UnityEngine;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        private void GatherTarget(GameObject go, Vector3 playerPos, float range,
                                  bool expandToPrefabRoot, bool isContainer,
                                  bool bodyOnly = false,
                                  Vector3? worldPosOverride = null)
        {
            int id = go.GetInstanceID();
            _activeThisTick.Add(id);

            Vector3 worldPos = worldPosOverride ?? go.transform.position;
            if (Vector3.Distance(playerPos, worldPos) > range) return;
            if (!_rendererCache.TryGetValue(id, out var cached))
            {
                bool blocked = _failRetryAt.TryGetValue(id, out float retryAt)
                            && Time.realtimeSinceStartup < retryAt;
                if (!_pendingCacheIds.Contains(id) && !blocked)
                {
                    _pendingCacheIds.Add(id);
                    _cacheQueue.Enqueue(new PendingCache
                    {
                        Go = go,
                        ExpandToPrefabRoot = expandToPrefabRoot,
                        BodyOnly = bodyOnly,
                        InstanceId = id,
                    });
                }
                return;
            }

            if (!isContainer)
            {
                Vector3 s = cached.Bounds.size;
                float largest = Mathf.Max(s.x, Mathf.Max(s.y, s.z));

                if (largest < MinItemSize) return;
            }

            float fade = Mathf.Clamp01(1f - Vector3.Distance(playerPos, worldPos) / Mathf.Max(range, 0.001f));

            _drawObjectsScratch.Add(new DrawObject(id, cached.Entries, isContainer, cached.Bounds, fade, bodyOnly));
        }

        private static RendererData CollectRenderers(GameObject go, bool expandToPrefabRoot, bool bodyOnly = false)
        {
            var entries = new List<MeshEntry>();
            Bounds combined = default;
            bool hasBounds = false;

            bool includeInactive = bodyOnly || !expandToPrefabRoot;
            var selected = go.GetComponentsInChildren<Renderer>(includeInactive);

            var lodGroups = go.GetComponentsInChildren<LODGroup>(includeInactive);
            if (!bodyOnly && lodGroups != null && lodGroups.Length > 0)
            {
                var lod0Set = new HashSet<Renderer>();
                foreach (var lg in lodGroups)
                {
                    if (lg == null) continue;
                    var lods = lg.GetLODs();
                    if (lods == null || lods.Length == 0) continue;
                    var lod0 = lods[0].renderers;
                    if (lod0 == null) continue;
                    foreach (var r in lod0) if (r != null) lod0Set.Add(r);
                }
                if (lod0Set.Count > 0)
                {
                    var filtered = new List<Renderer>(selected.Length);
                    foreach (var r in selected)
                    {
                        if (r == null) continue;
                        if (lod0Set.Contains(r)) { filtered.Add(r); continue; }
                        bool ownedByAnyLod = false;
                        foreach (var lg in lodGroups)
                        {
                            if (lg == null) continue;
                            var lods = lg.GetLODs();
                            if (lods == null) continue;
                            for (int i = 0; i < lods.Length && !ownedByAnyLod; i++)
                            {
                                var rs = lods[i].renderers;
                                if (rs == null) continue;
                                for (int j = 0; j < rs.Length; j++)
                                    if (ReferenceEquals(rs[j], r)) { ownedByAnyLod = true; break; }
                            }
                            if (ownedByAnyLod) break;
                        }
                        if (!ownedByAnyLod) filtered.Add(r);
                    }
                    selected = filtered.ToArray();
                }
            }

            if (expandToPrefabRoot && !bodyOnly && go.activeInHierarchy)
            {
                const int MaxParentChildren = 12;
                const int MaxWalkUpLevels = 2;
                Transform p = go.transform.parent;
                for (int w = 0; w < MaxWalkUpLevels && p != null; w++)
                {
                    if (w > 0 && p.childCount > MaxParentChildren) break;
                    var expanded = p.GetComponentsInChildren<Renderer>(false);
                    if (expanded.Length <= selected.Length) break;
                    if (expanded.Length > RootExpansionRendererCap) break;
                    selected = expanded;
                    p = p.parent;
                }
            }

            if (bodyOnly && _bodiesLogged < BodyLogCap && Plugin.DebugLogging.Value)
            {
                _bodiesLogged++;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[BreakoutOutline][BODY DIAG #{_bodiesLogged}] '{go.name}' - {selected.Length} renderer(s):");
                foreach (var r in selected)
                {
                    if (r == null) { sb.AppendLine("  • <null>"); continue; }
                    bool isSmr = r is SkinnedMeshRenderer;
                    var smrCast = r as SkinnedMeshRenderer;
                    string meshName = isSmr
                        ? (smrCast.sharedMesh != null ? smrCast.sharedMesh.name : "<null>")
                        : (r.GetComponent<MeshFilter>()?.sharedMesh != null ? r.GetComponent<MeshFilter>().sharedMesh.name : "<null>");
                    string decision = !isSmr ? "SKIP (non-skinned)"
                                    : smrCast.sharedMesh == null ? "SKIP (no mesh)"
                                    : IsWornEquipment(r.transform) ? "SKIP (worn gear)"
                                    : "KEEP";
                    sb.AppendLine($"  • {RendererPath(r.transform)} | {(isSmr ? "SMR" : "Mesh")} " +
                                  $"| en={r.enabled} act={r.gameObject.activeInHierarchy} | mesh={meshName} => {decision}");
                }
                Plugin.LogSource?.LogInfo(sb.ToString());
            }

            foreach (var r in selected)
            {
                if (r == null) continue;

                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null) continue;
                    if (bodyOnly)
                    {
                        if (IsWornEquipment(smr.transform)) continue;
                        smr.updateWhenOffscreen = true;
                    }
                    Bounds b = r.bounds;
                    if (hasBounds) combined.Encapsulate(b); else { combined = b; hasBounds = true; }
                    entries.Add(new MeshEntry(smr));
                }
                else
                {
                    if (bodyOnly) continue;

                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    Bounds b = r.bounds;
                    if (hasBounds) combined.Encapsulate(b); else { combined = b; hasBounds = true; }
                    entries.Add(new MeshEntry(mf.sharedMesh, r.transform, r.gameObject.layer, r));
                }
            }

            if (entries.Count == 0)
                return new RendererData(Array.Empty<MeshEntry>(), default);

            return new RendererData(entries.ToArray(),
                hasBounds ? combined : new Bounds(go.transform.position, Vector3.one),
                bodyOnly);
        }

        private static string RendererPath(Transform t)
        {
            if (t == null) return "<null>";
            string self = t.name;
            string parent = t.parent != null ? t.parent.name : "";
            string grand = t.parent != null && t.parent.parent != null ? t.parent.parent.name : "";
            return string.IsNullOrEmpty(grand) ? $"{parent}/{self}" : $"{grand}/{parent}/{self}";
        }

        private static bool IsWornEquipment(Transform t)
        {
            for (int i = 0; i < 8 && t != null; i++, t = t.parent)
            {
                if (t.name == null) continue;
                if (t.name.StartsWith("item_equipment_", StringComparison.Ordinal) ||
                    t.name.StartsWith("Slot_", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
