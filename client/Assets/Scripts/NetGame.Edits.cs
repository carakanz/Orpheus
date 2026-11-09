#nullable enable
using System.Collections.Generic;
using System.Text;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;
using Material = Orpheus.Core.Sim.Material; // HUD сумки: Sim.Material, не UnityEngine.Material

namespace Orpheus.Client
{
    /// <summary>
    /// Правки тайлов в сетевой игре (0-028; docs/07, 3.7; `05` 1.3–1.5) — заглушка управления до палитры и инструментов:
    /// ЛКМ зажата — копать тайл под курсором (повёл курсор — копается следующий); ПКМ — поставить каменную стену под курсором.
    /// Предсказание и откат — в <see cref="ClientWorld"/>; здесь — откат на экране: красная вспышка на тайле 0,6 с и причина в HUD.
    /// Вспышка — квадрат из текстуры 1 × 1, созданной в коде (ассета нет — `_TEMP` не нужен).
    /// </summary>
    public sealed partial class NetGame
    {
        /// <summary>Вспышка отката держится 0,6 с.</summary>
        public const long FlashUs = 600_000;

        struct Flash
        {
            public TileCoord Tile;
            public long UntilUs;
            public SpriteRenderer Renderer;
        }

        readonly List<Flash> _flashes = new List<Flash>();

        // Зеркало своей сумки (1-002): наполняется сервером — «сумка» (тип 26) приходит при входе и при каждом изменении
        // (копание, подбор). Выбранная ячейка для руки берётся из своих кадров, пока подтверждение не пришло.
        Inventory _bag = Inventory.StartingKit();

        /// <summary>Сумка от сервера (этап 4): зеркало — серверная сумка целиком, включая выбранную ячейку.</summary>
        void OnInventory(InventoryMessage m) => _bag = m.Bag;

        /// <summary>Предмет в руках: ячейка последнего отправленного кадра, содержимое — из зеркала сумки (1-002).</summary>
        byte HeldItem => _bag.SlotAt(_pred?.SelectedSlot ?? _bag.Selected);

        /// <summary>Кусок HUD про сумку (1-002): что в руках и ненулевые материалы.</summary>
        string BagHud()
        {
            var sb = new StringBuilder("  в руках: ");
            sb.Append(ItemName(HeldItem));
            sb.Append($" [{_slot + 1}/{InputFrame.MaxSlots}]");
            for (int m = 0; m < (int)Material.Count; m++)
            {
                int n = _bag.CountAt((Material)m);
                if (n > 0)
                {
                    sb.Append($"  {MaterialName(m)} {n}");
                }
            }

            return sb.ToString();
        }

        static string ItemName(byte id) => id switch
        {
            Items.None => "пусто",
            Items.StonePickaxe => "кирка",
            Items.StoneAxe => "топор",
            Items.Hammer => "молот",
            Items.WoodenSword => "меч",
            Items.SimpleBow => "лук",
            Items.Torch => "факел",
            _ => $"№{id}",
        };

        static string MaterialName(int m) => m switch
        {
            (int)Material.Wood => "древесина",
            (int)Material.Stone => "камень",
            (int)Material.Resin => "смола",
            (int)Material.Fiber => "волокно",
            (int)Material.Copper => "медь",
            (int)Material.Arrows => "стрелы",
            _ => "факелы",
        };

        Sprite? _flashSprite;
        string _lastRefusal = string.Empty;
        long _lastRefusalUntilUs;
        bool _smokeEditDone;

        /// <summary>Откатов показано (HUD, smoke).</summary>
        public int Rollbacks { get; private set; }

