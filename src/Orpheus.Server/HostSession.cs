namespace Orpheus.Server
{
    /// <summary>
    /// Ожидание хозяина у сервера игры (0-024, docs/07 2.3 и 7.6): игра хоста упала — сервер работает
    /// <see cref="GraceMicros"/> (60 с) от последнего сообщения хозяина, остальные играют; хозяин вернулся — отсчёт снят;
    /// не вернулся — «хост вышел». До первого входа хозяина отсчёт идёт от старта: игра, упавшая до подключения, не
    /// оставляет сервер жить вечно. Время — микросекунды цикла такта (те же, что у <c>NetServer.Poll</c>).
    /// </summary>
    public sealed class HostSession
    {
        private long _deadline;

        public HostSession(long graceMicros, long nowMicros)
        {
            GraceMicros = graceMicros;
            _deadline = nowMicros + graceMicros;
        }

        public long GraceMicros { get; }

        /// <summary>Хозяин в игре.</summary>
        public bool HostIn { get; private set; }

        /// <summary>Хозяин хоть раз входил.</summary>
        public bool EverJoined { get; private set; }

        /// <summary>Сколько раз хозяин возвращался после ухода.</summary>
        public int Returns { get; private set; }

        /// <summary>Когда кончится ожидание (мкс); хозяин в игре — не важно.</summary>
        public long DeadlineMicros => _deadline;

        public void Joined()
        {
            if (EverJoined && !HostIn)
            {
                Returns++;
            }

            HostIn = true;
            EverJoined = true;
        }

        /// <summary>Хозяин ушёл; отсчёт — от его последнего сообщения (транспорт замечает обрыв лишь через тайм-аут).</summary>
        public void Left(long lastHeardMicros, long nowMicros)
        {
            HostIn = false;
            long from = lastHeardMicros > 0 && lastHeardMicros <= nowMicros ? lastHeardMicros : nowMicros;
            _deadline = from + GraceMicros;
        }

        /// <summary>Ожидание кончилось: хоста нет и срок прошёл.</summary>
        public bool Expired(long nowMicros) => !HostIn && nowMicros >= _deadline;
    }
}
