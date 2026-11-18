using System;
using System.Collections.Concurrent;
using MelonLoader;
using ConsoleIntegration;
using UnityEngine;

[assembly: MelonInfo(typeof(BGSHConsoleIntegration.Core), "BGSHConsoleIntegration", "1.0.0", "Kirik", null)]
[assembly: MelonGame("Alt Shift", "Battlestar Galactica Scattered Hopes")]

namespace BGSHConsoleIntegration
{
    public class Core : MelonMod
    {
        private static ConsoleConnector connector;
        private static StatusManager statusManager;
        private static readonly ConcurrentQueue<string> incoming = new();
        private static long lastContextFingerprint = long.MinValue;
        private static string lastContextJson;
        private static float tContextCheck;
        private const float ContextCheckInterval = 0.25f;

        private sealed class ActionLogger : IConsoleLogger
        {
            private readonly Action<string> msg;
            private readonly Action<string> warning;
            private readonly Action<string> error;

            public ActionLogger(Action<string> msg, Action<string> warning, Action<string> error)
            {
                this.msg = msg;
                this.warning = warning;
                this.error = error;
            }

            public void Msg(string m) => msg?.Invoke(m);
            public void Warning(string m) => warning?.Invoke(m);
            public void Error(string m) => error?.Invoke(m);
        }

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initialized.");
        }

        public override void OnApplicationStart()
        {
            statusManager = new StatusManager();

            connector = new ConsoleConnector(logger: new ActionLogger(
                m => LoggerInstance.Msg(m),
                m => LoggerInstance.Warning(m),
                m => LoggerInstance.Error(m)));

            connector.OnMessageReceived += msg => incoming.Enqueue(msg);
            connector.Start();
        }

        // Главный поток: исполняем сообщения из консоли здесь
        public override void OnUpdate()
        {
            while (incoming.TryDequeue(out string json))
            {
                try
                {
                    string result = EventsProcessor.ProcessEvent(json, statusManager);
                    connector.SendResponse(result);
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error($"Event processing failed: {ex}");
                }
            }
            statusManager.Update(Time.deltaTime);
            TickVipContext();
        }

        private static void TickVipContext()
        {
            tContextCheck += Time.unscaledDeltaTime;
            if (tContextCheck < ContextCheckInterval) return;
            tContextCheck = 0f;

            long fp = VipContext.Fingerprint();
            if (fp == lastContextFingerprint) return;
            lastContextFingerprint = fp;

            string json = VipContext.BuildJson();
            if (json == lastContextJson) return;   // фликер fingerprint без смены содержимого → не шлём
            lastContextJson = json;
            connector.SetContext(json);
        }

        public override void OnApplicationQuit()
        {
            connector?.Stop();
        }
    }
}