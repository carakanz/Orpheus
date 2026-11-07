using System;

namespace Orpheus.Core
{
    /// <summary>
    /// Обход правил повторяемости ORP001–ORP006 (анализатор Orpheus.Analyzers) для одного метода.
    /// Только для логов и метрик вне симуляции; причина обязательна (иначе ORP007),
    /// каждое использование попадает в отчёт сборки предупреждением ORP900.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false, AllowMultiple = false)]
    public sealed class AllowNondeterministicAttribute : Attribute
    {
        public AllowNondeterministicAttribute(string reason)
        {
            Reason = reason;
        }

        /// <summary>Зачем методу недетерминированный код.</summary>
        public string Reason { get; }
    }
}
