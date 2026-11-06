using Orpheus.Net.Protocol;

namespace Orpheus.Net
{
    /// <summary>
    /// Модуль сети (docs/07, 2.10): протокол, снимки, часы, <c>ITransport</c> (LiteNetLib, связь в памяти, имитатор).
    /// </summary>
    public static class NetModule
    {
        /// <summary>Версия протокола: растёт с каждым несовместимым изменением схемы сообщений.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>
        /// Версия сборки, которую сверяет вход (2.10: «клиент и сервер разных сборок не соединяются»). Пока номера
        /// сборки из CI нет — 0.0.<see cref="ProtocolVersion"/>.
        /// </summary>
        public static readonly NetVersion Version = new NetVersion(0, 0, ProtocolVersion);
    }
}
