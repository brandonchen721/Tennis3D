using System.Numerics;

namespace Tennis3D.Shared;

public sealed class TennisPhysics
{
    // Previous authoritative ball position, used for continuous racquet collision
    // detection. This prevents a fast ball from tunneling through the string bed
    // between server physics frames.
    private Vector3 previousBallPosition;
    private bool hasPreviousBallPosition;
    public void LaunchAutomaticServe(WorldState world, PlayerState player)
    {
        BallState ball = world.Ball;
        if (!ball.InPlay || !ball.IsServeToss || ball.TossOwner != player.Id) return;

        // The randomized target was chosen authoritatively by the server when the
        // player clicked the top-down service box. Solve a gravity-only ballistic
        // launch so every client sees the same complete flight and first bounce.
        Vector3 target = new(ball.SelectedTossX, GameConstants.BallRadius, ball.SelectedTossHeight);
        Vector3 horizontalDelta = new(target.X - ball.Position.X, 0f, target.Z - ball.Position.Z);
        float distance = Math.Max(0.25f, horizontalDelta.Length());
        float horizontalSpeed = 31f;
        float flightTime = Math.Max(0.22f, distance / horizontalSpeed);
        Vector3 horizontalVelocity = horizontalDelta / flightTime;
        float verticalVelocity = (target.Y - ball.Position.Y +
                                  0.5f * GameConstants.Gravity * flightTime * flightTime) /
                                 flightTime;

        ball.Velocity = new Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
        ball.AngularVelocity = new Vector3(105f, 0f, 0f);
        ball.LastHitBy = player.Id;
        ball.BounceCount = 0;
        ball.IsServeToss = false;
        ball.TossOwner = -1;
        ball.ServeMustBounce = true;
        ball.ServeBounceCount = 0;
        world.Phase = MatchPhase.Rally;
        world.Message = "Serve struck automatically — watch the landing.";
    }

