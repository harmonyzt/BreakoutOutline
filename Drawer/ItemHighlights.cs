using System;
using UnityEngine;

namespace BreakoutOutlines.Drawer
{
    public partial class LootOutlineController
    {
        private QuestCheckmarks _questLookup;
        private int _questCursor;

        private static Color HighlightColor(DrawObject target, Color itemColor, Color containerColor)
        {
            if (target.IsContainer)
            {
                return containerColor;
            }

            var loot = target.LooseLoot;
            if (loot == null)
            {
                return itemColor;
            }

            Color color = itemColor;
            if (loot.NativeQuestItem || loot.QuestNeeded)
            {
                color = Plugin.QuestColor.Value;
            }
            else if (loot.Wishlisted)
            {
                color = Plugin.WishlistColor.Value;
            }

            color.a = itemColor.a;
            return color;
        }

        private void RefreshQuestHighlight()
        {
            if (_questLookup == null || _drawObjects.Count == 0)
            {
                return;
            }

            if (_questCursor >= _drawObjects.Count)
            {
                _questCursor = 0;
            }

            var loot = _drawObjects[_questCursor++].LooseLoot;
            float now = Time.unscaledTime;
            if (loot == null || loot.NativeQuestItem || now < loot.NextQuestCheck)
            {
                return;
            }

            loot.NextQuestCheck = now + 5f;
            try
            {
                loot.QuestNeeded = _questLookup.IsMarked(_gameWorld.MainPlayer.Profile, loot.LooseItem, now);
            }
            catch (Exception ex)
            {
                loot.QuestNeeded = false;
                
                // Retry on the next refresh.
                _questLookup.LogFailureOnce(ex);
            }
        }
    }
}
