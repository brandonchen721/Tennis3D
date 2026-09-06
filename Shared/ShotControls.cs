namespace Tennis3D.Shared;

public enum ShotStyle
{
    Forehand = 0,
    Slice = 1
}

/// <summary>
/// Shot-selection values are carried in the already-existing AimYaw/AimPitch input
/// slots, so this feature does not change the packet shape or protocol version.
/// </summary>
public static class ShotControls
{
    public static float EncodeStyle(ShotStyle style) => style == ShotStyle.Slice ? 1f : 0f;

    public static ShotStyle DecodeStyle(float encoded) =>
        encoded >= 0.5f ? ShotStyle.Slice : ShotStyle.Forehand;

    public static float DecodePower(float encoded) => Math.Clamp(encoded, 0.00f, 1.00f);

    // Reuse the existing FacingYaw state field as a visual-only shot-style marker so
    // remote clients can render the same committed animation without changing the
    // network packet shape. Normal facing remains 0/PI; slice adds one full turn.
    public static float EncodeVisualFacingYaw(int playerId, ShotStyle style)
    {
        float baseYaw = playerId == 0 ? 0f : MathF.PI;
        return style == ShotStyle.Slice ? baseYaw + MathF.Tau : baseYaw;
    }

    public static ShotStyle DecodeVisualStyle(float facingYaw) =>
        facingYaw >= MathF.Tau - 0.01f ? ShotStyle.Slice : ShotStyle.Forehand;

    public static float AngleRadians(ShotStyle style, int preset)
    {
        preset = Math.Clamp(preset, 0, 2);
        float degrees = style switch
        {
            ShotStyle.Forehand => preset switch { 0 => -15f, 1 => 0f, _ => 15f },
            ShotStyle.Slice => preset switch { 0 => 15f, 1 => 45f, _ => 60f },
            _ => 0f
        };
        return degrees * MathF.PI / 180f;
    }
}
