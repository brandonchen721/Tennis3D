using System.Text.Json;
using System.Numerics;
using Tennis3D.Shared;

if (args.Contains("--physics-soak", StringComparer.OrdinalIgnoreCase))
    return RunPhysicsSoak();
if (args.Contains("--packet-fuzz", StringComparer.OrdinalIgnoreCase))
    return RunPacketFuzz();

var tests = new (string Name, Action Run)[]
{
    ("Basic game scoring", BasicGameScoring),
    ("Deuce and advantage", DeuceAndAdvantage),
    ("Serve rotation after game", ServeRotation),
    ("First and second serve faults", ServeFaults),
    ("Tiebreak completion", TieBreakCompletion),
    ("Tiebreak next-set server is receiver of first tiebreak point", TieBreakNextSetServer),
    ("Best-of-three match completion", MatchCompletion),
    ("Out call", OutCall),
    ("Ball touching singles line is IN", LineTouchIsIn),
    ("Serve touching service-box line is IN", ServeLineTouchIsIn),
    ("Rally net failure ends point as NET", RallyNetCall),
    ("Net mesh absorbs impact instead of hard-wall bouncing", NetMeshAbsorbsImpact),
    ("Net height sags from posts to center", NetHeightSag),
    ("Own-side first bounce loses point", OwnSideFirstBounceCall),
    ("Legal first bounce leaving receiver area is MISSED", PostBounceBoundaryIsMissed),
    ("Second bounce outside court is still DOUBLE BOUNCE", SecondBounceOutsideIsDoubleBounce),
    ("Unbounced serve leaving playable area is a fault", ServeBoundaryExitIsFault),
    ("Double bounce call", DoubleBounceCall),
    ("Deterministic physics regression", DeterministicPhysics),
    ("Protocol round trip", ProtocolRoundTrip),
    ("Super Impossible minion state survives protocol round trip", MinionProtocolRoundTrip),
    ("Jump request survives protocol round trip", JumpRequestProtocolRoundTrip),
    ("Jump height raises authoritative racquet geometry", JumpRaisesRacquetGeometry),
    ("Camera-relative racquet mapping", RacquetMapping),
    ("Left/right racquet face angle symmetry", RacquetFaceAngleSymmetry),
    ("Left/right racquet forward-depth symmetry", RacquetForwardDepthSymmetry),
    ("Serve starts behind baseline", ServeStartsBehindBaseline),
    ("Serve starts on correct diagonal half", ServeStartsOnCorrectHalf),
    ("Serve toss reaches exact apex", ServeTossExactApex),
    ("Serve must land diagonally", ServeMustLandDiagonally),
    ("Automatic serve launches synchronized ball", AutomaticServeLaunch),
    ("3D oval racquet contact matches visible racquet", RacquetOvalContact),
    ("3D oval rejects contact outside enlarged ellipsoid", RacquetOvalRejectsOutsideEllipsoid),
    ("Contact timing steers right-hand early/late shots", ContactTimingSteersRightHand),
    ("Contact timing mirrors correctly for left hand", ContactTimingMirrorsLeftHand),
    ("Forward/back preparation stacks with contact timing up to 1.25", PreparationTimingStacksWithContact),
    ("Forward/back timing follows physical quarter-circle angle", PreparationTimingFollowsPhysicalAngle),
    ("Forward/back preparation mirrors correctly by hand", PreparationTimingMirrorsByHand),
    ("Corner position biases neutral return toward center", CornerAimBiasesTowardCenter),
    ("Hold power meter reaches 100% in 0.5s and holds until release", PowerMeterTiming),
    ("Power selection preserves 100% baseline", PowerSelectionScalesShot),
    ("Forehand wheel angles change pace and arc", ForehandPresetTrajectories),
    ("Slice wheel angles add hand-opposite sidespin", SlicePresetSpinDirection),
    ("Slice veer starts only after first bounce", SliceVeerStartsAfterBounce),
    ("Racquet forward/back stops at 90-degree perpendicular limit", RacquetQuarterHoopStopsAtPerpendicular),
    ("Racquet half-hoop mirrors left and right hands", RacquetHalfHoopMirrorsHands),
    ("Forehand animation moves low-to-high and inward", ForehandSwingAnimation),
    ("Slice animation moves high-to-low and inward", SliceSwingAnimation),
    ("String tension recycles only incoming opponent pace", StringTensionRecycleFormula),
    ("String tension affects return but not self-generated swing power", StringTensionAffectsReturnSpeed)
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try { run(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static WorldState World()
{
    var world = new WorldState();
    world.Players.Add(new PlayerState { Id = 0, Name = "P1", Position = new Vector3(0, 0, -9.5f) });
    world.Players.Add(new PlayerState { Id = 1, Name = "P2", Position = new Vector3(0, 0, 9.5f) });
    world.Score.Server = 0;
    return world;
}

static void BasicGameScoring()
{
    WorldState w = World(); var r = new TennisRules();
    for (int i = 0; i < 4; i++) r.AwardPoint(w, 0, "test");
    Equal(1, w.Score.Games[0]); Equal(0, w.Score.Points[0]);
}

static void DeuceAndAdvantage()
{
    WorldState w = World(); var r = new TennisRules();
    for (int i = 0; i < 3; i++) { r.AwardPoint(w, 0, "test"); r.AwardPoint(w, 1, "test"); }
    r.AwardPoint(w, 0, "test");
    True(TennisRules.Format(w.Score).Contains("AD-40"), "Expected advantage display.");
    r.AwardPoint(w, 1, "test");
    True(TennisRules.Format(w.Score).Contains("40-40"), "Expected return to deuce.");
}

static void ServeRotation()
{
    WorldState w = World(); var r = new TennisRules();
    for (int i = 0; i < 4; i++) r.AwardPoint(w, 0, "test");
    Equal(1, w.Score.Server);
}

static void ServeFaults()
{
    WorldState w = World(); var r = new TennisRules();
    r.ServeFault(w, 0, "wide"); Equal(2, w.Score.ServeNumber); Equal(MatchPhase.ServeSetup, w.Phase);
    r.ServeFault(w, 0, "wide"); Equal(1, w.Score.Points[1]);
}

static void TieBreakCompletion()
{
    WorldState w = World(); var r = new TennisRules();
    w.Score.Games[0] = 6; w.Score.Games[1] = 6; w.Score.TieBreak = true;
    for (int i = 0; i < 7; i++) r.AwardPoint(w, 0, "test");
    Equal(1, w.Score.Sets[0]); Equal(0, w.Score.Games[0]); True(!w.Score.TieBreak, "Tiebreak should end.");
}

static void TieBreakNextSetServer()
{
    WorldState w = World(); var r = new TennisRules();
    w.Score.Games[0] = 6; w.Score.Games[1] = 6; w.Score.TieBreak = true;
    w.Score.Server = 0; // Player 0 serves the first tie-break point.
    for (int i = 0; i < 7; i++) r.AwardPoint(w, 0, "test");
    Equal(1, w.Score.Server);
    Equal(1, w.Score.ServeNumber);
}

static void MatchCompletion()
{
    WorldState w = World(); var r = new TennisRules();
    w.Score.Sets[0] = 1; w.Score.Games[0] = 5;
    for (int i = 0; i < 4; i++) r.AwardPoint(w, 0, "test");
    Equal(2, w.Score.Sets[0]); Equal(MatchPhase.MatchOver, w.Phase);
}

static void OutCall()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Ball.InPlay = true; w.Ball.LastHitBy = 0;
    w.Ball.Position = new Vector3(GameConstants.SinglesHalfWidth + .2f, GameConstants.BallRadius + .01f, 2f);
    w.Ball.Velocity = new Vector3(0, -2f, 0);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(1, w.Score.Points[1]);
}

static void OwnSideFirstBounceCall()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Ball.InPlay = true; w.Ball.LastHitBy = 0;
    // Player 0 must send the ball to +Z. A first bounce on -Z is on the hitter's own side.
    w.Ball.Position = new Vector3(0f, GameConstants.BallRadius + .001f, -2f);
    w.Ball.Velocity = new Vector3(0f, -2f, 0f);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(1, w.Score.Points[1]);
}

static void LineTouchIsIn()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Ball.InPlay = true; w.Ball.LastHitBy = 0;
    w.Ball.Position = new Vector3(GameConstants.SinglesHalfWidth + GameConstants.BallRadius * 0.80f,
        GameConstants.BallRadius + .001f, 2f);
    w.Ball.Velocity = new Vector3(0f, -2f, 0f);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(0, w.Score.Points[1]);
    Equal(MatchPhase.Rally, w.Phase);
}

