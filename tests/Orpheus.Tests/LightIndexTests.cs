using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка 1-014, фаза B «свет-сетка» (docs/01, 6 «Источники света»): факел 3/6, костёр 4/8;
    /// вклад полный до r0, далее линейно 1024×(r−d)/(r−r0); сетка на чанк, пересчёт по штампам ревизий
    /// окна 3 × 3; «построенный свет» (без фона) — вход для правил появления (1-009); шов тора работает.
    /// </summary>
    public class LightIndexTests
    {
        private static WorldModel Flat(ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static void PutTile(WorldModel w, int x, int y, ushort obj) =>
            w.SetBaseTile(new TileCoord(x, y), FloorId.Grass, obj);

        private static void PutTorch(WorldModel w, int x, int y) => PutTile(w, x, y, ObjectId.Torch);

        // ---------- Факел: профиль вклада ----------

        [Fact]
        public void L01_Torch_Profile()
        {
            WorldModel w = Flat(1);
            PutTorch(w, 100, 100);
            LightIndex li = w.Lights;

            // Полный свет до 3 тайлов (d² ≤ 9).
            for (int d = 0; d <= 3; d++)
            {
                Assert.Equal(1024, li.SourceLightX1024(new TileCoord(100 + d, 100)));
            }

            // d = 4: 1024×(6−4)/3 = 682; d = 5: 341; d = 6: 0.
            Assert.Equal(1024 * 2 / 3, li.SourceLightX1024(new TileCoord(104, 100)));
            Assert.Equal(1024 * 1 / 3, li.SourceLightX1024(new TileCoord(105, 100)));
            Assert.Equal(0, li.SourceLightX1024(new TileCoord(106, 100)));

            // По диагонали d округляется вниз: (3,3) → d = 4 → 682.
            Assert.Equal(682, li.SourceLightX1024(new TileCoord(103, 103)));

            // Симметрия.
            Assert.Equal(li.SourceLightX1024(new TileCoord(96, 100)), li.SourceLightX1024(new TileCoord(104, 100)));
        }

        [Fact]
        public void L02_Campfire_Profile()
        {
            WorldModel w = Flat(1);
            PutTile(w, 200, 200, ObjectId.Campfire);
            LightIndex li = w.Lights;

            // Полный свет до 4 тайлов (d² ≤ 16).
            for (int d = 0; d <= 4; d++)
            {
                Assert.Equal(1024, li.SourceLightX1024(new TileCoord(200 + d, 200)));
            }

            // d = 6: 1024×(8−6)/4 = 512 — ровно порог «построенного света» 0,5; d = 8: 0.
            Assert.Equal(512, li.SourceLightX1024(new TileCoord(206, 200)));
            Assert.Equal(0, li.SourceLightX1024(new TileCoord(208, 200)));
        }

        [Fact]
        public void L03_Sums_Two_Sources()
        {
            WorldModel w = Flat(1);
            PutTorch(w, 300, 300);
            PutTorch(w, 308, 300);
            LightIndex li = w.Lights;

            // Между факелами (по 4 тайла от каждого): 682 + 682 (сумму источников L не обрезает, clamp — в формуле).
            Assert.Equal(1364, li.SourceLightX1024(new TileCoord(304, 300)));
        }

        // ---------- Шов тора ----------

        [Fact]
        public void L04_Torus_Seam()
        {
            WorldModel w = Flat(2);
            PutTorch(w, 1, 5);
            LightIndex li = w.Lights;

            // Свет заворачивает через западный край: (1024−3+1) = (1022,5) в 3 тайлах от факела.
            Assert.Equal(1024, li.SourceLightX1024(new TileCoord(1022, 5)));
            Assert.Equal(0, li.SourceLightX1024(new TileCoord(1024 - 7, 5)));

            // И по Y: (5, 1024−3+1=1022)… факел в (5,1).
            PutTorch(w, 5, 1);
            Assert.Equal(1024, li.SourceLightX1024(new TileCoord(5, 1022)));
        }

        // ---------- L = фон × k + источники ----------

        [Fact]
        public void L05_Night_Forest_Needs_Torch()
        {
            WorldModel w = Flat(3);
            int night = DayClock.NightStartTick; // фон 307
            int forest = DayClock.BiomeKX1024(1); // 870

            // Ночь в лесу без света: 307 × 870 / 1024 = 260 < 277 — не видно (<c>01` 6). Запрос вдалеке от места факела.
            Assert.False(DayClock.IsVisible(w.Lights.LightX1024(new TileCoord(450, 450), DayClock.BackgroundX1024(night), forest)));

            // Факел: тайл источника — 260 + 1024 → clamp 1024, видно; в 5 тайлах: 260 + 341 = 601, видно.
            PutTorch(w, 400, 400);
            int bg = DayClock.BackgroundX1024(night);
            Assert.True(DayClock.IsVisible(w.Lights.LightX1024(new TileCoord(400, 400), bg, forest)));
            Assert.Equal(601, w.Lights.LightX1024(new TileCoord(405, 400), bg, forest));
            Assert.True(DayClock.IsVisible(w.Lights.LightX1024(new TileCoord(405, 400), bg, forest)));

            // День в лесу: 1024 × 870 / 1024 = 870 — видно без источников.
            Assert.True(DayClock.IsVisible(w.Lights.LightX1024(new TileCoord(450, 450), DayClock.DayBgX1024, forest)));
        }

        // ---------- «Построенный свет» (запрет появления, 1-009; порог 512) ----------

        [Fact]
        public void L06_Built_Light_Threshold()
        {
            WorldModel w = Flat(4);
            PutTile(w, 500, 500, ObjectId.Campfire);
            PutTorch(w, 600, 600);
            LightIndex li = w.Lights;

            // У костра и в 6 тайлах — ≥ 512; в 8 — 0. Факел: в 5 тайлах 341 < 512 — появлению не мешает.
            Assert.True(li.SourceLightX1024(new TileCoord(500, 500)) >= 512);
            Assert.True(li.SourceLightX1024(new TileCoord(506, 500)) >= 512);
            Assert.Equal(0, li.SourceLightX1024(new TileCoord(508, 500)));
            Assert.True(li.SourceLightX1024(new TileCoord(605, 600)) < 512);
            Assert.Equal(0, li.SourceLightX1024(new TileCoord(900, 900)));
        }

        // ---------- Пересчёт по ревизиям: бюджет и снос ----------

        [Fact]
        public void L07_Rebuild_On_Edit_Budget_And_Removal()
        {
            WorldModel w = Flat(5);
            PutTorch(w, 100, 100);

            // Прогрев: сетка чанка (3,3) и соседей построена.
            Assert.Equal(1024, w.Lights.SourceLightX1024(new TileCoord(100, 100)));
            long warmed = w.Lights.GridRebuilds;

            // Снос факела — серверная правка (ApplyEdits, ревизия +1): свет гаснет.
            Chunk c = w.GetOrCreateChunk(new ChunkCoord(3, 3));
            var edits = new[] { new TileEdit((ushort)(4 * Torus.ChunkSize + 4), FloorId.Grass, ObjectId.Empty) };
            Span<EditResult> res = stackalloc EditResult[1];
            c.ApplyEdits(edits, res);
            Assert.Equal(EditResult.Applied, res[0]);
            Assert.Equal(0, w.Lights.SourceLightX1024(new TileCoord(100, 100)));

            // Бюджет: правка одного тайла — пересчёт не больше 9 чанков (окно 3 × 3 над изменённым).
            long before = w.Lights.GridRebuilds;
            Span<ChunkCoord> window = stackalloc ChunkCoord[Torus.MaxNeighborChunks(1, 1)];
            int n = Torus.NeighborChunks(w.Shape, new ChunkCoord(3, 3), 1, 1, window);
            for (int i = 0; i < n; i++)
            {
                TileCoord corner = Torus.CornerOf(window[i]);
                w.Lights.SourceLightX1024(new TileCoord(corner.X, corner.Y));
            }

            Assert.True(w.Lights.GridRebuilds - before <= 9, $"пересчёт {w.Lights.GridRebuilds - before} чанков после одной правки");
            Assert.True(w.Lights.GridRebuilds > warmed);

            // Повторный запрос без правок — пересчёта нет.
            long stable = w.Lights.GridRebuilds;
            w.Lights.SourceLightX1024(new TileCoord(100, 100));
            w.Lights.SourceLightX1024(new TileCoord(101, 101));
            Assert.Equal(stable, w.Lights.GridRebuilds);
        }

        [Fact]
        public void L08_Placeable_And_Recipe()
        {
            // Костёр: id 26, проходим, ставится, рецепт 8 камня + 4 древесины, прочность 60 (`05` 101).
            Assert.Equal(26, ObjectId.IdOf(ObjectId.Campfire));
            Assert.True(ObjectId.IsPassable(ObjectId.Campfire));
            Assert.True(EditRules.IsPlaceable(ObjectId.Campfire));
            Assert.True(BuildDefs.TryGet(ObjectId.Campfire, out BuildDef def));
            Assert.Equal(Material.Stone, def.Material1);
            Assert.Equal(8, def.Count1);
            Assert.Equal(Material.Wood, def.Material2);
            Assert.Equal(4, def.Count2);
            Assert.Equal(60, def.MaxIntegrity);
            Assert.False(def.IsFloor);
        }

        [Fact]
        public void L09_Flat_Map_No_Edge_Wrap()
        {
            // Данж (плоская карта): свет не заворачивается через край.
            var w = new WorldModel(MapShape.Flat(64, 64));
            w.CreateAllChunks();
            Chunk c = w.GetOrCreateChunk(new ChunkCoord(0, 0));
            var edits = new[] { new TileEdit(0, FloorId.Stone, ObjectId.Torch) };
            Span<EditResult> res = stackalloc EditResult[1];
            c.ApplyEdits(edits, res);
            Assert.Equal(EditResult.Applied, res[0]);

            LightIndex li = w.Lights;
            Assert.Equal(1024, li.SourceLightX1024(new TileCoord(0, 0)));
            Assert.Equal(0, li.SourceLightX1024(new TileCoord(63, 0))); // «западный сосед» (−3,0) на плоской карте — тайла нет
            Assert.Equal(0, li.SourceLightX1024(new TileCoord(0, 63)));
        }
    }
}
