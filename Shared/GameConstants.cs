namespace Tennis3D.Shared;

public static class GameConstants
{
    public const int ProtocolVersion = 12;
    public const int Port = 27015;
    // Clients can override this through the CLI host argument or the
    // TENSIS3D_SERVER_HOST environment variable for remote play.
    public const string DefaultServerIp = "localhost";
    public const int MaximumPlayers = 128;
    public const float CourtHalfWidth = 5.485f;
    public const float CourtHalfLength = 11.885f;
    public const float SinglesHalfWidth = 4.115f;
    public const float ServiceLine = 6.40f;
    public const float ServeBaselineClearance = 0.18f;
    public const float ServeSideOffset = 1.65f;
    public const float MinimumTossApex = 1.55f;
    public const float MaximumTossApex = 7.50f;
    public const float MaximumTossSideOffset = 1.65f;
    public const float NetHeightCenter = 0.914f;
    public const float NetHeightPost = 1.07f;
    public const float BallRadius = 0.0335f;
    public const float PlayerRadius = 0.45f;
    public const float PlayerSpeed = 6.4f;
    public const float PlayerJumpSpeed = 5.4f;
    public const float SprintMultiplier = 1.5f;
    public const float Gravity = 9.81f;
    public const float AirDensity = 1.225f;
    public const float BallMass = 0.057f;
    public const float BallArea = 0.00352f;
    public const float DragCoefficient = 0.55f;
    public const float MagnusCoefficient = 0.00042f;
    public const float GroundRestitution = 0.73f;
    public const float GroundFriction = 0.18f;
    public const float NetThickness = 0.08f;
    public const float FixedDt = 1f / 120f;
    public const float StateSendInterval = 1f / 30f;
}
