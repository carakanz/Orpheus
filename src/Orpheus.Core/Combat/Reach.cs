using Orpheus.Core.Movement;

namespace Orpheus.Core.Combat
{
    /// <summary>
    /// Досягаемость — «визуально как персонаж + инструмент» (копание) и «как персонаж + оружие» (удар): слово Автора 08.11,
    /// `BRIEF.md` п. 11b; 0-034. Радиус тела (коллизия 0,4 т) + длина предмета. Длины — параметр на предмет, в unit (1/256 т),
    /// <i>(прикидка 0-034)</i>; пока предметов два — «руки/кирка» и «меч».
    /// <para>
    /// Как меряется: копание — от центра персонажа до <b>ближайшей точки тайла</b> (кончик инструмента касается тайла,
    /// <see cref="World.EditRules.InDigReach(in Geometry.MapShape, Geometry.WorldPos, Geometry.TileCoord)"/>); удар — радиус
    /// сектора от центра атакующего, цель задета, если её круг пересекает сектор (<see cref="MeleeSector"/>).
    /// </para>
    /// </summary>
    public static class Reach
    {
        /// <summary>Радиус тела персонажа и врага-человека: 0,4 т = 102 unit (коллизия, <see cref="Collision.RadiusUnits"/>).</summary>
        public const int BodyRadiusUnits = Collision.RadiusUnits;

        /// <summary>Длина «руки/кирка»: 0,75 т = 192 unit.</summary>
        public const int PickaxeUnits = 192;

        /// <summary>Длина «меч»: 1,0 т = 256 unit.</summary>
        public const int SwordUnits = 256;

        /// <summary>Досягаемость копания: тело + кирка = 294 unit ≈ 1,15 т до ближайшей точки тайла.</summary>
        public const int DigUnits = BodyRadiusUnits + PickaxeUnits;

        /// <summary>Досягаемость удара мечом: тело + меч = 358 unit = 1,4 т (радиус сектора).</summary>
        public const int SwordStrikeUnits = BodyRadiusUnits + SwordUnits;

        /// <summary>Досягаемость «персонаж + предмет длиной <paramref name="itemUnits"/>».</summary>
        public static int Of(int itemUnits) => BodyRadiusUnits + itemUnits;
    }
}