static void ServeLineTouchIsIn()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Score.Server = 0; w.Ball.InPlay = true; w.Ball.LastHitBy = 0;
    w.Ball.ServeMustBounce = true; w.Ball.ServeTargetXSign = 1;
    w.Ball.Position = new Vector3(GameConstants.SinglesHalfWidth + GameConstants.BallRadius * 0.80f,
        GameConstants.BallRadius + .001f, GameConstants.ServiceLine + GameConstants.BallRadius * 0.80f);
    w.Ball.Velocity = new Vector3(0f, -2f, 0f);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(1, w.Score.ServeNumber);
    Equal(MatchPhase.Rally, w.Phase);
    True(!w.Ball.ServeMustBounce, "Serve should be accepted when the ball footprint touches the line.");
}

static void RallyNetCall()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Ball.InPlay = true; w.Ball.LastHitBy = 0;
    w.Ball.Position = new Vector3(0f, GameConstants.NetHeightCenter * 0.55f, -0.03f);
    w.Ball.Velocity = new Vector3(0f, 0f, 8f);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(1, w.Score.Points[1]);
    Equal(1, w.Score.PointWinner);
    True(w.Message.Contains("net", StringComparison.OrdinalIgnoreCase), "Expected NET point reason.");
}

static void NetMeshAbsorbsImpact()
{
    Vector3 position = new(0f, GameConstants.NetHeightCenter * 0.55f, -0.05f);
    Vector3 velocity = new(1.5f, 0.8f, 20f);
    Vector3 spin = new(80f, 20f, 5f);
    int bounces = 0;
    bool serveMustBounce = false;

    BallFlightEvents events = BallFlightModel.AdvanceStep(
        ref position, ref velocity, ref spin, ref bounces, ref serveMustBounce, 0.01f);

    True((events & BallFlightEvents.NetContact) != 0, "Expected swept ball path to contact the net.");
    True(velocity.Z < 0f, "Net should stop the crossing ball and leave only a small same-side rebound.");
    True(MathF.Abs(velocity.Z) < 2.0f, "Net mesh should absorb most normal speed instead of acting like a hard wall.");
    True(MathF.Abs(velocity.X) < 1.5f, "Mesh friction should damp lateral speed.");
    True(spin.Length() < new Vector3(80f, 20f, 5f).Length(), "Net contact should dissipate some ball spin.");
}

static void NetHeightSag()
{
    NearScalar(GameConstants.NetHeightCenter, BallFlightModel.NetHeightAtX(0f), 0.0001f);
    NearScalar(GameConstants.NetHeightPost, BallFlightModel.NetHeightAtX(GameConstants.CourtHalfWidth), 0.0001f);
    float halfway = BallFlightModel.NetHeightAtX(GameConstants.CourtHalfWidth * 0.5f);
    True(halfway > GameConstants.NetHeightCenter && halfway < GameConstants.NetHeightPost,
        "Net cable should rise smoothly from center strap toward the posts.");
}

static void PostBounceBoundaryIsMissed()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Ball.InPlay = true; w.Ball.LastHitBy = 0; w.Ball.BounceCount = 1;
    w.Ball.Position = new Vector3(0f, 1.0f, 23.2f);
    w.Ball.Velocity = new Vector3(0f, 0f, 8f);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(1, w.Score.Points[0]);
    Equal(0, w.Score.PointWinner);
    Equal(MatchPhase.PointOver, w.Phase);
    True(w.Message.Contains("missed", StringComparison.OrdinalIgnoreCase), "Expected MISSED return reason.");
}

static void SecondBounceOutsideIsDoubleBounce()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Ball.InPlay = true; w.Ball.LastHitBy = 0; w.Ball.BounceCount = 1;
    w.Ball.Position = new Vector3(GameConstants.SinglesHalfWidth + 0.7f,
        GameConstants.BallRadius + .001f, 5f);
    w.Ball.Velocity = new Vector3(0f, -2f, 0f);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(1, w.Score.Points[0]);
    Equal(0, w.Score.PointWinner);
    True(w.Message.Contains("double bounce", StringComparison.OrdinalIgnoreCase),
        "Second bounce must be DOUBLE BOUNCE even when outside the painted court.");
}

static void ServeBoundaryExitIsFault()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Ball.InPlay = true; w.Ball.LastHitBy = 0;
    w.Ball.ServeMustBounce = true; w.Score.Server = 0; w.Score.ServeNumber = 1;
    w.Ball.Position = new Vector3(0f, 2.0f, 23.2f);
    w.Ball.Velocity = new Vector3(0f, 0f, 8f);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(2, w.Score.ServeNumber);
    Equal(MatchPhase.ServeSetup, w.Phase);
    Equal(0, w.Score.Points[1]);
}

static void DoubleBounceCall()
{
    WorldState w = World(); var r = new TennisRules(); var p = new TennisPhysics();
    w.Phase = MatchPhase.Rally; w.Ball.InPlay = true; w.Ball.LastHitBy = 0; w.Ball.BounceCount = 1;
    w.Ball.Position = new Vector3(0, GameConstants.BallRadius + .001f, 3f);
    w.Ball.Velocity = new Vector3(0, -2f, 0);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(1, w.Score.Points[0]);
}

