#nullable enable
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Кадр слоя теней и тона суток (E1-7): <c>LateUpdate</c> — после того как <see cref="GameBoot"/> в <c>Update</c>
    /// поставил камеру. Висит на основной камере, добавляет его <see cref="WorldFx.Init"/>.
    /// </summary>
    public sealed class WorldFxDriver : MonoBehaviour
    {
        Camera? _camera;

        void LateUpdate()
        {
            WorldFx.LateFrame();
            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
            }

            if (_camera != null)
            {
                LifeFx.LateFrame(_camera); // E1-11: ветер, толчки деревьев, след в траве, листья
            }
        }
    }
}