    public void StepBall(WorldState world, float dt, TennisRules rules)
    {
        BallState ball = world.Ball;
        if (!ball.InPlay)
        {
            hasPreviousBallPosition = false;
            return;
        }

        // Save the beginning of this authoritative physics step. On the next input
        // frame TryRacquetHit tests the entire segment from here to the new position.
        previousBallPosition = ball.Position;
        hasPreviousBallPosition = true;

        if (ball.IsServeToss)
        {
            // Standard server-authoritative toss: gravity only, no depth or lateral
            // drift. The player still has to strike the moving ball manually.
            ball.Velocity.Z = 0f;
            ball.Velocity.X = 0f;
            ball.Position.Y += ball.Velocity.Y * dt - 0.5f * GameConstants.Gravity * dt * dt;
            ball.Velocity.Y -= GameConstants.Gravity * dt;
        }
        else
        {
            BallFlightEvents events = BallFlightModel.AdvanceStep(
                ref ball.Position, ref ball.Velocity, ref ball.AngularVelocity,
                ref ball.BounceCount, ref ball.ServeMustBounce, dt);

            if ((events & BallFlightEvents.ServeNetFault) != 0)
            {
                if (world.Phase == MatchPhase.Rally && ball.LastHitBy == world.Score.Server)
                {
                    rules.ServeFault(world, world.Score.Server, "serve hit the net");
                    return;
                }
            }

            // A rally shot that strikes the net before its first legal bounce and is
            // reflected back has failed to cross the net, so the point ends as NET.
            // Serves are handled above as serve faults instead.
            if ((events & BallFlightEvents.NetContact) != 0 &&
                (events & BallFlightEvents.ServeNetFault) == 0 &&
                world.Phase == MatchPhase.Rally && ball.BounceCount == 0 && ball.LastHitBy >= 0)
            {
                rules.AwardPoint(world, 1 - ball.LastHitBy, "net");
                return;
            }

            if ((events & BallFlightEvents.GroundBounce) != 0)
            {
                if ((events & BallFlightEvents.ServeBounce) != 0)
                {
                    ball.ServeBounceCount++;
                    bool serverOnNearSide = world.Score.Server == 0;
                    bool landedOpposite = serverOnNearSide ? ball.Position.Z > 0 : ball.Position.Z < 0;
                    // Lines are IN: if any part of the ball footprint touches a
                    // service-box line, accept the bounce. This includes the center
                    // service line as well as the sideline/service line.
                    bool landedInTargetHalf = ball.ServeTargetXSign == 0 ||
                                              ball.Position.X * ball.ServeTargetXSign >= -GameConstants.BallRadius;
                    bool correctServiceBox = landedOpposite && landedInTargetHalf &&
                                             MathF.Abs(ball.Position.Z) <= GameConstants.ServiceLine + GameConstants.BallRadius &&
                                             MathF.Abs(ball.Position.X) <= GameConstants.SinglesHalfWidth + GameConstants.BallRadius;
                    if (!correctServiceBox)
                    {
                        rules.ServeFault(world, world.Score.Server, "serve missed the service box");
                        return;
                    }
                    world.Message = "Serve is in — rally in progress.";
                    return;
                }

                // Once a legal first bounce has happened, the receiver owns the next
                // play. A second ground contact is DOUBLE BOUNCE regardless of whether
                // that second bounce happens inside or outside the painted court.
                if (ball.BounceCount >= 2)
                {
                    int winner = ball.LastHitBy >= 0
                        ? ball.LastHitBy
                        : (ball.Position.Z < 0f ? 1 : 0);
                    rules.AwardPoint(world, winner, "double bounce");
                    return;
                }

                // Only the FIRST rally bounce decides IN/OUT.
                bool inSingles = BallTouchesSingles(ball.Position);
                if (!inSingles)
                {
                    rules.AwardPoint(world, ball.LastHitBy < 0 ? 0 : 1 - ball.LastHitBy, "ball landed out");
                    return;
                }

                // A legal rally shot must first bounce on the opponent's side.
                if (ball.BounceCount == 1 && ball.LastHitBy >= 0)
                {
                    bool landedOnOpponentSide = ball.LastHitBy == 0
                        ? ball.Position.Z > 0f
                        : ball.Position.Z < 0f;
                    if (!landedOnOpponentSide)
                    {
                        rules.AwardPoint(world, 1 - ball.LastHitBy, "ball bounced on hitter's side");
                        return;
                    }
                }
            }
        }

        if (ball.Position.Y < -2f || MathF.Abs(ball.Position.Z) > 23f || MathF.Abs(ball.Position.X) > 15f)
        {
            if (ball.ServeMustBounce && ball.LastHitBy == world.Score.Server)
            {
                rules.ServeFault(world, world.Score.Server, "serve left the playable area");
                return;
            }

            if (ball.BounceCount == 0)
            {
                // The shot never made a legal first bounce: this is genuinely OUT.
                int winner = ball.LastHitBy < 0 ? 0 : 1 - ball.LastHitBy;
                rules.AwardPoint(world, winner, "ball landed out");
            }
            else
            {
                // The shot already landed legally and the receiver never returned it.
                // If it leaves the simulation area before a physical second bounce,
                // the point is a MISSED return, not an OUT by the hitter.
                int winner = ball.LastHitBy >= 0 ? ball.LastHitBy : 0;
                rules.AwardPoint(world, winner, "missed return");
            }
        }
    }

    /// <summary>
    /// Continues the already-decided point's ball animation without invoking any
    /// scoring or racquet logic. The next point reset replaces the ball entirely.
    /// </summary>
    public void StepPointOverBall(WorldState world, float dt)
    {
        BallState ball = world.Ball;
        if (!ball.InPlay || ball.IsServeToss) return;
        BallFlightModel.AdvanceStep(
            ref ball.Position, ref ball.Velocity, ref ball.AngularVelocity,
            ref ball.BounceCount, ref ball.ServeMustBounce, dt);
    }

    private static bool BallTouchesSingles(Vector3 position) =>
        MathF.Abs(position.X) <= GameConstants.SinglesHalfWidth + GameConstants.BallRadius &&
        MathF.Abs(position.Z) <= GameConstants.CourtHalfLength + GameConstants.BallRadius;

