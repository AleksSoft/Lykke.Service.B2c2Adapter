﻿using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Common.Log;
using Lykke.B2c2Client;
using Lykke.B2c2Client.Exceptions;
using Lykke.B2c2Client.Models.WebSocket;
using Lykke.Common.ExchangeAdapter.Contracts;
using Lykke.Common.Log;
using Lykke.Service.B2c2Adapter.RabbitPublishers;
using Lykke.Service.B2c2Adapter.Settings;

namespace Lykke.Service.B2c2Adapter.Services
{
    public sealed class B2c2OrderBooksService : IStartable
    {
        private const string Source = "b2c2";
        private readonly IReadOnlyCollection<InstrumentLevels> _instrumentsLevels;
        private readonly ConcurrentDictionary<string, string> _withWithoutSuffixMapping;
        private readonly ConcurrentDictionary<string, string> _withoutWithSuffixMapping;
        private readonly ConcurrentDictionary<string, PriceMessage> _priceMessagesCache;
        private readonly ConcurrentDictionary<string, OrderBook> _orderBooksCache;
        private readonly IB2c2RestClient _b2c2RestClient;
        private readonly IB2c2WebSocketClient _b2C2WebSocketClient;
        private readonly IOrderBookPublisher _orderBookPublisher;
        private readonly ITickPricePublisher _tickPricePublisher;
        private readonly ILog _log;
        private const string Suffix = ".SPOT";

        public B2c2OrderBooksService(IReadOnlyCollection<InstrumentLevels> instrumentsLevels, 
            IB2c2RestClient b2C2RestClient, IB2c2WebSocketClient b2C2WebSocketClient,
            IOrderBookPublisher orderBookPublisher, ITickPricePublisher tickPricePublisher,
            ILogFactory logFactory)
        {
            _instrumentsLevels = instrumentsLevels;
            _withWithoutSuffixMapping = new ConcurrentDictionary<string, string>();
            _withoutWithSuffixMapping = new ConcurrentDictionary<string, string>();
            _priceMessagesCache = new ConcurrentDictionary<string, PriceMessage>();
            _orderBooksCache = new ConcurrentDictionary<string, OrderBook>();
            _b2c2RestClient = b2C2RestClient;
            _b2C2WebSocketClient = b2C2WebSocketClient;
            _orderBookPublisher = orderBookPublisher;
            _tickPricePublisher = tickPricePublisher;
            _log = logFactory.CreateLog(this);
        }

        public void Start()
        {
            InitializeAssetPairs();
            SubscribeToOrderBooks();
        }

        public IReadOnlyCollection<string> GetAllInstruments()
        {
            return _withoutWithSuffixMapping.Keys.ToList();
        }

        public IReadOnlyCollection<TickPrice> GetAllTickPrices()
        {
            return _orderBooksCache.Values.Select(TickPrice.FromOrderBook).ToList();
        }

        public OrderBook GetOrderBook(string assetPair)
        {
            if (!_orderBooksCache.ContainsKey(assetPair))
                return null;

            return _orderBooksCache[assetPair];
        }

        private void InitializeAssetPairs()
        {
            var instruments = _b2c2RestClient.InstrumentsAsync().GetAwaiter().GetResult();
            foreach (var instrument in instruments)
            {
                var withoutSpotSuffix = InstrumentWoSuffix(instrument.Name);
                _withWithoutSuffixMapping[instrument.Name] = withoutSpotSuffix;
                _withoutWithSuffixMapping[withoutSpotSuffix] = instrument.Name;
            }
        }

        private void SubscribeToOrderBooks()
        {
            foreach (var instrumentLevels in _instrumentsLevels)
            {
                var instrument = instrumentLevels.Instrument;
                if (_withWithoutSuffixMapping.ContainsKey(instrument))
                {
                    _log.Warning($"Didn't find instrument {instrument}.");
                    continue;
                }

                var instrumentWithSuffix = _withoutWithSuffixMapping[instrument];
                var levels = instrumentLevels.Levels;

                try
                {
                    _b2C2WebSocketClient.SubscribeAsync(instrumentWithSuffix, levels, HandleAsync).GetAwaiter().GetResult();
                }
                catch (B2c2WebSocketException e)
                {
                    _log.Warning($"Can't subscribe to instrument {instrumentWithSuffix}. {e.Message}");
                }
            }
        }

        private async Task HandleAsync(PriceMessage message)
        {
            _priceMessagesCache[message.Instrument] = message;

            // Publish order books
            var orderBook = Convert(message);
            await _orderBookPublisher.PublishAsync(orderBook);

            var instrument = _withWithoutSuffixMapping[message.Instrument];
            _orderBooksCache[instrument] = orderBook;

            // Publish tock prices
            var tickPrice = TickPrice.FromOrderBook(orderBook);
            await _tickPricePublisher.PublishAsync(tickPrice);
        }

        private static string InstrumentWoSuffix(string instrument)
        {
            Debug.Assert(instrument.Contains(Suffix));

            return instrument.Replace(Suffix, "");
        }

        private OrderBook Convert(PriceMessage priceMessage)
        {
            var assetPair = _withWithoutSuffixMapping[priceMessage.Instrument];

            var bids = GetOrderBookItems(priceMessage.Levels.Sell);
            var asks = GetOrderBookItems(priceMessage.Levels.Buy);

            var result = new OrderBook(Source, assetPair, priceMessage.Timestamp, asks, bids);

            return result;
        }

        private static IEnumerable<OrderBookItem> GetOrderBookItems(IEnumerable<QuantityPrice> quantitiesPrices)
        {
            var result = new List<OrderBookItem>();

            foreach (var qp in quantitiesPrices)
                result.Add(new OrderBookItem((decimal)qp.Price, (decimal)qp.Quantity));

            return result;
        }
    }
}
