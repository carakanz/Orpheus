#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using Orpheus.Client.Anim.Dragon;
using UnityEngine;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// D-004 шаг 1а: показ дракона «вырезкой» (<c>--dragon-shots &lt;папка&gt;</c>, окно 1920×1080 на скрытом столе).
    /// Каждый дракон делает своё действие по сценарию (время — от начала своей ленты), ленты по 6 кадров:
    /// <c>D-004_idle_side/_front/_back</c>, <c>_walk_side/_front/_back/_left</c>, <c>_run_side/_front</c>, <c>_fly_side/_front</c>,
    /// <c>_bite_side/_front</c>, <c>_fire_side/_front</c>, <c>_hit_side/_front</c>, <c>_death_side/_front</c>, <c>_views</c> (4 вида разом).
    /// </summary>
    public sealed partial class AnimShowcase
    {
        sealed class DragonShow
        {
            public DragonActor Actor = null!;
            public Vector3 Home;
            public float Start;            // время сценария, с которого идёт своё действие
            public Action<DragonShow, float, float> Script = null!; // (сам, своё время, dt)
            public Vector3 Last;
        }

        sealed class DragonStrip
        {
            public string Name = "";
            public float[] Rel = Array.Empty<float>();
            public Func<int, Vector3> Focus = null!;
            public int W = 900, H = 640;
            public float Ortho = 2.4f;
            public float Start;
        }

        readonly bool _dragonMode;
        List<DragonShow>? _dragons;
        readonly List<(DragonShow show, FireFx fx)> _fires = new List<(DragonShow, FireFx)>();

        const float DragonWalk = 1.5f, DragonRun = 4.0f;

        void DragonFrame(float dt)
        {
            foreach (DragonShow d in _dragons!)
            {
                float own = _t - d.Start;
                d.Script(d, own, dt);
                Vector3 pos = d.Actor.Root.position;
                float moved = (pos - d.Last).magnitude;
                d.Last = pos;
                d.Actor.Step(dt, moved);
            }

            foreach ((DragonShow show, FireFx fx) in _fires)
            {
                DragonActor a = show.Actor;
                fx.Step(dt, a.Breathing, a.MouthWorld, a.MouthDir, a.Order);
            }
        }

        static Action<DragonShow, float, float> Walker(DragonAct act, Vector2 dir, float speed, float lane)
        {
            DragonAct cur = DragonAct.Idle;
            float prevX = -1f;
            return (d, own, dt) =>
            {
                float s = own + 0.8f; // разгон до кадров ленты
                if (s < 0f)
                {
                    d.Actor.Root.position = d.Home;
                    d.Last = d.Home;
                    return;
                }

                if (cur != act)
                {
                    cur = act;
                    d.Actor.Play(act);
                }

                float x = Mathf.Repeat(s * speed, lane);
                Vector3 pos = d.Home + (Vector3)(dir * x);
                if (x < prevX)
                {
                    d.Last = pos; // перенос в начало дорожки — не шаг
                }

                prevX = x;
                d.Actor.Root.position = pos;
            };
        }

        static Action<DragonShow, float, float> Once(DragonAct act, Vector2 hitFrom = default)
        {
            bool done = false;
            return (d, own, dt) =>
            {
                if (!done && own >= 0f)
                {
                    done = true;
                    d.Actor.Play(act, hitFrom);
                }
            };
        }

        IEnumerator DragonRun()
        {
            DragonParts? parts = DragonParts.Load();
            if (parts == null)
            {
                yield break;
            }

            Time.captureDeltaTime = 1f / 60f;
            Material mat = _g.Character.GetChild(0).GetComponent<SpriteRenderer>().sharedMaterial;
            _dragons = new List<DragonShow>();
            var strips = new List<DragonStrip>();
            float clock = 1.2f;
            int slot = 0;

            DragonShow Add(string name, DragonView view, Func<DragonShow, Action<DragonShow, float, float>> script, bool left = false)
            {
                int col = slot % 6, row = slot / 6;
                slot++;
                var home = _c + new Vector3(-17.5f + col * 7f, 9f - row * 7f, 0f);
                var d = new DragonShow { Home = home, Last = home };
                d.Actor = new DragonActor(_stage, "Dragon " + name, parts, view, mat);
                d.Actor.Root.position = home;
                d.Actor.Face(left);
                d.Script = script(d);
                _dragons.Add(d);
                return d;
            }

            Vector3 Body(DragonShow d, DragonView v) => d.Actor.Root.position + new Vector3(v == DragonView.Side ? -0.15f : 0f, v == DragonView.Side ? 0.75f : 0.65f, 0f);

            void Strip(string name, DragonShow d, DragonView v, float[] rel, int w = 0, int h = 0, float ortho = 2.4f)
            {
                d.Start = clock;
                var s = new DragonStrip
                {
                    Name = name, Rel = rel, Start = clock, Ortho = ortho,
                    W = w > 0 ? w : v == DragonView.Side ? 940 : 820, H = h > 0 ? h : 700,
                    Focus = _ => Body(d, v),
                };
                strips.Add(s);
                clock += rel[rel.Length - 1] + 0.35f;
            }

            float[] Steps(float a, float step) => new[] { a, a + step, a + 2 * step, a + 3 * step, a + 4 * step, a + 5 * step };

            // стоит
            foreach (DragonView v in new[] { DragonView.Side, DragonView.Front, DragonView.Back })
            {
                DragonShow d = Add("idle " + v, v, _ => (s, own, dt) => { });
                Strip("D-004_idle_" + v.ToString().ToLowerInvariant(), d, v, Steps(0.2f, 0.45f));
            }

            // идёт: цикл шага 0,95 т пути = 0,63 с — 6 кадров через 0,105 с покрывают цикл
            DragonShow walkSide = Add("walk side", DragonView.Side, _ => Walker(DragonAct.Walk, Vector2.right, DragonWalk, 6f));
            Strip("D-004_walk_side", walkSide, DragonView.Side, Steps(0.2f, 0.105f));
            DragonShow walkLeft = Add("walk left", DragonView.Side, _ => Walker(DragonAct.Walk, Vector2.left, DragonWalk, 6f), left: true);
            Strip("D-004_walk_left", walkLeft, DragonView.Side, Steps(0.2f, 0.105f));
            DragonShow walkFront = Add("walk front", DragonView.Front, _ => Walker(DragonAct.Walk, Vector2.down, DragonWalk, 5f));
            Strip("D-004_walk_front", walkFront, DragonView.Front, Steps(0.2f, 0.105f));
            DragonShow walkBack = Add("walk back", DragonView.Back, _ => Walker(DragonAct.Walk, Vector2.up, DragonWalk, 5f));
            Strip("D-004_walk_back", walkBack, DragonView.Back, Steps(0.2f, 0.105f));

            // бежит (галоп): цикл 1,7 т = 0,425 с
            DragonShow runSide = Add("run side", DragonView.Side, _ => Walker(DragonAct.Run, Vector2.right, DragonRun, 12f));
            Strip("D-004_run_side", runSide, DragonView.Side, Steps(0.3f, 0.071f));
            DragonShow runFront = Add("run front", DragonView.Front, _ => Walker(DragonAct.Run, Vector2.down, DragonRun, 10f));
            Strip("D-004_run_front", runFront, DragonView.Front, Steps(0.3f, 0.071f));

            // взлёт и зависание
            DragonShow flySide = Add("fly side", DragonView.Side, _ => Once(DragonAct.Fly));
            Strip("D-004_fly_side", flySide, DragonView.Side, new[] { 0.15f, 0.45f, 0.9f, 1.5f, 1.62f, 1.73f }, 960, 820, 2.6f);
            DragonShow flyFront = Add("fly front", DragonView.Front, _ => Once(DragonAct.Fly));
            Strip("D-004_fly_front", flyFront, DragonView.Front, new[] { 0.15f, 0.45f, 0.9f, 1.5f, 1.62f, 1.73f }, 1000, 820, 2.6f);

            // укус, огонь
            DragonShow biteSide = Add("bite side", DragonView.Side, _ => Once(DragonAct.Bite));
            Strip("D-004_bite_side", biteSide, DragonView.Side, new[] { 0.1f, 0.35f, 0.47f, 0.52f, 0.62f, 1.0f });
            DragonShow biteFront = Add("bite front", DragonView.Front, _ => Once(DragonAct.Bite));
            Strip("D-004_bite_front", biteFront, DragonView.Front, new[] { 0.1f, 0.35f, 0.47f, 0.52f, 0.62f, 1.0f });
            DragonShow fireSide = Add("fire side", DragonView.Side, _ => Once(DragonAct.Fire));
            Strip("D-004_fire_side", fireSide, DragonView.Side, new[] { 0.3f, 0.6f, 0.85f, 1.25f, 1.75f, 2.4f }, 1300, 760, 2.8f);
            DragonShow fireFront = Add("fire front", DragonView.Front, _ => Once(DragonAct.Fire));
            Strip("D-004_fire_front", fireFront, DragonView.Front, new[] { 0.3f, 0.6f, 0.85f, 1.25f, 1.75f, 2.4f }, 960, 1000, 2.8f);

            // удар, смерть (удар справа)
            DragonShow hitSide = Add("hit side", DragonView.Side, _ => Once(DragonAct.Hit, new Vector2(1f, 0.2f)));
            Strip("D-004_hit_side", hitSide, DragonView.Side, new[] { -0.05f, 0.04f, 0.1f, 0.2f, 0.4f, 0.9f });
            DragonShow hitFront = Add("hit front", DragonView.Front, _ => Once(DragonAct.Hit, new Vector2(0f, -1f)));
            Strip("D-004_hit_front", hitFront, DragonView.Front, new[] { -0.05f, 0.04f, 0.1f, 0.2f, 0.4f, 0.9f });
            DragonShow deathSide = Add("death side", DragonView.Side, _ => Once(DragonAct.Death, new Vector2(1f, 0.2f)));
            Strip("D-004_death_side", deathSide, DragonView.Side, new[] { 0.05f, 0.25f, 0.45f, 0.7f, 1.0f, 2.2f });
            DragonShow deathFront = Add("death front", DragonView.Front, _ => Once(DragonAct.Death, new Vector2(0f, -1f)));
            Strip("D-004_death_front", deathFront, DragonView.Front, new[] { 0.05f, 0.25f, 0.45f, 0.7f, 1.0f, 2.2f });

            foreach (DragonShow d in new[] { fireSide, fireFront })
            {
                _fires.Add((d, new FireFx(_stage, mat)));
            }

            // 4 вида разом: идущие вправо, влево, к зрителю, от зрителя — в один миг (свои ленты уже сняты, дорожки идут дальше)
            var views = new[] { walkSide, walkLeft, walkFront, walkBack };
            var viewKinds = new[] { DragonView.Side, DragonView.Side, DragonView.Front, DragonView.Back };
            float vt = clock + 0.2f;
            strips.Add(new DragonStrip
            {
                Name = "D-004_views", Rel = new[] { 0f, 0.001f, 0.002f, 0.003f }, Start = vt, W = 900, H = 700,
                Focus = i => Body(views[i], viewKinds[i]),
            });

            Debug.Log($"[dragon] драконов {_dragons.Count}, лент {strips.Count}, сценарий до {vt + 1f:0.0} с");
            foreach (DragonStrip s in strips)
            {
                yield return DragonStripRun(s);
            }

            // общий вид
            _ortho = 12f;
            _focus = _c + new Vector3(0f, 2f, 0f);
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D full = CaptureFrame();
            Save("D-004_overview.png", full);
            UnityEngine.Object.Destroy(full);
            Time.captureDeltaTime = 0f;
        }

        IEnumerator DragonStripRun(DragonStrip s)
        {
            _ortho = s.Ortho;
            const int gap = 4;
            int w = s.W, h = s.H;
            var strip = new Texture2D(s.Rel.Length * (w + gap) - gap, h, TextureFormat.RGB24, false);
            var fill = new Color[strip.width * strip.height];
            Color outline = AnimAssets.ToColor(AnimParams.Outline, 1f);
            for (int i = 0; i < fill.Length; i++)
            {
                fill[i] = outline;
            }

            strip.SetPixels(fill);
            for (int i = 0; i < s.Rel.Length; i++)
            {
                float at = s.Start + s.Rel[i];
                _focus = s.Focus(i);
                while (_t < at)
                {
                    _focus = s.Focus(i);
                    yield return null;
                }

                _focus = s.Focus(i);
                _g.Camera.orthographicSize = _ortho;
                _g.Camera.transform.position = _focus + GameBoot.CameraOffset;
                yield return new WaitForEndOfFrame();
                Texture2D frame = CaptureFrame();
                int cw = Mathf.Min(w, frame.width), ch = Mathf.Min(h, frame.height);
                int x0 = (frame.width - cw) / 2, y0 = (frame.height - ch) / 2;
                strip.SetPixels(i * (w + gap) + (w - cw) / 2, (h - ch) / 2, cw, ch, frame.GetPixels(x0, y0, cw, ch));
                UnityEngine.Object.Destroy(frame);
            }

            strip.Apply();
            Save(s.Name + ".png", strip);
            UnityEngine.Object.Destroy(strip);
        }
    }
}
