#nullable enable
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Набор картинок земли и воды и шейдер земли (E1-4). Лежит в <c>Assets/Resources/GroundSet.asset</c> — ссылка отсюда
    /// кладёт шейдер и картинки в сборку. Файл собирает редактор сам (<c>Orpheus.Editor.GroundImport</c>): при импорте
    /// картинок <c>*ground_*.png</c> / <c>*water_*.png</c> из <c>Assets/Art</c> и перед каждой сборкой.
    /// </summary>
    public sealed class GroundSet : ScriptableObject
    {
        public const string ResourceName = "GroundSet";

        public Shader? Shader;

        /// <summary>Имена без «TEMP_» и расширения (ground_grass, water_deep…), по одному на картинку.</summary>
        public string[] Names = new string[0];
        public Texture2D[] Textures = new Texture2D[0];

        public Texture2D? Find(string name)
        {
            for (int i = 0; i < Names.Length && i < Textures.Length; i++)
            {
                if (Names[i] == name)
                {
                    return Textures[i];
                }
            }

            return null;
        }
    }
}
