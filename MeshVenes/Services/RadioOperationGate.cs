using System;

namespace MeshVenes.Services;

public sealed record RadioOperationAvailability(
    bool IsAllowed,
    string? UnavailableMessage = null);

public static class RadioOperationGate
{
    public const string DisconnectedMessage = "Connect to a radio before continuing.";
    public const string ConnectingMessage = "Wait for the radio connection to finish.";
    public const string ReconnectingMessage = "Wait for the radio to reconnect.";
    public const string DisconnectingMessage = "Wait for the radio to disconnect, then connect again.";
    public const string FailedMessage = "Reconnect to the radio before continuing.";
    public const string MissingConnectedNodeIdentityMessage =
        "Wait for the connected node identity before continuing.";

    private static readonly RadioOperationAvailability Allowed = new(true);

    public static RadioOperationAvailability Evaluate(
        RadioConnectionState connectionState,
        bool requiresConnectedNodeIdentity = false,
        bool hasConnectedNodeIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(connectionState);

        var unavailableMessage = connectionState.Status switch
        {
            RadioConnectionStatus.Disconnected => DisconnectedMessage,
            RadioConnectionStatus.Connecting => ConnectingMessage,
            RadioConnectionStatus.Reconnecting => ReconnectingMessage,
            RadioConnectionStatus.Disconnecting => DisconnectingMessage,
            RadioConnectionStatus.Failed => FailedMessage,
            RadioConnectionStatus.Connected => null,
            _ => DisconnectedMessage
        };

        if (unavailableMessage is not null)
            return new RadioOperationAvailability(false, unavailableMessage);

        if (requiresConnectedNodeIdentity && !hasConnectedNodeIdentity)
        {
            return new RadioOperationAvailability(
                false,
                MissingConnectedNodeIdentityMessage);
        }

        return Allowed;
    }
}
