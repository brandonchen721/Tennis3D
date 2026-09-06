using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Tennis3D.Shared;

namespace Tennis3D.Client;

/// <summary>
/// Simple direct UDP client. It always connects to one known server endpoint.
/// There is intentionally no broadcast, multicast, adapter discovery, or secondary socket.
/// </summary>
public sealed class NetworkClient : IDisposable
{
    private readonly UdpClient udp;
    private readonly CancellationTokenSource cts = new();
    private readonly object sendLock = new();
    private readonly string clientId = Guid.NewGuid().ToString("N");
    private readonly string name;
    private readonly IPEndPoint server;
    private DateTime lastPacketUtc = DateTime.MinValue;
    private int disposed;

    public int PlayerId { get; private set; } = -1;
    public string MatchId { get; private set; } = "";
    public bool IsAccepted { get; private set; }
    public int RoundTripMilliseconds { get; private set; }
    public string ConnectionMessage { get; private set; }
    public volatile WorldState? Latest;

    public string ServerDisplay => $"{server.Address}:{server.Port}";

    public NetworkClient(string host, int port, string name)
    {
        string selectedHost = SelectHost(host);

        this.name = string.IsNullOrWhiteSpace(name)
            ? $"Player-{Environment.MachineName}"
            : name.Trim();

        server = new IPEndPoint(ResolveIPv4(selectedHost), port);
        ConnectionMessage = $"Connecting directly to {server.Address}:{server.Port}...";

        udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveBufferSize = 1_048_576;
        udp.Client.SendBufferSize = 262_144;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        // On Windows, prevent a transient ICMP "port unreachable" response from
        // permanently breaking ReceiveAsync while the server is starting/restarting.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                const int SioUdpConnReset = -1744830452;
                udp.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (SocketException) { }
            catch (PlatformNotSupportedException) { }
        }

