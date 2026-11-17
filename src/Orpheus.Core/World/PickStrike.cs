using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;

namespace Orpheus.Core.World
{
    /// <summary>
    /// C-028 (BRIEF 11n: «удары кирки должны быть горизонтальные и ломать сразу несколько камней в направлении удара; на 1 камень
    /// нужно несколько ударов»): взмах кирки — общие правила сервера, предсказания клиента и ботов, только целые числа.
    /// <list type="bullet">
    /// <item>С киркой в руках ЛКМ — взмах к тайлу прицела (<see cref="IsStrike"/>): длится <see cref="SwingFrames"/> кадров
    /// ввода с зажатым копанием, удар — в последний кадр.</item>
    /// <item>Удар задевает до <see cref="MaxTiles"/> «каменных» тайлов (<see cref="IsTarget"/>: природный объект, который этой
    /// киркой бьётся, <see cref="DigTimes.HitsFor"/> &gt; 0) в секторе ±45° от оси «персонаж → центр тайла прицела», ближняя
    /// точка тайла не дальше <see cref="ReachUnits"/> (досягаемость кирки + 1 тайл). Тайл прицела — первым, если он задет; дальше —
    /// по близости (квадрат расстояния до центра, затем отход от оси), равные — по обходу строк.</item>
    /// <item>Каждый задетый тайл получает удар (<see cref="TileWear"/>); набрал <see cref="DigTimes.HitsFor"/> — снимается правкой
    /// (добыча — как у копания, <see cref="DigLoot"/>).</item>
    /// </list>
    /// Числа — <i>прикидка</i> (карточка C-028: «3 тайла в секторе/линии длиной 2–3 тайла»).
    /// </summary>
    public static class PickStrike
    {
        /// <summary>Длина взмаха, кадров ввода 60 Гц (0,4 с; <i>прикидка</i>, близко к замаху E2-7 ≈ 0,42 с).</summary>
        public const int SwingFrames = 24;

        /// <summary>Тайлов на удар, не больше.</summary>
        public const int MaxTiles = 3;

        /// <summary>Досягаемость удара: от точки персонажа до ближней точки тайла (кирка + 1 тайл ≈ 2,15 т).</summary>
        public const int ReachUnits = Reach.DigUnits + Torus.SubTilesPerTile;

        private const int U = Torus.SubTilesPerTile;
        private const int Scan = 3; // тайлов вокруг персонажа (ReachUnits < 3 т)

        /// <summary>
        /// Взмах, а не копание по времени: кирка в руках и тайл прицела пуст или природный (скала, дерево, залежь…). Постройки
        /// киркой — прежнее копание по времени (снос, 2 с).
        /// </summary>
        public static bool IsStrike(WorldModel world, TileCoord aim, byte held)
        {
            if (Items.DigToolOf(held) != ToolKind.Pick)
            {
                return false;
            }

            ushort obj = world.GetObject(Torus.Wrap(world.Shape, aim));
            return obj == ObjectId.Empty || (ObjectId.IsNatural(obj) && !ObjectId.IsPart(obj));
        }

        /// <summary>
        /// Можно ли начать взмах к <paramref name="aim"/>: прицел в досягаемости кирки по природному объекту, которого эта кирка
        /// не берёт, — отказ строкой («нужен топор», «нужна кирка: медная»); иначе (пусто, далеко, камень) — принято.
        /// </summary>
        public static EditReply Check(WorldModel world, WorldPos pos, TileCoord aim, DigTimes times, byte held)
        {
            TileCoord t = Torus.Wrap(world.Shape, aim);
            ushort obj = world.GetObject(t);
            if (obj == ObjectId.Empty || ObjectId.IsPart(obj) || !EditRules.InDigReach(world.Shape, pos, t, held))
            {
                return EditReply.Accepted;
            }

            int hits = times.HitsFor(obj, ToolKind.Pick, Items.TierOf(held));
            return hits == DigTimes.NeedTool ? EditReply.ToolRequired
                : hits == DigTimes.NeedTier ? EditReply.TierRequired
                : EditReply.Accepted; // «нельзя» (обрыв) — взмах в воздух
        }

