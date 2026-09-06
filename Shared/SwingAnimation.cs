namespace Tennis3D.Shared;

public readonly record struct SwingAnimationPose(float LateralFactor, float ForwardFactor, float Height);

/// <summary>
/// Shared 0.5-second committed racquet animation. Both rendering and authoritative
/// collision use the same pose curve so the visible racquet and server hit volume agree.
/// </summary>
public static class SwingAnimation
{
    public const float DurationSeconds = 0.50f;

    public static SwingAnimationPose GetPose(float progress, ShotStyle style, float baseHeight)
    {
        progress = Math.Clamp(progress, 0f, 1f);
        float t = progress * progress * (3f - 2f * progress); // smoothstep

        // Both strokes begin farther away laterally and finish closer to the body.
        float lateral = Lerp(1.00f, 0.50f, t);
        float forward = Lerp(-0.18f, 0.72f, t);

        // Forehand brushes low-to-high. Slice cuts high-to-low.
        float low = Math.Clamp(baseHeight - 0.62f, 0.35f, 2.20f);
        float high = Math.Clamp(baseHeight + 0.72f, 0.55f, 2.35f);
        float height = style == ShotStyle.Slice ? Lerp(high, low, t) : Lerp(low, high, t);

        return new SwingAnimationPose(lateral, forward, height);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