static void DeterministicPhysics()
{
    WorldState a = World(), b = World(); var pa = new TennisPhysics(); var pb = new TennisPhysics();
    a.Phase = b.Phase = MatchPhase.Rally;
    a.Ball.InPlay = b.Ball.InPlay = true;
    a.Ball.Position = b.Ball.Position = new Vector3(.2f, 2.3f, -3f);
    a.Ball.Velocity = b.Ball.Velocity = new Vector3(3f, 8f, 21f);
    a.Ball.AngularVelocity = b.Ball.AngularVelocity = new Vector3(80f, 15f, 0);
    var ra = new TennisRules(); var rb = new TennisRules();
    for (int i = 0; i < 180; i++) { pa.StepBall(a, GameConstants.FixedDt, ra); pb.StepBall(b, GameConstants.FixedDt, rb); }
    Near(a.Ball.Position, b.Ball.Position, 1e-6f); Near(a.Ball.Velocity, b.Ball.Velocity, 1e-6f);
}

static void ProtocolRoundTrip()
{
    var input = new InputPacket(9, .2f, -.3f, 0, 0, true, false, HandedSwing.Left, 1.1f, true, -.4f, .2f, 900, 120, false, 3f, 0f);
    byte[] bytes = NetPacket.Pack(PacketKind.Input, input);
    (PacketKind kind, System.Text.Json.JsonElement payload) = NetPacket.Unpack(bytes);
    Equal(PacketKind.Input, kind);
    InputPacket decoded = payload.Deserialize<InputPacket>(NetPacket.JsonOptions)!;
    Equal(input.Sequence, decoded.Sequence); Equal(input.Hand, decoded.Hand); Equal(input.RacquetFaceAngle, decoded.RacquetFaceAngle);
}

static void MinionProtocolRoundTrip()
{
    WorldState w = World();
    w.MinionExplosionSequence = 7;
    w.MinionExplosionPosition = new Vector3(-0.75f, 0f, 7.25f);
    w.Minions.Add(new MinionState
    {
        Id = 2, OwnerId = 1, Position = new Vector3(1.25f, 0f, 8.5f),
        Swinging = true, SwingProgress = 0.25f, SwingHand = HandedSwing.Left,
        SwingHeight = 1.1f, RacquetFaceAngle = -0.4f
    });
    byte[] bytes = NetPacket.Pack(PacketKind.State, w);
    (PacketKind kind, JsonElement payload) = NetPacket.Unpack(bytes);
    Equal(PacketKind.State, kind);
    WorldState decoded = payload.Deserialize<WorldState>(NetPacket.JsonOptions)!;
    Equal(1, decoded.Minions.Count);
    Equal(2, decoded.Minions[0].Id);
    Equal(1, decoded.Minions[0].OwnerId);
    Equal(HandedSwing.Left, decoded.Minions[0].SwingHand);
    True(decoded.Minions[0].Swinging, "Minion swing state must survive serialization.");
    Near(new Vector3(1.25f, 0f, 8.5f), decoded.Minions[0].Position, 1e-6f);
    Equal(7L, decoded.MinionExplosionSequence);
    Near(new Vector3(-0.75f, 0f, 7.25f), decoded.MinionExplosionPosition, 1e-6f);
}

static void JumpRequestProtocolRoundTrip()
{
    var input = new InputPacket(10, 0f, 0f, 0f, 0f, false, false, HandedSwing.Right,
        1.05f, true, 0f, 0f, 0f, 0f, false, 0f, 0f, true);
    byte[] bytes = NetPacket.Pack(PacketKind.Input, input);
    (PacketKind kind, System.Text.Json.JsonElement payload) = NetPacket.Unpack(bytes);
    Equal(PacketKind.Input, kind);
    InputPacket decoded = payload.Deserialize<InputPacket>(NetPacket.JsonOptions)!;
    True(decoded.JumpRequested, "SPACE jump request must survive network serialization.");
}

static void JumpRaisesRacquetGeometry()
{
    var player = new PlayerState
    {
        Id = 0,
        Position = new Vector3(0f, 0f, -5f),
        SwingHand = HandedSwing.Right,
        SwingProgress = SwingAnimation.DurationSeconds * 0.5f
    };
    var input = new InputPacket(1, 0f, 0f, ShotControls.EncodeStyle(ShotStyle.Forehand), 1f,
        true, false, HandedSwing.Right, 1.05f, true, 0f, 0f, 0f, 0f, false, 0f, 0f);

    RacquetHitboxPose groundPose = RacquetGeometry.GetHitbox(player, input);
    const float jumpHeight = 0.85f;
    player.Position = new Vector3(player.Position.X, jumpHeight, player.Position.Z);
    RacquetHitboxPose airPose = RacquetGeometry.GetHitbox(player, input);

    True(MathF.Abs((airPose.Center.Y - groundPose.Center.Y) - jumpHeight) < 0.0001f,
        "Jump height must raise the authoritative racquet center by the same amount.");
}

static void RacquetMapping()
{
    // Camera-relative mouse X must map naturally for the active hand.
    // Shared control convention after conversion is always + = forward, - = backward.
    True(RacquetControls.MouseDeltaToControl(-1f, HandedSwing.Right) > 0f, "Right-hand mouse-left must move the racquet forward.");
    True(RacquetControls.MouseDeltaToControl(1f, HandedSwing.Right) < 0f, "Right-hand mouse-right must move the racquet backward.");
    True(RacquetControls.MouseDeltaToControl(1f, HandedSwing.Left) > 0f, "Left-hand mouse-right must move the racquet forward.");
    True(RacquetControls.MouseDeltaToControl(-1f, HandedSwing.Left) < 0f, "Left-hand mouse-left must move the racquet backward.");

    // Once converted, shared forward/back semantics remain hand-independent.
    True(RacquetControls.ToForwardness(1f, HandedSwing.Right) > 0f, "Positive shared control must mean forward for right hand.");
    True(RacquetControls.ToForwardness(1f, HandedSwing.Left) > 0f, "Positive shared control must mean forward for left hand.");
    True(RacquetControls.ToForwardness(-1f, HandedSwing.Right) < 0f, "Negative shared control must mean backward for right hand.");
    True(RacquetControls.ToForwardness(-1f, HandedSwing.Left) < 0f, "Negative shared control must mean backward for left hand.");
}

