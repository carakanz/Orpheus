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

    /// <summary>Строка таблицы «объект ядра → вид» (E1-7): какие картинки, как разбросать, тень.</summary>
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

        /// <summary>
        /// Качается на ветру и вздрагивает от существ (E1-11): смещение верха, тайлов на тайл высоты картинки (&lt; 1);
        /// 0 — стоит неподвижно (валун, пень, руда, постройка, обрыв).
        /// </summary>
        public float Sway;

        /// <summary>Листьев с кроны при толчке существом (E1-11): от и до; удар и срубание — больше (<see cref="LifeFx"/>).</summary>
        public int LeavesMin, LeavesMax;

        /// <summary>
        /// Блок (обрыв, шаги 1.6, 1.6б, C-016): не картинка с опорой, а кусок скального массива на весь тайл — верхняя грань,
        /// поднятая на высоту яруса, лицо к зрителю (к югу) там, где южный сосед ниже, наклонные бока, контур; рисуется кодом
        /// (<see cref="ObjectMesh"/>, файл <c>ObjectMesh.Cliffs.cs</c>).
        /// </summary>
        public bool Block;

        /// <summary>C-049: объект рисуют в «низкой полосе» строки — перед существом той же четверти (костёр).</summary>
        public bool LowBand;

        /// <summary>Высота блока, тайлов (у обрыва — нижний ярус, C-016).</summary>
        public float Height = 1f;

        /// <summary>Обрыв (C-016): прибавка высоты на ярус и число ярусов (ярус — длина ряда обрыва к югу, западу, востоку).</summary>
        public float Step;
        public int Levels = 1;

        /// <summary>Камни-украшения гряды (без коллизий): картинки, доля тайлов с камнем на верху (внутри / у края), с осыпью у подошвы.</summary>
        public string[] Deco = new string[0];

        /// <summary>Обрыв (C-016): растения на уступах массива — картинки, доля внутренних тайлов, оттенок.</summary>
        public string[] Plants = new string[0];
        public float PlantTop;

        /// <summary>Обрыв (C-016): пятна снега на верхнем ярусе широкого массива.</summary>
        public bool Snow;
        public Color32 PlantTint = new Color32(255, 255, 255, 255);
        internal int[] PlantSprites = new int[0];
        public float DecoTop, DecoEdge, DecoFoot;
        public Color32 DecoTint = new Color32(255, 255, 255, 255), RubbleTint = new Color32(255, 255, 255, 255);

        internal int[] DecoSprites = new int[0];
        internal int WhiteSprite = -1;

        /// <summary>
        /// Картинки по повороту объекта 0°, 90°, 180°, 270° (E2-16: кровать, верстак, сундук; пусто — без поворота) и зеркало
        /// по повороту (270° — картинка 90° зеркально). Нет картинки поворота — обычный вид.
        /// </summary>
        public string[] Turns = new string[0];
        public bool[] TurnFlip = new bool[0];
        internal int[] TurnSprites = new int[0];

        /// <summary>C-041: картинки старого размера кровати 1 × 2 / 2 × 1 (записы сохранений до C-030) по повороту;
        /// зеркало 270° — то же <see cref="TurnFlip"/>. Пусто — узкого вида нет.</summary>
        public string[] NarrowTurns = new string[0];
        internal int[] NarrowTurnSprites = new int[0];

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

        // E2-16: 270° — картинка «боком» (90°) зеркально.
        static readonly bool[] Mirror270 = { false, false, false, true };

        public static readonly ObjectLook[] Looks =
        {
            // C-027 (слово Автора 17.11 «Леса разбей на несколько разных видов по типам деревьев»): вид леса — пол генератора v9.
            // Дубрава (лесная подстилка): дубы, рощицами осенний дуб.
            new ObjectLook
            {
                Id = ObjectId.Tree, Ground = GroundTable.Forest, Patch = 9f,
                Variants = new[] { V("tree_oak_a", 3), V("tree_oak_b", 3), V("tree_oak_autumn", 0.7f) },
                ScaleMin = 0.9f, ScaleMax = 1.2f, Jitter = 0.2f, Flip = true, Tall = true,
                Sway = 0.028f, LeavesMin = 6, LeavesMax = 10,
            },
            // Сосновый бор (рыжая хвоя): сосны, светлее и теплее, выше.
            new ObjectLook
            {
                Id = ObjectId.Tree, Ground = GroundTable.Pine, Patch = 9f,
                Variants = new[] { V("tree_pine_a", 3), V("tree_pine_b", 3) },
                Tint = new Color32(255, 246, 214, 255),
                ScaleMin = 0.95f, ScaleMax = 1.25f, Jitter = 0.25f, Flip = true, Tall = true,
                Sway = 0.022f, LeavesMin = 3, LeavesMax = 6,
            },
            // Тёмный ельник (мох): тёмные холодные ели (сосна и кипарис с тоном), плотнее.
            new ObjectLook
            {
                Id = ObjectId.Tree, Ground = GroundTable.Spruce, Patch = 6f,
                Variants = new[] { V("tree_pine_a", 3), V("tree_pine_b", 3), V("tree_cypress_a", 2), V("tree_cypress_b", 2) },
                Tint = new Color32(150, 178, 168, 255),
                ScaleMin = 0.9f, ScaleMax = 1.2f, Jitter = 0.15f, Flip = true, Tall = true,
                Sway = 0.02f, LeavesMin = 3, LeavesMax = 6,
            },
            // Луг и всё остальное: олива и кипарис, изредка осенний дуб.
            new ObjectLook
            {
                Id = ObjectId.Tree, Patch = 7f,
                Variants = new[] { V("tree_olive_a", 3), V("tree_olive_b", 3), V("tree_oak_autumn", 0.4f), V("tree_cypress_a", 2), V("tree_cypress_b", 2) },
                ScaleMin = 0.85f, ScaleMax = 1.15f, Jitter = 0.2f, Flip = true, Tall = true,
                Sway = 0.028f, LeavesMin = 6, LeavesMax = 10,
            },
            new ObjectLook
            {
                Id = ObjectId.Bush, Variants = new[] { V("bush_a"), V("bush_b") },
                ScaleMin = 0.9f, ScaleMax = 1.25f, Jitter = 0.2f, Flip = true, Shadow = 0.7f,
                Sway = 0.045f, LeavesMin = 2, LeavesMax = 4,
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
            // Обрыв (шаги 1.6, 1.6б, C-016): скальный массив-граница, не копается и не проходится. Ярусы ступенями от южной
            // кромки к северному гребню (чем шире полоса, тем выше), лицо к югу, наклонные бока, контур — кодом
            // (ObjectMesh.Cliffs); валуны редко на гребне и уступах, осыпь у подошвы.
            new ObjectLook
            {
                Id = ObjectId.Cliff, Block = true, Height = 1.0f, Step = 0.45f, Levels = 5,
                Shadow = 1f,
                Deco = new[] { "boulder_a", "boulder_c", "boulder_b", "boulder_a" }, DecoTop = 0.025f, DecoEdge = 0.12f, DecoFoot = 0.3f,
                Plants = new[] { "bush_a", "bush_b" }, PlantTop = 0.035f, PlantTint = new Color32(210, 230, 190, 255),
                DecoTint = new Color32(200, 196, 214, 255), RubbleTint = new Color32(176, 170, 192, 255),
            },
            // Костёр, верстак I и факел (E2-4), сундук и кровать (E2-5): E2-17 — объёмные заглушки 3/4 tools/make_furniture.py
            // (128 × 160, верх 1:1, высота × ObjectMesh.WallHeight, опора внизу по центру); круг света костра и факела —
            // DayTintView (E1-8). Огонь тени не бросает: у костра тени нет, у факела — бледная (древко).
            new ObjectLook
            {
                Id = ObjectId.Campfire, Variants = new[] { V("obj_campfire") },
                Shadow = 0f,
                LowBand = true, // C-049: огонь костра не перекрывает персонажа — низкая полоса строки
                Sway = 0.07f,   // C-049: пламя колышет ветром (верх спрайта; шейдерный «живой мир», без покадровой анимации)
            },
            new ObjectLook
            {
                Id = ObjectId.Workbench, Variants = new[] { V("obj_workbench") },
                Turns = new[] { "obj_workbench", "obj_workbench_e", "obj_workbench_n", "obj_workbench_e" }, TurnFlip = Mirror270, // E2-16: 2 × 2
                Shadow = 0.45f,
            },
            new ObjectLook
            {
                Id = ObjectId.Torch, Variants = new[] { V("obj_torch") },
                Shadow = 0.3f,
                Sway = 0.05f, // C-049: пламя факела колышет ветром (мерцание света и теней — DayTint/ShadowComposite)
            },
            new ObjectLook
            {
                Id = ObjectId.Chest, Variants = new[] { V("obj_chest") },
                Turns = new[] { "obj_chest", "obj_chest_e", "obj_chest_n", "obj_chest_e" }, TurnFlip = Mirror270, // E2-16
                Shadow = 0.45f,
            },
            new ObjectLook
            {
                Id = ObjectId.Bed, Variants = new[] { V("obj_bed") },
                Turns = new[] { "obj_bed", "obj_bed_e", "obj_bed_s", "obj_bed_e" }, TurnFlip = Mirror270, // C-030: двуспальная 2 × 2 (E2-16: 1 × 2)
                NarrowTurns = new[] { "obj_bed_1x2", "obj_bed_1x2_e", "obj_bed_1x2_s", "obj_bed_1x2_e" }, // C-041: старый 1 × 2 — вид, когда окно занято соседом
                Shadow = 0.35f,
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

                foreach (string t in look.Turns)
                {
                    if (seen.Add(t))
                    {
                        yield return t;
                    }
                }

                foreach (string t in look.NarrowTurns) // C-041: старый 1 × 2 — набор картинок при импорте
                {
                    if (seen.Add(t))
                    {
                        yield return t;
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
                look.TurnSprites = new int[look.Turns.Length];
                for (int t = 0; t < look.Turns.Length; t++)
                {
                    look.TurnSprites[t] = ObjectAtlas.Find(look.Turns[t]); // E2-16: −1 — обычный вид
                }

                look.NarrowTurnSprites = new int[look.NarrowTurns.Length];
                for (int t = 0; t < look.NarrowTurns.Length; t++)
                {
                    look.NarrowTurnSprites[t] = ObjectAtlas.Find(look.NarrowTurns[t]); // C-041: −1 — узкого вида нет
                }

                look.Weights = weights.ToArray();
                look.TotalWeight = total;
                if (look.Block)
                {
                    look.WhiteSprite = ObjectAtlas.Find(ObjectAtlas.WhiteName);
                    var deco = new List<int>();
                    foreach (string name in look.Deco)
                    {
                        int d = ObjectAtlas.Find(name);
                        if (d >= 0)
                        {
                            deco.Add(d);
                        }
                    }

                    look.DecoSprites = deco.ToArray();
                    var plants = new List<int>();
                    foreach (string name in look.Plants)
                    {
                        int d = ObjectAtlas.Find(name);
                        if (d >= 0)
                        {
                            plants.Add(d);
                        }
                    }

                    look.PlantSprites = plants.ToArray();
                    if (look.WhiteSprite < 0)
                    {
                        Debug.LogWarning($"[objects] у блока {look.Id} нет белого квадрата листа — не рисуется");
                        continue;
                    }
                }
                else if (sprites.Count == 0)
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

        /// <summary>Объект — блок (обрыв)?</summary>
        public static bool IsBlock(ushort raw)
        {
            if (raw == ObjectId.Empty || ObjectId.IsPart(raw))
            {
                return false;
            }

            int id = ObjectId.IdOf(raw);
            ObjectLook[]? rows = id < _byId.Length ? _byId[id] : null;
            return rows != null && rows[0].Block;
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

        /// <summary>Место и вид объекта с поворотом (E2-16): картинка поворота и зеркало, если они есть у вида.</summary>
        public static Placement Place(ObjectLook look, int tx, int ty, int rotation)
        {
            Placement p = Place(look, tx, ty);
            if (rotation < look.TurnSprites.Length && look.TurnSprites[rotation] >= 0)
            {
                p.Sprite = look.TurnSprites[rotation];
                p.Flip = rotation < look.TurnFlip.Length && look.TurnFlip[rotation];
            }

            return p;
        }

        /// <summary>Вид старой узкой кровати 1 × 2 / 2 × 1 (C-041): картинка <see cref="ObjectLook.NarrowTurns"/>,
        /// зеркало 270° — как у поворота.</summary>
        public static Placement PlaceNarrow(ObjectLook look, int tx, int ty, int rotation)
        {
            Placement p = Place(look, tx, ty);
            if (rotation < look.NarrowTurnSprites.Length && look.NarrowTurnSprites[rotation] >= 0)
            {
                p.Sprite = look.NarrowTurnSprites[rotation];
                p.Flip = rotation < look.TurnFlip.Length && look.TurnFlip[rotation];
            }

            return p;
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
        internal static float Smooth(float x, float y, uint seed)
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