    public bool TryRacquetHit(WorldState world, PlayerState player, InputPacket input, float stringTension = StringTension.Default)
    {
        // Contact is checked throughout the held/release animation, not only on the
        // exact mouse-up packet. This makes the collision window match what the
        // player sees and prevents a valid swing from missing due to packet timing.
        bool activeSwing = input.SwingHeld || input.SwingReleased || player.Swinging;
        if (!activeSwing || !world.Ball.InPlay) return false;
        if (world.Ball.IsServeToss && world.Ball.TossOwner != player.Id) return false;
        if (!world.Ball.IsServeToss && world.Ball.LastHitBy == player.Id) return false;

        // Capture the opponent's incoming pace before any outgoing velocity is built.
        // String tension may recycle part of this velocity, but never amplifies the
        // player's own swing-generated component. Serves have no opponent pace to recycle.
        Vector3 incomingVelocity = world.Ball.IsServeToss ? Vector3.Zero : world.Ball.Velocity;

        Vector3 forward = player.Id == 0 ? Vector3.UnitZ : -Vector3.UnitZ;
        Vector3 right = player.Id == 0 ? Vector3.UnitX : -Vector3.UnitX;
        HandedSwing swingHand = input.Hand == HandedSwing.None ? player.SwingHand : input.Hand;

        if (!TryGetRacquetContact(world, player, input, out float contactTiming)) return false;

        // Preserve the pre-update shot strength as the 100% baseline, then apply
        // the player's explicit 10%-100% multiplier to the final launch speed.
        float chargePower = Math.Clamp(player.SwingProgress / 1.25f, 0f, 1f);
        float speedPower = Math.Clamp(input.MouseSwingSpeed / 2200f, 0f, 1f);
        float legacyPower = Math.Clamp(0.22f + chargePower * 0.25f + speedPower * 0.70f, 0.22f, 1f);
        float selectedPower = ShotControls.DecodePower(input.AimPitch);
        ShotStyle shotStyle = ShotControls.DecodeStyle(input.AimYaw);
        float faceAngle = Math.Clamp(input.RacquetFaceAngle, -0.872665f, 0.872665f);
        float upwardBrush = Math.Clamp(input.MouseSwingVerticalSpeed / 1800f, -1f, 1f);
        float racquetDepthAtHit = Math.Clamp(
            RacquetControls.GetPose(input.RacquetHorizontal, swingHand).DepthNormalized, -1f, 1f);

        bool serve = world.Ball.IsServeToss;
        float launchY;
        float stylePaceMultiplier;
        float topSpin = 0f;
        float sideSpin = 0f;

        float faceDegrees = faceAngle * 180f / MathF.PI;
        if (shotStyle == ShotStyle.Forehand)
        {
            // Continuous wheel-controlled forehand face angle (-50..+50 degrees).
            // Negative closes the face for a faster topspin drive; positive opens it
            // for progressively higher, slower lobs. sqrt() makes useful changes near
            // neutral while still leaving extra range at the extremes.
            if (faceDegrees < 0f)
            {
                float topspinAmount = MathF.Sqrt(Math.Clamp(-faceDegrees / 50f, 0f, 1f));
                launchY = MathHelperLerp(0.30f, 0.16f, topspinAmount);
                stylePaceMultiplier = MathHelperLerp(1.00f, 1.35f, topspinAmount);
                topSpin = MathHelperLerp(25f, 260f, topspinAmount);
            }
            else
            {
                float lobAmount = MathF.Sqrt(Math.Clamp(faceDegrees / 50f, 0f, 1f));
                launchY = MathHelperLerp(0.30f, 1.14f, lobAmount);
                stylePaceMultiplier = MathHelperLerp(1.00f, 0.68f, lobAmount);
                topSpin = MathHelperLerp(25f, -45f, lobAmount);
            }
            launchY += upwardBrush * 0.10f;
        }
        else
        {
            // Slice uses the same -50..+50 wheel range. Higher face degrees produce
            // more arc and more stored sidespin. The sideways Magnus component remains
            // disabled until after bounce #1, preserving the post-bounce-only veer rule.
            float sliceAmount = Math.Clamp((faceDegrees + 50f) / 100f, 0f, 1f);
            launchY = MathHelperLerp(0.26f, 0.92f, sliceAmount) + upwardBrush * 0.06f;
            stylePaceMultiplier = MathHelperLerp(1.02f, 0.70f, sliceAmount);
            float veerDirection = swingHand == HandedSwing.Right ? -1f : 1f;
            sideSpin = veerDirection * MathHelperLerp(55f, 400f, sliceAmount);
            topSpin = MathHelperLerp(-10f, -90f, sliceAmount);
        }

        launchY = Math.Clamp(launchY, 0.035f, 1.45f);
        // Do not bake slice veer into the launch direction. Slice leaves the strings
        // aimed normally and acquires its sideways curve only after the first bounce.
        // v2.6.1 horizontal placement combines two influences:
        // 1) a mild bias back toward court center, and
        // 2) effective timing, which combines actual oval contact timing with the
        //    confirmed 1:1 forward/back preparation timing contribution.
        //
        // contactTiming is +1 on the net-side (early) edge and -1 on the
        // player-side (late) edge. Early right-hand contact steers left; late
        // right-hand contact steers right. The left hand mirrors that behavior.
        float centerWorldX = Math.Clamp(-player.Position.X / Math.Max(0.01f, GameConstants.SinglesHalfWidth), -1f, 1f);
        float centerBiasLocal = centerWorldX * right.X;

        // Forward/back preparation is an ADDITIONAL early/late contribution on top
        // of where the ball actually contacted the oval. Each component can supply
        // up to +/-1 on its own, but the combined steering signal is intentionally
        // capped at +/-1.25 so stacking is noticeable without becoming extreme.
        // v2.6.15: invert the preparation contribution at the final steering stage.
        // The mouse/visual pose remains unchanged; this corrects the observed gameplay
        // direction without touching actual oval contact timing. Forward visual pose
        // must add earliness in play; backward visual pose must add lateness.
        float preparationTiming = -RacquetControls.TimingEffectFromForwardBack(input.RacquetHorizontal, swingHand);
        float effectiveTiming = Math.Clamp(contactTiming + preparationTiming, -1.25f, 1.25f);
        float timingDirectionLocal = swingHand == HandedSwing.Right ? -effectiveTiming : effectiveTiming;
        float lateralAim = centerBiasLocal * 0.18f
                         + timingDirectionLocal * 0.28f;
        lateralAim = Math.Clamp(lateralAim, -0.53f, 0.53f);
        Vector3 horizontalAim = forward + right * lateralAim;
        Vector3 direction = Vector3.Normalize(horizontalAim + Vector3.UnitY * launchY);
        float baseShotSpeed = (serve ? 24f : 16f) + legacyPower * (serve ? 36f : 27f);
        float shotSpeed = baseShotSpeed * stylePaceMultiplier * selectedPower;
        float recycledIncomingSpeed = serve
            ? 0f
            : StringTension.RecycledIncomingSpeed(incomingVelocity, stringTension, faceAngle);

        if (serve)
        {
            bool behindBaseline = player.Id == 0
                ? player.Position.Z <= -GameConstants.CourtHalfLength
                : player.Position.Z >= GameConstants.CourtHalfLength;
            if (!behindBaseline)
            {
                new TennisRules().ServeFault(world, player.Id, "foot fault: server crossed the baseline");
                return false;
            }
            Vector3 target = new(world.Ball.SelectedTossX, GameConstants.BallRadius, world.Ball.SelectedTossHeight);
            Vector3 horizontalDelta = new(target.X - world.Ball.Position.X, 0f, target.Z - world.Ball.Position.Z);
            float distance = Math.Max(0.25f, horizontalDelta.Length());
            float horizontalSpeed = Math.Clamp((25f + legacyPower * 17f) * selectedPower, 2.5f, 48f);
            float flightTime = distance / horizontalSpeed;
            Vector3 horizontalVelocity = horizontalDelta / Math.Max(0.12f, flightTime);
            float verticalVelocity = (target.Y - world.Ball.Position.Y +
                                      0.5f * GameConstants.Gravity * flightTime * flightTime) /
                                     Math.Max(0.12f, flightTime);
            world.Ball.Velocity = new Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
        }
        else
        {
            world.Ball.Velocity = direction * (shotSpeed + recycledIncomingSpeed)
                                + player.Velocity * (0.35f * selectedPower);
        }

        world.Ball.AngularVelocity = new Vector3(
            topSpin * MathF.Max(0.35f, selectedPower),
            sideSpin,
            (-racquetDepthAtHit * 45f) * selectedPower);
        world.Ball.LastHitBy = player.Id;
        world.Ball.BounceCount = 0;
        world.Ball.IsServeToss = false;
        world.Ball.TossOwner = -1;
        world.Ball.ServeMustBounce = serve;
        world.Ball.ServeBounceCount = 0;
        world.Phase = MatchPhase.Rally;
        world.Message = "Racquet contact — rally in progress.";
        return true;
    }

