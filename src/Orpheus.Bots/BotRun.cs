using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;

namespace Orpheus.Bots
{
    /// <summary>Итог одного бота.</summary>
    public sealed class BotStats
    {
        public int Index { get; set; }
        public string State { get; set; } = "";
        public int EntityId { get; set; }
        public string? RejectText { get; set; }
        public string? DisconnectReason { get; set; }
        public double SecondsInGame { get; set; }
        public long SnapshotsApplied { get; set; }
        public long SnapshotsStale { get; set; }
        public long SnapshotsMissed { get; set; }
        public long DeltasApplied { get; set; }
        public long DeltasUnmerged { get; set; }
        public double SnapshotsPerSecond { get; set; }
        public long InputsSent { get; set; }
        public long BadMessages { get; set; }
        public int RttMs { get; set; }
        public long SimSent { get; set; }
        public long SimSendLost { get; set; }
        public long SimReceived { get; set; }
        public long SimReceiveLost { get; set; }
        public double SimMeanDelayMs { get; set; }
    }

    /// <summary>Итог запуска ботов.</summary>
    public sealed class BotReport
    {
        public string Server { get; set; } = "";
        public string Script { get; set; } = "";
        public int Count { get; set; }
        public string Simulator { get; set; } = "";
        public double Seconds { get; set; }
        public int InGame { get; set; }
        public int Rejected { get; set; }

        /// <summary>Отключений после входа (не по нашей воле) и неудачных подключений.</summary>
        public int Disconnects { get; set; }

        public List<BotStats> Bots { get; } = new List<BotStats>();

        /// <summary>0 — все вошли и ни одного отключения; 1 — иначе.</summary>
        public int ExitCode => Disconnects == 0 && Rejected == 0 && InGame == Count ? 0 : 1;
    }

    /// <summary>
    /// Боты по сети (docs/07, 11.3): N клиентов в одном потоке, кадр 60 Гц на таймере высокого разрешения
    /// (<see cref="TickClock"/> сервера), у каждого — свой имитатор связи (2.10) со своим зерном.
    /// </summary>
    public static class BotRun
    {
        /// <summary>Кадров ввода в секунду (docs/07, 2.4).</summary>
        public const int FrameRate = 60;

        public static BotReport Run(BotOptions o, TextWriter? console, CancellationToken stop = default)
        {
            var bots = new NetBot[o.Count];
            var sims = new NetSimTransport?[o.Count];
            var entered = new double[o.Count];
            for (int i = 0; i < o.Count; i++)
            {
                ITransport t = LiteNetTransport.Connect(o.Host, o.Port);
                int serverPeer = ((LiteNetTransport)t).ServerPeer;
                NetSimSettings s = o.SimFor(i);
                if (s.IsActive)
                {
                    sims[i] = new NetSimTransport(t, s);
                    t = sims[i]!;
                }

                var client = new NetClient(t, serverPeer, $"{o.Name}{i}", o.Version);
                int index = i;
                if (o.OnSnapshot != null)
                {
                    client.SnapshotApplied = snap => o.OnSnapshot(index, snap, client.EntityId);
                }

                bots[i] = new NetBot(client, o.Script, i);
                entered[i] = -1;
            }

            console?.WriteLine($"боты: {o.Count} × {o.Script} → {o.Host}:{o.Port}, {o.Seconds:0.#} с, связь: {(o.SimFor(0).IsActive ? o.SimFor(0).ToString() : "без имитатора")}");
            var watch = Stopwatch.StartNew();
            using (var clock = new TickClock(FrameRate))
            {
                double nextStatus = 10;
                while (watch.Elapsed.TotalSeconds < o.Seconds && !stop.IsCancellationRequested)
                {
                    long now = watch.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                    bool anyAlive = false;
                    for (int i = 0; i < bots.Length; i++)
                    {
                        bots[i].Frame(now);
                        NetClientState st = bots[i].Client.State;
                        if (st == NetClientState.InGame && entered[i] < 0)
                        {
                            entered[i] = watch.Elapsed.TotalSeconds;
                        }

                        anyAlive |= st == NetClientState.Connecting || st == NetClientState.LoggingIn || st == NetClientState.InGame;
                    }

                    if (!anyAlive)
                    {
                        break;
                    }

                    if (console != null && watch.Elapsed.TotalSeconds >= nextStatus)
                    {
                        nextStatus += 10;
                        console.WriteLine($"  {watch.Elapsed.TotalSeconds:0} с: в игре {Count(bots, NetClientState.InGame)} из {bots.Length}, снимков у бота 0 — {bots[0].Client.SnapshotsApplied}");
                    }

                    clock.WaitNext(stop);
                }
            }

            double seconds = watch.Elapsed.TotalSeconds;
            var report = new BotReport
            {
                Server = $"{o.Host}:{o.Port}",
                Script = o.Script,
                Count = o.Count,
                Simulator = o.SimFor(0).IsActive ? o.SimFor(0).ToString() : "нет",
                Seconds = seconds,
            };

            for (int i = 0; i < bots.Length; i++)
            {
                NetClient c = bots[i].Client;
                var st = new BotStats
                {
                    Index = i,
                    State = c.State.ToString(),
                    EntityId = c.EntityId,
                    RejectText = c.RejectText,
                    DisconnectReason = c.DisconnectReason?.ToString(),
                    SecondsInGame = entered[i] >= 0 ? seconds - entered[i] : 0,
                    SnapshotsApplied = c.SnapshotsApplied,
                    SnapshotsStale = c.SnapshotsStale,
                    SnapshotsMissed = c.SnapshotsMissed,
                    DeltasApplied = c.DeltasApplied,
                    DeltasUnmerged = c.DeltasUnmerged,
                    InputsSent = c.InputsSent,
                    BadMessages = c.BadMessages,
                };
                st.SnapshotsPerSecond = st.SecondsInGame > 0 ? st.SnapshotsApplied / st.SecondsInGame : 0;
                if (sims[i] != null)
                {
                    NetSimStats send = sims[i]!.SendStats;
                    NetSimStats recv = sims[i]!.ReceiveStats;
                    st.SimSent = send.Packets;
                    st.SimSendLost = send.Lost;
                    st.SimReceived = recv.Packets;
                    st.SimReceiveLost = recv.Lost;
                    st.SimMeanDelayMs = recv.MeanDelayMs;
                }

                report.Bots.Add(st);
                if (c.State == NetClientState.InGame)
                {
                    report.InGame++;
                }
                else if (c.State == NetClientState.Rejected)
                {
                    report.Rejected++;
                }
                else
                {
                    report.Disconnects++;
                }
            }

            // RTT — до выхода: после отключения транспорт его не знает.
            for (int i = 0; i < bots.Length; i++)
            {
                report.Bots[i].RttMs = RttOf(bots[i], sims[i]);
            }

            Shutdown(bots, watch);
            if (o.ReportPath != null)
            {
                WriteReport(report, o.ReportPath);
            }

            console?.WriteLine($"итог: в игре {report.InGame} из {report.Count}, отказов {report.Rejected}, отключений {report.Disconnects}, " +
                               $"снимков/с у бота 0 — {(report.Bots.Count > 0 ? report.Bots[0].SnapshotsPerSecond : 0):0.0}");
            for (int i = 0; i < report.Bots.Count; i++)
            {
                if (report.Bots[i].RejectText != null)
                {
                    console?.WriteLine($"  бот {i}: {report.Bots[i].RejectText}");
                }
            }

            return report;
        }

