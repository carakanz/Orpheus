using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>Цвет цифры урона (E3-1): красная — урон игроку, жёлтая — свой урон по врагу, белая — чужой урон по врагу.</summary>
    public enum DamageTone : byte
    {
        Other = 0,
        ByMe = 1,
        ToPlayer = 2,
    }

    /// <summary>Цифра урона над существом (E3-1): где и когда появилась, сколько, цвет, сильный ли удар, дорожка (сдвиг вбок).</summary>
    public struct DamageNumber
    {
        public int Victim;
        public WorldPos At;
        public int Amount;
        public DamageTone Tone;
        public bool Strong;
        public double Start;
        public int Lane;
    }

    /// <summary>
    /// Лента боя для экрана (E3-1, шаг 3.1): цифры урона, полоски здоровья врагов и экран смерти — только из сообщений
    /// сервера (урон 37, смерть 24, возрождение 25), не из предсказания: ложных цифр нет. Чистый C#: клиент рисует
    /// (<c>CombatHud</c>), стенд снимков кормит теми же сообщениями, тесты проверяют времена и цвета.
    /// Время — секунды клиента (любая монотонная шкала).
    /// </summary>
    public sealed class CombatFeed
    {
        /// <summary>Цифра живёт 0,8 с: летит вверх и тает (карточка E3-1).</summary>
        public const double NumberSec = 0.8;

        /// <summary>Подъём цифры за жизнь, тайлов (с замедлением к концу).</summary>
        public const float RiseTiles = 1.8f;

        /// <summary>Цифра непрозрачна первую половину жизни, потом тает до 0.</summary>
        public const double SolidSec = 0.4;

        /// <summary>Сильный удар — цифра крупнее во столько раз.</summary>
        public const float StrongScale = 1.5f;

        /// <summary>Полоска здоровья врага видна после попадания и ещё 4 с после последнего (карточка E3-1).</summary>
        public const double BarSec = 4.0;

        /// <summary>Затемнение экрана смерти: нарастает за 0,6 с до 0,82 (холст смешивает в линейном цвете — на глаз ~60 %); после возрождения уходит за 0,6 с.</summary>
        public const double FadeSec = 0.6;
        public const float FadeMax = 0.82f;

        /// <summary>Надпись «Вы возродились …» после возрождения, секунд.</summary>
        public const double RespawnNoteSec = 2.5;

        /// <summary>Цифр на экране не больше (старые уходят первыми).</summary>
        public const int MaxNumbers = 48;

        /// <summary>Дорожек цифр на одном существе (частые удары не ложатся друг на друга).</summary>
        public const int Lanes = 3;

        readonly List<DamageNumber> _numbers = new List<DamageNumber>();
        // Последнее попадание по врагу: список по номеру сущности (порядок обхода определён, ORP006).
        readonly List<(int Id, double At)> _lastHit = new List<(int Id, double At)>();

        /// <summary>Свой персонаж (номер сущности).</summary>
        public int LocalId { get; set; }

        /// <summary>Цифры на экране, старые первыми.</summary>
        public IReadOnlyList<DamageNumber> Numbers => _numbers;

        /// <summary>Сообщений «урон» принято лентой.</summary>
        public int DamageCount { get; private set; }

        /// <summary>Урон пришёл: цифра над жертвой (<paramref name="victimAt"/> — её позиция сейчас) и отметка попадания врага.</summary>
        public void OnDamage(in DamageMessage m, WorldPos victimAt, bool victimIsCharacter, double now)
        {
            DamageCount++;
            bool toPlayer = victimIsCharacter || m.Victim == LocalId;
            DamageTone tone = toPlayer ? DamageTone.ToPlayer : m.Attacker == LocalId ? DamageTone.ByMe : DamageTone.Other;
            int lane = 0;
            for (int i = 0; i < _numbers.Count; i++)
            {
                if (_numbers[i].Victim == m.Victim && now - _numbers[i].Start < NumberSec)
                {
                    lane++;
                }
            }

            if (_numbers.Count >= MaxNumbers)
            {
                _numbers.RemoveAt(0);
            }

            _numbers.Add(new DamageNumber
            {
                Victim = m.Victim,
                At = victimAt,
                Amount = m.Amount,
                Tone = tone,
                Strong = m.Strong,
                Start = now,
                Lane = lane % Lanes,
            });
            if (!toPlayer)
            {
                int k = HitIndex(m.Victim);
                if (k >= 0)
                {
                    _lastHit[k] = (m.Victim, now);
                }
                else
                {
                    _lastHit.Add((m.Victim, now));
                }
            }
        }

        /// <summary>Смерть: своя — экран смерти (таймер, убийца, где возродится); чужая — полоска гаснет.</summary>
        public void OnDeath(in DeathMessage m, double now)
        {
            int hit = HitIndex(m.Victim);
            if (hit >= 0)
            {
                _lastHit.RemoveAt(hit);
            }

            if (m.Victim != LocalId)
            {
                return;
            }

            Dead = true;
            DiedAt = now;
            RespawnSec = m.RespawnTicks / (double)Simulation.TickRate;
            KillerId = m.Attacker;
            AtBed = m.AtBed;
        }

        /// <summary>Возрождение своего: экран смерти уходит, надпись «Вы возродились …».</summary>
        public void OnRespawn(in RespawnMessage m, double now)
        {
            if (m.Victim != LocalId || !Dead)
            {
                return;
            }

            Dead = false;
            RespawnedAt = now;
        }

        /// <summary>Убрать отжившие цифры и забытые попадания (раз в кадр).</summary>
        public void Expire(double now)
        {
            _numbers.RemoveAll(n => now - n.Start >= NumberSec);
            _lastHit.RemoveAll(h => now - h.At > BarSec);
        }

        int HitIndex(int id)
        {
            for (int i = 0; i < _lastHit.Count; i++)
            {
                if (_lastHit[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }

        // ---------- цифры ----------

        /// <summary>Подъём цифры возраста <paramref name="age"/> с, тайлов: быстро в начале, медленнее к концу.</summary>
        public static float Rise(double age)
        {
            double t = Clamp01(age / NumberSec);
            return (float)(RiseTiles * (1.0 - (1.0 - t) * (1.0 - t)));
        }

        /// <summary>Непрозрачность цифры: 1 первые <see cref="SolidSec"/>, к концу жизни — 0.</summary>
        public static float Alpha(double age)
        {
            if (age <= SolidSec)
            {
                return 1f;
            }

            return (float)(1.0 - Clamp01((age - SolidSec) / (NumberSec - SolidSec)));
        }

        /// <summary>Размер цифры: «вспышка» ×1,3 → ×1 за 0,12 с; сильный удар — ещё ×<see cref="StrongScale"/>.</summary>
        public static float Scale(double age, bool strong)
        {
            float pop = 1f + 0.3f * (float)(1.0 - Clamp01(age / 0.12));
            return pop * (strong ? StrongScale : 1f);
        }

        /// <summary>Сдвиг дорожки вбок, тайлов: 0, вправо, влево (цифра шириной ~1 тайл — соседние не налезают).</summary>
        public static float LaneOffset(int lane) => lane == 1 ? 0.65f : lane == 2 ? -0.65f : 0f;

        /// <summary>Подъём дорожки, тайлов: следующая цифра начинается чуть выше.</summary>
        public static float LaneLift(int lane) => 0.3f * lane;

        // ---------- полоска врага ----------

        /// <summary>
        /// Полоска здоровья врага видна: живой (<paramref name="healthPct"/> &gt; 0) и либо всегда (элитный, босс —
        /// <paramref name="always"/>), либо попадание было не дольше <see cref="BarSec"/> назад.
        /// </summary>
        public bool BarVisible(int id, double now, bool always, int healthPct)
        {
            if (healthPct <= 0)
            {
                return false;
            }

            int k = HitIndex(id);
            return always || (k >= 0 && now - _lastHit[k].At <= BarSec);
        }

        // ---------- экран смерти ----------

        /// <summary>Свой персонаж мёртв (пришла своя «смерть», ещё не было «возрождения»).</summary>
        public bool Dead { get; private set; }

        public double DiedAt { get; private set; }

        /// <summary>Секунд от смерти до возрождения по правилу ядра (из сообщения «смерть»).</summary>
        public double RespawnSec { get; private set; }

        /// <summary>Кто убил (номер сущности; −1 / 0 — неизвестно).</summary>
        public int KillerId { get; private set; }

        /// <summary>Возродится у своей кровати (иначе у Очага).</summary>
        public bool AtBed { get; private set; }

        public double RespawnedAt { get; private set; } = double.NegativeInfinity;

        /// <summary>Сколько секунд до возрождения (0 — вот-вот; не мёртв — 0).</summary>
        public double RespawnLeft(double now) => Dead ? Math.Max(0.0, RespawnSec - (now - DiedAt)) : 0.0;

        /// <summary>Целых секунд до возрождения для надписи (вверх: 4,2 → 5).</summary>
        public int RespawnLeftWhole(double now) => (int)Math.Ceiling(RespawnLeft(now) - 1e-9);

        /// <summary>Затемнение экрана 0…<see cref="FadeMax"/>: нарастает после смерти, уходит после возрождения.</summary>
        public float Fade(double now)
        {
            if (Dead)
            {
                return FadeMax * (float)Clamp01((now - DiedAt) / FadeSec);
            }

            return FadeMax * (float)(1.0 - Clamp01((now - RespawnedAt) / FadeSec));
        }

        /// <summary>Показывать «Вы возродились …»: первые <see cref="RespawnNoteSec"/> после возрождения.</summary>
        public bool RespawnNote(double now) => !Dead && now - RespawnedAt >= 0 && now - RespawnedAt < RespawnNoteSec;

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
