#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// Показ и замер процедурной анимации офлайн (K-001, шаг 8). Сцена — у дерева в мире по зерну; существа — копии спрайта
    /// героя, ими движет сценарий (позиция, уворот, фаза замаха), поза — тот же <see cref="ProceduralAnimator"/>, что в сети.
    /// <list type="bullet">
    /// <item><c>--anim-shots &lt;папка&gt;</c>: время игры идёт ровно 1/60 с на кадр (<see cref="Time.captureDeltaTime"/>), в
    /// заданные миги кадр снимается, вырезается кусок вокруг действия и кладётся в ленту из 6 кадров:
    /// <c>K-001_walk.png</c>, <c>_dodge</c>, <c>_swing_hit</c>, <c>_swing_dodge</c>, <c>_dig</c>, плюс общий <c>K-001_overview.png</c>.</item>
    /// <item><c>--anim-bench &lt;сек&gt;</c>: 8 персонажей (ходьба, увороты) и 40 врагов-болванок (замах с сектором по кругу) на
    /// экране при зуме ×1,0, без вертикальной синхронизации; в лог — средний fps и 1 % худших кадров, выход 0.</item>
    /// </list>
    /// Нужен вывод на экран — запускать без <c>-batchmode -nographics</c>.
    /// </summary>
    public sealed class AnimShowcase
    {
        const float WalkSpeed = 6f;     // т/с, базовая скорость (`02` 2.1)
        const float DashSpeed = 12f;    // 3 т за 0,25 с (`02` 2.2)
        const float DashSec = 0.25f;
        const float InvulnShare = 0.8f; // неуязвимость — первые 0,20 с рывка
        const float SwingCycle = 1.5f;  // манекен 0-029 — раз в 1,5 с
        const float ShotsOrtho = 2.2f;

        readonly GameBoot _g;
        readonly string? _dir;
        readonly int _benchSec;
        readonly Transform _stage;
        readonly Sparks _sparks;
        readonly Material _sectorMat;
        readonly Sprite _sprite;
        Vector3 _c; // центр сцены (Unity)
        float _t;

        // показ
        ActorRig _walker = null!;
        ActorRig _dodger = null!;
        SwingActor _dummy = null!;
        ActorRig _victim = null!;
        ActorRig _digger = null!;
        Vector3 _tree;
        Vector3 _focus;
        float _ortho = ShotsOrtho;

        // замер
        readonly List<ActorRig> _benchHeroes = new List<ActorRig>();
        readonly List<SwingActor> _benchEnemies = new List<SwingActor>();
        readonly List<Vector3> _benchHomes = new List<Vector3>();
        bool _bench;

        public AnimShowcase(GameBoot g, string? dir, int benchSec)
        {
            _g = g;
            _dir = dir == null ? null : Path.GetFullPath(dir);
            _benchSec = benchSec;
            if (_dir != null)
            {
                Directory.CreateDirectory(_dir);
            }

            TileCoord tree = FindStage(out int clutter);
            _g.Teleport(_g.TileCenter(new TileCoord(tree.X - 6, tree.Y)));
            _g.Character.gameObject.SetActive(false); // в показе свой персонаж не нужен — сцена из своих существ
            _tree = _g.Origin.ToUnity(_g.TileCenter(tree));
            _c = _tree + new Vector3(-6f, 0f, 0f);
            Debug.Log($"[anim] сцена у дерева {tree.X},{tree.Y} (лишних объектов рядом {clutter}), снимки → {_dir ?? "нет"}, замер {benchSec} с");

            _stage = new GameObject("K001 Stage").transform;
            _sprite = _g.Character.GetChild(0).GetComponent<SpriteRenderer>().sprite;
            _sectorMat = AnimAssets.SpriteMaterial(_g.Character.GetChild(0).GetComponent<SpriteRenderer>());
            _sparks = new Sparks(_stage);
            _g.StartCoroutine(Run());
        }

        // Дерево, вокруг которого (сцена 15 × 13 т западнее) нет других объектов; нет такого — где их меньше всего.
        TileCoord FindStage(out int clutter)
        {
            WorldModel w = _g.World;
            int c = _g.Map.Width / 2;
            TileCoord best = new TileCoord(c, c);
            clutter = int.MaxValue;
            for (int r = 0; r < 160 && clutter > 0; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }

                        TileCoord t = Torus.Wrap(_g.Map, new TileCoord(c + 40 + dx, c + dy));
                        if (ObjectId.IdOf(w.GetObject(t)) != ObjectId.Tree)
                        {
                            continue;
                        }

                        int n = 0;
                        for (int y = -5; y <= 8 && n < clutter; y++)
                        {
                            for (int x = -13; x <= 2; x++)
                            {
                                if (x == 0 && y == 0)
                                {
                                    continue;
                                }

                                // Unity y вверх = ядро y вниз.
                                TileCoord q = Torus.Wrap(_g.Map, new TileCoord(t.X + x, t.Y - y));
                                if (w.GetObject(q) != ObjectId.Empty || _g.Rules.IsBlocked(_g.Map, q))
                                {
                                    n++;
                                }
                            }
                        }

                        if (n < clutter)
                        {
                            clutter = n;
                            best = t;
                            if (n == 0)
                            {
                                return best;
                            }
                        }
                    }
                }
            }

            return best;
        }

        Transform Make(string name, Color color) => ActorFactory.Create(_stage, name, _sprite, color);

        void BuildShowcase()
        {
            _walker = new ActorRig(Make("Walker", Color.white));
            _dodger = new ActorRig(Make("Dodger", Color.white));
            _dummy = new SwingActor(Make("Dummy", ActorFactory.EnemyTint), _sectorMat) { Axis = Vector2.left };
            _victim = new ActorRig(Make("Victim", Color.white));
            _digger = new ActorRig(Make("Digger", Color.white));
        }

        Vector3 WalkerHome => _c + new Vector3(-5f, 3f, 0f);
        Vector3 DodgerHome => _c + new Vector3(-6f, -3f, 0f);
        Vector3 DummyPos => _c + new Vector3(2.5f, 3f, 0f);
        Vector3 VictimHome => DummyPos + new Vector3(-1.1f, 0f, 0f);
        Vector3 DiggerPos => _tree + new Vector3(-1f, 0f, 0f);

        /// <summary>Каждый кадр после расстановки GameBoot: время сценария, позы, камера.</summary>
        public void AfterFrame()
        {
            float dt = Time.deltaTime;
            _t += dt;
            if (_bench)
            {
                BenchFrame(dt);
            }
            else if (_walker != null)
            {
                ShowFrame(dt);
            }

            _sparks.Update(dt);
            _g.Camera.orthographicSize = _ortho;
            _g.Camera.transform.position = _focus + GameBoot.CameraOffset;
        }

        void ShowFrame(float dt)
        {
            // Ходьба: туда-обратно 4 т.
            float leg = 4f / WalkSpeed;
            float u = _t % (2f * leg);
            float x = u < leg ? u * WalkSpeed : (2f * leg - u) * WalkSpeed;
            _walker.Root.position = WalkerHome + new Vector3(x, 0f, 0f);
            _walker.AnimateWalk(dt);

            // Уворот: шаг вправо, рывок вправо, пауза, шаг влево, рывок влево, пауза — цикл 2,2 с.
            DodgerScript(_dodger, DodgerHome, _t, dt, 1f);

            // Манекен бьёт на запад раз в 1,5 с; жертва через раз уходит рывком на север.
            int k = (int)(_t / SwingCycle);
            float su = _t - k * SwingCycle;
            SwingPhase phase = PhaseAt(su, out float q);
            _dummy.Rig.Root.position = DummyPos;
            bool struck = _dummy.Animate(dt, phase, q);

            Vector3 vp = VictimHome;
            var vs = new ActorInput();
            if (k % 2 == 0)
            {
                if (su >= 0.22f && su < 0.22f + DashSec)
                {
                    float p = (su - 0.22f) / DashSec;
                    vp += new Vector3(0f, DashSpeed * DashSec * p, 0f);
                    vs.Dashing = true;
                    vs.DashProgress = p;
                    vs.DashDirY = 1f;
                    vs.Invulnerable = p < InvulnShare;
                }
                else if (su >= 0.22f + DashSec)
                {
                    float back = Mathf.Clamp01((su - 0.6f) / 0.9f);
                    vp += new Vector3(0f, 3f * (1f - back), 0f);
                }
            }

            _victim.Root.position = vp;
            Vector2 vv = _victim.MeasureVelocity(dt);
            vs.VelX = vv.x;
            vs.VelY = vv.y;
            if (struck && _dummy.InSector(vp))
            {
                Vector2 dir = ((Vector2)(vp - DummyPos)).normalized;
                _victim.Animator.OnHit(dir.x, dir.y);
                _sparks.Emit((Vector2)vp + new Vector2(0f, 0.35f), dir, AnimParams.HitSparks, AnimParams.HitSparkSpeed, AnimParams.HitSparkLife, 80f, false,
                    AnimParams.StrikeFlash, AnimParams.SparkLight);
            }

            _victim.Apply(_victim.Animator.Step(dt, vs));

            // Копание дерева к востоку.
            _digger.Root.position = DiggerPos;
            var ds = new ActorInput { Digging = true, DigDirX = 1f, DigPeriod = 0.3f };
            Pose dp = _digger.Animator.Step(dt, ds);
            _digger.Apply(dp);
            if (dp.DigPokeNow)
            {
                Vector2 at = (Vector2)_tree + new Vector2(-0.45f, 0f);
                _sparks.Emit(at, new Vector2(-1f, 0.8f), AnimParams.DigDust, AnimParams.DigDustSpeed, AnimParams.DigDustLife, 55f, true,
                    AnimParams.DustWood, AnimParams.DustWood, AnimParams.DustLight);
            }
        }

        static SwingPhase PhaseAt(float su, out float q)
        {
            const float windup = 0.4f;
            if (su < windup)
            {
                q = su / windup;
                return SwingPhase.Windup;
            }

            if (su < windup + AnimParams.StrikeSec)
            {
                q = (su - windup) / AnimParams.StrikeSec;
                return SwingPhase.Strike;
            }

            if (su < windup + AnimParams.StrikeSec + AnimParams.RecoverSec)
            {
                q = (su - windup - AnimParams.StrikeSec) / AnimParams.RecoverSec;
                return SwingPhase.Recover;
            }

            q = 0f;
            return SwingPhase.None;
        }

        /// <summary>Сценарий «шаг — рывок — пауза» туда и обратно, цикл 2,2 с; <paramref name="sign"/> — направление первого шага.</summary>
        static void DodgerScript(ActorRig rig, Vector3 home, float t, float dt, float sign)
        {
            const float cycle = 2.2f;
            float u = t % cycle;
            float x;
            var s = new ActorInput();
            if (u < 0.5f)
            {
                x = WalkSpeed * u;
            }
            else if (u < 0.5f + DashSec)
            {
                float p = (u - 0.5f) / DashSec;
                x = 3f + DashSpeed * DashSec * p;
                s.Dashing = true;
                s.DashProgress = p;
                s.DashDirX = sign;
                s.Invulnerable = p < InvulnShare;
            }
            else if (u < 1.1f)
            {
                x = 6f;
            }
            else if (u < 1.6f)
            {
                x = 6f - WalkSpeed * (u - 1.1f);
            }
            else if (u < 1.6f + DashSec)
            {
                float p = (u - 1.6f) / DashSec;
                x = 3f - DashSpeed * DashSec * p;
                s.Dashing = true;
                s.DashProgress = p;
                s.DashDirX = -sign;
                s.Invulnerable = p < InvulnShare;
            }
            else
            {
                x = 0f;
            }

            rig.Root.position = home + new Vector3(x * sign, 0f, 0f);
            Vector2 v = rig.MeasureVelocity(dt);
            s.VelX = v.x;
            s.VelY = v.y;
            rig.Apply(rig.Animator.Step(dt, s));
        }

        // ---------- снимки ----------

        struct Shot
        {
            public string Name;
            public Func<Vector3> Focus;
            public float Ortho;
            public float[] Times;
            public int CropW, CropH;
        }

        IEnumerator Run()
        {
            if (_dir != null)
            {
                Time.captureDeltaTime = 1f / 60f; // время игры — ровно кадр за кадром, снимки не сбивают сценарий
                BuildShowcase();
                _focus = _c;
                var shots = new[]
                {
                    new Shot { Name = "walk", Focus = () => _walker.Root.position, Ortho = ShotsOrtho, Times = Series(1.00f, 0.05f), CropW = 360, CropH = 520 },
                    new Shot { Name = "dodge", Focus = () => DodgerHome + new Vector3(4.2f, 0f, 0f), Ortho = ShotsOrtho,
                        Times = new[] { 2.62f, 2.70f, 2.76f, 2.82f, 2.90f, 3.02f }, CropW = 900, CropH = 520 },
                    new Shot { Name = "swing_hit", Focus = () => (DummyPos + VictimHome) * 0.5f, Ortho = ShotsOrtho,
                        Times = new[] { 4.45f, 4.62f, 4.84f, 4.91f, 4.96f, 5.25f }, CropW = 640, CropH = 560 },
                    new Shot { Name = "swing_dodge", Focus = () => DummyPos + new Vector3(-0.6f, 1.2f, 0f), Ortho = 2.6f,
                        Times = new[] { 6.10f, 6.24f, 6.30f, 6.40f, 6.48f, 6.64f }, CropW = 640, CropH = 680 },
                    new Shot { Name = "dig", Focus = () => (DiggerPos + _tree) * 0.5f, Ortho = ShotsOrtho, Times = Series(7.00f, 0.05f), CropW = 560, CropH = 560 },
                };

                foreach (Shot s in shots)
                {
                    yield return Strip(s);
                }

                _ortho = 5f;
                _focus = _c + new Vector3(0f, 1.2f, 0f);
                while (_t < 8.0f)
                {
                    yield return null;
                }

                yield return new WaitForEndOfFrame();
                Texture2D full = ScreenCapture.CaptureScreenshotAsTexture();
                Save("K-001_overview.png", full);
                UnityEngine.Object.Destroy(full);
                Time.captureDeltaTime = 0f;
            }

            if (_benchSec > 0)
            {
                yield return Bench();
            }

            Application.Quit(0);
        }

        static float[] Series(float start, float step)
        {
            var r = new float[6];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = start + i * step;
            }

            return r;
        }

        IEnumerator Strip(Shot s)
        {
            _ortho = s.Ortho;
            int w = Mathf.Min(s.CropW, Screen.width), h = Mathf.Min(s.CropH, Screen.height);
            const int gap = 4;
            var strip = new Texture2D(s.Times.Length * (w + gap) - gap, h, TextureFormat.RGB24, false);
            var fill = new Color[strip.width * strip.height];
            Color outline = AnimAssets.ToColor(AnimParams.SectorRim, 1f);
            for (int i = 0; i < fill.Length; i++)
            {
                fill[i] = outline;
            }

            strip.SetPixels(fill);
            for (int i = 0; i < s.Times.Length; i++)
            {
                // Камера — на действие уже в кадре до снимка.
                while (_t < s.Times[i])
                {
                    _focus = s.Focus();
                    yield return null;
                }

                yield return new WaitForEndOfFrame();
                Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
                int x0 = (frame.width - w) / 2, y0 = (frame.height - h) / 2;
                strip.SetPixels(i * (w + gap), 0, w, h, frame.GetPixels(x0, y0, w, h));
                UnityEngine.Object.Destroy(frame);
            }

            strip.Apply();
            Save($"K-001_{s.Name}.png", strip);
            UnityEngine.Object.Destroy(strip);
        }

        void Save(string file, Texture2D tex)
        {
            string path = Path.Combine(_dir!, file);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"[anim] снимок {path} ({tex.width}x{tex.height}), время сценария {_t:0.00} с");
        }

        // ---------- замер ----------

        IEnumerator Bench()
        {
            const int heroes = 8, enemies = 40;
            foreach (Transform child in _stage)
            {
                if (child.name != "Sparks")
                {
                    child.gameObject.SetActive(false);
                }
            }

            _walker = null!;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            _ortho = GameBoot.ReferenceHalfHeight; // зум ×1,0 — 40 × 22,5 т (`00` 5.1)
            _focus = _c;
            var rnd = new System.Random(1);
            for (int i = 0; i < heroes; i++)
            {
                _benchHomes.Add(_c + new Vector3(-14f + (i % 4) * 7f, i < 4 ? 6f : -7f, 0f));
                _benchHeroes.Add(new ActorRig(Make($"Hero {i}", i == 0 ? Color.white : new Color(0.55f, 0.8f, 1f))));
            }

            for (int i = 0; i < enemies; i++)
            {
                int col = i % 10, row = i / 10;
                Vector3 at = _c + new Vector3(-16f + col * 3.5f + (row % 2) * 1.2f, 4f - row * 3f, 0f);
                float a = rnd.Next(8) * Mathf.PI / 4f;
                var e = new SwingActor(Make($"Enemy {i}", ActorFactory.EnemyTint), _sectorMat) { Axis = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) };
                e.Rig.Root.position = at;
                e.Rig.Animator.PhaseOffset = (float)rnd.NextDouble();
                _benchEnemies.Add(e);
            }

            _bench = true;
            _t = 0f;
            while (_t < 1f)
            {
                yield return null; // прогрев
            }

            var frames = new List<float>(_benchSec * 600);
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < _benchSec)
            {
                yield return null;
                frames.Add(Time.unscaledDeltaTime);
            }

            frames.Sort();
            double sum = 0;
            foreach (float f in frames)
            {
                sum += f;
            }

            double avgMs = sum / frames.Count * 1000.0;
            int worstN = Math.Max(1, frames.Count / 100);
            double worst = 0;
            for (int i = frames.Count - worstN; i < frames.Count; i++)
            {
                worst += frames[i];
            }

            double worstMs = worst / worstN * 1000.0;
            Debug.Log($"[anim-bench] {heroes} персонажей + {enemies} врагов, {Screen.width}x{Screen.height}, {_benchSec} с: кадров {frames.Count}, " +
                      $"среднее {avgMs:0.00} мс ({1000.0 / avgMs:0} fps), 1 % худших {worstMs:0.00} мс ({1000.0 / worstMs:0} fps), " +
                      $"худший {frames[frames.Count - 1] * 1000f:0.0} мс, частиц {_sparks.Alive}");
            if (_dir != null)
            {
                yield return new WaitForEndOfFrame();
                Texture2D full = ScreenCapture.CaptureScreenshotAsTexture();
                Save("K-001_bench.png", full);
                UnityEngine.Object.Destroy(full);
            }
        }

        void BenchFrame(float dt)
        {
            for (int i = 0; i < _benchHeroes.Count; i++)
            {
                DodgerScript(_benchHeroes[i], _benchHomes[i], _t + i * 0.37f, dt, i % 2 == 0 ? 1f : -1f);
            }

            for (int i = 0; i < _benchEnemies.Count; i++)
            {
                SwingActor e = _benchEnemies[i];
                float su = (_t + e.Rig.Animator.PhaseOffset * SwingCycle) % SwingCycle;
                SwingPhase phase = PhaseAt(su, out float q);
                if (e.Animate(dt, phase, q))
                {
                    Vector2 at = (Vector2)e.Rig.Root.position + e.Axis * 1.1f;
                    _sparks.Emit(at, e.Axis, 3, AnimParams.HitSparkSpeed, AnimParams.HitSparkLife, 60f, false, AnimParams.StrikeFlash, AnimParams.SparkLight);
                }
            }
        }
    }
}