        udp.Connect(server);
        Console.WriteLine($"[CLIENT NET] Protocol={GameConstants.ProtocolVersion}");
        Console.WriteLine($"[CLIENT NET] Local UDP endpoint: {udp.Client.LocalEndPoint}");
        Console.WriteLine($"[CLIENT NET] Target server: {server}");
        _ = ReceiveLoop();
        _ = ConnectionLoop();
    }

    public void Send<T>(PacketKind kind, T packet)
    {
        if (Volatile.Read(ref disposed) != 0) return;

        try
        {
            byte[] bytes = NetPacket.Pack(kind, packet);
            if (kind is PacketKind.Hello or PacketKind.Heartbeat or PacketKind.Ping or PacketKind.Disconnect)
                Console.WriteLine($"[CLIENT SEND] {DateTime.Now:HH:mm:ss.fff} {kind} -> {server} ({bytes.Length} bytes)");
            lock (sendLock)
            {
                udp.Send(bytes, bytes.Length);
            }
        }
        catch (SocketException ex)
        {
            IsAccepted = false;
            ConnectionMessage = $"Cannot reach {ServerDisplay}: {ex.SocketErrorCode}. Retrying...";
        }
        catch (ObjectDisposedException) { }
    }

    private async Task ReceiveLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await udp.ReceiveAsync(cts.Token);
                ProcessDatagram(result.Buffer);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException ex) when (!cts.IsCancellationRequested)
            {
                IsAccepted = false;
                ConnectionMessage = $"Network receive error: {ex.SocketErrorCode}. Retrying {ServerDisplay}...";
                try { await Task.Delay(300, cts.Token); } catch (OperationCanceledException) { }
            }
            catch (Exception ex) when (!cts.IsCancellationRequested)
            {
                ConnectionMessage = $"Rejected server packet: {ex.Message}";
            }
        }
    }

    private void ProcessDatagram(byte[] buffer)
    {
        (PacketKind kind, JsonElement payload) = NetPacket.Unpack(buffer);
        Console.WriteLine($"[CLIENT RECV] {DateTime.Now:HH:mm:ss.fff} {kind} <- {server} ({buffer.Length} bytes)");
        lastPacketUtc = DateTime.UtcNow;

        switch (kind)
        {
            case PacketKind.Welcome:
            {
                WelcomePacket welcome = payload.Deserialize<WelcomePacket>(NetPacket.JsonOptions)!;
                if (welcome.ProtocolVersion != GameConstants.ProtocolVersion)
                {
                    PlayerId = -1;
                    MatchId = "";
                    IsAccepted = false;
                    ConnectionMessage = $"Version mismatch. Client={GameConstants.ProtocolVersion}, server={welcome.ProtocolVersion}.";
                    Console.WriteLine($"[CLIENT ERROR] {ConnectionMessage}");
                    return;
                }

                PlayerId = welcome.PlayerId;
                MatchId = welcome.MatchId;
                IsAccepted = welcome.Accepted;
                if (welcome.Accepted) lastPacketUtc = DateTime.UtcNow;
                ConnectionMessage = welcome.Message;
                Console.WriteLine($"[CLIENT WELCOME] accepted={welcome.Accepted} player={welcome.PlayerId} match={welcome.MatchId} message=\"{welcome.Message}\"");
                break;
            }

            case PacketKind.State:
            {
                WorldState? state = payload.Deserialize<WorldState>(NetPacket.JsonOptions);
                if (IsAccepted && state is not null && (string.IsNullOrEmpty(MatchId) || state.MatchId == MatchId))
                {
                    Latest = state;
                    MatchId = state.MatchId;
                    ConnectionMessage = state.Message;
                }
                break;
            }

            case PacketKind.Pong:
            {
                PongPacket pong = payload.Deserialize<PongPacket>(NetPacket.JsonOptions)!;
                RoundTripMilliseconds = (int)Math.Max(0,
                    TimeSpan.FromTicks(DateTime.UtcNow.Ticks - pong.ClientTicks).TotalMilliseconds);
                break;
            }
        }
    }

    private async Task ConnectionLoop()
    {
        int pingCounter = 0;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (!IsAccepted)
                {
                    ConnectionMessage = $"Connecting directly to {ServerDisplay}...";
                    Send(PacketKind.Hello, new HelloPacket(clientId, name, GameConstants.ProtocolVersion));
                }
                else
                {
                    Send(PacketKind.Heartbeat, new HeartbeatPacket(clientId, DateTime.UtcNow.Ticks));
                    if (++pingCounter % 4 == 0)
                        Send(PacketKind.Ping, new PingPacket(DateTime.UtcNow.Ticks));

                    if (lastPacketUtc != DateTime.MinValue &&
                        DateTime.UtcNow - lastPacketUtc > TimeSpan.FromSeconds(5))
                    {
                        IsAccepted = false;
                        PlayerId = -1;
                        MatchId = "";
                        Latest = null;
                        ConnectionMessage = $"Server stopped responding. Reconnecting to {ServerDisplay}...";
                    }
                }

                await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException) { }
        }
    }

    private static string SelectHost(string? host)
    {
        if (!string.IsNullOrWhiteSpace(host) &&
            !host.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return host.Trim();

        string? environmentHost = Environment.GetEnvironmentVariable("TENNIS3D_SERVER_HOST");
        if (!string.IsNullOrWhiteSpace(environmentHost))
            return environmentHost.Trim();

        return string.IsNullOrWhiteSpace(GameConstants.DefaultServerIp)
            ? "localhost"
            : GameConstants.DefaultServerIp;
    }

    private static IPAddress ResolveIPv4(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetwork)
            return parsed;

        IPAddress? address = Dns.GetHostAddresses(host)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

        return address ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        try
        {
            byte[] bytes = NetPacket.Pack(PacketKind.Disconnect,
                new DisconnectPacket(clientId, "client window closed"));
            lock (sendLock) udp.Send(bytes, bytes.Length);
        }
        catch { }

        cts.Cancel();
        udp.Dispose();
        cts.Dispose();
    }
}
