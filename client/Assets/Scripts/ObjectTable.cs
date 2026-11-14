#nullable enable
using System.Collections.Generic;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>Вариант картинки вида: имя без «TEMP_» и расширения и вес выбора.</summary>
    public readonly struct ObjectVariant
    {
        public readonly string Sprite;
        public readonly float Weight;

        public ObjectVariant(string sprite, float weight)
        {
            Sprite = sprite;
            Weight = weight;
        }
    }

    /// <summary>Строка таблицы «объект ядра → вид» (E1-7): какие картинки, как разбросать, тень, свет.</summary>
    public sealed class ObjectLook
    {
        /// <summary>Номер объекта ядра (<see cref="ObjectId"/>).</summary>
        public ushort Id;

        /// <summary>Слой земли под объектом (<see cref="GroundTable"/>), для которого строка; −1 — любая земля.</summary>
        public int Ground = -1;

        public ObjectVariant[] Variants = new ObjectVariant[0];

        /// <summary>Виды растут пятнами этого размера, тайлов (роща сосен, роща дубов); 0 — вперемешку по тайлу.</summary>
        public float Patch;

        /// <summary>Оттенок (умножение цвета картинки).</summary>
        public Color32 Tint = new Color32(255, 255, 255, 255);

        /// <summary>Размер: множитель и разброс по хэшу тайла.</summary>
        public float Size = 1f, ScaleMin = 1f, ScaleMax = 1f;

        /// <summary>Смещение опоры от места в тайле, ± тайлов по хэшу.</summary>
        public float Jitter;

        /// <summary>Зеркальное отражение по хэшу.</summary>
        public bool Flip;

        /// <summary>Длина тени в долях высоты картинки (1 — как у дерева); 0 — тени нет.</summary>
        public float Shadow = 1f;

        /// <summary>Высокий объект: его верх над своим персонажем становится полупрозрачным.</summary>
        public bool Tall;

        /// <summary>Радиус круга света ночью, тайлов; 0 — не светит.</summary>
        public float Light;

        // Заполняет ObjectTable при сборке: номера картинок листа и суммарный вес.
        internal int[] Sprites = new int[0];
        internal float[] Weights = new float[0];
        internal float TotalWeight;
    }

    /// <summary>
    /// Таблица «ObjectId → вид» (work/modules/worldview.md: вид тайла задаётся таблицей, не <c>if</c> по коду).
    /// Объект, которого в таблице нет (постройки, будущие номера ядра вроде обрыва), рисуется по-старому — плиткой
    /// Tilemap (<see cref="WorldView.ObjectTile"/>) или никак. Чтобы добавить вид: картинку в <c>Assets/Art/_TEMP</c>
    /// (имя с одной из приставок <c>Orpheus.Editor.ObjectImport</c>) и строку в <see cref="Looks"/>.
    /// Первая подходящая строка главнее: строки «на такой-то земле» ставить раньше строки «на любой».
    /// </summary>
    public static class ObjectTable
    {
        static ObjectVariant V(string sprite, float weight = 1f) => new ObjectVariant(sprite, weight);

        public static readonly ObjectLook[] Looks =
        {
            // Лес (лесная подстилка): дуб и сосна рощами, изредка осенний дуб.
            new ObjectLook
            {
                Id = ObjectId.Tree, Ground = GroundTable.Forest, Patch = 9f,
                Variants = new[] { V("tree_oak_a", 3), V("tree_oak_b", 3), V("tree_oak_autumn", 0.5f), V("tree_pine_a", 3), V("tree_pine_b", 3) },
                ScaleMin = 0.85f, ScaleMax = 1.15f, Jitter = 0.2f, Flip = true, Tall = true,
            },
            // Луг и всё остальное: олива и кипарис, изредка осенний дуб.
            new ObjectLook
            {
                Id = ObjectId.Tree, Patch = 7f,
                Variants = new[] { V("tree_olive_a", 3), V("tree_olive_b", 3), V("tree_oak_autumn", 0.4f), V("tree_cypress_a", 2), V("tree_cypress_b", 2) },
                ScaleMin = 0.85f, ScaleMax = 1.15f, Jitter = 0.2f, Flip = true, Tall = true,
            },
            new ObjectLook
            {
                Id = ObjectId.Bush, Variants = new[] { V("bush_a"), V("bush_b") },
                ScaleMin = 0.9f, ScaleMax = 1.25f, Jitter = 0.2f, Flip = true, Shadow = 0.7f,
            },
            new ObjectLook
            {
                Id = ObjectId.Rock, Variants = new[] { V("boulder_a"), V("boulder_b"), V("boulder_c") },
                ScaleMin = 0.85f, ScaleMax = 1.1f, Jitter = 0.12f, Flip = true, Shadow = 0.8f,
            },
            // Гранит — те же валуны, темнее и холоднее.
            new ObjectLook
            {
                Id = ObjectId.Granite, Variants = new[] { V("boulder_a"), V("boulder_b"), V("boulder_c") },
                Tint = new Color32(150, 152, 172, 255),
                ScaleMin = 0.9f, ScaleMax = 1.15f, Jitter = 0.12f, Flip = true, Shadow = 0.8f,
            },
            new ObjectLook
            {
                Id = ObjectId.Ore, Variants = new[] { V("ore_copper_a"), V("ore_copper_b") },
                ScaleMin = 0.9f, ScaleMax = 1.1f, Jitter = 0.1f, Flip = true, Shadow = 0.6f,
            },
            new ObjectLook
            {
                Id = ObjectId.Stump, Variants = new[] { V("stump") },
                ScaleMin = 0.9f, ScaleMax = 1.1f, Jitter = 0.15f, Flip = true, Shadow = 0.8f,
            },
            // Костёр и факел: своих картинок в наборе нет (заказать на Мак) — пень, окрашенный огнём; главное — круг света.
            new ObjectLook
            {
                Id = ObjectId.Campfire, Variants = new[] { V("stump") },
                Tint = new Color32(255, 150, 60, 255), Size = 0.9f, Shadow = 0f, Light = 7f,
            },
            new ObjectLook
            {
                Id = ObjectId.Torch, Variants = new[] { V("stump") },
                Tint = new Color32(255, 214, 110, 255), Size = 0.5f, Shadow = 0f, Light = 5.5f,
            },
        };

        /// <summary>Опора объекта в своём тайле: по центру, на этой высоте от нижнего края тайла.</summary>
        public const float BaseLift = 0.3f;

        // Строки по номеру объекта (по порядку таблицы); null — номера в таблице нет.
        static ObjectLook[]?[] _byId = new ObjectLook[0][];
        static bool _resolved;

        /// <summary>Имена всех картинок таблицы (для проверки набора при импорте).</summary>
        public static IEnumerable<string> SpriteNames()
        {
            var seen = new HashSet<string>();
            foreach (ObjectLook look in Looks)
            {
                foreach (ObjectVariant v in look.Variants)
                {
                    if (seen.Add(v.Sprite))
                    {
                        yield return v.Sprite;
                    }
                }
            }
        }

        /// <summary>Привязать строки к картинкам листа: варианта без картинки нет в выборе; строка без картинок выключена.</summary>
        public static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            var byId = new List<ObjectLook>?[ObjectId.MaxId + 1];
            foreach (ObjectLook look in Looks)
            {
                var sprites = new List<int>();
                var weights = new List<float>();
                float total = 0f;
                foreach (ObjectVariant v in look.Variants)
                {
                    int s = ObjectAtlas.Find(v.Sprite);
                    if (s < 0 || v.Weight <= 0f)
                    {
                        continue;
                    }

                    sprites.Add(s);
                    weights.Add(v.Weight);
                    total += v.Weight;
                }

                look.Sprites = sprites.ToArray();
                look.Weights = weights.ToArray();
                look.TotalWeight = total;
                if (sprites.Count == 0)
                {
                    Debug.LogWarning($"[objects] у объекта {look.Id} нет ни одной картинки — рисуется по-старому");
                    continue;
                }

                (byId[look.Id] ??= new List<ObjectLook>()).Add(look);
            }

            _byId = new ObjectLook[byId.Length][];
            for (int i = 0; i < byId.Length; i++)
            {
                _byId[i] = byId[i]?.ToArray();
            }
        }

        /// <summary>
        /// Вид объекта на тайле с таким полом; null — в таблице нет (часть многотайлового, постройка, неизвестный номер):
        /// рисовать по-старому.
        /// </summary>
        public static ObjectLook? Find(ushort raw, byte floor)
        {
            if (raw == ObjectId.Empty || ObjectId.IsPart(raw))
            {
                return null;
            }

            int id = ObjectId.IdOf(raw);
            ObjectLook[]? rows = id < _byId.Length ? _byId[id] : null;
            if (rows == null)
            {
                return null;
            }

            int layer = GroundTable.Of(floor).Layer;
            foreach (ObjectLook look in rows)
            {
                if (look.Ground < 0 || look.Ground == layer)
                {
                    return look;
                }
            }

            return null;
        }

        /// <summary>Есть ли номер в таблице на какой-нибудь земле (тогда плитки Tilemap у него нет).</summary>
        public static bool Has(ushort raw)
        {
            if (raw == ObjectId.Empty || ObjectId.IsPart(raw))
            {
                return false;
            }

            int id = ObjectId.IdOf(raw);
            return id < _byId.Length && _byId[id] != null;
        }

        /// <summary>Место и вид объекта на тайле мира (tx, ty) — одинаково у всех игроков и после перезахода.</summary>
        public struct Placement
        {
            public int Sprite;
            public bool Flip;
            public float Scale;
            public float Dx, Dy; // смещение опоры, тайлов (Y вверх)
        }

        public static Placement Place(ObjectLook look, int tx, int ty)
        {
            Placement p;
            // Вид: при Patch > 0 — плавный шум (рощи) с примесью случайного по тайлу, иначе чистый хэш.
            float pick = GroundAtlas.Hash(tx, ty, 101);
            if (look.Patch > 0f)
            {
                pick = Mathf.Repeat(Smooth(tx / look.Patch, ty / look.Patch, 103) * 2.2f + pick * 0.22f, 1f);
            }

            float at = pick * look.TotalWeight;
            int k = 0;
            while (k < look.Weights.Length - 1 && at >= look.Weights[k])
            {
                at -= look.Weights[k];
                k++;
            }

            p.Sprite = look.Sprites[k];
            p.Flip = look.Flip && GroundAtlas.Hash(tx, ty, 107) < 0.5f;
            p.Scale = look.Size * Mathf.Lerp(look.ScaleMin, look.ScaleMax, GroundAtlas.Hash(tx, ty, 109));
            p.Dx = (GroundAtlas.Hash(tx, ty, 113) * 2f - 1f) * look.Jitter;
            p.Dy = (GroundAtlas.Hash(tx, ty, 127) * 2f - 1f) * look.Jitter;
            return p;
        }

        // Плавный шум значений 0…1 на целой решётке.
        static float Smooth(float x, float y, uint seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Mathf.Lerp(GroundAtlas.Hash(x0, y0, seed), GroundAtlas.Hash(x0 + 1, y0, seed), fx);
            float b = Mathf.Lerp(GroundAtlas.Hash(x0, y0 + 1, seed), GroundAtlas.Hash(x0 + 1, y0 + 1, seed), fx);
            return Mathf.Lerp(a, b, fy);
        }
    }
}
