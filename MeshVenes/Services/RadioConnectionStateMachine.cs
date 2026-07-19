using System;

namespace MeshVenes.Services;

public enum RadioConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Failed
}

public sealed record RadioConnectionState(
    RadioConnectionStatus Status,
    string? Endpoint = null,
    string? ErrorMessage = null,
    long Revision = 0)
{
    public bool IsConnected => Status == RadioConnectionStatus.Connected;

    public bool IsBusy => Status is
        RadioConnectionStatus.Connecting or
        RadioConnectionStatus.Reconnecting or
        RadioConnectionStatus.Disconnecting;

    public bool CanConnect => Status is
        RadioConnectionStatus.Disconnected or
        RadioConnectionStatus.Failed;

    public bool CanDisconnect => Status is
        RadioConnectionStatus.Connecting or
        RadioConnectionStatus.Connected or
        RadioConnectionStatus.Reconnecting;
}

public sealed class RadioConnectionStateMachine
{
    private readonly object _sync = new();
    private RadioConnectionState _current = new(RadioConnectionStatus.Disconnected);

    public RadioConnectionState Current
    {
        get
        {
            lock (_sync)
                return _current;
        }
    }

    public event Action<RadioConnectionState>? Changed;

    public RadioConnectionState TransitionTo(
        RadioConnectionStatus status,
        string? endpoint = null,
        string? errorMessage = null)
    {
        RadioConnectionState next;
        bool changed;

        lock (_sync)
            (next, changed) = TransitionToLocked(status, endpoint, errorMessage);

        if (changed)
            Changed?.Invoke(next);
        return next;
    }

    public bool TryTransitionFrom(
        RadioConnectionStatus expectedStatus,
        RadioConnectionStatus status,
        string? endpoint = null,
        string? errorMessage = null)
    {
        RadioConnectionState next;
        bool changed;

        lock (_sync)
        {
            if (_current.Status != expectedStatus)
                return false;

            (next, changed) = TransitionToLocked(status, endpoint, errorMessage);
        }

        if (changed)
            Changed?.Invoke(next);
        return true;
    }

    private (RadioConnectionState State, bool Changed) TransitionToLocked(
        RadioConnectionStatus status,
        string? endpoint,
        string? errorMessage)
    {
        if (!IsTransitionAllowed(_current.Status, status))
        {
            throw new InvalidOperationException(
                $"Invalid radio connection transition: {_current.Status} -> {status}.");
        }

        var nextEndpoint = status == RadioConnectionStatus.Disconnected
            ? null
            : string.IsNullOrWhiteSpace(endpoint)
                ? _current.Endpoint
                : endpoint.Trim();

        var nextError = status is RadioConnectionStatus.Failed or RadioConnectionStatus.Reconnecting
            ? string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim()
            : null;

        if (_current.Status == status &&
            string.Equals(_current.Endpoint, nextEndpoint, StringComparison.Ordinal) &&
            string.Equals(_current.ErrorMessage, nextError, StringComparison.Ordinal))
        {
            return (_current, false);
        }

        var next = new RadioConnectionState(
            status,
            nextEndpoint,
            nextError,
            _current.Revision + 1);
        _current = next;
        return (next, true);
    }

    public static bool IsTransitionAllowed(RadioConnectionStatus from, RadioConnectionStatus to)
    {
        if (from == to)
            return true;

        return from switch
        {
            RadioConnectionStatus.Disconnected => to is
                RadioConnectionStatus.Connecting or
                RadioConnectionStatus.Reconnecting,

            RadioConnectionStatus.Connecting => to is
                RadioConnectionStatus.Connected or
                RadioConnectionStatus.Disconnecting or
                RadioConnectionStatus.Disconnected or
                RadioConnectionStatus.Failed,

            RadioConnectionStatus.Connected => to is
                RadioConnectionStatus.Reconnecting or
                RadioConnectionStatus.Disconnecting or
                RadioConnectionStatus.Failed,

            RadioConnectionStatus.Reconnecting => to is
                RadioConnectionStatus.Connected or
                RadioConnectionStatus.Disconnecting or
                RadioConnectionStatus.Disconnected or
                RadioConnectionStatus.Failed,

            RadioConnectionStatus.Disconnecting => to is
                RadioConnectionStatus.Disconnected or
                RadioConnectionStatus.Reconnecting or
                RadioConnectionStatus.Failed,

            RadioConnectionStatus.Failed => to is
                RadioConnectionStatus.Connecting or
                RadioConnectionStatus.Reconnecting or
                RadioConnectionStatus.Disconnecting or
                RadioConnectionStatus.Disconnected,

            _ => false
        };
    }
}
