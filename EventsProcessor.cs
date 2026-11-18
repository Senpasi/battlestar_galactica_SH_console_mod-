using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using AltShift.Exodus.GameStates;
using AltShift.Exodus.Outcomes;
using AltShift.Exodus.VIP;
using AltShift.Exodus.Config;
using AltShift.Maths.Randomization;
using UnityEngine;

namespace ConsoleIntegration
{
    public static class EventsProcessor
    {
        public static string ProcessEvent(string jsonString, StatusManager statusManager)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonString))
                    return "Missing JSON input.";

                JObject jsonData = JObject.Parse(jsonString);
                if (!jsonData.TryGetValue("event_id", out var eventIdObj) || eventIdObj == null)
                    return "Missing 'event_id'.";

                string eventId = eventIdObj.ToString();

                return eventId switch
                {
                    "add_resource" => AddResource(jsonData),
                    "vip_xp"       => VipXp(jsonData),
                    "vip_rename"   => VipRename(jsonData),
                    _ => $"Unknown event received: {eventId}"
                };
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                string where = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim();
                return $"Failed to process event. {ex.GetType().Name}: {ex.Message}" +
                       (string.IsNullOrEmpty(where) ? "" : $" [{where}]");
            }
        }

        private static string AddResource(JObject data)
        {
            // { "event_id":"add_resource", "resource":"scrap", "amount":10 }
            string resourceName = data.TryGetValue("resource", out var r) ? r.ToString() : null;
            if (!TryResolveResource(resourceName, out OutcomeItemType type))
                return $"Unknown resource '{resourceName}'. Use scrap|supplies|fuel|nuke|fleetHealth.";

            if (!data.TryGetValue("amount", out var a) || !int.TryParse(a.ToString(), out int amount))
                return "Missing or invalid 'amount'.";

            if (!GameState.HasRunState)
                return "No active run. Start a run before applying events.";

            var outcome = new Outcome(type, amount, OutcomeReceiver.None, OutcomeItemQtyClass.M_POS);
            outcome.TryIdentifyCategory();

            var randomizer = SeededRandomizer.Create();
            // prepare:false и applySituations:false — тихое применение, без UI-фидбека
            outcome.ApplyOutcome(randomizer, null,
                _applySituations: false, _applyNonSituations: true, _prepare: false);

            return $"+{amount} {resourceName}";
        }

        private static bool TryResolveResource(string name, out OutcomeItemType type)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "scrap": type = OutcomeItemType.Scrap; return true;
                case "supplies": type = OutcomeItemType.Supplies; return true;
                case "fuel": type = OutcomeItemType.Fuel; return true;
                case "nuke": type = OutcomeItemType.Nuke; return true;
                case "fleethealth": case "health": type = OutcomeItemType.FleetHealth; return true;
                default: type = OutcomeItemType.None; return false;
            }
        }
        
        private static readonly MethodInfo FirstNameSetter =
            typeof(VIPState).GetProperty("FirstName", BindingFlags.Instance | BindingFlags.Public)?.GetSetMethod(true);
        private static readonly MethodInfo LastNameSetter =
            typeof(VIPState).GetProperty("LastName", BindingFlags.Instance | BindingFlags.Public)?.GetSetMethod(true);
        private static readonly MethodInfo NicknameSetter =
            typeof(VIPState).GetProperty("Nickname", BindingFlags.Instance | BindingFlags.Public)?.GetSetMethod(true);
            
        private static readonly FieldInfo CompleteNameCacheField =
            typeof(VIPState).GetField("completeName", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FullNameCacheField =
            typeof(VIPState).GetField("fullName", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void RenameVip(VIPState v, string first, string last, string nickname)
        {
            if (first != null) FirstNameSetter.Invoke(v, new object[] { first.Trim() });
            if (last  != null) LastNameSetter.Invoke(v, new object[] { last.Trim() });
            if (nickname != null)
                NicknameSetter.Invoke(v, new object[] { string.IsNullOrEmpty(nickname) ? null : nickname.Trim() });
            // Принудительно инвалидируем кэш имён (SetNickNameKey не зовём, чтобы не зетереть кастомник).
            CompleteNameCacheField.SetValue(v, null);
            FullNameCacheField.SetValue(v, null);
            v.RefreshTags();
        }
        
        private static string VipRename(JObject data)
        {
            if (!GameState.HasRunState) return "No active run. Start a run before applying VIP events.";
            if (!ResolveTargets(data, out var targets, out var err)) return err;

            bool hasFirst = data.TryGetValue("first", out var f);
            bool hasLast  = data.TryGetValue("last",  out var l);
            bool hasNick  = data.TryGetValue("nickname", out var n);
            if (!hasFirst && !hasLast && !hasNick)
                return "Nothing to rename. Provide 'first', 'last' and/or 'nickname' (\"\" to clear nickname).";

            var lines = new List<string>();
            foreach (var v in targets)
            {
                string before = Describe(v);
                RenameVip(v,
                    hasFirst ? f?.ToString() : null,
                    hasLast  ? l?.ToString() : null,
                    hasNick  ? n?.ToString() : null);
                GameState.ScheduleRunStateSave();

                string note = v.Group == VipGroup.Static || RunState.Current.GetAuthoredVipCompleteName(v) != null
                    ? "  [authored/static name may override display]" : "";
                lines.Add($"{before}  ->  {Describe(v)}{note}");
            }
            return string.Join("\n", lines);
        }

        private static string VipXp(JObject data)
        {
            string action = data.TryGetValue("action", out var a) ? a?.ToString()?.Trim()?.ToLowerInvariant() : null;
            return action switch
            {
                null or "list"        => ListVips(),
                "inspect"             => InspectVips(data),
                "reset"               => ResetVips(data),
                "redistribute"        => RedistributeVips(data),
                "add"                 => AddVipXp(data),
                _ => $"Unknown vip_xp action '{action}'. Use list|inspect|reset|redistribute|add."
            };
        }

        // { "event_id":"vip_xp", "action":"list" }
        private static string ListVips()
        {
            if (!GameState.HasRunState) return "No active run. Start a run before applying VIP events.";
            var alive = RunState.Current.VIPStateList.Where(v => !v.IsDead).ToList();
            if (alive.Count == 0) return "No living VIPs in the roster.";
            var sb = new StringBuilder();
            sb.Append($"Roster ({alive.Count} alive):");
            foreach (var v in alive) sb.Append('\n').Append(Describe(v));
            return sb.ToString();
        }

        // { "event_id":"vip_xp", "action":"inspect", "target":"Adama" }
        private static string InspectVips(JObject data)
        {
            if (!ResolveTargets(data, out var targets, out var err)) return err;
            return string.Join("\n", targets.Select(Describe));
        }

        // { "event_id":"vip_xp", "action":"reset", "target":"all" }
        private static string ResetVips(JObject data)
        {
            if (!ResolveTargets(data, out var targets, out var err)) return err;
            var lines = new List<string>();
            foreach (var v in targets)
            {
                string before = Describe(v);
                v.ResetProgression();
                v.shouldClearCache = true;
                GameState.ScheduleRunStateSave();
                lines.Add($"{before}  ->  {Describe(v)}  [reset, HP refilled]");
            }
            return string.Join("\n", lines);
        }

        // { "event_id":"vip_xp", "action":"redistribute", "target":"3" }
        private static string RedistributeVips(JObject data)
        {
            if (!ResolveTargets(data, out var targets, out var err)) return err;
            var lines = new List<string>();
            foreach (var v in targets)
            {
                int oldXp       = v.CurrentXp;
                int perksBefore = v.TraitList.Count;
                string before   = Describe(v);

                v.ResetProgression();
                if (oldXp > 0) SetXpRaw(v, oldXp);
                v.shouldClearCache = true;
                GameState.ScheduleRunStateSave();

                int stripped  = Math.Max(0, perksBefore - v.TraitList.Count);
                int claimable = v.ComputeExpectedLevel() - 1; // сколько уровней/перков можно взять с L1
                lines.Add($"{before}  ->  {Describe(v)}  [refunded {oldXp} xp raw, stripped {stripped} perk(s), claimable {claimable}]");
            }
            return string.Join("\n", lines);
        }

        // { "event_id":"vip_xp", "action":"add", "target":"all", "amount":25 }
        private static string AddVipXp(JObject data)
        {
            if (!TryGetInt(data, "amount", out int amount)) return "Missing or invalid 'amount'.";
            if (amount <= 0) return "'amount' must be positive (use reset/redistribute to take xp back).";

            if (!ResolveTargets(data, out var targets, out var err)) return err;
            var lines = new List<string>();
            foreach (var v in targets)
            {
                string before = Describe(v);
                v.ChangeXp(amount);
                GameState.ScheduleRunStateSave();
                string flag = v.CanLevelUp ? $" [can level up to {v.ComputeExpectedLevel()}]" : "";
                lines.Add($"{before}  ->  {Describe(v)}{flag}");
            }
            return string.Join("\n", lines);
        }

        private static readonly MethodInfo CurrentXpSetter =
            typeof(VIPState).GetProperty("CurrentXp", BindingFlags.Instance | BindingFlags.Public)
                            ?.GetSetMethod(nonPublic: true);

        private static void SetXpRaw(VIPState v, int xp)
        {
            if (CurrentXpSetter == null)
                throw new InvalidOperationException("VIPState.CurrentXp setter not found via reflection");
            CurrentXpSetter.Invoke(v, new object[] { Math.Max(0, xp) });
            v.RefreshTags();
        }

        private static bool TryGetInt(JObject data, string key, out int value)
        {
            value = 0;
            if (!data.TryGetValue(key, out var t) || t == null) return false;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            { value = (int)Math.Round((double)t); return true; }
            return int.TryParse(t.ToString(), out value);
        }

        private static bool ResolveTargets(JObject data, out List<VIPState> targets, out string error)
        {
            targets = new List<VIPState>();
            error = null;

            if (!GameState.HasRunState)
            {
                error = "No active run. Start a run before applying VIP events.";
                return false;
            }

            if (!data.TryGetValue("target", out var t) || t == null)
            {
                error = "Missing 'target'. Use 'all', a VIP id (#N), or a name substring. " +
                        "Send {\"event_id\":\"vip_xp\",\"action\":\"list\"} to see the roster.";
                return false;
            }

            var alive = RunState.Current.VIPStateList.Where(v => !v.IsDead).ToList();
            string raw = t.ToString()?.Trim();

            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                int id = (int)Math.Round((double)t);
                var hit = alive.FirstOrDefault(v => v.HumanReadableId == id);
                if (hit == null) { error = $"No living VIP with id #{id}."; return false; }
                targets.Add(hit);
            }
            else if (raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                targets = alive;
            }
            else if (int.TryParse(raw, out int id2))
            {
                var hit = alive.FirstOrDefault(v => v.HumanReadableId == id2);
                if (hit == null) { error = $"No living VIP with id #{id2}."; return false; }
                targets.Add(hit);
            }
            else
            {
                string needle = raw.ToLowerInvariant();
                targets = alive.Where(v =>
                    (v.CompleteName ?? "").ToLowerInvariant().Contains(needle) ||
                    (v.FirstName    ?? "").ToLowerInvariant().Contains(needle) ||
                    (v.LastName     ?? "").ToLowerInvariant().Contains(needle) ||
                    (v.Nickname     ?? "").ToLowerInvariant().Contains(needle)).ToList();
                if (targets.Count == 0) { error = $"No VIP matches '{raw}'. Use 'list' to see the roster."; return false; }
            }

            if (targets.Count == 0) { error = "No matching living VIP."; return false; }
            return true;
        }

        private static string Describe(VIPState v)
        {
            string name = v.CompleteName;
            if (string.IsNullOrWhiteSpace(name)) name = v.UsageName;
            if (string.IsNullOrWhiteSpace(name)) name = v.Id;

            (int lo, int hi) = XpBounds(v.CurrentLevel);
            int xp = v.CurrentXp;
            string pct = (hi == int.MaxValue)
                ? "MAX"
                : ((int)Mathf.Round(Mathf.Clamp01((xp - lo) / (float)(hi - lo)) * 100f)) + "%";

            return $"#{v.HumanReadableId} {name.PadRight(16)} L{v.CurrentLevel}/{v.MaxVipLevel}  " +
                   $"XP {xp}[{pct}]  perks:{v.TraitList.Count}{(v.CanLevelUp ? "  UP" : "")}";
        }

        // Пороги уровней: чтобы стоять на L>=2 нужно CurrentXp >= th[L-2]; th[L-1] верхняя граница текущего уровня.
        private static (int lo, int hi) XpBounds(int level)
        {
            var th = BalancingConfiguration.Me.VipLevelThresholds;   // IReadOnlyList<int>
            int lo = level > 1 ? th[level - 2] : 0;
            int hi = (level - 1) < th.Count ? th[level - 1] : int.MaxValue;
            return (lo, hi);
        }
    }
}