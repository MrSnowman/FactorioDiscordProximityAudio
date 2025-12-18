using System.Collections.Concurrent;
using System.Text;
using Client.Models;
using ENet;
using Serilog;

namespace Client.Services;

public sealed class PositionTransferHostService : IService
{
    public const  int StartingPort   = 8970;
    public const  int CheckPortCount = 1029;
    private const int MaxClients     = 128;

    private bool ShuttingDown { get; set; }

    private Host?                              Server               { get; set; }
    private VolumeUpdaterService?              PlayerTrackerService { get; set; }
    private ConcurrentDictionary<uint, string> PeerToDiscordId      { get; } = [];
    private Thread?                            ENetThread           { get; set; }

    private readonly HashSet<Peer> _connectedPeers = [];

    public Task<bool> StartAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            PlayerTrackerService = services.GetService(typeof(VolumeUpdaterService)) as VolumeUpdaterService;

            Log.Information("Starting position transfer server...");

            Server = new Host();
            var address = new Address
            {
                Port = Main.targetPort
            };

            Server.Create(address, MaxClients, Enum.GetValues<ChannelType>().Length);

            ENetThread = new Thread(MainLoop);
            ENetThread.Start();

            Log.Information("Listening for connections on port {TargetPort}.", Main.targetPort);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Error opening websocket: {Message}", e.Message);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private void MainLoop()
    {
        while (Server is { IsSet: true } && !ShuttingDown)
        {
            // Keep processing events until ...
            if (Server.CheckEvents(out var netEvent) <= 0)
            {
                // No events to process, look for packets to turn into events
                // ENet runs on a single thread, if timeout is 0, it'll use 100% of the CPU
                if (Server.Service(15, out netEvent) <= 0)
                    continue;
            }

            switch (netEvent.Type)
            {
                case EventType.None:
                    break;

                case EventType.Connect:
                    OnClientConnected(ref netEvent);
                    break;

                case EventType.Disconnect:
                    OnClientDisconnected(ref netEvent);
                    break;

                case EventType.Timeout:
                    Log.Warning("Client timeout - ID: {ID}, IP: {IP}", netEvent.Peer.ID, netEvent.Peer.IP);
                    break;

                case EventType.Receive:
                    OnMessageReceived(ref netEvent);
                    netEvent.Packet.Dispose();
                    break;
            }
        }
    }

    private void OnClientConnected(ref Event netEvent)
    {
        _connectedPeers.Add(netEvent.Peer);
        Log.Information("Client {ID} connected.", netEvent.Peer.ID);
    }

    private void OnClientDisconnected(ref Event netEvent)
    {
        _connectedPeers.Remove(netEvent.Peer);
        if (!PeerToDiscordId.Remove(netEvent.Peer.ID, out var discordId) || string.IsNullOrEmpty(discordId))
            return;

        Log.Information("Client {ValueDiscordId} disconnected.", discordId);
        QueueClientDisconnectedBroadcast(netEvent.Peer.ID);
    }

    private unsafe void OnMessageReceived(ref Event netEvent)
    {
        var buffer = new Span<byte>((byte*)netEvent.Packet.Data, netEvent.Packet.Length);
        var opCode = (ChannelType)netEvent.ChannelID;

        switch (opCode)
        {
            case ChannelType.Identify:
            {
                var discordId = DiscordUtility.GetUid(Encoding.ASCII.GetString(buffer));

                PeerToDiscordId[netEvent.Peer.ID] = discordId;
                QueueInitializePlayers(discordId, netEvent.Peer);
                QueueBroadcastNewPlayer(discordId, netEvent.Peer);
                Log.Information("Client {DiscordId} connected.", discordId);
                break;
            }
            case ChannelType.Position:
            {
                BroadcastPositionPacket(buffer, netEvent.Peer);
                break;
            }
        }
    }

    private void QueueBroadcastNewPlayer(string discordId, Peer netEventPeer)
    {
        if (Server is not { IsSet: true })
            return;

        var discordIdBytes = Encoding.ASCII.GetBytes(DiscordUtility.GetFixedLengthUid(discordId));

        using var ms = new MemoryStream(sizeof(uint) + DiscordUtility.MaxUidLength);
        using (var binaryWriter = new BinaryWriter(ms))
        {
            binaryWriter.Write(netEventPeer.ID);
            binaryWriter.Write(discordIdBytes);
        }

        var packet = new Packet();
        // Use ToArray() to ensure only the written bytes are sent (avoids trailing/unused buffer data).
        packet.Create(ms.ToArray(), PacketFlags.Reliable);
        Server.Broadcast((byte)ChannelType.Identify, ref packet);
    }

    private void QueueInitializePlayers(string discordId, Peer connection)
    {
        if (Server is not { IsSet: true })
            return;

        using var ms = new MemoryStream(sizeof(uint) + DiscordUtility.MaxUidLength);
        using (var binaryWriter = new BinaryWriter(ms))
        {
            foreach (var client in PeerToDiscordId)
            {
                if (client.Value == discordId)
                    continue;

                binaryWriter.Write(client.Key);
                binaryWriter.Write(Encoding.ASCII.GetBytes(DiscordUtility.GetFixedLengthUid(client.Value)));
            }
        }

        var packet = new Packet();
        // Use ToArray() so we only send the bytes actually written to the stream.
        packet.Create(ms.ToArray(), PacketFlags.Reliable);
        connection.Send((byte)ChannelType.Identify, ref packet);
    }

    public void BroadcastPositionPacket(Span<byte> data, Peer sender)
    {
        if (Server is not { IsSet: true })
            return;

        using var ms = new MemoryStream(data.Length + sizeof(uint));
        using (var binaryWriter = new BinaryWriter(ms))
        {
            binaryWriter.Write(data);
            binaryWriter.Write(sender.ID);
        }

        var packet = new Packet();
        // Use ToArray() to avoid trailing bytes from internal buffer.
        packet.Create(ms.ToArray());

        Server.Broadcast((byte)ChannelType.Position, ref packet, sender);

        if (Main.useVerboseLogging)
            Log.Information("Broadcasted position from {SenderID}.", sender.ID);
    }

    private void QueueClientDisconnectedBroadcast(uint senderId)
    {
        if (Server is not { IsSet: true })
            return;

        var packet = new Packet();
        packet.Create(BitConverter.GetBytes(senderId));
        Server.Broadcast((byte)ChannelType.Disconnect, ref packet);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            ShuttingDown = true;

            while (ENetThread is { IsAlive: true })
                await Task.Delay(15, cancellationToken);

            if (Server != null)
            {
                Server.PreventConnections(true);

                foreach (var peer in _connectedPeers)
                {
                    peer.Disconnect(0);
                    Log.Information("");
                }

                Server.Flush();
                Server?.Dispose();
                Server = null;
            }

            Log.Information("Terminated proximity audio host.");
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Error terminating proximity audio host: {Message}", e.Message);
        }
        finally
        {
            ShuttingDown = false;
        }
    }
}
