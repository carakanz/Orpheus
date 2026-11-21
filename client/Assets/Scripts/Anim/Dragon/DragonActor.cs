#nullable enable
using UnityEngine;
using UnityEngine.Rendering;

namespace Orpheus.Client.Anim.Dragon
{
    /// <summary>Вид дракона: бок (смотрит вправо; влево — отражение корня), к зрителю, от зрителя.</summary>
    public enum DragonView
    {
        Side,
        Front,
        Back,
    }

    /// <summary>Что делает дракон (D-004): стоит, идёт, бежит (галоп), летит/зависает, кусает, дышит огнём, получил удар, умер.</summary>
    public enum DragonAct
    {
        Idle,
        Walk,
        Run,
        Fly,
        Bite,
        Fire,
        Hit,
        Death,
    }

    /// <summary>
    /// D-004 шаг 1а: дракон «вырезкой» — части-спрайты на костях, скелет собирается кодом (иерархия Transform: корень →
    /// тело → шея ×4 → голова → челюсть/пасть; тело → хвост ×7 → кончик; тело → плечо/бедро → голень → стопа ×4;
    /// тело → крыло: плечо → кисть ×2). Позы — процедурно:
    /// <list type="bullet">
    /// <item>шея, хвост и крылья — направления в «объёме дракона» (вперёд, вбок, вверх), спроецированные в экран своего вида
    /// (<see cref="Projection"/>): длина спрайта сжимается по проекции — крыло при взмахе поворачивается «в глубину», шея к
    /// зрителю укорачивается; углы сегментов догоняют цель пружинами с убывающей жёсткостью — хвост и шея идут с запаздыванием;</item>
    /// <item>лапы — двухзвенная IK к точке стопы; точка стопы — по фазе походки, фаза идёт от пройденного пути (а не от
    /// времени), поэтому в опоре стопа стоит на земле неподвижно (не скользит) при любой скорости;</item>
    /// <item>ходьба — боковая последовательность (ЛЗ, ЛП, ПЗ, ПП, опора 0,65), бег — галоп (задние парой, потом передние,
    /// опора 0,38) с качанием тела.</item>
    /// </list>
    /// Корень — точка на земле под центром тела; порядок частей — внутри <see cref="SortingGroup"/>, сам дракон — по ногам.
    /// </summary>
    public sealed partial class DragonActor
    {
        sealed class Bone
        {
            public Transform T = null!;
            public Transform S = null!;
            public SpriteRenderer? R;
            public Bone? Parent;
            public Vector2 Pos;
            public float Ang;
            public float Tint = 1f;
        }

        sealed class Leg
        {
            public Bone Upper = null!, Lower = null!, Foot = null!;
            public string Anchor = "";
            public Vector2 Rest;       // стопа (точка подошвы) в покое, от корня
            public float Bend;         // куда гнётся сустав: +1 — против часовой
            public float L1, L2;
            public Vector2 Sole;       // подошва от сустава стопы
            public int Role;           // 0 — ближняя/правая передняя, 1 — ближняя/правая задняя, 2, 3 — дальняя/левая
            public Vector2 Hang;       // в полёте стопа висит: от бедра
        }

        sealed class Wing
        {
            public Bone Arm = null!, Hand = null!;
            public string Anchor = "";
            public Vector2 Side;       // «вбок» этого крыла в экране
            public Spring ArmE, ArmS, HandE, HandS;
        }

        /// <summary>Пружина с критическим затуханием (угол в градусах или число).</summary>
        struct Spring
        {
            public float X, V;

            public void Step(float target, float omega, float dt)
            {
                float f = 1f + 2f * dt * omega;
                float oo = omega * omega;
                float hoo = dt * oo;
                float hhoo = dt * hoo;
                float inv = 1f / (f + hhoo);
                float x = (f * X + dt * V + hhoo * target) * inv;
                float v = (V + hoo * (target - X)) * inv;
                X = x;
                V = v;
            }
        }

        /// <summary>Проекция «объёма дракона» в экран вида: вперёд (куда смотрит), вбок (правый бок), вверх.</summary>
        struct Projection
        {
            public Vector2 F, L, U;

            public Vector2 Dir(float elevDeg, float yawDeg)
            {
                float e = elevDeg * Mathf.Deg2Rad, y = yawDeg * Mathf.Deg2Rad;
                return Mathf.Cos(e) * Mathf.Cos(y) * F + Mathf.Cos(e) * Mathf.Sin(y) * L + Mathf.Sin(e) * U;
            }
        }

