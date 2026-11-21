using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// «Лечь в кровать» на клиенте (C-030, слово Автора 17.11 «В кровать не получается лечь. Она должна точку респауна
    /// задавать»): F или ПКМ по кровати → заявка 33 → сервер ставит точку возрождения (<c>Simulation.RequestSetBed</c>) и
    /// отвечает «принято» (34) → персонаж ложится на эту кровать (поза и затемнение рисует клиент). Встать — любое движение,
    /// уворот, удар, F, смерть; кровать снесли — тоже встаёт. Только вид у своего клиента: в ядре персонаж стоит, где стоял
    /// (кровать твёрдая), чужие игроки лежащего не видят (бита в снимке нет — допущение C-030, протокол не трогаем).
    /// </summary>
    public sealed class BedRest
    {
        /// <summary>Лежит.</summary>
        public bool Active { get; private set; }

        /// <summary>Тайл-якорь кровати.</summary>
        public TileCoord Bed { get; private set; }

        /// <summary>Поворот кровати 0…3 (0 — изголовьем на север, 1 — на восток, 2 — на юг, 3 — на запад).</summary>
        public int Rotation { get; private set; }

        /// <summary>Сколько секунд лежит (для позы «ложится» и затемнения).</summary>
        public float Age { get; private set; }

        /// <summary>Сколько раз ложился (счётчик для smoke и тестов).</summary>
        public int Lay { get; private set; }

        /// <summary>
        /// Ответ сервера «принято» на действие с кроватью: <paramref name="tile"/> — тайл заявки (любой тайл кровати, хвост
        /// переводится в якорь). Не кровать (снесли, пока шёл ответ) — не ложится. Возвращает, лёг ли.
        /// </summary>
        public bool Begin(WorldModel world, TileCoord tile)
        {
            TileCoord t = Torus.Wrap(world.Shape, tile);
            if (ObjectId.IdOf(world.GetObject(t)) != ObjectId.Bed && BuildLayers.TryFurnitureAt(world, t, out TileCoord anchor))
            {
                t = anchor;
            }

            ushort obj = world.GetObject(t);
            if (ObjectId.IdOf(obj) != ObjectId.Bed)
            {
                return false;
            }

            Active = true;
            Bed = t;
            Rotation = ObjectId.RotationOf(obj);
            Age = 0f;
            Lay++;
            return true;
        }

        /// <summary>
        /// Кадр: <paramref name="wake"/> — движение, уворот, удар или F этого кадра; мёртв; кровати на якоре нет — встаёт.
        /// Возвращает, лежит ли после кадра.
        /// </summary>
        public bool Update(WorldModel world, bool alive, bool wake, float dt)
        {
            if (!Active)
            {
                return false;
            }

            if (wake || !alive || ObjectId.IdOf(world.GetObject(Bed)) != ObjectId.Bed)
            {
                Stop();
                return false;
            }

            Age += dt;
            return true;
        }

        /// <summary>Встать.</summary>
        public void Stop()
        {
            Active = false;
            Age = 0f;
        }
    }
}
