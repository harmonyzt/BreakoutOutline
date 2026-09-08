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
    [BepInPlugin("com.harmonyzt.breakoutoutline", "Breakout Outline", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        public static ConfigEntry<bool>  Enabled;
        public static ConfigEntry<bool>  OutlineLooseItems;
        public static ConfigEntry<bool>  OutlineContainers;
        public static ConfigEntry<bool>  DrawDeadBodies;
        public static ConfigEntry<Color> ItemOutlineColor;
        public static ConfigEntry<Color> ContainerOutlineColor;
        public static ConfigEntry<float> OutlineWidth;
        public static ConfigEntry<float> DetectionRange;
        public static ConfigEntry<float> InteractHideDistance;
        public static ConfigEntry<bool>  LineOfSightCheck;
        public static ConfigEntry<float> BodyDepthBias;
        public static ConfigEntry<int>   MaxOutlinedObjects;
        public static ConfigEntry<bool>  DebugLogging;

        // Loaded from the AssetBundle. Preference order: mask+edge (one fullscreen outline pass) - three-pass stencil/draw/clear - legacy combined shader
        // harmony: (deprecate this later)
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
            OutlineContainers     = Config.Bind("General", "Outline Containers", true,  "Highlight lootable containers (crates, bags, etc.)");
            DrawDeadBodies        = Config.Bind("General", "Draw Dead Bodies", true,  "Highlight dead bodies");
            ItemOutlineColor      = Config.Bind("Visuals", "Item Color",  new Color(1f, 1f, 1f, 1f),        "Outline color for loose items");
            ContainerOutlineColor = Config.Bind("Visuals", "Container Color", new Color(1f, 1f, 1f, 1f),   "Outline color for containers");
            OutlineWidth          = Config.Bind("Visuals", "Outline Width",3f,
                new ConfigDescription("Outline thickness in pixels (distance-independent)",
                    new AcceptableValueRange<float>(1f, 20f)));
            DetectionRange        = Config.Bind("General", "Draw Range",     10f,
                new ConfigDescription("Max distance from player to show outlines (meters)", 
                    new AcceptableValueRange<float>(1f, 25f)));
            InteractHideDistance  = Config.Bind("General", "Interact Hide Distance", 0.1f,
                new ConfigDescription("Hide an item's outline once you're this close - i.e. close enough to take it. Set to 0 to keep outlines visible even while standing on the item.",
                    new AcceptableValueRange<float>(0f, 3f)));
            LineOfSightCheck      = Config.Bind("Performance", "Line of Sight Check", true,
                "Enable to hide outlines for items, containers and bodies so outlines don't bleed through floors and walls. Turn OFF to see every outline through everything");
            BodyDepthBias         = Config.Bind("Visuals", "Body Outline Depth Bias", 0.5f,
                new ConfigDescription("Occlusion tolerance for dead-body outlines at contact surfaces (meters). Raise it if prone bodies still fragment - set lower it if body outlines bleed through thin walls. Loose items and containers use a fixed tight bias",
                    new AcceptableValueRange<float>(0f, 2f)));
            MaxOutlinedObjects    = Config.Bind("Performance", "Max Outlined Objects", 0,
                new ConfigDescription("Determines how many objects will be outlined at once - the nearest N (items, containers and bodies combined). 0 = unlimited. Set to ~20-30 if FPS dips in dense areas",
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
            string bundlePath = Path.Combine(Path.GetDirectoryName(Info.Location), "OutlineShader");
            if (!File.Exists(bundlePath))
            {
                LogSource.LogInfo($"BreakoutOutline: bundle not found at '{bundlePath}'. Stopping the mod...");
                return;
            }

            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                LogSource.LogError("BreakoutOutline: AssetBundle.LoadFromFile returned null - wrong Unity version?");
                return;
            }
            
            if (DebugLogging.Value)
            {
                var allNames = bundle.GetAllAssetNames();
                LogSource.LogInfo($"BreakoutOutline: bundle contains {allNames.Length} asset(s):");
                foreach (var n in allNames)
                    LogSource.LogInfo($"  • {n}");
            }
            
            OutlineShader = bundle.LoadAsset<Shader>("OutlineShader");
            StencilShader = bundle.LoadAsset<Shader>("OutlineStencil");
            DrawShader    = bundle.LoadAsset<Shader>("OutlineDraw");
            ClearShader   = bundle.LoadAsset<Shader>("OutlineClear");
            MaskShader    = bundle.LoadAsset<Shader>("OutlineMask");
            EdgeShader    = bundle.LoadAsset<Shader>("OutlineEdge");

            // Drop any that didn't compile
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
                    "BreakoutOutline: no usable shaders in bundle. " +
                    "Either the bundle was built with a different Unity version than EFT uses, " +
                    "or shader asset names changed. Rebuild the bundle.");
                bundle.Unload(false);
                return;
            }

            LogSource.LogInfo(
                maskEdgeReady  ? "BreakoutOutline: Tier-2 mask+edge shader pipeline loaded (single fullscreen outline pass)."
              : threePassReady ? "BreakoutOutline: three-pass shader pipeline loaded (stencil/draw/clear)."
                               : "BreakoutOutline: only legacy combined OutlineShader available — falling back.");

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