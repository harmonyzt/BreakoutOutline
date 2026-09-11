using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.Rendering;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController : MonoBehaviour
    {
        // Shader materials

        // Three-pass fallback pipeline: stencil silhouette -> draw ring -> clear, per object
        private Material _itemStencilMat, _itemDrawMat, _itemClearMat;
        private Material _contStencilMat, _contDrawMat, _contClearMat;
        private Material _bodyDrawMat;
        private bool _useThreePass;

        // Preferred pipeline: all silhouettes into one mask RT, then a single fullscreen edge pass
        private Material _maskMat, _edgeMat;
        private bool _useMaskEdge;
        private int _maskRtId;
        private int _fadeRtId;

        // ----
        // Command buffer and camera
        // ----

        private CommandBuffer _cmd;
        private Camera _attachedCam;
        private const CameraEvent OutlineEvent = CameraEvent.AfterForwardAlpha;
        private bool _eftProvidesDepth;

        // Cached once per Update from CameraManager.Instance.Camera
        private Camera _mainCam;
        
        // ----
        // World
        // ----
        
        private LootableContainer[] _containerCache = Array.Empty<LootableContainer>();

        // Per-pass snapshot of GameWorld.LootItems.List_0
        private readonly List<LootItem> _lootItemsSnapshot = new List<LootItem>();
        private LootItem[] _lootItemCache = Array.Empty<LootItem>();
        private float _lootItemCacheBuildTime = -999f;
        private const float LootItemCacheRefreshSeconds = 10f;
        private float _lastLootDiagTime = -999f;

        // Local-player equipped-ID cache
        private string[] _mainEquipIds = Array.Empty<string>();
        private float _mainEquipBuiltAt = -999f;
        private const float MainEquipRefreshSeconds = 2f;

        // Container cache
        private float _containerCacheTryTime = -999f;
        private const float ContainerCacheRetrySeconds = 5f;
        private const float ContainerRescanSeconds = 60f;

        // Per-container has-loot
        private struct ContainerLoot { public bool Has; public float At; }
        private static readonly HashSet<int> _searchedContainers = new();
        private static IPlayerSearchController _searchController;
        private readonly Dictionary<int, ContainerLoot> _containerLootCache = new Dictionary<int, ContainerLoot>();
        private const float ContainerLootTtlSeconds = 2f;
        
        // Mesh and other data
        private readonly struct MeshEntry
        {
            public readonly Mesh Mesh;
            public readonly Transform Tx;
            public readonly Matrix4x4 LocalOffset;
            public readonly Matrix4x4 FixedMatrix;
            public readonly int Layer;
            public readonly SkinnedMeshRenderer Smr;
            public readonly Renderer Rend;

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
            public readonly Bounds Bounds;
            public readonly bool IsBody;
            public RendererData(MeshEntry[] e, Bounds b, bool isBody = false) { Entries = e; Bounds = b; IsBody = isBody; }
        }

        private readonly Dictionary<int, RendererData> _rendererCache = new Dictionary<int, RendererData>();
        private readonly HashSet<int> _activeThisTick = new HashSet<int>();

        // ----
        // Cache build queue
        // ----

        private struct PendingCache
        {
            public GameObject Go;
            public bool ExpandToPrefabRoot;
            public bool BodyOnly;
            public int InstanceId;
        }

        private readonly Queue<PendingCache> _cacheQueue = new Queue<PendingCache>();
        private readonly HashSet<int> _pendingCacheIds = new HashSet<int>();
        private readonly Dictionary<int, float> _failRetryAt = new Dictionary<int, float>();
        private const float BodyFailRetrySeconds = 1f;
        private const float ItemFailRetrySeconds = 5f;

        // Per-corpse equipped-item-ID cache
        private struct CorpseEquip { public string[] Ids; public float BuiltAt; }
        private readonly Dictionary<int, CorpseEquip> _corpseEquipCache = new Dictionary<int, CorpseEquip>();
        private const float CorpseEquipRefreshSeconds = 5f;
        private readonly List<string> _equipScratch = new List<string>();
        private Coroutine _cacheCoroutine;
        private const float CacheBudgetSeconds = 0.003f;

        // ----
        // Draw list
        // ----

        private struct DrawObject
        {
            public int SourceId;
            public MeshEntry[] Entries;
            public bool IsContainer;
            public bool IsBody;
            public Bounds Bounds;
            public float Fade;
            public DrawObject(int sourceId, MeshEntry[] e, bool c, Bounds b, float fade, bool body = false)
            { SourceId = sourceId; Entries = e; IsContainer = c; IsBody = body; Bounds = b; Fade = fade; }
        }

        private readonly List<DrawObject> _drawObjects = new List<DrawObject>();
        private readonly HashSet<Player> _seenPlayers = new HashSet<Player>();
        private static readonly Predicate<Player> _destroyedPlayer = p => p == null;
        private readonly List<Vector3> _deadBodyPositions = new List<Vector3>();
        private const float BodyWeaponSuppressRadius = 1.15f;
        private const float BodyWeaponSuppressRadiusSqr = BodyWeaponSuppressRadius * BodyWeaponSuppressRadius;

        // Debug-log dedup sets
        private static readonly HashSet<string> _loggedItemFailures = new HashSet<string>();
        private static int _bodiesLogged;
        private const int BodyLogCap = 3;
        private static readonly HashSet<string> _loggedQueued = new HashSet<string>();
        private static readonly HashSet<string> _loggedOwnershipRejects = new HashSet<string>();

        private bool _limitOpacityForNightVision;
        private MaterialPropertyBlock _mpb;

        // Amortized draw-list builder state
        private enum BuildPhase { Idle, Items, Containers, Bodies, Finalize }
        private BuildPhase _buildPhase = BuildPhase.Idle;
        private int _buildCursor;
        private float _lastPassStartTime = -999f;
        private const float PassIntervalSeconds = 0.25f;
        private bool _cacheBuiltSincePass;
        private const float EarlyPassMinSeconds = 0.1f;

        // Per-frame slice sizes (amortized processing budgets)
        private const int ItemsPerFrame = 192;
        private const int ContainersPerFrame = 48;
        private const int BodiesPerFrame = 24;

        // Pass snapshot
        private Vector3 _passPlayerPos;
        private float _passRange, _passPrefilterSqr;
        private bool _passDiag;
        private int _diagInRange, _diagOwned, _diagQueued;
        private readonly List<Player> _seenSnapshot = new List<Player>();
        private readonly List<DrawObject> _drawObjectsScratch = new List<DrawObject>();

        // CB rebuild throttle
        private float _lastRebuildTime = -999f;
        private bool _drawListDirty;
        private const float RebuildMinInterval = 1f / 30f; // 30

        // Render dimension tracking
        // Used to fix downsampling, DLSS, and FSR issues
        private int _recordedCameraWidth = -1;
        private int _recordedCameraHeight = -1;
        private int _recordedScreenWidth = -1;
        private int _recordedScreenHeight = -1;
        private int _recordedTargetTextureId = -1;
        private int _recordedTargetWidth = -1;
        private int _recordedTargetHeight = -1;

        private const int RootExpansionRendererCap = 32;
        private const float MinItemSize = 0.05f;

        private readonly Plane[] _frustumPlanes = new Plane[6];
        private readonly List<int> _visibleIdxScratch = new List<int>();

        // Pooled scratch
        private Matrix4x4[] _matrixBuffer = new Matrix4x4[16];
        private readonly HashSet<Transform> _playerTransforms = new HashSet<Transform>();
        private readonly HashSet<string> _equippedItemIds = new HashSet<string>();
        private readonly List<int> _staleIds = new List<int>();

        // Nearest-N cap sort
        private Vector3 _drawSortOrigin;
        private Comparison<DrawObject> _drawSortCmp;
        
        // DEPTH BIAS
        private const float ItemDepthBias = 0.05f;
        private const float EdgeItemDepthBias = 0.1f;

        // Shader properties
        private static readonly int PROP_OutlineColor = Shader.PropertyToID("_OutlineColor");
        private static readonly int PROP_OutlineWidth = Shader.PropertyToID("_OutlineWidth");
        private static readonly int PROP_ObjectCenter = Shader.PropertyToID("_ObjectCenter");
        private static readonly int PROP_DepthOcclude = Shader.PropertyToID("_DepthOcclude");
        private static readonly int PROP_DepthBias = Shader.PropertyToID("_DepthBias");
        private static readonly int PROP_OutlineWidthPx = Shader.PropertyToID("_OutlineWidthPx");
        private static readonly int PROP_EdgeOcclude = Shader.PropertyToID("_EdgeOcclude");
        private static readonly int PROP_EdgeDepthBiasItem = Shader.PropertyToID("_EdgeDepthBiasItem");
        private static readonly int PROP_EdgeDepthBiasBody = Shader.PropertyToID("_EdgeDepthBiasBody");
        private static readonly int PROP_MaskBodyFlag = Shader.PropertyToID("_MaskBodyFlag");
        private static readonly int PROP_OutlineAlpha = Shader.PropertyToID("_OutlineAlpha");
        private static readonly int PROP_OutlineFade = Shader.PropertyToID("_OutlineFade");
        private static readonly int PROP_MaskFadeOnly = Shader.PropertyToID("_MaskFadeOnly");
        
        private void Awake()
        {
            _cacheCoroutine = StartCoroutine(CacheBuilderLoop());

            _drawSortCmp = (a, b) =>
                (a.Bounds.center - _drawSortOrigin).sqrMagnitude
                .CompareTo((b.Bounds.center - _drawSortOrigin).sqrMagnitude);

            InitializeShaderPipeline();
        }

        private void Update()
        {
            if (!Plugin.GlobalOutlineToggle.Value)
            {
                DetachCommandBuffer();
                _cmd?.Clear();
                return;
            }

            _mainCam = ResolveFpsCamera();
            var gameWorld = Singleton<GameWorld>.Instance;
            _limitOpacityForNightVision = Plugin.LimitNightVisionOpacity.Value
                && Plugin.IsNightVisionActive(gameWorld?.MainPlayer);

            EnsureCommandBufferAttached();

            if (_useMaskEdge || _useThreePass)
                _drawListDirty |= RenderDimensionsChanged();

            // Rebuild the CB from the current draw list, throttled to ~30Hz
            if (_useMaskEdge)
            {
                float nowRb = Time.realtimeSinceStartup;
                if (_drawListDirty || nowRb - _lastRebuildTime >= RebuildMinInterval)
                {
                    float drawStart = Time.realtimeSinceStartup;
                    RebuildMaskEdgeCommandBuffer();
                    LogDrawPerformance("mask-edge", drawStart);
                    _lastRebuildTime = nowRb;
                    _drawListDirty = false;
                }
            }
            else if (_useThreePass)
            {
                float nowRb = Time.realtimeSinceStartup;
                if (_drawListDirty || nowRb - _lastRebuildTime >= RebuildMinInterval)
                {
                    float drawStart = Time.realtimeSinceStartup;
                    RebuildThreePassCommandBuffer();
                    LogDrawPerformance("three-pass", drawStart);
                    _lastRebuildTime = nowRb;
                    _drawListDirty = false;
                }
            }

            StepDrawListBuilder();
            TryHookSearchController();
        }

        private void OnDestroy()
        {
            _mainCam = null;
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
            _searchedContainers.Clear();

            if (_itemStencilMat != null) Destroy(_itemStencilMat);
            if (_itemDrawMat != null) Destroy(_itemDrawMat);
            if (_itemClearMat != null) Destroy(_itemClearMat);
            if (_contStencilMat != null) Destroy(_contStencilMat);
            if (_contDrawMat != null) Destroy(_contDrawMat);
            if (_contClearMat != null) Destroy(_contClearMat);
            if (_bodyDrawMat != null) Destroy(_bodyDrawMat);
            if (_maskMat != null) Destroy(_maskMat);
            if (_edgeMat != null) Destroy(_edgeMat);
        }
        
        // Utility stuff
        private Color GetOutlineColor(bool isContainer)
        {
            Color color = isContainer
                ? Plugin.ContainerOutlineColor.Value
                : Plugin.ItemOutlineColor.Value;
            if (_limitOpacityForNightVision && color.a > 0.5f)
                color.a = 0.5f;
            return color;
        }

        private void LogDrawPerformance(string pipeline, float startedAt)
        {
            if (!Plugin.DebugLogging.Value) return;

            var cam = _attachedCam;
            int width = cam != null ? cam.pixelWidth : 0;
            int height = cam != null ? cam.pixelHeight : 0;
            Plugin.LogSource?.LogInfo(
                $"[BreakoutOutline][PERF][DRAW] pipeline={pipeline} " +
                $"ms={(Time.realtimeSinceStartup - startedAt) * 1000f:F3} " +
                $"objects={_drawObjects.Count} visible={_visibleIdxScratch.Count} " +
                $"resolution={width}x{height} cacheQueue={_cacheQueue.Count}");
        }
    }
}
