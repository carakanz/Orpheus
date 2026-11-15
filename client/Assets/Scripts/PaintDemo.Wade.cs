#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    public static partial class PaintDemo
    {
        /// <summary>Слово <c>--wade-demo</c> (C-018): <c>water</c> — персонаж в пруду, <c>bush</c> — в кустах, <c>enemy</c> — на берегу, враг в воде.</summary>
        public static bool IsWadeMode(string? mode) => mode == "water" || mode == "bush" || mode == "enemy";

        /// <summary>
        /// Сцена C-018 для снимков без сервера (<c>--wade-demo</c>): пруд 11 × 7 с песчаным берегом и заросль кустов рядом.
        /// <c>water</c> — персонаж (cx, cy) в середине пруда; <c>bush</c> — в середине заросли 5 × 3 (пруд — восточнее);
        /// <c>enemy</c> — персонаж на западном берегу, пруд к востоку (враг-болванка — <see cref="WadeDemoEnemyTile"/>).
        /// </summary>
        public static void Wade(WorldModel world, MapShape map, int cx, int cy, string mode)
        {
            int px = mode == "water" ? cx - 5 : mode == "enemy" ? cx + 2 : cx + 5; // западный край воды
            int py = cy - 3;
            for (int y = py - 1; y <= py + 7; y++)
            {
                for (int x = px - 1; x <= px + 11; x++)
                {
                    bool water = x >= px && x <= px + 10 && y >= py && y <= py + 6;
                    SetTile(world, map, x, y, water ? FloorId.Water : FloorId.Sand, ObjectId.Empty);
                }
            }

            int bx = mode == "bush" ? cx - 2 : px - 7;
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                for (int x = bx; x <= bx + 4; x++)
                {
                    SetTile(world, map, x, y, FloorId.Grass, ObjectId.Bush);
                }
            }

            if (mode == "enemy")
            {
                SetTile(world, map, cx, cy, FloorId.Sand, ObjectId.Empty);
            }
        }

        /// <summary>Тайл врага-болванки сцены <c>enemy</c> (в воде, 4 тайла восточнее персонажа); в прочих сценах — нет.</summary>
        public static bool WadeDemoEnemyTile(string mode, int cx, int cy, out TileCoord tile)
        {
            tile = new TileCoord(cx + 5, cy);
            return mode == "enemy";
        }

        static void SetTile(WorldModel world, MapShape map, int x, int y, byte floor, ushort obj)
        {
            TileCoord t = Torus.Wrap(map, new TileCoord(x, y));
            Chunk chunk = world.GetOrCreateChunk(Torus.ChunkOf(map, t));
            ushort index = (ushort)Chunk.IndexOf(t.X, t.Y);
            chunk.SetBaseTile(index, floor, obj);
        }
    }

    /// <summary>
    /// Враг-болванка сцены <c>--wade-demo enemy</c> (C-018): тот же спрайт с оттенком врага, ходит взад-вперёд по воде
    /// (только вид, ядра нет) — круги, линия воды и обрез «по колено» как у настоящего врага (<see cref="Anim.ActorRig"/>).
    /// </summary>
    public sealed class WadeDemoEnemy
    {
        readonly Anim.ActorRig _rig;
        readonly FloatingOrigin _origin;
        readonly WorldPos _home;
        float _t;

        public WadeDemoEnemy(Transform parent, Sprite sprite, FloatingOrigin origin, WorldPos home)
        {
            _origin = origin;
            _home = home;
            Transform root = Anim.ActorFactory.Create(parent, "WadeEnemy", sprite, Anim.ActorFactory.EnemyTint);
            WorldFx.AddActorShadow(root, 0.8f);
            _rig = new Anim.ActorRig(root);
            Place();
        }

        void Place()
        {
            float dx = 0.9f * Mathf.Sin(_t * 1.6f); // ±0,9 тайла по воде
            _rig.Root.position = _origin.ToUnity(_home) + new Vector3(dx, 0f, 0f);
        }

        public void Frame(float dt)
        {
            _t += dt;
            Place();
            _rig.AnimateWalk(dt);
        }
    }
}
