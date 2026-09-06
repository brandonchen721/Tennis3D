namespace Tennis3D.Shared;

/// <summary>
/// Shared racquet positioning used by both client rendering and authoritative
/// server collision. Horizontal mouse travel follows one half-hoop per hand:
/// neutral/outside -> maximum forward or backward at the body centerline.
/// </summary>
public static class RacquetControls
{
    public const float MaximumControl = 1.25f;
    public const float MaximumDepthAt = MaximumControl;
    public const float MaximumLateralReach = 0.86f;
    public const float MaximumDepthReach = 0.62f;

    /// <summary>
    /// Converts raw mouse travel into one shared forward/backward control value.
    /// The meaning is identical for both hands: positive is the forward half and
    /// negative is the backward half. Handedness mirrors only the lateral side.
    /// </summary>
    public static float ToHoopControl(float rawMouseHorizontal, HandedSwing hand)
    {
        _ = hand; // Hand does not alter forward/backward semantics.
        return Math.Clamp(rawMouseHorizontal, -MaximumControl, MaximumControl);
    }

    // Backward-compatible name used by existing call sites.
    public static float ToForwardness(float rawMouseHorizontal, HandedSwing hand) =>
        ToHoopControl(rawMouseHorizontal, hand);

    /// <summary>
    /// Converts camera-relative mouse X motion into the shared forward/backward
    /// control convention (+ = forward, - = backward). Right-hand controls are
    /// mirrored from left-hand controls so movement is natural from the active hand:
    /// right hand: mouse left = forward, mouse right = backward;
    /// left hand: mouse right = forward, mouse left = backward.
    /// </summary>
    public static float MouseDeltaToControl(float mouseDeltaX, HandedSwing hand) =>
        hand == HandedSwing.Left ? mouseDeltaX : -mouseDeltaX;

    /// <summary>
    /// Returns a smooth 90-degree quarter-hoop pose. At control 0 the hand is fully
    /// out to its own side. At +/-MaximumControl it reaches the body centerline at
    /// maximum backward/forward depth and stops; the racquet never wraps past
    /// perpendicular to the player.
    /// </summary>
    public static RacquetHoopPose GetPose(float hoopControl, HandedSwing hand)
    {
        float control = Math.Clamp(hoopControl, -MaximumControl, MaximumControl);
        float u = MathF.Abs(control) / MaximumControl; // 0 = hand side, 1 = 90-degree perpendicular limit

        // Positive control is physically forward; ForwardOffset below is -depth,
        // so forward control uses negative depth and backward control positive depth.
        float depthNormalized = -MathF.Sign(control) * MathF.Sin(MathF.PI * 0.5f * u);
        float lateralNormalized = MathF.Cos(MathF.PI * 0.5f * u);
        float handSide = hand == HandedSwing.Left ? 1f : -1f;

        return new RacquetHoopPose(
            control,
            depthNormalized,
            lateralNormalized,
            handSide * MaximumLateralReach * lateralNormalized,
            -depthNormalized * MaximumDepthReach);
    }

    /// <summary>
    /// Converts the racquet's actual quarter-circle forward/back pose into the
    /// shot-timing convention used by TennisPhysics: +1 = maximally early/forward,
    /// 0 = neutral, -1 = maximally late/backward. This is based on the physical
    /// rotated pose (DepthNormalized), not on the displayed face angle and not on
    /// a linear mouse/control percentage.
    /// </summary>
    public static float TimingEffectFromForwardBack(float hoopControl, HandedSwing hand)
    {
        RacquetHoopPose pose = GetPose(hoopControl, hand);
        return Math.Clamp(-pose.DepthNormalized, -1f, 1f);
    }

    public static bool IsBehind(float hoopControl, float deadZone = 0.10f) =>
        GetPose(hoopControl, HandedSwing.Right).ForwardOffset < -deadZone;

    public static bool IsInFront(float hoopControl, float deadZone = 0.10f) =>
        GetPose(hoopControl, HandedSwing.Right).ForwardOffset > deadZone;
}

public readonly record struct RacquetHoopPose(
    float Control,
    float DepthNormalized,
    float LateralNormalized,
    float LateralOffset,
    float ForwardOffset);