    /// <summary>
    /// Tests the authoritative ball segment against the same oriented 3D oval used
    /// for human and AI racquet contact. This does not alter ball state.
    /// </summary>
    public bool IsRacquetContact(WorldState world, PlayerState player, InputPacket input)
    {
        return TryGetRacquetContact(world, player, input, out _);
    }

    private bool TryGetRacquetContact(WorldState world, PlayerState player, InputPacket input, out float contactTiming)
    {
        contactTiming = 0f;
        if (!world.Ball.InPlay) return false;

        RacquetHitboxPose hitbox = RacquetGeometry.GetHitbox(player, input);
        Vector3 segmentEnd = world.Ball.Position;
        Vector3 segmentStart = hasPreviousBallPosition ? previousBallPosition : segmentEnd;
        if (!SegmentIntersectsEllipsoid(segmentStart, segmentEnd, hitbox, GameConstants.BallRadius, out float segmentT))
            return false;

        // Use the closest point on the ball's swept segment, not animation time.
        // Positive timing means the net-side/forward half of the oval (early);
        // negative means the player-side/back half (late). Normalize by the
        // ellipsoid's support radius along the player's forward direction so the
        // value remains stable if hitbox proportions are tuned later.
        Vector3 contactPoint = Vector3.Lerp(segmentStart, segmentEnd, segmentT);
        Vector3 forward = player.Id == 0 ? Vector3.UnitZ : -Vector3.UnitZ;
        float forwardRadius = MathF.Sqrt(
            MathF.Pow(hitbox.LongRadius * Vector3.Dot(forward, hitbox.LongAxis), 2f) +
            MathF.Pow(hitbox.WidthRadius * Vector3.Dot(forward, hitbox.WidthAxis), 2f) +
            MathF.Pow(hitbox.DepthRadius * Vector3.Dot(forward, hitbox.DepthAxis), 2f));
        if (forwardRadius > 0.0001f)
            contactTiming = Math.Clamp(Vector3.Dot(contactPoint - hitbox.Center, forward) / forwardRadius, -1f, 1f);

        return true;
    }

