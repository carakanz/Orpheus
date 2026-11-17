using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// C-028: взмах кирки у клиента. <see cref="StartDig(TileCoord)"/> с киркой к пустому или природному тайлу — взмах
    /// (<see cref="DigStrike"/>): <see cref="PickStrike.SwingFrames"/> своих кадров с зажатым копанием, затем удар своим счётом —
    /// тайлы сектора из <see cref="SelfPosition"/> получают удар в <see cref="Wear"/> (трещины сразу, <see cref="StrikePredicted"/>).
    /// Снятие тайла не предсказывается: кто добил камень — решает сервер (общие удары нескольких игроков), тайл уходит пачкой мира.
    /// «Взмах» сервера (39) кладёт удары как есть (свои и чужие) и зовёт <see cref="StrikeSeen"/>. Начатый взмах доигрывается:
    /// <see cref="StopDig"/> его не прерывает; следующий можно начать, как только удар посчитан своим счётом (<see cref="DigPredicted"/>).
    /// </summary>
    public sealed partial class NetClient
    {
        private bool _digStrike;
        private readonly TileCoord[] _predTiles = new TileCoord[PickStrike.MaxTiles];
        private readonly ushort[] _predObjs = new ushort[PickStrike.MaxTiles];
        private readonly byte[] _predNeeded = new byte[PickStrike.MaxTiles];

        /// <summary>Идущее копание — взмах кирки (C-028).</summary>
        public bool DigStrike => _digActive && _digStrike;

        /// <summary>
        /// Своя позиция для предсказания удара (игра — предсказанная позиция персонажа, бот — своя). Ставится каждый кадр или в
        /// <see cref="StartDig(TileCoord, WorldPos)"/>.
        /// </summary>
        public WorldPos SelfPosition { get; set; }

        /// <summary>Удары по тайлам у клиента (трещины): свои — сразу, затем как прислал сервер; сброс через 5 с без ударов.</summary>
        public TileWear Wear { get; } = new TileWear(TileWear.ClientResetUs);

        /// <summary>Свой удар посчитан: число тайлов (0 — в воздух); тайлы — <see cref="PredictedStrikeTile"/>.</summary>
        public Action<int>? StrikePredicted { get; set; }

        /// <summary>«Взмах» сервера (свой или чужой).</summary>
        public Action<StrikeMessage>? StrikeSeen { get; set; }

        /// <summary>Тайл № <paramref name="i"/> последнего своего удара.</summary>
        public TileCoord PredictedStrikeTile(int i) => _predTiles[i];

        /// <summary>Тайлов в последнем своём ударе.</summary>
        public int PredictedStrikeCount { get; private set; }

        /// <summary>Своих ударов посчитано / сообщений «взмах» принято.</summary>
        public long StrikesPredicted { get; private set; }

        /// <inheritdoc cref="StrikesPredicted"/>
        public long StrikesSeen { get; private set; }

        /// <summary>«Копать: начало» с позицией для предсказания удара (C-028).</summary>
        public ushort StartDig(TileCoord tile, WorldPos self)
        {
            SelfPosition = self;
            return StartDig(tile);
        }

        private void PredictStrike()
        {
            int n = PickStrike.Targets(World!.World, SelfPosition, _digTile, DigTimes, HeldItem, _predTiles, _predObjs, _predNeeded);
            for (int k = 0; k < n; k++)
            {
                Wear.Hit(_predTiles[k], _predObjs[k], _predNeeded[k], _nowUs);
            }

            PredictedStrikeCount = n;
            StrikesPredicted++;
            StrikePredicted?.Invoke(n);
        }

        /// <summary>Своих снятых взмахом тайлов в очереди не больше (сверх — старые уходят; боты разбирают каждый кадр).</summary>
        public const int MaxBrokenQueue = 256;

        private readonly System.Collections.Generic.List<(TileCoord Tile, ushort Obj)> _brokenByMe =
            new System.Collections.Generic.List<(TileCoord Tile, ushort Obj)>();

        /// <summary>
        /// Забрать тайлы, снятые своими взмахами (по «взмаху» сервера, с объектом до снятия) — для счёта добычи у ботов.
        /// Возвращает число добавленных в <paramref name="into"/>.
        /// </summary>
        public int DrainBrokenByMe(System.Collections.Generic.List<(TileCoord Tile, ushort Obj)> into)
        {
            int n = _brokenByMe.Count;
            into.AddRange(_brokenByMe);
            _brokenByMe.Clear();
            return n;
        }

        private void OnStrike(in StrikeMessage m)
        {
            StrikesSeen++;
            for (int k = 0; k < m.Count; k++)
            {
                StrikeTile t = m.Get(k);
                if (t.Broken && m.Striker == EntityId)
                {
                    if (_brokenByMe.Count >= MaxBrokenQueue)
                    {
                        _brokenByMe.RemoveAt(0);
                    }

                    _brokenByMe.Add((t.Tile, t.Obj));
                }

                // Снят — трещин больше нет (пачка мира уберёт объект); иначе — удары сервера (он главнее своего счёта).
                Wear.Set(t.Tile, t.Obj, t.Broken ? 0 : t.Hits, t.Needed, _nowUs);
            }

            StrikeSeen?.Invoke(m);
        }

        /// <summary>Набранная доля ударов тайла (0…1) для трещин: объект тайла по своему миру должен совпадать; сброс — по времени.</summary>
        public float WearOf(TileCoord tile)
        {
            if (World == null)
            {
                return 0f;
            }

            TileCoord t = Torus.Wrap(World.Shape, tile);
            ushort obj = World.World.GetObject(t);
            for (int i = 0; i < Wear.Count; i++)
            {
                WearEntry e = Wear[i];
                if (e.Tile == t)
                {
                    return e.Obj == obj && _nowUs - e.LastHit <= Wear.ResetAfter ? Math.Min(1f, e.Hits / (float)e.Needed) : 0f;
                }
            }

            return 0f;
        }
    }
}
