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

        /// <summary>
        /// Предмет в руках в кадре с этой ячейкой: сервер применяет ячейку в том же кадре, что кнопки (1-002),
        /// и с ней же решает удар (melee-гейт такта, 1-004) — предсказание берёт предмет по ячейке кадра.
        /// </summary>
        byte HeldItemAt(byte slot) => _bag.SlotAt(slot);

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

        // 1-022, шаг 4: стена на свободный тайл в net-smoke — предсказание видно в Tilemap в тот же кадр, сервер принимает.
        TileCoord _smokeWallTile;
        int _smokeWallTry;
        int _smokeWallFrameOk;
        int _smokeWallRollbacks;
        long _smokeWallConfirmed0;
        bool _smokeWallConfirmed;
        bool _smokeWallDone;
        long _smokeWallNextUs;

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
                // 1-004: в net-smoke удар — скриптом (окно удержания ЛКМ в беге), бит как на настоящей мыши.
                return (ushort)((c.Digging ? InputButtons.Dig : (ushort)0) | (ScriptedSmokeAttack(now) ? InputButtons.Attack : (ushort)0));
            }

            Mouse? mouse = Mouse.current;
            if (mouse == null)
            {
                return 0;
            }

            Vector3 screen = mouse.position.ReadValue();
            Vector3 v = _g.Camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -GameBoot.CameraOffset.z));
            TileCoord tile = Torus.TileOf(_map, _origin!.FromUnity(v));

            // 1-012: F — закрыть ближайшую открытую дверь (передний фронт здесь, запрос TileOp.CloseDoor — протокол 19;
            // радиус, проём и замок проверяет сервер, предсказание и откат — общие с правками).
            if (Keyboard.current?.fKey.wasPressedThisFrame == true)
            {
                CloseNearestDoor(c);
            }

            if (mouse.rightButton.wasPressedThisFrame)
            {
                c.Place(tile, tile, ObjectId.StoneWall, _localPos);
            }

            byte held = HeldItem;
            // 1-004 (`02` 1.2 «удержание ЛКМ — повтор»): с ближним оружием в руках ЛКМ — удар,
            // бит стоит на всех кадрах удержания (ядро берёт нажатие битом кадра).
            ushort attack = Items.KindOf(held) == Items.KindMelee && mouse.leftButton.isPressed ? InputButtons.Attack : (ushort)0;
            if (!mouse.leftButton.isPressed)
            {
                c.StopDig(); // отпустил до конца — «копать: конец»; докопанное своим счётом ждёт ответа
                return (ushort)((c.Digging ? InputButtons.Dig : (ushort)0) | attack);
            }

            // 1-011: «что копать и чем» решает ядро — CheckDig (кирка/топор/молот, ярус, ямы, построенные полы и
            // обломки молотом). Не подходит — ЛКМ не начинает копание и запрос не уходит (у сервера тот же отказ молча).
            if (!c.Digging && EditRules.CheckDig(_world!, _localPos, tile, c.DigTimes, held) == EditReply.Accepted)
            {
                c.StartDig(tile);
            }

            return (ushort)((c.Digging ? InputButtons.Dig : (ushort)0) | attack);
        }

        /// <summary>
        /// F (1-012): закрыть ближайшую открытую дверь в радиусе 1 тайл от своего персонажа (порядок обхода
        /// восток→юг→запад→север… — как порядок <c>NeighborDeltas</c> в ядре). Свой тайл — отказ: сервер
        /// проверяет сам. Шлёт <see cref="NetClient.CloseDoor"/>: предсказание + запрос (протокол 19).
        /// </summary>
        void CloseNearestDoor(NetClient c)
        {
            WorldModel w = _world!;
            TileCoord own = Torus.TileOf(_map, _localPos);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue; // в проёме F — отказ
                    }

                    TileCoord t = Torus.Wrap(_map, new TileCoord(own.X + dx, own.Y + dy));
                    if (w.GetObject(t) == ObjectId.Door && w.TryGetBuild(t, out BuildRecord rec) && rec.IsDoorOpen)
                    {
                        ushort id = c.CloseDoor(t);
                        Debug.Log($"[door] F: закрываю дверь {t.X},{t.Y}, запрос {id}");
                        return;
                    }
                }
            }
        }

        // ---------- 1-012: host-smoke проход через дверь (--smoke-door → сервер --debug-door) ----------

        TileCoord _smokeDoorTile;
        bool _smokeDoorFound;
        bool _smokeDoorOpenSeen;
        bool _smokeDoorClosedSeen;
        bool _smokeDoorPassed;

        /// <summary>
        /// Net-smoke каждый кадр (1-012): дверь — 4 тайла к востоку от фактического появления (сервер ставит её
        /// <c>--debug-door</c> закрытой). Скриптовый бег на восток: автооткрытие шагом → проход → автозакрытие
        /// через 30 тактов. Фиксируем все три события; проверка — в <c>NetSmokeCheck</c>.
        /// </summary>
        void SmokeDoorFrame(WorldPos confirmed)
        {
            if (!_smokeDoorFound)
            {
                TileCoord spawn = Torus.TileOf(_map, confirmed);
                _smokeDoorTile = Torus.Wrap(_map, new TileCoord(spawn.X + 4, spawn.Y));
                _smokeDoorFound = true;
                Debug.Log($"[door] smoke: ожидаю дверь на {_smokeDoorTile.X},{_smokeDoorTile.Y} (4 тайла к востоку от появления)");
            }

            WorldModel w = _world!;
            if (w.GetObject(_smokeDoorTile) != ObjectId.Door || !w.TryGetBuild(_smokeDoorTile, out BuildRecord rec))
            {
                return; // чанк с дверью ещё не пришёл
            }

            if (rec.IsDoorOpen)
            {
                if (!_smokeDoorOpenSeen)
                {
                    _smokeDoorOpenSeen = true;
                    Debug.Log("[door] smoke: дверь открыта — автооткрытие шагом сработало");
                }
            }
            else if (_smokeDoorOpenSeen && !_smokeDoorClosedSeen)
            {
                _smokeDoorClosedSeen = true;
                Debug.Log("[door] smoke: дверь закрылась — автозакрытие через 30 тактов сработало");
            }

            TileCoord me = Torus.TileOf(_map, confirmed);
            Int2 d = Torus.Delta(_map, _smokeDoorTile, me);
            if (d.X >= 1 && d.Y == 0)
            {
                _smokeDoorPassed = true; // персонаж прошёл тайл двери на восток
            }
        }

        /// <summary>
        /// Net-smoke: через 0,3 с — ковёр на свой тайл. Тайл занят существом (`05` 1.3), клиент предсказывает призрак,
        /// сервер отказывает — клиент откатывает со вспышкой; путь отката проверяется строкой «[edit] откат» в логе.
        /// Через 1,0 с — ковёр на свободный тайл (1-022, шаг 4): бесплатный (нет записи в <see cref="BuildDefs"/>),
        /// поэтому сумка стартового набора не нужна; молот в кадр ввода ставит <see cref="SmokeMeleeSlot"/>.
        /// Предсказание видно в Tilemap в тот же кадр, ответ сервера «принято» подтверждает правку;
        /// отказ — повтор на другом тайле, до 3 проб.
        /// </summary>
        void SmokeEdit(long now, NetClient c)
        {
            long sinceUs = now - _inGameSinceUs;
            if (!_smokeEditDone && sinceUs >= 300_000)
            {
                _smokeEditDone = true;
                TileCoord own = Torus.TileOf(_map, _localPos);
                ushort id = c.Place(own, own, ObjectId.Carpet, _localPos);
                Debug.Log($"[edit] smoke: ковёр на свой тайл {own.X},{own.Y}, запрос {id}");
            }

            if (!_smokeWallDone && _smokeWallTry < 3 && sinceUs >= 1_000_000 && now >= _smokeWallNextUs)
            {
                PlaceSmokeWall(c, now);
            }

            ClientWorld cw = c.World!;
            if (_smokeWallTry > 0 && !_smokeWallConfirmed && cw.PredictionsConfirmed > _smokeWallConfirmed0)
            {
                _smokeWallConfirmed = true;
                _smokeWallDone = true;
                bool inChunk = SmokeCarpetInChunk();
                Debug.Log($"[edit] smoke (1-022): правка принята сервером (проба {_smokeWallTry}), тайл {_smokeWallTile.X},{_smokeWallTile.Y} " +
                          $"в чанке = ковёр: {(inChunk ? "да" : "НЕТ")}");
                if (!inChunk)
                {
                    Debug.LogError($"[edit] FAIL net-smoke (1-022): после «принято» тайл {_smokeWallTile.X},{_smokeWallTile.Y} в чанке не ковёр");
                }
            }
        }

        /// <summary>
        /// 1-022, шаг 4: ковёр на свободный тайл. Предсказание ложится в чанк и в Tilemap синхронно
        /// (<c>ClientWorld.Predict → ChunkChanged → WorldView.ShowChunk</c>) — клетку Tilemap читаем сразу после вызова.
        /// </summary>
        void PlaceSmokeWall(NetClient c, long now)
        {
            _smokeWallTry++;
            if (!TryFindFreePlaceTile(out TileCoord t))
            {
                Debug.LogError($"[edit] FAIL net-smoke (1-022): нет свободного тайла в радиусе {EditRules.PlaceRadiusTiles} (проба {_smokeWallTry})");
                _smokeWallNextUs = now + 700_000;
                return;
            }

            _smokeWallTile = t;
            _smokeWallConfirmed0 = c.World!.PredictionsConfirmed;
            ushort id = c.Place(t, t, ObjectId.Carpet, _localPos);
            bool visible = TilemapShowsCarpet(t);
            Debug.Log($"[edit] smoke (1-022): ковёр на свободный тайл {t.X},{t.Y}, запрос {id}, проба {_smokeWallTry}, " +
                      $"Tilemap в тот же кадр: {(visible ? "да" : "НЕТ")}");
            if (visible)
            {
                _smokeWallFrameOk++;
            }
            else
            {
                Debug.LogError($"[edit] FAIL net-smoke (1-022): предсказание правки {t.X},{t.Y} не видно в Tilemap в тот же кадр");
            }

            _smokeWallNextUs = now + 1_200_000; // ждём ответ сервера; отказ сдвинет срок раньше (OnRolledBack)
        }

        /// <summary>
        /// Свободный тайл для постановки: 6 тайлов по оси максимума от персонажа (<see cref="EditRules.PlaceRadiusTiles"/>),
        /// тайл пуст, пол не вода/лава, проходим, чанк в окне, чужих существ ближе 3 тайлов нет (<c>`05` 1.3</c>).
        /// Обход детерминированный: верхний ряд кольца, далее построчно.
        /// </summary>
        bool TryFindFreePlaceTile(out TileCoord tile)
        {
            TileCoord c0 = Torus.TileOf(_map, _localPos);
            const int r = EditRules.PlaceRadiusTiles;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != r)
                    {
                        continue;
                    }

                    TileCoord t = Torus.Wrap(_map, new TileCoord(c0.X + dx, c0.Y + dy));
                    Chunk? chunk = _world!.FindChunk(Torus.ChunkOf(_map, t));
                    if (chunk == null)
                    {
                        continue;
                    }

                    int index = Chunk.IndexOf(t.X, t.Y);
                    if (chunk.GetObject(index) != ObjectId.Empty)
                    {
                        continue;
                    }

                    byte floor = chunk.GetFloor(index);
                    if (floor == FloorId.Water || floor == FloorId.Lava || _netRules!.IsBlocked(_map, t) || RemoteNear(t, 3))
                    {
                        continue;
                    }

                    tile = t;
                    return true;
                }
            }

            tile = default;
            return false;
        }

        /// <summary>Чужой персонаж в пределах <paramref name="tiles"/> тайлов по оси максимума (по узлам сущностей на экране).</summary>
        bool RemoteNear(TileCoord t, int tiles)
        {
            foreach (KeyValuePair<int, GameObject> kv in _ents)
            {
                if (!kv.Value.activeSelf)
                {
                    continue;
                }

                TileCoord at = Torus.TileOf(_map, _origin!.FromUnity(kv.Value.transform.position));
                Int2 d = Torus.Delta(_map, at, t);
                if (System.Math.Max(System.Math.Abs(d.X), System.Math.Abs(d.Y)) <= tiles)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Клетка Tilemap под тайлом — та же стена, что положило предсказание (чтение в том же кадре).</summary>
        bool TilemapShowsCarpet(TileCoord t)
        {
            ChunkCoord c = Torus.ChunkOf(_map, t);
            if (_view == null || !_view.TryGetNode(c, out WorldView.ChunkNode node))
            {
                return false;
            }

            return node.Objects.GetTile(WorldView.CellOf(t.X & (Torus.ChunkSize - 1), t.Y & (Torus.ChunkSize - 1)))
                == _view.ObjectTile(ObjectId.Carpet);
        }

        /// <summary>Тайл в чанке ядра клиента — ковёр (проверка после ответа «принято»).</summary>
        bool SmokeCarpetInChunk()
        {
            Chunk? chunk = _world!.FindChunk(Torus.ChunkOf(_map, _smokeWallTile));
            return chunk != null &&
                   ObjectId.IdOf(chunk.GetObject(Chunk.IndexOf(_smokeWallTile.X, _smokeWallTile.Y))) == ObjectId.Carpet;
        }

        void OnRolledBack(TileCoord tile, EditReply reason)
        {
            Rollbacks++;
            long now = NowUs();
            _lastRefusal = EditRules.Describe(reason);
            _lastRefusalUntilUs = now + FlashUs;
            Debug.Log($"[edit] откат: тайл {tile.X},{tile.Y} — «{_lastRefusal}»");
            if (_smokeWallTry > 0 && !_smokeWallConfirmed && !_smokeWallDone && tile == _smokeWallTile)
            {
                // 1-022, шаг 4: проба со свободным тайлом не удалась (занято существом и т. п.) — повтор на другом тайле.
                _smokeWallRollbacks++;
                _smokeWallNextUs = now + 700_000;
                Debug.Log($"[edit] smoke (1-022): проба {_smokeWallTry} отклонена — повтор на другом тайле");
            }

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