    private static float MathHelperLerp(float a, float b, float t) => a + (b - a) * t;
    private static bool SegmentIntersectsEllipsoid(
        Vector3 segmentStart, Vector3 segmentEnd, RacquetHitboxPose hitbox, float padding, out float segmentT)
    {
        float longRadius = Math.Max(0.01f, hitbox.LongRadius + padding);
        float widthRadius = Math.Max(0.01f, hitbox.WidthRadius + padding);
        float depthRadius = Math.Max(0.01f, hitbox.DepthRadius + padding);

        Vector3 ToUnitEllipsoidSpace(Vector3 point)
        {
            Vector3 d = point - hitbox.Center;
            return new Vector3(
                Vector3.Dot(d, hitbox.LongAxis) / longRadius,
                Vector3.Dot(d, hitbox.WidthAxis) / widthRadius,
                Vector3.Dot(d, hitbox.DepthAxis) / depthRadius);
        }

        Vector3 a = ToUnitEllipsoidSpace(segmentStart);
        Vector3 b = ToUnitEllipsoidSpace(segmentEnd);
        Vector3 segment = b - a;
        float lengthSquared = segment.LengthSquared();
        segmentT = lengthSquared < 0.000001f
            ? 0f
            : Math.Clamp(Vector3.Dot(-a, segment) / lengthSquared, 0f, 1f);
        Vector3 closest = a + segment * segmentT;
        return closest.LengthSquared() <= 1f;
    }

