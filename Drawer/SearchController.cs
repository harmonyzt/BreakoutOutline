using System;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        // Hook onto search controller to determine what we looted or not
        public void TryHookSearchController()
        {
            try
            {
                var player = Singleton<GameWorld>.Instance?.MainPlayer;

                if (player == null)
                    return;

                var controller = player.SearchController;

                if (controller == null)
                    return;

                if (ReferenceEquals(_searchController, controller))
                    return;

                if (_searchController != null)
                    _searchController.OnItemSearched -= OnItemSearched;

                _searchController = controller;
                _searchController.OnItemSearched += OnItemSearched;

                Plugin.LogSource?.LogInfo(
                    $"[BreakoutOutline] Hooked OnItemSearched: " +
                    $"{controller.GetType().FullName}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError(
                    $"[BreakoutOutline] Failed to hook SearchController: {ex}");
            }
        }

        private void OnItemSearched(SearchableItem item)
        {
            if (item == null)
                return;

            try
            {
                Plugin.LogSource?.LogInfo(
                    $"[BreakoutOutline] PLAYER SEARCHED: " +
                    $"type={item.GetType().FullName}, " +
                    $"id={item.Id}");

                var gameWorld = Singleton<GameWorld>.Instance;

                if (gameWorld == null)
                    return;

                foreach (var container in _containerCache)
                {
                    if (container == null)
                        continue;

                    var root = container.ItemOwner?.RootItem;

                    if (root == null)
                        continue;

                    if (!ReferenceEquals(root, item))
                        continue;

                    int instanceId = container.gameObject.GetInstanceID();

                    if (_searchedContainers.Add(instanceId))
                    {
                        if (Plugin.HideSearchedContainers.Value)
                            RemoveSearchedContainerDraw(instanceId);
                    }

                    return;
                }

                Plugin.LogSource?.LogInfo("[BreakoutOutline] Searched item did not match a tracked container.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[BreakoutOutline] OnItemSearched failed: {ex}");
            }
        }

        private static bool ContainerHasBeenSearched(LootableContainer c)
        {
            // This gets filled by OnItemSearched
            int instanceId = c.gameObject.GetInstanceID();
            if (_searchedContainers.Contains(instanceId))
                return true;

            return false;
        }

        // Remove drawn container after it was looted/or checked out
        private void RemoveSearchedContainerDraw(int instanceId)
        {
            for (int i = _drawObjects.Count - 1; i >= 0; i--)
                if (_drawObjects[i].SourceId == instanceId)
                    _drawObjects.RemoveAt(i);

            for (int i = _drawObjectsScratch.Count - 1; i >= 0; i--)
                if (_drawObjectsScratch[i].SourceId == instanceId)
                    _drawObjectsScratch.RemoveAt(i);

            _drawListDirty = true;
        }
    }
}