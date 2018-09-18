﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Common.Log;
using Lykke.B2c2Client.Exceptions;
using Lykke.B2c2Client.Models.Rest;
using Lykke.B2c2Client.Services;
using Lykke.Common.Log;
using Newtonsoft.Json;

namespace Lykke.B2c2Client
{
    public class B2C2RestClient : IB2c2RestService
    {
        private readonly string _authorizationToken;
        private readonly ILog _log;

        private static readonly HttpClient Client = new HttpClient {
            BaseAddress = new Uri("https://sandboxapi.b2c2.net/") };

        public B2C2RestClient(string authorizationToken, ILogFactory logFactory)
        {
            _authorizationToken = authorizationToken;
            Client.DefaultRequestHeaders.Add("Authorization", $"Token {_authorizationToken}");
            _log = logFactory.CreateLog(this);
        }

        public async Task<IReadOnlyDictionary<string, decimal>> GetBalance(CancellationToken ct = default(CancellationToken))
        {
            var requestId = Guid.NewGuid();
            _log.Info("balance - request", requestId);

            try
            {
                using (var response = await Client.GetAsync("balance/", ct))
                {
                    var status = response.StatusCode;
                    
                    var responseStr = await response.Content.ReadAsAsync<string>(ct);
                    _log.Info($"balance - response: {responseStr}", requestId);

                    EnsureNoErrorProperty(responseStr, status, requestId);

                    var result = JsonConvert.DeserializeObject<Dictionary<string, decimal>>(responseStr);

                    return result;
                }
            }
            catch (Exception e)
            {
                _log.Info($"balance - response exception: {e}", requestId);
                throw;
            }
        }

        public async Task<IReadOnlyCollection<Instrument>> GetInstruments(CancellationToken ct = default(CancellationToken))
        {
            var requestId = Guid.NewGuid();
            _log.Info("instruments - request", requestId);

            try
            {
                using (var response = await Client.GetAsync("instruments/", ct))
                {
                    var status = response.StatusCode;

                    var responseStr = await response.Content.ReadAsAsync<string>(ct);
                    _log.Info($"instruments - response: {responseStr}", requestId);

                    EnsureNoErrorProperty(responseStr, status, requestId);

                    var result = JsonConvert.DeserializeObject<IReadOnlyCollection<Instrument>>(responseStr);

                    return result;
                }
            }
            catch (Exception e)
            {
                _log.Info($"instruments - response exception: {e}", requestId);
                throw;
            }
        }

        public async Task<RequestForQuoteResponse> RequestForQuote(RequestForQuoteRequest requestForQuoteRequest, CancellationToken ct = default(CancellationToken))
        {
            if (requestForQuoteRequest == null) throw new ArgumentNullException(nameof(requestForQuoteRequest));

            var requestId = Guid.NewGuid();
            _log.Info($"request_for_quote - request: {JsonConvert.SerializeObject(requestForQuoteRequest)}", requestId);

            try
            {
                using (var response = await Client.PostAsJsonAsync("request_for_quote/", requestForQuoteRequest, ct))
                {
                    var status = response.StatusCode;

                    var responseStr = await response.Content.ReadAsAsync<string>(ct);
                    _log.Info($"request_for_quote - response: {responseStr}", requestId);

                    EnsureNoErrorProperty(responseStr, status, requestId);

                    var result = JsonConvert.DeserializeObject<RequestForQuoteResponse>(responseStr);

                    return result;
                }
            }
            catch (Exception e)
            {
                _log.Info($"request_for_quote - response exception: {e}", requestId);
                throw;
            }
        }

        public async Task<OrderResponse> PostOrder(OrderRequest orderRequest, CancellationToken ct = default(CancellationToken))
        {
            if (orderRequest == null) throw new ArgumentNullException(nameof(orderRequest));

            var requestId = Guid.NewGuid();
            _log.Info($"order - request: {JsonConvert.SerializeObject(orderRequest)}", requestId);

            try
            {
                using (var response = await Client.PostAsJsonAsync("order/", orderRequest, ct))
                {
                    var status = response.StatusCode;

                    var responseStr = await response.Content.ReadAsAsync<string>(ct);
                    _log.Info($"order - response: {responseStr}", requestId);

                    EnsureNoErrorProperty(responseStr, status, requestId);

                    var result = JsonConvert.DeserializeObject<OrderResponse>(responseStr);

                    return result;
                }
            }
            catch (Exception e)
            {
                _log.Info($"order - response exception: {e}", requestId);
                throw;
            }
        }

        private void EnsureNoErrorProperty(string response, HttpStatusCode status, Guid guid)
        {
            if (response.Contains("errors"))
            {
                ErrorResponse errorResponse = null;
                try
                {
                    errorResponse = JsonConvert.DeserializeObject<ErrorResponse>(response);
                    errorResponse.Status = status;
                }
                catch (Exception e)
                {
                    _log.Info($"Can't deserialize error response, status: {(int)status} {status.ToString()}, response: {response}", guid);
                }

                throw new B2c2Exception(errorResponse);
            }
        }
    }
}
