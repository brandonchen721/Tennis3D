namespace Tennis3D.Shared;

/// <summary>
/// Shared timing rules for the hold-to-charge shot meter. The meter rises from
/// 0% to 100% in 0.5 seconds, then stays at 100% until the player releases and
/// commits the swing. A new swing starts a fresh charge from 0%.
/// </summary>
public static class PowerMeter
{
    public const float ChargeSeconds = 0.50f;

    public static float ValueAt(float elapsedSeconds)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0f) return 0f;
        return Math.Clamp(elapsedSeconds / ChargeSeconds, 0f, 1f);
    }

    /// <summary>
    /// During charging, 0% holds the racquet perpendicular/out to the player's side;
    /// 100% reaches the existing maximum backswing depth.
    /// </summary>
    public static float PreparationControl(float power) =>
        -RacquetControls.MaximumDepthAt * Math.Clamp(power, 0f, 1f);

    /// <summary>
    /// Visual-only charge backswing. The animation starts from the forward/back pose
    /// present when charging began, then moves backward by up to one full 90-degree
    /// control range as power rises from 0 to 100%. The result is clamped at the same
    /// maximum-backward perpendicular endpoint used by RacquetControls.
    ///
    /// Examples at 100%: fully forward -> neutral; neutral -> fully backward;
    /// already backward -> reaches the backward clamp sooner and never wraps past it.
    /// </summary>
    public static float ChargeAnimationControl(float startingForwardness, float power)
    {
        float start = Math.Clamp(startingForwardness, -RacquetControls.MaximumControl, RacquetControls.MaximumControl);
        float charge = Math.Clamp(power, 0f, 1f);
        return Math.Clamp(
            start - RacquetControls.MaximumControl * charge,
            -RacquetControls.MaximumControl,
            RacquetControls.MaximumControl);
    }
}