        private static int RttOf(NetBot bot, NetSimTransport? sim) => bot.Client.Transport.RoundTripMs(bot.Client.ServerPeer);

        private static int Count(NetBot[] bots, NetClientState state)
        {
            int n = 0;
            foreach (NetBot b in bots)
            {
                if (b.Client.State == state)
                {
                    n++;
                }
            }

            return n;
        }

        private static void Shutdown(NetBot[] bots, Stopwatch watch)
        {
            foreach (NetBot b in bots)
            {
                if (b.Client.State == NetClientState.InGame)
                {
                    b.Client.Disconnect();
                }
            }

            // Дать отключениям уйти, затем закрыть сокеты.
            var until = watch.Elapsed.TotalSeconds + 0.2;
            while (watch.Elapsed.TotalSeconds < until)
            {
                foreach (NetBot b in bots)
                {
                    b.Client.Poll(watch.Elapsed.Ticks / 10);
                }

                Thread.Sleep(10);
            }

            foreach (NetBot b in bots)
            {
                b.Client.Transport.Dispose();
            }
        }

        private static void WriteReport(BotReport r, string path)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var w = new Utf8JsonWriter(f, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            w.WriteStartObject();
            w.WriteString("server", r.Server);
            w.WriteString("script", r.Script);
            w.WriteNumber("count", r.Count);
            w.WriteString("simulator", r.Simulator);
            w.WriteNumber("seconds", Math.Round(r.Seconds, 3));
            w.WriteNumber("inGame", r.InGame);
            w.WriteNumber("rejected", r.Rejected);
            w.WriteNumber("disconnects", r.Disconnects);
            w.WriteNumber("exitCode", r.ExitCode);
            w.WriteStartArray("bots");
            foreach (BotStats b in r.Bots)
            {
                w.WriteStartObject();
                w.WriteNumber("index", b.Index);
                w.WriteString("state", b.State);
                w.WriteNumber("entityId", b.EntityId);
                if (b.RejectText != null)
                {
                    w.WriteString("reject", b.RejectText);
                }

                if (b.DisconnectReason != null)
                {
                    w.WriteString("disconnectReason", b.DisconnectReason);
                }

                w.WriteNumber("secondsInGame", Math.Round(b.SecondsInGame, 3));
                w.WriteNumber("snapshotsApplied", b.SnapshotsApplied);
                w.WriteNumber("snapshotsStale", b.SnapshotsStale);
                w.WriteNumber("snapshotsMissed", b.SnapshotsMissed);
                w.WriteNumber("deltasApplied", b.DeltasApplied);
                w.WriteNumber("deltasUnmerged", b.DeltasUnmerged);
                w.WriteNumber("snapshotsPerSecond", Math.Round(b.SnapshotsPerSecond, 2));
                w.WriteNumber("inputsSent", b.InputsSent);
                w.WriteNumber("badMessages", b.BadMessages);
                w.WriteNumber("rttMs", b.RttMs);
                w.WriteNumber("simSent", b.SimSent);
                w.WriteNumber("simSendLost", b.SimSendLost);
                w.WriteNumber("simReceived", b.SimReceived);
                w.WriteNumber("simReceiveLost", b.SimReceiveLost);
                w.WriteNumber("simMeanDelayMs", Math.Round(b.SimMeanDelayMs, 2));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }
    }
}
