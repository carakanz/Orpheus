#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orpheus.Client.Anim.Dragon
{
    /// <summary>
    /// D-004: части дракона «вырезкой» — картинки <c>Resources/D004/TEMP_dragon_*.png</c> и описание скелета
    /// <c>TEMP_dragon_rig.json</c> (рисует <c>tools/art_d004_parts.py</c>): у каждой части опора — сустав к родителю
    /// (доли от левого нижнего угла), кость вдоль +x, точки крепления детей в тайлах от опоры.
    /// </summary>
    public sealed class DragonParts
    {
        [Serializable]
        sealed class AnchorJson
        {
            public string name = "";
            public float x, y;
        }

        [Serializable]
        sealed class PartJson
        {
            public string name = "";
            public string file = "";
            public float pivotX, pivotY;
            public AnchorJson[] anchors = Array.Empty<AnchorJson>();
        }

        [Serializable]
        sealed class RigJson
        {
            public int ppt = 256;
            public float far = 0.7f;
            public PartJson[] parts = Array.Empty<PartJson>();
        }

        public const string Folder = "D004/";

        readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        readonly Dictionary<string, Dictionary<string, Vector2>> _anchors = new Dictionary<string, Dictionary<string, Vector2>>();

        /// <summary>Тон дальних лап и крыла (множитель цвета).</summary>
        public float Far { get; private set; } = 0.7f;

        static DragonParts? _loaded;

        /// <summary>Загрузить один раз; нет описания — null (в лог — почему).</summary>
        public static DragonParts? Load()
        {
            if (_loaded != null)
            {
                return _loaded;
            }

            var json = Resources.Load<TextAsset>(Folder + "TEMP_dragon_rig");
            if (json == null)
            {
                Debug.LogError("[dragon] нет Resources/" + Folder + "TEMP_dragon_rig.json");
                return null;
            }

            RigJson rig = JsonUtility.FromJson<RigJson>(json.text);
            var p = new DragonParts { Far = rig.far };
            foreach (PartJson part in rig.parts)
            {
                var tex = Resources.Load<Texture2D>(Folder + part.file);
                if (tex == null)
                {
                    Debug.LogError("[dragon] нет картинки " + part.file);
                    continue;
                }

                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                p._sprites[part.name] = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(part.pivotX, part.pivotY),
                    rig.ppt, 0, SpriteMeshType.FullRect);
                var a = new Dictionary<string, Vector2>();
                foreach (AnchorJson an in part.anchors)
                {
                    a[an.name] = new Vector2(an.x, an.y);
                }

                p._anchors[part.name] = a;
            }

            Debug.Log($"[dragon] частей {p._sprites.Count}, {rig.ppt} px на тайл");
            _loaded = p;
            return p;
        }

        public Sprite Sprite(string part) => _sprites.TryGetValue(part, out Sprite s) ? s : throw new KeyError(part);

        public Vector2 Anchor(string part, string name) =>
            _anchors.TryGetValue(part, out Dictionary<string, Vector2>? a) && a.TryGetValue(name, out Vector2 v) ? v : throw new KeyError(part + "." + name);

        /// <summary>Длина кости (точка «next» по x); нет точки — 0.</summary>
        public float Length(string part) =>
            _anchors.TryGetValue(part, out Dictionary<string, Vector2>? a) && a.TryGetValue("next", out Vector2 v) ? v.x : 0f;

        sealed class KeyError : Exception
        {
            public KeyError(string what) : base("[dragon] нет части/точки " + what)
            {
            }
        }
    }
}
