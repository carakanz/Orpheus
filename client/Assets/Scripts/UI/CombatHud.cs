#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>Враг на экране для полоски здоровья (E3-1): где голова (мир Unity — точка ядра), доля здоровья, всегда ли видна, имя.</summary>
    public struct EnemyMark
    {
        public int Id;
        public Vector3 World;
        public int HealthPct;
        public bool Always;
        public string? Name;
    }

    /// <summary>
    /// Бой на экране (E3-1, шаг 3.1): цифры урона над существами, полоски здоровья врагов над головой и экран смерти.
    /// Данные — <see cref="CombatFeed"/> (только сообщения сервера); здесь — рисование. Размеры — <see cref="HudLayout"/>;
    /// всё ставится на целые пиксели кадра (<see cref="UiRoot.PixelsPerUnit"/>), надписи — <see cref="UiRoot.Label"/>
    /// (<see cref="UiTextFx"/>: целые пиксели и обводка), полоска — прямоугольники без растяжения растра (C-015).
    /// Слой цифр и полосок — под окнами (создаётся первым), экран смерти — поверх всего (<see cref="CreateOverlay"/> — последним).
    /// </summary>
    public sealed class CombatHud
    {
        const int NumberPool = 32, BarPool = 16;
        const float NumberBoxW = 200f, NumberBoxH = 80f;

        // Цвета цифр: урон игроку — красный, свой по врагу — жёлтый, чужой по врагу — белый.
        public static readonly Color32 ToneToPlayer = new Color32(255, 70, 58, 255);
        public static readonly Color32 ToneByMe = new Color32(255, 214, 64, 255);
        public static readonly Color32 ToneOther = new Color32(246, 242, 232, 255);
        static readonly Color32 BarBack = new Color32(20, 14, 12, 210);
        static readonly Color32 BarFill = new Color32(214, 44, 40, 255);
        static readonly Color32 BarFillElite = new Color32(232, 132, 30, 255);
        static readonly Color32 DeathTitle = new Color32(220, 48, 40, 255);

        readonly UiRoot _root;
        readonly RectTransform _marks;
        readonly List<Text> _numbers = new List<Text>();
        readonly List<Bar> _bars = new List<Bar>();
        int _numbersUsed, _barsUsed;

        RectTransform? _overlay;
        Image? _shade;
        readonly Image _restShade; // C-030: лёжа в кровати — экран темнеет (под всеми окнами)
        Text? _title, _cause, _timer, _note;

        sealed class Bar
        {
            public RectTransform Rt = null!;
            public Image Back = null!;
            public Image Fill = null!;
            public Text Name = null!;
        }

        public CombatHud(UiRoot root)
        {
            _root = root;
            RectTransform rest = UiRoot.Node(root.Transform, "RestShade", 0f, 0f, 0f, 0f);
            Stretch(rest);
            _restShade = rest.gameObject.AddComponent<Image>();
            _restShade.raycastTarget = false;
            _restShade.color = new Color32(0, 0, 0, 0);
            rest.gameObject.SetActive(false);
            _marks = UiRoot.Node(root.Transform, "CombatMarks", 0f, 0f, 0f, 0f);
            Anchor(_marks);
        }

        /// <summary>C-030: затемнение «лежит в кровати» (0 — нет): тёмно-синий тон ночи поверх мира, под окнами и цифрами.</summary>
        public void RestShade(float dim)
        {
            bool on = dim > 0.003f;
            SetActive(_restShade.gameObject, on);
            if (on)
            {
                _restShade.color = new Color(0.02f, 0.03f, 0.09f, dim);
            }
        }

        /// <summary>Экран смерти — поверх всего интерфейса: звать после создания окон.</summary>
        public void CreateOverlay()
        {
            _overlay = UiRoot.Node(_root.Transform, "DeathScreen", 0f, 0f, 0f, 0f);
            Stretch(_overlay);
            _shade = _overlay.gameObject.AddComponent<Image>();
            _shade.raycastTarget = false;
            _shade.color = new Color32(0, 0, 0, 0);
            _title = UiRoot.Label(_overlay, "Title", 0f, 0f, 1200f, 120f, HudLayout.DeathTitleFont, TextAnchor.MiddleCenter, DeathTitle, outline: true, bold: true);
            _cause = UiRoot.Label(_overlay, "Cause", 0f, 0f, 1200f, 44f, HudLayout.DeathLineFont, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true);
            _timer = UiRoot.Label(_overlay, "Timer", 0f, 0f, 1200f, 44f, HudLayout.DeathLineFont, TextAnchor.MiddleCenter, UiStyle.Gold, outline: true, bold: true);
            _note = UiRoot.Label(_overlay, "Note", 0f, 0f, 1200f, 44f, HudLayout.DeathLineFont, TextAnchor.MiddleCenter, UiStyle.TextGood, outline: true, bold: true);
            _overlay.gameObject.SetActive(false);
        }

        /// <summary>
        /// Кадр: полоски врагов (<paramref name="enemies"/>), цифры ленты (позиция — <paramref name="toUnity"/> от точки удара)
        /// и экран смерти (<paramref name="killer"/> — строка причины).
        /// </summary>
        public void Frame(CombatFeed feed, double now, IReadOnlyList<EnemyMark> enemies, System.Func<WorldPos, Vector3> toUnity, string killer)
        {
            _numbersUsed = 0;
            _barsUsed = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyMark e = enemies[i];
                if (feed.BarVisible(e.Id, now, e.Always, e.HealthPct))
                {
                    ShowBar(_root.FromWorld(e.World + new Vector3(0f, HudLayout.EnemyBarLift, 0f)), e.HealthPct / 100f, e.Always ? e.Name : null);
                }
            }

            IReadOnlyList<DamageNumber> numbers = feed.Numbers;
            for (int i = 0; i < numbers.Count; i++)
            {
                DamageNumber n = numbers[i];
                double age = now - n.Start;
                if (age < 0 || age >= CombatFeed.NumberSec)
                {
                    continue;
                }

                Vector3 w = toUnity(n.At) + new Vector3(0f, HudLayout.DamageLift + CombatFeed.Rise(age), 0f);
                Vector2 lane = new Vector2(CombatFeed.LaneSide(n.Lane) * HudLayout.DamageLaneX, (n.Lane > 0 ? 1 : 0) * HudLayout.DamageLaneY);
                ShowNumber(_root.FromWorld(w) + lane, n, age);
            }

            for (int i = _numbersUsed; i < _numbers.Count; i++)
            {
                SetActive(_numbers[i].gameObject, false);
            }

            for (int i = _barsUsed; i < _bars.Count; i++)
            {
                SetActive(_bars[i].Rt.gameObject, false);
            }

            DeathFrame(feed, now, killer);
        }

        void ShowBar(Vector2 at, float frac, string? name)
        {
            if (_barsUsed >= BarPool)
            {
                return;
            }

            if (_barsUsed == _bars.Count)
            {
                var b = new Bar();
                b.Rt = UiRoot.Node(_marks, "EnemyBar", 0f, 0f, HudLayout.EnemyBarW, HudLayout.EnemyBarH);
                b.Back = UiRoot.Picture(b.Rt, "Back", 0f, 0f, HudLayout.EnemyBarW, HudLayout.EnemyBarH, null, sliced: false);
                b.Back.color = BarBack;
                float inner = HudLayout.EnemyBarH - 2f * HudLayout.EnemyBarBorder;
                b.Fill = UiRoot.Picture(b.Rt, "Fill", HudLayout.EnemyBarBorder, HudLayout.EnemyBarBorder, 0f, inner, null, sliced: false);
                b.Fill.color = BarFill;
                b.Name = UiRoot.Label(b.Rt, "Name", -120f, HudLayout.EnemyBarH + HudLayout.EnemyNameGap, HudLayout.EnemyBarW + 240f, 26f,
                    HudLayout.EnemyNameFont, TextAnchor.LowerCenter, UiStyle.TextLight, outline: true, bold: true);
                _bars.Add(b);
            }

            Bar bar = _bars[_barsUsed++];
            SetActive(bar.Rt.gameObject, true);
            bar.Rt.anchoredPosition = Snap(at - new Vector2(HudLayout.EnemyBarW / 2f, 0f));
            float innerW = HudLayout.EnemyBarW - 2f * HudLayout.EnemyBarBorder;
            float ppu = UiRoot.PixelsPerUnit;
            float fillW = Mathf.Round(innerW * Mathf.Clamp01(frac) * ppu) / ppu; // край заливки — на целом пикселе
            bar.Fill.rectTransform.sizeDelta = new Vector2(Mathf.Max(fillW, frac > 0f ? 1f / ppu : 0f), HudLayout.EnemyBarH - 2f * HudLayout.EnemyBarBorder);
            bar.Fill.color = name != null ? BarFillElite : BarFill;
            bool named = name != null;
            SetActive(bar.Name.gameObject, named);
            if (named && bar.Name.text != name)
            {
                bar.Name.text = name;
            }
        }

        void ShowNumber(Vector2 at, in DamageNumber n, double age)
        {
            if (_numbersUsed >= NumberPool)
            {
                return;
            }

            if (_numbersUsed == _numbers.Count)
            {
                _numbers.Add(UiRoot.Label(_marks, "Damage", 0f, 0f, NumberBoxW, NumberBoxH, HudLayout.DamageFont, TextAnchor.LowerCenter,
                    ToneOther, outline: true, bold: true));
            }

            Text t = _numbers[_numbersUsed++];
            SetActive(t.gameObject, true);
            string s = n.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (t.text != s)
            {
                t.text = s;
            }

            int size = Mathf.RoundToInt(HudLayout.DamageFont * CombatFeed.Scale(age, n.Strong));
            if (t.fontSize != size)
            {
                t.fontSize = size;
            }

            Color32 c = n.Tone == DamageTone.ToPlayer ? ToneToPlayer : n.Tone == DamageTone.ByMe ? ToneByMe : ToneOther;
            c.a = (byte)Mathf.RoundToInt(255f * CombatFeed.Alpha(age));
            if (!t.color.Equals((Color)c))
            {
                t.color = c;
            }

            t.rectTransform.anchoredPosition = Snap(at - new Vector2(NumberBoxW / 2f, 0f));
        }

        void DeathFrame(CombatFeed feed, double now, string killer)
        {
            if (_overlay == null)
            {
                return;
            }

            float fade = feed.Fade(now);
            bool note = feed.RespawnNote(now);
            bool show = feed.Dead || fade > 0.003f || note;
            SetActive(_overlay.gameObject, show);
            if (!show)
            {
                return;
            }

            _shade!.color = new Color(0.03f, 0.01f, 0.02f, fade);
            Vector2 size = _root.Size;
            float titleY = size.y * HudLayout.DeathTitleY;
            Place(_title!, size, titleY);
            Place(_cause!, size, titleY - 60f - HudLayout.DeathLineGap - 22f);
            Place(_timer!, size, titleY - 60f - 2f * HudLayout.DeathLineGap - 66f);
            Place(_note!, size, titleY - 60f - HudLayout.DeathLineGap - 22f);
            SetActive(_title!.gameObject, feed.Dead);
            SetActive(_cause!.gameObject, feed.Dead);
            SetActive(_timer!.gameObject, feed.Dead);
            SetActive(_note!.gameObject, note);
            if (feed.Dead)
            {
                SetText(_title, UiStrings.DeathTitle);
                SetText(_cause, killer);
                SetText(_timer, UiStrings.RespawnIn(feed.RespawnLeftWhole(now), feed.AtBed));
            }

            if (note)
            {
                SetText(_note!, UiStrings.Respawned(feed.AtBed));
            }
        }

        void Place(Text t, Vector2 canvas, float centerY)
        {
            RectTransform rt = t.rectTransform;
            rt.anchoredPosition = Snap(new Vector2(canvas.x / 2f - rt.sizeDelta.x / 2f, centerY - rt.sizeDelta.y / 2f));
        }

        static void SetText(Text t, string s)
        {
            if (t.text != s)
            {
                t.text = s;
            }
        }

        /// <summary>Точка холста → ближайший целый пиксель кадра (надпись и полоска не «плывут» между пикселями).</summary>
        static Vector2 Snap(Vector2 p)
        {
            float ppu = UiRoot.PixelsPerUnit;
            return new Vector2(Mathf.Round(p.x * ppu) / ppu, Mathf.Round(p.y * ppu) / ppu);
        }

        static void SetActive(GameObject go, bool on)
        {
            if (go.activeSelf != on)
            {
                go.SetActive(on);
            }
        }

        static void Anchor(RectTransform rt)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
        }
    }
}