    private static float SegmentSegmentDistanceSquared(
        Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        // Closest distance between two finite 3D segments. One segment is the
        // ball's authoritative travelled path; the other is the racquet handle.
        const float epsilon = 0.000001f;
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        float s;
        float t;

        if (a <= epsilon && e <= epsilon)
            return Vector3.DistanceSquared(p1, p2);

        if (a <= epsilon)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= epsilon)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denominator = a * e - b * b;
                s = denominator != 0f
                    ? Math.Clamp((b * f - c * e) / denominator, 0f, 1f)
                    : 0f;
                t = (b * s + f) / e;

                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }

        Vector3 closest1 = p1 + d1 * s;
        Vector3 closest2 = p2 + d2 * t;
        return Vector3.DistanceSquared(closest1, closest2);
    }

    private static bool SegmentIntersectsOrientedBox(
        Vector3 segmentStart, Vector3 segmentEnd, Vector3 center,
        Vector3 axisX, Vector3 axisY, Vector3 axisZ, Vector3 halfExtents)
    {
        // Transform the world-space segment into racquet-local coordinates, then
        // use a slab intersection test. This is continuous collision detection,
        // not a single-frame proximity check.
        Vector3 startDelta = segmentStart - center;
        Vector3 endDelta = segmentEnd - center;
        Vector3 localStart = new(
            Vector3.Dot(startDelta, axisX),
            Vector3.Dot(startDelta, axisY),
            Vector3.Dot(startDelta, axisZ));
        Vector3 localEnd = new(
            Vector3.Dot(endDelta, axisX),
            Vector3.Dot(endDelta, axisY),
            Vector3.Dot(endDelta, axisZ));
        Vector3 direction = localEnd - localStart;

        float enter = 0f;
        float exit = 1f;
        for (int axis = 0; axis < 3; axis++)
        {
            float start = axis == 0 ? localStart.X : axis == 1 ? localStart.Y : localStart.Z;
            float delta = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float extent = axis == 0 ? halfExtents.X : axis == 1 ? halfExtents.Y : halfExtents.Z;

            if (MathF.Abs(delta) < 0.000001f)
            {
                if (start < -extent || start > extent) return false;
                continue;
            }

            float inverse = 1f / delta;
            float near = (-extent - start) * inverse;
            float far = (extent - start) * inverse;
            if (near > far) (near, far) = (far, near);
            enter = MathF.Max(enter, near);
            exit = MathF.Min(exit, far);
            if (enter > exit) return false;
        }

        return true;
    }

    private static void ResetMissedToss(WorldState world)
    {
        int server = world.Score.Server;
        PlayerState? player = world.Players.FirstOrDefault(p => p.Id == server);
        world.Ball.InPlay = false;
        world.Ball.IsServeToss = false;
        world.Ball.Velocity = Vector3.Zero;
        world.Ball.AngularVelocity = Vector3.Zero;
        world.Ball.Position = (player?.Position ?? new Vector3(0, 0, server == 0 ? -9.5f : 9.5f))
                              + new Vector3(server == 0 ? -0.55f : 0.55f, 1.15f, 0);
        world.Phase = MatchPhase.ServeSetup;
        world.Message = "Toss missed. Click to choose another toss height.";
    }
}
