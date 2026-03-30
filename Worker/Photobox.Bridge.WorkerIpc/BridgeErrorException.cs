using System;

namespace Photobox.Bridge.WorkerIpc;

public sealed class BridgeErrorException : Exception
{
    public string ErrorCode { get; }

    public BridgeErrorException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
