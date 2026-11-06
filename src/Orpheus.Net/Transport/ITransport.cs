using System;

namespace Orpheus.Net.Transport
{
    /// <summary>
    /// Каналы связи (docs/07, 2.10). Доставку задаёт номер канала: 0 — последовательная ненадёжная
    /// (последняя побеждает), 1–4 — надёжные по порядку; 4 — с дроблением.
    /// </summary>
    public enum NetChannel : byte
    {
        /// <summary>Снимки (сервер → клиент), пакеты ввода (клиент → сервер), пинги. Ненадёжный, ≤ 1200 байт.</summary>
        Unreliable = 0,

        /// <summary>Мир: чанки, правки тайлов, метки, порталы.</summary>
        World = 1,

        /// <summary>Факты игры: вход, урон, смерть, инвентарь, сюжет, чат, «часы».</summary>
        Facts = 2,

        /// <summary>Крупное при входе, своя дозировка.</summary>
        Bulk = 3,

        /// <summary>Полный снимок обзора, когда база потеряна; с дроблением.</summary>
        FullSnapshot = 4,
    }

    /// <summary>Почему соединение закрылось.</summary>
    public enum NetDisconnectReason : byte
    {
        /// <summary>Закрыли с этой стороны (<see cref="ITransport.Disconnect"/>).</summary>
        Local = 0,

        /// <summary>Закрыла другая сторона; причина — в данных отключения.</summary>
        Remote = 1,

        /// <summary>Тайм-аут: другая сторона молчит дольше <see cref="NetSettings.DisconnectTimeoutMs"/>.</summary>
        Timeout = 2,

        /// <summary>Подключиться не удалось (сервер не ответил или отказал на уровне транспорта).</summary>
        ConnectFailed = 3,

        /// <summary>Остановлен транспорт или ошибка сокета.</summary>
        Other = 4,
    }

    /// <summary>
    /// Получатель событий транспорта. События приходят только внутри <see cref="ITransport.Poll"/> — в потоке,
    /// который его зовёт (docs/07, 2.10: «сообщения разбираются в потоке такта»). Данные в <c>data</c> живут
    /// только до возврата из вызова — сохранять копию.
    /// </summary>
    public interface ITransportHandler
    {
        /// <summary>Соединение с <paramref name="peer"/> открыто.</summary>
        void OnConnected(int peer);

        /// <summary>Соединение закрыто; <paramref name="payload"/> — данные отключения другой стороны (может быть пусто).</summary>
        void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload);

        /// <summary>Пришло сообщение.</summary>
        void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data);
    }

    /// <summary>
    /// Транспорт за интерфейсом (docs/07, 1.6 и 2.10): на ПК — UDP через LiteNetLib (<see cref="LiteNetTransport"/>),
    /// «сервер в потоке» и тесты — в памяти (<see cref="LoopbackTransport"/>), порча связи — обёртка
    /// <see cref="NetSimTransport"/>; на PS5 — потом, через API платформы.
    /// <para>
    /// Часов внутри нет: время приходит снаружи в <see cref="Poll"/> (микросекунды от любого начала, монотонно) —
    /// так ядро не трогает настенных часов (11.5), а имитатор связи в тестах идёт по виртуальному времени.
    /// </para>
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>
        /// Разобрать пришедшее и раздать события <paramref name="handler"/>. <paramref name="nowMicros"/> — время
        /// вызывающего в микросекундах; по нему же метятся отправки до следующего <see cref="Poll"/>.
        /// </summary>
        void Poll(long nowMicros, ITransportHandler handler);

        /// <summary>
        /// Отправить <paramref name="data"/> по каналу <paramref name="channel"/>. Канал 0 — не больше
        /// <see cref="NetSettings.MaxUnreliablePayload"/> байт (иначе исключение); нет такого соединения — ничего.
        /// </summary>
        void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data);

        /// <summary>Закрыть соединение; <paramref name="payload"/> получит другая сторона (например, отказ входа).</summary>
        void Disconnect(int peer, ReadOnlySpan<byte> payload);

        /// <summary>Оценка RTT до <paramref name="peer"/> в миллисекундах; неизвестно — −1.</summary>
        int RoundTripMs(int peer);
    }
}
