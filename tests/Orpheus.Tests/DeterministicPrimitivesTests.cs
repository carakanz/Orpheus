using System;
using System.Collections.Generic;
using System.Globalization;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.Trig;
using Xunit;
using CoreRng = Orpheus.Core.Rng.SplitMix64;
using RefRng = Orpheus.Tests.SplitMix64;

namespace Orpheus.Tests
{
    /// <summary>
    /// Детерминированные примитивы ядра (задача 0-003, шаги 3–4; docs/07, 11.5 и 1.3):
    /// эталонные числа SplitMix64 и DetRandom закреплены тестом, таблица синусов — в допуске
    /// ≤ 1 младшего разряда против <c>Math.Sin</c>, atan2 — целый, точность до 1 шага угла.
    /// В тестах <c>Math.*</c> разрешён: здесь он эталон, а не код симуляции.
    /// </summary>
    public class DeterministicPrimitivesTests
    {
        // ---------- SplitMix64: эталон ----------

        /// <summary>Первые 5 значений Next() при зерне 42 (зафиксированы 05.11, .NET 9, x64).</summary>
        private static readonly ulong[] Seed42_First5 =
        {
            13679457532755275413UL,
            2949826092126892291UL,
            5139283748462763858UL,
            6349198060258255764UL,
            701532786141963250UL,
        };

        /// <summary>Состояние SplitMix64(42) после пяти вызовов Next().</summary>
        private const ulong Seed42_StateAfter5 = 1663341875487337619UL;

        [Fact]
        public void D00_Det_SplitMix64_Seed42_First5Values()
        {
            var rng = new CoreRng(42);
            for (int i = 0; i < Seed42_First5.Length; i++)
            {
                Assert.Equal(Seed42_First5[i], rng.Next());
            }

            Assert.Equal(Seed42_StateAfter5, rng.State);
        }

        [Fact]
        public void D00_Det_SplitMix64_MatchesIndependentReference()
        {
            var core = new CoreRng(12345);
            var reference = new RefRng(12345);
            for (int i = 0; i < 10_000; i++)
            {
                Assert.Equal(reference.Next(), core.Next());
            }
        }

        [Fact]
        public void D00_Det_SplitMix64_Seed42_Int32AndBounded()
        {
            int[] expectedInt32 = { 803958421, -1301876477, 319790930, 239788948, 608707570 };
            var anyInt = new CoreRng(42);
            for (int i = 0; i < expectedInt32.Length; i++)
            {
                Assert.Equal(expectedInt32[i], anyInt.NextAnyInt());
            }

            int[] expectedBounded = { 224917, 192771, 237394, 189332, 9202 };
            var bounded = new CoreRng(42);
            for (int i = 0; i < expectedBounded.Length; i++)
            {
                Assert.Equal(expectedBounded[i], bounded.NextInt(262144));
            }

            // Следствие: NextInt(bound) = Next() % bound — те же пять значений, что выше.
            var floats = new CoreRng(42);
            float[] expectedFloats = { 0.74156487f, 0.15991038f, 0.2786011f, 0.34419066f, 0.038030148f };
            for (int i = 0; i < expectedFloats.Length; i++)
            {
                Assert.Equal(expectedFloats[i], floats.NextFloat01(), 6);
            }
        }

        [Fact]
        public void D00_Det_SplitMix64_Hash_Reference()
        {
            Assert.Equal(12058926934050108962UL, CoreRng.Hash(0, 0, 42));
            Assert.Equal(7711417314423990183UL, CoreRng.Hash(1, 2, 42));
            Assert.Equal(14885136422063328437UL, CoreRng.Hash(4095, 4095, 42));
            Assert.Equal(12869769814138542410UL, CoreRng.Hash(-1, -1, 42));
            Assert.Equal(15313231649230124537UL, CoreRng.Hash(100, 200, 300, 42));
        }

        [Fact]
        public void D00_Det_SplitMix64_Hash_StableAndDistinct()
        {
            // Тот же тайл и то же зерно — тот же хэш: вид объекта в тайле (docs/07, 3.3).
            Assert.Equal(CoreRng.Hash(7, 9, 42), CoreRng.Hash(7, 9, 42));
            Assert.NotEqual(CoreRng.Hash(7, 9, 42), CoreRng.Hash(9, 7, 42));
            Assert.NotEqual(CoreRng.Hash(7, 9, 42), CoreRng.Hash(7, 9, 43));

            // 10 000 тайлов одного зерна — все хэши разные (вид соседних тайлов не повторяется).
            var seen = new HashSet<ulong>();
            for (int x = 0; x < 100; x++)
            {
                for (int y = 0; y < 100; y++)
                {
                    Assert.True(seen.Add(CoreRng.Hash(x, y, 42)), $"Повтор хэша на тайле ({x}, {y})");
                }
            }
        }

