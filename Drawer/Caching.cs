using UnityEngine;
using System.Collections;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        // Cached per-frame values to avoid repeated calculations
        private Vector3 _cachedCamPos;
        private Vector3 _cachedCamFwd;
        private Color _cachedItemColor;
        private Color _cachedContColor;
        private float _cachedBodyBias;
        private float _cachedOccl;
        private bool _frameDataCached;

        // Reusable pools to reduce GC pressure
        private static readonly System.Collections.Generic.Stack<System.Collections.Generic.List<MeshEntry>> _entryListPool
            = new System.Collections.Generic.Stack<System.Collections.Generic.List<MeshEntry>>();

        private void CacheFrameRenderData(Camera cam, bool losOn)
        {
            if (_frameDataCached) return;

            _cachedCamPos = cam.transform.position;
            _cachedCamFwd = cam.transform.forward;
            _cachedItemColor = GetOutlineColor(false);
            _cachedContColor = GetOutlineColor(true);
            _cachedBodyBias = Plugin.BodyDepthBias.Value;
            _cachedOccl = losOn ? 1f : 0f;
            _frameDataCached = true;
        }

        private void InvalidateFrameCache()
        {
            _frameDataCached = false;
        }

        // private static System.Collections.Generic.List<MeshEntry> RentEntryList()
        // {
        //     return _entryListPool.Count > 0
        //         ? _entryListPool.Pop()
        //         : new System.Collections.Generic.List<MeshEntry>();
        // }
        //
        // private static void ReturnEntryList(System.Collections.Generic.List<MeshEntry> list)
        // {
        //     if (list == null) return;
        //     list.Clear();
        //     if (_entryListPool.Count < 8)
        //         _entryListPool.Push(list);
        // }

        // Batch SMR check
        private static bool HasSkinnedMeshRenderers(MeshEntry[] entries)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Smr != null)
                    return true;
            }
            return false;
        }

        // Precompute matrices once per object
        private void PrecomputeMatrices(MeshEntry[] entries, Matrix4x4[] buffer)
        {
            int n = entries.Length;
            if (buffer.Length < n)
                return;

            for (int i = 0; i < n; i++)
            {
                var e = entries[i];
                if (e.Smr != null) continue;
                buffer[i] = e.Tx != null
                    ? e.Tx.localToWorldMatrix * e.LocalOffset
                    : e.FixedMatrix;
            }
        }
        
        // Async cache builder
        private IEnumerator CacheBuilderLoop()
        {
            // Idles when the queue is empty; otherwise processes up to CacheBudgetSeconds of work per frame.
            while (true)
            {
                if (_cacheQueue.Count == 0)
                {
                    yield return null;
                    continue;
                }

                float start = Time.realtimeSinceStartup;
                int processed = 0;
                int built = 0;
                int failed = 0;
                while (_cacheQueue.Count > 0
                       && (Time.realtimeSinceStartup - start) < CacheBudgetSeconds)
                {
                    var req = _cacheQueue.Dequeue();
                    _pendingCacheIds.Remove(req.InstanceId);
                    processed++;

                    if (req.Go == null) continue;
                    if (_rendererCache.ContainsKey(req.InstanceId))
                        continue;

                    float buildStart = Time.realtimeSinceStartup;
                    var rd = CollectRenderers(req.Go, req.ExpandToPrefabRoot, req.BodyOnly);
                    float buildMs = (Time.realtimeSinceStartup - buildStart) * 1000f;
                    if (Plugin.DebugLogging.Value)
                    {
                        Plugin.LogSource?.LogInfo(
                            $"[BreakoutOutline][PERF][CACHE ITEM] name='{req.Go.name}' " +
                            $"ms={buildMs:F3} entries={rd.Entries.Length} " +
                            $"body={req.BodyOnly} expanded={req.ExpandToPrefabRoot}");
                    }
                    if (rd.Entries.Length > 0)
                    {
                        _rendererCache[req.InstanceId] = rd;
                        _failRetryAt.Remove(req.InstanceId);
                        _cacheBuiltSincePass = true;
                        built++;
                    }
                    else
                    {
                        failed++;
                        _failRetryAt[req.InstanceId] = Time.realtimeSinceStartup
                            + (req.BodyOnly ? BodyFailRetrySeconds : ItemFailRetrySeconds);
                        if (Plugin.DebugLogging.Value
                            && !req.BodyOnly && !req.ExpandToPrefabRoot
                            && _loggedItemFailures.Add(req.Go.name))
                        {
                            int totalChildren = req.Go.GetComponentsInChildren<Transform>(true).Length;
                            int activeRends = req.Go.GetComponentsInChildren<Renderer>(false).Length;
                            int allRends = req.Go.GetComponentsInChildren<Renderer>(true).Length;
                            Plugin.LogSource?.LogInfo(
                                $"[BreakoutOutline] Item '{req.Go.name}' produced 0 entries " +
                                $"(children={totalChildren}, activeRenderers={activeRends}, " +
                                $"allRenderers={allRends}, activeInHierarchy={req.Go.activeInHierarchy})");
                        }
                    }
                }
                if (Plugin.DebugLogging.Value)
                {
                    Plugin.LogSource?.LogInfo(
                        $"[BreakoutOutline][PERF][CACHE] ms={(Time.realtimeSinceStartup - start) * 1000f:F3} " +
                        $"processed={processed} built={built} failed={failed} " +
                        $"remaining={_cacheQueue.Count} cached={_rendererCache.Count}");
                }
                yield return null;
            }
        }

        // Draw gate for STATIC entries: the cached renderer must actually be rendering.
        private static bool EntryAlive(in MeshEntry e)
        {
            var r = e.Rend;
            return r == null || (r.enabled && r.gameObject.activeInHierarchy);
        }

        // Current world-AABB from each entry's live renderer.
        private static bool ComputeLiveBounds(MeshEntry[] entries, out Bounds bounds)
        {
            bounds = default;
            bool seeded = false;
            foreach (var e in entries)
            {
                Renderer r = e.Smr != null ? (Renderer)e.Smr : e.Rend;
                if (r == null) continue;
                if (!r.enabled) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                Bounds rb = r.bounds;
                if (seeded) bounds.Encapsulate(rb); else { bounds = rb; seeded = true; }
            }
            return seeded;
        }
    }
}
