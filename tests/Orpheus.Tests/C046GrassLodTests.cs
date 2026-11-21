#nullable enable
using System;
using System.IO;
using Xunit;
using Orpheus.Client;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-046 (слово Автора 21.11 «при отдалении не пропадает графика»): на обзоре 40/64/96 тайлов
    /// показана одна и та же растительность — пучки травы и куртины цветов МЕЛЬЧЕ вместе с кадром,
    /// но НЕ РЕЖЕ. Прежняя формула прореживания (<c>LifeFx</c>: <c>Clamp01((viewTiles − 64)/32) · 0,65</c>
    /// → <c>_OrphGrass.x</c>) прятала до 65 % пучков на обзоре 96: шейдер схлопывал квад пучка в точку,
    /// поле на обзоре «лысело». Проверки:
    /// 1) чистая <see cref="GrassLod.HideFraction"/> = 0 на любом обзоре (правка числа красит тест);
    /// 2) плотность пучков на ближнем и дальнем обзоре одинаковая;
    /// 3) в <c>Grass.shader</c> нет схлопывания квада (<c>v.info.z &lt; _OrphGrass.x</c> → <c>wp.xy = base</c>);
    /// 4) <c>LifeFx</c> кормит <c>_OrphGrass.x</c> из <see cref="GrassLod"/>, а не прежней формулой.
    /// Формулировки 3–4 читают исходники — по образцу <c>CoreAssemblyRulesTests</c>/<c>AimMathTests</c>.
    /// </summary>
    public class C046GrassLodTests
    {
        /// <summary>Обзоры из карточки (40/64/96 = <c>GameBoot.NearViewTiles</c>/<c>DefaultViewTiles</c>/<c>FarViewTiles</c>) и запас по краям.</summary>
        [Theory]
        [InlineData(24f)]
        [InlineData(40f)]
        [InlineData(64f)]
        [InlineData(80f)]
        [InlineData(96f)]
        [InlineData(128f)]
        [InlineData(200f)]
        public void HideFraction_Is_Zero_At_Every_View(float viewTiles)
        {
            Assert.Equal(0f, GrassLod.HideFraction(viewTiles));
        }

        /// <summary>Правило Автора «мельче, но не реже»: видимая доля пучков на обзоре 40 и 96 одинаковая.</summary>
        [Fact]
        public void Near_And_Far_View_Have_Equal_Tuft_Density()
        {
            float nearVisible = 1f - GrassLod.HideFraction(40f);
            float defaultVisible = 1f - GrassLod.HideFraction(64f);
            float farVisible = 1f - GrassLod.HideFraction(96f);
            Assert.Equal(nearVisible, defaultVisible);
            Assert.Equal(nearVisible, farVisible);
            Assert.Equal(1f, farVisible);
        }

        /// <summary>
        /// Шейдер рисует каждый пучок на любом обзоре: ветки, схлопывающей квадрат пучка в точку
        /// опоры, в <c>Grass.shader</c> нет (проверка на исходник — правка шейдера красит тест).
        /// По замечанию П 1 (21.11) проверка держит и перефразировку: доли скрытия в шейдере нет вовсе
        /// (ни одного <c>_OrphGrass</c>) и точке опоре позиция не присваивается.
        /// </summary>
        [Fact]
        public void Shader_Draws_Every_Tuft_Quad_At_Any_View()
        {
            string shader = File.ReadAllText(Path.Combine(ClientRes, "Grass.shader"));
            Assert.DoesNotMatch(@"v\.info\.z\s*<\s*_OrphGrass\.x", shader);
            Assert.DoesNotMatch(@"wp\.xy\s*=\s*base", shader);
            Assert.DoesNotContain("_OrphGrass", shader); // доля скрытия не читается ни в каком виде
            Assert.DoesNotMatch(@"=\s*base\s*[;,)]", shader); // позицию не приравнивают к опоре
            // Ветровой изгиб пучка остался (иначе проверка «нет скрытия» ничего не стоит):
            Assert.Contains("OrphWind(base, v.info.z)", shader);
        }

        /// <summary>
        /// Клиент считает долю скрытых пучков только через <see cref="GrassLod"/> — прежняя формула
        /// прореживания по обзору в <c>LifeFx.cs</c> отсутствует (иначе «пропадает графика» вернётся).
        /// </summary>
        [Fact]
        public void LifeFx_Feeds_GrassLod_Not_The_Old_Thinning_Formula()
        {
            string lifeFx = File.ReadAllText(Path.Combine(Scripts, "LifeFx.cs"));
            Assert.Contains("GrassLod.HideFraction(viewTiles)", lifeFx);
            Assert.DoesNotMatch(@"viewTiles\s*-\s*64f", lifeFx);
            Assert.DoesNotMatch(@"0\.65f", lifeFx);
        }

        static string ClientRes => Path.Combine(RepoRoot, "client", "Assets", "Resources");
        static string Scripts => Path.Combine(RepoRoot, "client", "Assets", "Scripts");

        /// <summary>Корень репозитория: вверх от каталога тестов до <c>Orpheus.sln</c> (как в AimMathTests).</summary>
        static string RepoRoot
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Orpheus.sln")))
                {
                    dir = dir.Parent;
                }

                Assert.NotNull(dir);
                return dir!.FullName;
            }
        }
    }
}
