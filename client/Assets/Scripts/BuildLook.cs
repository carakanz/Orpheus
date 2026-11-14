#nullable enable
using System.Collections.Generic;
using Orpheus.Core.World;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Orpheus.Client
{
    /// <summary>
    /// Картинки построек (E2-5): значок в ленте режима стройки, призрак под курсором, плитки Tilemap построенных полов,
    /// стен, забора и двери. Источник — набор объектов (<see cref="ObjectAtlas.Set"/>: <c>TEMP_floor_*</c>,
    /// <c>TEMP_obj_wall_*</c>, <c>obj_fence</c>, <c>obj_door*</c>, <c>obj_chest</c>, <c>obj_bed</c>, станки и факел — скрипт
    /// <c>tools/make_item_icons.py</c>). Плитки и призрак — копии спрайта с опорой по центру (в наборе опора внизу).
    /// Вид — таблица «ключ постройки → имя картинки» (<see cref="NameOf"/>), не ветки по коду.
    /// </summary>
    public static class BuildLook
    {
        static readonly Dictionary<ushort, string> Names = new Dictionary<ushort, string>
        {
            { BuildDefs.WoodFloorKey, "floor_wood" },
            { BuildDefs.StoneFloorKey, "floor_stone" },
            { BuildDefs.PlatformKey, "floor_platform" },
            { ObjectId.WoodWall, "obj_wall_wood" },
            { ObjectId.StoneWall, "obj_wall_stone" },
            { ObjectId.WoodFence, "obj_fence" },
            { ObjectId.Door, "obj_door" },
            { ObjectId.Torch, "obj_torch" },
            { ObjectId.Campfire, "obj_campfire" },
            { ObjectId.Workbench, "obj_workbench" },
            { ObjectId.Chest, "obj_chest" },
            { ObjectId.Bed, "obj_bed" },
        };

        /// <summary>Открытая дверь — своя плитка.</summary>
        public const string DoorOpen = "obj_door_open";

        static readonly Dictionary<string, Sprite?> _icons = new Dictionary<string, Sprite?>();
        static readonly Dictionary<string, Sprite?> _centered = new Dictionary<string, Sprite?>();
        static readonly Dictionary<string, TileBase?> _tiles = new Dictionary<string, TileBase?>();

        /// <summary>Имя картинки постройки по ключу (<see cref="BuildDefs.Palette"/>); нет — null.</summary>
        public static string? NameOf(ushort key) => Names.TryGetValue(key, out string? n) ? n : null;

        /// <summary>Значок для интерфейса (без пурпурного уголка, как у значков предметов).</summary>
        public static Sprite? Icon(ushort key)
        {
            string? name = NameOf(key);
            if (name == null)
            {
                return null;
            }

            if (!_icons.TryGetValue(name, out Sprite? s))
            {
                Sprite? found = PileView.Find(name);
                s = found != null ? PileView.Unmarked(found) : null;
                _icons[name] = s;
            }

            return s;
        }

        /// <summary>Картинка с опорой по центру (призрак, плитки); нет картинки — null.</summary>
        public static Sprite? Centered(string name)
        {
            if (_centered.TryGetValue(name, out Sprite? s))
            {
                return s;
            }

            Sprite? src = PileView.Find(name);
            s = src != null
                ? Sprite.Create(src.texture, src.rect, new Vector2(0.5f, 0.5f * Mathf.Min(1f, src.rect.width / src.rect.height)), src.pixelsPerUnit)
                : null;
            _centered[name] = s;
            return s;
        }

        /// <summary>Плитка Tilemap по имени картинки (полы, стены, забор, дверь); нет картинки — null (рисуется по-старому).</summary>
        public static TileBase? Tile(string name)
        {
            if (_tiles.TryGetValue(name, out TileBase? t))
            {
                return t;
            }

            Sprite? s = Centered(name);
            Tile? tile = null;
            if (s != null)
            {
                tile = ScriptableObject.CreateInstance<Tile>();
                tile.sprite = s;
                tile.name = name;
            }

            _tiles[name] = tile;
            return tile;
        }

        /// <summary>Плитка постройки по ключу (пол — ключ пола, объект — id).</summary>
        public static TileBase? TileOf(ushort key)
        {
            string? name = NameOf(key);
            return name != null ? Tile(name) : null;
        }
    }
}
