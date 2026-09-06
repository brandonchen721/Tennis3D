using System.Text.Json;

namespace Tennis3D.Server;

public sealed class ServerTelemetry
{
    private long received;
    private long rejected;
    private long rateLimited;
    private long statesSent;
    private readonly DateTime startedUtc = DateTime.UtcNow;

    public void Received() => Interlocked.Increment(ref received);
    public void Rejected() => Interlocked.Increment(ref rejected);
    public void RateLimited() => Interlocked.Increment(ref rateLimited);
    public void StateSent() => Interlocked.Increment(ref statesSent);

    public void WriteHealth(string path, int onlinePlayers, int activeMatches)
    {
        var snapshot = new
        {
            status = "healthy",
            startedUtc,
            updatedUtc = DateTime.UtcNow,
            uptimeSeconds = (long)(DateTime.UtcNow - startedUtc).TotalSeconds,
            onlinePlayers,
            activeMatches,
            packetsReceived = Interlocked.Read(ref received),
            packetsRejected = Interlocked.Read(ref rejected),
            packetsRateLimited = Interlocked.Read(ref rateLimited),
            statesSent = Interlocked.Read(ref statesSent),
            workingSetBytes = Environment.WorkingSet
        };
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
