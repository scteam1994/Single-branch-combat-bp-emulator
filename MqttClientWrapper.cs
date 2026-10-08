using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace BPTrainer;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

public class MqttClientWrapper : IDisposable
{
    private IMqttClient? _client;
    private string _roomId = "";
    private string _topic = "";
    private string _clientId = "";
    private string _mqttClientId = "";
    private bool _disposed = false;
    private CancellationTokenSource? _reconnectCts;

    public event Action<ConnectionState>? ConnectionStateChanged;
    public event Action<BpAction>? ActionReceived;
    public event Action? SnapshotRequested;
    public event Action<BpStateSnapshot>? SnapshotReceived;
    public event Action<string, BpSide>? PlayerJoined;
    public event Action? StartReceived;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string RoomId => _roomId;

    public async Task ConnectAsync(string roomId)
    {
        _roomId = roomId;
        _topic = $"bp/training/room_{roomId}";
        _clientId = Guid.NewGuid().ToString("N")[..8];
        _mqttClientId = $"bp_trainer_{_clientId}";

        await DisconnectInternalAsync();

        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.Array!,
                    e.ApplicationMessage.PayloadSegment.Offset,
                    e.ApplicationMessage.PayloadSegment.Count);

                var msg = JsonSerializer.Deserialize<MqttMessage>(json);
                if (msg == null) return;

                switch (msg.Type)
                {
                    case "action":
                        if (msg.Action != null && msg.SenderId != _clientId)
                            ActionReceived?.Invoke(msg.Action);
                        break;
                    case "join":
                        if (msg.SenderId != _clientId && msg.Side.HasValue)
                            PlayerJoined?.Invoke(msg.SenderId, msg.Side.Value);
                        break;
                    case "snapshot_request":
                        if (msg.SenderId != _clientId)
                            SnapshotRequested?.Invoke();
                        break;
                    case "snapshot":
                        if (msg.Snapshot != null && msg.SenderId != _clientId)
                            SnapshotReceived?.Invoke(msg.Snapshot);
                        break;
                    case "start":
                        if (msg.SenderId != _clientId)
                            StartReceived?.Invoke();
                        break;
                }
            }
            catch { }
        };

        _client.DisconnectedAsync += async e =>
        {
            State = ConnectionState.Disconnected;
            ConnectionStateChanged?.Invoke(State);

            if (!_disposed && _reconnectCts != null && !_reconnectCts.IsCancellationRequested)
            {
                await Task.Delay(3000);
                if (!_disposed && _reconnectCts != null && !_reconnectCts.IsCancellationRequested)
                {
                    await ConnectInternalAsync();
                }
            }
        };

        await ConnectInternalAsync();
    }

    private async Task ConnectInternalAsync()
    {
        if (_client == null) return;

        try
        {
            State = ConnectionState.Connecting;
            ConnectionStateChanged?.Invoke(State);

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("broker.hivemq.com", 1883)
                .WithClientId(_mqttClientId)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithTimeout(TimeSpan.FromSeconds(10))
                .WithCleanSession()
                .Build();

            await _client.ConnectAsync(options, CancellationToken.None);

            await _client.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic(_topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());

            State = ConnectionState.Connected;
            ConnectionStateChanged?.Invoke(State);

            await Task.Delay(500);
            await RequestSnapshotAsync();
        }
        catch
        {
            State = ConnectionState.Disconnected;
            ConnectionStateChanged?.Invoke(State);
        }
    }

    public async Task SendActionAsync(BpAction action)
    {
        if (_client == null || !_client.IsConnected) return;

        action.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var msg = new MqttMessage
        {
            Type = "action",
            SenderId = _clientId,
            Action = action
        };

        await PublishAsync(msg);
    }

    public async Task SendJoinAsync(BpSide side)
    {
        if (_client == null || !_client.IsConnected) return;

        var msg = new MqttMessage
        {
            Type = "join",
            SenderId = _clientId,
            Side = side
        };

        await PublishAsync(msg);
    }

    public async Task RequestSnapshotAsync()
    {
        if (_client == null || !_client.IsConnected) return;

        var msg = new MqttMessage { Type = "snapshot_request", SenderId = _clientId };
        await PublishAsync(msg);
    }

    public async Task SendStartAsync()
    {
        if (_client == null || !_client.IsConnected) return;

        var msg = new MqttMessage
        {
            Type = "start",
            SenderId = _clientId
        };

        await PublishAsync(msg);
    }

    public async Task SendSnapshotAsync(BpStateSnapshot snapshot)
    {
        if (_client == null || !_client.IsConnected) return;

        var msg = new MqttMessage
        {
            Type = "snapshot",
            SenderId = _clientId,
            Snapshot = snapshot
        };

        await PublishAsync(msg);
    }

    private async Task PublishAsync(MqttMessage msg)
    {
        if (_client == null || !_client.IsConnected) return;

        var json = JsonSerializer.Serialize(msg);
        var payload = Encoding.UTF8.GetBytes(json);

        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic(_topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(applicationMessage, CancellationToken.None);
    }

    private async Task DisconnectInternalAsync()
    {
        _reconnectCts?.Cancel();

        if (_client != null && _client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync();
            }
            catch { }
        }

        _client?.Dispose();
        _client = null;
    }

    public async Task DisconnectAsync()
    {
        _disposed = true;
        await DisconnectInternalAsync();
        State = ConnectionState.Disconnected;
        ConnectionStateChanged?.Invoke(State);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _client?.Dispose();
    }
}

public class MqttMessage
{
    public string Type { get; set; } = "";
    public string SenderId { get; set; } = "";
    public BpSide? Side { get; set; }
    public BpAction? Action { get; set; }
    public BpStateSnapshot? Snapshot { get; set; }
}
