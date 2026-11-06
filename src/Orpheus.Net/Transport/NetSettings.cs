namespace Orpheus.Net.Transport
{
    /// <summary>
    /// Настройки транспорта — явно и одним местом (docs/07, 2.10): умолчания LiteNetLib не подходят.
    /// </summary>
    public static class NetSettings
    {
        /// <summary>Размер UDP-пакета: 1280 − 40 (IPv6) − 8 (UDP); поиск MTU выключен.</summary>
        public const int Mtu = 1232;

        /// <summary>Заголовок канала LiteNetLib (последовательный и надёжный каналы).</summary>
        public const int ChannelHeaderBytes = 4;

        /// <summary>Полезная нагрузка одного пакета канала: 1232 − 4 = 1228.</summary>
        public const int MaxUnreliablePayload = Mtu - ChannelHeaderBytes;

        /// <summary>Бюджет снимка (4.3): помещается в пакет с запасом 28 байт.</summary>
        public const int SnapshotBudget = 1200;

        /// <summary>Каналов: 0–4 (таблица 2.10).</summary>
        public const int ChannelsCount = 5;

        /// <summary>Тайм-аут отключения: правила 7.5 рассчитаны на 10 с (по умолчанию у LiteNetLib — 5 с).</summary>
        public const int DisconnectTimeoutMs = 10_000;

        /// <summary>Пинг раз в секунду — для часов (2.4).</summary>
        public const int PingIntervalMs = 1_000;

        /// <summary>Ключ подключения: чужие UDP-пакеты на порт не становятся соединениями.</summary>
        public const string ConnectionKey = "orpheus";

        /// <summary>Наибольший размер надёжного сообщения (каналы 1–4, LiteNetLib дробит): 64 КБ *(прикидка)*.</summary>
        public const int MaxReliablePayload = 64 * 1024;
    }
}
