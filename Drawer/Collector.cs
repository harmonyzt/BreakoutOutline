using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
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

        private static bool IsOwnedByAnyPlayer(LootItem li, HashSet<Transform> playerTransforms, HashSet<string> equippedItemIds)
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

        // Compound-item detection via "Slots"/"Grids" properties (avoids a direct CompoundItem reference); cached per Type.
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
    }
}