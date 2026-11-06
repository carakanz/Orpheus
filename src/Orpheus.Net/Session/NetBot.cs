using Orpheus.Core.Bots;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Бот по сети (docs/07, 11.3): <see cref="NetClient"/> + сценарий <see cref="SimBot"/> (<c>idle</c>, <c>wander</c>).
    /// Вызывающий зовёт <see cref="Frame"/> 60 раз в секунду: разбор пришедшего, раз в два кадра — решение сценария,
    /// каждый кадр — пакет ввода. Сценарий решает по своему номеру такта (кадр / 2) и позиции из снимка.
    /// </summary>
    public sealed class NetBot
    {
        private readonly string _script;
        private readonly int _botIndex;
        private SimBot? _bot;
        private InputFrame _current;

        public NetBot(NetClient client, string script, int botIndex)
        {
            Client = client;
            _script = script;
            _botIndex = botIndex;
        }

        public NetClient Client { get; }

        /// <summary>Сценарий.</summary>
        public string Script => _script;

        /// <summary>Кадр 60 Гц: разобрать пришедшее, решить, отправить ввод.</summary>
        public void Frame(long nowMicros)
        {
            Client.Poll(nowMicros);
            if (Client.State != NetClientState.InGame)
            {
                return;
            }

            if (_bot == null)
            {
                _bot = SimBot.Create(_script, Client.EntityId, _botIndex, Client.Accept.WorldSeed);
            }

            uint next = Client.Frame + 1;
            if ((next & 1) == 0)
            {
                var self = new SimEntity { Id = Client.EntityId, Kind = SimEntity.KindCharacter };
                if (Client.HasSnapshot && Client.Latest.TryFind(Client.EntityId, out SnapshotEntity e))
                {
                    self.Mover = new Mover(e.Position);
                }

                _current = _bot.Think(next / 2, self);
            }

            Client.SendFrame(_current);
        }
    }
}
