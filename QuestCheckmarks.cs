using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;

namespace BreakoutOutlines
{
    // Resolve this once per raid
    internal sealed class QuestCheckmarks
    {
        private const string PluginId = "com.zgfuedkx.allquestscheckmarks";
        private readonly MethodInfo _active, _future, _stash;
        private readonly FieldInfo _condition, _quests, _fir, _nonFir, _stashFir, _stashNonFir;
        private readonly ConfigEntry<bool> _onlyActive, _includeNonFir, _hideFulfilled, _markSquad;
        private readonly SquadCheck _squad;
        private readonly bool _hasFika;
        private readonly HashSet<string> _activeTemplates = new HashSet<string>(StringComparer.Ordinal);
        private float _nextActiveScan;
        private bool _loggedFailure;

        private delegate bool SquadCheck(Item item, out List<string> players);

        public static QuestCheckmarks TryConnect()
        {
            if (!Chainloader.PluginInfos.TryGetValue(PluginId, out var info) || info.Instance == null)
            {
                return null;
            }

            try
            {
                return new QuestCheckmarks(info.Instance.GetType().Assembly);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Optional AQC highlight integration unavailable: {ex.GetBaseException().Message}");
                return null;
            }
        }

        private QuestCheckmarks(Assembly assembly)
        {
            Type Helper(string name) => assembly.GetType("AllQuestsCheckmarks.Helpers." + name, true);
            var helper = Helper("QuestsHelper");
            var current = Helper("QuestsHelper+CurrentQuest");
            var data = Helper("QuestsData+ItemData");
            var counts = Helper("StashHelper+ItemsCount");
            var dictionary = typeof(Dictionary<,>).MakeGenericType(typeof(MongoID), current).MakeByRefType();

            _active = RequireMethod(helper, "GetActiveQuestsWithItem", typeof(Profile), typeof(Item), dictionary, dictionary);
            _future = RequireMethod(helper, "IsNeededForActiveOrFutureQuests", typeof(Item), data.MakeByRefType());
            _stash = RequireMethod(Helper("StashHelper"), "GetItemsInStash", typeof(MongoID));
            _condition = RequireField(current, "Condition");
            _quests = RequireField(data, "Quests");
            _fir = RequireField(data, "Fir");
            _nonFir = RequireField(data, "NonFir");
            _stashFir = RequireField(counts, "Fir");
            _stashNonFir = RequireField(counts, "NonFir");

            var settings = Helper("Settings");
            ConfigEntry<bool> Setting(string name) => (ConfigEntry<bool>)RequireField(settings, name).GetValue(null);
            _onlyActive = Setting("OnlyActiveQuests");
            _includeNonFir = Setting("IncludeNonFir");
            _hideFulfilled = Setting("HideFulfilled");
            _markSquad = Setting("SquadQuests");
            _hasFika = (bool)RequireField(assembly.GetType("AllQuestsCheckmarks.Plugin", true), "isFikaInstalled").GetValue(null);
            var squad = RequireMethod(Helper("SquadQuests"), "IsNeededForSquadMembers",
                typeof(Item), typeof(List<string>).MakeByRefType());
            _squad = (SquadCheck)squad.CreateDelegate(typeof(SquadCheck));
        }

        private static MethodInfo RequireMethod(Type type, string name, params Type[] parameters)
        {
            return type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, parameters, null) ?? throw new MissingMethodException(type.FullName, name);
        }

        private static FieldInfo RequireField(Type type, string name)
        {
            return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new MissingFieldException(type.FullName, name);
        }

        private void RefreshActiveTemplates(Profile profile, float now)
        {
            if (now < _nextActiveScan)
            {
                return;
            }

            _activeTemplates.Clear();
            foreach (var quest in profile.QuestsData)
            {
                if (quest.Template == null || (quest.Status != EQuestStatus.Started && quest.Status != EQuestStatus.AvailableForFinish))
                {
                    continue;
                }

                foreach (var conditions in quest.Template.Conditions.Values)
                {
                    foreach (var condition in conditions)
                    {
                        if (condition is ConditionItem target && target.target != null)
                        {
                            foreach (string id in target.target)
                            {
                                _activeTemplates.Add(id);
                            }
                        }
                    }
                }
            }

            _nextActiveScan = now + 5f;
        }

        public bool IsMarked(Profile profile, Item item, float now)
        {
            if (_hasFika && _markSquad.Value && _squad(item, out _))
            {
                return true;
            }

            RefreshActiveTemplates(profile, now);
            IDictionary active = null, completed = null;
            bool matches = false;
            if (_activeTemplates.Contains(item.StringTemplateId))
            {
                object[] activeArgs = { profile, item, null, null };
                matches = (bool)_active.Invoke(null, activeArgs);
                active = (IDictionary)activeArgs[2];
                completed = (IDictionary)activeArgs[3];
            }

            bool marked = false;
            int needFir = 0, needOther = 0, doneFir = 0, doneOther = 0;

            if (matches)
            {
                foreach (var entry in active.Values)
                {
                    var condition = (ConditionItem)_condition.GetValue(entry);
                    if (!_includeNonFir.Value && !item.QuestItem && !condition.onlyFoundInRaid)
                    {
                        continue;
                    }

                    marked = true;
                    if (!_hideFulfilled.Value)
                    {
                        return true;
                    }

                    if (condition is ConditionHandoverItem && profile.TaskConditionCounters.TryGetValue(condition.id, out var counter))
                    {
                        if (condition.onlyFoundInRaid)
                        {
                            doneFir += counter.Value;
                            needFir += (int)condition.value - counter.Value;
                        }
                        else
                        {
                            doneOther += counter.Value;
                            needOther += (int)condition.value - counter.Value;
                        }
                    }
                }
            }

            if (!_onlyActive.Value)
            {
                object[] futureArgs = { item, null };
                if ((bool)_future.Invoke(null, futureArgs))
                {
                    var data = futureArgs[1];
                    foreach (var key in ((IDictionary)_quests.GetValue(data)).Keys)
                    {
                        if (active == null || (!active.Contains(key) && !completed.Contains(key)))
                        {
                            marked = true;
                            break;
                        }
                    }

                    if (marked && _hideFulfilled.Value)
                    {
                        foreach (var entry in completed?.Values ?? Array.Empty<object>())
                        {
                            var condition = (ConditionItem)_condition.GetValue(entry);
                            if (condition.onlyFoundInRaid)
                            {
                                doneFir += (int)condition.value;
                            }
                            else
                            {
                                doneOther += (int)condition.value;
                            }
                        }

                        needFir = (int)_fir.GetValue(data) - doneFir;
                        needOther = (int)_nonFir.GetValue(data) - doneOther;
                    }
                }
            }

            if (!marked || !_hideFulfilled.Value)
            {
                return marked;
            }

            var stash = _stash.Invoke(null, new object[] { item.TemplateId });
            int spareFir = (int)_stashFir.GetValue(stash) - needFir;
            return spareFir < 0 || (int)_stashNonFir.GetValue(stash) + spareFir < needOther;
        }

        public void LogFailureOnce(Exception ex)
        {
            if (!_loggedFailure)
            {
                Plugin.LogSource.LogWarning($"AQC could not get loose loot: {ex.GetBaseException().Message}");
                _loggedFailure = true;
            }
        }
    }
}
