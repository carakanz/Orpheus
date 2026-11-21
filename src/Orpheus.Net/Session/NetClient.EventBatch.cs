using System;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// C-035: «пачка событий» (40) у клиента: записи 37/38/39 разбираются по порядку и проходят тем же путём,
    /// что одиночные сообщения — <see cref="DamageSeen"/>, <see cref="Projectiles"/> и <see cref="OnStrike"/>;
    /// счётчики (DamagesSeen и т. д.) считаются по записям, как раньше по пакетам.
    /// </summary>
    public sealed partial class NetClient
    {
        /// <summary>Запас под одно одиночное сообщение из пачки (тип + тело): наибольшее тело — взмах 7 + 13 × 3.</summary>
        private readonly byte[] _eventScratch = new byte[48];

        /// <summary>
        /// Пачка (40) наружу: записи по порядку; тело + байт вида (он же тип одиночного) в <see cref="_eventScratch"/>,
        /// прежние <see cref="DamageMessage.Read"/> / <see cref="ProjectileMessage.Read"/> / <see cref="StrikeMessage.Read"/>.
        /// </summary>
        private void OnEventBatch(ReadOnlySpan<byte> data)
        {
            EventBatchReader r = EventBatchMessage.Open(data);
            while (r.TryNext(out EventRecordKind kind, out ReadOnlySpan<byte> body))
            {
                switch (kind)
                {
                    case EventRecordKind.Damage:
                        body.CopyTo(_eventScratch.AsSpan(1));
                        _eventScratch[0] = (byte)MessageType.Damage;
                        DamagesSeen++;
                        DamageSeen?.Invoke(DamageMessage.Read(new ReadOnlySpan<byte>(_eventScratch, 0, body.Length + 1)));
                        break;
                    case EventRecordKind.Projectile:
                        body.CopyTo(_eventScratch.AsSpan(1));
                        _eventScratch[0] = (byte)MessageType.Projectile;
                        Projectiles.OnMessage(ProjectileMessage.Read(new ReadOnlySpan<byte>(_eventScratch, 0, body.Length + 1)), EntityId, _nowUs);
                        break;
                    default:
                        body.CopyTo(_eventScratch.AsSpan(1));
                        _eventScratch[0] = (byte)MessageType.Strike;
                        OnStrike(StrikeMessage.Read(new ReadOnlySpan<byte>(_eventScratch, 0, body.Length + 1)));
                        break;
                }
            }

            r.ExpectEnd();
        }
    }
}
