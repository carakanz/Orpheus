using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;
using Orpheus.Core.Trig;
using Orpheus.Core.World;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// P-002: partial Simulation - per-file code motion, no behavior change.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Ячейка кадра ввода становится выбранной (предмет в руках); уже выбрана — ничего.</summary>
        private static void ApplySlot(ref SimEntity e, byte slot)
        {
            if (slot != e.Inv.Selected && slot < Inventory.Slots)
            {
                e.Inv.Selected = slot;
            }
        }

        /// <summary>
        /// Изменения ввода, ждущие ближайшего такта, по номеру сущности — то, что пишется в журнал ввода.
        /// Возвращает число записанных в <paramref name="dest"/>.
        /// </summary>
        public int CopyPendingInputs(Span<EntityInput> dest)
        {
            int n = 0;
            for (int i = 0; i < _entityCount; i++)
            {
                if (_hasPending[i])
                {
                    dest[n++] = new EntityInput(_entities[i].Id, _pending[i]);
                }
            }

            return n;
        }

        /// <summary>Направление ввода такта: у тактового существа — ввод такта, у кадрового — последний кадр такта.</summary>
        private Int2 DesiredDirection(int slot)
        {
            int frames = _frameCount[slot];
            Input8 dir = frames < 0
                ? _entities[slot].Input.Direction
                : frames > 0 ? _frames[slot * MaxFramesPerTick + frames - 1].Direction : Input8.None;
            return new Int2(dir.DX, dir.DY);
        }

        /// <summary>
        /// Кадр ввода сущности на ближайший такт. Совпадает с текущим вводом — ничего не меняется
        /// (в журнал идут только изменения ввода). Нет такой сущности — false.
        /// </summary>
        public bool SetInput(int entityId, InputFrame frame)
        {
            int slot = SlotOf(entityId);
            if (slot < 0)
            {
                return false;
            }

            SetInputAt(slot, frame);
            return true;
        }

        /// <summary>Кадр ввода по месту в списке (<see cref="SlotOf"/>).</summary>
        public void SetInputAt(int slot, InputFrame frame)
        {
            if ((uint)slot >= (uint)_entityCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            if (frame == _entities[slot].Input)
            {
                _hasPending[slot] = false;
                return;
            }

            _pending[slot] = frame;
            _hasPending[slot] = true;
        }

        /// <summary>
        /// Кадры ввода 60 Гц на ближайший такт (docs/07, 2.7 «Применение ввода»; 0-019): в такте сущность шагает каждым
        /// кадром по порядку, полшага на кадр (<see cref="Mover.StepFrame"/>), — так же, как предсказание клиента.
        /// Пусто — в этом такте сущность стоит (время персонажа ждёт кадров). Текущий ввод после такта — последний кадр.
        /// Действует один такт; не вызвали — сущность шагает тактом по своему вводу (<see cref="SetInputAt"/>).
        /// В журнал ввода кадры не пишутся (backlog [журнал]): повтор журнала — для тактового ввода.
        /// </summary>
        public void SetFramesAt(int slot, ReadOnlySpan<InputFrame> frames)
        {
            if ((uint)slot >= (uint)_entityCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            if (frames.Length > MaxFramesPerTick)
            {
                throw new ArgumentOutOfRangeException(nameof(frames), frames.Length, $"Кадров на такт не больше {MaxFramesPerTick}.");
            }

            frames.CopyTo(new Span<InputFrame>(_frames, slot * MaxFramesPerTick, MaxFramesPerTick));
            _frameCount[slot] = frames.Length;
        }

    }
}
