using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// E5-1 (этап 5, шаг 5): сеть при RTT 300 мс в памяти — Loopback + <see cref="NetSimTransport"/>
    /// (задержка 150 мс в каждую сторону = RTT 300) на транспорте сервера и каждого клиента.
    /// Боты <c>mech_tour</c> (крафт/сундук/лук) и <c>night_camp</c> (рубка, лагерь, ночь, драка с
    /// отбросами); предсключение — как у живых ботов (<see cref="LocalPrediction"/>).
    /// Гейты (первый честный замер RTT 300 — карточка E5-1, «Решения»): без обрывов, все InGame,
    /// maxDev с отбросом ≤ 68+256 unit (формула карточки); без отброса и по >½ плитке — 68/0 у ночного
    /// лагеря (как E4-2 при RTT 0) и 68+256/≤64 у mech_tour (дрейф при ходьбе в препятствие на окне 150 мс).
    /// </summary>
    public class E51RttTests
    {
        private readonly ITestOutputHelper _out;

        public E51RttTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>RTT прогона, мс (задержка в одну сторону — RTT/2).</summary>
        private const int RttMs = 300;

        /// <summary>Порог расхождения предсказания без отброса — 1 плитка (net.md «предсказание», E4-2).</summary>
        private const int PredMaxDevNormUnits = 68;

        /// <summary>Длина отброса при ударе — 4 плитки (Combat.KnockbackLen), запас к порогу.</summary>
        private const int KnockLenUnits = 256;

        /// <summary>
        /// Бюджет сверок >½ плитки за полный тур mech_tour при RTT 300: замер 2026-11-20 — 30 и 22 за 51 000 сверок
        /// (периодический дрейф предсказания при ходьбе в препятствие на окне 150 мс); взят двойной запас (карточка E5-1).
        /// </summary>
        private const long MechOverHalfBudget = 64;

        private const long FrameMicros = 1_000_000 / 60;

        private static NetSimSettings Rtt() => NetSimSettings.FromRtt(RttMs, 0, 0, 0xE51);

        /// <summary>
        /// Стенд, как <see cref="EditTests.EditRig"/> (такт = 2 кадра клиента), но с предсказанием на клиенте:
        /// транспорт сервера и каждого клиента обёрнуты в <see cref="NetSimTransport"/> (RTT 300),
        /// после отправки кадра — <see cref="LocalPrediction.Sent"/>, по новому снимку — <see cref="LocalPrediction.Reconcile"/>
        /// (проводка как у <see cref="NetBot"/>).
        /// </summary>
        private sealed class Rig : IDisposable
        {
            private readonly LoopbackTransport _st;
            private long _frame;
            private readonly Func<int, WorldPos> _spawn;
            private readonly Dictionary<NetClient, uint> _recTick = new Dictionary<NetClient, uint>();
            private readonly Dictionary<NetClient, uint> _lateFrame = new Dictionary<NetClient, uint>();
            private readonly Dictionary<NetClient, LocalPrediction?> _pred = new Dictionary<NetClient, LocalPrediction?>();
            private readonly Dictionary<NetClient, MechTourBot?> _tour = new Dictionary<NetClient, MechTourBot?>();
            private readonly Dictionary<NetClient, NightCampBot?> _camp = new Dictionary<NetClient, NightCampBot?>();

            public Rig(Simulation sim, Func<int, WorldPos> spawn, string script)
            {
                Script = script;
                Sim = sim;
                _spawn = spawn;
                _st = LoopbackTransport.CreateServer(16);
                Server = new NetServer(new NetSimTransport(_st, Rtt()), sim, spawn, 8, NetModule.Version);
            }

            public string Script { get; }
            public Simulation Sim { get; }
            public NetServer Server { get; }
            public List<NetClient> Clients { get; } = new List<NetClient>();
            public long NowMicros => _frame * FrameMicros;
            public int Ticks { get; private set; }

            public NetClient Join()
            {
                NetClient c = new NetClient(new NetSimTransport(LoopbackTransport.ConnectTo(_st), Rtt()),
                    LoopbackTransport.ServerPeer, "c" + Clients.Count, NetModule.Version);
                Clients.Add(c);
                _recTick[c] = uint.MaxValue;
                _lateFrame[c] = uint.MaxValue;
                _pred[c] = null; // создаём на первом снимке — Shape до рукопожатия пустой (как NetBot L204-216)
                _tour[c] = null;
                _camp[c] = null;
                return c;
            }

            public void Run(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    for (int f = 0; f < 2; f++, _frame++)
                    {
                        foreach (NetClient c in Clients)
                        {
                            c.Poll(NowMicros);
                            LocalPrediction? p = _pred[c];
                            if (p == null && c.HasSnapshot && c.Latest.Self.Present)
                            {
                                // Проводка как у NetBot (L204-221): предсказание заводим на первом снимке
                                // (Shape до рукопожатия — заглушка), на стартовой позиции клиента.
                                p = new LocalPrediction(c.Shape, Sim.Solid, _spawn(Clients.IndexOf(c)));
                                _pred[c] = p;
                                if (c.InputsSent > 0)
                                {
                                    // C-003: кадры до снимка уходили без предсказания, в кольце их нет —
                                    // сверку замораживаем, пока снимок не подтвердит последний из них.
                                    _lateFrame[c] = c.Frame;
                                }
                            }

                            if (_lateFrame[c] != uint.MaxValue && c.HasSnapshot && (int)(c.Latest.AckFrame - _lateFrame[c]) >= 0)
                            {
                                _lateFrame[c] = uint.MaxValue;
                            }

                            if (p != null && _lateFrame[c] == uint.MaxValue && c.HasSnapshot && c.Latest.Tick != _recTick[c])
                            {
                                _recTick[c] = c.Latest.Tick;
                                p.Reconcile(c.Latest.AckFrame, c.Latest.Self, NowMicros);
                            }

                            InputFrame input = InputOf(c);
                            c.SendFrame(input);
                            p?.Sent(c.Frame, input);
                        }
                    }

                    Server.Poll(NowMicros);
                    Sim.Step();
                    Server.SendSnapshots();
                    Sim.RefillPools();
                    Ticks++;
                }
            }

            private InputFrame InputOf(NetClient c)
            {
                if (!c.HasSnapshot || !c.Latest.TryFind(c.EntityId, out SnapshotEntity se))
                {
                    return InputFrame.None;
                }

                if (Script == MechTourBot.Name)
                {
                    MechTourBot? bot = _tour[c];
                    if (bot == null)
                    {
                        bot = new MechTourBot(c.Shape, se.Position);
                        _tour[c] = bot;
                        c.EditReplied = bot.OnReply;
                        c.CraftReplied = bot.OnCraftReply;
                        c.DeathSeen = bot.OnDeath;
                    }

                    return bot.Next(c, se.Position, NowMicros);
                }

                NightCampBot? camp = _camp[c];
                if (camp == null)
                {
                    camp = new NightCampBot();
                    _camp[c] = camp;
                    c.DeathSeen = m => camp.OnDeath(m);
                }

                return camp.Next(c);
            }

            public IReadOnlyDictionary<NetClient, LocalPrediction?> Pred => _pred;
            public IEnumerable<MechTourBot> Tours => _tour.Values.Where(t => t != null).Select(t => t!);
            public IEnumerable<NightCampBot> Camps => _camp.Values.Where(t => t != null).Select(t => t!);

            public void Dispose()
            {
                foreach (NetClient c in Clients)
                {
                    c.Transport.Dispose();
                }

                _st.Dispose();
            }
        }

        private static Simulation NewSim()
        {
            // Мир, как в живых прогонах E5-1: сервер `--create --seed 11 --size 1024` (генератор по умолчанию).
            WorldModel world = WorldGenModule.ForKey(WorldGenModule.DefaultKey).Generate(Shape, 11);
            var sim = new Simulation(world, maxEntities: 32);
            sim.DayScale = 30; // как в живых прогонах (--debug-day-scale 30): ночь наступает за прогон
            return sim;
        }

        /// <summary>Форма мира как в живых прогонах (--size 1024).</summary>
        private static readonly MapShape Shape = MapShape.World(1024);

        /// <summary>
        /// Точка входа как у живых прогонов: серверный <see cref="ServerHost.SpawnPoint"/> (центр мира,
        /// смещения по индексу) — в seed-11 мире тайлы (300,200) из E42Soak попадают в глухой биом
        /// (3–5 деревьев, 0 камней), боты голодают и не доходят до сцены (замер 20.11).
        /// </summary>
        private static WorldPos Spawn(int index) => ServerHost.SpawnPoint(Shape, index);

        /// <summary>
        /// Гейты: без обрывов, все InGame, сверки есть; maxDev с отбросом ≤ 68+256 (формула карточки).
        /// Пороги без отброса и по >½ плитке — честные первые замеры RTT 300 (карточка E5-1, «Решения»):
        /// у спокойного сценария (ночной лагерь) — как у RTT 0 (68, overHalf=0); у mech_tour — с учётом
        /// дрейфа предсказания при ходьбе в препятствие на окно 150 мс (замер 256 unit, 27 сверок >½ плитки).
        /// </summary>
        private void AssertPredictionOk(Rig rig, int maxNoKnockUnits, long overHalfBudget)
        {
            Assert.Equal(0, rig.Server.Disconnects);
            Assert.All(rig.Clients, c => Assert.Equal(NetClientState.InGame, c.State));
            foreach ((NetClient c, LocalPrediction? p) in rig.Pred)
            {
                Assert.NotNull(p); // предсказание должно родиться на первом снимке
                _out.WriteLine($"клиент {c.Transport}: сверок {p.Compared}, >½ плитки {p.ComparedOverHalfTile}, " +
                    $"maxDev {p.MaxDevUnits} unit (кадр {p.MaxDevAckFrame}), без отброса {p.MaxDevNoKnockUnits}, " +
                    $"отбросов принято {p.KnockAdopted}, ресинков {p.Resynced}");
                Assert.True(p.Compared > 100, $"мало сверок предсказания: {p.Compared}");
                Assert.True(p.ComparedOverHalfTile <= overHalfBudget,
                    $"сверок >½ плитки {p.ComparedOverHalfTile} > {overHalfBudget}");
                Assert.True(p.MaxDevNoKnockUnits <= maxNoKnockUnits,
                    $"расхождение без отброса {p.MaxDevNoKnockUnits} > {maxNoKnockUnits} (кадр {p.MaxDevAckFrame})");
                Assert.True(p.MaxDevUnits <= PredMaxDevNormUnits + KnockLenUnits,
                    $"расхождение с отбросом {p.MaxDevUnits} > {PredMaxDevNormUnits} + {KnockLenUnits}");
            }
        }

        /// <summary>
        /// RTT 300, боты <c>mech_tour</c>: полный тур (Craft→Сундук→Лук→Стройка→Ночь, TourEndFrame 108 000 кадров),
        /// предсказание и сверки идут всё это время. Факты: добыча и полный цикл сундука у обоих ботов;
        /// верстак/стройка — при RTT 0 в E42Soak (в живом мире seed-11 у спавна ресурсной цепочки нет, [док] карточки).
        /// </summary>
        [Fact]
        public void E51_Rtt300_MechTour_Prediction_Holds()
        {
            using var rig = new Rig(NewSim(), Spawn, MechTourBot.Name);
            rig.Join();
            rig.Join();
            rig.Run(MechTourBot.TourEndFrame / 2 + 300); // полный тур (Craft→Сундук→Лук→Стройка→Ночь): 54 300 тактов
            // Гейты — честный первый замер RTT 300 in-proc (карточка E5-1, «Решения»): maxDev ≤ 68+256
            // (формула карточки); без отброса и overHalf — пороги по замеру. Гейты RTT 0 (E42Soak) не тронуты.
            AssertPredictionOk(rig, PredMaxDevNormUnits + KnockLenUnits, MechOverHalfBudget);
            List<MechTourBot> tours = rig.Tours.ToList();
            Assert.Equal(2, tours.Count);
            Assert.All(tours, b => Assert.True(b.TreesTotal + b.RocksTotal > 0, $"бот ничего не добыл: {b.Summary}"));
            // Факты сценария, достижимые при RTT 300 в живом мире (seed 11, серверный спавн): добыча и полный
            // цикл сундука (постановка, открытие, склад, взятие, закрытие) — edit/крафт/использование через сеть.
            // Верстак/лук/стройка здесь не покрываются: в seed-11 у спавна нет ресурсной цепочки (кустов 0-2,
            // дерево уходит на сундук), а живые прогоны 600 с не доходят до стройки (конец крафта 690 с);
            // верстак покрыт при RTT 0 в E42Soak (PreparedWorld). См. [док] в карточке E5-1.
            Assert.All(tours, b => Assert.True(b.ChestPlaced && b.ChestOpened, $"сундук не отработан: {b.Summary}"));
            _out.WriteLine($"mech_tour RTT {RttMs}: тактов {rig.Ticks}, боты: {string.Join(" | ", tours.Select(b => b.Summary))}");
        }

        /// <summary>
        /// RTT 300, боты <c>night_camp</c>: заготовка, лагерь, ночь и драка (DayScale 30 — ночь на ~720-м такте),
        /// два игровых дня. Удары с отбросом идут через предсказание; обрывов связи нет (на живых прогонах
        /// E5-1 этот сценарий рвался — см. карточку).
        /// </summary>
        [Fact]
        public void E51_Rtt300_NightCamp_No_Disconnect()
        {
            using var rig = new Rig(NewSim(), Spawn, NightCampBot.Name);
            rig.Join();
            rig.Join();
            rig.Run(3000); // 100 с wall-эквивалента: два игровых дня (ночь ~720-й такт, утро ~1440-й)

            AssertPredictionOk(rig, PredMaxDevNormUnits, 0); // как у RTT 0: замер 2026-11-20 уложился
            List<NightCampBot> camps = rig.Camps.ToList();
            Assert.Equal(2, camps.Count);
            Assert.All(camps, c => Assert.True(c.Ticks > 0, "бот не шагал ни одного такта"));
            string[] lines = camps.Select(c => "тактов " + c.Ticks + ", ударов принято " + c.HitsTaken +
                ", мин. здоровье " + c.MinHealthPct + "%, " +
                (c.Alive ? "жив" : "убит на такте " + c.TicksToDeath + " (убийца #" + c.KillerId + ")")).ToArray();
            _out.WriteLine($"night_camp RTT {RttMs}: тактов {rig.Ticks}, боты: {string.Join(" | ", lines)}");
        }
    }
}
