using UnityEditor;
using UnityEngine;

namespace Orpheus.Editor
{
    /// <summary>
    /// Настройки импорта картинок из Assets/Art при первом импорте (docs/00_vision.md, 5.1):
    /// спрайт, 128 px на тайл, без mip-карт. Персонажи (имя содержит "_char_") — опора внизу по центру.
    /// Объекты на тайле (имя содержит "_obj_", 128 × 192) — опора в центре нижнего квадрата 128 × 128
    /// (на высоте 64 px = 1/3 картинки): нижний квадрат ложится на свой тайл, верх заходит на тайл выше (наклон, 00 5.1).
    /// Срабатывает только для новых файлов: ручные правки .meta не перетираются.
    /// </summary>
    sealed class ArtImportSettings : AssetPostprocessor
    {
        public const float PixelsPerTile = 128f;

        /// <summary>Опора объекта 128 × 192 по Y: центр нижнего тайла — 64 px от низа = 1/3.</summary>
        public const float ObjectPivotY = 64f / 192f;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/Art/") || !assetImporter.importSettingsMissing)
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = PixelsPerTile;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect;
            if (assetPath.Contains("_char_"))
            {
                settings.spriteAlignment = (int)SpriteAlignment.BottomCenter;
            }
            else if (assetPath.Contains("_obj_"))
            {
                settings.spriteAlignment = (int)SpriteAlignment.Custom;
                settings.spritePivot = new Vector2(0.5f, ObjectPivotY);
            }
            else
            {
                settings.spriteAlignment = (int)SpriteAlignment.Center;
            }
            importer.SetTextureSettings(settings);
        }
    }
}
