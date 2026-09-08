using System.IO;
using ABI_H.Drawer;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace ABI_H
{
    [BepInPlugin("com.20fpsguy.LootOutline", "LootOutline", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        public static ConfigEntry<bool>  Enabled;
        public static ConfigEntry<bool>  OutlineLooseItems;
        public static ConfigEntry<bool>  OutlineContainers;
        public static ConfigEntry<Color> ItemOutlineColor;
        public static ConfigEntry<Color> ContainerOutlineColor;
        public static ConfigEntry<float> OutlineWidth;
        public static ConfigEntry<float> DetectionRange;
        public static ConfigEntry<float> InteractHideDistance;
        public static ConfigEntry<bool>  LineOfSightCheck;
        public static ConfigEntry<float> BodyDepthBias;
        public static ConfigEntry<int>   MaxOutlinedObjects;
        public static ConfigEntry<bool>  DebugLogging;

        // Loaded from the AssetBundle. Preference order: mask+edge (one fullscreen
        // outline pass) → three-pass stencil/draw/clear → legacy combined shader
        // → GL wireframe.
        public static Shader OutlineShader;
        public static Shader StencilShader;
        public static Shader DrawShader;
        public static Shader ClearShader;
        public static Shader MaskShader;
        public static Shader EdgeShader;

        private bool _controllerReady;

        private void Awake()
        {
            LogSource = Logger;

            Enabled               = Config.Bind("General", "Enabled", true,  "Enable loot outline highlighting");
            OutlineLooseItems     = Config.Bind("General", "Outline Loose Items", true,  "Highlight loose loot items lying on the ground");
            OutlineContainers     = Config.Bind("General", "Outline Containers", true,  "Highlight lootable containers (crates, bags, etc.) and dead bodies");
            ItemOutlineColor      = Config.Bind("Visuals", "Item Color",  new Color(1f, 1f, 1f, 1f),        "Outline color for loose items");
            ContainerOutlineColor = Config.Bind("Visuals", "Container Color", new Color(0.4f, 0.85f, 1f, 1f),   "Outline color for containers");
            OutlineWidth          = Config.Bind("Visuals", "Outline Width",3f,
                new ConfigDescription("Outline thickness in pixels (screen-space, distance-independent)", 
                    new AcceptableValueRange<float>(1f, 20f)));
            DetectionRange        = Config.Bind("General", "Detection Range",     3.5f,
                new ConfigDescription("Max distance from player to show outlines (meters)", 
                    new AcceptableValueRange<float>(1f, 25f)));
            InteractHideDistance  = Config.Bind("General", "Interact Hide Distance", 1.4f,
                new ConfigDescription("Hide an item's outline once you're this close — i.e. close enough to take it. The outline is there to help you spot loot; once you can pick it up the highlight gets out of the way. Set to 0 to keep outlines visible even while standing on the item.",
                    new AcceptableValueRange<float>(0f, 3f)));
            LineOfSightCheck      = Config.Bind("Performance", "Line of Sight Check", true,
                "Hide outlines for items, containers and bodies occluded by world geometry, so outlines don't bleed through floors and walls. Occlusion is per-pixel against the scene depth texture, so it's collider-accurate even on EFT's colliderless walls. Turn OFF to see every outline through everything. Also gates the depth prepass: while OFF the mod won't force depth-texture generation.");
            BodyDepthBias         = Config.Bind("Visuals", "Body Outline Depth Bias", 0.5f,
                new ConfigDescription("Occlusion tolerance for dead-body outlines at contact surfaces (meters). A corpse lies flat on the floor, so its outline ring sits at nearly the same depth as the body edge and can be wrongly clipped, fragmenting prone bodies. This bias lets the ring survive geometry up to this many meters in front of the body edge. Raise it if prone bodies still fragment; lower it if body outlines bleed through thin walls. Loose items and containers use a fixed tight bias.",
                    new AcceptableValueRange<float>(0f, 2f)));
            MaxOutlinedObjects    = Config.Bind("Performance", "Max Outlined Objects", 0,
                new ConfigDescription("Hard cap on how many objects are outlined at once — the nearest N (items, containers and bodies combined). Each outlined object costs draw calls every frame, so this bounds the worst case in cluttered loot rooms. 0 = unlimited. Set to ~20-30 if FPS dips in dense areas; objects past the cap aren't outlined until you move closer.",
                    new AcceptableValueRange<int>(0, 200)));
            DebugLogging          = Config.Bind("Debug", "Debug Logging", false,
                "Verbose diagnostic logging — renderer dumps for dead bodies, per-item ownership decisions, loose-item scan counts, and occlusion setup. Leave off for normal play; turn on only to diagnose missing or extra outlines.");

            TryLoadShaderBundle();

            bool anyPipeline = (MaskShader != null && EdgeShader != null)
                            || (StencilShader != null && DrawShader != null && ClearShader != null)
                            || OutlineShader != null;
            LogSource.LogInfo(anyPipeline
                ? "LootOutline loaded - using default shader outline pipeline."
                : "LootOutline loaded - no usable shaders, stopping...");
        }

        private void TryLoadShaderBundle()
        {
            string bundlePath = Path.Combine(Path.GetDirectoryName(Info.Location), "BreakoutOutlines", "lootoutlineshader");
            if (!File.Exists(bundlePath))
            {
                LogSource.LogInfo($"LootOutline: bundle not found at '{bundlePath}'. Stopping the mod...");
                return;
            }

            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                LogSource.LogError("LootOutline: AssetBundle.LoadFromFile returned null - wrong Unity version?");
                return;
            }
            
            if (DebugLogging.Value)
            {
                var allNames = bundle.GetAllAssetNames();
                LogSource.LogInfo($"LootOutline: bundle contains {allNames.Length} asset(s):");
                foreach (var n in allNames)
                    LogSource.LogInfo($"  • {n}");
            }
            
            OutlineShader = bundle.LoadAsset<Shader>("OutlineShader");
            StencilShader = bundle.LoadAsset<Shader>("OutlineStencil");
            DrawShader    = bundle.LoadAsset<Shader>("OutlineDraw");
            ClearShader   = bundle.LoadAsset<Shader>("OutlineClear");
            MaskShader    = bundle.LoadAsset<Shader>("OutlineMask");
            EdgeShader    = bundle.LoadAsset<Shader>("OutlineEdge");

            // Drop any that didn't compile for this runtime so the controller can make a clean fallback decision
            if (OutlineShader != null && !OutlineShader.isSupported) OutlineShader = null;
            if (StencilShader != null && !StencilShader.isSupported) StencilShader = null;
            if (DrawShader    != null && !DrawShader.isSupported)    DrawShader    = null;
            if (ClearShader   != null && !ClearShader.isSupported)   ClearShader   = null;
            if (MaskShader    != null && !MaskShader.isSupported)    MaskShader    = null;
            if (EdgeShader    != null && !EdgeShader.isSupported)    EdgeShader    = null;

            bool maskEdgeReady  = MaskShader != null && EdgeShader != null;
            bool threePassReady = StencilShader != null && DrawShader != null && ClearShader != null;

            if (!maskEdgeReady && !threePassReady && OutlineShader == null)
            {
                LogSource.LogError(
                    "LootOutline: no usable shaders in bundle. " +
                    "Either the bundle was built with a different Unity version than EFT uses, " +
                    "or shader asset names changed. Rebuild the bundle.");
                bundle.Unload(false);
                return;
            }

            LogSource.LogInfo(
                maskEdgeReady  ? "LootOutline: Tier-2 mask+edge shader pipeline loaded (single fullscreen outline pass)."
              : threePassReady ? "LootOutline: three-pass shader pipeline loaded (stencil/draw/clear)."
                               : "LootOutline: only legacy combined OutlineShader available — falling back.");

            bundle.Unload(false);
        }

        private void Update()
        {
            if (!Enabled.Value) return;

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                _controllerReady = false;
                return;
            }

            if (!_controllerReady)
            {
                if (gameWorld.GetComponent<LootOutlineController>() == null)
                    gameWorld.gameObject.AddComponent<LootOutlineController>();
                _controllerReady = true;
            }
        }
    }
}