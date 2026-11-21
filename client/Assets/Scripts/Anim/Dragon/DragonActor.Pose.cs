#nullable enable
using UnityEngine;

namespace Orpheus.Client.Anim.Dragon
{
    public sealed partial class DragonActor
    {
        // Походка: длина цикла (тайлы пути на полный цикл), доля опоры, сдвиги фаз лап по ролям
        // (0 — ближняя/правая передняя, 1 — ближняя/правая задняя, 2 — дальняя/левая передняя, 3 — дальняя/левая задняя).
        const float WalkCycle = 0.72f, WalkDuty = 0.65f, WalkLift = 0.10f;
        const float RunCycle = 1.5f, RunDuty = 0.38f, RunLift = 0.16f;
        static readonly float[] WalkOffsets = { 0.75f, 0.5f, 0.25f, 0f };   // боковая последовательность: ЛЗ, ЛП, ПЗ, ПП
        static readonly float[] RunOffsets = { 0.58f, 0.08f, 0.5f, 0f };    // галоп: задние парой, потом передние

        public const float FlyHeight = 1.1f;
        const float FlapHz = 2.3f;

        // сложенные крылья: плечо вверх-назад, кисть назад и вниз вдоль спины, перепонка свисает к боку
        const float FoldArmE = 40f, FoldArmS = 55f, FoldHandE = -15f, FoldHandS = 100f;

