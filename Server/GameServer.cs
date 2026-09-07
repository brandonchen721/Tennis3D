using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using Tennis3D.Shared;

namespace Tennis3D.Server;

public sealed class GameServer : IDisposable
{
    private enum BotDifficulty { Easy, Medium, Hard, Impossible, SuperImpossible }

    private sealed class ClientSession
    {
        public required string ClientId;
        public required string Name;
        public required IPEndPoint EndPoint;
        public required DateTime LastSeenUtc;
        public string MatchId = "";
        public int PlayerId = -1;
        public DateTime RateWindowUtc = DateTime.UtcNow;
        public int PacketsInWindow;
        public long LastAcceptedInputSequence;
        public BotDifficulty? RequestedBotDifficulty;
        public float StringTension = Tennis3D.Shared.StringTension.Default;
    }

    private sealed class MatchRoom
    {
        public required string Id;
        public readonly WorldState World = new();
        public readonly Dictionary<int, string> PlayerClientIds = new();
        public readonly Dictionary<int, InputPacket> Inputs = new();
        public readonly TennisPhysics Physics = new();
        public readonly TennisRules Rules = new();
        public float PointOverTimer;
        public float StateTimer;
        public bool Started;
        // Server-owned automatic serve animation. These fields are private to the
        // server and do not alter the network protocol or connection system.
        public bool AutoServeActive;
        public bool AutoServeBallLaunched;
        public float AutoServeTimer;

        // AI mode is server-authoritative. Bot state lives only on the server and
        // uses the normal WorldState sent to the human client; no packet schema change.
        public bool BotEnabled;
        public int BotPlayerId = 1;
        public BotDifficulty BotDifficulty = BotDifficulty.Medium;
        public bool BotWillReach = true;
        public bool BotShouldAttemptIncoming = true;
        public bool BotPredictedIncomingOut;
        public int BotTrackedLastHitBy = int.MinValue;
        public Vector3 BotRunTarget;
        public Vector3 BotPlannedContact;
        public float BotPlannedContactEta;
        public HandedSwing BotPlannedHand = HandedSwing.Right;
        public float BotPlannedSwingHeight = 1.05f;
        public bool BotPlanReady;
        public bool BotWaitForBounce;
        public bool BotSwingActive;
        public float BotSwingTimer;
        public bool BotBallLaunchedThisSwing;
        public float BotDecisionCooldown;
        public float BotPlanRefreshTimer;
        public readonly Dictionary<int, float> StringTensions = new();
        public readonly float BotStringTension = Tennis3D.Shared.StringTension.Default;
        public ShotStyle BotImpossibleShotStyle = ShotStyle.Forehand;
        public bool BotImpossibleNextSlice;
        public bool BotSuperSmashPlanned;
        public float SuperMinionCycleSeconds;
        public bool SuperMinionsActive;
        // Up to ceil(living minions / 5) helpers may commit to an incoming ball. The first
        // selected helper owns the authoritative return; the others visibly cover nearby
        // lanes without creating duplicate ball contacts.
        public readonly int[] SuperMinionInterceptors = new int[4] { -1, -1, -1, -1 };
        public int SuperMinionInterceptorCount;
        public int NextSuperMinionId;
        public float SuperMinionRetryCooldown;
        // Server-only idle movement state. This is intentionally not network schema: clients
        // already receive each minion's authoritative Position in WorldState.
        public readonly Dictionary<int, Vector3> SuperMinionWanderTargets = new();
        public readonly Dictionary<int, float> SuperMinionWanderRetargetSeconds = new();
        // Minions that already returned the ball stay visible until their committed swing
        // animation finishes. They remain present (and keep the boss passive) during this
        // short visual tail, then are removed and emit the replicated sparkle explosion.
        public readonly HashSet<int> SuperMinionsPendingExplosion = new();

        public bool HasOpenSlot => !BotEnabled && PlayerClientIds.Count < 2;
        public bool ReadyToPlay => BotEnabled ? PlayerClientIds.Count == 1 : PlayerClientIds.Count == 2;
    }

    private static bool IsImpossibleTier(BotDifficulty difficulty) =>
        difficulty is BotDifficulty.Impossible or BotDifficulty.SuperImpossible;

    private static bool IsSuperImpossible(BotDifficulty difficulty) =>
        difficulty == BotDifficulty.SuperImpossible;

    // Super Impossible prefers to visibly hover between exchanges so its divine
    // falling-sparkle aura remains on display. Smash planning may move it much higher.
    private const float SuperImpossibleRestHoverHeight = 1.65f;

    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(8);
    private readonly UdpClient udp;
    private readonly object sync = new();
    private readonly Dictionary<string, ClientSession> clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> endpointToClient = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MatchRoom> matches = new(StringComparer.Ordinal);
    private readonly Random random = new();
    private readonly int listenPort;
    private readonly ServerTelemetry telemetry = new();
    private DateTime nextHealthWriteUtc = DateTime.UtcNow;
    private bool disposed;