        /// <summary>Ударов до снятия объекта <paramref name="obj"/> этой киркой; ≤ 0 — тайл ударом не берётся.</summary>
        public static int HitsOf(DigTimes times, ushort obj, byte held) =>
            obj == ObjectId.Empty || ObjectId.IsPart(obj) || !ObjectId.IsNatural(obj) || Items.DigToolOf(held) != ToolKind.Pick
                ? 0
                : times.HitsFor(obj, ToolKind.Pick, Items.TierOf(held));

        /// <summary>Объект бьётся взмахом этой кирки.</summary>
        public static bool IsTarget(DigTimes times, ushort obj, byte held) => HitsOf(times, obj, held) > 0;

        /// <summary>
        /// Тайлы удара из точки <paramref name="pos"/> к тайлу прицела <paramref name="aim"/>: до <see cref="MaxTiles"/> в
        /// <paramref name="tiles"/> (обёрнуты), объект каждого — в <paramref name="objs"/>, ударов до снятия — в
        /// <paramref name="needed"/>. Возвращает число; прицел в тайле персонажа ровно в точке — 0 (оси нет).
        /// </summary>
        public static int Targets(WorldModel world, WorldPos pos, TileCoord aim, DigTimes times, byte held,
            Span<TileCoord> tiles, Span<ushort> objs, Span<byte> needed)
        {
            MapShape map = world.Shape;
            TileCoord aimT = Torus.Wrap(map, aim);
            Int2 a = Torus.Delta(map, pos, EditRules.CenterOf(aimT));
            long ax = a.X, ay = a.Y;
            long len2 = ax * ax + ay * ay;
            if (len2 == 0 || Items.DigToolOf(held) != ToolKind.Pick)
            {
                return 0;
            }

            // Ось длиннее 4 т — укорачиваем (порядок и сектор не меняются, произведения в long без переполнения).
            while (len2 > 16L * U * U)
            {
                ax /= 2;
                ay /= 2;
                len2 = ax * ax + ay * ay;
            }

            int n = Math.Min(MaxTiles, Math.Min(tiles.Length, Math.Min(objs.Length, needed.Length)));
            Span<long> keys = stackalloc long[MaxTiles];
            int count = 0;
            TileCoord me = Torus.TileOf(map, pos);
            for (int oy = -Scan; oy <= Scan; oy++)
            {
                for (int ox = -Scan; ox <= Scan; ox++)
                {
                    TileCoord t = Torus.Wrap(map, new TileCoord(me.X + ox, me.Y + oy));
                    if (!EditRules.InReach(map, pos, t, ReachUnits))
                    {
                        continue;
                    }

                    Int2 v = Torus.Delta(map, pos, EditRules.CenterOf(t));
                    long dot = v.X * ax + v.Y * ay;
                    long cross = v.X * ay - v.Y * ax;
                    bool isAim = t == aimT;
                    if (!isAim && (dot <= 0 || cross * cross > dot * dot))
                    {
                        continue; // вне сектора ±45°
                    }

                    ushort obj = world.GetObject(t);
                    int hits = HitsOf(times, obj, held);
                    if (hits <= 0)
                    {
                        continue;
                    }

                    long d2 = (long)v.X * v.X + (long)v.Y * v.Y;
                    long key = isAim ? -1 : d2 * len2 + cross * cross; // прицел — первым
                    int at = count;
                    while (at > 0 && key < keys[at - 1])
                    {
                        at--;
                    }

                    if (at >= n)
                    {
                        continue;
                    }

                    int last = Math.Min(count, n - 1);
                    for (int k = last; k > at; k--)
                    {
                        keys[k] = keys[k - 1];
                        tiles[k] = tiles[k - 1];
                        objs[k] = objs[k - 1];
                        needed[k] = needed[k - 1];
                    }

                    keys[at] = key;
                    tiles[at] = t;
                    objs[at] = obj;
                    needed[at] = (byte)Math.Min(byte.MaxValue, hits);
                    count = Math.Min(n, count + 1);
                }
            }

            return count;
        }
    }
}