        public readonly Transform Root;
        public readonly DragonView View;
        readonly DragonParts _parts;
        readonly SortingGroup _group;
        readonly Transform _shadow;
        readonly Bone _body;
        readonly Bone[] _neck;
        readonly Bone[] _tail;
        readonly Bone _tip;
        readonly Bone _head;
        readonly Bone? _jaw;
        readonly Bone? _mouth;
        readonly Leg[] _legs;
        readonly Wing[] _wings;
        readonly Bone[] _all;
        readonly Projection _pr;
        readonly Vector2 _bodyRest;
        readonly float[] _neckRest;
        readonly float[] _tailRest;
        readonly Spring[] _neckE, _neckY, _tailE, _tailY;
        Spring _headP, _jawS, _height, _bodyDrop, _bodyTilt, _fold;

        // ---- состояние ----
        DragonAct _act = DragonAct.Idle;
        float _actT;
        float _t;
        float _phase;      // фаза шага (циклы), идёт от пройденного пути
        float _gaitW;      // 0 — стоит, 1 — идёт (плавно)
        float _flap;       // фаза взмаха (радианы)
        float _flash;
        Vector2 _hitDir = Vector2.left;
        bool _facingLeft;

        /// <summary>Пасть (мир) и направление огня — для частиц (<see cref="FireFx"/>).</summary>
        public Vector2 MouthWorld { get; private set; }
        public Vector2 MouthDir { get; private set; }

        /// <summary>Сейчас дышит огнём (выпускать частицы).</summary>
        public bool Breathing { get; private set; }

        /// <summary>Порядок отрисовки дракона целиком (огонь — на шаг выше).</summary>
        public int Order => _group.sortingOrder;

        public DragonAct Act => _act;

