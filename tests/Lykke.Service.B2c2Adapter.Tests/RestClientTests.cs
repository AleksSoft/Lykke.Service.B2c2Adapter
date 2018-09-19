﻿using Lykke.B2c2Client;
using Lykke.Logs;
using Xunit;

namespace Lykke.Service.B2c2Adapter.Tests
{
    public class RestClientTests
    {
        //[Fact]
        public async void BalanceTest()
        {
            var restClient = new B2c2RestClient("1bbe66d9dda462f37e7159c091e86994593b88d5", LogFactory.Create());

            var result = await restClient.GetBalanceAsync();
            
            Assert.NotEmpty(result);
            Assert.DoesNotContain("", result.Keys);
        }
    }
}