static void RacquetFaceAngleSymmetry()
{
    WorldState w = TestWorld();
    PlayerState p = w.Players[0];
    p.Swinging = true;
    p.SwingProgress = 0.25f;

    foreach (float degrees in new[] { -30f, 0f, 30f })
    {
        float angle = degrees * MathF.PI / 180f;
        var rightInput = new InputPacket(1, 0f, 0f, ShotControls.EncodeStyle(ShotStyle.Forehand), 1f, true, false,
            HandedSwing.Right, 1.2f, false, 0f, angle, 0f, 0f, false, 0f, 0f);
        var leftInput = rightInput with { Hand = HandedSwing.Left };

        RacquetHitboxPose right = RacquetGeometry.GetHitbox(p, rightInput);
        RacquetHitboxPose left = RacquetGeometry.GetHitbox(p, leftInput);

        True(MathF.Abs(right.DepthAxis.Y - left.DepthAxis.Y) < 0.001f,
            $"{degrees} degree face tilt should have the same vertical orientation on both hands.");
        True(MathF.Abs(Vector3.Dot(right.DepthAxis, Vector3.UnitZ) - Vector3.Dot(left.DepthAxis, Vector3.UnitZ)) < 0.001f,
            $"{degrees} degree face tilt should have the same forward orientation on both hands.");
    }

    Vector3 rightTop = SimulateReturn(ShotStyle.Forehand, -30f, 1f, HandedSwing.Right).Velocity;
    Vector3 leftTop = SimulateReturn(ShotStyle.Forehand, -30f, 1f, HandedSwing.Left).Velocity;
    Vector3 rightLob = SimulateReturn(ShotStyle.Forehand, 30f, 1f, HandedSwing.Right).Velocity;
    Vector3 leftLob = SimulateReturn(ShotStyle.Forehand, 30f, 1f, HandedSwing.Left).Velocity;
    True(MathF.Abs(rightTop.Y - leftTop.Y) < 0.001f && MathF.Abs(rightTop.Z - leftTop.Z) < 0.001f,
        "Topspin forehand arc/power must match across hands.");
    True(MathF.Abs(rightLob.Y - leftLob.Y) < 0.001f && MathF.Abs(rightLob.Z - leftLob.Z) < 0.001f,
        "Lob forehand arc/power must match across hands.");
}

static void RacquetForwardDepthSymmetry()
{
    foreach (float control in new[] { -RacquetControls.MaximumDepthAt, 0f, RacquetControls.MaximumDepthAt })
    {
        RacquetHoopPose right = RacquetControls.GetPose(control, HandedSwing.Right);
        RacquetHoopPose left = RacquetControls.GetPose(control, HandedSwing.Left);
        True(MathF.Abs(right.ForwardOffset - left.ForwardOffset) < 0.001f,
            "The same forward/back control must produce the same depth on both hands.");
        True(MathF.Abs(right.LateralOffset + left.LateralOffset) < 0.001f,
            "Only lateral racquet placement should mirror between hands.");
    }
}

static void ServeStartsBehindBaseline()
{
    WorldState w = World();
    w.Score.Server = 0;
    new TennisRules().ResetPlayersAndBall(w);
    PlayerState server = w.Players.Single(p => p.Id == 0);
    True(server.Position.Z < -GameConstants.CourtHalfLength, "Near-side server must start behind the baseline.");
}


static void ServeStartsOnCorrectHalf()
{
    var r = new TennisRules();
    WorldState w = World();
    w.Score.Server = 0;
    w.Score.Points[0] = 0; w.Score.Points[1] = 0;
    r.ResetPlayersAndBall(w);
    PlayerState p0 = w.Players.First(p => p.Id == 0);
    True(TennisRules.IsLegalServeX(w.Score, 0, p0.Position.X),
        "Player 0 deuce serve must start on its assigned half.");

    w.Score.Server = 1;
    w.Score.Points[0] = 0; w.Score.Points[1] = 0;
    r.ResetPlayersAndBall(w);
    PlayerState p1 = w.Players.First(p => p.Id == 1);
    True(TennisRules.IsLegalServeX(w.Score, 1, p1.Position.X),
        "Player 1 deuce serve must mirror onto its assigned half.");
}

static void ServeTossExactApex()
{
    WorldState w = World(); var p = new TennisPhysics(); var r = new TennisRules();
    w.Ball.InPlay = true;
    w.Ball.IsServeToss = true;
    w.Ball.SelectedTossHeight = 4.321f;
    w.Ball.Position = new Vector3(0, 1.15f, -GameConstants.CourtHalfLength - .2f);
    w.Ball.Velocity = new Vector3(0, MathF.Sqrt(2f * GameConstants.Gravity * (w.Ball.SelectedTossHeight - w.Ball.Position.Y)), 0);
    float highest = w.Ball.Position.Y;
    for (int i = 0; i < 500 && w.Ball.InPlay; i++)
    {
        p.StepBall(w, GameConstants.FixedDt, r);
        highest = MathF.Max(highest, w.Ball.Position.Y);
    }
    True(MathF.Abs(highest - 4.321f) < 0.0001f, $"Expected exact apex 4.321, got {highest}.");
}

static void ServeMustLandDiagonally()
{
    WorldState w = World(); var p = new TennisPhysics(); var r = new TennisRules();
    w.Phase = MatchPhase.Rally;
    w.Score.Server = 0;
    w.Ball.InPlay = true;
    w.Ball.ServeMustBounce = true;
    w.Ball.ServeTargetXSign = -1;
    w.Ball.LastHitBy = 0;
    w.Ball.Position = new Vector3(1f, GameConstants.BallRadius + .001f, 3f);
    w.Ball.Velocity = new Vector3(0, -2f, 0);
    p.StepBall(w, GameConstants.FixedDt, r);
    Equal(2, w.Score.ServeNumber);
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Near(Vector3 expected, Vector3 actual, float epsilon)
{
    if (Vector3.Distance(expected, actual) > epsilon) throw new Exception($"Expected {expected}, got {actual}.");
}
static void NearScalar(float expected, float actual, float epsilon)
{
    if (MathF.Abs(expected - actual) > epsilon) throw new Exception($"Expected {expected}, got {actual}.");
}

static int RunPhysicsSoak()
{
    var random = new Random(123456);
    var rules = new TennisRules();
    for (int match = 0; match < 100; match++)
    {
        WorldState w = World();
        var physics = new TennisPhysics();
        w.Phase = MatchPhase.Rally;
        w.Ball.InPlay = true;
        w.Ball.Position = new Vector3(0, 1.2f, -5f);
        w.Ball.Velocity = new Vector3(
            (float)(random.NextDouble() * 12 - 6),
            (float)(random.NextDouble() * 10 + 4),
            (float)(random.NextDouble() * 18 + 14));
        w.Ball.AngularVelocity = new Vector3(
            (float)(random.NextDouble() * 250 - 125),
            (float)(random.NextDouble() * 180 - 90), 0);
        for (int tick = 0; tick < 120 * 90 && w.Phase != MatchPhase.MatchOver; tick++)
        {
            physics.StepBall(w, GameConstants.FixedDt, rules);
            if (!Finite(w.Ball.Position) || !Finite(w.Ball.Velocity))
            {
                Console.WriteLine($"FAIL physics soak match {match}, tick {tick}");
                return 1;
            }
            if (!w.Ball.InPlay)
            {
                w.Phase = MatchPhase.Rally;
                w.Ball.InPlay = true;
                w.Ball.Position = new Vector3(0, 1.2f, -5f);
                w.Ball.Velocity = new Vector3(0, 7f, 22f);
            }
        }
    }
    Console.WriteLine("PASS physics soak: 100 deterministic simulated matches.");
    return 0;
}

static int RunPacketFuzz()
{
    var random = new Random(98765);
    int safelyRejected = 0;
    for (int i = 0; i < 50_000; i++)
    {
        byte[] bytes = new byte[random.Next(0, 2048)];
        random.NextBytes(bytes);
        try { _ = NetPacket.Unpack(bytes); }
        catch (JsonException) { safelyRejected++; }
        catch (InvalidDataException) { safelyRejected++; }
        catch (NotSupportedException) { safelyRejected++; }
    }
    Console.WriteLine($"PASS packet fuzz: {safelyRejected}/50000 malformed packets safely rejected.");
    return 0;
}

static bool Finite(Vector3 value) =>
    float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);


