#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Orpheus.Save;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// E4-1: главное меню игры (процедурная uGUI в том же стиле, что и весь интерфейс - <see cref="UiRoot"/>/<see cref="UiButton"/>):
    /// четыре кнопки - «Новый мир», «Продолжить», «Открыть для друзей», «Войти по адресу». Меню не знает про сервер:
    /// папку мира, посев и адрес оно передаёт в <see cref="GameBoot"/> через <see cref="Start"/>, и игра стартует
    /// теми же путями, что и с командной строки (<c>--single</c> / <c>--connect</c>). Экран «Уведомление» - кнопка «В меню»
    /// после обрыва связи. История адресов - <c>connections.txt</c> в persistentDataPath (до 5, свежие вверху).
    /// Для витрины снимков: <see cref="IsMenuDemo"/> и <see cref="ShowDemo"/> (режимы menu, menu-new, menu-host, menu-join, menu-notice).
    /// </summary>
    public sealed class MenuUi : IDisposable
    {
        /// <summary>Что пользователь выбрал в меню; GameBoot превращает это в аргументы запуска.</summary>
        public enum Kind { NewWorld, Continue, FriendsNew, FriendsContinue, Join }

        public readonly struct Start
        {
            public readonly Kind Kind;
            public readonly long Seed;
            public readonly string? Address;

            public Start(Kind kind, long seed = 0, string? address = null)
            {
                Kind = kind;
                Seed = seed;
                Address = address;
            }
        }

        /// <summary>Адрес и UDP-порт «для друзей» (сеть карточки - 127.0.0.2; наружу не открываем никогда).</summary>
        public const string FriendsAddress = "127.0.0.2";
        public const int FriendsPort = 7788;

        const float BtnW = 460f, BtnH = 68f, Gap = 22f;
        const float CardW = 700f, CardH = 580f;

        readonly UiRoot _root;
        readonly Action<Start> _onStart;
        readonly Action _onMenu;

        readonly Image _shade, _card;
        readonly UiButton _btnNew, _btnContinue, _btnFriends, _btnJoin;
        readonly UiButton _btnCreate, _btnCreateBack, _btnFriendsNew, _btnFriendsCont, _btnFriendsBack;
        readonly UiButton _btnConnect, _btnJoinBack;
        readonly UiButton _btnNoticeMenu;
        readonly UiButton[] _historyButtons = new UiButton[5];
        readonly UiButton[] _all;

        readonly Text _title, _cardTitle, _seedLine, _worldLine, _addrLine, _promptLine, _noticeText, _noSavesNote, _friendsAddr;
        readonly HashSet<UiButton> _off = new HashSet<UiButton>();
        readonly List<string> _history;

        string _addr = string.Empty;
        long _seed;
        int _screen; // 0 - главный, 1 - новый мир, 2 - друзья, 3 - войти, 4 - уведомление
        bool _enterPressed;
        bool _noticeShown;

        public MenuUi(Camera camera, Action<Start> onStart, Action? onMenu = null)
        {
            _root = new UiRoot(camera);
            _onStart = onStart;
            _onMenu = onMenu ?? delegate { };
            _history = LoadHistory();

            Transform rt = _root.Transform;
            var bg = UiRoot.Picture(rt, "MenuBg", 0f, 0f, _root.Size.x, _root.Size.y,
                UiStyle.Frame(new Color32(14, 12, 24, 255), new Color32(14, 12, 24, 255), thick: 0f, radius: 0f));
            Stretch(bg.rectTransform);
            _shade = UiRoot.Picture(rt, "MenuShade", 0f, 0f, _root.Size.x, _root.Size.y,
                UiStyle.Frame(new Color32(6, 5, 12, 150), new Color32(6, 5, 12, 150), thick: 0f, radius: 0f));
            Stretch(_shade.rectTransform);
            _shade.enabled = false;
            _card = UiRoot.Picture(rt, "MenuCard", 0f, 0f, CardW, CardH, UiStyle.Frame(UiStyle.Parchment, UiStyle.Ink, thick: 4f, radius: 10f));
            _card.enabled = false;

            _title = UiRoot.Label(rt, "MenuTitle", 0f, 0f, 900f, 100f, 72, TextAnchor.MiddleCenter, UiStyle.Gold, outline: true, bold: true);
            _title.text = UiStrings.MenuTitle;
            _cardTitle = UiRoot.Label(rt, "MenuCardTitle", 0f, 0f, CardW - 60f, 52f, 34, TextAnchor.MiddleCenter, UiStyle.TextDark, bold: true);

            _btnNew = Button(UiStrings.MenuNewWorld, 26);
            _btnContinue = Button(UiStrings.MenuContinue, 26);
            _btnFriends = Button(UiStrings.MenuFriends, 26);
            _btnJoin = Button(UiStrings.MenuJoin, 26);

            // Карточка «Новый мир»: название будущей папки + посев + «Создать».
            _worldLine = CardText("MenuWorldLine", 26);
            _seedLine = CardText("MenuSeedLine", 22);
            _btnCreate = Button(UiStrings.MenuCreate, 26);
            _btnCreateBack = Button(UiStrings.MenuBack, 22);

            // Карточка «Открыть для друзей»: адрес для гостей + «Новый мир» / «Продолжить».
            _friendsAddr = CardText("MenuFriendsAddr", 30);
            _btnFriendsNew = Button(UiStrings.MenuNewWorld, 24);
            _btnFriendsCont = Button(UiStrings.MenuContinue, 24);
            _btnFriendsBack = Button(UiStrings.MenuBack, 22);

            // Карточка «Войти по адресу»: ввод host:порт, история, «Подключиться».
            _promptLine = CardText("MenuJoinPrompt", 22);
            _promptLine.text = UiStrings.MenuJoinPrompt;
            _addrLine = CardText("MenuJoinAddr", 30);
            _btnConnect = Button(UiStrings.MenuConnect, 26);
            _btnJoinBack = Button(UiStrings.MenuBack, 22);
            for (int i = 0; i < _historyButtons.Length; i++)
            {
                _historyButtons[i] = Button(string.Empty, 20);
                SetEnabled(_historyButtons[i], false);
            }

            // Карточка «Уведомление»: текст обрыва + «В меню».
            _noticeText = CardText("MenuNoticeText", 26);
            _btnNoticeMenu = Button(UiStrings.MenuToMenu, 26);

            _noSavesNote = CardText("MenuNoSaves", 18);
            _noSavesNote.text = UiStrings.MenuNoSaves;

            _all = new[] { _btnNew, _btnContinue, _btnFriends, _btnJoin, _btnCreate, _btnCreateBack,
                           _btnFriendsNew, _btnFriendsCont, _btnFriendsBack, _btnConnect, _btnJoinBack, _btnNoticeMenu };
            if (Keyboard.current != null)
            {
                Keyboard.current.onTextInput += OnTextInput; // адрес печатаем событием символов (screen 3 фильтрует)
            }

            ShowMain();
        }

        UiButton Button(string text, int size) => new UiButton(_root.Transform, 0f, 0f, BtnW, BtnH, text, size, glass: true);

        Text CardText(string name, int size) =>
            UiRoot.Label(_root.Transform, name, 0f, 0f, CardW - 60f, 44f, size, TextAnchor.MiddleCenter, UiStyle.TextDark, outline: false);

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        // ---------- миры и история ----------

        /// <summary>Корень папок миров - тот же, что у сервера по умолчанию (LocalServer/ThreadServer).</summary>
        public static string WorldsRoot => Path.Combine(Application.persistentDataPath, "Worlds");

        public static string WorldName(int n) => "world-" + n.ToString(CultureInfo.InvariantCulture);

        public static string NextWorldName()
        {
            int n = 1;
            while (Directory.Exists(Path.Combine(WorldsRoot, WorldName(n))))
            {
                n++;
            }

            return WorldName(n);
        }

        /// <summary>Самая свежая папка мира с <c>world.base</c> (куда сервер пишет сохранение); null - сохранений нет.</summary>
        public static string? LatestWorldName()
        {
            try
            {
                if (!Directory.Exists(WorldsRoot))
                {
                    return null;
                }

                string? best = null;
                DateTime bestT = DateTime.MinValue;
                foreach (string dir in Directory.GetDirectories(WorldsRoot, "world-*"))
                {
                    if (!File.Exists(Path.Combine(dir, WorldFolder.BaseFileName)))
                    {
                        continue;
                    }

                    DateTime t = Directory.GetLastWriteTimeUtc(dir);
                    if (t > bestT)
                    {
                        bestT = t;
                        best = Path.GetFileName(dir);
                    }
                }

                return best;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static bool HasSavedWorld() => LatestWorldName() != null;

        static long FreshSeed() => (DateTime.UtcNow.Ticks ^ ((long)Environment.TickCount << 17)) & long.MaxValue;

        static string HistoryFile => Path.Combine(Application.persistentDataPath, "connections.txt");

        static List<string> LoadHistory()
        {
            var list = new List<string>();
            try
            {
                foreach (string line in File.ReadAllLines(HistoryFile))
                {
                    string s = line.Trim();
                    if (s.Length > 0 && !list.Contains(s))
                    {
                        list.Add(s);
                    }

                    if (list.Count >= 5)
                    {
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // нет файла - нет истории
            }

            return list;
        }

        void AddHistory(string addr)
        {
            _history.Remove(addr);
            _history.Insert(0, addr);
            while (_history.Count > 5)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            try
            {
                File.WriteAllLines(HistoryFile, _history);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // не смогли сохранить - история только на этот запуск
            }
        }

        static bool IsAddrChar(char ch) =>
            char.IsLetterOrDigit(ch) || ch == '.' || ch == ':' || ch == '-' || ch == '_';

        public static bool IsMenuDemo(string? mode) => mode != null && mode.StartsWith("menu", StringComparison.Ordinal);

        // ---------- экраны ----------

        void Show(int screen)
        {
            _screen = screen;
            _shade.enabled = screen != 0;
            _card.enabled = screen != 0;
            _cardTitle.gameObject.SetActive(screen != 0);
            _cardTitle.text = screen switch
            {
                1 => UiStrings.MenuNewWorld,
                2 => UiStrings.MenuFriends,
                3 => UiStrings.MenuJoin,
                4 => UiStrings.MenuNoticeTitle,
                _ => string.Empty,
            };
            if (screen == 2)
            {
                _friendsAddr.text = UiStrings.MenuFriendsAddr(FriendsAddress, FriendsPort);
            }

            foreach (UiButton b in _all)
            {
                b.Visible = false;
            }

            // История — не в _all: прячем всегда, на экране 3 включаются заново (иначе висят пустыми в углу).
            foreach (UiButton b in _historyButtons)
            {
                b.Visible = false;
            }

            _title.gameObject.SetActive(screen == 0);
            _worldLine.gameObject.SetActive(screen == 1);
            _seedLine.gameObject.SetActive(screen == 1);
            _friendsAddr.gameObject.SetActive(screen == 2);
            _promptLine.gameObject.SetActive(screen == 3);
            _addrLine.gameObject.SetActive(screen == 3);
            _noticeText.gameObject.SetActive(screen == 4);
            _noSavesNote.gameObject.SetActive(screen == 0 && !HasSavedWorld());
            switch (screen)
            {
                case 0:
                    _btnNew.Visible = _btnContinue.Visible = _btnFriends.Visible = _btnJoin.Visible = true;
                    break;
                case 1:
                    _btnCreate.Visible = _btnCreateBack.Visible = true;
                    break;
                case 2:
                    _btnFriendsNew.Visible = _btnFriendsCont.Visible = _btnFriendsBack.Visible = true;
                    break;
                case 3:
                    _btnConnect.Visible = _btnJoinBack.Visible = true;
                    for (int i = 0; i < _historyButtons.Length; i++)
                    {
                        _historyButtons[i].Visible = i < _history.Count;
                        if (i < _history.Count)
                        {
                            _historyButtons[i].Label.text = _history[i];
                            SetEnabled(_historyButtons[i], true);
                        }
                    }

                    break;
                case 4:
                    _btnNoticeMenu.Visible = true;
                    break;
            }
        }

        void ShowMain() => Show(0);
        void ShowFriends() => Show(2);

        void ShowNew()
        {
            _seed = FreshSeed();
            _worldLine.text = UiStrings.MenuWorld(NextWorldName());
            _seedLine.text = UiStrings.MenuSeed(_seed);
            Show(1);
        }

        void ShowJoin()
        {
            _addr = string.Empty;
            _addrLine.text = UiStrings.MenuJoinAddr(_addr);
            Show(3);
        }

        /// <summary>Экран «Уведомление» (обрыв связи, отказ сервера): текст + «В меню».</summary>
        public void ShowNotice(string text)
        {
            if (!_noticeShown)
            {
                _noticeShown = true;
                _noticeText.text = text;
            }

            Show(4);
        }

        /// <summary>Витрина снимков (--ui-demo menu, menu-new, menu-host, menu-join, menu-notice).</summary>
        public void ShowDemo(string mode)
        {
            switch (mode)
            {
                case "menu-new":
                    ShowNew();
                    break;
                case "menu-host":
                    _seed = 0xE4100001;
                    Show(2);
                    break;
                case "menu-join":
                    _addr = "193.19.1.11:7788";
                    _addrLine.text = UiStrings.MenuJoinAddr(_addr);
                    Show(3);
                    break;
                case "menu-notice":
                    ShowNotice("Не удалось подключиться к 193.19.1.11:7788: сервер не отвечает (10 с)");
                    break;
                default:
                    ShowMain();
                    break;
            }
        }

        void SetEnabled(UiButton b, bool on)
        {
            if (on)
            {
                _off.Remove(b);
            }
            else
            {
                _off.Add(b);
            }

            b.Set(false, false, on);
        }

        bool Clicked(UiButton b)
        {
            if (!b.Visible || _off.Contains(b))
            {
                return false;
            }

            Mouse? m = Mouse.current;
            return m != null && m.leftButton.wasPressedThisFrame && b.Contains(_root.FromScreen(m.position.ReadValue()));
        }

        /// <summary>Кадры меню: раскладка под текущий canvas + опрос мыши и клавиатуры.</summary>
        public void Frame()
        {
            Layout();
            _enterPressed = false;
            switch (_screen)
            {
                case 0:
                    SetEnabled(_btnContinue, HasSavedWorld());
                    if (Clicked(_btnNew))
                    {
                        ShowNew();
                    }
                    else if (Clicked(_btnContinue))
                    {
                        _onStart(new Start(Kind.Continue));
                    }
                    else if (Clicked(_btnFriends))
                    {
                        ShowFriends();
                    }
                    else if (Clicked(_btnJoin))
                    {
                        ShowJoin();
                    }

                    break;
                case 1:
                    if (Clicked(_btnCreate))
                    {
                        _onStart(new Start(Kind.NewWorld, _seed));
                    }
                    else if (Clicked(_btnCreateBack))
                    {
                        ShowMain();
                    }

                    break;
                case 2:
                    SetEnabled(_btnFriendsCont, HasSavedWorld());
                    if (Clicked(_btnFriendsNew))
                    {
                        _seed = FreshSeed();
                        _onStart(new Start(Kind.FriendsNew, _seed));
                    }
                    else if (Clicked(_btnFriendsCont))
                    {
                        _onStart(new Start(Kind.FriendsContinue));
                    }
                    else if (Clicked(_btnFriendsBack))
                    {
                        ShowMain();
                    }

                    break;
                case 3:
                    ReadAddressInput();
                    if ((Clicked(_btnConnect) || _enterPressed) && _addr.Length > 0)
                    {
                        AddHistory(_addr);
                        _onStart(new Start(Kind.Join, 0, _addr));
                    }
                    else if (Clicked(_btnJoinBack))
                    {
                        ShowMain();
                    }
                    else
                    {
                        for (int i = 0; i < _historyButtons.Length; i++)
                        {
                            if (i < _history.Count && Clicked(_historyButtons[i]))
                            {
                                _addr = _history[i];
                                break;
                            }
                        }
                    }

                    break;
                case 4:
                    if (Clicked(_btnNoticeMenu))
                    {
                        _onMenu();
                    }

                    break;
            }

            UpdateHover();
        }

        void ReadAddressInput()
        {
            Keyboard? kb = Keyboard.current;
            if (kb == null)
            {
                return;
            }

            if (kb.backspaceKey.wasPressedThisFrame && _addr.Length > 0)
            {
                _addr = _addr.Substring(0, _addr.Length - 1);
            }

            _enterPressed = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
            _addrLine.text = UiStrings.MenuJoinAddr(_addr);
        }

        /// <summary>Печать адреса: новый InputSystem отдаёт символы событием onTextInput (у Keyboard нет inputString).</summary>
        void OnTextInput(char ch)
        {
            if (_screen == 3 && IsAddrChar(ch) && _addr.Length < 48)
            {
                _addr += ch;
            }
        }

        void UpdateHover()
        {
            Mouse? m = Mouse.current;
            Vector2 p = m != null ? _root.FromScreen(m.position.ReadValue()) : new Vector2(-100000f, -100000f);
            foreach (UiButton b in _all)
            {
                if (b.Visible)
                {
                    b.Set(b.Contains(p), false, !_off.Contains(b));
                }
            }

            for (int i = 0; i < _historyButtons.Length; i++)
            {
                UiButton b = _historyButtons[i];
                if (b.Visible)
                {
                    b.Set(b.Contains(p), false, !_off.Contains(b));
                }
            }
        }

        void Layout()
        {
            Vector2 s = _root.Size;
            float cx = (s.x - BtnW) / 2f;
            _title.rectTransform.anchoredPosition = new Vector2((s.x - 900f) / 2f, s.y * 0.78f);
            float y = s.y * 0.60f;
            _btnNew.Rt.anchoredPosition = new Vector2(cx, y);
            _btnContinue.Rt.anchoredPosition = new Vector2(cx, y -= BtnH + Gap);
            _btnFriends.Rt.anchoredPosition = new Vector2(cx, y -= BtnH + Gap);
            _btnJoin.Rt.anchoredPosition = new Vector2(cx, y -= BtnH + Gap);
            _noSavesNote.rectTransform.anchoredPosition = new Vector2(cx, y - BtnH - 14f);

            float cardX = (s.x - CardW) / 2f, cardY = (s.y - CardH) / 2f;
            float midX = (s.x - (CardW - 60f)) / 2f;
            _card.rectTransform.anchoredPosition = new Vector2(cardX, cardY);
            if (_screen != 0)
            {
                _cardTitle.rectTransform.anchoredPosition = new Vector2(midX, cardY + CardH - 76f);
            }

            switch (_screen)
            {
                case 1:
                    _worldLine.rectTransform.anchoredPosition = new Vector2(midX, cardY + CardH - 140f);
                    _seedLine.rectTransform.anchoredPosition = new Vector2(midX, cardY + CardH - 196f);
                    _btnCreate.Rt.anchoredPosition = new Vector2(cx, cardY + 250f);
                    _btnCreateBack.Rt.anchoredPosition = new Vector2(cx, cardY + 120f);
                    break;
                case 2:
                    _friendsAddr.rectTransform.anchoredPosition = new Vector2(midX, cardY + CardH - 150f);
                    _btnFriendsNew.Rt.anchoredPosition = new Vector2(cx, cardY + 330f);
                    _btnFriendsCont.Rt.anchoredPosition = new Vector2(cx, cardY + 220f);
                    _btnFriendsBack.Rt.anchoredPosition = new Vector2(cx, cardY + 110f);
                    break;
                case 3:
                    _promptLine.rectTransform.anchoredPosition = new Vector2(midX, cardY + CardH - 130f);
                    _addrLine.rectTransform.anchoredPosition = new Vector2(midX, cardY + CardH - 186f);
                    _btnConnect.Rt.anchoredPosition = new Vector2(cx, cardY + 150f);
                    _btnJoinBack.Rt.anchoredPosition = new Vector2(cx, cardY + 60f);
                    float hy = cardY + CardH - 250f;
                    for (int i = 0; i < _historyButtons.Length; i++)
                    {
                        _historyButtons[i].Rt.anchoredPosition = new Vector2(cx, hy - i * 54f);
                    }

                    break;
                case 4:
                    _noticeText.rectTransform.anchoredPosition = new Vector2(midX, cardY + CardH - 170f);
                    _btnNoticeMenu.Rt.anchoredPosition = new Vector2(cx, cardY + 150f);
                    break;
            }
        }

        public void Dispose()
        {
            if (Keyboard.current != null)
            {
                Keyboard.current.onTextInput -= OnTextInput;
            }

            UnityEngine.Object.Destroy(_root.Transform.gameObject);
        }
    }
}
