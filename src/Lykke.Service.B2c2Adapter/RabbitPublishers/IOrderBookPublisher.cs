﻿using System.Threading.Tasks;
using Lykke.Common.ExchangeAdapter.Contracts;

namespace Lykke.Service.B2c2Adapter.RabbitPublishers
{
    public interface IOrderBookPublisher
    {
        Task PublishAsync(OrderBook message);
    }
}
