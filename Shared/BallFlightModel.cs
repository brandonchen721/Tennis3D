using System.Numerics;

namespace Tennis3D.Shared;


[Flags]
public enum BallFlightEvents
{
    None = 0,
    NetContact = 1,
    ServeNetFault = 2,
    GroundBounce = 4,
    ServeBounce = 8
}

/// <summary>
/// Single source of truth for free-flight acceleration and ground-bounce response.
/// Both the authoritative live ball and AI prediction use this model so trajectory
/// planning cannot silently drift from gameplay physics.
/// </summary>
public static class BallFlightModel
{
    public static Vector3 ComputeAcceleration(
        Vector3 velocity, Vector3 angularVelocity, int bounceCount, bool serveMustBounce)
    {
        if (serveMustBounce)
            return new Vector3(0f, -GameConstants.Gravity, 0f);

        float speed = velocity.Length();
        Vector3 drag = speed > 0.001f
            ? -0.5f * GameConstants.AirDensity * GameConstants.DragCoefficient *
              GameConstants.BallArea * speed * velocity / GameConstants.BallMass
            : Vector3.Zero;

        Vector3 magnusSpin = angularVelocity;
        // Slice sidespin is dormant until after the first bounce in the live game.
        if (bounceCount == 0)
            magnusSpin.Y = 0f;

        Vector3 magnus = GameConstants.MagnusCoefficient *
                          Vector3.Cross(magnusSpin, velocity) / GameConstants.BallMass;
        return new Vector3(0f, -GameConstants.Gravity, 0f) + drag + magnus;
    }

    /// <summary>
    /// Advances one complete non-toss physics step using the exact same ordering for
    /// live gameplay and AI prediction: free flight -> net response -> ground bounce.
    /// Rule decisions (service-box legality, point winner, etc.) intentionally remain
    /// outside this method, but every physical mutation is centralized here.
    /// </summary>
    public static BallFlightEvents AdvanceStep(
        ref Vector3 position, ref Vector3 velocity, ref Vector3 angularVelocity,
        ref int bounceCount, ref bool serveMustBounce, float dt)
    {
        Vector3 stepStart = position;
        AdvanceFreeFlight(ref position, ref velocity, ref angularVelocity, bounceCount, serveMustBounce, dt);

        BallFlightEvents events = BallFlightEvents.None;
        bool crossedNetPlane = TryGetNetPlaneCrossing(stepStart, position, out Vector3 netCrossing);
        bool hitNet = (crossedNetPlane &&
                       MathF.Abs(netCrossing.X) <= GameConstants.CourtHalfWidth + GameConstants.BallRadius &&
                       netCrossing.Y - GameConstants.BallRadius < NetHeightAtX(netCrossing.X)) ||
                      IsNetContact(position);
        if (hitNet)
        {
            events |= BallFlightEvents.NetContact;
            if (serveMustBounce)
                events |= BallFlightEvents.ServeNetFault;

            // Resolve from the incoming side of the net. Using the swept crossing
            // prevents fast balls from tunneling through the mesh between 120 Hz steps.
            // A tennis net is not a rigid wall: it gives substantially, absorbs most of
            // the ball's normal momentum, damps tangential motion, and leaves the ball
            // dropping close to the net instead of springing sharply backward.
            if (crossedNetPlane)
            {
                position.X = netCrossing.X;
                position.Y = netCrossing.Y;
            }
            float incomingSide = MathF.Abs(stepStart.Z) > 0.0001f
                ? stepStart.Z
                : -MathF.CopySign(1f, velocity.Z == 0f ? 1f : velocity.Z);
            position.Z = MathF.CopySign(GameConstants.NetThickness + GameConstants.BallRadius + 0.012f, incomingSide);

            float incomingNormalSpeed = MathF.Abs(velocity.Z);
            velocity.Z *= -0.06f;
            velocity.X *= 0.78f;
            velocity.Y *= velocity.Y > 0f ? 0.48f : 0.72f;
            velocity.Y -= MathF.Min(1.15f, incomingNormalSpeed * 0.025f);
            angularVelocity *= 0.76f;
        }

        if (position.Y <= GameConstants.BallRadius && velocity.Y < 0f)
        {
            bool wasServeBounce = serveMustBounce;
            ApplyGroundBounce(ref position, ref velocity, ref angularVelocity);
            bounceCount++;
            if (wasServeBounce)
            {
                serveMustBounce = false;
                events |= BallFlightEvents.ServeBounce;
            }
            events |= BallFlightEvents.GroundBounce;
        }

        return events;
    }

    public static void AdvanceFreeFlight(
        ref Vector3 position, ref Vector3 velocity, ref Vector3 angularVelocity,
        int bounceCount, bool serveMustBounce, float dt)
    {
        Vector3 acceleration = ComputeAcceleration(velocity, angularVelocity, bounceCount, serveMustBounce);
        position += velocity * dt + acceleration * (0.5f * dt * dt);
        velocity += acceleration * dt;
        angularVelocity *= MathF.Pow(0.992f, dt * 120f);
    }

    public static void ApplyGroundBounce(
        ref Vector3 position, ref Vector3 velocity, ref Vector3 angularVelocity)
    {
        position.Y = GameConstants.BallRadius;
        velocity.Y = -velocity.Y * GameConstants.GroundRestitution;
        Vector3 horizontal = new Vector3(velocity.X, 0f, velocity.Z);
        Vector3 spinSurface = Vector3.Cross(
            angularVelocity, new Vector3(0f, -GameConstants.BallRadius, 0f));
        horizontal += (spinSurface - horizontal) * GameConstants.GroundFriction;
        velocity.X = horizontal.X;
        velocity.Z = horizontal.Z;
        angularVelocity *= 0.82f;
    }

    public static bool TryGetNetPlaneCrossing(Vector3 start, Vector3 end, out Vector3 crossing)
    {
        float dz = end.Z - start.Z;
        if (MathF.Abs(dz) <= 0.000001f || (start.Z < 0f && end.Z < 0f) || (start.Z > 0f && end.Z > 0f))
        {
            crossing = default;
            return false;
        }

        float t = Math.Clamp(-start.Z / dz, 0f, 1f);
        crossing = Vector3.Lerp(start, end, t);
        return true;
    }

    public static float NetHeightAtX(float x)
    {
        float normalized = Math.Clamp(MathF.Abs(x) / GameConstants.CourtHalfWidth, 0f, 1f);
        // The center strap holds the net at 0.914 m while the cable rises smoothly
        // toward the posts. A quadratic curve gives a restrained, natural-looking sag.
        return GameConstants.NetHeightCenter +
               (GameConstants.NetHeightPost - GameConstants.NetHeightCenter) * normalized * normalized;
    }

    public static bool IsNetContact(Vector3 position)
    {
        return MathF.Abs(position.Z) <= GameConstants.NetThickness + GameConstants.BallRadius &&
               position.Y - GameConstants.BallRadius < NetHeightAtX(position.X) &&
               MathF.Abs(position.X) <= GameConstants.CourtHalfWidth + GameConstants.BallRadius;
    }
}