static void AutomaticServeLaunch()
{
    WorldState w = World();
    var physics = new TennisPhysics();
    PlayerState server = w.Players[0];
    w.Phase = MatchPhase.ServeToss;
    w.Ball.InPlay = true;
    w.Ball.IsServeToss = true;
    w.Ball.TossOwner = 0;
    w.Ball.Position = new Vector3(server.Position.X, 3.05f, server.Position.Z);
    w.Ball.SelectedTossX = -1.2f;
    w.Ball.SelectedTossHeight = 4.5f;
    physics.LaunchAutomaticServe(w, server);
    True(!w.Ball.IsServeToss, "Automatic serve should end toss state at contact.");
    True(w.Ball.ServeMustBounce, "Automatic serve must be validated on first bounce.");
    Equal(MatchPhase.Rally, w.Phase);
    Equal(0, w.Ball.LastHitBy);
    True(w.Ball.Velocity.Length() > 1f, "Automatic serve must launch a visible moving ball.");
}

static void RacquetOvalContact()
{
    WorldState w = World();
    var physics = new TennisPhysics();
    PlayerState receiver = w.Players[0];
    receiver.Swinging = true;
    receiver.SwingProgress = SwingAnimation.DurationSeconds * 0.5f;
    w.Phase = MatchPhase.Rally;
    w.Ball.InPlay = true;
    w.Ball.LastHitBy = 1;

    var input = new InputPacket(
        1, 0f, 0f, ShotControls.EncodeStyle(ShotStyle.Forehand), 0.50f, true, false, HandedSwing.Right,
        1.2f, false, 0f, 0f, 900f, 0f, false, 0f, 0f);
    RacquetHitboxPose hitbox = RacquetGeometry.GetHitbox(receiver, input);
    w.Ball.Position = hitbox.Center;
    w.Ball.Velocity = new Vector3(0f, -0.5f, -24f);

    True(physics.TryRacquetHit(w, receiver, input),
        "Ball at the v2.1 racquet oval center must register contact.");
    Equal(0, w.Ball.LastHitBy);
    True(w.Ball.Velocity.Z > 0f, "Player 0 return must travel toward the opponent side.");
}

static void RacquetOvalRejectsOutsideEllipsoid()
{
    WorldState w = World();
    var physics = new TennisPhysics();
    PlayerState receiver = w.Players[0];
    receiver.Swinging = true;
    receiver.SwingProgress = SwingAnimation.DurationSeconds * 0.5f;
    w.Phase = MatchPhase.Rally;
    w.Ball.InPlay = true;
    w.Ball.LastHitBy = 1;

    var input = new InputPacket(
        1, 0f, 0f, ShotControls.EncodeStyle(ShotStyle.Forehand), 0.50f, true, false, HandedSwing.Right,
        1.2f, false, 0f, 0f, 900f, 0f, false, 0f, 0f);
    RacquetHitboxPose hitbox = RacquetGeometry.GetHitbox(receiver, input);

    // Put the ball safely beyond the enlarged short axis plus ball padding.
    w.Ball.Position = hitbox.Center + hitbox.WidthAxis * (hitbox.WidthRadius + GameConstants.BallRadius + 0.50f);
    w.Ball.Velocity = new Vector3(0f, -0.5f, -24f);

    True(!physics.TryRacquetHit(w, receiver, input),
        "A ball clearly outside the enlarged ellipsoid must not register contact.");
}


static Vector3 SimulateTimedContact(HandedSwing hand, float forwardFraction, float playerX = 0f, float racquetControl = 0f)
{
    WorldState w = World();
    var physics = new TennisPhysics();
    PlayerState receiver = w.Players[0];
    receiver.Position = new Vector3(playerX, 0f, -9.5f);
    receiver.Swinging = true;
    receiver.SwingProgress = SwingAnimation.DurationSeconds * 0.5f;
    w.Phase = MatchPhase.Rally;
    w.Ball.InPlay = true;
    w.Ball.LastHitBy = 1;

    var input = new InputPacket(
        10, 0f, 0f, ShotControls.EncodeStyle(ShotStyle.Forehand), 0.65f, true, false, hand,
        1.2f, false, racquetControl, 0f, 1200f, 0f, false, 0f, 0f);
    RacquetHitboxPose hitbox = RacquetGeometry.GetHitbox(receiver, input);
    Vector3 forward = Vector3.UnitZ;
    float inverseRadialSquared =
        MathF.Pow(Vector3.Dot(forward, hitbox.LongAxis) / hitbox.LongRadius, 2f) +
        MathF.Pow(Vector3.Dot(forward, hitbox.WidthAxis) / hitbox.WidthRadius, 2f) +
        MathF.Pow(Vector3.Dot(forward, hitbox.DepthAxis) / hitbox.DepthRadius, 2f);
    float forwardRadialRadius = 1f / MathF.Sqrt(Math.Max(0.000001f, inverseRadialSquared));
    w.Ball.Position = hitbox.Center + forward * (forwardRadialRadius * forwardFraction);
    w.Ball.Velocity = new Vector3(0f, -0.2f, -20f);

    True(physics.TryRacquetHit(w, receiver, input), "Timed contact point must intersect the racquet oval.");
    return w.Ball.Velocity;
}

static void ContactTimingSteersRightHand()
{
    Vector3 early = SimulateTimedContact(HandedSwing.Right, +0.70f);
    Vector3 center = SimulateTimedContact(HandedSwing.Right, 0f);
    Vector3 late = SimulateTimedContact(HandedSwing.Right, -0.70f);
    True(early.X < center.X - 0.5f, "Early right-hand contact should steer left.");
    True(late.X > center.X + 0.5f, "Late right-hand contact should steer right.");
}

