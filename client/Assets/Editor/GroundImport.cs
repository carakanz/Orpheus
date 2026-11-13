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
    /// Картинки земли и воды (E1-4): <c>Assets/Art/**/[TEMP_]ground_*.png</c> и <c>[TEMP_]water_*.png</c>.
    /// <list type="bullet">
    /// <item>Импорт — всегда одинаковый, и для новых файлов с Мака: обычная текстура (не спрайт), повтор, mip-карты,
    /// трилинейный фильтр, без сжатия, читаемая (клиент собирает из них <c>Texture2DArray</c> при запуске).</item>
    /// <item><c>Assets/Resources/GroundSet.asset</c> — список этих картинок и шейдер земли; пересобирается при импорте
    /// и перед каждой сборкой, поэтому шейдер и картинки попадают в сборку без ручных шагов.</item>
    /// </list>
    /// </summary>
    sealed class GroundImport : AssetPostprocessor
    {
        const string ArtDir = "Assets/Art";
        const string SetPath = "Assets/Resources/" + GroundSet.ResourceName + ".asset";
        public const string ShaderPath = "Assets/Shaders/Ground.shader";

        /// <summary>Перед сборкой набор собирается заново; без шейдера или картинок сборка падает, а не выходит без земли.</summary>
        sealed class BeforeBuild : IPreprocessBuildWithReport
        {
            public int callbackOrder => 0;

            public void OnPreprocessBuild(BuildReport report)
            {
                if (!Rebuild())
                    throw new BuildFailedException("[ground] нет шейдера " + ShaderPath + " или картинок земли — сборка была бы без земли");
            }
        }

        // Смена числа заставляет Unity заново импортировать картинки старыми .meta (были спрайтами до E1-4).
        public override uint GetVersion() => 1;

        /// <summary>Имя вида по пути: «…/TEMP_ground_grass.png» → «ground_grass»; не земля и не вода — null.</summary>
        static string KeyOf(string path)
        {
            if (!path.StartsWith(ArtDir + "/", StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return null;
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("TEMP_", StringComparison.Ordinal)) name = name.Substring(5);
            return name.StartsWith("ground_", StringComparison.Ordinal) || name.StartsWith("water_", StringComparison.Ordinal) ? name : null;
        }

        // Порядок 10: после ArtImportSettings (тот ставит «спрайт» новым файлам) — здесь последнее слово для земли.
        public override int GetPostprocessOrder() => 10;

        void OnPreprocessTexture()
        {
            if (KeyOf(assetPath) == null) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.isReadable = true;
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(p => KeyOf(p) != null || p == ShaderPath))
            {
                EditorApplication.delayCall += () => Rebuild();
            }
        }

        /// <summary>Собрать <c>GroundSet.asset</c> заново. Файл не трогается, если состав не изменился.</summary>
        [MenuItem("Orpheus/Пересобрать набор земли")]
        public static bool Rebuild()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            var names = new List<string>();
            var textures = new List<Texture2D>();
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { ArtDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string key = KeyOf(path);
                if (key == null) continue;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null) continue;
                // Готовая картинка (без TEMP_) главнее заглушки с тем же именем.
                int at = names.IndexOf(key);
                if (at >= 0)
                {
                    if (!Path.GetFileName(path).StartsWith("TEMP_", StringComparison.Ordinal)) textures[at] = tex;
                    continue;
                }
                names.Add(key);
                textures.Add(tex);
            }

            string[] order = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Texture2D[] sorted = order.Select(n => textures[names.IndexOf(n)]).ToArray();

            var set = AssetDatabase.LoadAssetAtPath<GroundSet>(SetPath);
            if (set == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
                set = ScriptableObject.CreateInstance<GroundSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }

            if (set.Shader != shader || !set.Names.SequenceEqual(order) || !set.Textures.SequenceEqual(sorted))
            {
                set.Shader = shader;
                set.Names = order;
                set.Textures = sorted;
                EditorUtility.SetDirty(set);
                AssetDatabase.SaveAssetIfDirty(set);
                Debug.Log($"[ground] {SetPath}: шейдер {(shader != null ? shader.name : "НЕТ")}, картинок {order.Length}: {string.Join(", ", order)}");
            }

            foreach (GroundLayer layer in GroundTable.Layers)
            {
                if (!order.Contains(layer.Name))
                    Debug.LogWarning($"[ground] нет картинки слоя {layer.Name} — в игре будет ровный цвет");
            }

            return shader != null && order.Length > 0;
        }
    }
}
