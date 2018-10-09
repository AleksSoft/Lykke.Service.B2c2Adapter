﻿using System;
using Lykke.B2c2Client.Models.WebSocket;

namespace Lykke.B2c2Client.Exceptions
{
    public class B2c2WebSocketException : Exception
    {
        public ErrorResponse ErrorResponse { get; }

        public B2c2WebSocketException(string message, Exception e) : base(message, e)
        {
        }

        public B2c2WebSocketException(string message) : base(message)
        {
        }

        public B2c2WebSocketException(string message, ErrorResponse errorResponse) : base(message)
        {
            ErrorResponse = errorResponse;
        }
    }
}
