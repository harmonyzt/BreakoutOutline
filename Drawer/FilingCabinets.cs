using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        private sealed class FilingCabinet
        {
            public GameObject Root;
            public LootableContainer[] Drawers;
        }

        private readonly Dictionary<LootableContainer, FilingCabinet> _drawerCabinets = new();
        private readonly HashSet<FilingCabinet> _visitedCabinets = new();

        private bool CanHighlightContainer(LootableContainer container)
        {
            return container.gameObject.activeInHierarchy &&
                !_playerTransforms.Contains(container.transform.root) &&
                (!Plugin.HideSearchedContainers.Value || !ContainerHasBeenSearched(container)) &&
                (Plugin.OutlineEmptyContainers.Value || ContainerHasLoot(container));
        }

        private bool TryGetNearestDrawer(FilingCabinet cabinet, out Vector3 nearest)
        {
            nearest = default;
            float bestDistance = float.PositiveInfinity;
            float rangeSquared = _passRange * _passRange;

            foreach (var drawer in cabinet.Drawers)
            {
                if (drawer == null)
                {
                    continue;
                }

                Vector3 position = drawer.transform.position;
                float distance = (position - _passPlayerPos).sqrMagnitude;
                if (distance <= rangeSquared && distance < bestDistance && CanHighlightContainer(drawer))
                {
                    nearest = position;
                    bestDistance = distance;
                }
            }

            return bestDistance < float.PositiveInfinity;
        }

        private FilingCabinet GetFilingCabinet(LootableContainer drawer)
        {
            if (_drawerCabinets.TryGetValue(drawer, out var cached))
            {
                return cached;
            }

            var item = drawer.ItemOwner?.RootItem;
            if (item == null)
            {
                return null;
            }

            FilingCabinet cabinet = null;
            if (item.TemplateId == DrawerTemplateId)
            {
                var parent = drawer.transform.parent;
                for (int level = 0; level < 4 && parent != null; level++, parent = parent.parent)
                {
                    // Avoid walking a room or map hierarchy during cabinet discovery.
                    int remaining = 80;
                    if (!FitsCabinetHierarchy(parent, ref remaining))
                    {
                        break;
                    }

                    var drawers = parent.GetComponentsInChildren<LootableContainer>(true);
                    if (!SameDrawerStack(drawer, drawers))
                    {
                        break;
                    }

                    if ((cabinet == null || drawers.Length > cabinet.Drawers.Length) && HasCabinetHousing(parent, drawers))
                    {
                        cabinet = new FilingCabinet { Root = parent.gameObject, Drawers = drawers };
                        if (drawers.Length > 1)
                        {
                            break;
                        }
                    }
                }
            }

            if (Plugin.DebugLogging.Value && item.TemplateId == DrawerTemplateId)
            {
                string result = cabinet == null ? "using drawer mesh" :
                    $"housing='{cabinet.Root.name}', drawers={cabinet.Drawers.Length}";
                Plugin.LogSource?.LogInfo($"[BreakoutOutline] Filing drawer '{drawer.name}': {result}");
            }

            _drawerCabinets[drawer] = cabinet;
            if (cabinet != null)
            {
                foreach (var member in cabinet.Drawers)
                {
                    _drawerCabinets[member] = cabinet;
                }
            }

            return cabinet;
        }

        private static bool FitsCabinetHierarchy(Transform node, ref int remaining)
        {
            remaining--;
            if (remaining < node.childCount)
            {
                return false;
            }

            for (int i = 0; i < node.childCount; i++)
            {
                if (!FitsCabinetHierarchy(node.GetChild(i), ref remaining))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameDrawerStack(LootableContainer source, LootableContainer[] drawers)
        {
            foreach (var drawer in drawers)
            {
                if (drawer.ItemOwner?.RootItem?.TemplateId != DrawerTemplateId)
                {
                    return false;
                }

                Vector3 offset = drawer.transform.position - source.transform.position;
                if (offset.x * offset.x + offset.z * offset.z > 0.35f * 0.35f)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasCabinetHousing(Transform parent, LootableContainer[] drawers)
        {
            foreach (var renderer in parent.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null)
                {
                    continue;
                }

                bool belongsToDrawer = false;
                foreach (var drawer in drawers)
                {
                    if (renderer.transform.IsChildOf(drawer.transform))
                    {
                        belongsToDrawer = true;
                        break;
                    }
                }

                if (!belongsToDrawer)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
