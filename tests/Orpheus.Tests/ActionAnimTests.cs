using System;
using Orpheus.Client.Anim;
using Orpheus.Core.Sim;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Анимации действий (карточка E2-7): чистые функции <c>client/Assets/Scripts/Anim/Pose/ActionAnim.cs</c> и слой действий
    /// <see cref="ProceduralAnimator"/> — выбор анимации по предмету, такт удара по времени копания ядра, независимость от
    /// частоты кадров, направление, жесты стройки и подбора, меч в руке.
    /// </summary>
    public class ActionAnimTests
    {
        [Fact]
        public void E27_LookAndAction_ByItem()
        {
            Assert.Equal(HeldLook.None, ActionAnim.LookOf(Items.None));
            Assert.Equal(HeldLook.Axe, ActionAnim.LookOf(Items.StoneAxe));
            Assert.Equal(HeldLook.Axe, ActionAnim.LookOf(Items.CopperAxe));
            Assert.Equal(HeldLook.Pick, ActionAnim.LookOf(Items.StonePickaxe));
            Assert.Equal(HeldLook.Pick, ActionAnim.LookOf(Items.CopperPickaxe));
            Assert.Equal(HeldLook.Hammer, ActionAnim.LookOf(Items.Hammer));
            Assert.Equal(HeldLook.Sword, ActionAnim.LookOf(Items.WoodenSword));
            Assert.Equal(HeldLook.Bow, ActionAnim.LookOf(Items.SimpleBow));
            Assert.Equal(HeldLook.Torch, ActionAnim.LookOf(Items.Torch));
            Assert.Equal(HeldLook.Item, ActionAnim.LookOf(Items.Wood));
            Assert.Equal(HeldLook.Item, ActionAnim.LookOf(Items.StoneWall));
            Assert.True(ActionAnim.IsGripped(HeldLook.Axe));
            Assert.False(ActionAnim.IsGripped(HeldLook.Item));

            Assert.Equal(ActionKind.Chop, ActionAnim.DigActionOf(Items.StoneAxe));
            Assert.Equal(ActionKind.Mine, ActionAnim.DigActionOf(Items.CopperPickaxe));
            Assert.Equal(ActionKind.Hammer, ActionAnim.DigActionOf(Items.Hammer));
            Assert.Equal(ActionKind.Poke, ActionAnim.DigActionOf(Items.Wood));
            Assert.Equal(ActionKind.Poke, ActionAnim.DigActionOf(Items.None));
        }

        [Fact]
        public void E27_Beats_WholeNumber_LastImpactAtCompletion()
        {
            Assert.Equal(1, ActionAnim.BeatsFor(15));   // 0,25 с — один замах
            Assert.Equal(2, ActionAnim.BeatsFor(54));   // 0,9 с — два по 0,45
            Assert.Equal(3, ActionAnim.BeatsFor(75));   // 1,25 с — три
            Assert.Equal(1, ActionAnim.BeatsFor(0));
            foreach (int frames in new[] { 9, 15, 18, 30, 54, 75, 120, 300 })
            {
                int beats = ActionAnim.BeatsFor(frames);
                float total = frames / 60f;
                Assert.Equal(total, ActionAnim.BeatSec(frames) * beats, 4);

                // Ударов за всё копание — ровно по числу замахов; последний — в кадр, когда ядро снимает объект.
                Assert.Equal(beats, ActionAnim.ImpactsBetween(0f, total, frames));
                Assert.Equal(1, ActionAnim.ImpactsBetween(total - 1f / 60f, total, frames));
                Assert.Equal(0, ActionAnim.ImpactsBetween(total, total + 2f, frames)); // после — новых ударов нет
            }
        }

        [Fact]
        public void E27_BeatPhase_EndsInRecover_NoNewSwingWithoutNewDig()
        {
            float u = ActionAnim.BeatPhase(0.2f, 54, out int beat);
            Assert.Equal(0, beat);
            Assert.InRange(u, 0.44f, 0.45f);
            ActionAnim.BeatPhase(0.5f, 54, out beat);
            Assert.Equal(1, beat);
            u = ActionAnim.BeatPhase(5f, 54, out beat); // ответ сервера задержался
            Assert.Equal(2, beat);
            Assert.Equal(ActionAnim.RecoverShare, u, 4);
        }

        [Theory]
        [InlineData(ActionKind.Chop)]
        [InlineData(ActionKind.Mine)]
        [InlineData(ActionKind.Hammer)]
        public void E27_ToolSwing_RaisesThenStrikesAtBeatEnd(ActionKind kind)
        {
            float top = ActionAnim.ToolSwing(kind, ActionAnim.StrikeShare, false).HandDeg;
            float hit = ActionAnim.ToolSwing(kind, 1f, false).HandDeg;
            float carry = ActionAnim.CarryToolDeg;
            Assert.True(top > carry + 40f, $"верх замаха {top} выше «несёт» {carry}");
            Assert.True(hit < carry - 30f, $"удар {hit} ниже «несёт» {carry}");

            // Подъём монотонный, удар монотонный (сверху вниз).
            float prev = float.MinValue;
            for (float x = ActionAnim.RecoverShare; x <= ActionAnim.StrikeShare; x += 0.02f)
            {
                float a = ActionAnim.ToolSwing(kind, x, false).HandDeg;
                Assert.True(a >= prev - 1e-3f);
                prev = a;
            }

            prev = float.MaxValue;
            for (float x = ActionAnim.StrikeShare; x <= 1f; x += 0.02f)
            {
                float a = ActionAnim.ToolSwing(kind, x, false).HandDeg;
                Assert.True(a <= prev + 1e-3f);
                prev = a;
            }

            // Первый замах начинается от «несёт», без скачка.
            Assert.Equal(carry, ActionAnim.ToolSwing(kind, 0f, true).HandDeg, 3);

            // Отдача: после удара предмет подскакивает (угол выше удара), тело отходит назад от выпада.
            ActionAnim.Swing r = ActionAnim.ToolSwing(kind, ActionAnim.RecoverShare * 0.3f, false);
            Assert.True(r.HandDeg > hit);
            Assert.True(r.Forward < ActionAnim.ToolSwing(kind, 1f, false).Forward);
        }

        [Fact]
        public void E27_Mine_StrikesLowerAndFurtherThanChop()
        {
            ActionAnim.Swing chop = ActionAnim.ToolSwing(ActionKind.Chop, 1f, false);
            ActionAnim.Swing mine = ActionAnim.ToolSwing(ActionKind.Mine, 1f, false);
            Assert.True(mine.HandDeg < chop.HandDeg);
            Assert.True(mine.Forward > chop.Forward);
        }

        static ActorInput Dig(ActionKind kind, float sec, int frames, float dirX = 1f) => new ActorInput
        {
            Digging = true, DigDirX = dirX, Action = kind, ActionSec = sec, ActionFrames = frames, Held = HeldLook.Axe,
        };

        /// <summary>Прогон копания тайла шагом кадра <paramref name="dt"/>: удары по кадрам и угол руки в миг <paramref name="probe"/>.</summary>
        static int RunDig(float dt, int frames, float probe, out float handAtProbe)
        {
            var a = new ProceduralAnimator();
            float total = frames / 60f;
            int impacts = 0;
            handAtProbe = float.NaN;
            int n = (int)Math.Ceiling((total + 0.3f) / dt);
            for (int i = 1; i <= n; i++)
            {
                float t = Math.Min(i * dt, total);
                Pose p = a.Step(dt, Dig(ActionKind.Chop, t, frames));
                impacts += p.DigPokeNow ? 1 : 0;
                if (float.IsNaN(handAtProbe) && i * dt >= probe - 1e-5f)
                {
                    handAtProbe = p.HandDeg;
                }
            }

            return impacts;
        }

        [Fact]
        public void E27_Impacts_InBeat_IndependentOfFrameRate()
        {
            const int frames = 75; // три замаха
            int at30 = RunDig(1f / 30f, frames, 0.6f, out float h30);
            int at60 = RunDig(1f / 60f, frames, 0.6f, out float h60);
            int at144 = RunDig(1f / 144f, frames, 0.6f, out float h144);
            Assert.Equal(3, at60);
            Assert.Equal(3, at30);
            Assert.Equal(3, at144);
            Assert.Equal(h60, h30, 0);
            Assert.InRange(h144, h60 - 2f, h60 + 2f);
        }

        [Fact]
        public void E27_Impact_FrameMatchesCoreDigStep()
        {
            // Удар показывается ровно в тот кадр, когда счёт кадров копания проходит конец замаха.
            const int frames = 54;
            var a = new ProceduralAnimator();
            int beatFrames = frames / ActionAnim.BeatsFor(frames);
            for (int f = 1; f <= frames; f++)
            {
                Pose p = a.Step(1f / 60f, Dig(ActionKind.Chop, f / 60f, frames));
                Assert.Equal(f % beatFrames == 0, p.DigPokeNow);
            }
        }

        [Fact]
        public void E27_NewDig_RestartsBeats()
        {
            var a = new ProceduralAnimator();
            int impacts = 0;
            for (int tile = 0; tile < 2; tile++)
            {
                for (int f = 1; f <= 30; f++)
                {
                    impacts += a.Step(1f / 60f, Dig(ActionKind.Chop, f / 60f, 30)).DigPokeNow ? 1 : 0;
                }
            }

            Assert.Equal(2, impacts);
        }

        [Fact]
        public void E27_Facing_TowardTarget_HandMirroredByRig()
        {
            var a = new ProceduralAnimator();
            Pose p = default;
            for (int i = 0; i < 10; i++)
            {
                p = a.Step(1f / 60f, Dig(ActionKind.Chop, i / 60f, 54, dirX: -1f));
            }

            Assert.False(a.FacingRight);
            Assert.Equal(AnimParams.SpriteFacesRight, p.FlipX);

            // Цель выше — рука выше на ударе (наводится к тайлу).
            float level = ActionAnim.ToolSwing(ActionKind.Chop, 1f, false).HandDeg + ActionAnim.AimDeg(1f, 0f) * ActionAnim.AimFollow;
            float up = ActionAnim.ToolSwing(ActionKind.Chop, 1f, false).HandDeg + ActionAnim.AimDeg(0.5f, 0.86f) * ActionAnim.AimFollow;
            Assert.True(up > level + 15f);
            Assert.Equal(ActionAnim.AimDeg(1f, 0.5f), ActionAnim.AimDeg(-1f, 0.5f), 4); // зеркало не меняет угол цели
        }

        [Fact]
        public void E27_BuildGesture_FacesGhost_ReachesAndReturns()
        {
            var a = new ProceduralAnimator();
            a.Step(1f / 60f, new ActorInput { Held = HeldLook.Item, VelX = 0f });
            Assert.True(a.FacingRight);
            a.OnBuild(-1f, -0.3f);
            Assert.True(a.Gesturing);
            float maxReach = 0f;
            Pose p = default;
            for (int i = 0; i < 30; i++)
            {
                p = a.Step(1f / 60f, new ActorInput { Held = HeldLook.Item });
                maxReach = Math.Max(maxReach, p.HandReachX);
            }

            Assert.False(a.FacingRight); // повернулся к призраку
            Assert.False(a.Gesturing);
            Assert.True(maxReach > 0.15f);
            Assert.Equal(0f, p.HandReachX, 3);
        }

        [Fact]
        public void E27_Pickup_BendsDown_ThenStraightens()
        {
            var a = new ProceduralAnimator();
            a.OnPickup(0.6f, -0.4f);
            float minScaleY = 1f, minReachY = 0f;
            Pose p = default;
            for (int i = 0; i < 24; i++)
            {
                p = a.Step(1f / 60f, new ActorInput { Held = HeldLook.Item });
                minScaleY = Math.Min(minScaleY, p.ScaleY);
                minReachY = Math.Min(minReachY, p.HandReachY);
            }

            Assert.True(minScaleY < 0.92f);
            Assert.True(minReachY < -0.2f);
            Assert.False(a.Gesturing);
            Assert.InRange(p.ScaleY, 0.97f, 1.03f);
        }

        [Fact]
        public void E27_SwordInHand_FollowsSwing_BodySwingUnchanged()
        {
            Assert.True(ActionAnim.SwordDeg(SwingPhase.Windup, 1f) > 90f);
            Assert.True(ActionAnim.SwordDeg(SwingPhase.Strike, 1f) < -80f);
            Assert.Equal(ActionAnim.CarryToolDeg, ActionAnim.SwordDeg(SwingPhase.Recover, 1f), 3);

            // Тело при ударе — как в K-001, с предметом в руке и без.
            var bare = new ProceduralAnimator();
            var armed = new ProceduralAnimator();
            for (int i = 0; i < 20; i++)
            {
                var s = new ActorInput { Swing = SwingPhase.Windup, SwingProgress = i / 20f, SwingDirX = 1f };
                Pose pb = bare.Step(1f / 60f, s);
                s.Held = HeldLook.Sword;
                Pose pa = armed.Step(1f / 60f, s);
                Assert.Equal(pb.OffsetX, pa.OffsetX, 5);
                Assert.Equal(pb.RotationDeg, pa.RotationDeg, 5);
            }
        }

        [Fact]
        public void E27_Walk_ItemSwingsWithStep_HandReturnsSmoothlyAfterAction()
        {
            var a = new ProceduralAnimator();
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int i = 0; i < 120; i++)
            {
                Pose p = a.Step(1f / 60f, new ActorInput { VelX = 6f, Held = HeldLook.Pick });
                if (i > 30)
                {
                    minH = Math.Min(minH, p.HandDeg);
                    maxH = Math.Max(maxH, p.HandDeg);
                }
            }

            Assert.InRange(maxH - minH, ActionAnim.WalkSwingDeg, ActionAnim.WalkSwingDeg * 2.2f);

            // После удара рука идёт к «несёт» плавно, без скачка за кадр.
            var b = new ProceduralAnimator();
            Pose last = b.Step(1f / 60f, Dig(ActionKind.Chop, 54 / 60f, 54));
            Pose next = b.Step(1f / 60f, new ActorInput { Held = HeldLook.Axe });
            Assert.True(Math.Abs(next.HandDeg - last.HandDeg) < 25f);
        }

        [Fact]
        public void E27_Poke_WithoutTool_KeepsOldBehavior()
        {
            var a = new ProceduralAnimator();
            int pokes = 0;
            for (int i = 0; i < 90; i++)
            {
                Pose p = a.Step(1f / 60f, new ActorInput { Digging = true, DigDirX = 1f, DigPeriod = 0.3f, Action = ActionKind.Poke });
                pokes += p.DigPokeNow ? 1 : 0;
            }

            Assert.Equal(5, pokes); // 1,5 с по 0,3 с — как в K-001
        }
            /// <summary>
        /// Связка «счёт кадров копания → поза» как в <c>NetAnimation.Frame</c>: кадры рисования шагом <paramref name="dt"/>, счёт
        /// ядра растёт раз в 1/60 с; при счёте = <paramref name="frames"/> предсказание снимает объект (в мире — пусто).
        /// Возвращает удары: (счёт в прошлом кадре, счёт в кадре удара, вид отклика, снят ли объект в этот кадр).
        /// </summary>
        /// <summary>
        /// Счёт кадров ввода к мигу <paramref name=time/>: кадр k отправляется в k/60 с, нечётные — с опозданием 0,8 кадра
        /// (ввод шлётся по своему такту, кадры рисования его не ждут) — между счётами бывает больше кадра.
        /// </summary>
        static int CountAt(double time, int frames)
        {
            int n = 0;
            for (int k = 1; k <= frames; k++)
            {
                if (k / 60.0 + (k % 2 == 1 ? 0.8 / 60 : 0) <= time + 1e-9)
                {
                    n = k;
                }
            }

            return n;
        }

        static System.Collections.Generic.List<(int Prev, int Progress, ImpactLook Look, bool Removed)> RunTracked(float dt, int frames, ushort obj)
        {
            var track = new DigTrack();
            var anim = new ProceduralAnimator();
            var hits = new System.Collections.Generic.List<(int, int, ImpactLook, bool)>();
            int prev = 0;
            double time = 0;
            int n = (int)Math.Ceiling((frames / 60.0 + 0.3) / dt);
            for (int i = 0; i < n; i++)
            {
                time += dt;
                int progress = CountAt(time, frames);
                bool removed = progress >= frames;
                ushort now = removed ? Orpheus.Core.World.ObjectId.Empty : obj;
                float sec = track.Update(5, 7, now, progress, frames, removed, (float)dt, out _);
                Pose p = anim.Step((float)dt, new ActorInput
                {
                    Digging = true, DigDirX = 1f, Action = ActionKind.Chop, ActionSec = sec, ActionFrames = frames, Held = HeldLook.Axe,
                });
                if (p.DigPokeNow)
                {
                    hits.Add((prev, progress, track.Look, removed));
                }

                prev = progress;
            }

            return hits;
        }

        [Theory]
        [InlineData(1f / 30f)]
        [InlineData(1f / 60f)]
        [InlineData(1f / 144f)]
        [InlineData(1f / 240f)]
        public void E27_OneSwingDig_OnTree_GivesTreeImpact_InRemovalFrame(float dt)
        {
            // Копание в один замах (0,3 с): единственный удар — в кадр снятия объекта предсказанием, отклик — дерева, не пыли.
            var hits = RunTracked(dt, 18, Orpheus.Core.World.ObjectId.Tree);
            Assert.Single(hits);
            Assert.Equal(18, hits[0].Progress);
            Assert.True(hits[0].Prev < 18);
            Assert.True(hits[0].Removed);
            Assert.Equal(ImpactLook.Wood, hits[0].Look);
        }

        [Theory]
        [InlineData(1f / 60f)]
        [InlineData(1f / 144f)]
        [InlineData(1f / 240f)]
        public void E27_EveryImpact_NotBeforeCoreCount_LastInRemovalFrame(float dt)
        {
            const int frames = 75; // три замаха по 25 кадров
            var hits = RunTracked(dt, frames, Orpheus.Core.World.ObjectId.Rock);
            Assert.Equal(3, hits.Count);
            // Удар — в первый кадр рисования, где счёт ядра дошёл до конца замаха (25, 50, 75), не раньше.
            for (int i = 0; i < 3; i++)
            {
                int boundary = 25 * (i + 1);
                Assert.True(hits[i].Prev < boundary && hits[i].Progress >= boundary, $"удар {i}: счёт {hits[i].Prev}→{hits[i].Progress}, граница {boundary}");
            }

            Assert.Equal(75, hits[2].Progress);
            Assert.True(hits[2].Removed);
            Assert.All(hits, h => Assert.Equal(ImpactLook.Stone, h.Look));
        }

        [Fact]
        public void E27_ClockSec_SubFrameNeverCrossesBeatBeforeCount()
        {
            // Счёт 29 из 30, доля кадра — целый кадр и больше: удара ещё нет; счёт 30 — удар.
            float before = DigTrack.ClockSec(29, 1f / 60f, 30);
            Assert.Equal(0, ActionAnim.ImpactsBetween(0f, before, 30));
            Assert.True(before > 28f / 60f); // поза всё равно идёт вперёд
            Assert.Equal(1, ActionAnim.ImpactsBetween(before, DigTrack.ClockSec(30, 0f, 30), 30));
            Assert.Equal(ImpactLook.Wood, DigTrack.LookOf(Orpheus.Core.World.ObjectId.Tree));
            Assert.Equal(ImpactLook.Stone, DigTrack.LookOf(Orpheus.Core.World.ObjectId.Ore));
            Assert.Equal(ImpactLook.Dust, DigTrack.LookOf(Orpheus.Core.World.ObjectId.Empty));
        }

        [Fact]
        public void E27_DigTrack_RemembersObject_ResetsOnNewTile()
        {
            var t = new DigTrack();
            t.Update(1, 1, Orpheus.Core.World.ObjectId.Tree, 0, 30, false, 0.01f, out bool started);
            Assert.True(started);
            t.Update(1, 1, Orpheus.Core.World.ObjectId.Empty, 30, 30, true, 0.01f, out started);
            Assert.False(started);
            Assert.Equal(ImpactLook.Wood, t.Look); // объект снят предсказанием — помним дерево
            t.Update(2, 1, Orpheus.Core.World.ObjectId.Rock, 0, 30, false, 0.01f, out started);
            Assert.True(started);
            Assert.Equal(ImpactLook.Stone, t.Look);
        }
    }
}
