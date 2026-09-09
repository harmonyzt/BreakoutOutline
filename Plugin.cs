using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using UnityEngine;
using LootOutlineController = BreakoutOutlines.Drawer.LootOutlineController;

namespace BreakoutOutlines
{
    [BepInPlugin("com.harmonyzt.breakoutoutlines", "Breakout Outlines", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> GlobalOutlineToggle;
        public static ConfigEntry<KeyboardShortcut> ToggleOutlineKey;
        public static ConfigEntry<bool> OutlineLooseItems;
        public static ConfigEntry<bool> OutlineContainers;
        public static ConfigEntry<bool> OutlineEmptyContainers;
        public static ConfigEntry<bool> DrawDeadBodies;
        public static ConfigEntry<bool> HideSearchedContainers;
        public static ConfigEntry<Color> ItemOutlineColor;
        public static ConfigEntry<Color> ContainerOutlineColor;
        public static ConfigEntry<bool> LimitNightVisionOpacity;
        public static ConfigEntry<float> OutlineWidth;
        public static ConfigEntry<float> DetectionRange;
        public static ConfigEntry<bool> LineOfSightCheck;
        public static ConfigEntry<float> BodyDepthBias;
        public static ConfigEntry<int> MaxOutlinedObjects;
        public static ConfigEntry<bool> DebugLogging;

        // Loaded from the AssetBundle.
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

            Enabled = Config.Bind("General", "Enabled", true, "Global outline toggle - switch all loot outlines on or off");
            GlobalOutlineToggle = Enabled;
            ToggleOutlineKey = Config.Bind("General", "Toggle Outline Key", new KeyboardShortcut(KeyCode.F6),
                "Keyboard shortcut for toggling all loot outlines");
            OutlineLooseItems = Config.Bind("General", "Outline Loose Items", true, "Highlight loose loot items lying on the ground");
            OutlineContainers = Config.Bind("General", "Outline Containers", true, "Highlight lootable containers (crates, bags, etc.)");
            OutlineEmptyContainers = Config.Bind("General", "Outline Containers", false, "Should we outline empty containers too?");
            DrawDeadBodies = Config.Bind("General", "Draw Dead Bodies", true, "Highlight dead bodies");
            HideSearchedContainers = Config.Bind("General", "Hide Searched Containers", false, "Stop outlining containers after they have been searched");
            ItemOutlineColor = Config.Bind("Visuals", "Item Color", new Color(1f, 1f, 1f, 1f), "Outline color for loose items");
            ContainerOutlineColor = Config.Bind("Visuals", "Container Color", new Color(1f, 1f, 1f, 1f), "Outline color for containers");
            LimitNightVisionOpacity = Config.Bind("Performance", "Limit Opacity With Night Vision", true,
                "Cap loose loot and container outline alpha at half while night vision is active to prevent bleeding/excessive bloom of outlines");
            OutlineWidth = Config.Bind("Visuals", "Outline Width", 3f,
                new ConfigDescription("Outline thickness in pixels (distance-independent)",
                    new AcceptableValueRange<float>(1f, 20f)));
            DetectionRange = Config.Bind("General", "Draw Range", 5.5f,
                new ConfigDescription("Max distance from player to show outlines (meters)",
                    new AcceptableValueRange<float>(1f, 25f)));
            LineOfSightCheck = Config.Bind("Performance", "Line of Sight Check", true, "Enable to hide outlines for items, containers and bodies so outlines don't bleed through floors and walls. Turn OFF to see every outline through everything");
            BodyDepthBias = Config.Bind("Visuals", "Body Outline Depth Bias", 0.5f,
                new ConfigDescription("Occlusion tolerance for dead-body outlines at contact surfaces (meters). Raise it if prone bodies still fragment - set lower it if body outlines bleed through thin walls. Loose items and containers use a fixed tight bias",
                    new AcceptableValueRange<float>(0f, 2f)));
            MaxOutlinedObjects = Config.Bind("Performance", "Max Outlined Objects", 25,
                new ConfigDescription("Determines how many objects will be outlined at once - the nearest N (items, containers and bodies combined). 0 = unlimited. Set to ~20-30 if FPS dips in dense areas",
                    new AcceptableValueRange<int>(0, 200)));
            DebugLogging = Config.Bind("Debug", "Debug Logging", false, "Leave off for normal play.");

            TryLoadShaderBundle();

            bool anyPipeline = (MaskShader != null && EdgeShader != null)
                            || (StencilShader != null && DrawShader != null && ClearShader != null)
                            || OutlineShader != null;
            LogSource.LogInfo(anyPipeline
                ? "LootOutline loaded - using default shader outline pipeline."
                : "LootOutline loaded - no usable shaders, stopping...");
        }

        public static bool IsNightVisionActive(Player player)
        {
            if (player == null) return false;

            var nightVis = player.NightVisionObserver.Component;
            return nightVis != null && nightVis.Togglable.On;
        }

        private void TryLoadShaderBundle()
        {
            string bundlePath = Path.Combine(Path.GetDirectoryName(Info.Location), "OutlineShader");
            if (!File.Exists(bundlePath))
            {
                LogSource.LogInfo($"BreakoutOutline: bundle not found at '{bundlePath}'. The mod will not work.");
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
            DrawShader = bundle.LoadAsset<Shader>("OutlineDraw");
            ClearShader = bundle.LoadAsset<Shader>("OutlineClear");
            MaskShader = bundle.LoadAsset<Shader>("OutlineMask");
            EdgeShader = bundle.LoadAsset<Shader>("OutlineEdge");

            // Drop any that didn't compile
            if (OutlineShader != null && !OutlineShader.isSupported) OutlineShader = null;
            if (StencilShader != null && !StencilShader.isSupported) StencilShader = null;
            if (DrawShader != null && !DrawShader.isSupported) DrawShader = null;
            if (ClearShader != null && !ClearShader.isSupported) ClearShader = null;
            if (MaskShader != null && !MaskShader.isSupported) MaskShader = null;
            if (EdgeShader != null && !EdgeShader.isSupported) EdgeShader = null;

            bool maskEdgeReady = MaskShader != null && EdgeShader != null;
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
                maskEdgeReady ? "BreakoutOutline: Tier-2 mask+edge shader pipeline loaded (single fullscreen outline pass)."
              : threePassReady ? "BreakoutOutline: three-pass shader pipeline loaded (stencil/draw/clear)."
                               : "BreakoutOutline: only legacy combined OutlineShader available - falling back.");

            bundle.Unload(false);
        }

        private void Update()
        {
            if (ToggleOutlineKey.Value.IsDown())
                GlobalOutlineToggle.Value = !GlobalOutlineToggle.Value;

            if (!GlobalOutlineToggle.Value)
            {
                return;
            }

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                _controllerReady = false;
                return;
            }

            if (!_controllerReady)
            {
                if (gameWorld.GetComponent<LootOutlineController>() == null)
                {
                    gameWorld.gameObject.AddComponent<LootOutlineController>();
                }

                _controllerReady = true;
            }
        }
    }
}