using Orpheus.Client.Anim;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-053: выбор вида существа по направлению (ViewPick.Pick) — 4 направления на экране:
    /// вбок (бок-налево — отражение), к зрителю, от зрителя. Порог сектора — |dirY| &gt; |dirX|·1,2;
    /// мёртвая зона сохраняет предыдущий вид (стоящий на месте враг не «мигает» видами).
    /// </summary>
    public class C053ViewPickTests
    {
        [Fact]
        public void C053_Pick_Cardinals()
        {
            Assert.Equal(ActorView.Side, ViewPick.Pick(1f, 0f, ActorView.Front, 0.001f));   // восток — бок
            Assert.Equal(ActorView.Side, ViewPick.Pick(-1f, 0f, ActorView.Front, 0.001f));  // запад — тот же спрайт (отражение делает FlipX)
            Assert.Equal(ActorView.Front, ViewPick.Pick(0f, -1f, ActorView.Side, 0.001f));  // на нас (y экрана уменьшается)
            Assert.Equal(ActorView.Back, ViewPick.Pick(0f, 1f, ActorView.Side, 0.001f));    // от нас
        }

        [Fact]
        public void C053_Pick_Threshold_ExactlyAtWeight_IsSide()
        {
            // Граница сектора точная: |dirY| = |dirX|·FrontWeight — ещё «вбок», чуть выше — «к нам».
            // Порог закрепления литералом (П 21.11): завышение FrontWeight без правки теста — красный.
            Assert.Equal(1.2f, ViewPick.FrontWeight);
            Assert.Equal(ActorView.Side, ViewPick.Pick(1f, 1.2f, ActorView.Back, 0.001f));
            Assert.Equal(ActorView.Front, ViewPick.Pick(1f, -1.21f, ActorView.Back, 0.001f));
            Assert.Equal(ActorView.Back, ViewPick.Pick(-1f, 1.21f, ActorView.Front, 0.001f));
            // Диагональ 45° (|x|=|y|) ближе к вертикали порога 1,2 — остаётся боковой.
            Assert.Equal(ActorView.Side, ViewPick.Pick(0.7f, 0.7f, ActorView.Front, 0.001f));
        }

        [Fact]
        public void C053_Pick_DeadZone_KeepsPrevious()
        {
            Assert.Equal(ActorView.Front, ViewPick.Pick(0f, 0f, ActorView.Front, 0.001f));   // стоим — вид не меняется
            Assert.Equal(ActorView.Back, ViewPick.Pick(0.0004f, -0.0004f, ActorView.Back, 0.001f)); // ниже мёртвой зоны
            Assert.Equal(ActorView.Front, ViewPick.Pick(0.01f, -0.01f, ActorView.Front, 0.02f));    // обе оси в зоне — стоим, вид прежний
        }
    }
}
