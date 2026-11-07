using Orpheus.Net.Protocol;

namespace Orpheus.Net
{
    /// <summary>
    /// Модуль сети (docs/07, 2.10): протокол, снимки, часы, <c>ITransport</c> (LiteNetLib, связь в памяти, имитатор).
    /// </summary>
    public static class NetModule
    {
        /// <summary>Версия протокола: растёт с каждым несовместимым изменением схемы сообщений.</summary>
        public const int ProtocolVersion = 5; // 2 — хэш генерации во «входе принят» (0-017); 3 — токен хозяина во входе, «Открыть для друзей», «мир закрывается» (0-024); 4 — эпоха часов в снимке и дельте, Ping/Pong (0-025); 5 — уворот в своём состоянии снимка, «замах начат» (13) и «удар решён» (14) (0-029; 0-026 тоже берёт 5 — второму при слиянии брать 6)

        /// <summary>
        /// Версия сборки, которую сверяет вход (2.10: «клиент и сервер разных сборок не соединяются»). Пока номера
        /// сборки из CI нет — 0.0.<see cref="ProtocolVersion"/>.
        /// </summary>
        public static readonly NetVersion Version = new NetVersion(0, 0, ProtocolVersion);
    }
}
