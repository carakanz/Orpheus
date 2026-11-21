#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// C-053: свои спрайты врагов по архетипу из снимка (<see cref="Orpheus.Core.Sim.EnemyBrain.ArchetypeOf"/>):
    /// разбойник-мечник, разбойник-лучник, волк. Картинки — набор объектов (<see cref="ObjectSet"/>, имя файла без TEMP_),
    /// три вида: «_side» (бок, смотрит вправо; влево — отражение), «_front» (к зрителю), «_back» (от зрителя).
    /// Нет картинки архетипа — false: остаётся прежний путь (спрайт героя + оттенок по архетипу, E3-3/E3-4).
    /// </summary>
    public static class EnemyVisuals
    {
        /// <summary>Ключи спрайтов по архетипу: [бок, к зрителю, от зрителя] (ключи ObjectSet).</summary>
        static readonly Dictionary<int, string[]> Keys = new Dictionary<int, string[]>
        {
            { 1, new[] { "char_bandit_sword_side", "char_bandit_sword_front", "char_bandit_sword_back" } }, // Melee
            { 2, new[] { "char_bandit_bow_side", "char_bandit_bow_front", "char_bandit_bow_back" } },        // Ranged
            { 3, new[] { "char_wolf_side", "char_wolf_front", "char_wolf_back" } },                          // Beast (волк)
        };

        struct Views
        {
            public Sprite Side;
            public Sprite? Front;
            public Sprite? Back;
        }

        static readonly Dictionary<int, Views?> _cache = new Dictionary<int, Views?>();

        /// <summary>Спрайты врага по архетипу; null — своих картинок нет (тинт-заглушка E3-4 остаётся).</summary>
        public static bool TryViews(byte archetype, out Sprite side, out Sprite? front, out Sprite? back)
        {
            if (!_cache.TryGetValue(archetype, out Views? v))
            {
                v = Load(archetype);
                _cache[archetype] = v;
            }

            if (v == null)
            {
                side = null!;
                front = back = null;
                return false;
            }

            side = v.Value.Side;
            front = v.Value.Front;
            back = v.Value.Back;
            return true;
        }

        static Views? Load(byte archetype)
        {
            if (!Keys.TryGetValue(archetype, out string[]? keys))
            {
                return null;
            }

            Sprite? side = PileView.Find(keys[0]);
            if (side == null)
            {
                return null;
            }

            // Нет вида «к/от зрителя» — подставляем боковой (плавная деградация); обязательность трёх видов проверяют снимки.
            return new Views { Side = side, Front = PileView.Find(keys[1]) ?? side, Back = PileView.Find(keys[2]) ?? side };
        }
    }
}
