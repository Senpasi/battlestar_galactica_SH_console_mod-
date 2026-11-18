using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleIntegration
{

    public class NullLogger : IConsoleLogger
    {
        public void Msg(string msg) { }
        public void Warning(string msg) { }
        public void Error(string msg) { }
    }

    public class ConsoleConnector : IDisposable
    {
        private const string DEFAULT_READ_PIPE = "game-event-pipe";
        private const string DEFAULT_WRITE_PIPE = "game-event-pipe-response";
        private const int MAX_QUEUE_SIZE = 100;

        private readonly string readPipeName;
        private readonly string writePipeName;

        private Thread readThread;
        private Thread writeThread;
        private volatile bool isRunning = false;

        private CancellationTokenSource cts = new();
        private volatile string pendingContext;
        private volatile bool contextDirty;
        private volatile string lastSentContext;
        private volatile bool contextPaused;
        private int contextStreak;
        private DateTime lastContextProbeUtc;
        private const int ContextProbeBurst = 3;
        private const float ContextHeartbeatSec = 8f;

        private volatile NamedPipeServerStream server;

        private readonly ConcurrentQueue<string> sendQueue = new();
        private readonly ConcurrentQueue<string> receiveQueue = new();

        private readonly IConsoleLogger logger;

        public event Action<string> OnMessageReceived;

        public ConsoleConnector(string readPipe = DEFAULT_READ_PIPE, string writePipe = DEFAULT_WRITE_PIPE, IConsoleLogger logger = null)
        {
            this.readPipeName = readPipe;
            this.writePipeName = writePipe;
            this.logger = logger ?? new NullLogger();
        }

        public void Start()
        {
            if (isRunning) return;
            isRunning = true;

            readThread = new Thread(ReadLoop) { IsBackground = true };
            writeThread = new Thread(WriteLoop) { IsBackground = true };

            readThread.Start();
            writeThread.Start();

            logger.Msg($"ConsoleConnector started. Listening on \\\\.\\pipe\\{readPipeName}");
        }

        private void ReadLoop()
        {
            while (isRunning)
            {
                var srv = new NamedPipeServerStream(
                    readPipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                server = srv;

                try
                {
                    var task = srv.WaitForConnectionAsync(cts.Token);
                    while (!task.IsCompleted && isRunning)
                    {
                        Thread.Sleep(10);
                    }

                    if (!isRunning)
                        break;

                    if (srv.IsConnected)
                    {
                        var buffer = new byte[65536];
                        using var ms = new MemoryStream();

                        while (isRunning && srv.IsConnected)
                        {
                            int bytesRead;
                            try
                            {
                                bytesRead = srv.Read(buffer, 0, buffer.Length);
                            }
                            catch (IOException)
                            {
                                break;
                            }

                            if (bytesRead == 0) continue;

                            ms.Write(buffer, 0, bytesRead);

                            if (srv.CanRead && srv.ReadByte() == -1)
                            {
                                var data = ms.ToArray();
                                ms.SetLength(0);

                                string message = System.Text.Encoding.UTF8.GetString(data).Trim();
                                if (!string.IsNullOrEmpty(message))
                                {
                                    if (receiveQueue.Count >= MAX_QUEUE_SIZE)
                                        receiveQueue.TryDequeue(out _);

                                    receiveQueue.Enqueue(message);
                                    OnMessageReceived?.Invoke(message);
                                    logger.Msg($"Received: {message}");
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.Msg("Pipe connection wait cancelled.");
                }
                catch (ThreadAbortException)
                {
                    // Не глотаем abort — выходим из потока.
                    isRunning = false;
                }
                catch (IOException ex) when (isRunning)
                {
                    logger.Error($"Pipe IO error: {ex.Message}");
                }
                catch (Exception ex) when (isRunning)
                {
                    logger.Error($"Unexpected error in ReadLoop: {ex.Message}");
                }
                finally
                {
                    try { srv.Dispose(); } catch { }
                    server = null;
                }

                if (isRunning)
                    Thread.Sleep(100);
            }
        }

        // Есть ли на другом конце запущенный pipe-сервер (т.е. консоль).
        public bool IsRemoteListening
        {
            get
            {
                try { return File.Exists(@"\\.\pipe\" + writePipeName); }
                catch { return true; } // если проверка не удалась — ведём себя как раньше, но с backoff
            }
        }

        public int QueueDepth => sendQueue.Count;

        public void SetContext(string json) { pendingContext = json; contextDirty = true; }

        private void WriteLoop()
        {
            bool online = false;
            int replyFailures = 0;
            DateTime retryAt = DateTime.MinValue;
            const int connectTimeoutMs = 300;

            while (isRunning)
            {
                bool hasReply = sendQueue.TryPeek(out string reply);
                bool wantContext = contextDirty
                    && !string.IsNullOrEmpty(pendingContext)
                    && pendingContext != lastSentContext
                    && !contextPaused;

                if (!hasReply && !wantContext) { Thread.Sleep(25); continue; }
                if (!isRunning) break;

                // Когда в паузе — раз и в ContextHeartbeatSec отпускаем одну пробу (чтобы контекст
                // появился сам, если консоль стартует позже игры). Heartbeat=0 → пауза до первого reply.
                if (contextPaused && !hasReply)
                {
                    if (ContextHeartbeatSec <= 0f) { Thread.Sleep(100); continue; }
                    if ((DateTime.UtcNow - lastContextProbeUtc).TotalSeconds < ContextHeartbeatSec) { Thread.Sleep(50); continue; }
                    contextPaused = false;   // одна репроба
                    contextStreak = 0;
                    lastContextProbeUtc = DateTime.UtcNow;
                }

                if (DateTime.UtcNow < retryAt) { Thread.Sleep(50); continue; }

                try
                {
                    using var client = new NamedPipeClientStream(".", writePipeName, PipeDirection.Out);
                    client.Connect(connectTimeoutMs);
                    using var writer = new StreamWriter(client) { AutoFlush = true };

                    if (hasReply) writer.WriteLine(reply);
                    else writer.WriteLine(pendingContext);

                    if (hasReply) sendQueue.TryDequeue(out _);
                    else { contextDirty = false; lastSentContext = pendingContext; }

                    // Успех по любому каналу = консоль жива → снимаем паузу, доставим остаток контекста.
                    contextPaused = false;
                    contextStreak = 0;
                    replyFailures = 0;
                    if (!online)
                    {
                        online = true;
                        logger.Msg($"Console pipe '{writePipeName}' connected; resyncing context.");
                    }
                }
                catch (ThreadAbortException) { isRunning = false; }
                catch (Exception ex) when (ex is TimeoutException || ex is IOException)
                {
                    if (hasReply)
                    {
                        // Ответ = штучный; ретраим его активно, но не спамом.
                        if (++replyFailures == 1 && online)
                            logger.Warning($"Reply send failed, retrying: {ex.Message}");
                        retryAt = DateTime.UtcNow.AddMilliseconds(
                            Math.Min(1500, 300 * (1 << Math.Min(replyFailures, 3))));
                    }
                    else
                    {
                        // Контекст «бьём» коротким burst, потом пауза до heartbeat/reply.
                        if (++contextStreak >= ContextProbeBurst)
                        {
                            contextPaused = true;
                            online = false;
                            if (ContextHeartbeatSec <= 0f)
                                logger.Msg($"Console not ready; context held until next console action.");
                        }
                        retryAt = DateTime.UtcNow.AddMilliseconds(
                            Math.Min(800, 200 * (1 << Math.Min(contextStreak, 2))));
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"WriteLoop: {ex.Message}");
                    retryAt = DateTime.UtcNow.AddSeconds(2);
                }
            }
        }

        public void SendResponse(string message)
        {
            if (sendQueue.Count >= MAX_QUEUE_SIZE)
                sendQueue.TryDequeue(out _);
            sendQueue.Enqueue(message);
        }

        public void Stop()
        {
            if (!isRunning) return;
            isRunning = false;
            logger.Msg("ConsoleConnector stopping...");

            try { cts.Cancel(); } catch { }

            // Разрываем сервер, чтобы выбить Read/WaitForConnectionAsync.
            try { server?.Disconnect(); } catch { }
            try { server?.Dispose(); } catch { }

            // Join с таймаутом — не зависнем вечно, если поток всё же залип.
            try { readThread?.Join(2000); } catch { }
            try { writeThread?.Join(2000); } catch { }
        }

        public bool TryReceiveMessage(out string message) => receiveQueue.TryDequeue(out message);

        public void Dispose()
        {
            Stop();
        }
    }
}