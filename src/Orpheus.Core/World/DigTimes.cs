using System;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Время копания — «база × модификатор предмета» (слово Автора 08.11, `BRIEF.md` п. 11b; 0-034). Одно место, данные, без
    /// рефлексии (IL2CPP): база — кадров ввода 60 Гц с зажатым копанием, модификатор — проценты на id объекта (100 = ×1).
    /// Правятся и база (<see cref="BaseFrames"/>), и модификатор конкретного объекта (<see cref="SetModifier"/>).
    /// <b>Время 0 — разрушить нельзя</b>: модификатор 0 или база 0 — отказ «нельзя».
    /// <para>
    /// Числа — <i>(прикидка 0-034)</i>: база 15 кадров = 0,25 с («0,25 с — ок»); скала ×1, дерево ×1,2, залежь ×2, пень ×0,6; куст ×0,5 и гранит ×1,5 (1-017).
    /// Постройки — 0 (снос — потом); кто копается вообще — ещё и <see cref="EditRules.IsDiggable"/> (природный одиночный).
    /// </para>
    /// Таблица — у каждого, кто считает копание (сервер, клиент, бот): они должны совпадать, по умолчанию — одна и та же.
    /// </summary>
    public sealed class DigTimes
    {
        /// <summary>База по умолчанию: 15 кадров = 0,25 с.</summary>
        public const int DefaultBaseFrames = 15;

        /// <summary>Модификатор «×1» в процентах.</summary>
        public const int One = 100;

        /// <summary>Наибольший модификатор (×100) — чтобы время помещалось в int с запасом.</summary>
        public const int MaxModifier = 10000;

        /// <summary>Наибольшая база: 60 с.</summary>
        public const int MaxBaseFrames = 3600;

        private readonly ushort[] _modifier = new ushort[ObjectId.MaxKnownId + 1];
        private int _baseFrames;

        /// <summary>Таблица по умолчанию (прикидка 0-034).</summary>
        public DigTimes()
        {
            _baseFrames = DefaultBaseFrames;
            _modifier[ObjectId.Rock] = One;
            _modifier[ObjectId.Tree] = 120;
            _modifier[ObjectId.Ore] = 200;
            _modifier[ObjectId.Stump] = 60;

            // Объекты генератора v1 (1-017), по отношению времён `01` 5 к скале (1 с): куст 0,5 с, гранит 1,5 с (прикидка).
            _modifier[ObjectId.Bush] = 50;
            _modifier[ObjectId.Granite] = 150;
        }

        /// <summary>База — кадров 60 Гц (0…<see cref="MaxBaseFrames"/>); 0 — не копается ничего.</summary>
        public int BaseFrames
        {
            get => _baseFrames;
            set
            {
                if (value < 0 || value > MaxBaseFrames)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"База — от 0 до {MaxBaseFrames} кадров.");
                }

                _baseFrames = value;
            }
        }

        /// <summary>Модификатор объекта в процентах (по id, поворот не важен); неизвестный id и «часть» — 0.</summary>
        public int ModifierOf(ushort obj)
        {
            if (ObjectId.IsPart(obj))
            {
                return 0;
            }

            int id = ObjectId.IdOf(obj);
            return id < _modifier.Length ? _modifier[id] : 0;
        }

        /// <summary>Задать модификатор объекта <paramref name="id"/> (1…<see cref="ObjectId.MaxKnownId"/>) в процентах; 0 — неразрушим.</summary>
        public void SetModifier(ushort id, int percent)
        {
            if (id == ObjectId.Empty || id > ObjectId.MaxKnownId)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, $"id — от 1 до {ObjectId.MaxKnownId}.");
            }

            if (percent < 0 || percent > MaxModifier)
            {
                throw new ArgumentOutOfRangeException(nameof(percent), percent, $"Модификатор — от 0 до {MaxModifier} %.");
            }

            _modifier[id] = (ushort)percent;
        }

        /// <summary>
        /// Кадров копания объекта: база × модификатор / 100, округление к ближайшему, не меньше 1 кадра. 0 — неразрушим.
        /// </summary>
        public int FramesOf(ushort obj)
        {
            int mod = ModifierOf(obj);
            if (mod == 0 || _baseFrames == 0)
            {
                return 0;
            }

            int frames = (_baseFrames * mod + One / 2) / One;
            return frames < 1 ? 1 : frames;
        }
    }
}