        /// <summary>
        /// До отправки кадров: курсор и кнопки мыши → запросы правок; возвращает биты кнопок для кадров ввода
        /// (<see cref="InputButtons.Dig"/>, пока копание идёт).
        /// </summary>
        ushort UpdateEdits(long now)
        {
            NetClient c = _client!;
            if (_args.NetSmokeSeconds > 0)
            {
                SmokeEdit(now, c);
                return c.Digging ? InputButtons.Dig : (ushort)0;
            }

            Mouse? mouse = Mouse.current;
            if (mouse == null)
            {
                return 0;
            }

            Vector3 screen = mouse.position.ReadValue();
            Vector3 v = _g.Camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -GameBoot.CameraOffset.z));
            TileCoord tile = Torus.TileOf(_map, _origin!.FromUnity(v));
            if (mouse.rightButton.wasPressedThisFrame)
            {
                c.Place(tile, tile, ObjectId.StoneWall, _localPos);
            }

            if (!mouse.leftButton.isPressed)
            {
                c.StopDig(); // отпустил до конца — «копать: конец»; докопанное своим счётом ждёт ответа
                return c.Digging ? InputButtons.Dig : (ushort)0;
            }

            // Копает только кирка (1-002, `06` 2.3): в руках меч — ЛКМ не начинает копание и запрос не уходит
            // (у сервера тот же отказ молча). Досягаемость — «персонаж + предмет в руках».
            byte held = HeldItem;
            if (!c.Digging && Items.IsDiggingTool(held) &&
                EditRules.IsDiggable(_world!.GetObject(tile), c.DigTimes) && EditRules.InDigReach(_map, _localPos, tile, held))
            {
                c.StartDig(tile);
            }

            return c.Digging ? InputButtons.Dig : (ushort)0;
        }

        /// <summary>
        /// Net-smoke: один раз через 0,3 с — стена на свой тайл. Сервер отказывает «занято существом» (`05` 1.3), клиент откатывает
        /// призрак со вспышкой — путь отката проверяется в настоящей сборке (строка «[edit] откат» в логе).
        /// </summary>
        void SmokeEdit(long now, NetClient c)
        {
            if (_smokeEditDone || now - _inGameSinceUs < 300_000)
            {
                return;
            }

            _smokeEditDone = true;
            TileCoord own = Torus.TileOf(_map, _localPos);
            ushort id = c.Place(own, own, ObjectId.StoneWall, _localPos);
            Debug.Log($"[edit] smoke: стена на свой тайл {own.X},{own.Y}, запрос {id}");
        }

        void OnRolledBack(TileCoord tile, EditReply reason)
        {
            Rollbacks++;
            long now = NowUs();
            _lastRefusal = EditRules.Describe(reason);
            _lastRefusalUntilUs = now + FlashUs;
            Debug.Log($"[edit] откат: тайл {tile.X},{tile.Y} — «{_lastRefusal}»");
            if (_flashSprite == null)
            {
                var tex = new Texture2D(1, 1) { filterMode = FilterMode.Point };
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _flashSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }

            var go = new GameObject($"Flash {tile.X},{tile.Y}");
            go.transform.SetParent(_entityRoot.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _flashSprite;
            sr.color = new Color(1f, 0.1f, 0.1f, 0.6f);
            sr.sortingOrder = WorldView.ObjectOrder + 1;
            _flashes.Add(new Flash { Tile = tile, UntilUs = now + FlashUs, Renderer = sr });
        }

        /// <summary>Вспышки: место от якоря (после переезда начала), гаснут к концу 0,6 с, потом убираются.</summary>
        void RenderFlashes(long now)
        {
            for (int i = _flashes.Count - 1; i >= 0; i--)
            {
                Flash f = _flashes[i];
                long left = f.UntilUs - now;
                if (left <= 0)
                {
                    Object.Destroy(f.Renderer.gameObject);
                    _flashes.RemoveAt(i);
                    continue;
                }

                f.Renderer.transform.position = _origin!.ToUnity(CenterOf(f.Tile.X, f.Tile.Y));
                Color col = f.Renderer.color;
                col.a = 0.6f * left / FlashUs;
                f.Renderer.color = col;
            }
        }

        /// <summary>Строка HUD: последняя причина отказа, пока горит вспышка.</summary>
        string EditHud(long now) => now < _lastRefusalUntilUs ? $"  отказ: {_lastRefusal}" : string.Empty;
    }
}
