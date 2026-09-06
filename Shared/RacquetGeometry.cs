using System.Numerics;

namespace Tennis3D.Shared;

/// <summary>
/// Single source of truth for racquet visual proportions and authoritative contact geometry.
/// The renderer consumes the visual constants; physics and AI consume the same dimensions
/// to prevent visual/collision drift after future tuning.
/// </summary>
public static class RacquetGeometry
{
    // v2.1 visual size: exactly half of the previous 2x racquet dimensions.
    public const float VisualScale = 1.0f;
    public const float HandleLengthBase = 0.50f;
    public const float HandleGapBase = 0.08f;
    public const float ThroatLengthBase = 0.30f;
    public const float HoopHalfWidthBase = 0.34f;
    public const float HoopHalfLengthBase = 0.49f;

    // v2.1.1 hitbox: keep the v2.1 oval shape but enlarge ONLY the collision
    // volume by 2.5x on every axis. Visual racquet dimensions are unchanged.
    // Base v2.1 full long diameter = 1.75x visual length.
    // Base v2.1 short diameters = 2.00x visual hoop width.
    public const float HitboxLongMultiplier = 1.75f;
    public const float HitboxShortMultiplier = 2.00f;
    public const float HitboxCollisionScale = 2.50f;

    public static float HandleLength => HandleLengthBase * VisualScale;
    public static float HandleGap => HandleGapBase * VisualScale;
    public static float ThroatLength => ThroatLengthBase * VisualScale;
    public static float HoopHalfWidth => HoopHalfWidthBase * VisualScale;
    public static float HoopHalfLength => HoopHalfLengthBase * VisualScale;
    public static float VisualLongLength => HandleLength + HandleGap + ThroatLength + 2f * HoopHalfLength;
    public static float VisualShortWidth => 2f * HoopHalfWidth;
    public static float HitboxLongRadius => VisualLongLength * HitboxLongMultiplier * HitboxCollisionScale * 0.5f;
    public static float HitboxShortRadius => VisualShortWidth * HitboxShortMultiplier * HitboxCollisionScale * 0.5f;

    public static RacquetHitboxPose GetHitbox(PlayerState player, InputPacket input)
    {
        Vector3 forward = player.Id == 0 ? Vector3.UnitZ : -Vector3.UnitZ;
        Vector3 right = player.Id == 0 ? Vector3.UnitX : -Vector3.UnitX;
        HandedSwing swingHand = input.Hand == HandedSwing.None ? player.SwingHand : input.Hand;
        float handSide = swingHand == HandedSwing.Left ? 1f : -1f;

        Vector3 shoulder = player.Position + right * (0.31f * handSide) + new Vector3(0f, 1.38f, 0f);
        ShotStyle style = ShotControls.DecodeStyle(input.AimYaw);
        float progress = input.SwingReleased
            ? 1f
            : Math.Clamp(player.SwingProgress / SwingAnimation.DurationSeconds, 0f, 1f);
        SwingAnimationPose animationPose = SwingAnimation.GetPose(progress, style, input.SwingStartHeight);

        Vector3 armDirection = Vector3.Normalize(
            right * (handSide * animationPose.LateralFactor) +
            forward * animationPose.ForwardFactor);
        float swingHeight = animationPose.Height;
        Vector3 wrist = shoulder + armDirection * 0.62f + new Vector3(0f, swingHeight - 1.05f, 0f);

        float heightT = SmoothStep(Math.Clamp((swingHeight - 0.35f) / 2.0f, 0f, 1f));
        Vector3 horizontalShaft = new Vector3(armDirection.X, 0f, armDirection.Z);
        if (horizontalShaft.LengthSquared() < 0.0001f) horizontalShaft = right;
        horizontalShaft = Vector3.Normalize(horizontalShaft);
        Vector3 raisedShaft = Vector3.Normalize(Vector3.UnitY * 0.985f + horizontalShaft * 0.17f);
        Vector3 shaft = Vector3.Normalize(Vector3.Lerp(horizontalShaft, raisedShaft, heightT));

        Vector3 baseNormal = forward - shaft * Vector3.Dot(forward, shaft);
        if (baseNormal.LengthSquared() < 0.0001f) baseNormal = Vector3.Cross(shaft, right);
        baseNormal = Vector3.Normalize(baseNormal);
        if (Vector3.Dot(baseNormal, forward) < 0f) baseNormal = -baseNormal;
        float faceAngle = Math.Clamp(input.RacquetFaceAngle, -0.872665f, 0.872665f);
        float geometricRoll = swingHand == HandedSwing.Left ? -faceAngle : faceAngle;
        Vector3 faceNormal = RotateAroundAxis(baseNormal, shaft, geometricRoll);
        Vector3 across = Vector3.Normalize(Vector3.Cross(shaft, faceNormal));
        if (Vector3.Dot(across, right) < 0f) across = -across;
        faceNormal = Vector3.Normalize(Vector3.Cross(across, shaft));

        Vector3 handleEnd = wrist + shaft * HandleLength;
        Vector3 throatBase = handleEnd + shaft * HandleGap;
        Vector3 hoopBottomCenter = throatBase + shaft * ThroatLength;
        Vector3 hoopCenter = hoopBottomCenter + shaft * HoopHalfLength;
        Vector3 stringTip = hoopCenter + shaft * HoopHalfLength;

        // Center the oval on the actual visible racquet span rather than an arbitrary
        // body point. This keeps the long dimension evenly distributed around the
        // handle/frame while the short axes stay centered on the string plane.
        Vector3 center = (wrist + stringTip) * 0.5f;
        return new RacquetHitboxPose(center, shaft, across, faceNormal,
            HitboxLongRadius, HitboxShortRadius, HitboxShortRadius);
    }

    private static float SmoothStep(float t) => t * t * (3f - 2f * t);

    private static Vector3 RotateAroundAxis(Vector3 value, Vector3 axis, float angle)
    {
        axis = Vector3.Normalize(axis);
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        return value * c + Vector3.Cross(axis, value) * s + axis * Vector3.Dot(axis, value) * (1f - c);
    }
}

public readonly record struct RacquetHitboxPose(
    Vector3 Center,
    Vector3 LongAxis,
    Vector3 WidthAxis,
    Vector3 DepthAxis,
    float LongRadius,
    float WidthRadius,
    float DepthRadius);
