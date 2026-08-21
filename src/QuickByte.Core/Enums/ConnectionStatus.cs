namespace QuickByte.Core.Enums;

/// <summary>
/// State of a single chunk connection, as displayed in the
/// Download Details window connections ListView.
/// </summary>
public enum ConnectionStatus
{
    Idle,
    SendingRequest,
    ReceivingData,
    Finished,
    Failed,
    Paused
}