        static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        static Vector2 Rot(Vector2 v, float deg)
        {
            float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        static float AngOf(Vector2 v) => Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;

        void Set(Bone b, Vector2 pos, float ang, float sx = 1f, float sy = 1f)
        {
            b.Pos = pos;
            b.Ang = ang;
            if (b.Parent == null)
            {
                b.T.localPosition = pos;
                b.T.localRotation = Quaternion.Euler(0f, 0f, ang);
            }
            else
            {
                b.T.localPosition = Rot(pos - b.Parent.Pos, -b.Parent.Ang);
                b.T.localRotation = Quaternion.Euler(0f, 0f, ang - b.Parent.Ang);
            }

            b.S.localScale = new Vector3(sx, sy, 1f);
        }

        Vector2 At(Bone b, string part, string anchor) => b.Pos + Rot(_parts.Anchor(part, anchor), b.Ang);

        string BodyPart => View == DragonView.Side ? "body_side" : View == DragonView.Front ? "body_front" : "body_back";

        float DeathK => _act == DragonAct.Death ? Smooth((_actT - 0.15f) / 0.75f) : 0f;

        void Pose(float dt, float moved)
        {
            DragonAct a = _act;
            float T = _actT;
            bool walk = a == DragonAct.Walk, run = a == DragonAct.Run;
            float cycle = run ? RunCycle : WalkCycle, duty = run ? RunDuty : WalkDuty, liftH = run ? RunLift : WalkLift;
            float[] offsets = run ? RunOffsets : WalkOffsets;
            if (walk || run)
            {
                _phase += moved / cycle;
            }

            _gaitW = Mathf.MoveTowards(_gaitW, walk || run ? 1f : 0f, dt * 5f);
            float ph = _phase * Mathf.PI * 2f;
            float breath = Mathf.Sin(_t * Mathf.PI * 2f / 2.8f);
            float dk = DeathK;
            bool side = View == DragonView.Side;

            // ---------------- полёт ----------------
            float flyW = 0f, crouch = 0f;
            if (a == DragonAct.Fly)
            {
                _flap += dt * Mathf.PI * 2f * FlapHz;
                crouch = T < 0.4f ? -0.09f * Mathf.Sin(Mathf.PI * T / 0.4f) : 0f;
                flyW = Smooth((T - 0.3f) / 0.4f);
            }

            _height.Step(a == DragonAct.Fly && T > 0.32f ? FlyHeight : 0f, 3.2f, dt);
            float h = Mathf.Max(0f, _height.X);

            // ---------------- тело ----------------
            Vector2 off = Vector2.zero;
            float bodyAng = 0f;
            if (walk)
            {
                off.y -= 0.02f * (0.5f - 0.5f * Mathf.Cos(2f * ph)) * _gaitW;
                if (side)
                {
                    bodyAng += 1.6f * Mathf.Sin(ph) * _gaitW;
                }
                else
                {
                    off.x += 0.022f * Mathf.Sin(ph) * _gaitW;
                }
            }
            else if (run)
            {
                off.y += 0.05f * Mathf.Sin(ph - 0.6f) * _gaitW;
                if (side)
                {
                    bodyAng += -6f * Mathf.Cos(ph) * _gaitW;
                }
                else
                {
                    off.x += 0.016f * Mathf.Sin(ph) * _gaitW;
                }
            }

            off.y += 0.008f * breath;
            off.y -= 0.05f * Mathf.Cos(_flap) * flyW; // тело подскакивает на взмахе вниз

            // укус, огонь — веса фаз
            float reared = 0f, lunge = 0f, raise = 0f, blow = 0f, jaw = 0f, headPitch = 0f;
            if (a == DragonAct.Bite)
            {
                float w = Smooth(T / 0.45f), s = Smooth((T - 0.45f) / 0.12f), r = Smooth((T - 0.66f) / 0.5f);
                reared = w * (1f - s);
                lunge = s * (1f - r);
                jaw = 0.7f * reared + (T >= 0.45f && T < 0.57f ? 1f : 0f);
                headPitch = 18f * reared - 20f * lunge;
            }
            else if (a == DragonAct.Fire)
            {
                raise = Smooth(T / 0.6f) * (1f - Smooth((T - 0.6f) / 0.18f));
                blow = Smooth((T - 0.6f) / 0.15f) * (1f - Smooth((T - 2.0f) / 0.35f));
                jaw = 0.25f * raise + 0.95f * blow;
                headPitch = 22f * raise - 10f * blow;
            }

            Breathing = a == DragonAct.Fire && T > 0.66f && T < 2.05f;

            // удар: отскок от удара и назад
            Vector2 recoil = Vector2.zero;
            if (a == DragonAct.Hit || a == DragonAct.Death)
            {
                float r = T < 0.07f ? T / 0.07f : Mathf.Exp(-(T - 0.07f) * 7f);
                recoil = -_hitDir * 0.11f * r;
            }

            float dropTarget = dk * (side ? 0.36f : 0.30f);
            _bodyDrop.Step(dropTarget, 9f, dt);
            _bodyTilt.Step(side ? -4f * dk : 0f, 6f, dt);
            Vector2 bodyPos = _bodyRest + off + recoil + new Vector2(0f, h + crouch - _bodyDrop.X);
            float swell = 1f + 0.05f * raise + 0.012f * breath;
            Set(_body, bodyPos, bodyAng + _bodyTilt.X + (a == DragonAct.Hit ? 6f * recoil.x : 0f), 1f, swell);

            // ---------------- шея и голова ----------------
            Vector2 p = At(_body, BodyPart, "neck");
            float[] deathNeck = side ? new[] { -12f, -22f, -24f, -8f } : View == DragonView.Front ? new[] { 34f, 24f, 14f, 4f } : new[] { 12f, 2f, -6f, -10f };
            Vector2 lastDir = Vector2.right;
            for (int i = 0; i < 4; i++)
            {
                float e = _neckRest[i], y = 0f;
                if (a == DragonAct.Idle || a == DragonAct.Hit)
                {
                    e += 3f * Mathf.Sin(1.3f * _t - 0.5f * i);
                    y += 9f * Mathf.Sin(0.45f * _t - 0.3f * i);
                }

                if (walk)
                {
                    e += 3f * Mathf.Sin(2f * ph - 0.7f * i) - 4f;
                    y += 3f * Mathf.Sin(ph - 0.4f * i);
                }

                if (run)
                {
                    e += -14f + 5f * Mathf.Sin(ph - 0.8f * i);
                }

                if (a == DragonAct.Fly)
                {
                    e += -8f + 4f * Mathf.Sin(_flap - 0.6f * i) * flyW;
                }

                e += 22f * reared - 40f * lunge * (1f - 0.12f * i);
                e += 20f * raise - 12f * blow;
                if (a == DragonAct.Fire)
                {
                    y += blow * 16f * Mathf.Sin((T - 0.6f) * 2.4f) * (0.35f + 0.22f * i);
                }

                e = Mathf.Lerp(e, deathNeck[i], dk);
                float omega = dk > 0f ? 6f - 0.5f * i : 16f - 2.2f * i;
                _neckE[i].Step(e, omega, dt);
                _neckY[i].Step(y, omega, dt);
                Vector2 d = _pr.Dir(_neckE[i].X, _neckY[i].X);
                if (side)
                {
                    d = Rot(d, _body.Ang);
                }

                float len = d.magnitude, sx = Mathf.Max(len, 0.32f);
                Vector2 dn = len > 1e-5f ? d / len : Vector2.right;
                string part = (side ? "neck_" : "neck_top_") + i;
                Set(_neck[i], p, AngOf(dn), sx, 1f);
                p += dn * _parts.Length(part) * sx;
                lastDir = dn;
            }

            _headP.Step(headPitch + (a == DragonAct.Death ? -10f * dk : 0f), 14f, dt);
            _jawS.Step(Mathf.Lerp(jaw, 0.35f, dk), 30f, dt);
            float jawOpen = Mathf.Clamp01(_jawS.X);
            if (side)
            {
                float ha = AngOf(lastDir) - 30f + _headP.X;
                Set(_head, p, ha);
                Vector2 hinge = At(_head, "head_side", "jaw");
                Set(_mouth!, hinge, ha - 6f * jawOpen);
                Set(_jaw!, hinge, ha - 32f * jawOpen);
                MouthWorld = Root.TransformPoint(At(_head, "head_side", "mouth") + Rot(new Vector2(0f, -0.04f * jawOpen), ha));
                MouthDir = Root.TransformDirection(Rot(Vector2.right, ha - 14f * jawOpen));
            }
            else
            {
                float roll = _neckY[3].X * 0.35f;
                float hs = 1.2f * (1f + 0.16f * lunge + 0.04f * raise);
                Vector2 hp = p + new Vector2(0f, (View == DragonView.Front ? 0.0f : 0.06f) + _headP.X * 0.002f);
                Set(_head, hp, roll, hs, hs);
                if (_jaw != null)
                {
                    Vector2 hinge = At(_head, "head_front", "jaw") * 1f;
                    hinge = hp + Rot(_parts.Anchor("head_front", "jaw") * hs, roll);
                    Set(_mouth!, hinge, roll, hs, hs * (0.35f + 0.85f * jawOpen));
                    Set(_jaw, hinge + Rot(new Vector2(0f, -0.10f * jawOpen * hs), roll), roll, hs, hs);
                    MouthWorld = Root.TransformPoint(hinge + Rot(new Vector2(0f, -0.08f * hs), roll));
                    MouthDir = Root.TransformDirection(Rot(new Vector2(0f, -1f), roll * 2f + _neckY[3].X * 0.6f));
                }
                else
                {
                    MouthWorld = Root.TransformPoint(At(_head, "head_back", "mouth"));
                    MouthDir = Vector2.up;
                }
            }

            // ---------------- хвост ----------------
            p = At(_body, BodyPart, "tail");
            for (int i = 0; i < 7; i++)
            {
                float amp, wave;
                if (run)
                {
                    amp = 6f + 3.2f * i;
                    wave = Mathf.Sin(ph - 0.55f * i);
                }
                else if (walk)
                {
                    amp = 5f + 2.6f * i;
                    wave = Mathf.Sin(3.4f * _t - 0.55f * i);
                }
                else if (a == DragonAct.Fly)
                {
                    amp = 4f + 2.2f * i;
                    wave = Mathf.Sin(_flap * 0.5f - 0.6f * i);
                }
                else
                {
                    amp = 3f + 2.4f * i;
                    wave = Mathf.Sin(1.7f * _t - 0.55f * i);
                }

                float e = _tailRest[i] + 0.55f * amp * Mathf.Sin(1.7f * _t * (run ? 2f : 1f) - 0.55f * i + 1.1f);
                float y = amp * wave;
                if (a == DragonAct.Fly)
                {
                    e -= 6f * flyW;
                }

                e = Mathf.Lerp(e, i == 0 ? -6f : 0f, dk);
                y = Mathf.Lerp(y, 10f + 5f * i, dk);
                float omega = dk > 0f ? 5f : 10f - 1.0f * i;
                _tailE[i].Step(e, omega, dt);
                _tailY[i].Step(y, omega, dt);
                // хвост смотрит назад: «вперёд» × −1
                Vector2 d = _pr.Dir(_tailE[i].X, 180f + _tailY[i].X);
                if (side)
                {
                    d = Rot(d, _body.Ang);
                }

                float len = d.magnitude, sx = Mathf.Max(len, 0.32f);
                Vector2 dn = len > 1e-5f ? d / len : Vector2.left;
                string part = (side ? "tail_" : "tail_top_") + i;
                Set(_tail[i], p, AngOf(dn), sx, 1f);
                p += dn * _parts.Length(part) * sx;
                lastDir = dn;
            }

            Set(_tip, p, AngOf(lastDir));

            // ---------------- лапы ----------------
            Vector2 fwd = _pr.F.normalized;
            foreach (Leg l in _legs)
            {
                Vector2 hip = At(_body, BodyPart, l.Anchor);
                float along = 0f, lift = 0f;
                if (_gaitW > 0f)
                {
                    float q = Mathf.Repeat(_phase + offsets[l.Role], 1f);
                    float L = cycle * duty;
                    if (q < duty)
                    {
                        along = L * (0.5f - q / duty);
                    }
                    else
                    {
                        float s = (q - duty) / (1f - duty);
                        along = L * (-0.5f + Smooth(s));
                        lift = Mathf.Sin(Mathf.PI * s);
                    }

                    along *= _gaitW;
                    lift *= _gaitW;
                }

                Vector2 target = l.Rest + fwd * along + new Vector2(0f, liftH * lift);
                if (flyW > 0f)
                {
                    Vector2 hang = hip + Rot(l.Hang, _body.Ang) + new Vector2(0f, 0.03f * Mathf.Sin(_flap - 1f + l.Role));
                    target = Vector2.Lerp(target, hang, flyW);
                }

                if (dk > 0f)
                {
                    Vector2 splay = side ? new Vector2(l.Role % 2 == 0 ? 0.16f : -0.16f, 0.02f) : new Vector2(l.Rest.x * 0.35f, 0.04f);
                    target = Vector2.Lerp(target, l.Rest + splay, dk);
                }

                float footAng = side ? -18f * lift - 40f * flyW : 0f;
                Vector2 ankle = target - Rot(l.Sole, footAng);
                Vector2 dv = ankle - hip;
                float dist = Mathf.Clamp(dv.magnitude, Mathf.Abs(l.L1 - l.L2) + 1e-3f, (l.L1 + l.L2) * 0.999f);
                float baseA = Mathf.Atan2(dv.y, dv.x);
                float cosA = (l.L1 * l.L1 + dist * dist - l.L2 * l.L2) / (2f * l.L1 * dist);
                float a1 = baseA + l.Bend * Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
                Vector2 knee = hip + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * l.L1;
                Vector2 toAnkle = ankle - knee;
                float a2 = Mathf.Atan2(toAnkle.y, toAnkle.x);
                Vector2 reach = knee + new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * l.L2; // недостижимая цель — стопа на конце ноги
                Set(l.Upper, hip, a1 * Mathf.Rad2Deg);
                Set(l.Lower, knee, a2 * Mathf.Rad2Deg);
                Set(l.Foot, reach, footAng);
            }

            // ---------------- крылья ----------------
            float aE, aS, hE, hS, wOmega = 9f, foldT = 1f;
            if (a == DragonAct.Fly && T > 0.15f)
            {
                float f = _flap;
                aE = 18f + 52f * Mathf.Sin(f);
                aS = 22f - 10f * Mathf.Cos(f);
                hE = aE + 32f * Mathf.Sin(f - 1.0f) - 6f;
                hS = 48f + 22f * Mathf.Cos(f - 0.5f);
                wOmega = 30f;
                foldT = 0f;
            }
            else if (a == DragonAct.Bite || a == DragonAct.Fire)
            {
                float k = Mathf.Max(reared + lunge, raise + blow);
                aE = Mathf.Lerp(FoldArmE, 42f, k);
                aS = Mathf.Lerp(FoldArmS, 38f, k);
                hE = Mathf.Lerp(FoldHandE, 8f, k);
                hS = Mathf.Lerp(FoldHandS, 92f, k);
                foldT = 1f - k;
            }
            else
            {
                aE = FoldArmE + 2f * breath + (run ? 6f * Mathf.Sin(ph) : 0f);
                aS = FoldArmS;
                hE = FoldHandE + 1.5f * breath;
                hS = FoldHandS;
            }

            if (a == DragonAct.Hit && T < 0.45f)
            {
                float k = 1f - T / 0.45f;
                aE += 26f * k;
                aS -= 30f * k;
                hS -= 50f * k;
                hE += 30f * k;
                wOmega = 14f;
                foldT *= 1f - k;
            }

            if (dk > 0f)
            {
                aE = Mathf.Lerp(aE, -10f, dk);
                aS = Mathf.Lerp(aS, side ? 40f : 12f, dk);
                hE = Mathf.Lerp(hE, -16f, dk);
                hS = Mathf.Lerp(hS, side ? 86f : 45f, dk);
                wOmega = 6f;
                foldT *= 1f - dk;
            }

            _fold.Step(foldT, wOmega, dt);
            float fold = Mathf.Clamp01(_fold.X);
            Vector2 back = -_pr.F;
            Vector2 backN = back.normalized;
            foreach (Wing w in _wings)
            {
                w.ArmE.Step(aE, wOmega, dt);
                w.ArmS.Step(aS, wOmega, dt);
                w.HandE.Step(hE, wOmega, dt);
                w.HandS.Step(hS, wOmega, dt);
                Vector2 sh = At(_body, BodyPart, w.Anchor);
                // сложенное крыло: перепонка собрана (уже), кисть короче
                Vector2 wrist = WingBone(w.Arm, sh, w.Side, back, backN, w.ArmE.X, w.ArmS.X, "wing_arm", 1f, Mathf.Lerp(1f, 0.8f, fold), fold);
                WingBone(w.Hand, wrist, w.Side, back, backN, w.HandE.X, w.HandS.X, null, Mathf.Lerp(1f, 0.85f, fold), Mathf.Lerp(1f, 0.7f, fold), fold);
            }

            // ---------------- тень, цвет ----------------
            float sh2 = 1f - 0.45f * Mathf.Clamp01(h / FlyHeight);
            _shadow.localScale = new Vector3(sh2, sh2, 1f);
            float dark = Mathf.Lerp(1f, 0.58f, Smooth((T - 0.6f) / 1.2f) * (a == DragonAct.Death ? 1f : 0f));
            foreach (Bone b in _all)
            {
                if (b.R == null)
                {
                    continue;
                }

                float c = b.Tint * dark;
                b.R.color = new Color(c, Mathf.Lerp(c, c * 0.5f, _flash), Mathf.Lerp(c, c * 0.45f, _flash), 1f);
            }
        }

        /// <summary>Кость крыла: направление в объёме (подъём, отвод назад) → экран; длина спрайта — по проекции; перепонка
        /// (локальный −y спрайта) всегда «стекает» назад — при повороте крыла ребром она сжимается и переходит на другую сторону.</summary>
        Vector2 WingBone(Bone b, Vector2 at, Vector2 lateral, Vector2 back, Vector2 backN, float elev, float sweep, string? part, float kx, float ky, float fold)
        {
            float e = elev * Mathf.Deg2Rad, s = sweep * Mathf.Deg2Rad;
            Vector2 v = Mathf.Cos(e) * Mathf.Cos(s) * lateral + Mathf.Cos(e) * Mathf.Sin(s) * back + Mathf.Sin(e) * _pr.U;
            if (View == DragonView.Side)
            {
                v = Rot(v, _body.Ang);
            }

            float len = v.magnitude, sx = Mathf.Max(len, 0.24f);
            Vector2 vn = len > 1e-5f ? v / len : Vector2.up;
            Vector2 n = new Vector2(vn.y, -vn.x);
            float sy = Mathf.Clamp(Vector2.Dot(n, backN) * 1.6f, -1f, 1f);
            // сложенное крыло: перепонка прижата к телу — сбоку свисает вниз (к боку), спереди/сзади — внутрь, к хребту
            Vector2 inward = View == DragonView.Side ? Vector2.down : -lateral.normalized;
            float down = Mathf.Clamp(Vector2.Dot(n, inward) * 1.6f, -1f, 1f);
            sy = Mathf.Lerp(sy, down, fold);
            if (Mathf.Abs(sy) < 0.2f)
            {
                sy = sy < 0f ? -0.2f : 0.2f;
            }

            Set(b, at, AngOf(vn), sx * kx, sy * ky);
            return part != null ? at + vn * _parts.Length(part) * sx * kx : at;
        }
    }
}