        [Fact]
        public void D00_Det_SplitMix64_Ranges()
        {
            var rng = new CoreRng(7);
            var floats = new CoreRng(7);
            for (int i = 0; i < 50_000; i++)
            {
                Assert.InRange(rng.NextInt(100), 0, 99);
                Assert.InRange(rng.NextInt(-5, 5), -5, 4);
                Assert.InRange(rng.NextBits(13), 0, 8191);

                float f = floats.NextFloat01();
                Assert.True(f >= 0f && f < 1f, $"NextFloat01 вне [0, 1): {f.ToString(CultureInfo.InvariantCulture)}");
            }

            var bits = new CoreRng(1);
            Assert.InRange(bits.NextBits(1), 0, 1);
            bits.NextBits(64); // не бросает исключение на полном диапазоне
        }

        [Fact]
        public void D00_Det_SplitMix64_StateRoundTrip()
        {
            var a = new CoreRng(99);
            for (int i = 0; i < 7; i++)
            {
                a.Next();
            }

            ulong state = a.State;
            var b = new CoreRng(0);
            b.SetState(state);
            for (int i = 0; i < 50; i++)
            {
                Assert.Equal(a.Next(), b.Next());
            }
        }

        [Fact]
        public void D00_Det_SplitMix64_RejectsBadBounds()
        {
            var rng = new CoreRng(1);
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(-7));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextBits(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextBits(65));
        }

        [Fact]
        public void D00_Det_SplitMix64_BoolIsBalanced()
        {
            var rng = new CoreRng(2026);
            const int draws = 100_000;
            int trueCount = 0;
            for (int i = 0; i < draws; i++)
            {
                if (rng.NextBool())
                {
                    trueCount++;
                }
            }

            // Ожидаем половину; допуск — ~6 средних квадратических отклонений бинома (σ ≈ 158).
            Assert.InRange(trueCount, draws / 2 - 950, draws / 2 + 950);
        }

        // ---------- DetRandom ----------

        [Fact]
        public void D00_Det_DetRandom_ForSystem_Reference()
        {
            ulong[] expectedSeeds =
            {
                12058926934050108962UL,
                13679457532755275413UL,
                2949826092126892291UL,
                5139283748462763858UL,
                6349198060258255764UL,
                701532786141963250UL,
            };

            for (int system = 0; system < expectedSeeds.Length; system++)
            {
                Assert.Equal(expectedSeeds[system], DetRandom.ForSystem(42, system).State);
            }

            Assert.Equal(10996452266160306281UL, DetRandom.ForSystem(42, 0).Next());
            Assert.Equal(81, DetRandom.ForSystem(42, 0).NextInt(100));
        }

