using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ABI_H.Drawer
{
    public partial class LootOutlineController : MonoBehaviour
    {
        // Three-pass fallback pipeline: stencil silhouette -> draw ring -> clear, per object
        private Material _itemStencilMat, _itemDrawMat, _itemClearMat;
        private Material _contStencilMat, _contDrawMat, _contClearMat;
        // Bodies get their own draw material (larger depth bias so a flat corpse doesn't self-clip against the floor)
        private Material _bodyDrawMat;
        private bool     _useThreePass;

        // Preferred pipeline: all silhouettes into one mask RT, then a single fullscreen edge pass.
        // Cost no longer scales with object/submesh count
        private Material _maskMat, _edgeMat;
        private bool     _useMaskEdge;
        private int      _maskRtId;
        private int      _fadeRtId;
        // Contact-surface depth tolerance for items/containers in the mask pass.
        // Bodies use the config-driven BodyDepthBias instead
        private const float ItemDepthBias = 0.03f;
        // Ring occlusion tolerance for item/container rings; slightly looser than
        // the silhouette bias, still far below wall/door thickness
        private const float EdgeItemDepthBias = 0.05f;

        // Legacy single combined shader (10 passes). Used when the three-pass
        // shaders aren't all present in the bundle
        private Material _itemMat;
        private Material _contMat;

        private bool _useShader;

        private CommandBuffer _cmd;
        private Camera        _attachedCam;
        private const CameraEvent OutlineEvent = CameraEvent.AfterForwardAlpha;
        // Did EFT already request a depth texture before we touched the flag?
        // If not, forcing one costs a full-scene depth prepass, so it's only
        // enabled while occlusion is on (see ApplyDepthMode)
        private bool _eftProvidesDepth;

        // Cached once per Update. Must be the real FPS camera - Camera.main can
        // return a wider environment camera and break the back-cull/frustum checks
        private Camera _mainCam;

        // Cached discovery
        private LootableContainer[] _containerCache = Array.Empty<LootableContainer>();

        // Per-pass snapshot of GameWorld.LootItems.List_0 (the live world-loot
        // registry - every LootItem passes through GameWorld.RegisterLoot)
        // Plain reference copy, no scene scan
        private readonly List<LootItem> _lootItemsSnapshot = new List<LootItem>();
        // Fallback if the registry is unexpectedly empty: throttled full-scene scan
        private LootItem[]         _lootItemCache         = Array.Empty<LootItem>();
        private float _lootItemCacheBuildTime = -999f;
        private const float LootItemCacheRefreshSeconds = 10f;
        private float _lastLootDiagTime = -999f;

        // Local-player equipped-ID cache; the deep GetAllItems walk allocates,
        // so it's refreshed on a TTL instead of every pass
        private string[] _mainEquipIds    = Array.Empty<string>();
        private float    _mainEquipBuiltAt = -999f;
        private const float MainEquipRefreshSeconds = 2f;

        // Containers are map-static - scanned with a throttled retry while the
        // world populates, then re-scanned slowly: FindObjectsOfType only returns
        // ACTIVE objects, so containers in rooms EFT had culling-disabled at scan
        // time would otherwise be missing for the whole raid
        private float _containerCacheTryTime = -999f;
        private const float ContainerCacheRetrySeconds = 5f;
        private const float ContainerRescanSeconds = 60f;

        // Per-container has-loot TTL cache. The GetAllItems walk allocates and
        // runs per in-range container per pass; contents only change on looting,
        // so 2s of staleness is invisible. Cleared on world reset.
        private struct ContainerLoot { public bool Has; public float At; }
        private readonly Dictionary<int, ContainerLoot> _containerLootCache
            = new Dictionary<int, ContainerLoot>();
        private const float ContainerLootTtlSeconds = 2f;

        // One renderer's mesh + how to position it. Static meshes keep a live
        // Transform (animated lids still track); SkinnedMeshRenderers go through
        // CommandBuffer.DrawRenderer for live skinning (dead bodies).
        private readonly struct MeshEntry
        {
            public readonly Mesh      Mesh;
            public readonly Transform Tx;          // null → use FixedMatrix
            public readonly Matrix4x4 LocalOffset;
            public readonly Matrix4x4 FixedMatrix;
            public readonly int       Layer;
            public readonly SkinnedMeshRenderer Smr;
            // Cached for live state queries (active/enabled, current world bounds).
            // Static entries store their MeshRenderer here; SMR entries leave it null
            // because Smr already covers everything we need.
            public readonly Renderer  Rend;

            public MeshEntry(Mesh m, Transform t, int l, Renderer r = null)
            { Mesh = m; Tx = t; LocalOffset = Matrix4x4.identity; FixedMatrix = Matrix4x4.identity; Layer = l; Smr = null; Rend = r; }
            public MeshEntry(Mesh m, Transform t, Matrix4x4 off, int l, Renderer r = null)
            { Mesh = m; Tx = t; LocalOffset = off; FixedMatrix = Matrix4x4.identity; Layer = l; Smr = null; Rend = r; }
            public MeshEntry(SkinnedMeshRenderer s)
            { Mesh = null; Tx = null; LocalOffset = Matrix4x4.identity; FixedMatrix = Matrix4x4.identity; Layer = 0; Smr = s; Rend = null; }
        }

        private readonly struct RendererData
        {
            public readonly MeshEntry[] Entries;
            public readonly Bounds      Bounds;
            // Corpse entries set updateWhenOffscreen=true on their SMRs (accurate
            // bounds/visibility for settled ragdolls). That flag costs a full
            // skinning pass per SMR per frame even when the corpse is offscreen,
            // so it MUST be reset when the entry is pruned - see ReleaseCorpseSmrs.
            public readonly bool        IsBody;
            public RendererData(MeshEntry[] e, Bounds b, bool isBody = false) { Entries = e; Bounds = b; IsBody = isBody; }
        }

        private readonly Dictionary<int, RendererData> _rendererCache
            = new Dictionary<int, RendererData>();
        private readonly HashSet<int> _activeThisTick = new HashSet<int>();

        // Async cache-build queue: cache misses are queued and a coroutine drains
        // them on a small per-frame time budget, so entering a dense loot area
        // doesn't hitch on GetComponentsInChildren scans.
        private struct PendingCache
        {
            public GameObject Go;
            public bool ExpandToPrefabRoot;
            public bool BodyOnly;
            public int  InstanceId;
        }
        private readonly Queue<PendingCache> _cacheQueue = new Queue<PendingCache>();
        private readonly HashSet<int>        _pendingCacheIds = new HashSet<int>();
        // Failed cache builds retry on a timed backoff - NEVER a permanent
        // blacklist. A build can transiently return 0 entries while a ragdoll is
        // still initialising, or while EFT's room culling has the object's
        // renderers deactivated for the current player position. Bodies retry
        // fast; items/containers slower (genuinely renderer-less prefabs exist).
        private readonly Dictionary<int, float> _failRetryAt = new Dictionary<int, float>();
        private const float BodyFailRetrySeconds = 1f;
        private const float ItemFailRetrySeconds = 5f;

        // Per-corpse equipped-item-ID cache. The deep loadout walk (GetAllItems)
        // allocates, and a dead body's loadout only changes when looted, so it's
        // refreshed on a TTL rather than every pass.
        private struct CorpseEquip { public string[] Ids; public float BuiltAt; }
        private readonly Dictionary<int, CorpseEquip> _corpseEquipCache = new Dictionary<int, CorpseEquip>();
        private const float CorpseEquipRefreshSeconds = 5f;
        private readonly List<string> _equipScratch = new List<string>();
        private Coroutine _cacheCoroutine;
        // Per-frame time budget for the cache-build coroutine. 3ms: entering a
        // dense loot area outlines within a few frames while staying well under
        // a 60fps frame budget.
        private const float CacheBudgetSeconds = 0.003f;

        // What to draw this tick (rebuilt every slow tick, consumed every frame).
        // Occlusion is per-pixel in the shader, so there's no per-object LOS state to
        // carry - just the geometry, type, and a world AABB for the frustum cull.
        private struct DrawObject
        {
            public MeshEntry[] Entries;
            public bool        IsContainer; // selects material set + colour
            public bool        IsBody;      // dead body: own draw material (larger depth bias)
            public Bounds      Bounds;      // world AABB for per-frame frustum cull
            public float       Fade;       // 0 at detection range, 1 at the player
            public DrawObject(MeshEntry[] e, bool c, Bounds b, float fade, bool body = false)
            { Entries = e; IsContainer = c; IsBody = body; Bounds = b; Fade = fade; }
        }
        private readonly List<DrawObject> _drawObjects = new List<DrawObject>();
        // Every player ever observed alive. SPT removes some bots (especially
        // bosses) from RegisteredPlayers shortly after death, so the live list
        // misses their corpses.
        private readonly HashSet<Player> _seenPlayers = new HashSet<Player>();
        // Unity fake-null: a destroyed Player compares == null.
        private static readonly Predicate<Player> _destroyedPlayer = p => p == null;
        // Dead-player positions this pass, used to suppress weapons that fell
        // with the ragdoll (they'd overlap the body outline).
        private readonly List<Vector3> _deadBodyPositions = new List<Vector3>();
        private const float BodyWeaponSuppressRadius    = 1.15f;
        private const float BodyWeaponSuppressRadiusSqr = BodyWeaponSuppressRadius * BodyWeaponSuppressRadius;
        // Debug-log dedup sets (one line per unique prefab name per session).
        private static readonly HashSet<string> _loggedItemFailures = new HashSet<string>();
        private static int _bodiesLogged;
        private const int  BodyLogCap = 3;
        private static readonly HashSet<string> _loggedQueued = new HashSet<string>();
        private static readonly HashSet<string> _loggedOwnershipRejects = new HashSet<string>();
        private MaterialPropertyBlock _mpb;
        private static readonly int PROP_OutlineColor = Shader.PropertyToID("_OutlineColor");
        private static readonly int PROP_OutlineWidth = Shader.PropertyToID("_OutlineWidth");
        private static readonly int PROP_ObjectCenter = Shader.PropertyToID("_ObjectCenter");
        // Tier-2 mask/edge shader properties.
        private static readonly int PROP_DepthOcclude   = Shader.PropertyToID("_DepthOcclude");
        private static readonly int PROP_DepthBias      = Shader.PropertyToID("_DepthBias");
        private static readonly int PROP_OutlineWidthPx = Shader.PropertyToID("_OutlineWidthPx");
        private static readonly int PROP_EdgeOcclude    = Shader.PropertyToID("_EdgeOcclude");
        // Per-type ring occlusion: the mask stores signed depth (negative = body)
        // and the edge pass picks the matching bias.
        private static readonly int PROP_EdgeDepthBiasItem = Shader.PropertyToID("_EdgeDepthBiasItem");
        private static readonly int PROP_EdgeDepthBiasBody = Shader.PropertyToID("_EdgeDepthBiasBody");
        private static readonly int PROP_MaskBodyFlag      = Shader.PropertyToID("_MaskBodyFlag");
        private static readonly int PROP_OutlineAlpha   = Shader.PropertyToID("_OutlineAlpha");
        private static readonly int PROP_OutlineFade    = Shader.PropertyToID("_OutlineFade");
        private static readonly int PROP_MaskFadeOnly   = Shader.PropertyToID("_MaskFadeOnly");

        // Amortized draw-list builder: the rebuild is spread across frames with a
        // state machine (bounded slice per frame into scratch lists, swapped into
        // the live lists at Finalize so the CB replay never sees a half-built list).
        private enum BuildPhase { Idle, Items, Containers, Bodies, Finalize }
        private BuildPhase _buildPhase = BuildPhase.Idle;
        private int   _buildCursor;
        private float _lastPassStartTime = -999f;
        private const float PassIntervalSeconds = 0.25f;
        // Set by the cache coroutine when it finishes building entries a pass
        // requested; lets the next pass start as soon as the queue drains instead
        // of waiting out the full interval (new loot appears without the lag).
        // The floor keeps early passes from running back-to-back while walking
        // through dense loot (each pass discovers more, builds, retriggers).
        private bool _cacheBuiltSincePass;
        private const float EarlyPassMinSeconds = 0.1f;
        // Per-frame slice sizes; items dominate scene-wide so they get the most.
        private const int ItemsPerFrame      = 192;
        private const int ContainersPerFrame = 48;
        private const int BodiesPerFrame     = 24;
        // Snapshot held for the duration of one multi-frame pass.
        private Vector3 _passPlayerPos, _passCamPos;
        private float   _passRange, _passPrefilterSqr, _passInteractHideSqr;
        private bool    _passDiag;
        private int     _diagInRange, _diagOwned, _diagQueued;
        private readonly List<Player> _seenSnapshot = new List<Player>();
        private readonly List<DrawObject> _drawObjectsScratch = new List<DrawObject>();
        // CB rebuild throttle (~30Hz). The attached CB replays every frame anyway;
        // rebuilding is the CPU-heavy part (bounds, frustum tests, recording).
        // Time-based so low-FPS machines still rebuild every frame; _drawListDirty
        // forces a rebuild the moment a pass publishes.
        private float _lastRebuildTime = -999f;
        private bool  _drawListDirty;
        private const float RebuildMinInterval = 1f / 30f;

        private const int RootExpansionRendererCap = 32;

        // Items with a largest axis below this (meters) are never outlined -
        // filters shell casings and similar debris. Fixed on purpose.
        private const float MinItemSize = 0.05f;

        // Held-weapon exclusion: the first-person weapon's LootItem hierarchy never
        // reaches the player transform, so anything this close to the eye camera
        // is treated as held. Shelf/counter items sit 1.5m+ away when lootable.
        private const float HeldItemExclusionRadius = 1.2f;

        private readonly Plane[] _frustumPlanes = new Plane[6];

        // Draw-object indices that survived culling this rebuild. Culling runs
        // before CB recording so an all-culled frame skips the mask RT + blit
        private readonly List<int> _visibleIdxScratch = new List<int>();

        // Pooled scratch, reused to avoid per-frame/per-tick GC
        private Matrix4x4[] _matrixBuffer = new Matrix4x4[16];
        private readonly HashSet<Transform> _playerTransforms = new HashSet<Transform>();
        private readonly HashSet<string>    _equippedItemIds  = new HashSet<string>();
        private readonly List<int>          _staleIds         = new List<int>();

        // Nearest-N cap sort (delegate cached in Awake to avoid per-tick alloc)
        private Vector3 _drawSortOrigin;
        private Comparison<DrawObject> _drawSortCmp;
        

        // Wake up, gordon freeman
        private void Awake()
        {
            _cacheCoroutine = StartCoroutine(CacheBuilderLoop());

            _drawSortCmp = (a, b) =>
                (a.Bounds.center - _drawSortOrigin).sqrMagnitude
                .CompareTo((b.Bounds.center - _drawSortOrigin).sqrMagnitude);

            // Mask+edge preferred; Plugin already dropped shaders that failed isSupported, so these flags are authoritative
            _useMaskEdge =
                Plugin.MaskShader != null &&
                Plugin.EdgeShader != null;

            _useThreePass = !_useMaskEdge &&
                Plugin.StencilShader != null &&
                Plugin.DrawShader    != null &&
                Plugin.ClearShader   != null;

            if (_useMaskEdge)
            {
                _maskMat = new Material(Plugin.MaskShader) { hideFlags = HideFlags.HideAndDontSave };
                _edgeMat = new Material(Plugin.EdgeShader) { hideFlags = HideFlags.HideAndDontSave };
                _maskRtId = Shader.PropertyToID("_LootOutlineMask");
                _fadeRtId = Shader.PropertyToID("_LootOutlineFade");

                _useShader = true;
                _cmd = new CommandBuffer { name = "LootOutline" };
                _mpb = new MaterialPropertyBlock();

                WarmupShaderPasses(_maskMat);
                WarmupShaderPasses(_edgeMat);
            }
            else if (_useThreePass)
            {
                _itemStencilMat = new Material(Plugin.StencilShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
                _itemDrawMat    = new Material(Plugin.DrawShader)    { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3050 };
                _itemClearMat   = new Material(Plugin.ClearShader)   { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3100 };
                _contStencilMat = new Material(Plugin.StencilShader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
                _contDrawMat    = new Material(Plugin.DrawShader)    { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3050 };
                _contClearMat   = new Material(Plugin.ClearShader)   { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3100 };
                _bodyDrawMat    = new Material(Plugin.DrawShader)    { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3050 };

                // Stencil must be a pure depth-independent silhouette projection,
                // otherwise self-occluded faces leave gaps the draw pass bleeds through as jagged lines
                int zAlways = (int)CompareFunction.Always;
                foreach (var m in new[] { _itemStencilMat, _contStencilMat,
                                          _itemClearMat,   _contClearMat })
                {
                    m.SetInt("_ZTest",  zAlways);
                    m.SetInt("_ZWrite", 0);
                }

                // Occlusion is per-pixel vs _CameraDepthTexture in the shader
                // (collider-independent, works on colliderless walls); the hardware
                // z-buffer is unreliable at AfterForwardAlpha, so ZTest stays Always.
                // Occlude mode + body bias are re-applied per rebuild from config.
                _itemDrawMat.SetInt("_ZTest", zAlways);
                _contDrawMat.SetInt("_ZTest", zAlways);
                _bodyDrawMat.SetInt("_ZTest", zAlways);
                _itemDrawMat.SetFloat("_DepthOcclude", 1f);
                _contDrawMat.SetFloat("_DepthOcclude", 1f);
                _bodyDrawMat.SetFloat("_DepthOcclude", 1f);
                _bodyDrawMat.SetFloat("_DepthBias", Plugin.BodyDepthBias.Value);

                _useShader = true;
                _cmd = new CommandBuffer { name = "LootOutline" };
                _mpb = new MaterialPropertyBlock();

                WarmupShaderPasses(_itemStencilMat);
                WarmupShaderPasses(_itemDrawMat);
                WarmupShaderPasses(_itemClearMat);
                WarmupShaderPasses(_bodyDrawMat);
            }
            else if (Plugin.OutlineShader != null)
            {
                _itemMat = new Material(Plugin.OutlineShader) { hideFlags = HideFlags.HideAndDontSave };
                _contMat = new Material(Plugin.OutlineShader) { hideFlags = HideFlags.HideAndDontSave };
                _useShader = true;
                WarmupShaderPasses(_itemMat);
                WarmupShaderPasses(_contMat);
            }
        }

        private static void WarmupShaderPasses(Material mat)
        {
            if (mat == null || mat.shader == null) return;
            int passes = mat.shader.passCount;
            for (int p = 0; p < passes; p++)
            {
                try { mat.SetPass(p); } catch { }
            }
        }

        private void Update()
        {
            if (!Plugin.Enabled.Value)
            {
                DetachCommandBuffer();
                return;
            }

            _mainCam = ResolveFpsCamera();

            if (_useShader) EnsureCommandBufferAttached();

            // Rebuild the CB from the current draw list, throttled to ~30Hz.
            // The attached CB keeps replaying the last build in between.
            if (_useMaskEdge)
            {
                float nowRb = Time.realtimeSinceStartup;
                if (_drawListDirty || nowRb - _lastRebuildTime >= RebuildMinInterval)
                {
                    RebuildMaskEdgeCommandBuffer();
                    _lastRebuildTime = nowRb;
                    _drawListDirty   = false;
                }
            }
            else if (_useThreePass)
            {
                float nowRb = Time.realtimeSinceStartup;
                if (_drawListDirty || nowRb - _lastRebuildTime >= RebuildMinInterval)
                {
                    RebuildThreePassCommandBuffer();
                    _lastRebuildTime = nowRb;
                    _drawListDirty   = false;
                }
            }
            else if (_useShader) SubmitLegacyDraws();

            // Advance the amortized draw-list build by one slice.
            StepDrawListBuilder();
        }

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
                    // Cadence measured from the previous pass START, so a long pass
                    // rolls straight into the next. A drained cache queue with fresh
                    // builds short-circuits the interval - newly cached objects get
                    // drawn on the very next pass instead of waiting it out.
                    float sincePass = Time.realtimeSinceStartup - _lastPassStartTime;
                    if (sincePass >= PassIntervalSeconds ||
                        (sincePass >= EarlyPassMinSeconds &&
                         _cacheBuiltSincePass && _cacheQueue.Count == 0))
                        BeginPass(gameWorld);
                    break;
                case BuildPhase.Items:      StepItemsPhase();      break;
                case BuildPhase.Containers: StepContainersPhase(); break;
                case BuildPhase.Bodies:     StepBodiesPhase();     break;
                case BuildPhase.Finalize:   FinalizePass();        break;
            }
        }

        // First snapshot everything the pass needs so the sliced phases stay consistent while we move
        private void BeginPass(GameWorld gameWorld)
        {
            _lastPassStartTime = Time.realtimeSinceStartup;
            _cacheBuiltSincePass = false;

            _passPlayerPos = gameWorld.MainPlayer.Transform.position;
            _passRange     = Plugin.DetectionRange.Value;
            float prefilter = _passRange + 2f;
            _passPrefilterSqr = prefilter * prefilter;
            float interactHide = Plugin.InteractHideDistance.Value;
            _passInteractHideSqr = interactHide * interactHide;

            // camPos drives only the held-item exclusion (never ownership - the camera sits ~1.7m above the feet and would flag shelf loot)
            _passCamPos = _attachedCam != null
                ? _attachedCam.transform.position
                : (_mainCam != null ? _mainCam.transform.position : _passPlayerPos + Vector3.up * 1.7f);

            // Drop destroyed Player refs (despawned corpses) so the seen set
            // doesn't accumulate across a long raid
            _seenPlayers.RemoveWhere(_destroyedPlayer);

            // Fresh accumulation for this pass
            _playerTransforms.Clear();
            _equippedItemIds.Clear();
            _activeThisTick.Clear();
            _drawObjectsScratch.Clear();

            // Deep equipped-ID walk only for the local player (nested weapon mods
            // can appear as separate LootItems in the world view), TTL-cached.
            // Bots only need the top-level pass in AddPlayerToScratch
            if (gameWorld.MainPlayer is MonoBehaviour mpMb)
                _playerTransforms.Add(mpMb.transform);
            if (Time.realtimeSinceStartup - _mainEquipBuiltAt >= MainEquipRefreshSeconds)
            {
                _mainEquipIds     = CollectEquippedIds(gameWorld.MainPlayer);
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
                    // SPT drops some bots from RegisteredPlayers after death;
                    // Keep the reference so the corpse stays outlinable
                    if (p is Player pl) _seenPlayers.Add(pl);
                }

            // Dead-body positions, snapshotted before the loose-item phase so
            // weapons that fell with the ragdoll can be suppressed
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

                // Collect the corpse's equipped item IDs (TTL-cached) so its
                // still-holstered gear isn't outlined as loose loot - dead bots
                // leave RegisteredPlayers, so the ID pass above misses them
                if ((corpsePos - _passPlayerPos).sqrMagnitude <= corpseEquipRangeSqr)
                {
                    // MonoBehaviour transform (not the BifacialTransform) for the
                    // parent-chain ownership check.
                    _playerTransforms.Add(((MonoBehaviour)dp).transform);

                    int cid = dp.gameObject.GetInstanceID();
                    _activeThisTick.Add(cid); // keep the cache warm + prune-safe
                    if (!_corpseEquipCache.TryGetValue(cid, out var ce) ||
                        Time.realtimeSinceStartup - ce.BuiltAt >= CorpseEquipRefreshSeconds)
                    {
                        ce = new CorpseEquip
                        {
                            Ids     = CollectEquippedIds(dp),
                            BuiltAt = Time.realtimeSinceStartup,
                        };
                        _corpseEquipCache[cid] = ce;
                    }
                    var ids = ce.Ids;
                    for (int i = 0; i < ids.Length; i++) _equippedItemIds.Add(ids[i]);
                }
            }

            // Indexable copy so the bodies phase can slice it
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
                    // Registry empty (very early raid?) - throttled full-scene scan
                    float nowR = Time.realtimeSinceStartup;
                    if (nowR - _lootItemCacheBuildTime >= LootItemCacheRefreshSeconds)
                    {
                        _lootItemCache = FindObjectsOfType<LootItem>();
                        _lootItemCacheBuildTime = nowR;
                    }
                    _lootItemsSnapshot.AddRange(_lootItemCache);
                }

                // Diagnostic summary cadence (Debug Logging only).
                if (Plugin.DebugLogging.Value &&
                    Time.realtimeSinceStartup - _lastLootDiagTime >= LootItemCacheRefreshSeconds)
                {
                    _passDiag = true;
                    _lastLootDiagTime = Time.realtimeSinceStartup;
                }
            }

            // Container cache: fast retry while empty, then a slow re-scan that
            // only ever grows the snapshot (a scan taken while EFT has more rooms culled would otherwise shrink it)
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
            _buildPhase  = BuildPhase.Items;
        }

        // Phase 1 - loose loot, sliced ItemsPerFrame at a time.
        private void StepItemsPhase()
        {
            if (!Plugin.OutlineLooseItems.Value || _lootItemsSnapshot.Count == 0)
            {
                _buildCursor = 0;
                _buildPhase  = BuildPhase.Containers;
                return;
            }
        
            int end = Mathf.Min(_buildCursor + ItemsPerFrame, _lootItemsSnapshot.Count);
            for (int i = _buildCursor; i < end; i++)
            {
                var li = _lootItemsSnapshot[i];
                if (li == null || li.gameObject == null) continue;
                
                // Skip items that are not active or have no active renderers
                if (!li.gameObject.activeInHierarchy)
                {
                    _activeThisTick.Add(li.gameObject.GetInstanceID());
                    continue;
                }
        
                // Check if there are any active renderers, if none, it's not spawned yet
                var renderers = li.gameObject.GetComponentsInChildren<Renderer>(false);
                if (renderers.Length == 0)
                {
                    _activeThisTick.Add(li.gameObject.GetInstanceID());
                    continue;
                }
        
                // deprecate
                float dSqr = (li.transform.position - _passPlayerPos).sqrMagnitude;
                if (dSqr > _passPrefilterSqr) continue;
                if (_passInteractHideSqr > 0f && dSqr < _passInteractHideSqr)
                {
                    _activeThisTick.Add(li.gameObject.GetInstanceID());
                    continue;
                }
                _diagInRange++;
                if (!HasResolvableItem(li)) continue;
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
                GatherTarget(li.gameObject, _passPlayerPos, _passCamPos, _passRange,
                             applyHeldExclusion: true,
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
                _buildPhase  = BuildPhase.Containers;
            }
        }

        // Phase 2 - containers, sliced. Distance prefilter runs before ContainerHasLoot so a far container never pays the GetAllItems walk
        private void StepContainersPhase()
        {
            if (!Plugin.OutlineContainers.Value || _containerCache.Length == 0)
            {
                _buildCursor = 0;
                _buildPhase  = BuildPhase.Bodies;
                return;
            }

            int end = Mathf.Min(_buildCursor + ContainersPerFrame, _containerCache.Length);
            for (int i = _buildCursor; i < end; i++)
            {
                var c = _containerCache[i];
                if (c == null || c.gameObject == null) continue;
                // NO activeInHierarchy skip: EFT's baked room culling deactivates
                // prop GOs by player position (conservative around fences/grates),
                // but we draw cached meshes ourselves - a culled container keeps its outline (bounds fall back to the cached AABB)
                if ((c.transform.position - _passPlayerPos).sqrMagnitude > _passPrefilterSqr) continue;
                // Skip container spawn points with no loot. EFT can have an
                // initialized ItemOwner with an empty grid, so a null check isn't
                // sufficient - we need at least one item beyond the container's root
                if (!ContainerHasLoot(c)) continue;
                if (_playerTransforms.Contains(c.transform.root)) continue;
                GatherTarget(c.gameObject, _passPlayerPos, _passCamPos, _passRange,
                             applyHeldExclusion: false,
                             expandToPrefabRoot: true,
                             isContainer: true);
            }

            _buildCursor = end;
            if (_buildCursor >= _containerCache.Length)
            {
                _buildCursor = 0;
                _buildPhase  = BuildPhase.Bodies;
            }
        }

        // Phase 4 - cap, atomically publish the scratch list, prune, flag dirty
        private void FinalizePass()
        {
            // Nearest-N cap (0 = unlimited). _activeThisTick is not trimmed so
            // capped-out objects keep their cache warm
            int maxOutlines = Plugin.MaxOutlinedObjects.Value;
            if (maxOutlines > 0 && _drawObjectsScratch.Count > maxOutlines)
            {
                _drawSortOrigin = _passPlayerPos;
                _drawObjectsScratch.Sort(_drawSortCmp);
                _drawObjectsScratch.RemoveRange(maxOutlines, _drawObjectsScratch.Count - maxOutlines);
            }

            // Atomic publish: the per-frame CB replay reads _drawObjects, so it only
            // ever sees a fully-built pass, never a half-populated slice
            _drawObjects.Clear();
            _drawObjects.AddRange(_drawObjectsScratch);
            PruneStaleCaches();
            _activeThisTick.Clear();

            // Immediate CB rebuild so changes don't wait on the 30Hz throttle
            _drawListDirty = true;
            _buildPhase = BuildPhase.Idle;
        }

        // Drop cache entries for objects this pass didn't touch (left range / gone).
        // A fresh spawn reusing a recycled instance ID then gets a real attempt instead of an instant skip
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

            // Retry-backoff and corpse equip caches: same lifetime rule, so the dictionaries don't accumulate across a raid
            _staleIds.Clear();
            foreach (var id in _failRetryAt.Keys)
                if (!_activeThisTick.Contains(id)) _staleIds.Add(id);
            foreach (var id in _staleIds) _failRetryAt.Remove(id);

            _staleIds.Clear();
            foreach (var id in _corpseEquipCache.Keys)
                if (!_activeThisTick.Contains(id)) _staleIds.Add(id);
            foreach (var id in _staleIds) _corpseEquipCache.Remove(id);
        }

        // Full reset when the GameWorld (raid) goes away. Also re-arms the builder state machine so the next raid starts a clean pass
        private void ResetWorldState()
        {
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
            _containerCache         = Array.Empty<LootableContainer>();
            _containerCacheTryTime  = -999f;
            _containerLootCache.Clear();
            _lootItemCache          = Array.Empty<LootItem>();
            _lootItemCacheBuildTime = -999f;
            _lootItemsSnapshot.Clear();
            _mainEquipIds     = Array.Empty<string>();
            _mainEquipBuiltAt = -999f;
            _buildPhase  = BuildPhase.Idle;
            _buildCursor = 0;
        }

        // Target gathering

        private void GatherTarget(GameObject go, Vector3 playerPos, Vector3 camPos,
                                  float range, bool applyHeldExclusion,
                                  bool expandToPrefabRoot, bool isContainer,
                                  bool bodyOnly = false,
                                  Vector3? worldPosOverride = null)
        {
            // Mark active BEFORE the range/held early-outs: everything inside the
            // prefilter buffer keeps its cache warm, so an object hovering at the
            // detection-range edge (or excluded as held) isn't pruned and
            // async-rebuilt on every pass.
            int id = go.GetInstanceID();
            _activeThisTick.Add(id);

            // For dead bodies, go.transform.position is the player MonoBehaviour
            // transform - pinned at world origin in EFT, so the range check would
            // always fail. Caller supplies Player.Transform.position via the
            // override.
            Vector3 worldPos = worldPosOverride ?? go.transform.position;
            if (Vector3.Distance(playerPos, worldPos) > range) return;
            if (applyHeldExclusion &&
                Vector3.Distance(camPos, worldPos) < HeldItemExclusionRadius)
                return;

            if (!_rendererCache.TryGetValue(id, out var cached))
            {
                // Cache miss - enqueue for async build (appears within a few
                // frames). Recently-failed builds wait out their retry backoff
                // so transient failures (ragdoll init, room culling) self-heal.
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

            // Size filter for loose items only - filters ejected shell casings and
            // similar tiny debris. Containers and dead bodies always show.
            if (!isContainer)
            {
                Vector3 s = cached.Bounds.size;
                float largest = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                if (largest < MinItemSize) return;
            }

            // Append to the scratch lists; published atomically at FinalizePass.
            float fade = Mathf.Clamp01(1f - Vector3.Distance(playerPos, worldPos)
                                       / Mathf.Max(range, 0.001f));
            _drawObjectsScratch.Add(new DrawObject(cached.Entries, isContainer, cached.Bounds,
                                                   fade, bodyOnly));
        }

        private static RendererData CollectRenderers(GameObject go, bool expandToPrefabRoot, bool bodyOnly = false)
        {
            var entries = new List<MeshEntry>();
            Bounds combined = default;
            bool   hasBounds = false;

            // includeInactive must be FALSE for containers: many container prefabs
            // hold several visual variants with only one active per raid, and the
            // inactive ones would produce ghost outlines / stretched bounds.
            // Loose items need TRUE (EFT often authors the visible mesh under an
            // inactive child), and so do bodies (EFT's character LOD disables the
            // high-detail SMRs at distance; the per-frame enabled guard in the
            // draw loops picks whichever LOD is live).
            bool includeInactive = bodyOnly || !expandToPrefabRoot;
            var selected = go.GetComponentsInChildren<Renderer>(includeInactive);

            // Restrict LODGroup-owned renderers to the LOD0 slot so the combined
            // bounds don't jump when Unity swaps LOD levels. Bodies skip this:
            // EFT swaps corpse SMRs between LODs with distance, so every LOD is
            // cached and the per-frame enabled guard picks the live one.
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
                    // Keep any renderers NOT under a LODGroup (they're stable),
                    // plus the LOD0 renderers from any LODGroup we found.
                    var filtered = new List<Renderer>(selected.Length);
                    foreach (var r in selected)
                    {
                        if (r == null) continue;
                        if (lod0Set.Contains(r)) { filtered.Add(r); continue; }
                        // Is this renderer in ANY LOD level of any group?
                        // If so it's a non-LOD0 slot - skip it. Otherwise (no group owns it), keep it
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

            // Many containers keep their visible mesh as a SIBLING of the
            // LootableContainer trigger, so walk up a couple of prefab-root-ish
            // levels. Child-count/renderer caps keep the walk away from huge
            // scene-grouping nodes; a parent is only accepted if it actually
            // contributes additional renderers. NEVER expand from an inactive
            // root: it contributes 0 renderers itself, so the walk would cache
            // its SIBLINGS' meshes - a ghost outline around neighbouring props
            if (expandToPrefabRoot && !bodyOnly && go.activeInHierarchy)
            {
                const int MaxParentChildren    = 12;
                const int MaxWalkUpLevels      = 2;
                Transform p = go.transform.parent;
                for (int w = 0; w < MaxWalkUpLevels && p != null; w++)
                {
                    // Immediate parent is always tried (the mesh commonly lives
                    // one level up); deeper levels get the child-count guard
                    if (w > 0 && p.childCount > MaxParentChildren) break;
                    // includeInactive=false: don't grab variant subtrees that didn't spawn this raid
                    var expanded = p.GetComponentsInChildren<Renderer>(false);
                    if (expanded.Length <= selected.Length) break;
                    if (expanded.Length > RootExpansionRendererCap) break;
                    selected = expanded;
                    p = p.parent;
                }
            }

            // One-shot renderer dump for the first few corpses (Debug Logging only).
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
                    // Body path: reject worn gear so the corpse outlines as a clean body silhouette
                    if (bodyOnly)
                    {
                        if (IsWornEquipment(smr.transform)) continue;
                        // Force per-frame bounds recompute - a settled ragdoll's
                        // skinned bounds go stale otherwise and break the frustum
                        // test. Reset in ReleaseCorpseSmrs when the entry is pruned
                        smr.updateWhenOffscreen = true;
                    }
                    Bounds b = r.bounds;
                    if (hasBounds) combined.Encapsulate(b); else { combined = b; hasBounds = true; }
                    entries.Add(new MeshEntry(smr));
                }
                else
                {
                    // Body path: skip non-skinned renderers (weapon/helmet)
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

        // Builds a short "grandparent/parent/self" path for diagnostic logging
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
            // Equipment SMRs sit a few levels under an "item_equipment_*" (or legacy "Slot_*") root; walk up with a small cap.
            for (int i = 0; i < 8 && t != null; i++, t = t.parent)
            {
                if (t.name == null) continue;
                if (t.name.StartsWith("item_equipment_", StringComparison.Ordinal) ||
                    t.name.StartsWith("Slot_", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // Populates the pooled _playerTransforms / _equippedItemIds for one player.
        // Typed as `object` to avoid referencing IPlayer directly (drags in the
        // DissonanceVoip assembly); every IPlayer is a Player at runtime.
        // Top-level equipment IDs only - the deep nested-mod walk is only needed
        // for the local player and corpses, via the TTL-cached CollectEquippedIds.
        private void AddPlayerToScratch(object obj)
        {
            if (obj == null) return;
            var mb = obj as MonoBehaviour;
            if (mb != null)
            {
                // NOT transform.root - in EFT that's a scene-level parent shared
                // with every LootItem, which would suppress all loose loot.
                _playerTransforms.Add(mb.transform);
            }
            var p = obj as Player;
            if (p == null) return;

            try
            {
                var profile = p.Profile;
                var inv = profile?.Inventory;
                if (inv == null) return;
                foreach (var top in inv.GetPlayerItems(EPlayerItems.Equipment))
                {
                    if (top != null && top.Id != null) _equippedItemIds.Add(top.Id);
                }
            }
            catch { /* defensive - transient Profile/Inventory null during spawn/respawn */ }
        }

        // Deep-collects a player's equipped item IDs (top-level gear + every
        // nested child) into a fresh array, so callers can cache the result.
        private string[] CollectEquippedIds(Player p)
        {
            _equipScratch.Clear();
            try
            {
                var inv = p?.Profile?.Inventory;
                if (inv != null)
                {
                    foreach (var top in inv.GetPlayerItems(EPlayerItems.Equipment))
                    {
                        if (top == null) continue;
                        if (top.Id != null) _equipScratch.Add(top.Id);
                        try
                        {
                            foreach (var child in top.GetAllItems())
                                if (child != null && child.Id != null)
                                    _equipScratch.Add(child.Id);
                        }
                        catch { /* item type may not expose GetAllItems */ }
                    }
                }
            }
            catch { /* transient Profile/Inventory null during spawn/respawn */ }
            return _equipScratch.Count > 0 ? _equipScratch.ToArray() : Array.Empty<string>();
        }

        // True when a position is close enough to a dead body that the item
        // likely fell with the ragdoll (suppressed so it doesn't overlap the
        // body outline).
        private static bool IsOnDeadBody(Vector3 pos, List<Vector3> deadBodyPositions)
        {
            foreach (var bp in deadBodyPositions)
                if ((pos - bp).sqrMagnitude < BodyWeaponSuppressRadiusSqr) return true;
            return false;
        }

        // Debug log: one line per unique item name that will be outlined.
        private void LogQueuedDiag(bool diag, LootItem li)
        {
            if (!diag || li == null || li.gameObject == null) return;
            string n = li.gameObject.name;
            if (!_loggedQueued.Add(n)) return;
            string id = "?";
            bool   has = false;
            try
            {
                var it = li.Item;
                if (it != null)
                {
                    id  = it.Id ?? "null";
                    has = it.Id != null && _equippedItemIds.Contains(it.Id);
                }
            }
            catch { id = "<throw>"; }
            Plugin.LogSource?.LogInfo(
                $"[BreakoutOutline] QUEUED '{n}' id={id} equippedHas={has} equippedCount={_equippedItemIds.Count}");
        }

        // A LootItem whose backing Item has no resolvable Id is an in-hand weapon
        // (or one of its mod parts), not droppable world loot - floor loot always
        // resolves a non-null Item.Id. Skipping these filters bot-held guns and
        // per-mod duplicates of loose weapons.
        private static bool HasResolvableItem(LootItem li)
        {
            try { var it = li.Item; return it != null && it.Id != null; }
            catch { return false; }
        }

        private static bool IsOwnedByAnyPlayer(LootItem li,
                                               HashSet<Transform> playerTransforms,
                                               HashSet<string> equippedItemIds)
        {
            // 1. Item-id match against every player's equipped inventory.
            try
            {
                var item = li.Item;
                if (item != null && item.Id != null && equippedItemIds.Contains(item.Id))
                    return true;
            }
            catch { /* LootItem.Item can throw on some transient states */ }

            var go = li.gameObject;

            // 2. Parent chain - catches items still parented under a player /
            //    dead-body skeleton, e.g. rigid attachments.
            var t = go.transform;
            while (t != null)
            {
                if (playerTransforms.Contains(t)) return true;
                t = t.parent;
            }

            return false;
        }

        // Compound-item detection via "Slots"/"Grids" properties (avoids a direct
        // CompoundItem reference); cached per Type.
        private static readonly Dictionary<Type, bool> _isCompoundTypeCache = new Dictionary<Type, bool>();
        private static bool IsCompoundItemType(Type t)
        {
            if (_isCompoundTypeCache.TryGetValue(t, out bool cached)) return cached;
            bool isCompound = t.GetProperty("Slots") != null
                           || t.GetProperty("Grids") != null;
            _isCompoundTypeCache[t] = isCompound;
            return isCompound;
        }

        private bool ContainerHasLoot(LootableContainer c)
        {
            int id = c.gameObject.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            if (_containerLootCache.TryGetValue(id, out var cl) &&
                now - cl.At < ContainerLootTtlSeconds)
                return cl.Has;

            bool has = ComputeContainerHasLoot(c);
            _containerLootCache[id] = new ContainerLoot { Has = has, At = now };
            return has;
        }

        private static bool ComputeContainerHasLoot(LootableContainer c)
        {
            try
            {
                var owner = c.ItemOwner;
                if (owner == null) return false;
                var root = owner.RootItem;
                if (root == null) return false;

                // Only NON-compound items count as real loot: compound sub-items
                // (grid wrappers, a PC's internal case slot) are structural and
                // present even in empty containers, while populated containers
                // always have at least one non-compound leaf.
                foreach (var item in root.GetAllItems())
                {
                    if (ReferenceEquals(item, root)) continue;
                    if (IsCompoundItemType(item.GetType())) continue;
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        // Mask + edge CB build: render every culled-in object's silhouette into a
        // screen-sized mask RT (colour in RGB, signed eye-depth in A), then one
        // fullscreen edge pass composites the outline rings over the camera colour.
        private void RebuildMaskEdgeCommandBuffer()
        {
            if (_cmd == null) return;
            _cmd.Clear();

            var cam = _attachedCam;
            if (cam == null) return;

            // "Line of Sight Check" toggles per-pixel depth occlusion and gates the depth prepass.
            // Applied before the empty-list early-out so switching LOS off releases the forced prepass even with nothing outlined.
            bool losOn = Plugin.LineOfSightCheck.Value;
            ApplyDepthMode(cam, losOn);
            if (_drawObjects.Count == 0) return;
            float occl = losOn ? 1f : 0f;

            Vector3 camPos = cam.transform.position;
            Vector3 camFwd = cam.transform.forward;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            Color itemColor = Plugin.ItemOutlineColor.Value;
            Color contColor = Plugin.ContainerOutlineColor.Value;
            float bodyBias  = Plugin.BodyDepthBias.Value;

            // Cull first
            // If nothing survives (all outlined objects behind the camera / out of
            // frustum - very common while sweeping a room), the CB stays empty and
            // the whole mask pipeline (fullscreen RT + clear + edge blit) is skipped
            _visibleIdxScratch.Clear();
            int count = _drawObjects.Count;
            for (int oi = 0; oi < count; oi++)
            {
                var d = _drawObjects[oi];

                // Deliberately no Renderer.isVisible gate - it reads the cached
                // LOD0 renderer and EFT's occlusion culling, both of which can go
                // false while the object is plainly on screen (see the note above
                // ComputeLiveBounds)

                // Back-cull on the stable CACHED centre (live centre drifts as EFT
                // LOD disables body SMRs)
                float fwdDist = Vector3.Dot(camFwd, d.Bounds.center - camPos);
                if (fwdDist < 0f) continue;

                // Live bounds when a cached renderer is alive; cached bounds
                // otherwise - EFT LOD/culling can disable every cached renderer
                // while the object is still visible via another LOD
                if (!ComputeLiveBounds(d.Entries, out var liveBounds)) liveBounds = d.Bounds;
                if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, liveBounds)) continue;

                _visibleIdxScratch.Add(oi);
            }
            if (_visibleIdxScratch.Count == 0) return;

            // Mask RT: RGB = colour, A = signed eye-depth doubling as the filled
            // marker. No depth buffer - occlusion is per-pixel in the shader
            int w = Mathf.Max(1, cam.pixelWidth);
            int h = Mathf.Max(1, cam.pixelHeight);
            _cmd.GetTemporaryRT(_maskRtId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            _cmd.GetTemporaryRT(_fadeRtId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.RHalf);
            _cmd.SetRenderTarget(_maskRtId);
            _cmd.ClearRenderTarget(false, true, Color.clear);

            int visCount = _visibleIdxScratch.Count;
            for (int vi = 0; vi < visCount; vi++)
            {
                var d = _drawObjects[_visibleIdxScratch[vi]];

                Color color = d.IsContainer ? contColor : itemColor; // bodies = container colour
                float bias  = d.IsBody ? bodyBias : ItemDepthBias;

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

                // Static meshes - DrawMesh + MPB (per-object colour/occlusion)
                _mpb.Clear();
                _mpb.SetColor(PROP_OutlineColor, color);
                _mpb.SetFloat(PROP_DepthOcclude, occl);
                _mpb.SetFloat(PROP_DepthBias,    bias);
                _mpb.SetFloat(PROP_MaskBodyFlag, d.IsBody ? 1f : 0f);
                _mpb.SetFloat(PROP_OutlineFade,  d.Fade);
                _mpb.SetFloat(PROP_MaskFadeOnly, 0f);
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    int sc = e.Mesh.subMeshCount;
                    for (int s = 0; s < sc; s++)
                        _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], _maskMat, s, 0, _mpb);
                }

                // Skinned bodies - DrawRenderer has no MPB overload, so the per-object
                // colour/occlusion go through CommandBuffer globals (executed in CB order; the next object's globals overwrite ours before its draws)
                bool anySmr = false;
                for (int i = 0; i < n; i++) { if (d.Entries[i].Smr != null) { anySmr = true; break; } }
                if (anySmr)
                {
                    _cmd.SetGlobalColor(PROP_OutlineColor, color);
                    _cmd.SetGlobalFloat(PROP_DepthOcclude, occl);
                    _cmd.SetGlobalFloat(PROP_DepthBias,    bias);
                    _cmd.SetGlobalFloat(PROP_MaskBodyFlag, d.IsBody ? 1f : 0f);
                    _cmd.SetGlobalFloat(PROP_OutlineFade,  d.Fade);
                    _cmd.SetGlobalFloat(PROP_MaskFadeOnly, 0f);
                    for (int i = 0; i < n; i++)
                    {
                        var e = d.Entries[i];
                        if (e.Smr == null) continue;
                        var smr = e.Smr;
                        // Skip LOD-swapped-out SMRs; drawing a disabled renderer via CommandBuffer throws "vertex stride mismatch"
                        if (!smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                        var mesh = smr.sharedMesh;
                        if (mesh == null) continue;
                        int subCount = mesh.subMeshCount;
                        for (int s = 0; s < subCount; s++)
                            _cmd.DrawRenderer(smr, _maskMat, s, 0);
                    }
                }
            }

            // The mask alpha is reserved for signed eye-depth, so record the per-object distance fade in a companion scalar render target
            _cmd.SetRenderTarget(_fadeRtId);
            _cmd.ClearRenderTarget(false, true, Color.clear);
            RecordFadeMaskPass(occl);

            // Fullscreen edge pass. Ring opacity is global (the mask A holds depth, not per-object alpha).
            // Ring-occlusion tolerance is per type via the sign of the mask depth: tight for items/containers, loose for bodies
            // so prone-corpse rings survive the floor they lie on
            float widthPx = Plugin.OutlineWidth.Value;
            _edgeMat.SetFloat(PROP_OutlineWidthPx, widthPx);
            _edgeMat.SetFloat(PROP_EdgeOcclude,    occl);
            _edgeMat.SetFloat(PROP_EdgeDepthBiasItem, EdgeItemDepthBias);
            _edgeMat.SetFloat(PROP_EdgeDepthBiasBody, bodyBias);
            _edgeMat.SetFloat(PROP_OutlineAlpha,   Mathf.Max(itemColor.a, contColor.a));
            _cmd.SetGlobalTexture("_FadeTex", _fadeRtId);

            _cmd.Blit(_maskRtId, BuiltinRenderTextureType.CameraTarget, _edgeMat);
            _cmd.ReleaseTemporaryRT(_maskRtId);
            _cmd.ReleaseTemporaryRT(_fadeRtId);
        }

        private void RecordFadeMaskPass(float occl)
        {
            for (int vi = 0; vi < _visibleIdxScratch.Count; vi++)
            {
                var d = _drawObjects[_visibleIdxScratch[vi]];
                int n = d.Entries.Length;
                if (_matrixBuffer.Length < n)
                    _matrixBuffer = new Matrix4x4[Mathf.Max(n, _matrixBuffer.Length * 2)];
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Smr == null)
                        _matrixBuffer[i] = e.Tx != null
                            ? e.Tx.localToWorldMatrix * e.LocalOffset
                            : e.FixedMatrix;
                }

                _mpb.Clear();
                _mpb.SetFloat(PROP_OutlineFade, d.Fade);
                _mpb.SetFloat(PROP_MaskFadeOnly, 1f);
                _mpb.SetFloat(PROP_DepthOcclude, occl);
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    for (int s = 0; s < e.Mesh.subMeshCount; s++)
                        _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], _maskMat, s, 0, _mpb);
                }

                bool anySmr = false;
                for (int i = 0; i < n; i++)
                    if (d.Entries[i].Smr != null) { anySmr = true; break; }
                if (!anySmr) continue;

                _cmd.SetGlobalFloat(PROP_OutlineFade, d.Fade);
                _cmd.SetGlobalFloat(PROP_MaskFadeOnly, 1f);
                _cmd.SetGlobalFloat(PROP_DepthOcclude, occl);
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

        // Three-pass CommandBuffer build (per frame)

        private void RebuildThreePassCommandBuffer()
        {
            if (_cmd == null) return;
            _cmd.Clear();
            // See the mask-edge path: depth mode follows the LOS toggle even when nothing is drawn
            ApplyDepthMode(_attachedCam, Plugin.LineOfSightCheck.Value);
            if (_drawObjects.Count == 0) return;

            // Convert outline width from screen pixels (config) to NDC units NDC vertical span is 2 units, so each pixel ≈ 2 / Screen.height NDC units
            float widthPx  = Plugin.OutlineWidth.Value;
            float widthNdc = (Screen.height > 0)
                ? widthPx * 2f / Screen.height
                : widthPx * 0.003f;

            Color itemColor = Plugin.ItemOutlineColor.Value;
            Color contColor = Plugin.ContainerOutlineColor.Value;

            // Must be _attachedCam (the camera the CB executes on) - Camera.main can return a wide-FOV environment camera
            var cam = _attachedCam;
            if (cam == null) return;
            Vector3 camPos = cam.transform.position;
            Vector3 camFwd = cam.transform.forward;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            // LOS toggle drives the shader's per-pixel depth occlusion; body bias is live-tunable config (prone-corpse floor contact tolerance)
            bool losOn = Plugin.LineOfSightCheck.Value;
            _itemDrawMat.SetFloat("_DepthOcclude", losOn ? 1f : 0f);
            _contDrawMat.SetFloat("_DepthOcclude", losOn ? 1f : 0f);
            _bodyDrawMat.SetFloat("_DepthOcclude", losOn ? 1f : 0f);
            _bodyDrawMat.SetFloat("_DepthBias", Plugin.BodyDepthBias.Value);

            int count = _drawObjects.Count;
            for (int oi = 0; oi < count; oi++)
            {
                var d = _drawObjects[oi];

                // Back-cull on the CACHED centre - the live centre drifts when EFT LOD disables some of a body's SMRs
                float fwdDist = Vector3.Dot(camFwd, d.Bounds.center - camPos);
                if (fwdDist < 0f) continue;

                // Cached-bounds fallback: see the mask-edge path
                if (!ComputeLiveBounds(d.Entries, out var liveBounds)) liveBounds = d.Bounds;

                if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, liveBounds))
                    continue;

                // Bodies share the container stencil/clear + colour but use their own draw material
                var stencilMat = d.IsContainer ? _contStencilMat : _itemStencilMat;
                var drawMat    = d.IsBody ? _bodyDrawMat
                                          : (d.IsContainer ? _contDrawMat : _itemDrawMat);
                var clearMat   = d.IsContainer ? _contClearMat   : _itemClearMat;
                var color      = d.IsContainer ? contColor       : itemColor;
                color.a *= d.Fade;

                // Resolve matrices for this object's mesh entries
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

                // Live AABB centre as _ObjectCenter - mesh pivots sit at model corners and would make the ring expansion uneven
                Vector3 centre = liveBounds.center;

                _mpb.Clear();
                _mpb.SetColor(PROP_OutlineColor, color);
                _mpb.SetFloat(PROP_OutlineWidth, widthNdc);
                _mpb.SetVector(PROP_ObjectCenter, new Vector4(centre.x, centre.y, centre.z, 1f));

                // Stencil pass - every submesh, or unmasked submesh edges bleed through the draw pass on multi-material objects
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    int sc = e.Mesh.subMeshCount;
                    for (int s = 0; s < sc; s++) _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], stencilMat, s, 0);
                }
                // Draw pass - outline ring around combined silhouette
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    int sc = e.Mesh.subMeshCount;
                    for (int s = 0; s < sc; s++) _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], drawMat, s, 0, _mpb);
                }
                // Clear pass - wipe the stencil so the next object starts fresh
                for (int i = 0; i < n; i++)
                {
                    var e = d.Entries[i];
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    int sc = e.Mesh.subMeshCount;
                    for (int s = 0; s < sc; s++) _cmd.DrawMesh(e.Mesh, _matrixBuffer[i], clearMat, s, 0);
                }

                // Skinned (dead bodies): DrawRenderer has no MPB overload, so the
                // per-object props go through CB globals written immediately before
                // this body's draws (globals execute in CB order; MPB still wins for
                // the static DrawMesh path). _ObjectCenter.w carries the bound
                // radius - a separate property would read its material default over
                // a per-body CB global
                bool anySmr = false;
                for (int i = 0; i < n; i++) { if (d.Entries[i].Smr != null) { anySmr = true; break; } }
                if (anySmr)
                {
                    _cmd.SetGlobalVector(PROP_ObjectCenter,
                        new Vector4(centre.x, centre.y, centre.z, liveBounds.extents.magnitude));
                    _cmd.SetGlobalColor (PROP_OutlineColor, color);
                    _cmd.SetGlobalFloat (PROP_OutlineWidth, widthNdc);

                    // Three separate loops (stencil-all - draw-all - clear-all): a
                    // per-SMR trio would draw ring edges between body parts
                    for (int i = 0; i < n; i++)
                    {
                        var e = d.Entries[i];
                        if (e.Smr == null) continue;
                        var smr = e.Smr;
                        // Skip SMRs EFT has swapped out (LOD change). Drawing a
                        // disabled renderer via CommandBuffer causes "vertex stride
                        // mismatch" errors and corrupt outlines on dead bodies
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
                while (_cacheQueue.Count > 0
                       && (Time.realtimeSinceStartup - start) < CacheBudgetSeconds)
                {
                    var req = _cacheQueue.Dequeue();
                    _pendingCacheIds.Remove(req.InstanceId);

                    if (req.Go == null) continue;                  // object destroyed
                    if (_rendererCache.ContainsKey(req.InstanceId))   // raced with another build
                        continue;

                    var rd = CollectRenderers(req.Go, req.ExpandToPrefabRoot, req.BodyOnly);
                    if (rd.Entries.Length > 0)
                    {
                        _rendererCache[req.InstanceId] = rd;
                        // A later successful build clears any pending backoff.
                        _failRetryAt.Remove(req.InstanceId);
                        // Let the builder state machine start the next pass early
                        // so this object gets drawn without waiting the interval.
                        _cacheBuiltSincePass = true;
                    }
                    else
                    {
                        // Transient failure - back off and retry (ragdoll still
                        // initialising, or EFT room culling has the renderers
                        // deactivated from the current position).
                        _failRetryAt[req.InstanceId] = Time.realtimeSinceStartup
                            + (req.BodyOnly ? BodyFailRetrySeconds : ItemFailRetrySeconds);
                        // Debug log, once per unique item name.
                        if (Plugin.DebugLogging.Value
                            && !req.BodyOnly && !req.ExpandToPrefabRoot
                            && _loggedItemFailures.Add(req.Go.name))
                        {
                            int totalChildren = req.Go.GetComponentsInChildren<Transform>(true).Length;
                            int activeRends   = req.Go.GetComponentsInChildren<Renderer>(false).Length;
                            int allRends      = req.Go.GetComponentsInChildren<Renderer>(true).Length;
                            Plugin.LogSource?.LogInfo(
                                $"[BreakoutOutline] Item '{req.Go.name}' produced 0 entries " +
                                $"(children={totalChildren}, activeRenderers={activeRends}, " +
                                $"allRenderers={allRends}, activeInHierarchy={req.Go.activeInHierarchy})");
                        }
                    }
                }
                yield return null;
            }
        }

        // Cull helpers
        // Deliberately NO Renderer.isVisible anywhere: it reads the cached LOD0
        // renderer and EFT's per-any-camera occlusion culling, both of which go
        // false while an object is plainly on screen. Per-pixel GPU occlusion +
        // the frustum test are the visibility authorities.

        // Draw gate for STATIC entries: the cached renderer must actually be
        // rendering. EFT hides unspawned container variants by disabling the
        // Renderer COMPONENT on a still-active GO - includeInactive=false never
        // filtered those, and drawing them makes ghost outlines at empty spawn
        // points. Deliberately checks enabled/active, NOT isVisible (isVisible
        // goes false on plainly-visible objects; see the note below).
        private static bool EntryAlive(in MeshEntry e)
        {
            var r = e.Rend;
            return r == null || (r.enabled && r.gameObject.activeInHierarchy);
        }

        // Current world-AABB from each entry's live renderer. Skips disabled/
        // inactive entries (filters a container's animated lid swap, tracks
        // ragdolling SMRs). Do NOT skip by isVisible here either - frustum-culled
        // sub-renderers would shrink the bounds and shift _ObjectCenter.
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

        // Legacy single-shader path (combined 10-pass material)

        private void SubmitLegacyDraws()
        {
            if (_drawObjects.Count == 0) return;

            float w = Plugin.OutlineWidth.Value;
            _itemMat.SetColor("_OutlineColor", Plugin.ItemOutlineColor.Value);
            _itemMat.SetFloat("_OutlineWidth", w);
            _contMat.SetColor("_OutlineColor", Plugin.ContainerOutlineColor.Value);
            _contMat.SetFloat("_OutlineWidth", w);

            // No CommandBuffer on this path, so _attachedCam is never set - cull
            // against the resolved FPS camera instead.
            var cam = _mainCam;
            if (cam == null) return;
            Vector3 camPos = cam.transform.position;
            Vector3 camFwd = cam.transform.forward;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            // Legacy compiled-shader path (no depth-texture occlusion); only runs
            // when the newer compiled pipelines are unavailable.
            int count = _drawObjects.Count;
            for (int oi = 0; oi < count; oi++)
            {
                var d = _drawObjects[oi];
                float fwdDist = Vector3.Dot(camFwd, d.Bounds.center - camPos);
                if (fwdDist < 0f) continue;
                if (!ComputeLiveBounds(d.Entries, out var liveBounds)) liveBounds = d.Bounds;
                if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, liveBounds)) continue;

                var mat = d.IsContainer ? _contMat : _itemMat;
                Color color = d.IsContainer ? Plugin.ContainerOutlineColor.Value
                                            : Plugin.ItemOutlineColor.Value;
                color.a *= d.Fade;
                mat.SetColor("_OutlineColor", color);
                foreach (var e in d.Entries)
                {
                    if (e.Mesh == null || !EntryAlive(e)) continue;
                    Matrix4x4 m = e.Tx != null
                        ? e.Tx.localToWorldMatrix * e.LocalOffset
                        : e.FixedMatrix;
                    Graphics.DrawMesh(e.Mesh, m, mat, e.Layer);
                }
            }
        }

        // CommandBuffer attach/detach

        // CameraManager.Instance.Camera is the real FPS camera; fall back to a name
        // lookup, then Camera.main. try/catch because Instance can be null during
        // scene load.
        private static Camera ResolveFpsCamera()
        {
            try
            {
                var inst = CameraManager.Instance;
                if (inst != null && inst.Camera != null) return inst.Camera;
            }
            catch { }

            var all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c != null && c.name == "FPS Camera") return c;
            }

            return Camera.main;
        }

        private void EnsureCommandBufferAttached()
        {
            if (_cmd == null) return;
            var cam = _mainCam;
            if (cam == _attachedCam) return;
            DetachCommandBuffer();
            if (cam != null)
            {
                cam.AddCommandBuffer(OutlineEvent, _cmd);
                // Record EFT's depth-texture state before touching the flag.
                _eftProvidesDepth = (cam.depthTextureMode & DepthTextureMode.Depth) != 0;
                _attachedCam = cam;
                ApplyDepthMode(cam, Plugin.LineOfSightCheck.Value);
                if (Plugin.DebugLogging.Value) LogOcclusionDiag(cam);
            }
        }

        // Enable the depth prepass only when occlusion needs it. When LOS is off we
        // restore the camera to whatever EFT had: clear our Depth bit only if EFT
        // wasn't already requesting it (don't fight EFT's own post-FX needs).
        private void ApplyDepthMode(Camera cam, bool losOn)
        {
            if (cam == null) return;
            if (losOn)              cam.depthTextureMode |=  DepthTextureMode.Depth;
            else if (!_eftProvidesDepth) cam.depthTextureMode &= ~DepthTextureMode.Depth;
        }

        // Debug Logging
        private void LogOcclusionDiag(Camera cam)
        {
            try
            {
                var depthTex = Shader.GetGlobalTexture("_CameraDepthTexture");
                string depthDesc = depthTex != null
                    ? $"{depthTex.name} {depthTex.width}x{depthTex.height}"
                    : "NULL(at-Update; may still bind during render)";

                float itemOcc = _itemDrawMat != null ? _itemDrawMat.GetFloat("_DepthOcclude") : -1f;
                int   itemZ   = _itemDrawMat != null ? _itemDrawMat.GetInt("_ZTest")        : -1;

                Plugin.LogSource?.LogInfo(
                    "[BreakoutOutline][OCCLUSION DIAG] " +
                    $"cam='{cam.name}' renderPath={cam.actualRenderingPath} " +
                    $"hdr={cam.allowHDR} msaa={cam.allowMSAA} depthMode={cam.depthTextureMode} " +
                    $"eftProvidesDepth={_eftProvidesDepth} " +
                    $"event={OutlineEvent} | _CameraDepthTexture={depthDesc} | " +
                    $"losCfg={Plugin.LineOfSightCheck.Value} itemDepthOcclude={itemOcc} itemZTest={itemZ} " +
                    $"| threePass={_useThreePass} useShader={_useShader}");
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogWarning($"[LootOutline][OCCLUSION DIAG] failed: {e.Message}");
            }
        }

        private void DetachCommandBuffer()
        {
            if (_cmd == null || _attachedCam == null) { _attachedCam = null; return; }
            try { _attachedCam.RemoveCommandBuffer(OutlineEvent, _cmd); }
            catch { }
            _attachedCam = null;
        }

        private void OnDestroy()
        {
            if (_cacheCoroutine != null) { StopCoroutine(_cacheCoroutine); _cacheCoroutine = null; }
            _cacheQueue.Clear();
            _pendingCacheIds.Clear();
            _failRetryAt.Clear();

            DetachCommandBuffer();
            if (_cmd != null) { _cmd.Release(); _cmd = null; }

            _drawObjects.Clear();
            foreach (var kv in _rendererCache) ReleaseCorpseSmrs(kv.Value);
            _rendererCache.Clear();
            _activeThisTick.Clear();

            if (_itemStencilMat != null) Destroy(_itemStencilMat);
            if (_itemDrawMat    != null) Destroy(_itemDrawMat);
            if (_itemClearMat   != null) Destroy(_itemClearMat);
            if (_contStencilMat != null) Destroy(_contStencilMat);
            if (_contDrawMat    != null) Destroy(_contDrawMat);
            if (_contClearMat   != null) Destroy(_contClearMat);
            if (_bodyDrawMat    != null) Destroy(_bodyDrawMat);
            if (_maskMat        != null) Destroy(_maskMat);
            if (_edgeMat        != null) Destroy(_edgeMat);
            if (_itemMat != null) Destroy(_itemMat);
            if (_contMat != null) Destroy(_contMat);
        }
    }
}