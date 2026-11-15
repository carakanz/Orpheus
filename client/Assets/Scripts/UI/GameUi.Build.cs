#nullable enable
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Что режиму стройки, сундуку и F нужно знать за кадр (E2-5): молот в руках, проверка тайла под курсором
    /// (<see cref="BuildHand.Check"/> — та же функция ядра, что у сервера), прочность постройки под курсором, открытый
    /// сундук сервера (<c>NetClient.Chest</c>) и что сделает F (<see cref="ObjectUse.Find"/>).
    /// </summary>
    public struct UiBuildState
    {
        public bool Hammer;

        /// <summary>В руке предмет-постройка (E2-11): призрак постановки.</summary>
        public bool Placing;
        public bool HasGhost;
        public BuildCheck Check;
        public bool HasIntegrity;
        public int Integrity;
        public int MaxIntegrity;
        public bool ChestOpen;
        public Inventory Chest;
        public UseKind Use;

        /// <summary>E2-13: постройка в руке и режим линейки — показать подпись режима; идёт протягивание — план <see cref="Plan"/>.</summary>
        public bool RulerMode;

        /// <summary>E2-16: постройка в руке поворачивается (R), она взята из меню, и сколько её можно поставить (готовые + изготовить).</summary>
        public bool Rotatable;
        public bool FromMenu;
        public int MenuAvailable;
        public bool Dragging;
        public BuildRuler? Plan;
    }

    /// <summary>Плашка у курсора (E2-5): строка призрака («Сундук — нельзя: занято») и полоса прочности постройки.</summary>
    public sealed class UiNote
    {
        readonly RectTransform _rt;
        readonly Image _back;
        readonly Text _text;
        readonly RectTransform _barRt;
        readonly UiBar _bar;
        string _shown = string.Empty;
        const float Line = 34f, BarH = 16f;

        public UiNote(UiRoot root)
        {
            _rt = UiRoot.Node(root.Transform, "Note", 0f, 0f, 100f, Line);
            _back = UiRoot.Picture(_rt, "Back", 0f, 0f, 100f, Line, UiStyle.Frame(UiStyle.Ink, UiStyle.Ink, radius: 6f));
            _text = UiRoot.Label(_rt, "Text", 12f, 0f, 400f, Line, 20, TextAnchor.MiddleLeft, UiStyle.TextLight);
            _barRt = UiRoot.Node(_rt, "Bar", 10f, 6f, 160f, BarH);
            _bar = new UiBar(_barRt, 0f, 0f, 160f, BarH, UiStyle.Stamina);
            _rt.gameObject.SetActive(false);
        }

        /// <summary>Показать строку (и полосу, если <paramref name="bar"/> ≥ 0) у точки холста; пусто — скрыть.</summary>
        public void Show(string? text, float bar, bool bad, Vector2 canvas, Vector2 at)
        {
            bool on = !string.IsNullOrEmpty(text);
            if (_rt.gameObject.activeSelf != on)
            {
                _rt.gameObject.SetActive(on);
            }

            if (!on)
            {
                return;
            }

            bool hasBar = bar >= 0f;
            float h = hasBar ? Line + BarH + 8f : Line;
            if (text != _shown)
            {
                _shown = text!;
                _text.text = _shown;
            }

            _text.color = bad ? new Color32(255, 150, 130, 255) : UiStyle.TextLight;
            float w = Mathf.Max(Mathf.Ceil(_text.preferredWidth) + 24f, hasBar ? 180f : 0f);
            _rt.sizeDelta = new Vector2(w, h);
            _back.rectTransform.sizeDelta = new Vector2(w, h);
            _text.rectTransform.anchoredPosition = new Vector2(12f, h - Line);
            _barRt.gameObject.SetActive(hasBar);
            if (hasBar)
            {
                _bar.Set(bar);
            }

            _rt.anchoredPosition = new Vector2(Mathf.Clamp(at.x + 22f, 4f, canvas.x - w - 4f), Mathf.Clamp(at.y - h - 18f, 4f, canvas.y - h - 4f));
        }
    }

    public sealed partial class GameUi
    {
        ChestWindow _chest = null!;
        UiNote _note = null!;
        BuildHand _build = null!;
        BuildHand _menuBuild = null!;
        Text _noticeText = null!;
        UiGroup _notice = null!;
        UiGroup _mode = null!;
        Text _modeText = null!;
        readonly BuildRuler _ruler = new BuildRuler { Mode = SessionMode };

        /// <summary>Режим постановки на сессию (E2-13): новый интерфейс (переподключение) берёт прежний.</summary>
        static PlaceMode SessionMode = PlaceMode.Ruler;
        string? _noticeMsg;
        float _noticeUntil;

        /// <summary>«Рука строителя» (заявки постановки и починки — тот же класс, что у бота).</summary>
        public BuildHand Build => _build;

        /// <summary>Линейка (E2-13): режим, протягивание, план — общий класс с ботом и тестами.</summary>
        public BuildRuler Ruler => _ruler;

        /// <summary>«Рука строителя» для постановки из меню (E2-16): те же заявки с флагом меню (изготовить на месте).</summary>
        public BuildHand MenuBuild => _menuBuild;

        /// <summary>
        /// Постройка, взятая «Поставить» из окна изготовления (E2-16), — предмет; 0 — нет. Ставит и снимает
        /// <c>NetGame</c> (Esc / ПКМ — убрать из руки, смена ячейки панели — тоже).
        /// </summary>
        public byte MenuPlace { get; set; }

        // E2-16: «Поставить» у рецепта постройки — окно закрывается, постройка в руке (призрак под курсором).
        void PlaceFromMenu(int recipe)
        {
            byte item = CraftWindow.PlaceableResult(recipe);
            if (item == Items.None)
            {
                return;
            }

            MenuPlace = item;
            _ruler.Cancel();
            _craft.Visible = false;
        }

        /// <summary>Переключить «линейка / поштучно» (Tab; до E2-16 — R); режим запоминается на сессию.</summary>
        public PlaceMode ToggleRuler()
        {
            _ruler.Cancel();
            SessionMode = _ruler.Toggle();
            return SessionMode;
        }

        /// <summary>Окно сундука открыто.</summary>
        public bool ChestShown => _chest.Visible;

        /// <summary>Игрок закрыл окно сундука в этом кадре (Esc, Tab) — сказать серверу «закрыть».</summary>
        public bool ChestCloseRequested { get; private set; }

        void CreateBuild(UiRoot root, IBuildRequests build, float barTop)
        {
            _build = new BuildHand(build);
            _menuBuild = new BuildHand(MenuHand.Requests(build));
            _chest = new ChestWindow(root);
            _note = new UiNote(root);
            const float nw = 620f, nh = 40f;
            _notice = new UiGroup(root, "Notice", new Vector2(0.5f, 0f), new Vector2(-nw / 2f, barTop + 48f), new Vector2(nw, nh));
            UiRoot.Picture(_notice.Rt, "Back", 0f, 0f, nw, nh, UiStyle.Frame(UiStyle.Ink, UiStyle.Ink, radius: 6f));
            _noticeText = UiRoot.Label(_notice.Rt, "Text", 0f, 0f, nw, nh, 22, TextAnchor.MiddleCenter, UiStyle.TextLight, bold: true);
            _notice.Visible = false;

            // E2-13: подпись режима постановки — над левым краем быстрой панели (справа — подсказка станка).
            const float mw = 430f, mh = 34f; // E2-16: + «R — повернуть», «из меню: есть N»
            _mode = new UiGroup(root, "PlaceMode", new Vector2(0.5f, 0f), new Vector2(-BottomBar.PanelW / 2f, barTop + 6f), new Vector2(mw, mh));
            UiRoot.Picture(_mode.Rt, "Back", 0f, 0f, mw, mh, UiStyle.Frame(UiStyle.Ink, UiStyle.Ink, radius: 6f));
            _modeText = UiRoot.Label(_mode.Rt, "Text", 0f, 0f, mw, mh, 18, TextAnchor.MiddleCenter, UiStyle.TextLight, bold: true);
            _mode.Visible = false;
        }

        /// <summary>Точка мира Unity → точка холста (скрипт стенда снимков).</summary>
        public Vector2 WorldToCanvas(Vector3 world) => _root.FromWorld(world);

        /// <summary>Середина ячейки сундука (40…69) на холсте (скрипт стенда снимков).</summary>
        public Vector2 ChestCellCenter(int cell) => _chest.Origin(_root.Size) + _chest.CellCenter(cell);

        /// <summary>Сообщение над панелью на 2,5 с (кровать назначена, отказ действия с объектом).</summary>
        public void Notify(string text)
        {
            _noticeMsg = text;
            _noticeUntil = Time.unscaledTime + MessageSeconds;
        }

        /// <summary>Открыть окно «сумка + изготовление» (F у верстака или костра; E2-9: разделы вместо вкладок станков).</summary>
        public void OpenCraft(CraftStation station, in UiCraftState craft)
        {
            SetChestShown(false);
            SetOpen(true);
            SetCraftOpen(true, craft);
        }

        // Сундук сервера открылся / закрылся: окно сундука и сумка — рядом; крафт закрывается.
        void SyncChestWindow(in UiBuildState build, in UiCraftState craft)
        {
            ChestCloseRequested = false;
            if (build.ChestOpen && !_chest.Visible)
            {
                SetCraftOpen(false, craft);
                SetOpen(true);
                SetChestShown(true);
            }
            else if (!build.ChestOpen && _chest.Visible)
            {
                SetChestShown(false);
            }
        }

        void SetChestShown(bool on)
        {
            if (_chest.Visible && !on)
            {
                _hand.Cancel();
            }

            _chest.Visible = on;
        }

        // Пользователь закрыл окна (Esc, Q, I, C) при открытом сундуке — сундук тоже закрыть и сказать серверу.
        void CloseChestByUser()
        {
            if (_chest.Visible)
            {
                SetChestShown(false);
                ChestCloseRequested = true;
            }
        }

        // Строка у курсора, подсказка F, сообщение — после ввода окон. E2-11: ленты построек у молота нет — постройка
        // берётся в руку из панели; у курсора — «Стена: ЛКМ — поставить (в руке N)» или причина.
        void ShowBuild(in UiInput input, in Inventory bag, int selected, in UiBuildState build, bool overUi, Vector2 size)
        {
            string? note = null;
            float integrity = -1f;
            bool bad = false;
            if ((build.Hammer || build.Placing) && !overUi && !_hand.Holding)
            {
                if (build.Dragging && build.Plan != null)
                {
                    BuildRuler p = build.Plan;
                    note = UiStrings.RulerNote(p.Kind, p.LengthCells, p.Width, p.Height, p.Need, p.Have);
                    bad = p.Need == 0 || p.Have < p.Need;
                }
                else if (build.HasGhost)
                {
                    BuildCheck c = build.Check;
                    bool repair = c.Action == BuildAction.Repair;
                    int inHand = build.FromMenu ? build.MenuAvailable : bag.CountIn(selected); // E2-16: из меню — готовые + изготовимые
                    string cost = repair ? CostText(c)
                        : build.RulerMode && BuildRuler.KindOf(c.Key) != RulerKind.Single ? UiStrings.RulerHint(BuildRuler.KindOf(c.Key), inHand)
                        : UiStrings.PlaceHint(inHand);
                    note = UiStrings.GhostNote(repair, c.Ok, c.Reason, c.Key, cost);
                    bad = !c.Ok;
                }

                if (build.HasIntegrity && build.MaxIntegrity > 0)
                {
                    integrity = build.Integrity / (float)build.MaxIntegrity;
                    string line = UiStrings.Integrity(build.Integrity, build.MaxIntegrity);
                    note = note == null ? line : line + "   " + note;
                }
            }

            _note.Show(note, integrity, bad, size, input.Pointer);

            bool showMode = build.Placing && !build.Hammer;
            _mode.Visible = showMode;
            if (showMode)
            {
                _modeText.text = UiStrings.PlaceModeLabel(build.RulerMode, build.Rotatable, build.FromMenu, build.MenuAvailable);
            }

            if (_noticeMsg != null && Time.unscaledTime > _noticeUntil)
            {
                _noticeMsg = null;
            }

            _notice.Visible = _noticeMsg != null;
            if (_noticeMsg != null)
            {
                _noticeText.text = _noticeMsg;
            }
        }

        static string CostText(in BuildCheck c)
        {
            string a = c.Count1 > 0 ? $"{c.Count1} {UiStrings.ItemName(ItemTable.OfMaterial(c.Material1)).ToLowerInvariant()}" : string.Empty;
            string b = c.Count2 > 0 && c.Material2 != c.Material1 ? $"{c.Count2} {UiStrings.ItemName(ItemTable.OfMaterial(c.Material2)).ToLowerInvariant()}" : string.Empty;
            return a.Length > 0 && b.Length > 0 ? a + " + " + b : a.Length > 0 ? a : b.Length > 0 ? b : "даром";
        }
    }
}