static void ContactTimingMirrorsLeftHand()
{
    Vector3 early = SimulateTimedContact(HandedSwing.Left, +0.70f);
    Vector3 center = SimulateTimedContact(HandedSwing.Left, 0f);
    Vector3 late = SimulateTimedContact(HandedSwing.Left, -0.70f);
    True(early.X > center.X + 0.5f, "Early left-hand contact should steer right.");
    True(late.X < center.X - 0.5f, "Late left-hand contact should steer left.");
}


static void PreparationTimingStacksWithContact()
{
    // v2.6.15 empirical correction: the preparation layer is reversed at the
    // final steering stage while actual oval contact timing remains unchanged.
    // Verify that the corrected preparation contribution still stacks and clamps
    // at +/-1.25 instead of replacing contact timing.
    float max = RacquetControls.MaximumDepthAt;

    // With the corrected gameplay sign, +control contributes toward the opposite
    // side from the pre-v2.6.15 build. Confirm it still adds rather than replacing.
    Vector3 neutral = SimulateTimedContact(HandedSwing.Right, 0f, 0f, 0f);
    Vector3 positivePrep = SimulateTimedContact(HandedSwing.Right, 0f, 0f, max);
    Vector3 negativePrep = SimulateTimedContact(HandedSwing.Right, 0f, 0f, -max);
    True(positivePrep.X > neutral.X + 0.20f, "Corrected + preparation must steer right of neutral for right hand.");
    True(negativePrep.X < neutral.X - 0.20f, "Corrected - preparation must steer left of neutral for right hand.");

    // Stacking: +1 contact plus corrected +control cancel toward neutral; +1 contact
    // plus corrected -control reinforce. The inverse must hold for -1 contact.
    Vector3 earlyNeutral = SimulateTimedContact(HandedSwing.Right, +1.0f, 0f, 0f);
    Vector3 earlyReinforced = SimulateTimedContact(HandedSwing.Right, +1.0f, 0f, -max);
    True(earlyReinforced.X < earlyNeutral.X - 0.20f, "Corrected preparation must still stack with early contact.");

    Vector3 lateNeutral = SimulateTimedContact(HandedSwing.Right, -1.0f, 0f, 0f);
    Vector3 lateReinforced = SimulateTimedContact(HandedSwing.Right, -1.0f, 0f, max);
    True(lateReinforced.X > lateNeutral.X + 0.20f, "Corrected preparation must still stack with late contact.");

    // +/-1.25 cap remains authoritative.
    Vector3 cappedEarlyA = SimulateTimedContact(HandedSwing.Right, +1.0f, 0f, -max);
    Vector3 cappedEarlyB = SimulateTimedContact(HandedSwing.Right, +0.25f, 0f, -max);
    True(MathF.Abs(cappedEarlyA.X - cappedEarlyB.X) < 0.001f, "Corrected early stack should cap at +1.25.");

    Vector3 cappedLateA = SimulateTimedContact(HandedSwing.Right, -1.0f, 0f, max);
    Vector3 cappedLateB = SimulateTimedContact(HandedSwing.Right, -0.25f, 0f, max);
    True(MathF.Abs(cappedLateA.X - cappedLateB.X) < 0.001f, "Corrected late stack should cap at -1.25.");
}

static void PreparationTimingFollowsPhysicalAngle()
{
    float max = RacquetControls.MaximumControl;
    float neutral = RacquetControls.TimingEffectFromForwardBack(0f, HandedSwing.Right);
    float forward45 = RacquetControls.TimingEffectFromForwardBack(max * 0.5f, HandedSwing.Right);
    float forward90 = RacquetControls.TimingEffectFromForwardBack(max, HandedSwing.Right);
    float backward45 = RacquetControls.TimingEffectFromForwardBack(-max * 0.5f, HandedSwing.Right);
    float backward90 = RacquetControls.TimingEffectFromForwardBack(-max, HandedSwing.Right);

    True(MathF.Abs(neutral) < 0.001f, "Neutral forward/back pose should add no timing effect.");
    True(MathF.Abs(forward45 - MathF.Sqrt(0.5f)) < 0.002f, "45-degree forward pose should add sqrt(1/2) early effect.");
    True(MathF.Abs(forward90 - 1f) < 0.001f, "90-degree forward pose should equal maximum early effect.");
    True(MathF.Abs(backward45 + MathF.Sqrt(0.5f)) < 0.002f, "45-degree backward pose should add sqrt(1/2) late effect.");
    True(MathF.Abs(backward90 + 1f) < 0.001f, "90-degree backward pose should equal maximum late effect.");
}

static void PreparationTimingMirrorsByHand()
{
    Vector3 rightForward = SimulateTimedContact(HandedSwing.Right, 0f, 0f, RacquetControls.MaximumDepthAt);
    Vector3 leftForward = SimulateTimedContact(HandedSwing.Left, 0f, 0f, RacquetControls.MaximumDepthAt);
    True(rightForward.X > 0f, "v2.6.15 forward preparation steering must use corrected gameplay sign for right hand.");
    True(leftForward.X < 0f, "v2.6.15 forward preparation steering must mirror corrected gameplay sign for left hand.");

    Vector3 rightBackward = SimulateTimedContact(HandedSwing.Right, 0f, 0f, -RacquetControls.MaximumDepthAt);
    Vector3 leftBackward = SimulateTimedContact(HandedSwing.Left, 0f, 0f, -RacquetControls.MaximumDepthAt);
    True(rightBackward.X < 0f, "v2.6.15 backward preparation steering must use corrected gameplay sign for right hand.");
    True(leftBackward.X > 0f, "v2.6.15 backward preparation steering must mirror corrected gameplay sign for left hand.");
}

static void CornerAimBiasesTowardCenter()
{
    Vector3 fromCenter = SimulateTimedContact(HandedSwing.Right, 0f, 0f);
    Vector3 fromRightCorner = SimulateTimedContact(HandedSwing.Right, 0f, GameConstants.SinglesHalfWidth * 0.90f);
    Vector3 fromLeftCorner = SimulateTimedContact(HandedSwing.Left, 0f, -GameConstants.SinglesHalfWidth * 0.90f);
    True(fromRightCorner.X < fromCenter.X - 0.5f, "Right-corner neutral contact should bias left toward court center.");
    True(fromLeftCorner.X > fromCenter.X + 0.5f, "Left-corner neutral contact should bias right toward court center.");
}

