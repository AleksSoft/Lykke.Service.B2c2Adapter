﻿using System;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace Lykke.Service.B2c2Adapter.Settings
{
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public class B2c2AdapterSettings
    {
        public string RestUrl { get; set; }

        public string WebSocketUrl { get; set; }

        public string AuthorizationToken { get; set; }

        public IReadOnlyList<InstrumentLevels> InstrumentLevels { get; set; } = new List<InstrumentLevels>();

        public TimeSpan PublishFromCacheInterval { get; set; }

        public DbSettings Db { get; set; }

        public RabbitMqSettings RawPriceRabbitMq { get; set; }
    }
}
