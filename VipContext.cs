using System.Collections.Generic;
using System.Linq;
using AltShift.Exodus.GameStates;
using AltShift.Exodus.VIP;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConsoleIntegration
{
    public static class VipContext
    {
        // Fingerprint ростера: то, что реально видит консоль (id -> имя, только живые).
        public static long Fingerprint()
        {
            if (!GameState.HasRunState) return -1;
            unchecked
            {
                long h = 17;
                foreach (var v in RunState.Current.VIPStateList)
                {
                    if (v.IsDead) continue;
                    h = h * 31 + v.HumanReadableId;
                    h = h * 31 + ((v.FirstName?.GetHashCode() ?? 0) ^ (v.LastName?.GetHashCode() ?? 0));
                    h = h * 31 + (v.Nickname?.GetHashCode() ?? 0);
                }
                return h;
            }
        }

        // ВАЖНО: ToString(Formatting.None) — обязательна одна строка,
        // иначе WriteLine в WriteLoop разобьёт контекст на несколько сообщений.
        public static string BuildJson()
        {
            var vips = new JArray();
            if (GameState.HasRunState)
            {
                foreach (var v in RunState.Current.VIPStateList.Where(v => !v.IsDead).OrderBy(v => v.HumanReadableId))
                    vips.Add(v.CompleteName);
            }
            var root = new JObject {
                    ["action"] = "context",
                    ["vips"] = vips 
                };
            return root.ToString(Formatting.None);
        }
    }
}