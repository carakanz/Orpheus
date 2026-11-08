#nullable enable
using UnityEngine;

namespace Orpheus.Client.Anim
{
    /// <summary>Существо на экране из кода (как чужие в <c>NetGame.GetEntity</c>): корень + дочерний спрайт с опорой у ступней.</summary>
    public static class ActorFactory
    {
        /// <summary>Опора спрайта ниже центра существа (как у героя в сцене, <c>ProjectSetup</c>).</summary>
        public const float SpriteFootY = -0.4f;

        /// <summary>Враги-болванки и манекен — тот же спрайт, тёплый красный оттенок (своих ассетов врагов в клиенте пока нет).</summary>
        public static readonly Color EnemyTint = new Color(1f, 0.55f, 0.45f);

        public static Transform Create(Transform parent, string name, Sprite sprite, Color color)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            var spriteGo = new GameObject("Sprite");
            spriteGo.transform.SetParent(root.transform, false);
            spriteGo.transform.localPosition = new Vector3(0f, SpriteFootY, 0f);
            var sr = spriteGo.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = WorldView.ObjectOrder;
            sr.spriteSortPoint = SpriteSortPoint.Pivot;
            return root.transform;
        }
    }

    /// <summary>
    /// Враг с ближним ударом на экране (манекен 0-029, болванки замера): поза по фазе замаха и сектор на земле.
    /// Фазу даёт <see cref="SwingTrack"/> (сеть) или сценарий (показ, замер).
    /// </summary>
    public sealed class SwingActor
    {
        public readonly ActorRig Rig;
        readonly SectorView _sector;
        SwingPhase _lastPhase;
        float _sinceContact = float.MaxValue;

        /// <summary>Ось удара (единичная, экранные оси), досягаемость (тайлов), половина дуги (радианы).</summary>
        public Vector2 Axis = Vector2.left;
        public float Reach = 1.4f;
        public float HalfArc = Mathf.PI / 4f;

        public SwingActor(Transform root, Material sectorMaterial)
        {
            Rig = new ActorRig(root);
            _sector = new SectorView(root.parent, sectorMaterial, root.name + " sector");
        }

        /// <summary>Кадр: фаза и доля; возвращает true в кадре, где начался удар (касание).</summary>
        public bool Animate(float dt, SwingPhase phase, float progress)
        {
            Vector2 v = Rig.MeasureVelocity(dt);
            var s = new ActorInput { VelX = v.x, VelY = v.y, Swing = phase, SwingProgress = progress, SwingDirX = Axis.x, SwingDirY = Axis.y };
            Rig.Apply(Rig.Animator.Step(dt, s));

            bool struck = phase == SwingPhase.Strike && _lastPhase != SwingPhase.Strike;
            if (struck)
            {
                _sinceContact = 0f;
            }
            else if (_sinceContact < float.MaxValue)
            {
                _sinceContact += dt;
            }

            _lastPhase = phase;
            Vector3 c = Rig.Root.position;
            float flash = _sinceContact < AnimParams.StrikeFlashSec ? 1f - _sinceContact / AnimParams.StrikeFlashSec : 0f;
            switch (phase)
            {
                case SwingPhase.Windup:
                    _sector.Show(c, Axis, Reach, HalfArc, progress, 0f, 1f);
                    break;
                case SwingPhase.Strike:
                    _sector.Show(c, Axis, Reach, HalfArc, 1f, flash, 1f);
                    break;
                case SwingPhase.Recover when progress < 0.35f:
                    _sector.Show(c, Axis, Reach, HalfArc, 1f, flash, 1f - progress / 0.35f);
                    break;
                default:
                    _sector.Hide();
                    break;
            }

            return struck;
        }

        /// <summary>Точка внутри сектора удара (для искр у цели в показе).</summary>
        public bool InSector(Vector2 point)
        {
            Vector2 d = point - (Vector2)Rig.Root.position;
            if (d.sqrMagnitude > (Reach + 0.4f) * (Reach + 0.4f))
            {
                return false;
            }

            return Vector2.Angle(Axis, d) <= HalfArc * Mathf.Rad2Deg + 15f;
        }
    }
}
