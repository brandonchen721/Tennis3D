using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tennis3D.Shared;

public enum PacketKind { Hello, Welcome, Heartbeat, Input, State, Disconnect, Ping, Pong }
public enum HandedSwing { None, Left, Right }
public enum MatchPhase { Waiting, ServeSetup, ServeToss, Rally, PointOver, ChangeEnds, MatchOver }

public sealed record NetPacket(PacketKind Kind, string Payload)
{
    public static byte[] Pack<T>(PacketKind kind, T value) =>
        JsonSerializer.SerializeToUtf8Bytes(new NetPacket(kind, JsonSerializer.Serialize(value, JsonOptions)), JsonOptions);

    public static (PacketKind kind, JsonElement payload) Unpack(ReadOnlySpan<byte> data)
    {
        NetPacket packet = JsonSerializer.Deserialize<NetPacket>(data, JsonOptions)
            ?? throw new InvalidDataException("Invalid network packet.");
        using JsonDocument document = JsonDocument.Parse(packet.Payload);
        return (packet.Kind, document.RootElement.Clone());
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record HelloPacket(string ClientId, string Name, int ProtocolVersion);
public sealed record WelcomePacket(int PlayerId, bool Accepted, string Message, string MatchId, int ProtocolVersion);
public sealed record HeartbeatPacket(string ClientId, long ClientTicks);
public sealed record DisconnectPacket(string ClientId, string Reason);
public sealed record PingPacket(long ClientTicks);
public sealed record PongPacket(long ClientTicks, long ServerTicks);

public sealed record InputPacket(
    long Sequence,
    float MoveX,
    float MoveZ,
    float AimYaw,
    float AimPitch,
    bool SwingHeld,
    bool SwingReleased,
    HandedSwing Hand,
    float SwingStartHeight,
    bool RacquetBehind,
    float RacquetHorizontal,
    float RacquetFaceAngle,
    float MouseSwingSpeed,
    float MouseSwingVerticalSpeed,
    bool ServeTossRequested,
    float ServeTossHeight,
    float ServeTossX,
    bool JumpRequested = false);

public sealed class PlayerState
{
    public int Id;
    public string Name = "Player";
    public Vector3 Position;
    public Vector3 Velocity;
    public float FacingYaw;
    public bool Swinging;
    public float SwingProgress;
    public HandedSwing SwingHand = HandedSwing.Right;
    public float SwingHeight = 1.05f;
    public bool RacquetBehind = true;
    public float RacquetHorizontal;
    // Radians of roll around the racquet handle/shaft axis.
    public float RacquetFaceAngle;
    public bool SwingReleasedVisual;
    public float SwingVisualTimer;
    public float SwingPower;
    public long LastInputSequence;
    public long LastJumpSequence;
    public bool Grounded = true;
}

public sealed class MinionState
{
    public int Id;
    public int OwnerId = 1;
    public Vector3 Position;
    public bool Swinging;
    public float SwingProgress;
    public HandedSwing SwingHand = HandedSwing.Right;
    public float SwingHeight = 0.90f;
    public float RacquetFaceAngle = -0.35f;
}

public sealed class BallState
{
    public Vector3 Position = new(0, 1.2f, 0);
    public Vector3 Velocity;
    public Vector3 AngularVelocity;
    public int LastHitBy = -1;
    public int BounceCount;
    public bool InPlay;
    public bool IsServeToss;
    public int TossOwner = -1;
    public float SelectedTossHeight = 3.0f;
    public float SelectedTossX;
    // -1 targets the world-X left service box; +1 targets world-X right.
    public int ServeTargetXSign;
    public bool TossReachedApex;
    public bool ServeMustBounce;
    public int ServeBounceCount;
}

public sealed class ScoreState
{
    public int[] Points = new int[2];
    public int[] Games = new int[2];
    public int[] Sets = new int[2];
    public int Server;
    public int PointWinner = -1;
    public int ServeNumber = 1;
    public bool TieBreak;
    public string Display = "Sets 0-0  Games 0-0  Points 0-0";
}

public sealed class LobbyState
{
    public int ConnectedPlayers;
    public int MaximumPlayers = 2;
    public bool IsFull;
    public string[] PlayerNames = Array.Empty<string>();
    public int OnlinePlayers;
    public int ActiveMatches;
}

public sealed class WorldState
{
    public string MatchId = "";
    public long Tick;
    public MatchPhase Phase = MatchPhase.Waiting;
    public List<PlayerState> Players = new();
    public List<MinionState> Minions = new();
    // Monotonic event marker used to render a one-shot minion sparkle explosion on clients.
    public long MinionExplosionSequence;
    public Vector3 MinionExplosionPosition;
    public BallState Ball = new();
    public ScoreState Score = new();
    public LobbyState Lobby = new();
    public string Message = "Waiting for an opponent";
}