        public DragonActor(Transform parent, string name, DragonParts parts, DragonView view, Material material)
        {
            _parts = parts;
            View = view;
            Root = new GameObject(name).transform;
            Root.SetParent(parent, false);
            _group = Root.gameObject.AddComponent<SortingGroup>();
            _shadow = new GameObject("ShadowAnchor").transform;
            _shadow.SetParent(Root, false);
            WorldFx.AddActorShadow(_shadow, view == DragonView.Side ? 2.0f : 1.3f, view == DragonView.Side ? 0.55f : 0.9f);
            float far = parts.Far;
            var all = new System.Collections.Generic.List<Bone>();

            Bone Make(string bname, string? part, Bone? par, int order, float tint = 1f)
            {
                var b = new Bone { Parent = par, Tint = tint };
                b.T = new GameObject(bname).transform;
                b.T.SetParent(par != null ? par.T : Root, false);
                b.S = new GameObject("Sprite").transform;
                b.S.SetParent(b.T, false);
                if (part != null)
                {
                    b.R = b.S.gameObject.AddComponent<SpriteRenderer>();
                    b.R.sprite = parts.SpriteOf(part);
                    b.R.sharedMaterial = material;
                    b.R.sortingOrder = order;
                    b.R.color = new Color(tint, tint, tint, 1f);
                }

                all.Add(b);
                return b;
            }

            string bodyPart, headPart, neckPrefix, tailPrefix, tipPart;
            int[] neckOrder, tailOrder;
            int headOrder;
            switch (view)
            {
                case DragonView.Side:
                    _pr = new Projection { F = new Vector2(1f, 0f), L = new Vector2(0f, -0.42f), U = new Vector2(0f, 0.9f) };
                    _bodyRest = new Vector2(0f, 0.60f);
                    bodyPart = "body_side";
                    headPart = "head_side";
                    neckPrefix = "neck_";
                    tailPrefix = "tail_";
                    tipPart = "tail_tip";
                    _neckRest = new[] { 50f, 42f, 30f, 16f };
                    _tailRest = new[] { -10f, -8f, -4f, 0f, 4f, 7f, 9f };
                    neckOrder = new[] { 31, 32, 33, 34 };
                    tailOrder = new[] { 20, 19, 18, 17, 16, 15, 14 };
                    headOrder = 40;
                    break;
                case DragonView.Front:
                    _pr = new Projection { F = new Vector2(0f, -0.8f), L = new Vector2(-1f, 0f), U = new Vector2(0f, 0.62f) };
                    _bodyRest = new Vector2(0f, 0.50f);
                    bodyPart = "body_front";
                    headPart = "head_front";
                    neckPrefix = "neck_top_";
                    tailPrefix = "tail_top_";
                    tipPart = "tail_tip_top";
                    _neckRest = new[] { 62f, 56f, 48f, 36f };
                    _tailRest = new[] { 10f, 14f, 16f, 12f, 6f, 0f, -4f };
                    neckOrder = new[] { 31, 32, 33, 34 };
                    tailOrder = new[] { 7, 6, 5, 4, 3, 2, 1 };
                    headOrder = 40;
                    break;
                default:
                    _pr = new Projection { F = new Vector2(0f, 0.8f), L = new Vector2(1f, 0f), U = new Vector2(0f, 0.62f) };
                    _bodyRest = new Vector2(0f, 0.50f);
                    bodyPart = "body_back";
                    headPart = "head_back";
                    neckPrefix = "neck_top_";
                    tailPrefix = "tail_top_";
                    tipPart = "tail_tip_top";
                    _neckRest = new[] { 50f, 44f, 36f, 26f };
                    _tailRest = new[] { -6f, -6f, -4f, -2f, 0f, 0f, 0f };
                    neckOrder = new[] { 8, 7, 6, 5 };
                    tailOrder = new[] { 30, 31, 32, 33, 34, 35, 36 };
                    headOrder = 2;
                    break;
            }

            _body = Make("Body", bodyPart, null, 20);
            _neck = new Bone[4];
            Bone prev = _body;
            for (int i = 0; i < 4; i++)
            {
                _neck[i] = Make("Neck" + i, neckPrefix + i, prev, neckOrder[i]);
                prev = _neck[i];
            }

            _head = Make("Head", headPart, prev, headOrder);
            if (view != DragonView.Back)
            {
                _mouth = Make("Mouth", view == DragonView.Side ? "mouth_side" : "mouth_front", _head, headOrder - 2);
                _jaw = Make("Jaw", view == DragonView.Side ? "jaw_side" : "jaw_front", _head, headOrder - 1);
            }

            _tail = new Bone[7];
            prev = _body;
            for (int i = 0; i < 7; i++)
            {
                _tail[i] = Make("Tail" + i, tailPrefix + i, prev, tailOrder[i]);
                prev = _tail[i];
            }

            _tip = Make("TailTip", tipPart, prev, tailOrder[6] + (view == DragonView.Back ? 1 : -1));

            _legs = new Leg[4];
            _wings = new Wing[2];
            if (view == DragonView.Side)
            {
                _legs[0] = MakeLeg("LegNF", "shoulder_near", "fore", new Vector2(0.50f, 0f), -1f, 1f, 50, 0, Make);
                _legs[1] = MakeLeg("LegNH", "hip_near", "hind", new Vector2(-0.44f, 0f), 1f, 1f, 54, 1, Make);
                _legs[2] = MakeLeg("LegFF", "shoulder_far", "fore", new Vector2(0.58f, 0.17f), -1f, far, 2, 2, Make);
                _legs[3] = MakeLeg("LegFH", "hip_far", "hind", new Vector2(-0.36f, 0.17f), 1f, far, 6, 3, Make);
                _wings[0] = MakeWing("WingN", "wing_near", new Vector2(0f, -0.5f), 1f, 60, Make);
                _wings[1] = MakeWing("WingF", "wing_far", new Vector2(0f, 0.5f), far, 0, Make);
            }
            else
            {
                bool front = view == DragonView.Front;
                // к зрителю: передние лапы ближе (перед телом), задние — за ним; от зрителя — наоборот
                int foreOrder = front ? 22 : 6, hindOrder = front ? 12 : 26;
                float foreY = front ? -0.02f : 0.50f, hindY = front ? 0.62f : -0.16f;
                float foreTint = front ? 1f : 0.86f, hindTint = front ? 0.86f : 1f;
                _legs[0] = MakeLeg("LegRF", "shoulder_r", "top", new Vector2(0.44f, foreY), 1f, foreTint, foreOrder, 0, Make);
                _legs[1] = MakeLeg("LegRH", "hip_r", "top", new Vector2(0.40f, hindY), 1f, hindTint, hindOrder, 1, Make);
                _legs[2] = MakeLeg("LegLF", "shoulder_l", "top", new Vector2(-0.44f, foreY), -1f, foreTint, foreOrder, 2, Make);
                _legs[3] = MakeLeg("LegLH", "hip_l", "top", new Vector2(-0.40f, hindY), -1f, hindTint, hindOrder, 3, Make);
                int wingOrder = front ? 10 : 24;
                _wings[0] = MakeWing("WingR", "wing_r", new Vector2(1f, 0f), 1f, wingOrder, Make);
                _wings[1] = MakeWing("WingL", "wing_l", new Vector2(-1f, 0f), 1f, wingOrder, Make);
            }

            _all = all.ToArray();
            _neckE = new Spring[4];
            _neckY = new Spring[4];
            _tailE = new Spring[7];
            _tailY = new Spring[7];
            for (int i = 0; i < 4; i++)
            {
                _neckE[i].X = _neckRest[i];
            }

            for (int i = 0; i < 7; i++)
            {
                _tailE[i].X = _tailRest[i];
            }

            foreach (Wing w in _wings)
            {
                w.ArmE.X = FoldArmE;
                w.ArmS.X = FoldArmS;
                w.HandE.X = FoldHandE;
                w.HandS.X = FoldHandS;
            }

            _fold.X = 1f;
            Step(0f, 0f);
        }