    public GameServer(int port)
    {
        listenPort = port;
        udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));

        Console.WriteLine($"Tennis3D server listening on every IPv4 adapter: 0.0.0.0:{port}/UDP");
        foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName())
                     .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
            Console.WriteLine($"Direct LAN address: {address}:{port}");
        Console.WriteLine($"Capacity: {GameConstants.MaximumPlayers} players / {GameConstants.MaximumPlayers / 2} simultaneous matches");
        Console.WriteLine($"[SERVER NET] Protocol={GameConstants.ProtocolVersion}");
        Console.WriteLine("[SERVER NET] Waiting for UDP Hello packets...");
    }

    public async Task RunAsync()
    {
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Task receiveTask = ReceiveLoop(cts.Token);
        Stopwatch clock = Stopwatch.StartNew();
        double last = clock.Elapsed.TotalSeconds;
        double accumulator = 0;

        try
        {
            while (!cts.IsCancellationRequested)
            {
                double now = clock.Elapsed.TotalSeconds;
                accumulator += Math.Min(0.1, now - last);
                last = now;
                while (accumulator >= GameConstants.FixedDt)
                {
                    lock (sync)
                    {
                        RemoveTimedOutClients();
                        foreach (MatchRoom room in matches.Values.ToArray()) UpdateRoom(room, GameConstants.FixedDt);
                        if (DateTime.UtcNow >= nextHealthWriteUtc)
                        {
                            telemetry.WriteHealth("server-health.json", clients.Count,
                                matches.Values.Count(m => m.ReadyToPlay));
                            nextHealthWriteUtc = DateTime.UtcNow.AddSeconds(5);
                        }
                    }
                    accumulator -= GameConstants.FixedDt;
                }
                await Task.Delay(1, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            cts.Cancel();
            try { await receiveTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await udp.ReceiveAsync(token);
                telemetry.Received();
                Console.WriteLine($"[SERVER RECV] {DateTime.Now:HH:mm:ss.fff} {result.Buffer.Length} bytes from {result.RemoteEndPoint}");
                if (result.Buffer.Length == 0 || result.Buffer.Length > 32 * 1024)
                {
                    telemetry.Rejected();
                    Console.WriteLine($"Rejected datagram of {result.Buffer.Length} bytes from {result.RemoteEndPoint}.");
                    continue;
                }
                (PacketKind kind, JsonElement payload) = NetPacket.Unpack(result.Buffer);
                Console.WriteLine($"[SERVER PACKET] {kind} from {result.RemoteEndPoint}");
                lock (sync) HandlePacket(kind, payload, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Packet error: {ex.Message}");
            }
        }
    }

    private void HandlePacket(PacketKind kind, JsonElement payload, IPEndPoint endpoint)
    {
        switch (kind)
        {
            case PacketKind.Hello:
            {
                HelloPacket hello = payload.Deserialize<HelloPacket>(NetPacket.JsonOptions)!;
                HandleHello(hello, endpoint);
                break;
            }
            case PacketKind.Heartbeat:
            {
                HeartbeatPacket heartbeat = payload.Deserialize<HeartbeatPacket>(NetPacket.JsonOptions)!;
                if (clients.TryGetValue(heartbeat.ClientId, out ClientSession? session))
                {
                    UpdateEndpoint(session, endpoint);
                    session.LastSeenUtc = DateTime.UtcNow;
                }
                break;
            }
            case PacketKind.Input:
            {
                ClientSession? session = FindSession(endpoint);
                if (session is null || !matches.TryGetValue(session.MatchId, out MatchRoom? room)) break;
                if (!AllowPacket(session)) { telemetry.RateLimited(); break; }
                session.LastSeenUtc = DateTime.UtcNow;
                InputPacket input = payload.Deserialize<InputPacket>(NetPacket.JsonOptions)!;
                if (!IsValidInput(input) || input.Sequence <= session.LastAcceptedInputSequence) break;
                session.LastAcceptedInputSequence = input.Sequence;
                room.Inputs[session.PlayerId] = input;
                break;
            }
            case PacketKind.Disconnect:
            {
                DisconnectPacket disconnect = payload.Deserialize<DisconnectPacket>(NetPacket.JsonOptions)!;
                if (clients.TryGetValue(disconnect.ClientId, out ClientSession? session))
                    RemoveClient(session, disconnect.Reason);
                break;
            }
            case PacketKind.Ping:
            {
                PingPacket ping = payload.Deserialize<PingPacket>(NetPacket.JsonOptions)!;
                Send(endpoint, PacketKind.Pong, new PongPacket(ping.ClientTicks, DateTime.UtcNow.Ticks));
                break;
            }
        }
    }


    private static bool AllowPacket(ClientSession session)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - session.RateWindowUtc).TotalSeconds >= 1)
        {
            session.RateWindowUtc = now;
            session.PacketsInWindow = 0;
        }
        session.PacketsInWindow++;
        return session.PacketsInWindow <= 240;
    }

    private static bool IsValidInput(InputPacket input)
    {
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        return input.Sequence >= 0
            && Finite(input.MoveX) && MathF.Abs(input.MoveX) <= GameConstants.SprintMultiplier + 0.05f
            && Finite(input.MoveZ) && MathF.Abs(input.MoveZ) <= GameConstants.SprintMultiplier + 0.05f
            && Finite(input.AimYaw) && MathF.Abs(input.AimYaw) <= MathF.PI * 4f
            && Finite(input.AimPitch) && MathF.Abs(input.AimPitch) <= MathF.PI * 2f
            && Enum.IsDefined(input.Hand)
            && Finite(input.SwingStartHeight) && input.SwingStartHeight is >= 0.2f and <= 2.6f
            && Finite(input.RacquetHorizontal) && MathF.Abs(input.RacquetHorizontal) <= RacquetControls.MaximumControl + 0.10f
            && Finite(input.RacquetFaceAngle) && input.RacquetFaceAngle is >= -1.1f and <= 1.1f
            && Finite(input.MouseSwingSpeed) && input.MouseSwingSpeed is >= 0f and <= 10000f
            && Finite(input.MouseSwingVerticalSpeed) && MathF.Abs(input.MouseSwingVerticalSpeed) <= 10000f
            && Finite(input.ServeTossHeight) && input.ServeTossHeight >= GameConstants.MinimumTossApex && input.ServeTossHeight <= GameConstants.MaximumTossApex
            && Finite(input.ServeTossX) && MathF.Abs(input.ServeTossX) <= GameConstants.CourtHalfWidth;
    }

    private void HandleHello(HelloPacket hello, IPEndPoint endpoint)
    {
        Console.WriteLine($"[SERVER HELLO] clientId={hello.ClientId} name=\"{hello.Name}\" protocol={hello.ProtocolVersion} endpoint={endpoint}");
        if (hello.ProtocolVersion != GameConstants.ProtocolVersion)
        {
            Send(endpoint, PacketKind.Welcome,
                new WelcomePacket(-1, false, "Client/server versions differ. Rebuild both from the same ZIP.", "", GameConstants.ProtocolVersion));
            return;
        }
        if (string.IsNullOrWhiteSpace(hello.ClientId)) return;

        if (clients.TryGetValue(hello.ClientId, out ClientSession? existing))
        {
            UpdateEndpoint(existing, endpoint);
            existing.LastSeenUtc = DateTime.UtcNow;
            SendWelcome(existing, "Reconnected to match.");
            return;
        }
        if (clients.Count >= GameConstants.MaximumPlayers)
        {
            Send(endpoint, PacketKind.Welcome,
                new WelcomePacket(-1, false, "Server is at capacity. Retrying automatically.", "", GameConstants.ProtocolVersion));
            return;
        }

        (string displayName, BotDifficulty? requestedBot, float requestedTension) = ParseStartupRequest(hello.Name);
        ClientSession session = new()
        {
            ClientId = hello.ClientId,
            Name = SanitizeName(displayName),
            EndPoint = endpoint,
            LastSeenUtc = DateTime.UtcNow,
            RequestedBotDifficulty = requestedBot,
            StringTension = requestedTension
        };
        clients.Add(session.ClientId, session);
        endpointToClient[EndpointKey(endpoint)] = session.ClientId;
        AssignToMatch(session);
        Console.WriteLine($"{session.Name} connected from {endpoint}. Online: {clients.Count}");
    }

    private void AssignToMatch(ClientSession session)
    {
        MatchRoom room;
        if (session.RequestedBotDifficulty is BotDifficulty requestedDifficulty)
        {
            // AI matches are private one-human rooms. They never enter the human
            // matchmaking pool, so VS HUMAN behavior stays exactly as before.
            room = new MatchRoom
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                BotEnabled = true,
                BotDifficulty = requestedDifficulty,
                BotPlayerId = 1
            };
            room.World.MatchId = room.Id;
            matches.Add(room.Id, room);
        }
        else
        {
            room = matches.Values.FirstOrDefault(m => !m.BotEnabled && m.HasOpenSlot && !m.Started)
                   ?? new MatchRoom { Id = Guid.NewGuid().ToString("N")[..8] };
            if (!matches.ContainsKey(room.Id))
            {
                room.World.MatchId = room.Id;
                matches.Add(room.Id, room);
            }
        }

        int playerId = room.BotEnabled ? 0 : (room.PlayerClientIds.ContainsKey(0) ? 1 : 0);
        room.PlayerClientIds[playerId] = session.ClientId;
        room.StringTensions[playerId] = session.StringTension;
        session.MatchId = room.Id;
        session.PlayerId = playerId;
        room.World.Players.RemoveAll(p => p.Id == playerId);
        room.World.Players.Add(new PlayerState
        {
            Id = playerId,
            Name = session.Name,
            Position = new Vector3(0, 0, playerId == 0 ? -9.5f : 9.5f),
            FacingYaw = playerId == 0 ? 0f : MathF.PI
        });

        if (room.BotEnabled)
        {
            room.World.Players.RemoveAll(p => p.Id == room.BotPlayerId);
            room.World.Players.Add(new PlayerState
            {
                Id = room.BotPlayerId,
                Name = $"AI {room.BotDifficulty}",
                Position = new Vector3(0, 0, GameConstants.CourtHalfLength + GameConstants.ServeBaselineClearance),
                FacingYaw = MathF.PI,
                SwingHand = HandedSwing.Right
            });
        }
        if (room.BotEnabled) room.StringTensions[room.BotPlayerId] = room.BotStringTension;
        room.World.Players.Sort((a, b) => a.Id.CompareTo(b.Id));

        if (room.ReadyToPlay)
        {
            room.Started = true;
            room.World.Score = new ScoreState { Server = random.Next(2) };
            room.World.Score.Display = TennisRules.Format(room.World.Score);
            room.Rules.ResetPlayersAndBall(room.World);
            room.World.Phase = MatchPhase.ServeSetup;
            room.World.Message = room.BotEnabled
                ? $"AI {room.BotDifficulty} match. {room.World.Players.First(p => p.Id == room.World.Score.Server).Name} serves first."
                : $"{room.World.Players.First(p => p.Id == room.World.Score.Server).Name} serves first. Click a landing spot.";
            foreach (string clientId in room.PlayerClientIds.Values)
                if (clients.TryGetValue(clientId, out ClientSession? playerSession))
                    SendWelcome(playerSession, room.BotEnabled ? $"AI {room.BotDifficulty} match ready." : "Match found.");
            Console.WriteLine($"Match {room.Id} started: {string.Join(" vs ", room.World.Players.Select(p => p.Name))}");
        }
        else
        {
            room.World.Phase = MatchPhase.Waiting;
            room.World.Message = "Waiting for an opponent...";
            SendWelcome(session, "Joined lobby. Waiting for an opponent...");
        }
        RefreshLobby(room);
        SendState(room);
    }

    private void UpdateRoom(MatchRoom room, float dt)
    {
        if (!room.ReadyToPlay)
        {
            RefreshLobby(room);
            room.StateTimer += dt;
            if (room.StateTimer >= GameConstants.StateSendInterval)
            {
                room.StateTimer = 0;
                SendState(room);
            }
            return;
        }

        room.World.Tick++;
        foreach (PlayerState player in room.World.Players)
        {
            if (!room.Inputs.TryGetValue(player.Id, out InputPacket? input)) continue;
            ApplyPlayerInput(room, player, input, dt);
        }

        if (room.BotEnabled)
        {
            UpdateSuperImpossibleMinionCycle(room, dt);
            UpdateBot(room, dt);
        }
        foreach (PlayerState player in room.World.Players)
            AdvancePlayerJump(room, player, dt);
        UpdateAutomaticServe(room, dt);

        // Give persistent Super Impossible helpers a pre-physics interception opportunity.
        // This is what allows a helper to rescue a still-airborne incoming ball even after it
        // has already left the painted arena, before normal boundary scoring can end the point.
        if (room.BotEnabled && IsSuperImpossible(room.BotDifficulty) && room.World.Minions.Count > 0 &&
            room.World.Phase == MatchPhase.Rally && room.World.Ball.InPlay &&
            (!room.AutoServeActive || room.AutoServeBallLaunched))
            UpdateSuperImpossibleMinionsAfterPhysics(room);

        MatchPhase phaseAtPhysicsStart = room.World.Phase;
        if (room.World.Phase is MatchPhase.ServeToss or MatchPhase.Rally)
        {
            room.Physics.StepBall(room.World, dt, room.Rules);

            // Test racquet contact after the authoritative ball physics step as well
            // as during input processing. Previously, a fast ball could cross the
            // complete racquet volume between two input packets: the pre-step test
            // saw it before contact, then physics moved it past the racquet before
            // the next packet arrived. TennisPhysics keeps the previous ball position,
            // so this post-step pass checks the exact segment traveled this server tick.
            if (room.World.Phase == MatchPhase.Rally)
            {
                // Human/manual contact remains disabled while the automatic server is
                // still animating, preserving the existing serve ownership rules.
                if (!room.AutoServeActive)
                {
                    foreach (PlayerState player in room.World.Players)
                    {
                        if (!room.Inputs.TryGetValue(player.Id, out InputPacket? input)) continue;
                        if (!input.SwingHeld && !input.SwingReleased && !player.Swinging) continue;

                        if (room.Physics.TryRacquetHit(room.World, player,
                                input with { MouseSwingSpeed = Math.Max(player.SwingPower, input.MouseSwingSpeed) },
                                GetStringTension(room, player.Id)))
                            break;
                    }
                }

                // Once the automatic serve ball has actually launched, the receiving
                // bot must get the same post-physics swept-contact check as any rally
                // return. Previously AutoServeActive suppressed this for the server's
                // entire follow-through, even though the ball was already live.
                if (room.BotEnabled && (!room.AutoServeActive || room.AutoServeBallLaunched))
                {
                    if (IsSuperImpossible(room.BotDifficulty) && room.World.Minions.Count > 0)
                        UpdateSuperImpossibleMinionsAfterPhysics(room);
                    else
                        UpdateBotAfterPhysics(room, dt);
                }
            }
        }

        if (room.World.Phase == MatchPhase.PointOver)
        {
            // The point is decided, but the ball keeps moving naturally for the
            // 1.5-second result animation. No further scoring/contact is evaluated.
            if (phaseAtPhysicsStart == MatchPhase.PointOver)
                room.Physics.StepPointOverBall(room.World, dt);
            room.PointOverTimer += dt;
            if (room.PointOverTimer >= 1.50f)
            {
                room.PointOverTimer = 0f;
                room.Rules.PrepareNextPoint(room.World);
            }
        }
        else if (room.World.Phase == MatchPhase.MatchOver)
        {
            // Let the final point's ball continue instead of freezing on match point.
            // Do not advance it twice on the exact tick that awarded match point.
            if (phaseAtPhysicsStart == MatchPhase.MatchOver)
                room.Physics.StepPointOverBall(room.World, dt);
        }

        RefreshLobby(room);
        room.StateTimer += dt;
        if (room.StateTimer >= GameConstants.StateSendInterval)
        {
            room.StateTimer = 0f;
            SendState(room);
        }
    }

    private static void AdvancePlayerJump(MatchRoom room, PlayerState player, float dt)
    {
        // Super Impossible is intentionally supernatural: once airborne during a rally,
        // it hovers at the chosen authoritative Y instead of being pulled down by gravity.
        if (room.BotEnabled && player.Id == room.BotPlayerId && IsSuperImpossible(room.BotDifficulty) &&
            player.Position.Y > 0f)
        {
            player.Grounded = false;
            player.Velocity = new Vector3(player.Velocity.X, 0f, player.Velocity.Z);
            return;
        }
        if (player.Grounded && player.Position.Y <= 0f)
        {
            player.Position = new Vector3(player.Position.X, 0f, player.Position.Z);
            player.Velocity = new Vector3(player.Velocity.X, 0f, player.Velocity.Z);
            return;
        }

        float nextY = player.Position.Y + player.Velocity.Y * dt - 0.5f * GameConstants.Gravity * dt * dt;
        float nextVy = player.Velocity.Y - GameConstants.Gravity * dt;
        if (nextY <= 0f)
        {
            nextY = 0f;
            nextVy = 0f;
            player.Grounded = true;
        }
        else
        {
            player.Grounded = false;
        }
        player.Position = new Vector3(player.Position.X, nextY, player.Position.Z);
        player.Velocity = new Vector3(player.Velocity.X, nextVy, player.Velocity.Z);
    }

    private void ApplyPlayerInput(MatchRoom room, PlayerState player, InputPacket input, float dt)
    {
        Vector3 movement = new Vector3(input.MoveX, 0, input.MoveZ);
        float requestedMoveMagnitude = movement.Length();
        bool sprintRequested = requestedMoveMagnitude > 1.01f;
        if (movement.LengthSquared() > 1f) movement = Vector3.Normalize(movement);
        bool isServingNow = player.Id == room.World.Score.Server &&
                            (room.World.Phase == MatchPhase.ServeSetup || room.World.Phase == MatchPhase.ServeToss);
        // Once the toss starts, hold the server and camera anchor still. This keeps
        // the vertical 3D toss line projected onto the exact screen pixel selected.
        if (player.Id == room.World.Score.Server && room.World.Phase == MatchPhase.ServeToss)
            movement = Vector3.Zero;
        float playerSpeedMultiplier = sprintRequested ? GameConstants.SprintMultiplier : 1f;
        Vector3 horizontalVelocity = movement * (GameConstants.PlayerSpeed * playerSpeedMultiplier);
        player.Velocity = new Vector3(horizontalVelocity.X, player.Velocity.Y, horizontalVelocity.Z);
        player.Position += new Vector3(player.Velocity.X * dt, 0f, player.Velocity.Z * dt);
        if (input.JumpRequested && input.Sequence > player.LastJumpSequence && player.Grounded)
        {
            player.LastJumpSequence = input.Sequence;
            player.Grounded = false;
            player.Velocity = new Vector3(player.Velocity.X, GameConstants.PlayerJumpSpeed, player.Velocity.Z);
        }
        float minZ = player.Id == 0 ? -GameConstants.CourtHalfLength - 3f : 0.75f;
        float maxZ = player.Id == 0 ? -0.75f : GameConstants.CourtHalfLength + 3f;
        float minX = -GameConstants.CourtHalfWidth - 2.5f;
        float maxX = GameConstants.CourtHalfWidth + 2.5f;
        if (isServingNow)
        {
            if (player.Id == 0) maxZ = -GameConstants.CourtHalfLength - GameConstants.ServeBaselineClearance;
            else minZ = GameConstants.CourtHalfLength + GameConstants.ServeBaselineClearance;

            // During a serve the server must remain on the assigned deuce/ad half,
            // between the center mark and singles sideline. This prevents walking
            // across the center line and turning the required diagonal serve into a
            // straight-ahead serve. The server is authoritative, so both clients see
            // the same legal position without any protocol change.
            float serveFromXSign = TennisRules.ServeFromXSign(room.World.Score, player.Id);
            const float centerMarkClearance = 0.08f;
            if (serveFromXSign > 0f)
            {
                minX = centerMarkClearance;
                maxX = GameConstants.SinglesHalfWidth;
            }
            else
            {
                minX = -GameConstants.SinglesHalfWidth;
                maxX = -centerMarkClearance;
            }
        }
        player.Position = new Vector3(
            Math.Clamp(player.Position.X, minX, maxX),
            player.Position.Y,
            Math.Clamp(player.Position.Z, minZ, maxZ));
        player.SwingHand = input.Hand == HandedSwing.None ? player.SwingHand : input.Hand;
        player.SwingHeight = input.SwingStartHeight;
        player.RacquetBehind = input.RacquetBehind;
        player.RacquetHorizontal = input.RacquetHorizontal;
        player.RacquetFaceAngle = Math.Clamp(input.RacquetFaceAngle, -0.872665f, 0.872665f);
        player.FacingYaw = ShotControls.EncodeVisualFacingYaw(player.Id, ShotControls.DecodeStyle(input.AimYaw));
        player.LastInputSequence = input.Sequence;

        bool racquetContactAllowed = room.World.Phase is MatchPhase.Rally or MatchPhase.ServeToss;
        if (racquetContactAllowed && !room.AutoServeActive && input.SwingHeld)
        {
            // One click commits to one fixed 0.5-second animation. The client keeps
            // SwingHeld asserted for exactly that window; the server advances the
            // authoritative pose and checks contact throughout the complete stroke.
            if (!player.Swinging)
            {
                player.Swinging = true;
                player.SwingProgress = 0f;
                player.SwingPower = 0f;
                player.SwingReleasedVisual = false;
            }
            player.SwingProgress = Math.Min(SwingAnimation.DurationSeconds, player.SwingProgress + dt);
            player.SwingPower = Math.Max(player.SwingPower, input.MouseSwingSpeed);
            room.Physics.TryRacquetHit(room.World, player,
                input with { MouseSwingSpeed = player.SwingPower }, GetStringTension(room, player.Id));
        }
        if (racquetContactAllowed && !room.AutoServeActive && input.SwingReleased)
        {
            // Test the final pose once more, then finish immediately. There is no
            // separate free-moving follow-through after the 0.5-second animation.
            player.SwingProgress = SwingAnimation.DurationSeconds;
            player.SwingPower = Math.Max(player.SwingPower, input.MouseSwingSpeed);
            room.Physics.TryRacquetHit(room.World, player,
                input with { MouseSwingSpeed = player.SwingPower }, GetStringTension(room, player.Id));
            player.Swinging = false;
            player.SwingReleasedVisual = false;
            player.SwingProgress = 0f;
            player.SwingPower = 0f;
        }

        if (room.World.Phase == MatchPhase.ServeSetup && player.Id == room.World.Score.Server && input.ServeTossRequested)
        {
            bool behindBaseline = player.Id == 0
                ? player.Position.Z <= -GameConstants.CourtHalfLength
                : player.Position.Z >= GameConstants.CourtHalfLength;
            if (!behindBaseline)
            {
                room.Rules.ServeFault(room.World, player.Id, "foot fault: server must stand behind the baseline");
                return;
            }

            bool onRequiredServeSide = TennisRules.IsLegalServeX(room.World.Score, player.Id, player.Position.X);
            if (!onRequiredServeSide)
            {
                room.Rules.ServeFault(room.World, player.Id, "foot fault: server crossed the center mark or singles sideline");
                return;
            }

            // Decode the clicked top-down landing point from the existing packet
            // fields. This preserves protocol version 9 and all connection code.
            float selectedX = Math.Clamp(input.ServeTossX, -GameConstants.SinglesHalfWidth, GameConstants.SinglesHalfWidth);
            float encoded01 = Math.Clamp((input.ServeTossHeight - GameConstants.MinimumTossApex) /
                                         (GameConstants.MaximumTossApex - GameConstants.MinimumTossApex), 0f, 1f);
            float selectedAbsZ = encoded01 * GameConstants.ServiceLine;
            float selectedZ = player.Id == 0 ? selectedAbsZ : -selectedAbsZ;

            float serveFromXSign = TennisRules.ServeFromXSign(room.World.Score, player.Id);
            int targetXSign = serveFromXSign > 0f ? -1 : 1;
            selectedX = targetXSign < 0
                ? Math.Clamp(selectedX, -GameConstants.SinglesHalfWidth, 0f)
                : Math.Clamp(selectedX, 0f, GameConstants.SinglesHalfWidth);

            // Server-authoritative random error. Radius is exactly one quarter of
            // the service-box length. The click must be legal, but edge targets can
            // scatter out and fault, making ace attempts carry real risk.
            float randomRadius = GameConstants.ServiceLine * 0.25f;
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            float radius = MathF.Sqrt(Random.Shared.NextSingle()) * randomRadius;
            float actualTargetX = selectedX + MathF.Cos(angle) * radius;
            float actualTargetZ = selectedZ + MathF.Sin(angle) * radius;

            // Use a simple, repeatable gravity-driven toss directly above the hand.
            float tossHandSide = player.Id == 0 ? -0.22f : 0.22f;
            float startX = player.Position.X + tossHandSide;
            const float standardTossApex = 3.15f;
            room.World.Ball.Position = new Vector3(startX, 1.15f, player.Position.Z);
            float rise = standardTossApex - room.World.Ball.Position.Y;
            float launchSpeed = MathF.Sqrt(2f * GameConstants.Gravity * rise);
            room.World.Ball.Velocity = new Vector3(0f, launchSpeed, 0f);
            room.World.Ball.AngularVelocity = Vector3.Zero;
            room.World.Ball.InPlay = true;
            room.World.Ball.IsServeToss = true;
            room.World.Ball.TossOwner = player.Id;
            // Reuse existing toss fields as the authoritative randomized serve
            // landing target; no protocol or state-schema changes are required.
            room.World.Ball.SelectedTossHeight = actualTargetZ;
            room.World.Ball.SelectedTossX = actualTargetX;
            room.World.Ball.TossReachedApex = false;
            room.World.Ball.ServeTargetXSign = targetXSign;
            room.World.Phase = MatchPhase.ServeToss;

            // Start a synchronized server-owned animation. The player no longer
            // needs to hit the toss manually; every client sees the same toss,
            // backswing, contact, follow-through, and ball flight.
            room.AutoServeActive = true;
            room.AutoServeBallLaunched = false;
            room.AutoServeTimer = 0f;
            player.Swinging = true;
            player.SwingReleasedVisual = false;
            player.SwingVisualTimer = 0f;
            player.SwingProgress = 0f;
            player.SwingPower = 1f;
            player.FacingYaw = ShotControls.EncodeVisualFacingYaw(player.Id, ShotStyle.Forehand);
            room.World.Message = "Automatic serve: toss and swing in progress.";
        }
    }

    private static readonly float[] SuperMinionSideOffsets = { -1.70f, -0.85f, 0.85f, 1.70f };
    private const float SuperMinionMinimumSpacing = 0.72f;
    // Approximate combined left-to-right arm span of the smaller rendered helper. Idle
    // helpers evacuate the live ball path and any teleport contact target by at least this.
    private const float SuperMinionArmSpanMargin = 1.35f;
    private const float SuperMinionWanderSpeed = 1.15f;
    private const float SuperMinionWanderRetargetMin = 1.25f;
    private const float SuperMinionWanderRetargetMax = 3.25f;
    // Persistent helpers never expire, so cap the live population to keep replication,
    // rendering, spacing checks, and interception selection permanently bounded.
    private const int MaximumSuperMinions = 16;

    private void UpdateSuperImpossibleMinionCycle(MatchRoom room, float dt)
    {
        if (!IsSuperImpossible(room.BotDifficulty))
        {
            room.SuperMinionsActive = false;
            ClearSuperMinionInterceptors(room);
            room.SuperMinionsPendingExplosion.Clear();
            room.SuperMinionWanderTargets.Clear();
            room.SuperMinionWanderRetargetSeconds.Clear();
            room.World.Minions.Clear();
            return;
        }

        // Minions never expire. The summon clock advances only during live rally time;
        // every five rally seconds another four-minion wave is added into unoccupied slots.
        if (room.World.Phase == MatchPhase.Rally && room.World.Ball.InPlay)
        {
            room.SuperMinionCycleSeconds += dt;
            while (room.SuperMinionCycleSeconds >= 5f)
            {
                room.SuperMinionCycleSeconds -= 5f;
                SpawnSuperMinionWave(room);
            }
        }

        room.SuperMinionsActive = room.World.Minions.Count > 0;
        if (!room.SuperMinionsActive)
        {
            ClearSuperMinionInterceptors(room);
            return;
        }

        room.SuperMinionRetryCooldown = Math.Max(0f, room.SuperMinionRetryCooldown - dt);
        PlayerState? boss = room.World.Players.FirstOrDefault(p => p.Id == room.BotPlayerId);
        if (boss is null) return;

        int opponentId = 1 - boss.Id;
        bool incoming = room.World.Ball.LastHitBy == opponentId;
        if (!incoming)
            ClearSuperMinionInterceptors(room);

        // Make idle helpers feel alive without adding protocol data. They stroll only on the
        // boss's own half, but can instantly evacuate an unsafe ball corridor or a committed
        // interceptor's teleport target. Interceptors themselves are handled separately.
        UpdateSuperMinionWandering(room, boss, dt);

        // A minion that has already made successful contact stays alive for the remainder of
        // the SAME committed swing. Only after the animation reaches its natural endpoint is
        // that minion removed and its sparkle firework replicated. Ball physics are unchanged.
        for (int i = room.World.Minions.Count - 1; i >= 0; i--)
        {
            MinionState minion = room.World.Minions[i];
            if (!minion.Swinging) continue;

            minion.SwingProgress += dt;
            if (minion.SwingProgress < SwingAnimation.DurationSeconds) continue;

            minion.SwingProgress = SwingAnimation.DurationSeconds;
            minion.Swinging = false;
            if (!room.SuperMinionsPendingExplosion.Remove(minion.Id))
            {
                minion.SwingProgress = 0f;
                continue;
            }

            room.World.MinionExplosionPosition = minion.Position + new Vector3(0f, 0.78f, 0f);
            room.World.MinionExplosionSequence++;
            int explodedId = minion.Id;
            room.World.Minions.RemoveAt(i);
            room.SuperMinionWanderTargets.Remove(explodedId);
            room.SuperMinionWanderRetargetSeconds.Remove(explodedId);
            RemoveSuperMinionInterceptor(room, explodedId);
            room.World.Message = $"Minion {explodedId + 1} finishes its swing and explodes into sparkles.";
        }

        room.SuperMinionsActive = room.World.Minions.Count > 0;
    }

    private void SpawnSuperMinionWave(MatchRoom room)
    {
        PlayerState? boss = room.World.Players.FirstOrDefault(p => p.Id == room.BotPlayerId);
        if (boss is null) return;

        int availableSlots = Math.Max(0, MaximumSuperMinions - room.World.Minions.Count);
        int spawnCount = Math.Min(SuperMinionSideOffsets.Length, availableSlots);
        if (spawnCount == 0)
        {
            room.World.Message = "Super Impossible's minion squad is at maximum strength.";
            return;
        }

        for (int i = 0; i < spawnCount; i++)
        {
            int id = room.NextSuperMinionId++;
            Vector3 desired = new(boss.Position.X + SuperMinionSideOffsets[i], 0f, boss.Position.Z);
            Vector3 safe = FindSafeMinionSpawn(room.World.Minions, desired, boss.Position, id);
            room.World.Minions.Add(new MinionState
            {
                Id = id,
                OwnerId = boss.Id,
                Position = safe,
                SwingHand = (i & 1) == 0 ? HandedSwing.Right : HandedSwing.Left
            });
            room.SuperMinionWanderTargets[id] = safe;
            room.SuperMinionWanderRetargetSeconds[id] = 0f;
        }

        EnsureSuperMinionSpacing(room.World.Minions);
        room.SuperMinionsActive = true;
        room.World.Message = spawnCount == 4
            ? "Super Impossible summons four more minions."
            : $"Super Impossible summons {spawnCount} minion(s) into open slots.";
    }

    private static Vector3 FindSafeMinionSpawn(List<MinionState> minions, Vector3 desired, Vector3 bossPosition, int id)
    {
        float minX = -GameConstants.CourtHalfWidth + 0.35f;
        float maxX = GameConstants.CourtHalfWidth - 0.35f;
        float bossSideMinZ = bossPosition.Z >= 0f ? 0.65f : -GameConstants.CourtHalfLength - 1.7f;
        float bossSideMaxZ = bossPosition.Z >= 0f ? GameConstants.CourtHalfLength + 1.7f : -0.65f;

        for (int ring = 0; ring < 18; ring++)
        {
            float zStep = (ring / 2 + 1) * 0.82f;
            float z = ring == 0 ? desired.Z : desired.Z + ((ring & 1) == 0 ? zStep : -zStep);
            z = Math.Clamp(z, Math.Min(bossSideMinZ, bossSideMaxZ), Math.Max(bossSideMinZ, bossSideMaxZ));
            for (int lateral = 0; lateral < 9; lateral++)
            {
                float shift = lateral == 0 ? 0f : ((lateral & 1) == 1 ? 1f : -1f) * ((lateral + 1) / 2) * 0.78f;
                float x = Math.Clamp(desired.X + shift, minX, maxX);
                Vector3 candidate = new(x, 0f, z);
                if (IsMinionPositionClear(minions, candidate, -1, SuperMinionMinimumSpacing)) return candidate;
            }
        }

        float directionZ = bossPosition.Z >= 0f ? 1f : -1f;
        for (int step = 1; ; step++)
        {
            float x = Math.Clamp(desired.X + ((id & 1) == 0 ? -0.39f : 0.39f), minX, maxX);
            Vector3 candidate = new(x, 0f, desired.Z + directionZ * step * SuperMinionMinimumSpacing);
            if (IsMinionPositionClear(minions, candidate, -1, SuperMinionMinimumSpacing)) return candidate;
        }
    }

    private void UpdateSuperMinionWandering(MatchRoom room, PlayerState boss, float dt)
    {
        BallState ball = room.World.Ball;
        Vector3 ballStart = new(ball.Position.X, 0f, ball.Position.Z);
        Vector3 horizontalVelocity = new(ball.Velocity.X, 0f, ball.Velocity.Z);
        float lookAheadSeconds = 0.40f;
        Vector3 ballEnd = ballStart + horizontalVelocity * lookAheadSeconds;

        for (int i = 0; i < room.World.Minions.Count; i++)
        {
            MinionState minion = room.World.Minions[i];
            if (minion.Swinging || room.SuperMinionsPendingExplosion.Contains(minion.Id) || IsSuperMinionInterceptor(room, minion.Id))
                continue;

            bool unsafeBallPath = DistancePointToSegmentXZ(minion.Position, ballStart, ballEnd) < SuperMinionArmSpanMargin;
            if (unsafeBallPath)
            {
                minion.Position = FindSafeIdleEvacuation(room.World.Minions, minion.Id, minion.Position, boss, ballStart, ballEnd, ReadOnlySpan<Vector3>.Empty);
                room.SuperMinionWanderTargets[minion.Id] = minion.Position;
                room.SuperMinionWanderRetargetSeconds[minion.Id] = NextWanderRetargetSeconds();
                continue;
            }

            float remaining = room.SuperMinionWanderRetargetSeconds.TryGetValue(minion.Id, out float timer) ? timer - dt : 0f;
            Vector3 target;
            if (remaining <= 0f || !room.SuperMinionWanderTargets.TryGetValue(minion.Id, out target) ||
                Vector3.DistanceSquared(minion.Position, target) < 0.05f)
            {
                target = ChooseWanderTarget(room.World.Minions, minion.Id, boss);
                room.SuperMinionWanderTargets[minion.Id] = target;
                remaining = NextWanderRetargetSeconds();
            }
            room.SuperMinionWanderRetargetSeconds[minion.Id] = remaining;

            Vector3 delta = target - minion.Position;
            delta.Y = 0f;
            float distance = delta.Length();
            if (distance <= 0.0001f) continue;
            float step = Math.Min(distance, SuperMinionWanderSpeed * dt);
            Vector3 candidate = minion.Position + delta / distance * step;
            candidate.Y = 0f;
            if (IsMinionPositionClear(room.World.Minions, candidate, minion.Id, SuperMinionMinimumSpacing))
                minion.Position = candidate;
            else
                room.SuperMinionWanderRetargetSeconds[minion.Id] = 0f;
        }
    }

    private Vector3 ChooseWanderTarget(List<MinionState> minions, int movingId, PlayerState boss)
    {
        float minX = -GameConstants.CourtHalfWidth + 0.45f;
        float maxX = GameConstants.CourtHalfWidth - 0.45f;
        float minZ = boss.Id == 0 ? -GameConstants.CourtHalfLength - 0.8f : 0.75f;
        float maxZ = boss.Id == 0 ? -0.75f : GameConstants.CourtHalfLength + 0.8f;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            Vector3 candidate = new(
                minX + (float)random.NextDouble() * (maxX - minX),
                0f,
                minZ + (float)random.NextDouble() * (maxZ - minZ));
            if (IsMinionPositionClear(minions, candidate, movingId, SuperMinionMinimumSpacing))
                return candidate;
        }
        MinionState? minion = FindMinionById(minions, movingId);
        return minion?.Position ?? new Vector3(0f, 0f, (minZ + maxZ) * 0.5f);
    }

    private float NextWanderRetargetSeconds() =>
        SuperMinionWanderRetargetMin + (float)random.NextDouble() *
        (SuperMinionWanderRetargetMax - SuperMinionWanderRetargetMin);

    private void UpdateSuperImpossibleMinionsAfterPhysics(MatchRoom room)
    {
        if (room.World.Minions.Count == 0) return;
        if (room.World.Phase != MatchPhase.Rally || !room.World.Ball.InPlay) return;

        PlayerState? boss = room.World.Players.FirstOrDefault(p => p.Id == room.BotPlayerId);
        if (boss is null) return;
        BallState ball = room.World.Ball;
        int opponentId = 1 - boss.Id;
        if (ball.LastHitBy != opponentId) return;
        if (room.SuperMinionRetryCooldown > 0f) return;
        if (ball.ServeMustBounce) return;

        // Every incoming rally ball gets a minion response immediately. Do not wait for the
        // ball to enter the boss half and do not reject high/wide rescue attempts. This is
        // intentionally evaluated before the authoritative physics step too, so a ball that
        // is already escaping the arena can still be rescued before boundary scoring fires.
        // The helper formation itself may float when the ball is outside the arena.

        // Exact requested squad scaling: ceil(total living minions / 5). With the existing
        // hard cap of 16 helpers this produces 1..4 interceptors and can never overflow the
        // fixed interceptor storage.
        int desiredCount = Math.Clamp((room.World.Minions.Count + 4) / 5, 1, room.SuperMinionInterceptors.Length);
        SelectClosestSuperMinions(room, ball.Position, desiredCount);
        if (room.SuperMinionInterceptorCount == 0) return;

        PositionInterceptorsAndEvacuate(room, boss, ball);

        MinionState? primary = FindMinionById(room.World.Minions, room.SuperMinionInterceptors[0]);
        if (primary is null) return;

        // Only the closest helper owns the one authoritative return so the ball never receives
        // duplicate launches in one tick. Commit the squad's visible swings only after the
        // bounded solver has validated and launched that return.
        if (!LaunchSuperMinionReturn(room, boss, primary, ball))
        {
            room.SuperMinionRetryCooldown = 0.06f;
            ClearSuperMinionInterceptors(room);
            return;
        }

        for (int i = 0; i < room.SuperMinionInterceptorCount; i++)
        {
            MinionState? minion = FindMinionById(room.World.Minions, room.SuperMinionInterceptors[i]);
            if (minion is not null && !minion.Swinging)
                StartSuperMinionSwing(minion, ball);
        }

        room.SuperMinionsPendingExplosion.Add(primary.Id);
        room.SuperMinionRetryCooldown = 0f;
        ClearSuperMinionInterceptors(room);
        room.BotDecisionCooldown = 0.05f;
        room.World.Message = $"Minion {primary.Id + 1} returns the ball while the squad covers the rescue.";
    }

    private static void SelectClosestSuperMinions(MatchRoom room, Vector3 ballPosition, int desiredCount)
    {
        ClearSuperMinionInterceptors(room);
        Span<float> bestDistances = stackalloc float[4] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
        Span<int> bestIds = stackalloc int[4] { -1, -1, -1, -1 };
        Vector3 groundBall = new(ballPosition.X, 0f, ballPosition.Z);

        for (int i = 0; i < room.World.Minions.Count; i++)
        {
            MinionState minion = room.World.Minions[i];
            if (room.SuperMinionsPendingExplosion.Contains(minion.Id) || minion.Swinging) continue;
            Vector3 delta = minion.Position - groundBall;
            float distanceSquared = delta.X * delta.X + delta.Z * delta.Z;

            for (int slot = 0; slot < desiredCount; slot++)
            {
                if (distanceSquared >= bestDistances[slot]) continue;
                for (int shift = desiredCount - 1; shift > slot; shift--)
                {
                    bestDistances[shift] = bestDistances[shift - 1];
                    bestIds[shift] = bestIds[shift - 1];
                }
                bestDistances[slot] = distanceSquared;
                bestIds[slot] = minion.Id;
                break;
            }
        }

        for (int i = 0; i < desiredCount; i++)
        {
            if (bestIds[i] < 0) break;
            room.SuperMinionInterceptors[room.SuperMinionInterceptorCount++] = bestIds[i];
        }
    }

    private static void PositionInterceptorsAndEvacuate(MatchRoom room, PlayerState boss, BallState ball)
    {
        float zBehindBall = boss.Id == 0 ? ball.Position.Z - 0.34f : ball.Position.Z + 0.34f;
        int count = room.SuperMinionInterceptorCount;
        Span<Vector3> targets = stackalloc Vector3[4];
        float spacing = Math.Max(SuperMinionArmSpanMargin, SuperMinionMinimumSpacing);
        float center = (count - 1) * 0.5f;

        bool outsideArena = MathF.Abs(ball.Position.X) > GameConstants.CourtHalfWidth + GameConstants.BallRadius ||
                            MathF.Abs(ball.Position.Z) > GameConstants.CourtHalfLength + GameConstants.BallRadius;
        // Ground helpers stay grounded for normal court defense. When rescuing an out-of-arena
        // ball they may float just enough to put the same swing volume around the ball. There
        // is intentionally no arena or altitude clamp on the rescue position.
        float rescueY = outsideArena ? MathF.Max(0f, ball.Position.Y - 1.05f) : 0f;

        // Symmetric side-by-side formation centered on the ball: 1=center, 2=left/right,
        // 3=left/center/right, 4=two per side. Exact arm-span spacing prevents stacking.
        for (int i = 0; i < count; i++)
            targets[i] = new Vector3(ball.Position.X + (i - center) * spacing, rescueY, zBehindBall);

        Vector3 ballStart = new(ball.Position.X, 0f, ball.Position.Z);
        Vector3 ballEnd = ballStart + new Vector3(ball.Velocity.X, 0f, ball.Velocity.Z) * 0.40f;

        // Move non-assigned helpers out of both the live ball corridor and every committed
        // teleport destination by a full minion arm span before the interceptors arrive.
        for (int m = 0; m < room.World.Minions.Count; m++)
        {
            MinionState minion = room.World.Minions[m];
            if (IsSuperMinionInterceptor(room, minion.Id) || room.SuperMinionsPendingExplosion.Contains(minion.Id))
                continue;

            bool unsafe = DistancePointToSegmentXZ(minion.Position, ballStart, ballEnd) < SuperMinionArmSpanMargin;
            if (!unsafe)
            {
                for (int t = 0; t < count; t++)
                {
                    if (DistanceXZ(minion.Position, targets[t]) < SuperMinionArmSpanMargin)
                    {
                        unsafe = true;
                        break;
                    }
                }
            }
            if (!unsafe) continue;

            minion.Position = FindSafeIdleEvacuation(room.World.Minions, minion.Id, minion.Position, boss, ballStart, ballEnd, targets[..count]);
            room.SuperMinionWanderTargets[minion.Id] = minion.Position;
            room.SuperMinionWanderRetargetSeconds[minion.Id] = 0f;
        }

        for (int i = 0; i < count; i++)
        {
            MinionState? minion = FindMinionById(room.World.Minions, room.SuperMinionInterceptors[i]);
            if (minion is null) continue;
            // Interceptors own reserved formation slots. Ignore the selected squad's OLD
            // positions while validating those slots; otherwise a helper that has not moved
            // yet can incorrectly displace another helper from its intended side-by-side slot.
            // Non-selected helpers were evacuated above and still participate in clearance.
            minion.Position = FindSafeInterceptorTeleport(room, minion.Id, targets[i]);
        }
    }

    private static Vector3 FindSafeIdleEvacuation(
        List<MinionState> minions, int movingId, Vector3 origin, PlayerState boss,
        Vector3 ballStart, Vector3 ballEnd, ReadOnlySpan<Vector3> reservedTargets)
    {
        float minX = -GameConstants.CourtHalfWidth + 0.45f;
        float maxX = GameConstants.CourtHalfWidth - 0.45f;
        float minZ = boss.Id == 0 ? -GameConstants.CourtHalfLength - 0.8f : 0.75f;
        float maxZ = boss.Id == 0 ? -0.75f : GameConstants.CourtHalfLength + 0.8f;

        for (int ring = 1; ring <= 10; ring++)
        {
            float radius = SuperMinionArmSpanMargin * ring;
            for (int step = 0; step < 12; step++)
            {
                float angle = MathF.Tau * step / 12f;
                Vector3 candidate = new(
                    Math.Clamp(origin.X + MathF.Cos(angle) * radius, minX, maxX),
                    0f,
                    Math.Clamp(origin.Z + MathF.Sin(angle) * radius, minZ, maxZ));
                if (!IsMinionPositionClear(minions, candidate, movingId, SuperMinionMinimumSpacing)) continue;
                if (DistancePointToSegmentXZ(candidate, ballStart, ballEnd) < SuperMinionArmSpanMargin) continue;

                bool targetClear = true;
                for (int i = 0; i < reservedTargets.Length; i++)
                {
                    if (DistanceXZ(candidate, reservedTargets[i]) < SuperMinionArmSpanMargin)
                    {
                        targetClear = false;
                        break;
                    }
                }
                if (targetClear) return candidate;
            }
        }
        return origin;
    }

    private static MinionState? FindMinionById(List<MinionState> minions, int id)
    {
        for (int i = 0; i < minions.Count; i++)
            if (minions[i].Id == id)
                return minions[i];
        return null;
    }

    private static Vector3 FindSafeInterceptorTeleport(MatchRoom room, int movingId, Vector3 desired)
    {
        Vector3 basePosition = desired;
        if (IsInterceptorSlotClear(room, basePosition, movingId)) return basePosition;

        // Extremely rare fallback if an idle helper could not evacuate. Keep the squad
        // side-by-side by searching only outward along X in whole arm-span increments.
        for (int step = 1; step <= 12; step++)
        {
            float offset = SuperMinionArmSpanMargin * step;
            Vector3 left = new(basePosition.X - offset, basePosition.Y, basePosition.Z);
            if (IsInterceptorSlotClear(room, left, movingId)) return left;
            Vector3 right = new(basePosition.X + offset, basePosition.Y, basePosition.Z);
            if (IsInterceptorSlotClear(room, right, movingId)) return right;
        }
        return basePosition;
    }

    private static bool IsInterceptorSlotClear(MatchRoom room, Vector3 candidate, int movingId)
    {
        float minimumSquared = SuperMinionMinimumSpacing * SuperMinionMinimumSpacing;
        for (int i = 0; i < room.World.Minions.Count; i++)
        {
            MinionState other = room.World.Minions[i];
            if (other.Id == movingId || IsSuperMinionInterceptor(room, other.Id)) continue;
            float dx = other.Position.X - candidate.X;
            float dz = other.Position.Z - candidate.Z;
            if (dx * dx + dz * dz < minimumSquared) return false;
        }
        return true;
    }

    private static Vector3 FindSafeMinionTeleport(List<MinionState> minions, int movingId, Vector3 desired, bool noArenaBounds)
    {
        Vector3 basePosition = noArenaBounds
            ? new Vector3(desired.X, 0f, desired.Z)
            : new Vector3(Math.Clamp(desired.X, -GameConstants.CourtHalfWidth + 0.35f, GameConstants.CourtHalfWidth - 0.35f), 0f, desired.Z);
        if (IsMinionPositionClear(minions, basePosition, movingId, SuperMinionMinimumSpacing)) return basePosition;

        for (int ring = 1; ring <= 8; ring++)
        {
            float r = SuperMinionArmSpanMargin * ring;
            for (int step = 0; step < 12; step++)
            {
                float angle = MathF.Tau * step / 12f;
                float x = basePosition.X + MathF.Cos(angle) * r;
                if (!noArenaBounds)
                    x = Math.Clamp(x, -GameConstants.CourtHalfWidth + 0.35f, GameConstants.CourtHalfWidth - 0.35f);
                Vector3 candidate = new(x, 0f, basePosition.Z + MathF.Sin(angle) * r);
                if (IsMinionPositionClear(minions, candidate, movingId, SuperMinionMinimumSpacing)) return candidate;
            }
        }
        return basePosition;
    }

    private static bool IsMinionPositionClear(List<MinionState> minions, Vector3 candidate, int ignoredId, float minimumDistance)
    {
        float minimumSquared = minimumDistance * minimumDistance;
        for (int i = 0; i < minions.Count; i++)
        {
            MinionState other = minions[i];
            if (other.Id == ignoredId) continue;
            float dx = other.Position.X - candidate.X;
            float dz = other.Position.Z - candidate.Z;
            if (dx * dx + dz * dz < minimumSquared) return false;
        }
        return true;
    }

    private static void EnsureSuperMinionSpacing(List<MinionState> minions)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            for (int i = 0; i < minions.Count; i++)
            {
                for (int j = i + 1; j < minions.Count; j++)
                {
                    Vector2 a = new(minions[i].Position.X, minions[i].Position.Z);
                    Vector2 b = new(minions[j].Position.X, minions[j].Position.Z);
                    Vector2 delta = b - a;
                    float distance = delta.Length();
                    if (distance >= SuperMinionMinimumSpacing) continue;

                    Vector2 axis = distance > 0.0001f
                        ? delta / distance
                        : new Vector2(((minions[i].Id + minions[j].Id) & 1) == 0 ? 1f : -1f, 0f);
                    float correction = (SuperMinionMinimumSpacing - distance) * 0.5f + 0.001f;
                    a -= axis * correction;
                    b += axis * correction;
                    minions[i].Position = new Vector3(a.X, 0f, a.Y);
                    minions[j].Position = new Vector3(b.X, 0f, b.Y);
                }
            }
        }
    }

    private static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float DistancePointToSegmentXZ(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector2 p = new(point.X, point.Z);
        Vector2 a = new(start.X, start.Z);
        Vector2 b = new(end.X, end.Z);
        Vector2 ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared <= 0.000001f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    private static void StartSuperMinionSwing(MinionState minion, BallState ball)
    {
        minion.SwingHand = ball.Position.X >= minion.Position.X ? HandedSwing.Left : HandedSwing.Right;
        minion.SwingHeight = Math.Clamp(ball.Position.Y - minion.Position.Y - 0.48f, 0.35f, 2.25f);
        minion.RacquetFaceAngle = -0.42f;
        minion.Swinging = true;
        minion.SwingProgress = SwingAnimation.DurationSeconds * 0.50f;
    }

    private bool LaunchSuperMinionReturn(MatchRoom room, PlayerState boss, MinionState minion, BallState ball)
    {
        var helper = new PlayerState
        {
            Id = boss.Id,
            Name = "Super Impossible Minion",
            Position = minion.Position,
            Grounded = minion.Position.Y <= 0.001f,
            Swinging = true,
            SwingProgress = SwingAnimation.DurationSeconds * 0.50f,
            SwingHand = minion.SwingHand,
            SwingHeight = minion.SwingHeight,
            RacquetHorizontal = 0f,
            RacquetFaceAngle = minion.RacquetFaceAngle,
            SwingPower = 1f,
            FacingYaw = ShotControls.EncodeVisualFacingYaw(boss.Id, ShotStyle.Forehand)
        };

        room.BotSuperSmashPlanned = false; // minions never use the flying smash path
        room.BotImpossibleShotStyle = ShotStyle.Forehand;
        return LaunchBotReturn(room, helper);
    }

    private static bool IsSuperMinionInterceptor(MatchRoom room, int id)
    {
        for (int i = 0; i < room.SuperMinionInterceptorCount; i++)
            if (room.SuperMinionInterceptors[i] == id) return true;
        return false;
    }

    private static void ClearSuperMinionInterceptors(MatchRoom room)
    {
        for (int i = 0; i < room.SuperMinionInterceptors.Length; i++)
            room.SuperMinionInterceptors[i] = -1;
        room.SuperMinionInterceptorCount = 0;
    }

    private static void RemoveSuperMinionInterceptor(MatchRoom room, int id)
    {
        int write = 0;
        for (int i = 0; i < room.SuperMinionInterceptorCount; i++)
        {
            int current = room.SuperMinionInterceptors[i];
            if (current == id) continue;
            room.SuperMinionInterceptors[write++] = current;
        }
        for (int i = write; i < room.SuperMinionInterceptors.Length; i++)
            room.SuperMinionInterceptors[i] = -1;
        room.SuperMinionInterceptorCount = write;
    }

    private void UpdateBot(MatchRoom room, float dt)
    {
        if (!room.BotEnabled) return;
        PlayerState? bot = room.World.Players.FirstOrDefault(p => p.Id == room.BotPlayerId);
        if (bot is null) return;

        // Super Impossible may hit ONLY when no minions are present. Survivors persist
        // across summon intervals, so any living helper keeps the boss passive.
        if (IsSuperImpossible(room.BotDifficulty) && room.World.Minions.Count > 0 &&
            room.World.Phase == MatchPhase.Rally)
        {
            ResetBotRallyState(room, bot);
            bot.Position = new Vector3(bot.Position.X, SuperImpossibleRestHoverHeight, bot.Position.Z);
            bot.Grounded = false;
            bot.Velocity = Vector3.Zero;
            return;
        }

        room.BotDecisionCooldown = Math.Max(0f, room.BotDecisionCooldown - dt);
        room.BotPlanRefreshTimer = Math.Max(0f, room.BotPlanRefreshTimer - dt);
        if (room.BotPlanReady)
            room.BotPlannedContactEta = Math.Max(0f, room.BotPlannedContactEta - dt);

        if (room.World.Phase == MatchPhase.ServeSetup &&
            room.World.Score.Server == bot.Id && !room.AutoServeActive)
        {
            StartBotServe(room, bot);
            return;
        }

        if (room.World.Phase == MatchPhase.ServeSetup &&
            room.World.Score.Server != bot.Id)
        {
            ResetBotRallyState(room, bot);
            bot.Position = BotPreferredRecoveryCenter(room, bot.Id);
            bot.Grounded = !IsSuperImpossible(room.BotDifficulty);
            return;
        }

        if (room.World.Phase != MatchPhase.Rally || !room.World.Ball.InPlay)
        {
            ResetBotRallyState(room, bot);
            return;
        }

        AdvanceBotSwing(room, bot, dt);

        BallState ball = room.World.Ball;
        int opponentId = 1 - bot.Id;
        bool incoming = ball.LastHitBy == opponentId;

        if (!incoming)
        {
            // Once the bot has returned the ball, recover toward the real center
            // baseline while the opponent is playing the next shot.
            room.BotTrackedLastHitBy = bot.Id;
            room.BotPlanReady = false;
            room.BotWaitForBounce = false;
            room.BotPredictedIncomingOut = false;

            if (!room.BotSwingActive)
            {
                if (IsSuperImpossible(room.BotDifficulty))
                {
                    bot.Position = new Vector3(bot.Position.X, SuperImpossibleRestHoverHeight, bot.Position.Z);
                    bot.Grounded = false;
                    bot.Velocity = new Vector3(bot.Velocity.X, 0f, bot.Velocity.Z);
                }
                MoveBotToward(bot, BotPreferredRecoveryCenter(room, bot.Id), 0.90f, dt);
            }
            return;
        }

        // One decision per new opponent stroke. Planning itself is refreshed
        // continuously; only the intentional reach/error decision is sticky.
        if (room.BotTrackedLastHitBy != opponentId)
            BeginBotIncomingShot(room, bot, ball);

        if (!room.BotShouldAttemptIncoming)
        {
            // Deliberately leave projected-out balls according to difficulty.
            if (!room.BotSwingActive)
            {
                if (IsSuperImpossible(room.BotDifficulty))
                {
                    bot.Position = new Vector3(bot.Position.X, SuperImpossibleRestHoverHeight, bot.Position.Z);
                    bot.Grounded = false;
                    bot.Velocity = new Vector3(bot.Velocity.X, 0f, bot.Velocity.Z);
                }
                MoveBotToward(bot, BotPreferredRecoveryCenter(room, bot.Id), 0.90f, dt);
            }
            return;
        }

        if (room.BotSwingActive)
        {
            // A human can keep moving during the 0.5 s swing, so the AI must be able
            // to finish the same physically possible path. Freezing the feet at swing
            // start left up to 0.35 m of planned travel unfinished and was a major
            // source of serve/topspin misses.
            float swingPathDistance = Vector2.Distance(
                new Vector2(bot.Position.X, bot.Position.Z),
                new Vector2(room.BotRunTarget.X, room.BotRunTarget.Z));
            float remainingToContact = Math.Max(GameConstants.FixedDt, room.BotPlannedContactEta);
            float swingPathSpeed = Math.Clamp(
                swingPathDistance / (GameConstants.PlayerSpeed * remainingToContact),
                0.50f,
                GameConstants.SprintMultiplier);
            if (IsImpossibleTier(room.BotDifficulty))
            {
                float targetY = IsSuperImpossible(room.BotDifficulty) ? MathF.Max(0f, room.BotRunTarget.Y) : bot.Position.Y;
                bot.Position = new Vector3(room.BotRunTarget.X, targetY, room.BotRunTarget.Z);
                bot.Grounded = targetY <= 0f;
                bot.Velocity = Vector3.Zero;
            }
            else
            {
                MoveBotToward(bot, room.BotRunTarget, swingPathSpeed, dt);
            }

            // Hand, height, body target and contact point are one locked solution.
            // SwingHeight changes the shaft orientation as well as Y, so changing it
            // after the planner computed BodyTarget silently moves the oval in X/Z and
            // invalidates an otherwise exact contact. The planner already searches the
            // full vertical range for every shot, so keep that per-shot height fixed
            // through the committed swing.
            bot.SwingHand = room.BotPlannedHand;
            bot.SwingHeight = room.BotPlannedSwingHeight;
            return;
        }

        // Serves always require their authoritative service bounce, regardless of
        // front-court/high-ball heuristics. For rally balls, BotWaitForBounce keeps
        // the existing tactical bounce preference.
        bool stillNeedsBounce = ball.ServeMustBounce ||
                                (room.BotWaitForBounce && ball.BounceCount == 0);

        // Fresh planner rule: failure to find an exact contact does NOT mean stand
        // still. First move toward a shared-physics staging position, then keep
        // replanning until an exact body/racquet contact becomes physically reachable.
        bool finalApproachLocked = room.BotPlanReady && room.BotPlannedContactEta <= 0.40f;
        if (!room.BotPlanReady || (room.BotPlanRefreshTimer <= 0f && !finalApproachLocked))
        {
            bool impossible = IsImpossibleTier(room.BotDifficulty);
            if (room.BotWillReach &&
                TryPlanFeasibleBotContact(room, ball, bot, stillNeedsBounce, out BotContactPlan exactPlan, impossible))
            {
                ApplyBotContactPlan(room, exactPlan);
                MaybeStartBotJump(bot, exactPlan.ContactPoint, exactPlan.Eta);
                if (impossible)
                {
                    // Impossible tiers teleport to the exact shared-flight solution.
                    // Super Impossible also uses the solved authoritative Y and then hovers.
                    float targetY = IsSuperImpossible(room.BotDifficulty) ? MathF.Max(0f, exactPlan.BodyTarget.Y) : bot.Position.Y;
                    bot.Position = new Vector3(exactPlan.BodyTarget.X, targetY, exactPlan.BodyTarget.Z);
                    bot.Grounded = targetY <= 0f;
                    bot.Velocity = new Vector3(0f, 0f, 0f);
                }
            }
            else
            {
                room.BotPlanReady = false;
                room.BotRunTarget = ComputeBotStagingTarget(ball, bot, stillNeedsBounce);
                room.BotPlannedContact = new Vector3(
                    room.BotRunTarget.X,
                    Math.Clamp(ball.Position.Y, 0.75f, 1.75f),
                    room.BotRunTarget.Z);
                room.BotPlannedContactEta = 0f;
            }

            room.BotPlanRefreshTimer = 0.03f;
        }

        Vector3 movementTarget = room.BotRunTarget;
        float speedFactor;

        if (!room.BotWillReach)
        {
            // The intentional 20% failure still looks like a real attempt. Offset
            // once from the genuine target and chase at half speed; never accumulate.
            float missDirection = movementTarget.X >= bot.Position.X ? -1f : 1f;
            movementTarget.X += missDirection * 2.6f;
            speedFactor = 0.50f;
        }
        else if (room.BotPlanReady)
        {
            float distance = Vector2.Distance(
                new Vector2(bot.Position.X, bot.Position.Z),
                new Vector2(movementTarget.X, movementTarget.Z));
            float movementSeconds = Math.Max(
                0.06f,
                room.BotPlannedContactEta - SwingAnimation.DurationSeconds * 0.50f);
            speedFactor = Math.Clamp(
                distance / (GameConstants.PlayerSpeed * movementSeconds),
                0.50f,
                GameConstants.SprintMultiplier);
        }
        else
        {
            // No exact contact yet: run decisively toward the projected lateral lane.
            // This is what makes a future exact contact feasible instead of waiting
            // motionless for a plan that can never become reachable.
            speedFactor = GameConstants.SprintMultiplier;
        }

        if (IsImpossibleTier(room.BotDifficulty) && room.BotPlanReady)
        {
            bot.Position = new Vector3(movementTarget.X, bot.Position.Y, movementTarget.Z);
            bot.Velocity = new Vector3(0f, bot.Velocity.Y, 0f);
        }
        else
        {
            MoveBotToward(bot, movementTarget, speedFactor, dt);
        }

        // Keep the visible racquet following the predicted lane. Once an exact plan
        // exists, calibrate to the actual future contact point and shared hitbox.
        Vector3 racquetTarget = room.BotPlanReady ? room.BotPlannedContact : new Vector3(
            room.BotRunTarget.X, Math.Clamp(ball.Position.Y, 0.75f, 2.20f), ball.Position.Z);
        if (room.BotPlanReady)
        {
            bot.SwingHand = room.BotPlannedHand;
            bot.SwingHeight = room.BotPlannedSwingHeight;
        }
        else
        {
            BallState targetBall = new BallState { Position = racquetTarget };
            bot.SwingHand = ChooseBotHand(bot, targetBall);
            bot.SwingHeight = CalibrateBotSwingHeight(bot, racquetTarget, bot.SwingHand);
        }
    }

    private void BeginBotIncomingShot(MatchRoom room, PlayerState bot, BallState ball)
    {
        room.BotTrackedLastHitBy = 1 - bot.Id;
        room.BotPlanReady = false;
        room.BotPlanRefreshTimer = 0f;
        room.BotSwingActive = false;
        room.BotBallLaunchedThisSwing = false;

        Vector3 projectedBounce = SimulateIncomingFirstBounce(ball);
        bool projectedIn = ball.ServeMustBounce
            ? IsProjectedServeBounceLegal(projectedBounce, ball.LastHitBy, ball.ServeTargetXSign)
            : IsProjectedBounceInForReceiver(projectedBounce, bot.Id);
        room.BotPredictedIncomingOut = !projectedIn;

        double hitOutBallChance = room.BotDifficulty switch
        {
            BotDifficulty.Easy => 0.50,
            BotDifficulty.Medium => 0.25,
            _ => 0.00
        };

        if (IsImpossibleTier(room.BotDifficulty))
        {
            // Impossible never volunteers to play an opponent ball that the shared
            // flight model already predicts will land out. Letting it go is the
            // perfect decision. For projected-in balls, reach is guaranteed.
            room.BotShouldAttemptIncoming = projectedIn;
            room.BotWillReach = projectedIn;
            room.BotImpossibleShotStyle = room.BotImpossibleNextSlice ? ShotStyle.Slice : ShotStyle.Forehand;
        }
        else if (room.BotPredictedIncomingOut)
        {
            room.BotShouldAttemptIncoming = random.NextDouble() < hitOutBallChance;
            room.BotWillReach = room.BotShouldAttemptIncoming;
        }
        else
        {
            room.BotShouldAttemptIncoming = true;
            room.BotWillReach = random.NextDouble() < 0.80;
        }

        bool frontCourt = IsBotInFrontCourt(bot);
        float projectedPeak = SimulateIncomingPeakHeightUntilBounce(ball);
        bool highVolleyOpportunity = !ball.ServeMustBounce && projectedIn && projectedPeak >= 2.75f;

        // A serve is NEVER a volley opportunity. The authoritative server requires
        // its first service-box bounce before the receiver may touch it, so the AI
        // planner must simulate through that exact same bounce before normal contact
        // planning begins. Rally balls retain the existing front-court/high-volley rule.
        room.BotWaitForBounce = IsImpossibleTier(room.BotDifficulty)
            ? ball.ServeMustBounce
            : ball.ServeMustBounce || (projectedIn && !frontCourt && !highVolleyOpportunity);

        ShotStyle incomingStyle = IsImpossibleTier(room.BotDifficulty)
            ? room.BotImpossibleShotStyle
            : ShotStyle.Forehand;
        bot.RacquetFaceAngle = room.BotDifficulty switch
        {
            BotDifficulty.SuperImpossible when incomingStyle == ShotStyle.Slice => 0.55f,
            BotDifficulty.SuperImpossible => -0.55f,
            BotDifficulty.Impossible when incomingStyle == ShotStyle.Slice => 0.55f,
            BotDifficulty.Impossible => -0.55f,
            BotDifficulty.Hard => -0.12f,
            _ => 0.02f
        };
        bot.RacquetHorizontal = 0f;
        bot.FacingYaw = ShotControls.EncodeVisualFacingYaw(bot.Id, incomingStyle);
    }

    private static void ApplyBotContactPlan(MatchRoom room, BotContactPlan plan)
    {
        room.BotPlanReady = true;
        room.BotPlannedContact = plan.ContactPoint;
        room.BotRunTarget = plan.BodyTarget;
        room.BotPlannedContactEta = plan.Eta;
        room.BotPlannedHand = plan.Hand;
        room.BotPlannedSwingHeight = plan.SwingHeight;
    }

    private static Vector3 ComputeBotStagingTarget(BallState ball, PlayerState bot, bool stillNeedsBounce)
    {
        Vector3 bounce = SimulateIncomingFirstBounce(ball);

        // Before the bounce, line up laterally with the predicted bounce while staying
        // near the baseline. This is a real tennis recovery/staging position and, more
        // importantly, it gives the exact planner a moving starting point.
        float targetX = Math.Clamp(
            bounce.X,
            -GameConstants.SinglesHalfWidth - 1.0f,
            GameConstants.SinglesHalfWidth + 1.0f);

        float baselineZ = bot.Id == 0 ? -GameConstants.CourtHalfLength : GameConstants.CourtHalfLength;
        float targetZ = baselineZ;

        if (!stillNeedsBounce)
        {
            // After the required bounce, move into the court toward the live ball lane
            // while leaving enough room for the racquet in front of the body.
            float liveZ = Math.Clamp(
                ball.Position.Z,
                bot.Id == 0 ? -GameConstants.CourtHalfLength - 1.5f : 0.9f,
                bot.Id == 0 ? -0.9f : GameConstants.CourtHalfLength + 1.5f);
            targetZ = baselineZ + (liveZ - baselineZ) * 0.45f;
        }

        return new Vector3(targetX, 0f, targetZ);
    }

    private static void ResetBotRallyState(MatchRoom room, PlayerState bot)
    {
        bot.Velocity = Vector3.Zero;
        float restY = IsSuperImpossible(room.BotDifficulty) ? SuperImpossibleRestHoverHeight : 0f;
        bot.Position = new Vector3(bot.Position.X, restY, bot.Position.Z);
        bot.Grounded = restY <= 0f;
        room.BotSuperSmashPlanned = false;
        room.BotPlanReady = false;
        room.BotWaitForBounce = false;
        room.BotTrackedLastHitBy = int.MinValue;
        room.BotSwingActive = false;
        room.BotBallLaunchedThisSwing = false;
        room.BotPlanRefreshTimer = 0f;
        room.BotShouldAttemptIncoming = true;
        room.BotWillReach = true;
        bot.Swinging = false;
        bot.SwingReleasedVisual = false;
        bot.SwingProgress = 0f;
    }

    private static void AdvanceBotSwing(MatchRoom room, PlayerState bot, float dt)
    {
        if (!room.BotSwingActive) return;

        room.BotSwingTimer += dt;
        bot.Swinging = room.BotSwingTimer < SwingAnimation.DurationSeconds;
        bot.SwingProgress = Math.Min(room.BotSwingTimer, SwingAnimation.DurationSeconds);
        bot.SwingReleasedVisual = room.BotSwingTimer >= SwingAnimation.DurationSeconds * 0.72f;

        if (room.BotSwingTimer >= SwingAnimation.DurationSeconds)
        {
            room.BotSwingActive = false;
            room.BotBallLaunchedThisSwing = false;
            bot.Swinging = false;
            bot.SwingReleasedVisual = false;
            bot.SwingProgress = 0f;
        }
    }

    private void UpdateBotAfterPhysics(MatchRoom room, float dt)
    {
        if (!room.BotEnabled || room.BotDecisionCooldown > 0f) return;
        if (room.World.Phase != MatchPhase.Rally || !room.World.Ball.InPlay) return;

        PlayerState? bot = room.World.Players.FirstOrDefault(p => p.Id == room.BotPlayerId);
        if (bot is null) return;

        BallState ball = room.World.Ball;
        int opponentId = 1 - bot.Id;
        if (ball.LastHitBy != opponentId ||
            !room.BotWillReach ||
            !room.BotShouldAttemptIncoming)
            return;

        bool onBotSide = bot.Id == 0 ? ball.Position.Z <= 0.35f : ball.Position.Z >= -0.35f;
        if (!onBotSide) return;
        // The live server owns serve legality through ServeMustBounce. Use that
        // authoritative flag directly so AI planning/contact can never treat an
        // airborne serve as playable.
        bool waitingForRequiredBounce = ball.ServeMustBounce ||
                                        (room.BotWaitForBounce && ball.BounceCount == 0);

        if (room.BotSwingActive)
        {
            // The bot is allowed to PREPARE a return before a required serve/rally
            // bounce, but it may not actually contact the ball until that bounce has
            // occurred. This lets a fast serve meet the racquet near mid-swing instead
            // of forcing the bot to begin a 0.5 s stroke only after the bounce.
            if (waitingForRequiredBounce || ball.ServeMustBounce)
                return;

            InputPacket botInput = BuildBotRacquetInput(bot);
            if (!room.BotBallLaunchedThisSwing &&
                room.Physics.IsRacquetContact(room.World, bot, botInput))
            {
                if (!LaunchBotReturn(room, bot))
                {
                    room.BotSwingActive = false;
                    room.BotPlanReady = false;
                    bot.Swinging = false;
                    bot.SwingProgress = 0f;
                    return;
                }
                room.BotBallLaunchedThisSwing = true;
                room.BotDecisionCooldown = 0.18f;
                room.BotPlanReady = false;
            }
            return;
        }

        // Keep replanning while the shot is still far away, but lock the final
        // approach once contact is within 0.40 s. Movement and swing timing must then
        // solve the SAME body/contact target instead of chasing a target that changes
        // every physics frame.
        BotContactPlan livePlan;
        if (room.BotPlanReady && room.BotPlannedContactEta <= 0.40f)
        {
            livePlan = new BotContactPlan(
                room.BotPlannedContact, room.BotRunTarget, room.BotPlannedContactEta, 0.50f,
                room.BotPlannedHand, room.BotPlannedSwingHeight);
        }
        else
        {
            if (!TryPlanFeasibleBotContact(room, ball, bot, waitingForRequiredBounce, out livePlan,
                    IsImpossibleTier(room.BotDifficulty)))
            {
                room.BotPlanReady = false;
                return;
            }
            ApplyBotContactPlan(room, livePlan);
            MaybeStartBotJump(bot, livePlan.ContactPoint, livePlan.Eta);
        }

        const float desiredContactAtSwingSeconds = SwingAnimation.DurationSeconds * 0.50f;
        float bodyDistance = Vector2.Distance(
            new Vector2(bot.Position.X, bot.Position.Z),
            new Vector2(livePlan.BodyTarget.X, livePlan.BodyTarget.Z));

        bool canPrepareSwing =
            livePlan.Eta <= desiredContactAtSwingSeconds + GameConstants.FixedDt * 2f &&
            bodyDistance <= 0.35f &&
            livePlan.ContactPoint.Y >= 0.15f &&
            livePlan.ContactPoint.Y <= 3.65f + bot.Position.Y;

        if (!canPrepareSwing)
        {
            // If a locked plan has already passed, release it immediately so the
            // next frame can search for a later physically reachable contact.
            if (room.BotPlanReady && room.BotPlannedContactEta <= 0.05f)
                room.BotPlanReady = false;
            return;
        }

        room.BotSwingActive = true;
        room.BotSwingTimer = 0f;
        room.BotBallLaunchedThisSwing = false;
        bot.Swinging = true;
        bot.SwingProgress = 0f;
        bot.SwingReleasedVisual = false;
        bot.SwingHand = livePlan.Hand;
        bot.SwingHeight = livePlan.SwingHeight;
        ShotStyle swingStyle = IsImpossibleTier(room.BotDifficulty)
            ? room.BotImpossibleShotStyle
            : ShotStyle.Forehand;
        bot.RacquetFaceAngle = room.BotDifficulty switch
        {
            BotDifficulty.SuperImpossible when room.BotSuperSmashPlanned => -0.86f,
            BotDifficulty.SuperImpossible when swingStyle == ShotStyle.Slice => 0.55f,
            BotDifficulty.SuperImpossible => -0.55f,
            BotDifficulty.Impossible when swingStyle == ShotStyle.Slice => 0.55f,
            BotDifficulty.Impossible => -0.55f,
            BotDifficulty.Hard => -0.12f,
            _ => 0.02f
        };
        bot.RacquetHorizontal = 0f;
        bot.FacingYaw = ShotControls.EncodeVisualFacingYaw(bot.Id, swingStyle);
    }

    private bool TryImpossibleLiveContact(MatchRoom room, PlayerState bot, BallState ball)
    {
        bool playableHeight = ball.Position.Y >= 0.18f && ball.Position.Y <= 3.65f + bot.Position.Y;
        if (!playableHeight) return false;

        HandedSwing preferred = PreferredBotHandForContact(bot, ball.Position);
        HandedSwing other = preferred == HandedSwing.Left ? HandedSwing.Right : HandedSwing.Left;
        foreach (HandedSwing hand in new[] { preferred, other })
        {
            Vector3 bodyTarget = ComputeBotBodyTargetForContact(bot, ball.Position, hand, out float swingHeight);

            bot.Position = new Vector3(bodyTarget.X, bot.Position.Y, bodyTarget.Z);
            bot.Velocity = new Vector3(0f, bot.Velocity.Y, 0f);
            bot.SwingHand = hand;
            bot.SwingHeight = swingHeight;
            bot.Swinging = true;
            bot.SwingProgress = SwingAnimation.DurationSeconds * 0.50f;
            bot.SwingReleasedVisual = false;
            room.BotSwingActive = true;
            room.BotSwingTimer = bot.SwingProgress;
            room.BotBallLaunchedThisSwing = false;
            room.BotRunTarget = bodyTarget;
            room.BotPlannedContact = ball.Position;
            room.BotPlannedContactEta = 0f;
            room.BotPlannedHand = hand;
            room.BotPlannedSwingHeight = swingHeight;

            InputPacket input = BuildBotRacquetInput(bot);
            if (!room.Physics.IsRacquetContact(room.World, bot, input)) continue;

            if (!LaunchBotReturn(room, bot))
            {
                room.BotSwingActive = false;
                bot.Swinging = false;
                bot.SwingProgress = 0f;
                continue;
            }
            room.BotBallLaunchedThisSwing = true;
            room.BotDecisionCooldown = 0.05f;
            room.BotPlanReady = false;
            return true;
        }
        return false;
    }

    private static InputPacket BuildBotRacquetInput(PlayerState bot)
    {
        return new InputPacket(
            bot.LastInputSequence, 0f, 0f,
            ShotControls.EncodeStyle(ShotControls.DecodeVisualStyle(bot.FacingYaw)), 1f,
            true, false, bot.SwingHand, bot.SwingHeight,
            true, bot.RacquetHorizontal, bot.RacquetFaceAngle,
            2200f, 0f, false, 0f, 0f);
    }

    private static HandedSwing ChooseBotHand(PlayerState bot, BallState ball) =>
        PreferredBotHandForContact(bot, ball.Position);

    private static HandedSwing PreferredBotHandForContact(PlayerState bot, Vector3 contactPoint)
    {
        // Player-relative A/D semantics are mirrored in world X:
        //   player 0: +X is the player's LEFT side
        //   player 1: -X is the player's LEFT side
        // The old sign was reversed, so the staging racquet often favored the wrong
        // hand. This helper matches the exact same handedness convention as the client.
        float playerLeft = (contactPoint.X - bot.Position.X) * (bot.Id == 0 ? 1f : -1f);
        return playerLeft >= 0f ? HandedSwing.Left : HandedSwing.Right;
    }

    private static void MaybeStartBotJump(PlayerState bot, Vector3 contactPoint, float eta)
    {
        if (!bot.Grounded) return;
        bool highContact = contactPoint.Y >= 2.85f;
        bool frontCourt = MathF.Abs(contactPoint.Z) <= GameConstants.CourtHalfLength * 0.62f;
        // Jump close enough to the planned contact that the racquet is still elevated
        // when the ball arrives. Apex is roughly 0.55 s with PlayerJumpSpeed=5.4.
        bool jumpTimingUseful = eta <= 0.68f;
        if (!highContact || !frontCourt || !jumpTimingUseful) return;
        bot.Grounded = false;
        bot.Velocity = new Vector3(bot.Velocity.X, GameConstants.PlayerJumpSpeed, bot.Velocity.Z);
    }

    private static void MoveBotToward(PlayerState bot, Vector3 target,
        float speedFactor, float dt)
    {
        Vector3 delta = new Vector3(target.X - bot.Position.X, 0f, target.Z - bot.Position.Z);
        float distance = delta.Length();
        if (distance < 0.03f)
        {
            bot.Position = new Vector3(target.X, bot.Position.Y, target.Z);
            bot.Velocity = new Vector3(0f, bot.Velocity.Y, 0f);
            return;
        }

        Vector3 direction = delta / distance;
        float speed = GameConstants.PlayerSpeed * Math.Clamp(speedFactor, 0.5f, GameConstants.SprintMultiplier);
        float travel = Math.Min(distance, speed * dt);
        bot.Position += direction * travel;
        Vector3 horizontalBotVelocity = travel >= distance - 0.0001f
            ? Vector3.Zero
            : direction * speed;
        bot.Velocity = new Vector3(horizontalBotVelocity.X, bot.Velocity.Y, horizontalBotVelocity.Z);

        float minZ = bot.Id == 0 ? -GameConstants.CourtHalfLength - 3f : 0.75f;
        float maxZ = bot.Id == 0 ? -0.75f : GameConstants.CourtHalfLength + 3f;
        bot.Position = new Vector3(
            Math.Clamp(bot.Position.X, -GameConstants.CourtHalfWidth - 2.5f, GameConstants.CourtHalfWidth + 2.5f),
            bot.Position.Y,
            Math.Clamp(bot.Position.Z, minZ, maxZ));
    }

    private static Vector3 BotBaselineCenter(int botId) =>
        new Vector3(0f, 0f, botId == 0 ? -GameConstants.CourtHalfLength : GameConstants.CourtHalfLength);

    private static Vector3 BotPreferredRecoveryCenter(MatchRoom room, int botId)
    {
        Vector3 baseline = BotBaselineCenter(botId);
        return IsSuperImpossible(room.BotDifficulty)
            ? new Vector3(baseline.X, SuperImpossibleRestHoverHeight, baseline.Z)
            : baseline;
    }

    private static bool IsBotInFrontCourt(PlayerState bot)
    {
        // Front half of a player's court is the half closer to the net.
        return MathF.Abs(bot.Position.Z) <= GameConstants.CourtHalfLength * 0.5f;
    }

    private readonly record struct BotContactPlan(
        Vector3 ContactPoint, Vector3 BodyTarget, float Eta, float RequiredSpeedFactor,
        HandedSwing Hand, float SwingHeight);

    private bool TryPlanFeasibleBotContact(
        MatchRoom room, BallState ball, PlayerState bot, bool requireBounce, out BotContactPlan plan,
        bool ignoreMovementLimits = false)
    {
        if (IsSuperImpossible(room.BotDifficulty) &&
            TryPlanSuperImpossibleSmashContact(room, ball, bot, requireBounce, out plan))
        {
            room.BotSuperSmashPlanned = true;
            return true;
        }
        room.BotSuperSmashPlanned = false;

        // The live game and this planner both step BallFlightModel.AdvanceStep directly.
        // Impossible is allowed to search the whole legal window through the first
        // bounce and up to (but never including) bounce #2. Lower difficulties keep
        // their original earliest-feasible-contact behavior.
        Vector3 p = ball.Position;
        Vector3 v = ball.Velocity;
        Vector3 spin = ball.AngularVelocity;
        int bounceCount = ball.BounceCount;
        bool serveMustBounce = ball.ServeMustBounce;
        bool bounceSatisfied = !requireBounce || (bounceCount > 0 && !serveMustBounce);
        const float step = GameConstants.FixedDt;
        const float desiredContactAtSwingSeconds = SwingAnimation.DurationSeconds * 0.50f;

        // For Impossible, collect a bounded set of geometric contact candidates across
        // the legal window. Outgoing-shot validation is deliberately deferred until the
        // actual hit so planning can never multiply into nested trajectory searches.
        var impossibleCandidates = new List<(float Score, BotContactPlan Plan, Vector3 IncomingVelocity)>(256);

        for (int i = 1; i <= 1500; i++)
        {
            float eta = i * step;
            BallFlightEvents events = BallFlightModel.AdvanceStep(
                ref p, ref v, ref spin, ref bounceCount, ref serveMustBounce, step);

            if ((events & BallFlightEvents.ServeNetFault) != 0)
                break;

            if ((events & BallFlightEvents.GroundBounce) != 0)
            {
                if (!bounceSatisfied)
                {
                    bounceSatisfied = true;
                    continue;
                }
                if (bounceCount >= 2)
                    break;
                if (!requireBounce && !ignoreMovementLimits)
                    break;
            }

            if (!bounceSatisfied)
                continue;

            // Impossible tiers teleport, so evaluating every fourth 120 Hz flight sample
            // is still sub-frame accurate for visuals while bounding CPU cost.
            if (ignoreMovementLimits && (i & 3) != 0 &&
                (events & BallFlightEvents.GroundBounce) == 0)
                continue;

            bool onBotSide = bot.Id == 0 ? p.Z < -0.20f : p.Z > 0.20f;
            // Every bot may use the shared jump mechanic for high front-court balls.
            // Impossible can teleport as well, but the vertical reach ceiling is the
            // same jump-assisted physical racquet range for all bots.
            float maxPlayableHeight = 4.65f;
            bool playableHeight = p.Y >= 0.18f && p.Y <= maxPlayableHeight;
            if (!onBotSide || !playableHeight)
                continue;

            float movementSeconds = eta - desiredContactAtSwingSeconds;
            bool foundHand = false;
            Vector3 bestBodyTarget = default;
            HandedSwing bestHand = HandedSwing.Right;
            float bestSwingHeight = 1.05f;
            float bestRequiredSpeedFactor = float.PositiveInfinity;
            HandedSwing preferredHand = PreferredBotHandForContact(bot, p);
            HandedSwing otherHand = preferredHand == HandedSwing.Left
                ? HandedSwing.Right
                : HandedSwing.Left;

            if (ignoreMovementLimits)
            {
                // Allocation-free coarse geometry for teleporting tiers. Precise shared
                // racquet geometry is solved once after the best contact is selected.
                bestHand = preferredHand;
                bestSwingHeight = Math.Clamp(p.Y - bot.Position.Y - 0.45f, 0.25f, 2.65f);
                float lateral = bestHand == HandedSwing.Left ? -0.55f : 0.55f;
                float forward = bot.Id == 0 ? -0.42f : 0.42f;
                bestBodyTarget = new Vector3(p.X + lateral, 0f, p.Z + forward);
                bestRequiredSpeedFactor = 0.50f;
                foundHand = true;
            }
            else
            {
                for (int handIndex = 0; handIndex < 2; handIndex++)
                {
                    HandedSwing hand = handIndex == 0 ? preferredHand : otherHand;
                    Vector3 bodyTarget = ComputeBotBodyTargetForContact(bot, p, hand, out float swingHeight);
                    if (!IsBotBodyTargetAllowed(bot.Id, bodyTarget))
                        continue;

                    float distance = Vector2.Distance(
                        new Vector2(bot.Position.X, bot.Position.Z),
                        new Vector2(bodyTarget.X, bodyTarget.Z));

                    float requiredSpeedFactor;
                    if (movementSeconds <= GameConstants.FixedDt * 2f)
                    {
                        if (distance > 0.35f)
                            continue;
                        requiredSpeedFactor = 0.50f;
                    }
                    else
                    {
                        requiredSpeedFactor = distance / (GameConstants.PlayerSpeed * movementSeconds);
                        if (requiredSpeedFactor > GameConstants.SprintMultiplier + 0.02f)
                            continue;
                    }

                    const float handSwitchAdvantage = 0.03f;
                    bool materiallyEasier = requiredSpeedFactor < bestRequiredSpeedFactor - handSwitchAdvantage;
                    bool effectivelyTied = MathF.Abs(requiredSpeedFactor - bestRequiredSpeedFactor) <= handSwitchAdvantage;
                    bool preferredTieBreak = effectivelyTied && hand == preferredHand && bestHand != preferredHand;
                    if (!foundHand || materiallyEasier || preferredTieBreak)
                    {
                        foundHand = true;
                        bestBodyTarget = bodyTarget;
                        bestHand = hand;
                        bestSwingHeight = swingHeight;
                        bestRequiredSpeedFactor = requiredSpeedFactor;
                    }
                }
            }

            if (!foundHand)
                continue;

            BotContactPlan candidate = new(
                p, bestBodyTarget, eta,
                Math.Clamp(bestRequiredSpeedFactor, 0.5f, GameConstants.SprintMultiplier),
                bestHand, bestSwingHeight);

            if (!ignoreMovementLimits)
            {
                plan = candidate;
                return true;
            }

            // Impossible can teleport, so don't force the earliest volley. Prefer a
            // legal post-bounce contact and a body position nearer the baseline. This
            // lets it back up and strike a safer ball any time before bounce #2.
            float baselineDistance = MathF.Abs(MathF.Abs(bestBodyTarget.Z) - GameConstants.CourtHalfLength);
            float postBounceBonus = bounceCount >= 1 ? -4.0f : 0f;
            float frontCourtPenalty = MathF.Max(0f, GameConstants.CourtHalfLength * 0.62f - MathF.Abs(bestBodyTarget.Z)) * 0.55f;
            float highContactPenalty = MathF.Max(0f, p.Y - 2.8f) * 0.40f;
            float score = baselineDistance + frontCourtPenalty + highContactPenalty + postBounceBonus + eta * 0.01f;
            bool importantSample = (events & BallFlightEvents.GroundBounce) != 0 || (i & 3) == 0;
            if (importantSample)
                impossibleCandidates.Add((score, candidate, v));
        }

        if (ignoreMovementLimits && impossibleCandidates.Count > 0)
        {
            // Real-time safety: never run an outgoing-shot brute-force solver from inside
            // the contact planner.  That used to multiply the future-contact search by
            // dozens of full return simulations and could stall the authoritative 120 Hz
            // loop.  Impossible tiers can teleport, so choose the best geometric contact
            // here and validate/build the outgoing return exactly once at actual contact.
            int bestIndex = 0;
            float bestScore = impossibleCandidates[0].Score;
            for (int i = 1; i < impossibleCandidates.Count; i++)
            {
                if (impossibleCandidates[i].Score < bestScore)
                {
                    bestScore = impossibleCandidates[i].Score;
                    bestIndex = i;
                }
            }
            BotContactPlan coarse = impossibleCandidates[bestIndex].Plan;
            Vector3 exactBody = ComputeBotBodyTargetForContact(
                bot, coarse.ContactPoint, coarse.Hand, out float exactSwingHeight);
            plan = coarse with { BodyTarget = exactBody, SwingHeight = exactSwingHeight };
            return true;
        }

        plan = default;
        return false;
    }

    private bool TryPlanSuperImpossibleSmashContact(
        MatchRoom room, BallState ball, PlayerState bot, bool requireBounce, out BotContactPlan plan)
    {
        // Real-time planner: search only contact geometry here.  The previous version
        // also solved and simulated a full smash return for nearly every future contact
        // sample, which could create millions of physics iterations in one server tick.
        // Outgoing trajectory validation now happens once, when the racquet actually hits.
        Vector3 p = ball.Position;
        Vector3 v = ball.Velocity;
        Vector3 spin = ball.AngularVelocity;
        int bounceCount = ball.BounceCount;
        bool serveMustBounce = ball.ServeMustBounce;
        bool bounceSatisfied = !requireBounce || (bounceCount > 0 && !serveMustBounce);
        const float step = GameConstants.FixedDt;

        bool found = false;
        float bestScore = float.PositiveInfinity;
        BotContactPlan best = default;

        for (int i = 1; i <= 1500; i++)
        {
            float eta = i * step;
            BallFlightEvents events = BallFlightModel.AdvanceStep(
                ref p, ref v, ref spin, ref bounceCount, ref serveMustBounce, step);
            if ((events & BallFlightEvents.ServeNetFault) != 0) break;
            if ((events & BallFlightEvents.GroundBounce) != 0)
            {
                if (!bounceSatisfied) { bounceSatisfied = true; continue; }
                if (bounceCount >= 2) break;
            }
            if (!bounceSatisfied) continue;

            // Evaluate at 30 Hz instead of every 120 Hz simulation sample. Teleporting
            // gives Super Impossible far more than enough positional precision, while
            // this hard bound keeps planning cost predictable.
            if ((i & 3) != 0 && (events & BallFlightEvents.GroundBounce) == 0) continue;

            bool onBotSide = bot.Id == 0 ? p.Z < -0.20f : p.Z > 0.20f;
            if (!onBotSide || p.Y < 1.55f) continue;

            HandedSwing hand = PreferredBotHandForContact(bot, p);
            // Coarse allocation-free body estimate while scanning. The exact airborne
            // racquet offset is computed only once for the winning contact below.
            float lateral = hand == HandedSwing.Left ? -0.50f : 0.50f;
            float forward = bot.Id == 0 ? -0.38f : 0.38f;
            Vector3 bodyTarget = new(p.X + lateral, Math.Max(0.10f, p.Y - 1.35f), p.Z + forward);
            float swingHeight = 1.05f;

            float frontBonus = MathF.Abs(p.Z) <= GameConstants.CourtHalfLength * 0.62f ? -1.0f : 0f;
            float score = -p.Y * 3.5f + frontBonus + eta * 0.05f;
            if (!found || score < bestScore)
            {
                bestScore = score;
                best = new BotContactPlan(p, bodyTarget, eta, 0.5f, hand, swingHeight);
                found = true;
            }
        }

        if (found)
        {
            Vector3 exactBody = ComputeSuperImpossibleAirBodyTarget(
                bot, best.ContactPoint, best.Hand, room.BotImpossibleShotStyle, out float exactSwingHeight);
            if (exactBody.Y < 0.05f)
            {
                HandedSwing other = best.Hand == HandedSwing.Left ? HandedSwing.Right : HandedSwing.Left;
                exactBody = ComputeSuperImpossibleAirBodyTarget(
                    bot, best.ContactPoint, other, room.BotImpossibleShotStyle, out exactSwingHeight);
                best = best with { Hand = other };
            }
            if (exactBody.Y < 0.05f) exactBody = new Vector3(exactBody.X, 0.10f, exactBody.Z);
            best = best with { BodyTarget = exactBody, SwingHeight = exactSwingHeight };
        }
        plan = best;
        return found;
    }

    private static Vector3 ComputeSuperImpossibleAirBodyTarget(
        PlayerState bot, Vector3 contactPoint, HandedSwing hand, ShotStyle style, out float swingHeight)
    {
        // Solve all three axes from the actual authoritative racquet hitbox at the
        // middle of the swing. This makes the airborne teleport a real collision,
        // not merely a visual character offset.
        swingHeight = 1.05f;
        var sampleBot = new PlayerState
        {
            Id = bot.Id,
            Position = Vector3.Zero,
            Swinging = true,
            SwingProgress = SwingAnimation.DurationSeconds * 0.50f,
            SwingHand = hand,
            SwingHeight = swingHeight,
            RacquetFaceAngle = -0.86f,
            FacingYaw = ShotControls.EncodeVisualFacingYaw(bot.Id, style)
        };
        InputPacket sampleInput = BuildBotRacquetInput(sampleBot);
        RacquetHitboxPose hitbox = RacquetGeometry.GetHitbox(sampleBot, sampleInput);
        Vector3 offset = hitbox.Center - sampleBot.Position;
        return contactPoint - offset;
    }

    private static bool TryComputeSuperImpossibleSmashReturn(
        Vector3 start, Vector3 desiredTarget, float desiredPace, Vector3 desiredSpin, int botId,
        out Vector3 velocity, out Vector3 spin)
    {
        // Strictly bounded smash solver.  The old 3x6x5x7 nested search was invoked
        // from planning as well as contact and could monopolize a server tick.
        velocity = Vector3.Zero;
        spin = Vector3.Zero;
        float zSign = botId == 0 ? 1f : -1f;
        Vector3 corner = ClampLandingToOpponentCourt(desiredTarget, botId, 0.42f);
        Vector3[] targets =
        {
            corner,
            new Vector3(corner.X * 0.90f, 0f, zSign * (GameConstants.CourtHalfLength - 0.90f)),
            new Vector3(0f, 0f, zSign * (GameConstants.CourtHalfLength - 1.35f))
        };
        float[] horizontalScales = { 0.76f, 0.90f, 1.02f };
        float[] downwardSpeeds = { -8.5f, -6.0f, -3.5f, -1.0f };
        float[] spinScales = { 0.75f, 0.25f };

        float bestScore = float.PositiveInfinity;
        bool found = false;
        foreach (Vector3 target in targets)
        {
            Vector3 hd = new(target.X - start.X, 0f, target.Z - start.Z);
            if (hd.LengthSquared() < 0.0001f) continue;
            Vector3 dir = Vector3.Normalize(hd);
            foreach (float spinScale in spinScales)
            foreach (float horizontalScale in horizontalScales)
            foreach (float vy in downwardSpeeds)
            {
                Vector3 candidate = dir * (desiredPace * horizontalScale) + Vector3.UnitY * vy;
                Vector3 candidateSpin = desiredSpin * spinScale;
                if (!SimulateBotShotFirstBounce(start, candidate, candidateSpin,
                        out Vector3 bounce, out float netClearance, out float apex)) continue;
                bool safelyInside = MathF.Abs(bounce.X) <= GameConstants.SinglesHalfWidth - 0.32f &&
                                    MathF.Abs(bounce.Z) <= GameConstants.CourtHalfLength - 0.34f &&
                                    (botId == 0 ? bounce.Z > 0.55f : bounce.Z < -0.55f);
                if (!safelyInside || netClearance < 0.12f) continue;
                float targetError = Vector2.Distance(
                    new Vector2(bounce.X, bounce.Z), new Vector2(corner.X, corner.Z));
                float score = targetError + MathF.Max(0f, apex - start.Y) * 0.05f;
                if (score < bestScore)
                {
                    bestScore = score;
                    velocity = candidate;
                    spin = candidateSpin;
                    found = true;
                }
            }
            if (found) return true;
        }

        // Cheap non-smash safety fallback; bounded solver below has its own validation.
        return TryComputeGuaranteedImpossibleReturn(
            start, new Vector3(0f, 0f, zSign * 7.4f), MathF.Min(desiredPace, 34f),
            Vector3.Zero, botId, out velocity, out spin);
    }

    private static bool IsBotBodyTargetAllowed(int botId, Vector3 bodyTarget)
    {
        if (MathF.Abs(bodyTarget.X) > GameConstants.CourtHalfWidth + 2.5f)
            return false;

        float minZ = botId == 0 ? -GameConstants.CourtHalfLength - 3f : 0.75f;
        float maxZ = botId == 0 ? -0.75f : GameConstants.CourtHalfLength + 3f;
        return bodyTarget.Z >= minZ && bodyTarget.Z <= maxZ;
    }

    private static float SimulateIncomingPeakHeightUntilBounce(BallState ball)
    {
        Vector3 p = ball.Position;
        Vector3 v = ball.Velocity;
        Vector3 spin = ball.AngularVelocity;
        int bounceCount = ball.BounceCount;
        bool serveMustBounce = ball.ServeMustBounce;
        float peak = p.Y;
        const float step = GameConstants.FixedDt;

        for (int i = 0; i < 900; i++)
        {
            BallFlightEvents events = BallFlightModel.AdvanceStep(
                ref p, ref v, ref spin, ref bounceCount, ref serveMustBounce, step);
            if ((events & BallFlightEvents.ServeNetFault) != 0)
                break;
            peak = Math.Max(peak, p.Y);
            if ((events & BallFlightEvents.GroundBounce) != 0)
                break;
        }
        return peak;
    }

    private static float CalibrateBotSwingHeight(PlayerState bot, Vector3 contactPoint, HandedSwing hand)
    {
        // This now runs only for the final chosen contact, so retain the original
        // exhaustive shared-geometry calibration for maximum correctness.
        float bestHeight = 1.05f;
        float bestError = float.PositiveInfinity;
        for (float candidateHeight = 0.25f; candidateHeight <= 2.65f; candidateHeight += 0.05f)
        {
            var sampleBot = new PlayerState
            {
                Id = bot.Id,
                Position = Vector3.Zero,
                Swinging = true,
                SwingProgress = SwingAnimation.DurationSeconds * 0.50f,
                SwingHand = hand,
                SwingHeight = candidateHeight,
                RacquetFaceAngle = bot.RacquetFaceAngle,
                FacingYaw = bot.FacingYaw
            };
            InputPacket sampleInput = BuildBotRacquetInput(sampleBot);
            RacquetHitboxPose hitbox = RacquetGeometry.GetHitbox(sampleBot, sampleInput);
            float relativeContactY = contactPoint.Y - bot.Position.Y;
            float error = MathF.Abs(hitbox.Center.Y - relativeContactY);
            if (error < bestError)
            {
                bestError = error;
                bestHeight = candidateHeight;
            }
        }
        return bestHeight;
    }

    private static Vector3 ComputeBotBodyTargetForContact(
        PlayerState bot, Vector3 contactPoint, HandedSwing hand, out float baseHeight)
    {
        baseHeight = CalibrateBotSwingHeight(bot, contactPoint, hand);
        var sampleBot = new PlayerState
        {
            Id = bot.Id,
            Position = Vector3.Zero,
            Swinging = true,
            SwingProgress = SwingAnimation.DurationSeconds * 0.50f,
            SwingHand = hand,
            SwingHeight = baseHeight,
            RacquetFaceAngle = bot.RacquetFaceAngle,
            FacingYaw = bot.FacingYaw
        };
        InputPacket sampleInput = BuildBotRacquetInput(sampleBot);
        RacquetHitboxPose sampleHitbox = RacquetGeometry.GetHitbox(sampleBot, sampleInput);
        Vector3 racquetOffset = sampleHitbox.Center - sampleBot.Position;

        return new Vector3(contactPoint.X - racquetOffset.X, 0f, contactPoint.Z - racquetOffset.Z);
    }

    private void StartBotServe(MatchRoom room, PlayerState bot)
    {
        // Super Impossible normally hovers, but deliberately lands for its conventional
        // automatic serve. It resumes the preferred airborne rest position afterward.
        if (IsSuperImpossible(room.BotDifficulty))
        {
            bot.Position = new Vector3(bot.Position.X, 0f, bot.Position.Z);
            bot.Velocity = new Vector3(bot.Velocity.X, 0f, bot.Velocity.Z);
            bot.Grounded = true;
        }

        float serveFromXSign = TennisRules.ServeFromXSign(room.World.Score, bot.Id);
        int targetXSign = serveFromXSign > 0f ? -1 : 1;
        float safeX = targetXSign * (room.BotDifficulty == BotDifficulty.Hard ? 3.35f : 2.0f);
        float safeAbsZ = room.BotDifficulty == BotDifficulty.Hard ? 5.7f : 4.4f;
        float targetZ = bot.Id == 0 ? safeAbsZ : -safeAbsZ;

        float tossHandSide = bot.Id == 0 ? -0.22f : 0.22f;
        room.World.Ball.Position = new Vector3(bot.Position.X + tossHandSide, 1.15f, bot.Position.Z);
        const float standardTossApex = 3.15f;
        float rise = standardTossApex - room.World.Ball.Position.Y;
        float launchSpeed = MathF.Sqrt(2f * GameConstants.Gravity * rise);
        room.World.Ball.Velocity = new Vector3(0f, launchSpeed, 0f);
        room.World.Ball.AngularVelocity = Vector3.Zero;
        room.World.Ball.InPlay = true;
        room.World.Ball.IsServeToss = true;
        room.World.Ball.TossOwner = bot.Id;
        room.World.Ball.SelectedTossHeight = targetZ;
        room.World.Ball.SelectedTossX = safeX;
        room.World.Ball.TossReachedApex = false;
        room.World.Ball.ServeTargetXSign = targetXSign;
        room.World.Phase = MatchPhase.ServeToss;
        room.AutoServeActive = true;
        room.AutoServeBallLaunched = false;
        room.AutoServeTimer = 0f;
        bot.Swinging = true;
        bot.SwingProgress = 0f;
        bot.SwingPower = 1f;
        bot.FacingYaw = ShotControls.EncodeVisualFacingYaw(bot.Id, ShotStyle.Forehand);
        room.World.Message = $"AI {room.BotDifficulty} serves.";
    }

    private bool LaunchBotReturn(MatchRoom room, PlayerState bot)
    {
        PlayerState? human = room.World.Players.FirstOrDefault(p => p.Id != bot.Id);
        if (human is null) return false;

        Vector3 target;
        float pace;
        Vector3 spin = Vector3.Zero;
        bool requireIn = true;

        switch (room.BotDifficulty)
        {
            case BotDifficulty.Easy:
            {
                // Easy aims at/near the player with up to 1.5 current visual racquet
                // lengths of error. This follows v2.1's smaller racquet automatically.
                float easyErrorRadius = RacquetGeometry.VisualLongLength * 1.5f;
                float a = random.NextSingle() * MathF.Tau;
                float r = MathF.Sqrt(random.NextSingle()) * easyErrorRadius;
                target = human.Position + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
                target = ClampLandingToOpponentCourt(target, bot.Id, 0.35f);
                pace = 20f;
                spin = new Vector3(35f, 0f, 0f);
                break;
            }
            case BotDifficulty.Medium:
            {
                bool intentionallyOut = random.NextDouble() < 0.25;
                requireIn = !intentionallyOut;
                if (intentionallyOut)
                {
                    bool sideOut = random.Next(2) == 0;
                    float side = random.Next(2) == 0 ? -1f : 1f;
                    target = sideOut
                        ? new Vector3(side * (GameConstants.SinglesHalfWidth + 0.8f + random.NextSingle() * 1.2f), 0f,
                            bot.Id == 0 ? 6f : -6f)
                        : new Vector3((random.NextSingle() * 2f - 1f) * GameConstants.SinglesHalfWidth, 0f,
                            bot.Id == 0 ? GameConstants.CourtHalfLength + 1.2f : -GameConstants.CourtHalfLength - 1.2f);
                }
                else
                {
                    target = new Vector3(
                        (random.NextSingle() * 2f - 1f) * (GameConstants.SinglesHalfWidth - 0.45f),
                        0f,
                        bot.Id == 0
                            ? 2.2f + random.NextSingle() * (GameConstants.CourtHalfLength - 3.0f)
                            : -(2.2f + random.NextSingle() * (GameConstants.CourtHalfLength - 3.0f)));
                }
                pace = 25f;
                spin = new Vector3(45f, 0f, 0f);
                break;
            }
            case BotDifficulty.Hard:
            {
                bool shouldBeIn = random.NextDouble() < 0.95;
                requireIn = shouldBeIn;
                if (!shouldBeIn)
                {
                    float side = human.Position.X >= 0f ? -1f : 1f;
                    target = new Vector3(side * (GameConstants.SinglesHalfWidth + 0.75f), 0f,
                        bot.Id == 0 ? 8.8f : -8.8f);
                    pace = 31f;
                }
                else
                {
                    (target, pace) = ChooseHardSimulatedTarget(
                        room.World.Ball.Position, room.World.Ball.Velocity, bot.Id, human.Position,
                        room.BotStringTension, bot.RacquetFaceAngle);
                }
                spin = Vector3.Zero;
                break;
            }
            case BotDifficulty.SuperImpossible:
            case BotDifficulty.Impossible:
            {
                requireIn = true;
                target = ChooseImpossibleFarthestCorner(bot.Id, human.Position);
                pace = room.BotDifficulty == BotDifficulty.SuperImpossible && room.BotSuperSmashPlanned ? 42f : 34f;
                if (room.BotImpossibleShotStyle == ShotStyle.Slice)
                {
                    float sideSpin = bot.SwingHand == HandedSwing.Right ? -300f : 300f;
                    spin = room.BotDifficulty == BotDifficulty.SuperImpossible && room.BotSuperSmashPlanned
                        ? new Vector3(-35f, sideSpin * 0.45f, 0f)
                        : new Vector3(-75f, sideSpin, 0f);
                }
                else
                {
                    spin = room.BotDifficulty == BotDifficulty.SuperImpossible && room.BotSuperSmashPlanned
                        ? new Vector3(120f, 0f, 0f)
                        : new Vector3(235f, 0f, 0f);
                }
                break;
            }
            default:
                throw new InvalidOperationException("Unknown bot difficulty.");
        }

        float recycledIncomingSpeed = StringTension.RecycledIncomingSpeed(
            room.World.Ball.Velocity, room.BotStringTension, bot.RacquetFaceAngle);
        float totalPace = pace + recycledIncomingSpeed;

        Vector3 velocity;
        Vector3 launchSpin = spin;
        if (requireIn)
        {
            if (IsImpossibleTier(room.BotDifficulty))
            {
                bool solved = room.BotDifficulty == BotDifficulty.SuperImpossible && room.BotSuperSmashPlanned
                    ? TryComputeSuperImpossibleSmashReturn(
                        room.World.Ball.Position, target, totalPace, spin, bot.Id, out velocity, out launchSpin)
                    : TryComputeGuaranteedImpossibleReturn(
                        room.World.Ball.Position, target, totalPace, spin, bot.Id, out velocity, out launchSpin);
                if (!solved)
                    return false;
            }
            else
            {
                // Intended-in returns are simulated before launch. If the bounded
                // solver cannot prove a legal trajectory, do not launch an unvalidated
                // fallback; the bot simply waits for another legitimate contact.
                if (!TryComputeValidatedBotReturn(
                        room.World.Ball.Position, target, totalPace, spin, bot.Id,
                        out velocity, out launchSpin))
                    return false;
            }
        }
        else
        {
            velocity = ComputeBotLaunchVelocity(room.World.Ball.Position, target, totalPace);
        }

        room.World.Ball.Velocity = velocity;
        room.World.Ball.AngularVelocity = launchSpin;
        room.World.Ball.LastHitBy = bot.Id;
        room.World.Ball.BounceCount = 0;
        room.World.Ball.IsServeToss = false;
        room.World.Ball.TossOwner = -1;
        room.World.Ball.ServeMustBounce = false;
        room.World.Ball.ServeBounceCount = 0;
        room.World.Message = $"AI {room.BotDifficulty} return.";
        room.BotTrackedLastHitBy = bot.Id;
        if (IsImpossibleTier(room.BotDifficulty))
            room.BotImpossibleNextSlice = !room.BotImpossibleNextSlice;
        room.BotSuperSmashPlanned = false;
        return true;
    }

    private static Vector3 ChooseImpossibleFarthestCorner(int botId, Vector3 humanPosition)
    {
        float edgeX = GameConstants.SinglesHalfWidth - 0.38f;
        float farZ = botId == 0
            ? GameConstants.CourtHalfLength - 0.42f
            : -GameConstants.CourtHalfLength + 0.42f;
        Vector3 left = new(-edgeX, 0f, farZ);
        Vector3 right = new(edgeX, 0f, farZ);
        float leftDistance = Vector2.Distance(new Vector2(left.X, left.Z), new Vector2(humanPosition.X, humanPosition.Z));
        float rightDistance = Vector2.Distance(new Vector2(right.X, right.Z), new Vector2(humanPosition.X, humanPosition.Z));
        return leftDistance >= rightDistance ? left : right;
    }

    private (Vector3 target, float pace) ChooseHardSimulatedTarget(
        Vector3 start, Vector3 incomingVelocity, int botId, Vector3 humanPosition,
        float stringTension, float racquetAngle)
    {
        Vector3 bestTarget = new Vector3(0f, 0f, botId == 0 ? 8f : -8f);
        float bestPace = 30f;
        float bestScore = float.NegativeInfinity;
        float[] xCandidates = { -3.75f, -2.4f, 0f, 2.4f, 3.75f };
        float[] zAbsCandidates = { 4.0f, 6.8f, 9.4f, 10.9f };
        float[] paces = { 27f, 30f, 33f };
        float recycledIncomingSpeed = StringTension.RecycledIncomingSpeed(
            incomingVelocity, stringTension, racquetAngle);

        foreach (float x in xCandidates)
        foreach (float zAbs in zAbsCandidates)
        foreach (float pace in paces)
        {
            Vector3 intended = new Vector3(x, 0f, botId == 0 ? zAbs : -zAbs);
            Vector3 velocity = ComputeBotLaunchVelocity(start, intended, pace + recycledIncomingSpeed);
            bool clearedNet = SimulateBotShotFirstBounce(start, velocity, Vector3.Zero, out Vector3 predicted);
            bool inside = clearedNet &&
                          MathF.Abs(predicted.X) <= GameConstants.SinglesHalfWidth - 0.12f &&
                          MathF.Abs(predicted.Z) <= GameConstants.CourtHalfLength - 0.12f &&
                          (botId == 0 ? predicted.Z > 0f : predicted.Z < 0f);
            if (!inside) continue;

            float separation = Vector2.Distance(new Vector2(predicted.X, predicted.Z),
                                                new Vector2(humanPosition.X, humanPosition.Z));
            float edgeBonus = MathF.Abs(predicted.X) * 0.18f + MathF.Abs(predicted.Z) * 0.04f;
            float score = separation + edgeBonus;
            if (score > bestScore)
            {
                bestScore = score;
                bestTarget = intended;
                bestPace = pace;
            }
        }
        return (bestTarget, bestPace);
    }

    private static Vector3 SimulateIncomingFirstBounce(BallState ball)
    {
        Vector3 p = ball.Position;
        Vector3 v = ball.Velocity;
        Vector3 spin = ball.AngularVelocity;
        int bounceCount = ball.BounceCount;
        bool serveMustBounce = ball.ServeMustBounce;
        const float step = GameConstants.FixedDt;

        for (int i = 0; i < 900; i++)
        {
            BallFlightEvents events = BallFlightModel.AdvanceStep(
                ref p, ref v, ref spin, ref bounceCount, ref serveMustBounce, step);
            if ((events & BallFlightEvents.ServeNetFault) != 0)
                return new Vector3(999f, GameConstants.BallRadius, 999f);
            if ((events & BallFlightEvents.GroundBounce) != 0)
                return new Vector3(p.X, GameConstants.BallRadius, p.Z);
        }
        return p;
    }

    private static bool IsProjectedServeBounceLegal(Vector3 bounce, int serverId, int targetXSign)
    {
        bool landedOpposite = serverId == 0 ? bounce.Z > 0f : bounce.Z < 0f;
        bool landedInTargetHalf = targetXSign == 0 || bounce.X * targetXSign >= -GameConstants.BallRadius;
        return landedOpposite && landedInTargetHalf &&
               MathF.Abs(bounce.Z) <= GameConstants.ServiceLine + GameConstants.BallRadius &&
               MathF.Abs(bounce.X) <= GameConstants.SinglesHalfWidth + GameConstants.BallRadius;
    }

    private static bool IsProjectedBounceInForReceiver(Vector3 bounce, int receiverId)
    {
        bool onReceiverSide = receiverId == 0 ? bounce.Z < 0f : bounce.Z > 0f;
        return onReceiverSide &&
               MathF.Abs(bounce.X) <= GameConstants.SinglesHalfWidth + GameConstants.BallRadius &&
               MathF.Abs(bounce.Z) <= GameConstants.CourtHalfLength + GameConstants.BallRadius;
    }

    private static Vector3 ComputeBotLaunchVelocity(Vector3 start, Vector3 target, float pace)
    {
        Vector3 horizontalDelta = new Vector3(target.X - start.X, 0f, target.Z - start.Z);
        float distance = Math.Max(0.5f, horizontalDelta.Length());
        float flightTime = Math.Clamp(distance / Math.Max(8f, pace), 0.48f, 1.05f);
        Vector3 horizontalVelocity = horizontalDelta / flightTime;
        float verticalVelocity = (GameConstants.BallRadius - start.Y +
                                  0.5f * GameConstants.Gravity * flightTime * flightTime) / flightTime;
        verticalVelocity = Math.Max(verticalVelocity, 2.4f);
        return new Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
    }


    private static bool TryComputeGuaranteedImpossibleReturn(
        Vector3 start, Vector3 desiredTarget, float desiredPace, Vector3 desiredSpin, int botId,
        out Vector3 velocity, out Vector3 spin)
    {
        // Strict bounded search: choose the best validated trajectory inside a small,
        // deterministic candidate budget. No planning-stage nested simulation remains.
        velocity = Vector3.Zero;
        spin = Vector3.Zero;
        Vector3 bestVelocity = Vector3.Zero;
        Vector3 bestSpin = Vector3.Zero;
        float bestScore = float.PositiveInfinity;
        bool found = false;
        float zSign = botId == 0 ? 1f : -1f;
        Vector3 safeCorner = ClampLandingToOpponentCourt(desiredTarget, botId, 0.42f);
        Vector3[] targets =
        {
            safeCorner,
            new Vector3(safeCorner.X * 0.72f, 0f, zSign * (GameConstants.CourtHalfLength - 1.15f)),
            new Vector3(0f, 0f, zSign * 7.2f)
        };
        float[] paceScales = { 0.68f, 0.84f, 1.00f };
        float[] verticalScales = { 0.86f, 1.00f, 1.16f, 1.32f };
        float[] spinScales = { 1.00f, 0.50f, 0.00f };
        float maxLaunchY = start.Y >= 2.4f ? 5.2f : start.Y >= 1.5f ? 6.4f : 7.6f;
        float maxApex = Math.Max(start.Y + 2.2f, 5.2f);

        for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            Vector3 target = targets[targetIndex];
            foreach (float spinScale in spinScales)
            foreach (float paceScale in paceScales)
            foreach (float verticalScale in verticalScales)
            {
                Vector3 candidate = ComputeBotLaunchVelocity(start, target, desiredPace * paceScale);
                candidate.Y *= verticalScale;
                if (candidate.Y > maxLaunchY) continue;
                Vector3 candidateSpin = desiredSpin * spinScale;
                if (!SimulateBotShotFirstBounce(start, candidate, candidateSpin,
                        out Vector3 bounce, out float netClearance, out float apex)) continue;
                bool inside = MathF.Abs(bounce.X) <= GameConstants.SinglesHalfWidth - 0.28f &&
                              MathF.Abs(bounce.Z) <= GameConstants.CourtHalfLength - 0.30f &&
                              (botId == 0 ? bounce.Z > 0.45f : bounce.Z < -0.45f);
                if (!inside || netClearance < 0.14f || apex > maxApex) continue;

                float targetError = Vector2.Distance(
                    new Vector2(bounce.X, bounce.Z), new Vector2(safeCorner.X, safeCorner.Z));
                float score = targetIndex * 0.30f + targetError +
                              MathF.Abs(paceScale - 1f) * 0.08f +
                              MathF.Abs(verticalScale - 1f) * 0.03f +
                              (1f - spinScale) * 0.04f;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestVelocity = candidate;
                    bestSpin = candidateSpin;
                    found = true;
                }
            }
        }

        if (found)
        {
            velocity = bestVelocity;
            spin = bestSpin;
            return true;
        }

        Vector3 safetyTarget = new(0f, 0f, zSign * 6.8f);
        foreach (float pace in new[] { 16f, 20f, 24f, 28f })
        foreach (float verticalScale in new[] { 0.9f, 1.1f, 1.3f, 1.5f })
        {
            Vector3 candidate = ComputeBotLaunchVelocity(start, safetyTarget, pace);
            candidate.Y *= verticalScale;
            if (!SimulateBotShotFirstBounce(start, candidate, Vector3.Zero,
                    out Vector3 bounce, out float netClearance, out _)) continue;
            bool inside = MathF.Abs(bounce.X) <= GameConstants.SinglesHalfWidth - 0.20f &&
                          MathF.Abs(bounce.Z) <= GameConstants.CourtHalfLength - 0.20f &&
                          (botId == 0 ? bounce.Z > 0.40f : bounce.Z < -0.40f);
            if (!inside || netClearance < 0.10f) continue;
            velocity = candidate;
            spin = Vector3.Zero;
            return true;
        }
        return false;
    }

    private static bool TryComputeValidatedBotReturn(
        Vector3 start, Vector3 desiredTarget, float desiredPace, Vector3 desiredSpin, int botId,
        out Vector3 velocity, out Vector3 spin)
    {
        // Bounded Easy/Medium/Hard solver. Score all candidates in the small budget so
        // tactical placement remains stable instead of accepting the first valid shot.
        velocity = Vector3.Zero;
        spin = Vector3.Zero;
        Vector3 bestVelocity = Vector3.Zero;
        Vector3 bestSpin = Vector3.Zero;
        float bestScore = float.PositiveInfinity;
        bool found = false;
        float zSign = botId == 0 ? 1f : -1f;
        Vector3 requested = ClampLandingToOpponentCourt(desiredTarget, botId, 0.18f);
        Vector3[] targets =
        {
            requested,
            new Vector3(requested.X * 0.55f, 0f, zSign * 7.4f),
            new Vector3(0f, 0f, zSign * 6.2f)
        };
        float[] paceScales = { 0.70f, 0.88f, 1.04f };
        float[] verticalScales = { 0.90f, 1.05f, 1.22f, 1.42f };
        float[] spinScales = { 1.00f, 0.50f, 0.00f };

        for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            Vector3 target = targets[targetIndex];
            foreach (float spinScale in spinScales)
            foreach (float paceScale in paceScales)
            foreach (float verticalScale in verticalScales)
            {
                Vector3 candidate = ComputeBotLaunchVelocity(start, target, desiredPace * paceScale);
                candidate.Y *= verticalScale;
                Vector3 candidateSpin = desiredSpin * spinScale;
                if (!SimulateBotShotFirstBounce(start, candidate, candidateSpin,
                        out Vector3 bounce, out float netClearance)) continue;
                bool inside = MathF.Abs(bounce.X) <= GameConstants.SinglesHalfWidth - 0.10f &&
                              MathF.Abs(bounce.Z) <= GameConstants.CourtHalfLength - 0.10f &&
                              (botId == 0 ? bounce.Z > 0.25f : bounce.Z < -0.25f);
                if (!inside || netClearance < 0.24f) continue;

                float targetError = Vector2.Distance(
                    new Vector2(bounce.X, bounce.Z), new Vector2(requested.X, requested.Z));
                float score = targetIndex * 0.28f + targetError +
                              MathF.Abs(paceScale - 1f) * 0.08f +
                              MathF.Abs(verticalScale - 1f) * 0.03f +
                              (1f - spinScale) * 0.04f;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestVelocity = candidate;
                    bestSpin = candidateSpin;
                    found = true;
                }
            }
        }

        if (found)
        {
            velocity = bestVelocity;
            spin = bestSpin;
            return true;
        }

        Vector3 safetyTarget = new(0f, 0f, zSign * 6.2f);
        foreach (float pace in new[] { 12f, 16f, 20f, 24f, 28f })
        foreach (float verticalScale in new[] { 0.85f, 1.0f, 1.15f, 1.30f, 1.45f, 1.60f })
        {
            Vector3 candidate = ComputeBotLaunchVelocity(start, safetyTarget, pace);
            candidate.Y *= verticalScale;
            if (!SimulateBotShotFirstBounce(start, candidate, Vector3.Zero,
                    out Vector3 bounce, out float clearance)) continue;
            bool inside = MathF.Abs(bounce.X) <= GameConstants.SinglesHalfWidth - 0.10f &&
                          MathF.Abs(bounce.Z) <= GameConstants.CourtHalfLength - 0.10f &&
                          (botId == 0 ? bounce.Z > 0.25f : bounce.Z < -0.25f);
            if (!inside || clearance < 0.18f) continue;
            velocity = candidate;
            spin = Vector3.Zero;
            return true;
        }
        return false;
    }

    private static bool SimulateBotShotFirstBounce(
        Vector3 start, Vector3 velocity, Vector3 initialSpin, out Vector3 bounce)
    {
        return SimulateBotShotFirstBounce(start, velocity, initialSpin, out bounce, out _, out _);
    }

    private static bool SimulateBotShotFirstBounce(
        Vector3 start, Vector3 velocity, Vector3 initialSpin, out Vector3 bounce, out float netClearance)
    {
        return SimulateBotShotFirstBounce(start, velocity, initialSpin, out bounce, out netClearance, out _);
    }

    private static bool SimulateBotShotFirstBounce(
        Vector3 start, Vector3 velocity, Vector3 initialSpin, out Vector3 bounce, out float netClearance, out float apex)
    {
        Vector3 p = start; Vector3 v = velocity; Vector3 spin = initialSpin;
        int bounceCount = 0; bool serveMustBounce = false;
        netClearance = float.NegativeInfinity; apex = start.Y;
        const float step = GameConstants.FixedDt;
        // Five simulated seconds is already far beyond a normal tennis return flight.
        // This hard ceiling prevents malformed/extreme velocities from consuming a tick.
        for (int i = 0; i < 600; i++)
        {
            Vector3 previous = p;
            BallFlightEvents events = BallFlightModel.AdvanceStep(ref p, ref v, ref spin, ref bounceCount, ref serveMustBounce, step);
            apex = Math.Max(apex, p.Y);
            if (BallFlightModel.TryGetNetPlaneCrossing(previous, p, out Vector3 crossing) && MathF.Abs(crossing.X) <= GameConstants.CourtHalfWidth)
                netClearance = crossing.Y - GameConstants.NetHeightCenter;
            if ((events & BallFlightEvents.NetContact) != 0)
            { bounce = new Vector3(999f, GameConstants.BallRadius, 999f); return false; }
            if ((events & BallFlightEvents.GroundBounce) != 0)
            { bounce = new Vector3(p.X, GameConstants.BallRadius, p.Z); return true; }
        }
        bounce = p; return false;
    }

    private static Vector3 ClampLandingToOpponentCourt(Vector3 target, int hitterId, float margin)
    {
        target.X = Math.Clamp(target.X, -GameConstants.SinglesHalfWidth + margin, GameConstants.SinglesHalfWidth - margin);
        float absZ = Math.Clamp(MathF.Abs(target.Z), 1.2f, GameConstants.CourtHalfLength - margin);
        target.Z = hitterId == 0 ? absZ : -absZ;
        target.Y = 0f;
        return target;
    }

    private static void UpdateAutomaticServe(MatchRoom room, float dt)
    {
        if (!room.AutoServeActive) return;

        // A fault, point award, disconnect, or match reset ends the animation
        // immediately so it cannot leak into the next point.
        if (room.AutoServeBallLaunched && room.World.Phase != MatchPhase.Rally)
        {
            room.AutoServeActive = false;
            room.AutoServeBallLaunched = false;
            room.AutoServeTimer = 0f;
            return;
        }

        PlayerState? server = room.World.Players.FirstOrDefault(p => p.Id == room.World.Score.Server);
        if (server is null)
        {
            room.AutoServeActive = false;
            return;
        }

        room.AutoServeTimer += dt;
        const float contactTime = 0.62f;
        const float finishTime = 1.02f;

        if (!room.AutoServeBallLaunched)
        {
            // Build a readable backswing while the gravity-driven toss rises.
            float preparation = Math.Clamp(room.AutoServeTimer / contactTime, 0f, 1f);
            server.Swinging = true;
            server.SwingReleasedVisual = false;
            server.SwingProgress = MathHelperSmooth(preparation) * SwingAnimation.DurationSeconds;
            server.SwingHeight = Math.Clamp(room.World.Ball.Position.Y - 0.25f, 0.75f, 2.35f);
            server.RacquetBehind = true;

            if (room.AutoServeTimer >= contactTime)
            {
                room.Physics.LaunchAutomaticServe(room.World, server);
                room.AutoServeBallLaunched = true;
                server.Swinging = true;
                server.SwingReleasedVisual = true;
                server.SwingVisualTimer = 0.34f;
                server.SwingProgress = SwingAnimation.DurationSeconds;
                server.RacquetBehind = false;
            }
        }
        else
        {
            // Keep the follow-through visible to both players after contact.
            server.Swinging = true;
            server.SwingReleasedVisual = true;
            server.SwingVisualTimer = Math.Max(0f, finishTime - room.AutoServeTimer);
            server.SwingProgress = SwingAnimation.DurationSeconds;
        }

        if (room.AutoServeTimer >= finishTime)
        {
            server.Swinging = false;
            server.SwingReleasedVisual = false;
            server.SwingVisualTimer = 0f;
            server.SwingProgress = 0f;
            server.SwingPower = 0f;
            room.AutoServeActive = false;
        }
    }

    private static float MathHelperSmooth(float value) => value * value * (3f - 2f * value);

    private void RemoveTimedOutClients()
    {
        DateTime cutoff = DateTime.UtcNow - ClientTimeout;
        foreach (ClientSession session in clients.Values.Where(c => c.LastSeenUtc < cutoff).ToArray())
            RemoveClient(session, "connection timed out");
    }

    private void RemoveClient(ClientSession session, string reason)
    {
        clients.Remove(session.ClientId);
        endpointToClient.Remove(EndpointKey(session.EndPoint));
        if (matches.TryGetValue(session.MatchId, out MatchRoom? room))
        {
            room.PlayerClientIds.Remove(session.PlayerId);
            room.Inputs.Remove(session.PlayerId);
            room.StringTensions.Remove(session.PlayerId);
            room.World.Players.RemoveAll(p => p.Id == session.PlayerId);
            room.Started = false;

            if (room.PlayerClientIds.Count == 0)
            {
                matches.Remove(room.Id);
            }
            else
            {
                string remainingId = room.PlayerClientIds.Values.Single();
                ClientSession remaining = clients[remainingId];
                room.World.Phase = MatchPhase.Waiting;
                room.World.Ball.InPlay = false;
                room.World.Message = $"Opponent left ({reason}). Waiting for a new opponent...";
                room.World.Score = new ScoreState();
                RefreshLobby(room);
                SendWelcome(remaining, room.World.Message);
                SendState(room);
            }
        }
        Console.WriteLine($"{session.Name} disconnected ({reason}). Online: {clients.Count}");
    }

    private void UpdateEndpoint(ClientSession session, IPEndPoint endpoint)
    {
        string oldKey = EndpointKey(session.EndPoint);
        string newKey = EndpointKey(endpoint);
        if (oldKey == newKey) return;
        endpointToClient.Remove(oldKey);
        session.EndPoint = endpoint;
        endpointToClient[newKey] = session.ClientId;
    }

    private ClientSession? FindSession(IPEndPoint endpoint) =>
        endpointToClient.TryGetValue(EndpointKey(endpoint), out string? id) && clients.TryGetValue(id, out ClientSession? session)
            ? session : null;

    private void RefreshLobby(MatchRoom room)
    {
        room.World.Lobby.ConnectedPlayers = room.BotEnabled ? room.PlayerClientIds.Count + 1 : room.PlayerClientIds.Count;
        room.World.Lobby.MaximumPlayers = 2;
        room.World.Lobby.IsFull = room.ReadyToPlay;
        room.World.Lobby.PlayerNames = room.World.Players.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        room.World.Lobby.OnlinePlayers = clients.Count;
        room.World.Lobby.ActiveMatches = matches.Values.Count(m => m.ReadyToPlay);
    }

    private void SendWelcome(ClientSession session, string message)
    {
        Console.WriteLine($"[SERVER WELCOME] -> {session.EndPoint} accepted=true player={session.PlayerId} match={session.MatchId} message=\"{message}\"");
        Send(session.EndPoint, PacketKind.Welcome,
            new WelcomePacket(session.PlayerId, true, message, session.MatchId, GameConstants.ProtocolVersion));
    }

    private void SendState(MatchRoom room)
    {
        telemetry.StateSent();
        foreach (string clientId in room.PlayerClientIds.Values)
            if (clients.TryGetValue(clientId, out ClientSession? session)) Send(session.EndPoint, PacketKind.State, room.World);
    }

    private void Send<T>(IPEndPoint endpoint, PacketKind kind, T packet)
    {
        try
        {
            byte[] bytes = NetPacket.Pack(kind, packet);
            if (kind is PacketKind.Welcome or PacketKind.Pong)
                Console.WriteLine($"[SERVER SEND] {DateTime.Now:HH:mm:ss.fff} {kind} -> {endpoint} ({bytes.Length} bytes)");
            udp.Send(bytes, bytes.Length, endpoint);
        }
        catch (SocketException ex) { Console.WriteLine($"Send to {endpoint} failed: {ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) { }
    }

    private static string EndpointKey(IPEndPoint endpoint) => $"{endpoint.Address}:{endpoint.Port}";
    private static (string displayName, BotDifficulty? difficulty, float tension) ParseStartupRequest(string rawName)
    {
        float tension = StringTension.Default;
        BotDifficulty? difficulty = null;
        string remaining = rawName;

        const string tensionPrefix = "__TENSION_";
        if (remaining.StartsWith(tensionPrefix, StringComparison.Ordinal))
        {
            int markerEnd = remaining.IndexOf("__::", tensionPrefix.Length, StringComparison.Ordinal);
            if (markerEnd > tensionPrefix.Length)
            {
                string percentText = remaining[tensionPrefix.Length..markerEnd];
                if (int.TryParse(percentText, out int percent))
                    tension = StringTension.Clamp(percent / 100f);
                remaining = remaining[(markerEnd + 4)..];
            }
        }

        if (remaining.StartsWith("__AI_EASY__::", StringComparison.Ordinal))
        {
            difficulty = BotDifficulty.Easy;
            remaining = remaining[13..];
        }
        else if (remaining.StartsWith("__AI_MEDIUM__::", StringComparison.Ordinal))
        {
            difficulty = BotDifficulty.Medium;
            remaining = remaining[15..];
        }
        else if (remaining.StartsWith("__AI_HARD__::", StringComparison.Ordinal))
        {
            difficulty = BotDifficulty.Hard;
            remaining = remaining[13..];
        }
        else if (remaining.StartsWith("__AI_SUPERIMPOSSIBLE__::", StringComparison.Ordinal))
        {
            const string superImpossiblePrefix = "__AI_SUPERIMPOSSIBLE__::";
            difficulty = BotDifficulty.SuperImpossible;
            remaining = remaining[superImpossiblePrefix.Length..];
        }
        else if (remaining.StartsWith("__AI_IMPOSSIBLE__::", StringComparison.Ordinal))
        {
            const string impossiblePrefix = "__AI_IMPOSSIBLE__::";
            difficulty = BotDifficulty.Impossible;
            remaining = remaining[impossiblePrefix.Length..];
        }

        return (remaining, difficulty, tension);
    }

    private static float GetStringTension(MatchRoom room, int playerId) =>
        room.StringTensions.TryGetValue(playerId, out float tension) ? tension : StringTension.Default;

    private static string SanitizeName(string value)
    {
        string cleaned = string.IsNullOrWhiteSpace(value) ? "Player" : value.Trim();
        return cleaned.Length <= 24 ? cleaned : cleaned[..24];
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        udp.Dispose();
    }
}
