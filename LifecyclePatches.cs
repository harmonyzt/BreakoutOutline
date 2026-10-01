using BreakoutOutlines.Drawer;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace BreakoutOutlines
{
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    internal static class GameWorldStartedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GameWorld __instance)
        {
            if (__instance == null || __instance.MainPlayer == null)
                return;

            if (__instance.GetComponent<LootOutlineController>() == null)
                __instance.gameObject.AddComponent<LootOutlineController>();
        }
    }

    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.Dispose))]
    internal static class GameWorldDisposePatch
    {
        [HarmonyPrefix]
        private static void Prefix(GameWorld __instance)
        {
            if (__instance == null)
                return;

            var controller = __instance.GetComponent<LootOutlineController>();
            if (controller == null)
                return;

            controller.OnGameWorldDisposing();
            Object.Destroy(controller);
        }
    }
}
