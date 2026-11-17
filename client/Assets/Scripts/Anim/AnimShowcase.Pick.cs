#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;
using Items = Orpheus.Core.Sim.Items;

namespace Orpheus.Client.Anim
{
    /// <summary>
    /// C-028: взмах кирки в ленте снимков (файлы <c>C-028_*.png</c>). Шахтёр E2-7 у камня бьёт взмахами по 24 кадра (как ядро,
    /// <see cref="PickStrike.SwingFrames"/>) к востоку; в миг удара — тайлы удара тем же <see cref="PickStrike.Targets"/>, что у
    /// сервера (по настоящему миру сцены), удар в свою <see cref="TileWear"/>, искры на каждом тайле, трещины —
    /// <see cref="TileCracksView"/>, как в игре. Набрал нужное — в игре камень снимается; здесь мира не правим: крошка
    /// и трещины сбрасываются.
    /// </summary>
    public sealed partial class AnimShowcase
    {
        readonly TileWear _pickWear = new TileWear(long.MaxValue / 4);
        readonly TileCoord[] _pickTiles = new TileCoord[PickStrike.MaxTiles];
        readonly ushort[] _pickObjs = new ushort[PickStrike.MaxTiles];
        readonly byte[] _pickNeeded = new byte[PickStrike.MaxTiles];
        readonly DigTimes _pickTimes = new DigTimes();
        TileCracksView? _pickCracks;

        /// <summary>Ударов шахтёра (для отчёта ленты).</summary>
        public int PickImpacts { get; private set; }

        /// <summary>Удар шахтёра: тайлы сектора к камню, удары, искры; набравший — «снят» (крошка, трещины с нуля).</summary>
        void PickImpact()
        {
            PickImpacts++;
            if (!_rockReal)
            {
                NetAnimation.Emit(_sparks, _rock, Vector2.left, ImpactLook.Stone);
                return;
            }

            WorldPos miner = _g.TileCenter(new TileCoord(_rockTile.X - 1, _rockTile.Y));
            int n = PickStrike.Targets(_g.World, miner, _rockTile, _pickTimes, Items.StonePickaxe, _pickTiles, _pickObjs, _pickNeeded);
            for (int k = 0; k < n; k++)
            {
                Vector3 c = _g.Origin.ToUnity(_g.TileCenter(_pickTiles[k]));
                Vector2 back = ((Vector2)(MinerPos - c)).normalized;
                NetAnimation.EmitOnRock(_sparks, c, back);
                if (_pickWear.Hit(_pickTiles[k], _pickObjs[k], _pickNeeded[k], PickImpacts) >= _pickNeeded[k])
                {
                    _pickWear.Set(_pickTiles[k], _pickObjs[k], 0, _pickNeeded[k], PickImpacts); // в игре — снят
                    _sparks.Order = WorldView.RowOrderAt(c.y - 0.5f + ObjectTable.BaseLift) + 2;
                    _sparks.Emit(c + new Vector3(0f, 0.35f, 0f), Vector2.up, AnimParams.StoneCrumbs * 3, AnimParams.DigDustSpeed * 1.4f, AnimParams.DigDustLife * 1.5f, 80f, true,
                        AnimParams.DustStone, AnimParams.DustStone, AnimParams.DustLight);
                    _sparks.Order = WorldView.ObjectOrder + 2;
                }
            }

            if (n == 0)
            {
                NetAnimation.Emit(_sparks, _rock, Vector2.left, ImpactLook.Dust);
            }
        }

        void PickFrame(float dt)
        {
            _pickCracks ??= new TileCracksView(_stage);
            _pickCracks.Frame(_pickWear, _ => true, _g.Origin, dt);
        }

        /// <summary>
        /// Ленты C-028 после лука (с 23,8 с): взмах (подъём, верх, удар — искры по трём камням, отдача) и трещины после 1-го, 2-го
        /// удара, перед 3-м и после него. Цикл шахтёра 0,75 с (взмах 0,4 + пауза ответа 0,35), удар № k — в 0,75k + 0,2 с.
        /// </summary>
        Shot[] PickShots() => new[]
        {
            new Shot { Name = "C-028_pick_swing", Focus = () => (MinerPos + _rock) * 0.5f, Ortho = ShotsOrtho,
                Times = new[] { 23.82f, 23.94f, 24.06f, 24.14f, 24.20f, 24.30f }, CropW = 640, CropH = 640 },
            new Shot { Name = "C-028_pick_cracks", Focus = () => (MinerPos + _rock) * 0.5f, Ortho = ShotsOrtho,
                Times = new[] { 25.10f, 25.85f, 26.42f, 26.52f }, CropW = 640, CropH = 640 },
        };
    }
}
