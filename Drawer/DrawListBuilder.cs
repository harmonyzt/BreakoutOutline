using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        // ═══════════════════════════════════════════════════════════════════════
        // DRAW LIST BUILDER - STATE MACHINE
        // ═══════════════════════════════════════════════════════════════════════

        private void StepDrawListBuilder()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld?.MainPlayer == null)
            {
                ResetWorldState();
                return;
            }

            switch (_buildPhase)
            {
                case BuildPhase.Idle:
                    float sincePass = Time.realtimeSinceStartup - _lastPassStartTime;
                    if (sincePass >= PassIntervalSeconds ||
                        (sincePass >= EarlyPassMinSeconds &&
                         _cacheBuiltSincePass && _cacheQueue.Count == 0))
                        BeginPass(gameWorld);
                    break;
                case BuildPhase.Items: StepItemsPhase(); break;
                case BuildPhase.Containers: StepContainersPhase(); break;
                case BuildPhase.Bodies: StepBodiesPhase(); break;
                case BuildPhase.Finalize: FinalizePass(); break;
            }
        }

        private void BeginPass(GameWorld gameWorld)
        {
            float passStart = Time.realtimeSinceStartup;
            
            _lastPassStartTime = Time.realtimeSinceStartup;
            _cacheBuiltSincePass = false;

            _passPlayerPos = gameWorld.MainPlayer.Transform.position;
            _passRange = Plugin.DetectionRange.Value;
            float prefilter = _passRange + 2f;
            _passPrefilterSqr = prefilter * prefilter;
            _seenPlayers.RemoveWhere(_destroyedPlayer);

            // Fresh accumulation for this pass
            _playerTransforms.Clear();
            _equippedItemIds.Clear();
            _activeThisTick.Clear();
            _drawObjectsScratch.Clear();

            // Deep equipped-ID walk for local player
            if (gameWorld.MainPlayer is MonoBehaviour mpMb)
                _playerTransforms.Add(mpMb.transform);
            if (Time.realtimeSinceStartup - _mainEquipBuiltAt >= MainEquipRefreshSeconds)
            {
                _mainEquipIds = CollectEquippedIds(gameWorld.MainPlayer);
                _mainEquipBuiltAt = Time.realtimeSinceStartup;
            }
            for (int mi = 0; mi < _mainEquipIds.Length; mi++)
                _equippedItemIds.Add(_mainEquipIds[mi]);

            var allPlayers = gameWorld.RegisteredPlayers;
            if (allPlayers != null)
                foreach (var p in allPlayers)
                {
                    if (ReferenceEquals(p, gameWorld.MainPlayer)) continue;
                    AddPlayerToScratch(p);
                    if (p is Player pl) _seenPlayers.Add(pl);
                }

            // Dead-body positions
            _deadBodyPositions.Clear();
            float corpseEquipRangeSqr = prefilter * prefilter;
            foreach (var dp in _seenPlayers)
            {
                if (dp == null || ReferenceEquals(dp, gameWorld.MainPlayer)) continue;
                var hc = dp.HealthController;
                if (hc != null && hc.IsAlive) continue;
                if (dp.Transform == null) continue;
                Vector3 corpsePos = dp.Transform.position;
                if (Plugin.DrawDeadBodies.Value)
                    _deadBodyPositions.Add(corpsePos);

                if ((corpsePos - _passPlayerPos).sqrMagnitude <= corpseEquipRangeSqr)
                {
                    _playerTransforms.Add(((MonoBehaviour)dp).transform);

                    int cid = dp.gameObject.GetInstanceID();
                    _activeThisTick.Add(cid);
                    if (!_corpseEquipCache.TryGetValue(cid, out var ce) ||
                        Time.realtimeSinceStartup - ce.BuiltAt >= CorpseEquipRefreshSeconds)
                    {
                        ce = new CorpseEquip
                        {
                            Ids = CollectEquippedIds(dp),
                            BuiltAt = Time.realtimeSinceStartup,
                        };
                        _corpseEquipCache[cid] = ce;
                    }
                    var ids = ce.Ids;
                    for (int i = 0; i < ids.Length; i++) _equippedItemIds.Add(ids[i]);
                }
            }

            _seenSnapshot.Clear();
            _seenSnapshot.AddRange(_seenPlayers);

            // Snapshot the live world-loot registry
            _passDiag = false;
            if (Plugin.OutlineLooseItems.Value)
            {
                _lootItemsSnapshot.Clear();
                var lootRegistry = gameWorld.LootItems;
                var registryList = lootRegistry != null ? lootRegistry._iteration : null;
                if (registryList != null && registryList.Count > 0)
                {
                    _lootItemsSnapshot.AddRange(registryList);
                }
                else
                {
                    float nowR = Time.realtimeSinceStartup;
                    if (nowR - _lootItemCacheBuildTime >= LootItemCacheRefreshSeconds)
                    {
                        _lootItemCache = FindObjectsOfType<LootItem>();
                        _lootItemCacheBuildTime = nowR;
                    }
                    _lootItemsSnapshot.AddRange(_lootItemCache);
                }

                if (Plugin.DebugLogging.Value &&
                    Time.realtimeSinceStartup - _lastLootDiagTime >= LootItemCacheRefreshSeconds)
                {
                    _passDiag = true;
                    _lastLootDiagTime = Time.realtimeSinceStartup;
                }
            }

            // Container cache
            if (Plugin.OutlineContainers.Value)
            {
                float nowC = Time.realtimeSinceStartup;
                float cadence = _containerCache.Length == 0
                    ? ContainerCacheRetrySeconds
                    : ContainerRescanSeconds;
                if (nowC - _containerCacheTryTime >= cadence)
                {
                    var scan = FindObjectsOfType<LootableContainer>();
                    if (scan.Length > _containerCache.Length) _containerCache = scan;
                    _containerCacheTryTime = nowC;
                }
            }

            _diagInRange = _diagOwned = _diagQueued = 0;
            _buildCursor = 0;
            _buildPhase = BuildPhase.Items;
            
            // Log
            LogDrawPerformance("begin-pass", passStart);
        }

        private void StepItemsPhase()
        {
            if (!Plugin.OutlineLooseItems.Value || _lootItemsSnapshot.Count == 0)
            {
                _buildCursor = 0;
                _buildPhase = BuildPhase.Containers;
                return;
            }

            int end = Mathf.Min(_buildCursor + ItemsPerFrame, _lootItemsSnapshot.Count);
            for (int i = _buildCursor; i < end; i++)
            {
                var li = _lootItemsSnapshot[i];
                if (li == null || li.gameObject == null) continue;

                if (!li.gameObject.activeInHierarchy)
                {
                    _activeThisTick.Add(li.gameObject.GetInstanceID());
                    continue;
                }

                var renderers = li.gameObject.GetComponentsInChildren<Renderer>(false);
                if (renderers.Length == 0)
                {
                    _activeThisTick.Add(li.gameObject.GetInstanceID());
                    continue;
                }

                float dSqr = (li.transform.position - _passPlayerPos).sqrMagnitude;

                if (dSqr > _passPrefilterSqr) continue;

                _diagInRange++;

                if (IsOwnedByAnyPlayer(li, _playerTransforms, _equippedItemIds))
                {
                    _diagOwned++;
                    if (_passDiag && _loggedOwnershipRejects.Add(li.gameObject.name))
                        Plugin.LogSource?.LogInfo($"[BreakoutOutline] '{li.gameObject.name}' rejected by IsOwnedByAnyPlayer");
                    continue;
                }
                if (IsOnDeadBody(li.transform.position, _deadBodyPositions)) continue;
                _diagQueued++;
                LogQueuedDiag(_passDiag, li);
                GatherTarget(li.gameObject, _passPlayerPos, _passRange,
                             expandToPrefabRoot: false,
                             isContainer: false);
            }

            _buildCursor = end;
            if (_buildCursor >= _lootItemsSnapshot.Count)
            {
                if (_passDiag)
                    Plugin.LogSource?.LogInfo(
                        $"[BreakoutOutline] loose-item scan: LootItem={_lootItemsSnapshot.Count}, " +
                        $"inRange={_diagInRange}, ownedRejected={_diagOwned}, queued={_diagQueued}");
                _buildCursor = 0;
                _buildPhase = BuildPhase.Containers;
            }
        }

        private void StepContainersPhase()
        {
            if (!Plugin.OutlineContainers.Value || _containerCache.Length == 0)
            {
                _buildCursor = 0;
                _buildPhase = BuildPhase.Bodies;
                return;
            }

            int end = Mathf.Min(_buildCursor + ContainersPerFrame, _containerCache.Length);
            for (int i = _buildCursor; i < end; i++)
            {
                var c = _containerCache[i];
                if (c == null || c.gameObject == null) continue;
                if ((c.transform.position - _passPlayerPos).sqrMagnitude > _passPrefilterSqr) continue;
                if (!ContainerHasLoot(c) && !Plugin.OutlineEmptyContainers.Value) continue;
                if (Plugin.HideSearchedContainers.Value && ContainerHasBeenSearched(c)) continue;
                if (_playerTransforms.Contains(c.transform.root)) continue;
                GatherTarget(c.gameObject, _passPlayerPos, _passRange,
                             expandToPrefabRoot: true,
                             isContainer: true);
            }

            _buildCursor = end;
            if (_buildCursor >= _containerCache.Length)
            {
                _buildCursor = 0;
                _buildPhase = BuildPhase.Bodies;
            }
        }

        private void FinalizePass()
        {
            int maxOutlines = Plugin.MaxOutlinedObjects.Value;
            if (maxOutlines > 0 && _drawObjectsScratch.Count > maxOutlines)
            {
                _drawSortOrigin = _passPlayerPos;
                _drawObjectsScratch.Sort(_drawSortCmp);
                _drawObjectsScratch.RemoveRange(maxOutlines, _drawObjectsScratch.Count - maxOutlines);
            }

            _drawObjects.Clear();
            _drawObjects.AddRange(_drawObjectsScratch);
            PruneStaleCaches();
            _activeThisTick.Clear();

            _drawListDirty = true;
            _buildPhase = BuildPhase.Idle;
        }

        private void PruneStaleCaches()
        {
            _staleIds.Clear();
            foreach (var id in _rendererCache.Keys)
                if (!_activeThisTick.Contains(id)) _staleIds.Add(id);
            foreach (var id in _staleIds)
            {
                if (_rendererCache.TryGetValue(id, out var rd)) ReleaseCorpseSmrs(rd);
                _rendererCache.Remove(id);
            }

            _staleIds.Clear();
            foreach (var id in _failRetryAt.Keys)
                if (!_activeThisTick.Contains(id)) _staleIds.Add(id);
            foreach (var id in _staleIds) _failRetryAt.Remove(id);

            _staleIds.Clear();
            foreach (var id in _corpseEquipCache.Keys)
                if (!_activeThisTick.Contains(id)) _staleIds.Add(id);
            foreach (var id in _staleIds) _corpseEquipCache.Remove(id);
        }

        private void ResetWorldState()
        {
            DetachCommandBuffer();
            _mainCam = null;
            _limitOpacityForNightVision = false;
            foreach (var kv in _rendererCache) ReleaseCorpseSmrs(kv.Value);
            _drawObjects.Clear();
            _drawObjectsScratch.Clear();
            _rendererCache.Clear();
            _activeThisTick.Clear();
            _cacheQueue.Clear();
            _pendingCacheIds.Clear();
            _failRetryAt.Clear();
            _corpseEquipCache.Clear();
            _seenPlayers.Clear();
            _seenSnapshot.Clear();
            _deadBodyPositions.Clear();
            _containerCache = System.Array.Empty<LootableContainer>();
            _containerCacheTryTime = -999f;
            _containerLootCache.Clear();
            _lootItemCache = System.Array.Empty<LootItem>();
            _lootItemCacheBuildTime = -999f;
            _lootItemsSnapshot.Clear();
            _mainEquipIds = System.Array.Empty<string>();
            _mainEquipBuiltAt = -999f;
            _buildPhase = BuildPhase.Idle;
            _buildCursor = 0;
        }
    }
}
