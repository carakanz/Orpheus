#nullable enable
using System;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Замахи врагов на экране игрока (K-001 по «замах начат» из 0-029/0-033; `02` 15.1, `07` 2.7): сервер шлёт сообщение каждый
    /// такт замаха — здесь повторы отбрасываются по (атакующий, кадр начала). Замах показывается с прихода первого сообщения
    /// до кадра решения удара по этому игроку (<c>ResolveFrame</c> — уже во времени его персонажа): там же на экране удар,
    /// дальше отход. Так показанный замах короче на задержку, но не короче min(d; 0,3 с) — это гарантирует кадр решения сервера.
    /// Время — микросекунды той же шкалы, что у клиента (<c>NetGame.NowUs</c>); без Unity.
    /// </summary>
    public sealed class SwingTrack
    {
        /// <summary>Длина кадра ввода 60 Гц, мкс.</summary>
        public const long FrameUs = 1_000_000 / 60;

        public struct Swing
        {
            public int AttackerId;
            public uint StartFrame;
            public long ShowStartUs;
            public long ContactUs;
        }

        readonly Swing[] _items;
        int _count;

        public SwingTrack(int capacity = 64)
        {
            _items = new Swing[capacity];
        }

        /// <summary>Замахов принято (без повторов).</summary>
        public long Accepted { get; private set; }

        /// <summary>
        /// «Замах начат»: атакующий, кадр начала s (время сервера), кадр решения по этому игроку и его текущий кадр ввода.
        /// Повтор того же замаха — false.
        /// </summary>
        public bool OnSwing(int attackerId, uint startFrame, uint resolveFrame, uint ownFrameNow, long nowUs)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].AttackerId == attackerId && _items[i].StartFrame == startFrame)
                {
                    return false;
                }
            }

            int left = Math.Max(0, unchecked((int)(resolveFrame - ownFrameNow)));
            var sw = new Swing { AttackerId = attackerId, StartFrame = startFrame, ShowStartUs = nowUs, ContactUs = nowUs + left * FrameUs };
            int slot = -1;
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].AttackerId == attackerId)
                {
                    slot = i; // у атакующего один замах на экране — новый заменяет старый
                    break;
                }
            }

            if (slot < 0)
            {
                if (_count == _items.Length)
                {
                    Expire(nowUs);
                }

                slot = _count < _items.Length ? _count++ : 0;
            }

            _items[slot] = sw;
            Accepted++;
            return true;
        }

        /// <summary>Фаза замаха атакующего на миг <paramref name="nowUs"/>: замах / удар / отход с долей 0…1 или <see cref="SwingPhase.None"/>.</summary>
        public SwingPhase Phase(int attackerId, long nowUs, out float progress)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].AttackerId == attackerId)
                {
                    return PhaseOf(_items[i], nowUs, out progress);
                }
            }

            progress = 0f;
            return SwingPhase.None;
        }

        /// <summary>Миг касания (конец показанного замаха) у атакующего; нет замаха — false.</summary>
        public bool TryGet(int attackerId, out Swing swing)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].AttackerId == attackerId)
                {
                    swing = _items[i];
                    return true;
                }
            }

            swing = default;
            return false;
        }

        public static SwingPhase PhaseOf(in Swing s, long nowUs, out float progress)
        {
            long strikeUs = (long)(AnimParams.StrikeSec * 1e6);
            long recoverUs = (long)(AnimParams.RecoverSec * 1e6);
            if (nowUs < s.ContactUs)
            {
                long len = Math.Max(1, s.ContactUs - s.ShowStartUs);
                progress = Math.Max(0f, (float)(nowUs - s.ShowStartUs) / len);
                return SwingPhase.Windup;
            }

            long after = nowUs - s.ContactUs;
            if (after < strikeUs)
            {
                progress = (float)after / strikeUs;
                return SwingPhase.Strike;
            }

            after -= strikeUs;
            if (after < recoverUs)
            {
                progress = (float)after / recoverUs;
                return SwingPhase.Recover;
            }

            progress = 0f;
            return SwingPhase.None;
        }

        /// <summary>Убрать отыгранные замахи (после отхода).</summary>
        public void Expire(long nowUs)
        {
            int keep = 0;
            for (int i = 0; i < _count; i++)
            {
                if (PhaseOf(_items[i], nowUs, out _) != SwingPhase.None)
                {
                    _items[keep++] = _items[i];
                }
            }

            _count = keep;
        }

        public int Count => _count;
    }
}