static (Vector3 Velocity, Vector3 Spin) SimulateReturn(
    ShotStyle style, float faceDegrees, float selectedPower, HandedSwing hand = HandedSwing.Right, float tension = StringTension.Default)
{
    WorldState w = World();
    var physics = new TennisPhysics();
    PlayerState receiver = w.Players[0];
    receiver.SwingProgress = 1.25f;
    w.Phase = MatchPhase.Rally;
    w.Ball.InPlay = true;
    w.Ball.LastHitBy = 1;
    float angle = faceDegrees * MathF.PI / 180f;
    var input = new InputPacket(
        2, 0f, 0f, ShotControls.EncodeStyle(style), selectedPower, true, false, hand,
        1.2f, false, 0f, angle, 2200f, 0f, false, 0f, 0f);
    RacquetHitboxPose hitbox = RacquetGeometry.GetHitbox(receiver, input);
    w.Ball.Position = hitbox.Center;
    w.Ball.Velocity = new Vector3(0f, -0.2f, -20f);
    True(physics.TryRacquetHit(w, receiver, input, tension), "Expected v2.1 oval-racquet return.");
    return (w.Ball.Velocity, w.Ball.AngularVelocity);
}


static void StringTensionRecycleFormula()
{
    Vector3 incoming = new Vector3(0f, 0f, -20f);
    float low = StringTension.RecycledIncomingSpeed(incoming, 0.10f, 0f);
    float normal = StringTension.RecycledIncomingSpeed(incoming, 0.40f, 0f);
    float high = StringTension.RecycledIncomingSpeed(incoming, 0.70f, 0f);
    float highFifty = StringTension.RecycledIncomingSpeed(incoming, 0.70f, MathF.PI * 50f / 180f);
    True(MathF.Abs(low - 2f) < 0.001f, "10% tension should recycle 10% of 20-unit incoming pace at 0 degrees.");
    True(MathF.Abs(normal - 8f) < 0.001f, "40% tension should recycle 40% of incoming pace at 0 degrees.");
    True(MathF.Abs(high - 14f) < 0.001f, "70% tension should recycle 70% of incoming pace at 0 degrees.");
    True(highFifty < high, "Recycled incoming pace must decrease as absolute racquet angle increases.");
}

static void StringTensionAffectsReturnSpeed()
{
    float low = SimulateReturn(ShotStyle.Forehand, 0f, 1f, HandedSwing.Right, 0.10f).Velocity.Length();
    float normal = SimulateReturn(ShotStyle.Forehand, 0f, 1f, HandedSwing.Right, 0.40f).Velocity.Length();
    float high = SimulateReturn(ShotStyle.Forehand, 0f, 1f, HandedSwing.Right, 0.70f).Velocity.Length();
    True(low < normal && normal < high, "Higher tension should recycle more opponent pace into the return.");

    // With no incoming opponent velocity there is nothing to recycle, so tension
    // must not increase the player's own generated shot power.
    var physics = new TennisPhysics();
    var wLow = TestWorld();
    var wHigh = TestWorld();
    var pLow = wLow.Players[0];
    var pHigh = wHigh.Players[0];
    pLow.Swinging = pHigh.Swinging = true;
    pLow.SwingProgress = pHigh.SwingProgress = 1.25f;
    wLow.Phase = wHigh.Phase = MatchPhase.Rally;
    wLow.Ball.InPlay = wHigh.Ball.InPlay = true;
    wLow.Ball.LastHitBy = wHigh.Ball.LastHitBy = 1;
    var input = new InputPacket(2, 0f, 0f, ShotControls.EncodeStyle(ShotStyle.Forehand), 1f, true, false,
        HandedSwing.Right, 1.2f, false, 0f, 0f, 2200f, 0f, false, 0f, 0f);
    wLow.Ball.Position = RacquetGeometry.GetHitbox(pLow, input).Center;
    wHigh.Ball.Position = RacquetGeometry.GetHitbox(pHigh, input).Center;
    wLow.Ball.Velocity = wHigh.Ball.Velocity = Vector3.Zero;
    True(physics.TryRacquetHit(wLow, pLow, input, 0.10f), "Low-tension zero-incoming test should hit.");
    physics = new TennisPhysics();
    True(physics.TryRacquetHit(wHigh, pHigh, input, 0.70f), "High-tension zero-incoming test should hit.");
    True(MathF.Abs(wLow.Ball.Velocity.Length() - wHigh.Ball.Velocity.Length()) < 0.001f,
        "Tension must not amplify self-generated swing power when incoming pace is zero.");
}

static void PowerMeterTiming()
{
    True(MathF.Abs(PowerMeter.ValueAt(0.00f) - 0.00f) < 0.001f, "Power meter should start at 0%.");
    True(MathF.Abs(PowerMeter.ValueAt(0.25f) - 0.50f) < 0.001f, "Power meter should be 50% after 0.25s.");
    True(MathF.Abs(PowerMeter.ValueAt(0.50f) - 1.00f) < 0.001f, "Power meter must reach 100% at 0.5s.");
    True(MathF.Abs(PowerMeter.ValueAt(0.75f) - 1.00f) < 0.001f, "Power meter should hold at 100% after charging.");
    True(MathF.Abs(PowerMeter.ValueAt(1.00f) - 1.00f) < 0.001f, "Power meter should remain at 100% while held.");
    True(MathF.Abs(PowerMeter.ValueAt(1.50f) - 1.00f) < 0.001f, "Power meter must not discharge without a swing.");
    True(MathF.Abs(PowerMeter.PreparationControl(0f)) < 0.001f, "0% preparation should be perpendicular/out to the side.");
    True(MathF.Abs(PowerMeter.PreparationControl(1f) + RacquetControls.MaximumDepthAt) < 0.001f,
        "100% preparation should reach maximum backswing depth.");

    // Visual-only charge animation is relative to the pose present when charging begins.
    float max = RacquetControls.MaximumControl;
    True(MathF.Abs(PowerMeter.ChargeAnimationControl(max, 0f) - max) < 0.001f,
        "Charge animation must begin at the exact starting forward pose.");
    True(MathF.Abs(PowerMeter.ChargeAnimationControl(max, 1f)) < 0.001f,
        "A fully-forward start should back up only to neutral at 100% charge.");
    True(MathF.Abs(PowerMeter.ChargeAnimationControl(0f, 0.5f) + max * 0.5f) < 0.001f,
        "A neutral start should be halfway to maximum backward at 50% charge.");
    True(MathF.Abs(PowerMeter.ChargeAnimationControl(0f, 1f) + max) < 0.001f,
        "A neutral start should reach maximum backward at 100% charge.");
    True(MathF.Abs(PowerMeter.ChargeAnimationControl(-max * 0.5f, 0.5f) + max) < 0.001f,
        "An already-backward start should reach the backward clamp sooner.");
    True(MathF.Abs(PowerMeter.ChargeAnimationControl(-max, 1f) + max) < 0.001f,
        "Charge animation must never wrap past the 90-degree backward clamp.");
}

