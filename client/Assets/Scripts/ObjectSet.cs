#nullable enable
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Набор картинок объектов «с высотой» (деревья, кусты, валуны, руда, пень) и шейдеры объектов и теней
    /// (E1-7, план 1.7). Лежит в <c>Assets/Resources/ObjectSet.asset</c> — ссылка отсюда кладёт картинки и шейдеры в
    /// сборку. Файл собирает редактор сам (<c>Orpheus.Editor.ObjectImport</c>): при импорте картинок и перед сборкой.
    /// </summary>
    public sealed class ObjectSet : ScriptableObject
    {
        public const string ResourceName = "ObjectSet";

        /// <summary>Объекты: спрайты из общего листа, полупрозрачность кроны над своим персонажем.</summary>
        public Shader? ObjectShader;

        /// <summary>Тени: силуэты в отдельную текстуру (наибольшее из перекрытий, не сумма).</summary>
        public Shader? ShadowShader;

        /// <summary>Наложение текстуры теней на землю — один раз, с постоянной прозрачностью.</summary>
        public Shader? ShadowCompositeShader;

        /// <summary>Имена без «TEMP_» и расширения (tree_oak_a, boulder_b…), по одному на картинку.</summary>
        public string[] Names = new string[0];
        public Sprite[] Sprites = new Sprite[0];
    }
}
