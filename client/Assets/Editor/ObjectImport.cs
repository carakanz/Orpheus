using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orpheus.Client;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Orpheus.Editor
{
    /// <summary>
    /// Картинки объектов «с высотой» (E1-7): <c>Assets/Art/**/[TEMP_]tree_*.png</c>, <c>bush_*</c>, <c>boulder_*</c>,
    /// <c>ore_*</c>, <c>stump*</c>, а также верх и лицо обрыва <c>cliff_top</c>, <c>cliff_face</c> (шаг 1.6).
    /// <list type="bullet">
    /// <item>Импорт — всегда одинаковый, и для новых/заменённых файлов с Мака: спрайт, 128 px на единицу, опора внизу по
    /// центру, без сжатия, читаемый (клиент при запуске собирает из них один лист — <see cref="ObjectAtlas"/>).</item>
    /// <item><c>Assets/Resources/ObjectSet.asset</c> — список этих спрайтов и шейдеры объектов и теней;
    /// пересобирается при импорте и перед каждой сборкой, поэтому всё попадает в сборку без ручных шагов.</item>
    /// </list>
    /// </summary>
    sealed class ObjectImport : AssetPostprocessor
    {
        const string ArtDir = "Assets/Art";
        const string SetPath = "Assets/Resources/" + ObjectSet.ResourceName + ".asset";
        const string ShaderDir = "Assets/Shaders/";
        static readonly string[] Prefixes = { "tree_", "bush_", "boulder_", "ore_", "stump", "cliff_top", "cliff_face" };
        static readonly string[] Shaders = { "Objects.shader", "Shadow.shader", "ShadowComposite.shader" };

        /// <summary>Перед сборкой набор собирается заново; без шейдеров сборка падает, а не выходит без леса и теней.</summary>
        sealed class BeforeBuild : IPreprocessBuildWithReport
        {
            public int callbackOrder => 1;

            public void OnPreprocessBuild(BuildReport report)
            {
                if (!Rebuild())
                    throw new BuildFailedException("[objects] нет шейдеров " + ShaderDir + "{" + string.Join(", ", Shaders) + "} или картинок объектов");
            }
        }

        // Смена числа заставляет Unity заново импортировать картинки (их .meta мог создать общий импорт с опорой в центре).
        public override uint GetVersion() => 2;

        /// <summary>Имя вида по пути: «…/TEMP_tree_oak_a.png» → «tree_oak_a»; не объект — null.</summary>
        static string KeyOf(string path)
        {
            if (!path.StartsWith(ArtDir + "/", StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return null;
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("TEMP_", StringComparison.Ordinal)) name = name.Substring(5);
            return Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) ? name : null;
        }

        // Порядок 10: после ArtImportSettings (тот ставит новым файлам опору в центре) — здесь последнее слово.
        public override int GetPostprocessOrder() => 10;

        void OnPreprocessTexture()
        {
            if (KeyOf(assetPath) == null) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = ArtImportSettings.PixelsPerTile;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.isReadable = true;

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect;
            settings.spriteAlignment = (int)SpriteAlignment.BottomCenter;
            settings.spritePivot = new Vector2(0.5f, 0f);
            importer.SetTextureSettings(settings);
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom)
                .Any(p => KeyOf(p) != null || (p.StartsWith(ShaderDir, StringComparison.Ordinal) && Shaders.Contains(Path.GetFileName(p)))))
            {
                EditorApplication.delayCall += () => Rebuild();
            }
        }

        /// <summary>Собрать <c>ObjectSet.asset</c> заново. Файл не трогается, если состав не изменился.</summary>
        [MenuItem("Orpheus/Пересобрать набор объектов")]
        public static bool Rebuild()
        {
            Shader[] shaders = Shaders.Select(s => AssetDatabase.LoadAssetAtPath<Shader>(ShaderDir + s)).ToArray();
            var names = new List<string>();
            var sprites = new List<Sprite>();
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { ArtDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string key = KeyOf(path);
                if (key == null) continue;
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null)
                {
                    // Старый .meta (не спрайт или импорт до этого постпроцессора) — импортировать заново.
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                    if (sprite == null) continue;
                }

                // Готовая картинка (без TEMP_) главнее заглушки с тем же именем.
                int at = names.IndexOf(key);
                if (at >= 0)
                {
                    if (!Path.GetFileName(path).StartsWith("TEMP_", StringComparison.Ordinal)) sprites[at] = sprite;
                    continue;
                }

                names.Add(key);
                sprites.Add(sprite);
            }

            string[] order = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Sprite[] sorted = order.Select(n => sprites[names.IndexOf(n)]).ToArray();

            var set = AssetDatabase.LoadAssetAtPath<ObjectSet>(SetPath);
            if (set == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
                set = ScriptableObject.CreateInstance<ObjectSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }

            if (set.ObjectShader != shaders[0] || set.ShadowShader != shaders[1] || set.ShadowCompositeShader != shaders[2]
                || !set.Names.SequenceEqual(order) || !set.Sprites.SequenceEqual(sorted))
            {
                set.ObjectShader = shaders[0];
                set.ShadowShader = shaders[1];
                set.ShadowCompositeShader = shaders[2];
                set.Names = order;
                set.Sprites = sorted;
                EditorUtility.SetDirty(set);
                AssetDatabase.SaveAssetIfDirty(set);
                Debug.Log($"[objects] {SetPath}: шейдеров {shaders.Count(s => s != null)} из {shaders.Length}, картинок {order.Length}: {string.Join(", ", order)}");
            }

            foreach (string name in ObjectTable.SpriteNames())
            {
                if (!order.Contains(name))
                    Debug.LogWarning($"[objects] нет картинки {name} — в игре вместо неё будет соседний вариант или старый значок");
            }

            return shaders.All(s => s != null) && order.Length > 0;
        }
    }
}
