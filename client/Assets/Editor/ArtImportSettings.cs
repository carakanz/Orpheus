using UnityEditor;
using UnityEngine;

namespace Orpheus.Editor
{
    /// <summary>
    /// Настройки импорта картинок из Assets/Art при первом импорте (docs/00_vision.md, 5.1):
    /// спрайт, 128 px на тайл, без mip-карт. Персонажи (имя содержит "_char_") — опора внизу по центру.
    /// Срабатывает только для новых файлов: ручные правки .meta не перетираются.
    /// </summary>
    sealed class ArtImportSettings : AssetPostprocessor
    {
        public const float PixelsPerTile = 128f;

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
            settings.spriteAlignment = assetPath.Contains("_char_")
                ? (int)SpriteAlignment.BottomCenter
                : (int)SpriteAlignment.Center;
            importer.SetTextureSettings(settings);
        }
    }
}
