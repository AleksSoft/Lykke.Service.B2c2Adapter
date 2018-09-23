﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Log;
using Lykke.B2c2Client.Exceptions;
using Lykke.B2c2Client.Models.WebSocket;
using Lykke.Common.Log;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lykke.B2c2Client
{
    public class B2c2WebSocketClient : IB2c2WebSocketClient
    {
        private readonly TimeSpan _timeOut = new TimeSpan(0, 0, 0, 30);
        private readonly string _baseUri;
        private readonly string _authorizationToken;
        private readonly ILog _log;
        private ClientWebSocket _clientWebSocket;
        private readonly object _sync = new object();
        private readonly ConcurrentDictionary<string, Subscription> _awaitingSubscription;
        private readonly ConcurrentDictionary<string, Func<PriceMessage, Task>> _instrumentsHandlers;
        private readonly ConcurrentDictionary<string, Subscription> _awaitingUnsubscription;
        private readonly IList<string> _tradableInstruments;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public B2c2WebSocketClient(string url, string authorizationToken, ILogFactory logFactory)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
                throw new ArgumentOutOfRangeException(nameof(url));
            if (string.IsNullOrWhiteSpace(authorizationToken)) throw new ArgumentOutOfRangeException(nameof(authorizationToken));
            if (logFactory == null) throw new NullReferenceException(nameof(logFactory));

            _baseUri = url[url.Length-1] == '/' ? url.Substring(0, url.Length - 1) : url;
            _authorizationToken = authorizationToken;
            _log = logFactory.CreateLog(this);
            _clientWebSocket = new ClientWebSocket();
            _awaitingSubscription = new ConcurrentDictionary<string, Subscription>();
            _instrumentsHandlers = new ConcurrentDictionary<string, Func<PriceMessage, Task>>();
            _awaitingUnsubscription = new ConcurrentDictionary<string, Subscription>();
            _tradableInstruments = new List<string>();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task SubscribeAsync(string instrument, int[] levels, Func<PriceMessage, Task> handler,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(instrument)) throw new ArgumentOutOfRangeException(nameof(instrument));
            if (levels.Length < 1 || levels.Length > 2) throw new ArgumentOutOfRangeException($"{nameof(levels)}. Minimum levels - 1, maximum - 2.");
            if (handler == null) throw new NullReferenceException(nameof(handler));

            var tag = Guid.NewGuid().ToString();
            lock (_sync)
            {
                if (_awaitingSubscription.ContainsKey(instrument)
                    || _instrumentsHandlers.ContainsKey(instrument))
                    throw new B2c2WebSocketException($"Subscription to {instrument} is already exists.");
            }

            if (_clientWebSocket.State == WebSocketState.None)
                Connect(ct);

            _log.Info($"Attempt to subscribe to order book updates, tag: {instrument}.");

            var subscribeRequest = new SubscribeRequest { Instrument = instrument, Levels = levels, Tag = tag };
            var request = StringToArraySegment(JsonConvert.SerializeObject(subscribeRequest));
            await _clientWebSocket.SendAsync(request, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            var taskCompletionSource = new TaskCompletionSource<int>();
            lock (_sync)
            {
                _awaitingSubscription[instrument] = new Subscription(tag, taskCompletionSource, handler);
            }

            await Task.Delay(_timeOut, ct);
            if (!ct.IsCancellationRequested)
            {
                lock (_sync)
                {
                    _awaitingSubscription.TryRemove(instrument, out _);
                }
                taskCompletionSource.TrySetException(new B2c2WebSocketException("Timeout."));
            }
        }

        public async Task UnsubscribeAsync(string instrument, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(instrument)) throw new NullReferenceException(nameof(instrument));

            var tag = Guid.NewGuid().ToString();
            lock (_sync)
            {
                if (!_instrumentsHandlers.ContainsKey(instrument))
                    throw new B2c2WebSocketException($"Subscription to {instrument} does not exist.");
            }

            if (_clientWebSocket.State != WebSocketState.Open)
                throw new B2c2WebSocketException($"WebSocketState is not 'Open' - {_clientWebSocket.State}.");

            _log.Info($"Attempt to unsubscribe from order book updates, tag: {instrument}.");

            var unsubscribeRequest = new UnsubscribeRequest { Instrument = instrument, Tag = tag };
            var request = StringToArraySegment(JsonConvert.SerializeObject(unsubscribeRequest));
            await _clientWebSocket.SendAsync(request, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            var taskCompletionSource = new TaskCompletionSource<int>();
            lock (_sync)
            {
                _awaitingUnsubscription[instrument] = new Subscription(tag, taskCompletionSource);
            }

            await Task.Delay(_timeOut, ct);
            if (!ct.IsCancellationRequested)
            {
                lock (_sync)
                {
                    _awaitingUnsubscription.TryRemove(instrument, out _);
                }
                taskCompletionSource.TrySetException(new B2c2WebSocketException("Timeout."));
            }
        }

        public async Task DisconnectAsync(CancellationToken ct = default(CancellationToken))
        {
            _log.Info("Attempt to close a WebSocket connection.");

            if (_clientWebSocket != null && _clientWebSocket.State == WebSocketState.Open)
            {
                await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal closure.", ct);
            }

            _awaitingSubscription.Clear();
            _instrumentsHandlers.Clear();
            _tradableInstruments.Clear();

            _log.Info("Connection to WebSocket was sucessfuly closed.");
        }

        private void Connect(CancellationToken ct = default(CancellationToken))
        {
            _log.Info("Attempt to establish a WebSocket connection.");

            _clientWebSocket.Options.SetRequestHeader("Authorization", $"Token {_authorizationToken}");
            _clientWebSocket.ConnectAsync(new Uri($"{_baseUri}/quotes"), ct).ConfigureAwait(false).GetAwaiter().GetResult();

            if (_clientWebSocket.State != WebSocketState.Open)
                throw new Exception($"Could not establish WebSocket connection to {_baseUri}.");

            // Listen for messages in separate io thread
            Task.Run(async () =>
                {
                    await HandleMessagesCycleAsync(_cancellationTokenSource.Token);
                }, _cancellationTokenSource.Token)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _log.Error(t.Exception, "Something went wrong in subscription thread.");
                }, default(CancellationToken));
        }

        private async Task HandleMessagesCycleAsync(CancellationToken ct)
        {
            while (_clientWebSocket.State == WebSocketState.Open)
            {
                using (var stream = new MemoryStream(8192))
                {
                    var receiveBuffer = new ArraySegment<byte>(new byte[1024]);
                    WebSocketReceiveResult receiveResult;
                    do
                    {
                        receiveResult = await _clientWebSocket.ReceiveAsync(receiveBuffer, ct);
                        await stream.WriteAsync(receiveBuffer.Array, receiveBuffer.Offset, receiveResult.Count, ct);
                    } while (!receiveResult.EndOfMessage);

                    var messageBytes = stream.ToArray();
                    var jsonMessage = Encoding.UTF8.GetString(messageBytes, 0, messageBytes.Length);

                    HandleWebSocketMessageAsync(jsonMessage);
                }
            }
        }

        private void HandleWebSocketMessageAsync(string jsonMessage)
        {
            var jToken = JToken.Parse(jsonMessage);
            var type = jToken["event"]?.Value<string>();

            switch (type)
            {
                case "tradable_instruments":
                    HandleTradableInstrumentMessage(jToken);
                    break;
                case "subscribe":
                    HandleSubscribeMessage(jToken);
                    break;
                case "price":
                    HandlePriceMessage(jToken);
                    break;
                case "unsubscribe":
                    HandleUnsubscribeMessage(jToken);
                    break;
            }
        }

        private void HandleTradableInstrumentMessage(JToken jToken)
        {
            if (jToken["success"]?.Value<bool>() == false)
            {
                _log.Error($"{nameof(ConnectResponse)}.{nameof(ConnectResponse.Success)} == false. {jToken}");
                return;
            }

            var result = jToken.ToObject<ConnectResponse>();
            foreach (var instrument in result.Instruments)
                _tradableInstruments.Add(instrument);
        }

        private void HandleSubscribeMessage(JToken jToken)
        {
            var instrument = jToken["instrument"].Value<string>();
            var tag = jToken["tag"].Value<string>();
            if (jToken["success"]?.Value<bool>() == false)
            {
                var message = $"{nameof(SubscribeMessage)}.{nameof(SubscribeMessage.Success)} == false. {jToken}";
                lock (_sync)
                {
                    _awaitingSubscription.Remove(instrument, out var value);
                    if (tag != value.Tag)
                        value.TaskCompletionSource.TrySetException(new InvalidOperationException($"Tags are not the same: {tag}, {value.Tag}."));
                    value.TaskCompletionSource.TrySetException(new B2c2WebSocketException(message));
                }

                return;
            }
            
            var result = jToken.ToObject<SubscribeMessage>();
            lock (_sync)
            {
                if (!_awaitingSubscription.ContainsKey(instrument))
                    _log.Error($"Subscriptions doesn't have element with {result.Instrument}.");

                _awaitingSubscription.Remove(instrument, out var subscription);
                
                if (_instrumentsHandlers.ContainsKey(result.Instrument))
                    subscription.TaskCompletionSource.TrySetException(new B2c2WebSocketException($"Attempt to second subscription to {result.Instrument}."));

                _instrumentsHandlers[instrument] = subscription.Function;                
            }
        }

        private void HandlePriceMessage(JToken jToken)
        {
            if (jToken["success"]?.Value<bool>() == false)
            {
                _log.Error($"{nameof(SubscribeMessage)}.{nameof(SubscribeMessage.Success)} == false. {jToken}");
                return;
            }

            var result = jToken.ToObject<PriceMessage>();
            lock (_sync)
            {
                var handler = _instrumentsHandlers[result.Instrument];
                handler(result);
            }
        }

        private void HandleUnsubscribeMessage(JToken jToken)
        {
            var instrument = jToken["instrument"].Value<string>();
            var tag = jToken["tag"].Value<string>();
            if (jToken["success"]?.Value<bool>() == false)
            {
                var message = $"{nameof(UnsubscribeMessage)}.{nameof(UnsubscribeMessage.Success)} == false. {jToken}";
                lock (_sync)
                {
                    _instrumentsHandlers.Remove(instrument, out _);
                    _awaitingUnsubscription.Remove(instrument, out var value);
                    if (tag != value.Tag)
                        value.TaskCompletionSource.TrySetException(new InvalidOperationException($"Tags are not the same: {tag}, {value.Tag}."));
                    value.TaskCompletionSource.TrySetException(new B2c2WebSocketException(message));
                }

                return;
            }

            var result = jToken.ToObject<UnsubscribeMessage>();
            lock (_sync)
            {
                if (!_awaitingUnsubscription.ContainsKey(instrument))
                    _log.Error($"Can't unsubscribe from '{instrument}', subscription does not exist. {jToken}");

                _awaitingUnsubscription.Remove(instrument, out var subscription);

                if (_instrumentsHandlers.ContainsKey(result.Instrument))
                    subscription.TaskCompletionSource.TrySetException(new B2c2WebSocketException($"Attempt to second subscription to {result.Instrument}."));

                _instrumentsHandlers.Remove(instrument, out _);
            }
        }

        private static ArraySegment<byte> StringToArraySegment(string message)
        {
            var messageBytes = Encoding.UTF8.GetBytes(message);
            var messageArraySegment = new ArraySegment<byte>(messageBytes);
            return messageArraySegment;
        }

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~B2c2WebSocketClient()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            
            if (_clientWebSocket != null)
            {
                _clientWebSocket.Abort();
                _clientWebSocket.Dispose();
                _clientWebSocket = null;
            }

            if (_cancellationTokenSource != null && _cancellationTokenSource.Token.CanBeCanceled)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }
        }

        #endregion

        private class Subscription
        {
            public string Tag { get; }

            public TaskCompletionSource<int> TaskCompletionSource { get; }

            public Func<PriceMessage, Task> Function { get; }

            public Subscription(string tag, TaskCompletionSource<int> taskCompletionSource, Func<PriceMessage, Task> function)
            {
                Tag = tag;
                TaskCompletionSource = taskCompletionSource;
                Function = function;
            }

            public Subscription(string tag, TaskCompletionSource<int> taskCompletionSource)
            {
                Tag = tag;
                TaskCompletionSource = taskCompletionSource;
            }
        }
    }
}