static void PowerSelectionScalesShot()
{
    var full = SimulateReturn(ShotStyle.Forehand, 0f, 1.0f).Velocity.Length();
    var half = SimulateReturn(ShotStyle.Forehand, 0f, 0.5f).Velocity.Length();
    var tenth = SimulateReturn(ShotStyle.Forehand, 0f, 0.1f).Velocity.Length();
    True(full > half && half > tenth, "Power percentage must monotonically scale shot speed.");
    True(MathF.Abs(half / full - 0.5f) < 0.08f, "50% should be close to half of the old 100% pace.");
    True(MathF.Abs(tenth / full - 0.1f) < 0.05f, "10% should be close to one tenth of full pace.");
}

static void ForehandPresetTrajectories()
{
    Vector3 top = SimulateReturn(ShotStyle.Forehand, -15f, 1f).Velocity;
    Vector3 flat = SimulateReturn(ShotStyle.Forehand, 0f, 1f).Velocity;
    Vector3 lob = SimulateReturn(ShotStyle.Forehand, 15f, 1f).Velocity;
    True(top.Z > flat.Z && flat.Z > lob.Z, "Topspin drive should have most pace; lob should have least.");
    True(lob.Y > flat.Y && flat.Y > top.Y, "Lob should have highest arc; topspin should have lowest.");
    True(top.Y > 7.0f, "-15 degree topspin drive should retain useful net clearance.");
}

static void SlicePresetSpinDirection()
{
    var right15 = SimulateReturn(ShotStyle.Slice, 15f, 1f, HandedSwing.Right);
    var right50 = SimulateReturn(ShotStyle.Slice, 50f, 1f, HandedSwing.Right);
    var left50 = SimulateReturn(ShotStyle.Slice, 50f, 1f, HandedSwing.Left);
    True(right15.Spin.Y < 0f && right50.Spin.Y < right15.Spin.Y,
        "Right-hand slice must veer player-left and increase spin with angle.");
    True(left50.Spin.Y > 0f, "Left-hand slice must veer player-right.");
    True(right50.Velocity.Y > right15.Velocity.Y, "Higher slice angle should create a higher arc.");
}

static void SliceVeerStartsAfterBounce()
{
    WorldState w = World();
    var physics = new TennisPhysics();
    PlayerState receiver = w.Players[0];
    receiver.SwingProgress = 1.25f;
    w.Phase = MatchPhase.Rally;
    w.Ball.InPlay = true;
    w.Ball.LastHitBy = 1;
    w.Ball.Position = receiver.Position + new Vector3(0f, 1.4f, 1.8f);
    w.Ball.Velocity = new Vector3(0f, -0.2f, -20f);

    var input = new InputPacket(
        3, 0f, 0f, ShotControls.EncodeStyle(ShotStyle.Slice), 1f, true, false, HandedSwing.Right,
        1.2f, false, 0f, 50f * MathF.PI / 180f, 2200f, 0f, false, 0f, 0f);
    True(physics.TryRacquetHit(w, receiver, input), "Expected slice contact.");

    float launchX = w.Ball.Velocity.X;
    physics.StepBall(w, 0.01f, new TennisRules());
    True(MathF.Abs(w.Ball.Velocity.X - launchX) < 0.05f,
        "Slice must not acquire meaningful sideways veer before its first bounce.");

    // Force the first ground contact, then advance one flight step. The stored
    // vertical-axis slice spin should begin producing hand-opposite Magnus curve.
    w.Ball.Position = new Vector3(w.Ball.Position.X, GameConstants.BallRadius + 0.001f, w.Ball.Position.Z);
    w.Ball.Velocity = new Vector3(w.Ball.Velocity.X, -3f, MathF.Abs(w.Ball.Velocity.Z));
    physics.StepBall(w, 0.01f, new TennisRules());
    True(w.Ball.BounceCount >= 1, "Expected first bounce.");
    float afterBounceX = w.Ball.Velocity.X;
    physics.StepBall(w, 0.03f, new TennisRules());
    True(w.Ball.Velocity.X < afterBounceX - 0.01f,
        "Right-hand slice should begin veering left only after the first bounce.");
}

static void RacquetQuarterHoopStopsAtPerpendicular()
{
    RacquetHoopPose maxForward = RacquetControls.GetPose(RacquetControls.MaximumControl, HandedSwing.Right);
    RacquetHoopPose maxBackward = RacquetControls.GetPose(-RacquetControls.MaximumControl, HandedSwing.Right);
    RacquetHoopPose beyondForward = RacquetControls.GetPose(RacquetControls.MaximumControl * 2f, HandedSwing.Right);

    True(maxForward.ForwardOffset > 0.60f, "Maximum forward control must reach the 90-degree forward endpoint.");
    True(maxBackward.ForwardOffset < -0.60f, "Maximum backward control must reach the 90-degree backward endpoint.");
    True(MathF.Abs(maxForward.LateralOffset) < 0.001f && MathF.Abs(maxBackward.LateralOffset) < 0.001f,
        "Both 90-degree endpoints must stop at the body centerline.");
    True(Vector3.Distance(new Vector3(maxForward.LateralOffset, 0f, maxForward.ForwardOffset),
                          new Vector3(beyondForward.LateralOffset, 0f, beyondForward.ForwardOffset)) < 0.001f,
        "Input beyond the 90-degree limit must clamp instead of wrapping around the player.");
}

static void RacquetHalfHoopMirrorsHands()
{
    RacquetHoopPose left = RacquetControls.GetPose(0f, HandedSwing.Left);
    RacquetHoopPose right = RacquetControls.GetPose(0f, HandedSwing.Right);
    True(left.LateralOffset > 0f && right.LateralOffset < 0f, "Hand-side offsets must mirror.");
    True(MathF.Abs(left.LateralOffset + right.LateralOffset) < 0.001f, "Mirrored offsets must have equal reach.");
}

static void ForehandSwingAnimation()
{
    var start = SwingAnimation.GetPose(0f, ShotStyle.Forehand, 1.25f);
    var middle = SwingAnimation.GetPose(0.5f, ShotStyle.Forehand, 1.25f);
    var end = SwingAnimation.GetPose(1f, ShotStyle.Forehand, 1.25f);
    True(start.Height < middle.Height && middle.Height < end.Height, "Forehand must rise throughout the stroke.");
    True(start.LateralFactor > middle.LateralFactor && middle.LateralFactor > end.LateralFactor, "Forehand must finish closer to the body.");
}

static void SliceSwingAnimation()
{
    var start = SwingAnimation.GetPose(0f, ShotStyle.Slice, 1.25f);
    var middle = SwingAnimation.GetPose(0.5f, ShotStyle.Slice, 1.25f);
    var end = SwingAnimation.GetPose(1f, ShotStyle.Slice, 1.25f);
    True(start.Height > middle.Height && middle.Height > end.Height, "Slice must descend throughout the stroke.");
    True(start.LateralFactor > middle.LateralFactor && middle.LateralFactor > end.LateralFactor, "Slice must finish closer to the body.");
}