        Leg MakeLeg(string name, string anchor, string kind, Vector2 rest, float bend, float tint, int order, int role,
            System.Func<string, string?, Bone?, int, float, Bone> make)
        {
            string up = kind == "top" ? "leg_top_upper" : kind + "_upper";
            string lo = kind == "top" ? "leg_top_lower" : kind + "_lower";
            string ft = kind == "top" ? "foot_front" : kind + "_foot";
            var l = new Leg { Anchor = anchor, Rest = rest, Bend = bend, Role = role };
            l.Upper = make(name + "Up", up, _body, order + 1, tint);
            l.Lower = make(name + "Lo", lo, l.Upper, order, tint);
            l.Foot = make(name + "Foot", ft, l.Lower, order + 2, tint);
            l.L1 = _parts.Length(up);
            l.L2 = _parts.Length(lo);
            l.Sole = _parts.Anchor(ft, "sole");
            l.Hang = View == DragonView.Side ? new Vector2(kind == "fore" ? 0.10f : -0.16f, -0.40f) : new Vector2(0f, -0.36f);
            return l;
        }

        Wing MakeWing(string name, string anchor, Vector2 side, float tint, int order, System.Func<string, string?, Bone?, int, float, Bone> make)
        {
            var w = new Wing { Anchor = anchor, Side = side };
            w.Arm = make(name + "Arm", "wing_arm", _body, order, tint);
            w.Hand = make(name + "Hand", "wing_hand", w.Arm, order + 1, tint);
            return w;
        }

        /// <summary>Сменить действие (время действия — с нуля). Удар — с направлением (экран, откуда пришёл).</summary>
        public void Play(DragonAct act, Vector2 hitFrom = default)
        {
            if (act == DragonAct.Hit || act == DragonAct.Death)
            {
                _hitDir = hitFrom.sqrMagnitude > 1e-4f ? hitFrom.normalized : Vector2.left;
                if (_facingLeft)
                {
                    _hitDir.x = -_hitDir.x;
                }

                _flash = 1f;
                // рывок шеи и хвоста от удара — пружины получают скорость, дальше догоняют сами
                for (int i = 0; i < 4; i++)
                {
                    _neckE[i].V += 260f * (0.6f + 0.25f * i);
                }

                for (int i = 0; i < 7; i++)
                {
                    _tailY[i].V += 300f * (i + 1) / 7f;
                }
            }

            _act = act;
            _actT = 0f;
        }

        /// <summary>Смотрит влево (только вид сбоку): корень отражается.</summary>
        public void Face(bool left)
        {
            _facingLeft = left && View == DragonView.Side;
            Root.localScale = new Vector3(_facingLeft ? -1f : 1f, 1f, 1f);
        }

        /// <summary>Кадр: <paramref name="moved"/> — сколько корень прошёл за кадр (тайлы) — от него идёт фаза шага.</summary>
        public void Step(float dt, float moved)
        {
            _t += dt;
            _actT += dt;
            _flash = Mathf.Max(0f, _flash - dt / 0.22f);
            Pose(dt, moved);
            _group.sortingOrder = WorldView.SortOrderAt(Root.position.y);
        }
    }
}
