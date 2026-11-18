using System;
using System.Collections.Generic;
using System.Linq;

namespace ConsoleIntegration
{
    public abstract class Status
    {
        public string Data { get; set; }
        public abstract void Update(float deltaTime);
        public abstract bool IsExpired();
    }

    public class TimeBasedStatus : Status
    {
        public float Duration { get; }
        private float elapsedTime = 0f;

        public TimeBasedStatus(float duration)
        {
            Duration = duration;
        }

        public override void Update(float deltaTime)
        {
            elapsedTime += deltaTime;
        }

        public override bool IsExpired() => elapsedTime >= Duration;
    }

    public class CounterBasedStatus : Status
    {
        public int Count { get; private set; }

        public CounterBasedStatus(int count)
        {
            Count = count;
        }

        public void Decrement() => Count = Math.Max(0, Count - 1);
        public void Add(int amount) => Count += amount;

        public override void Update(float deltaTime) { }

        public override bool IsExpired() => Count <= 0;
    }

    public class StatusManager
    {
        private readonly Dictionary<string, List<Status>> statuses = new();

        public void AddStatus(string identifier, Status status)
        {
            if (!statuses.ContainsKey(identifier))
                statuses[identifier] = new List<Status>();
            statuses[identifier].Add(status);
        }

        public bool HasStatus(string identifier) =>
            statuses.TryGetValue(identifier, out var list) && list.Count > 0;

        public void RemoveStatus(string identifier)
        {
            if (statuses.TryGetValue(identifier, out var list) && list.Count > 0)
                list.RemoveAt(0);
        }

        public Status GetStatus(string identifier)
        {
            if (statuses.TryGetValue(identifier, out var list) && list.Count > 0)
                return list[0];
            return null;
        }

        public void Update(float deltaTime)
        {
            foreach (var key in new List<string>(statuses.Keys))
            {
                var list = statuses[key];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var status = list[i];
                    status?.Update(deltaTime);
                    if (status?.IsExpired() == true)
                        list.RemoveAt(i);
                }
                if (list.Count == 0)
                    statuses.Remove(key);
            }
        }

        public List<(string Key, Status Status)> GetStatusesByPrefix(string prefix)
        {
            var result = new List<(string, Status)>();
            foreach (var pair in statuses)
            {
                if (pair.Key.StartsWith(prefix))
                {
                    foreach (var status in pair.Value)
                    {
                        result.Add((pair.Key, status));
                    }
                }
            }
            return result;
        }

        public int GetTotalStatusCount()
        {
            int count = 0;
            foreach (var list in statuses.Values)
            {
                count += list.Count;
            }
            return count;
        }

        public List<string> GetActiveIdentifiers() =>
            new List<string>(statuses.Where(kvp => kvp.Value.Count > 0).Select(kvp => kvp.Key));
    }
}