        [Fact]
        public void D00_Det_DetRandom_OrderOfSystemsDoesNotMatter()
        {
            // Поток системы не зависит от того, сколько бросков сделала соседняя система:
            // порядок обновления систем не влияет на результат (docs/07, 11.5).
            var before = DetRandom.ForSystem(42, 3);
            for (int i = 0; i < 500; i++)
            {
                DetRandom.ForSystem(42, 2).NextInt(1000);
            }

            var after = DetRandom.ForSystem(42, 3);
            Assert.Equal(before.State, after.State);
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(before.Next(), after.Next());
            }
        }

        [Fact]
        public void D00_Det_DetRandom_SameSeedSameStream()
        {
            var a = DetRandom.ForSystem(20261105, 11);
            var b = DetRandom.ForSystem(20261105, 11);
            for (int i = 0; i < 1000; i++)
            {
                Assert.Equal(a.Next(), b.Next());
            }

            Assert.NotEqual(a.State, DetRandom.ForSystem(20261105, 12).State);
            Assert.NotEqual(a.State, DetRandom.ForSystem(20261106, 11).State);
        }

        [Fact]
        public void D00_Det_DetRandom_StateRoundTrip()
        {
            var a = DetRandom.ForSystem(42, 4);
            for (int i = 0; i < 13; i++)
            {
                a.Next();
            }

            ulong state = a.State;
            var b = DetRandom.ForSystem(0, 0);
            b.SetState(state);
            for (int i = 0; i < 40; i++)
            {
                Assert.Equal(a.Next(), b.Next());
            }
        }

        // ---------- TrigTable: синус и косинус ----------

        [Fact]
        public void D00_Det_TrigTable_SinCos_Within1Lsb()
        {
            const int scale = TrigTable.Scale;
            double maxSin = 0, maxCos = 0;
            int worstSin = 0, worstCos = 0;

            for (int raw = 0; raw < Angle16.Steps; raw++)
            {
                double rad = raw * Math.PI / 32768.0; // 65 536 шагов на полный круг
                double exactSin = Math.Sin(rad) * scale;
                double exactCos = Math.Cos(rad) * scale;

                double errorSin = Math.Abs(TrigTable.SinRaw(raw) - exactSin);
                double errorCos = Math.Abs(TrigTable.CosRaw(raw) - exactCos);

                if (errorSin > maxSin)
                {
                    maxSin = errorSin;
                    worstSin = raw;
                }

                if (errorCos > maxCos)
                {
                    maxCos = errorCos;
                    worstCos = raw;
                }

                // Sin(Angle16) и Cos(Angle16) совпадают с таблицей по сырому углу.
                Assert.Equal(TrigTable.Sin(Angle16.FromRaw(raw)), TrigTable.SinRaw(raw));
                Assert.Equal(TrigTable.Cos(Angle16.FromRaw(raw)), TrigTable.CosRaw(raw));
            }

            Assert.True(maxSin <= 1.0, $"Синус вне допуска: {maxSin.ToString("F", CultureInfo.InvariantCulture)} LSB на угле {worstSin}");
            Assert.True(maxCos <= 1.0, $"Косинус вне допуска: {maxCos.ToString("F", CultureInfo.InvariantCulture)} LSB на угле {worstCos}");
        }

        [Fact]
        public void D00_Det_TrigTable_KnownAnglesMonotonicAndSymmetric()
        {
            Assert.Equal(0, TrigTable.Sin(Angle16.FromRaw(0)));
            Assert.Equal(TrigTable.Scale - 1, TrigTable.Cos(Angle16.FromRaw(0)));
            Assert.Equal(TrigTable.Scale - 1, TrigTable.Sin(Angle16.FromRaw(Angle16.Quarter)));
            Assert.Equal(0, TrigTable.Cos(Angle16.FromRaw(Angle16.Quarter)));

            // На четверти круга синус не убывает.
            for (int a = 1; a <= Angle16.Quarter; a++)
            {
                Assert.True(TrigTable.SinRaw(a) >= TrigTable.SinRaw(a - 1), $"Синус убывает на шаге {a}");
            }

            // Значения в диапазоне Q15.
            for (int raw = 0; raw < Angle16.Steps; raw += 7)
            {
                Assert.InRange(TrigTable.SinRaw(raw), -(TrigTable.Scale - 1), TrigTable.Scale - 1);
            }

            // Четверти: sin(180° − x) = sin x, sin(180° + x) = −sin x, sin(360° − x) = −sin x.
            var rnd = new CoreRng(5);
            for (int i = 0; i < 2000; i++)
            {
                int x = rnd.NextInt(Angle16.Quarter + 1);
                Assert.Equal(TrigTable.SinRaw(Angle16.Half - x), TrigTable.SinRaw(x));
                Assert.Equal(-TrigTable.SinRaw(x), TrigTable.SinRaw(Angle16.Half + x));
                Assert.Equal(-TrigTable.SinRaw(x), TrigTable.SinRaw((Angle16.Steps - x) & 0xFFFF));
            }
        }

        [Fact]
        public void D00_Det_TrigTable_SinTimesMatchesMath()
        {
            var rnd = new CoreRng(31);
            for (int i = 0; i < 20_000; i++)
            {
                Angle16 a = Angle16.FromRaw(rnd.NextInt(Angle16.Steps));
                int amp = rnd.NextInt(1, 200_001);

                CheckProduct(TrigTable.SinTimes(a, amp), Math.Sin(a.Raw * Math.PI / 32768.0) * amp, a, amp, "SinTimes");
                CheckProduct(TrigTable.CosTimes(a, amp), Math.Cos(a.Raw * Math.PI / 32768.0) * amp, a, amp, "CosTimes");
            }
        }

        // Допуск: ошибка таблицы ≤ 1 LSB (даёт ≤ amp/32768 после умножения) + 1 единица округления.
        private static void CheckProduct(int got, double exact, Angle16 a, int amp, string what)
        {
            double tol = amp / 32768.0 + 1.0;
            Assert.True(Math.Abs(got - exact) <= tol,
                $"{what}({a.Raw}, {amp}) = {got}, ожидание ~{exact.ToString("F2", CultureInfo.InvariantCulture)} (допуск {tol.ToString("F2", CultureInfo.InvariantCulture)})");
        }

        [Fact]
        public void D00_Det_TrigTable_Direction_KnownVectors()
        {
            Assert.Equal(new Int2(51, 0), TrigTable.Direction(Angle16.FromRaw(0), 51));
            Assert.Equal(new Int2(0, 51), TrigTable.Direction(Angle16.FromRaw(Angle16.Quarter), 51));
            Assert.Equal(new Int2(-51, 0), TrigTable.Direction(Angle16.FromRaw(Angle16.Half), 51));
            Assert.Equal(new Int2(0, -51), TrigTable.Direction(Angle16.FromRaw(3 * Angle16.Quarter), 51));

            // 45°: 51 · √2/2 = 36,06 → 36 по обеим осям (бег по диагонали, docs/01, 1).
            Assert.Equal(new Int2(36, 36), TrigTable.Direction(Angle16.FromRaw(8192), 51));
        }

        [Fact]
        public void D00_Det_TrigTable_Direction_LengthPreserved()
        {
            var rnd = new CoreRng(17);
            for (int i = 0; i < 20_000; i++)
            {
                Angle16 a = Angle16.FromRaw(rnd.NextInt(Angle16.Steps));
                int amp = rnd.NextInt(1, 4001);
                Int2 v = TrigTable.Direction(a, amp);

                double len = Math.Sqrt(v.X * (double)v.X + v.Y * (double)v.Y);
                Assert.True(Math.Abs(len - amp) <= 2.5,
                    $"|Direction| = {len.ToString("F3", CultureInfo.InvariantCulture)} при амплитуде {amp}, угол {a.Raw}");
            }
        }

        // ---------- TrigTable: atan2 ----------

        [Fact]
        public void D00_Det_TrigTable_Atan2_AxesAndDiagonals()
        {
            Assert.Equal(0, TrigTable.Atan2(0, 1).Raw);
            Assert.Equal(Angle16.Quarter, TrigTable.Atan2(1, 0).Raw);
            Assert.Equal(Angle16.Half, TrigTable.Atan2(0, -1).Raw);
            Assert.Equal(3 * Angle16.Quarter, TrigTable.Atan2(-1, 0).Raw);

            Assert.Equal(8192, TrigTable.Atan2(1, 1).Raw);
            Assert.Equal(24576, TrigTable.Atan2(1, -1).Raw);
            Assert.Equal(40960, TrigTable.Atan2(-1, -1).Raw);
            Assert.Equal(57344, TrigTable.Atan2(-1, 1).Raw);

            Assert.Equal(0, TrigTable.Atan2(0, 0).Raw); // ноль без исключения
        }

        [Fact]
        public void D00_Det_TrigTable_Atan2_Within1Step()
        {
            int maxErr = 0, worstX = 0, worstY = 0;
            var rnd = new CoreRng(7);

            for (int i = 0; i < 200_000; i++)
            {
                int x, y;
                switch (i % 4)
                {
                    case 0:
                        x = rnd.NextInt(-40, 41);
                        y = rnd.NextInt(-40, 41);
                        break;
                    case 1:
                        x = rnd.NextInt(-1_000_000, 1_000_001);
                        y = rnd.NextInt(-1_000_000, 1_000_001);
                        break;
                    case 2:
                        x = rnd.NextAnyInt();
                        y = rnd.NextAnyInt();
                        break;
                    default:
                        int k = rnd.NextInt(1, 18);
                        x = (1 << k) + rnd.NextInt(-3, 4);
                        y = -(1 << (k - 1)) + rnd.NextInt(-3, 4);
                        break;
                }

                if (x == 0 && y == 0)
                {
                    continue;
                }

                int got = TrigTable.Atan2(y, x).Raw;
                int expected = (int)Math.Round(Math.Atan2(y, x) * Angle16.Steps / (2.0 * Math.PI)) & 0xFFFF;
                int diff = Math.Abs(((got - expected + 98304) % 65536) - 32768);
                if (diff > maxErr)
                {
                    maxErr = diff;
                    worstX = x;
                    worstY = y;
                }
            }

            // 1 шаг угла = 0,0055° (docs/07, 11.5: прицел считается на u16-углах).
            Assert.True(maxErr <= 1, $"Atan2 даёт ошибку {maxErr} шагов угла на векторе ({worstX}, {worstY})");
        }

        [Fact]
        public void D00_Det_TrigTable_Atan2RoundTrip()
        {
            // atan2 → sin/cos воспроизводят направление вектора: целочисленная тригонометрия без Math.
            (int x, int y, int expSin, int expCos)[] cases =
            {
                (4, 3, 19661, 26214),    // 3/5 и 4/5 от 32 768
                (3, 4, 26214, 19661),
                (1, 1, 23170, 23170),    // √2/2 · 32 768 = 23 170
                (-5, 12, 30247, -12603), // 12/13 и −5/13
                (12, -5, -12603, 30247),
            };

            foreach ((int x, int y, int expSin, int expCos) in cases)
            {
                Angle16 a = TrigTable.Atan2(y, x);
                Assert.True(Math.Abs(TrigTable.Sin(a) - expSin) <= 2,
                    $"sin(atan2({y}, {x})) = {TrigTable.Sin(a)}, ожидание {expSin}");
                Assert.True(Math.Abs(TrigTable.Cos(a) - expCos) <= 2,
                    $"cos(atan2({y}, {x})) = {TrigTable.Cos(a)}, ожидание {expCos}");
            }
        }

        // ---------- Angle16 ----------

        [Fact]
        public void D00_Det_Angle16_WrapsBy65536()
        {
            Assert.Equal((ushort)65535, Angle16.FromRaw(-1).Raw);
            Assert.Equal((ushort)1, Angle16.FromRaw(65537).Raw);
            Assert.Equal((ushort)0, (Angle16.FromRaw(65535) + Angle16.FromRaw(1)).Raw);
            Assert.Equal((ushort)65535, (Angle16.FromRaw(0) - Angle16.FromRaw(1)).Raw);
            Assert.Equal((ushort)32768, (-Angle16.FromRaw(32768)).Raw);
            Assert.Equal(Angle16.FromRaw(40000), Angle16.FromRaw(40000 + 65536));
            Assert.NotEqual(Angle16.FromRaw(1), Angle16.FromRaw(2));
        }

        [Fact]
        public void D00_Det_Angle16_QuartersAndDirection()
        {
            Assert.Equal(0, Angle16.FromRaw(0).QuarterTurn);
            Assert.Equal(1, Angle16.FromRaw(Angle16.Quarter).QuarterTurn);
            Assert.Equal(2, Angle16.FromRaw(Angle16.Half).QuarterTurn);
            Assert.Equal(3, Angle16.FromRaw(3 * Angle16.Quarter).QuarterTurn);

            Assert.Equal(Angle16.FromRaw(Angle16.Half), Angle16.FromDirection(new Int2(-1, 0)));
            Assert.Equal(Angle16.FromRaw(3 * Angle16.Quarter), Angle16.FromDirection(new Int2(0, -1)));
            Assert.Equal(Angle16.FromRaw(0), Angle16.FromDirection(new Int2(7, 0)));
        }

        [Fact]
        public void D00_Det_Angle16_TrigHelpersMatchTable()
        {
            var rnd = new CoreRng(23);
            for (int i = 0; i < 20_000; i++)
            {
                Angle16 a = Angle16.FromRaw(rnd.NextInt(Angle16.Steps));
                Assert.Equal(TrigTable.Sin(a), a.Sin);
                Assert.Equal(TrigTable.Cos(a), a.Cos);
                Assert.Equal(TrigTable.SinTimes(a, 1000), a.SinBy(1000));
                Assert.Equal(TrigTable.CosTimes(a, 1000), a.CosBy(1000));
                Assert.Equal(TrigTable.Direction(a, 1000), a.Direction(1000));
                Assert.Equal(TrigTable.Atan2(a.Sin, a.Cos), Angle16.Atan2(a.Sin, a.Cos));
            }
        }
    }
}
