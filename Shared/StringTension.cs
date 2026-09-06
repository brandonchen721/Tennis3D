using System.Numerics;

namespace Tennis3D.Shared;

/// <summary>
/// Session-locked string rebound tuning. Tension does not amplify the player's
/// own swing. It only recycles a fraction of the opponent ball's incoming pace.
/// The recycled contribution is strongest at a neutral/continental face and
/// decreases as the absolute racquet face angle increases.
/// </summary>
public static class StringTension
{
    public const float Minimum = 0.10f;
    public const float Default = 0.40f;
    public const float Maximum = 0.70f;

    public static float Clamp(float tension) => Math.Clamp(tension, Minimum, Maximum);

    public static float RecycledIncomingSpeed(Vector3 incomingVelocity, float tension, float racquetAngleRadians)
    {
        tension = Clamp(tension);
        float angle = Math.Clamp(MathF.Abs(racquetAngleRadians), 0f, MathF.PI * 0.5f);
        float faceCoupling = MathF.Max(0f, MathF.Cos(angle));
        return incomingVelocity.Length() * tension * faceCoupling;
    }
}
