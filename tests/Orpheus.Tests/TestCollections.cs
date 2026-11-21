using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// P-003: тяжёлые по времени тесты идут одной коллекцией без параллели (работа над списком
    /// <c>work/flaky.md</c>). xUnit гоняет коллекции между собой параллельно, а внутри этой — строго по одному.
    /// <para>
    /// Почему одна, а не «сеть / процессы / диск»: все эти тесты меряют реальное время (бот входит за 6 с,
    /// сервер готов за 5 с, уход записан ≤ 1 с), а конкудируют они за CPU — UDP-прогон на 13 минут грузит
    /// ядра так же, как дочерний сервер или запись сейва. Отдельные коллекции на каждый ресурс продолжали бы
    /// сбивать друг друга (замер — в карточке P-003). Обычные быстрые тесты (остальные ~700) параллельны как были.
    /// </para>
    /// <para>
    /// В collection входят: UDP-боты и зонды (<c>NetTests</c>, <c>RunSeamTests</c>, <c>ReturnTests</c>,
    /// <c>ServerThreadTests</c>, <c>ClockUdpTests</c>, UDP-паузы в <c>PauseTests</c>, <c>NightBotsTests</c>),
    /// дочерние процессы сервера (<c>MetricsTests</c>, <c>HostProcessTests</c>), сейвы на диске
    /// (<c>SaveTests</c>, <c>SaveEventTests</c>, <c>SaveKillTests</c>, <c>SaveFlushTests</c>, <c>HostTests</c>)
    /// и CPU-бенчмарки (<c>PathTests</c>).
    /// </para>
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class HeavyCollection
    {
        public const string Name = "Тяжёлые по времени (по одному)";
    }
}
