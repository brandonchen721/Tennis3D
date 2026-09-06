using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Input;
using Tennis3D.Shared;
using N = System.Numerics;

namespace Tennis3D.Client;

public sealed class TennisGame : Game
{
    private enum PregameMode { ChooseTension, ChooseOpponent, ChooseDifficulty, Connecting }
    private enum AiDifficultyChoice { Easy, Medium, Hard, Impossible, SuperImpossible }

    private readonly GraphicsDeviceManager graphics;
    private NetworkClient net = null!;
    private PrimitiveRenderer draw = null!;
    private HudRenderer hud = null!;
    private KeyboardState oldK;
    private MouseState oldM;
    private long seq;
    private HandedSwing hand = HandedSwing.Right;
    private bool swing;
    private bool racquetBehind = true;
    private float swingHeight = 1.05f;
    private float racquetHorizontal;
    private float racquetRollAngle;
    private int previousScrollWheel;
    private ShotStyle selectedShotStyle = ShotStyle.Forehand; // Last committed/preview shot style.
    private float lastShotPower = 0.50f;
    private bool powerMeterActive;
    private bool powerMeterSawBothButtons;
    private bool powerMeterRequireReset;
    private float powerMeterElapsed;
    private float powerMeterValue;
    private float chargeAnimationStartForwardness;
    private float peakMouseSpeed;
    private float swingAnimationRemaining;
    private bool swingAnimationWasActive;
    private ShotStyle swingAnimationStyle = ShotStyle.Forehand;
    private float swingAnimationAngle;
    private float swingAnimationPower = 0.50f;
    private float swingAnimationStartHeight = 1.05f;
    private float swingAnimationStartHorizontal;
    private HandedSwing swingAnimationHand = HandedSwing.Right;
    private bool mouseWasUnlocked;
    private bool waitForServeClickRelease;
    private float serveTossPreviewHeight = 3.0f;
    private float serveTossPreviewX;
    private float serveTossGuideScreenX;
    private float serveTossGuideScreenY;
    private Rectangle serveTossTargetBox;
    private Rectangle serveLandingLegalBox;
    private const float VisualBallRadius = GameConstants.BallRadius * 3.25f;
    private string host = "auto";
    private string name = "Player";
    private PregameMode pregameMode = PregameMode.ChooseTension;
    private AiDifficultyChoice selectedAiDifficulty = AiDifficultyChoice.Medium;
    private bool aiModeSelected;
    private Rectangle vsHumanButton;
    private Rectangle vsAiButton;
    private Rectangle easyButton;
    private Rectangle mediumButton;
    private Rectangle hardButton;
    private Rectangle impossibleButton;
    private Rectangle superImpossibleButton;
    private Rectangle tensionTrack;
    private Rectangle tensionContinueButton;
    private float selectedStringTension = StringTension.Default;
    private bool draggingTension;
    private string pointResultOverlay = "";
    private string pointWinnerOverlay = "";
    private float pointResultOverlayRemaining;
    private MatchPhase lastObservedPhase = MatchPhase.Waiting;
    private const float PointResultOverlayDuration = 1.50f;

    private sealed class SparkleParticle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
        public float MaxLife;
    }

    private readonly List<SparkleParticle> superSparkles = new();
    private readonly Random sparkleRandom = new();
    private float superSparkleEmitAccumulator;
    private int lastSparkleBallHitBy = -1;
    private long lastMinionExplosionSequence;

    // Client-only net deformation. The server remains authoritative for collision and
    // scoring; the client infers the impact from successive authoritative snapshots and
    // animates a damped mesh ripple at the observed contact location.
    private long lastNetVisualTick = -1;
    private bool hasPreviousNetBallSample;
    private N.Vector3 previousNetBallVelocity;
    private bool previousNetBallInPlay;
    private float netRippleAge = 10f;
    private float netRippleAmplitude;
    private float netRippleCenterX;
    private float netRippleCenterY;
    private float netRippleDirectionZ = 1f;

    // Event-driven tennis audio. These are deliberately client-side only: the server
    // remains authoritative for physics/scoring, while each client turns observed
    // authoritative state transitions into sound exactly once.
    private SoundEffect? racquetHitSound;
    private SoundEffect? courtBounceSound;
    private SoundEffect? outErrorSound;
    private int lastAudioHitBy = -1;
    private int lastAudioBounceCount;
    private float strongestRecentDownwardSpeed;
    private string lastAudioMessage = "";
    private MatchPhase lastAudioPhase = MatchPhase.Waiting;

    public TennisGame(string[] args)
    {
        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1440,
            PreferredBackBufferHeight = 900,
            SynchronizeWithVerticalRetrace = true,
            PreferMultiSampling = true
        };
        graphics.GraphicsProfile = GraphicsProfile.HiDef;
        IsMouseVisible = false;
        Window.AllowUserResizing = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60.0);
        if (args.Length > 0) host = args[0];
        if (args.Length > 1) name = args[1];
    }

    protected override void Initialize()
    {
        graphics.ApplyChanges();
        draw = new PrimitiveRenderer(GraphicsDevice);
        hud = new HudRenderer(GraphicsDevice);
        IsMouseVisible = true;
        previousScrollWheel = Mouse.GetState().ScrollWheelValue;
        base.Initialize();
    }

    protected override void LoadContent()
    {
        base.LoadContent();
        LoadGameSounds();
    }

    private void LoadGameSounds()
    {
        racquetHitSound = LoadSoundAsset("tennis_hit.wav");
        courtBounceSound = LoadSoundAsset("court_bounce.wav");
        outErrorSound = LoadSoundAsset("out_error.wav");
    }

    private static SoundEffect? LoadSoundAsset(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(path)) return null;
        using FileStream stream = File.OpenRead(path);
        return SoundEffect.FromStream(stream);
    }

    protected override void UnloadContent()
    {
        racquetHitSound?.Dispose();
        courtBounceSound?.Dispose();
        outErrorSound?.Dispose();
        base.UnloadContent();
    }

    protected override void Update(GameTime gt)
    {
        KeyboardState k = Keyboard.GetState();
        MouseState m = Mouse.GetState();
        if (k.IsKeyDown(Keys.Escape)) Exit();

        // F11 toggles fullscreen using MonoGame's built-in display-mode switch.
        // Edge-triggered so holding the key cannot rapidly toggle modes.
        if (k.IsKeyDown(Keys.F11) && oldK.IsKeyUp(Keys.F11))
            graphics.ToggleFullScreen();

        if (net is null)
        {
            UpdatePregameMenu(k, m);
            oldK = k;
            oldM = m;
            base.Update(gt);
            return;
        }

        WorldState? w = net.Latest;
        float uiFrameSeconds = Math.Max(0.001f, (float)gt.ElapsedGameTime.TotalSeconds);
        if (pointResultOverlayRemaining > 0f)
            pointResultOverlayRemaining = Math.Max(0f, pointResultOverlayRemaining - uiFrameSeconds);
        if (w is not null)
        {
            bool pointJustEnded = (w.Phase == MatchPhase.PointOver || w.Phase == MatchPhase.MatchOver) &&
                                  lastObservedPhase != MatchPhase.PointOver &&
                                  lastObservedPhase != MatchPhase.MatchOver;
            if (pointJustEnded)
            {
                pointResultOverlay = PointResultText(w.Message);
                pointWinnerOverlay = PointWinnerText(w, net.PlayerId);
                pointResultOverlayRemaining = PointResultOverlayDuration;
            }
            UpdateGameAudio(w, pointJustEnded);
            UpdateNetRipple(uiFrameSeconds, w);
            lastObservedPhase = w.Phase;
        }
        else
        {
            UpdateNetRipple(uiFrameSeconds, null);
        }

        bool selectingServeToss = w is not null &&
                                  w.Phase == MatchPhase.ServeSetup &&
                                  w.Score.Server == net.PlayerId &&
                                  net.PlayerId >= 0;

        IsMouseVisible = selectingServeToss;
        bool serveTossRequested = false;
        float serveTossHeight = 3.0f;
        float serveTossX = 0f;
        float dx = 0f;
        float dy = 0f;

        if (selectingServeToss)
        {
            if (!mouseWasUnlocked)
            {
                Mouse.SetPosition(Window.ClientBounds.Width / 2, Window.ClientBounds.Height / 2);
                m = Mouse.GetState();
            }

            // Top-down serve placement selector. Cursor position is constrained to the
            // legal diagonal service box. X is sent directly; the target Z distance is
            // encoded into the existing height field so the network protocol is unchanged.
            Vector2 target = CalculateServeLandingTarget(w!, m.X, m.Y);
            serveTossPreviewX = target.X;
            serveTossPreviewHeight = EncodeServeTargetZ(MathF.Abs(target.Y));
            bool clicked = m.LeftButton == ButtonState.Pressed && oldM.LeftButton == ButtonState.Released;
            if (clicked)
            {
                serveTossHeight = serveTossPreviewHeight;
                serveTossX = serveTossPreviewX;
                serveTossRequested = true;
                waitForServeClickRelease = true;
            }
        }
        else
        {
            int cx = Window.ClientBounds.Width / 2;
            int cy = Window.ClientBounds.Height / 2;
            dx = m.X - cx;
            dy = m.Y - cy;

            // After the power-button release, the racquet is committed to its 0.5-second stroke.
            // Mouse motion is ignored for racquet positioning until that animation ends.
            if (swingAnimationRemaining <= 0f)
            {
                // Mouse X always owns the forward/back preparation pose, including while
                // the power button is held. Power controls shot strength only; it must not
                // silently replace the visible/authoritative racquet orientation. Shared
                // control uses + = forward/early and - = backward/late:
                // right hand: mouse left = forward / mouse right = backward;
                // left hand:  mouse right = forward / mouse left = backward.
                // Resolve a same-frame A/D hand change before applying mouse motion so
                // switching hands cannot produce one frame of reversed preparation.
                HandedSwing mouseControlHand = hand;
                bool aDownForHand = k.IsKeyDown(Keys.A);
                bool dDownForHand = k.IsKeyDown(Keys.D);
                if (aDownForHand && !dDownForHand) mouseControlHand = HandedSwing.Left;
                else if (dDownForHand && !aDownForHand) mouseControlHand = HandedSwing.Right;

                float controlDelta = RacquetControls.MouseDeltaToControl(dx, mouseControlHand);
                racquetHorizontal = Math.Clamp(
                    racquetHorizontal + controlDelta * 0.0065f,
                    -RacquetControls.MaximumControl,
                    RacquetControls.MaximumControl);
                swingHeight = Math.Clamp(swingHeight - dy * 0.009f, 0.35f, 2.35f);
            }

            // The mouse wheel now controls racquet face angle continuously.
            // Scroll UP closes the face (degrees decrease); scroll DOWN opens it.
            // Five degrees per notch keeps the full -50..+50 range quick to access.
            int scrollDelta = m.ScrollWheelValue - previousScrollWheel;
            if (scrollDelta != 0)
            {
                int wheelSteps = Math.Max(1, Math.Abs(scrollDelta) / 120);
                float degrees = MathHelper.ToDegrees(racquetRollAngle);
                degrees += Math.Sign(scrollDelta) * -5f * wheelSteps;
                racquetRollAngle = MathHelper.ToRadians(Math.Clamp(degrees, -50f, 50f));
            }

            CenterMouse();
        }

        mouseWasUnlocked = selectingServeToss;
        previousScrollWheel = m.ScrollWheelValue;

        // Player-relative controls. Both players use the same keys from their own view:
        // W = toward the net, S = away, A = screen/player left, D = screen/player right.
        float localRight = (k.IsKeyDown(Keys.A) ? 1f : 0f) - (k.IsKeyDown(Keys.D) ? 1f : 0f);
        float localForward = (k.IsKeyDown(Keys.W) ? 1f : 0f) - (k.IsKeyDown(Keys.S) ? 1f : 0f);
        bool farSidePlayer = net.PlayerId == 1;
        // Screen/player-relative horizontal movement. The near and far cameras face
        // opposite court directions, so their world-X mapping must be mirrored.
        // A is always player/camera-left; D is always player/camera-right.
        float moveX = farSidePlayer ? -localRight : localRight;
        float moveZ = farSidePlayer ? -localForward : localForward;
        bool sprintHeld = k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift);
        bool jumpRequested = k.IsKeyDown(Keys.Space) && oldK.IsKeyUp(Keys.Space);
        if (sprintHeld)
        {
            moveX *= GameConstants.SprintMultiplier;
            moveZ *= GameConstants.SprintMultiplier;
        }

        if (localRight > 0) hand = HandedSwing.Left;
        else if (localRight < 0) hand = HandedSwing.Right;

        // Convert raw camera-relative mouse X into shared racquet forwardness.
        // Forward/backward meaning is identical for both hands; only lateral placement mirrors.
        float racquetForwardness = RacquetControls.ToForwardness(racquetHorizontal, hand);
        if (RacquetControls.IsBehind(racquetForwardness)) racquetBehind = true;
        else if (RacquetControls.IsInFront(racquetForwardness)) racquetBehind = false;

        if (waitForServeClickRelease && !selectingServeToss && m.LeftButton == ButtonState.Released)
            waitForServeClickRelease = false;

        float frameSeconds = uiFrameSeconds;
        bool leftDown = m.LeftButton == ButtonState.Pressed;
        bool rightDown = m.RightButton == ButtonState.Pressed;
        bool leftPressed = leftDown && oldM.LeftButton == ButtonState.Released;
        bool rightPressed = rightDown && oldM.RightButton == ButtonState.Released;
        bool leftReleased = !leftDown && oldM.LeftButton == ButtonState.Pressed;
        bool rightReleased = !rightDown && oldM.RightButton == ButtonState.Pressed;

        // After a dual-button cancellation, both buttons must be physically released
        // before a fresh power-meter hold can begin. This prevents the still-held
        // button from immediately starting an unintended shot.
        if (powerMeterRequireReset && !leftDown && !rightDown)
            powerMeterRequireReset = false;

        if (!selectingServeToss && !waitForServeClickRelease &&
            swingAnimationRemaining <= 0f && !powerMeterActive && !powerMeterRequireReset &&
            (leftPressed || rightPressed))
        {
            powerMeterActive = true;
            powerMeterSawBothButtons = leftDown && rightDown;
            powerMeterElapsed = 0f;
            powerMeterValue = 0f;
            // Animation-only snapshot: the visual charge backswing starts from the
            // exact forward/back pose that existed when charging began. This value
            // never replaces racquetHorizontal and is never sent to shot physics.
            chargeAnimationStartForwardness = racquetForwardness;
            peakMouseSpeed = 0f;
        }

        if (powerMeterActive)
        {
            if (leftDown && rightDown)
                powerMeterSawBothButtons = true;

            // Charge once: 0 -> 100% in 0.5 s, then hold at 100% until release.
            // A new swing is required before the meter can recharge from 0%.
            powerMeterElapsed += frameSeconds;
            powerMeterValue = PowerMeter.ValueAt(powerMeterElapsed);

            // If both buttons were held at any time, releasing either one cancels the
            // meter exactly as requested. The player must release both and re-hold.
            if (powerMeterSawBothButtons && (leftReleased || rightReleased))
            {
                powerMeterActive = false;
                powerMeterRequireReset = true;
                powerMeterValue = 0f;
            }
            else if (!powerMeterSawBothButtons && (leftReleased || rightReleased))
            {
                ShotStyle releasedStyle = leftReleased ? ShotStyle.Forehand : ShotStyle.Slice;
                selectedShotStyle = releasedStyle;
                lastShotPower = powerMeterValue;

                // Releasing the held button commits one 0.5-second stroke.
                swingAnimationRemaining = SwingAnimation.DurationSeconds;
                swingAnimationStyle = releasedStyle;
                swingAnimationAngle = racquetRollAngle;
                swingAnimationPower = powerMeterValue;
                swingAnimationStartHeight = swingHeight;
                swingAnimationStartHorizontal = racquetHorizontal;
                swingAnimationHand = hand;
                powerMeterActive = false;
                peakMouseSpeed = 0f;
            }
        }

        bool animationActive = swingAnimationRemaining > 0f;
        float mouseSpeed = MathF.Sqrt(dx * dx + dy * dy) / frameSeconds;
        float mouseVerticalSpeed = -dy / frameSeconds;
        if (animationActive)
        {
            peakMouseSpeed = Math.Max(peakMouseSpeed, mouseSpeed);
            swingAnimationRemaining = Math.Max(0f, swingAnimationRemaining - frameSeconds);
        }
        bool held = animationActive;
        bool released = swingAnimationWasActive && !animationActive;
        swingAnimationWasActive = animationActive;
        swing = animationActive;
        if (released)
        {
            mouseSpeed = Math.Max(mouseSpeed, peakMouseSpeed);
            peakMouseSpeed = 0f;
        }

        // Freeze the shot definition for the entire committed animation.
        ShotStyle packetStyle = animationActive || released ? swingAnimationStyle : selectedShotStyle;
        float packetPower = animationActive || released ? swingAnimationPower : lastShotPower;
        float packetAngle = animationActive || released ? swingAnimationAngle : racquetRollAngle;
        float packetHeight = animationActive || released ? swingAnimationStartHeight : swingHeight;
        float packetHorizontal;
        if (animationActive || released)
            packetHorizontal = RacquetControls.ToForwardness(swingAnimationStartHorizontal, swingAnimationHand);
        else
            // Mouse-selected forward/back pose remains authoritative while charging.
            // This keeps visible orientation and trajectory timing on the same sign.
            packetHorizontal = racquetForwardness;
        HandedSwing packetHand = animationActive || released ? swingAnimationHand : hand;

        // Strict handshake ordering: never stream gameplay packets until the
        // server has accepted this client with a Welcome packet. This makes
        // reconnects deterministic and prevents stale/unknown endpoints from
        // flooding Input before Hello is processed.
        if (net.IsAccepted)
        {
            net.Send(PacketKind.Input, new InputPacket(
                ++seq, moveX, moveZ, ShotControls.EncodeStyle(packetStyle), packetPower, held, released,
                packetHand, packetHeight, racquetBehind, packetHorizontal,
                packetAngle, mouseSpeed, mouseVerticalSpeed,
                serveTossRequested, serveTossHeight, serveTossX, jumpRequested));
        }

        // Keep the operating-system window/title-bar text deliberately minimal.
        // Gameplay status, controls, power, shot mode, scoring, and connection
        // details belong in the in-game HUD instead of the window chrome.
        Window.Title = "Tennis 3D";

        UpdateSuperImpossibleSparkles(uiFrameSeconds, net.Latest);

        oldK = k;
        oldM = m;
        base.Update(gt);
    }

    private static bool Pressed(KeyboardState current, KeyboardState previous, Keys primary, Keys keypad) =>
        (current.IsKeyDown(primary) && previous.IsKeyUp(primary)) ||
        (current.IsKeyDown(keypad) && previous.IsKeyUp(keypad));

    private Vector2 CalculateServeLandingTarget(WorldState world, int mouseWindowX, int mouseWindowY)
    {
        int viewportWidth = GraphicsDevice.Viewport.Width;
        int viewportHeight = GraphicsDevice.Viewport.Height;
        int diagramHeight = Math.Clamp((int)(viewportHeight * 0.70f), 430, 690);
        int diagramWidth = Math.Clamp((int)(diagramHeight * (GameConstants.CourtHalfWidth / GameConstants.CourtHalfLength)), 260, 390);
        serveTossTargetBox = new Rectangle((viewportWidth - diagramWidth) / 2, (viewportHeight - diagramHeight) / 2, diagramWidth, diagramHeight);

        bool playerNearSide = net.PlayerId == 0;
        bool deuceCourt = (world.Score.Points[0] + world.Score.Points[1]) % 2 == 0;

        // Player-relative horizontal basis for the temporary serve map.
        // Screen-left is always the serving player's left-hand side and
        // screen-right is always the serving player's right-hand side.
        float serverRightSign = playerNearSide ? 1f : -1f;
        float serveFromXSign = serverRightSign * (deuceCourt ? 1f : -1f);
        int targetXSign = serveFromXSign > 0f ? -1 : 1;

        float WorldXToPlayerRight(float worldX) => worldX * serverRightSign;
        float PlayerRightToWorldX(float playerRightX) => playerRightX * serverRightSign;

        Vector2 WorldToDiagram(float worldX, float worldZ)
        {
            float playerRightX = WorldXToPlayerRight(worldX);
            float u = 1f - ((playerRightX + GameConstants.CourtHalfWidth) /
                           (GameConstants.CourtHalfWidth * 2f));
            float v = playerNearSide
                ? (GameConstants.CourtHalfLength - worldZ) / (GameConstants.CourtHalfLength * 2f)
                : (worldZ + GameConstants.CourtHalfLength) / (GameConstants.CourtHalfLength * 2f);
            return new Vector2(serveTossTargetBox.Left + u * serveTossTargetBox.Width,
                               serveTossTargetBox.Top + v * serveTossTargetBox.Height);
        }

        float x0 = targetXSign < 0 ? -GameConstants.SinglesHalfWidth : 0f;
        float x1 = targetXSign < 0 ? 0f : GameConstants.SinglesHalfWidth;
        float z0 = playerNearSide ? 0f : -GameConstants.ServiceLine;
        float z1 = playerNearSide ? GameConstants.ServiceLine : 0f;
        Vector2 a = WorldToDiagram(x0, z0);
        Vector2 b = WorldToDiagram(x1, z1);
        int left = (int)MathF.Round(MathF.Min(a.X, b.X));
        int right = (int)MathF.Round(MathF.Max(a.X, b.X));
        int top = (int)MathF.Round(MathF.Min(a.Y, b.Y));
        int bottom = (int)MathF.Round(MathF.Max(a.Y, b.Y));
        serveLandingLegalBox = new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));

        float mouseViewportX = mouseWindowX * viewportWidth / (float)Math.Max(1, Window.ClientBounds.Width);
        float mouseViewportY = mouseWindowY * viewportHeight / (float)Math.Max(1, Window.ClientBounds.Height);
        serveTossGuideScreenX = Math.Clamp(mouseViewportX, serveLandingLegalBox.Left + 2, serveLandingLegalBox.Right - 2);
        serveTossGuideScreenY = Math.Clamp(mouseViewportY, serveLandingLegalBox.Top + 2, serveLandingLegalBox.Bottom - 2);

        float uSelected = 1f - ((serveTossGuideScreenX - serveTossTargetBox.Left) / Math.Max(1f, serveTossTargetBox.Width));
        float vSelected = (serveTossGuideScreenY - serveTossTargetBox.Top) / Math.Max(1f, serveTossTargetBox.Height);
        float selectedPlayerRightX = MathHelper.Lerp(
            -GameConstants.CourtHalfWidth,
            GameConstants.CourtHalfWidth,
            uSelected);
        float worldX = PlayerRightToWorldX(selectedPlayerRightX);
        float worldZ = playerNearSide
            ? MathHelper.Lerp(GameConstants.CourtHalfLength, -GameConstants.CourtHalfLength, vSelected)
            : MathHelper.Lerp(-GameConstants.CourtHalfLength, GameConstants.CourtHalfLength, vSelected);

        worldX = Math.Clamp(worldX, MathF.Min(x0, x1) + 0.03f, MathF.Max(x0, x1) - 0.03f);
        worldZ = Math.Clamp(worldZ, MathF.Min(z0, z1) + 0.03f, MathF.Max(z0, z1) - 0.03f);
        return new Vector2(worldX, worldZ);
    }

    private void UpdatePregameMenu(KeyboardState keyboard, MouseState mouse)
    {
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        int buttonWidth = Math.Clamp(width / 4, 250, 360);
        int buttonHeight = 72;
        int centerX = width / 2;
        int top = height / 2 - 75;
        vsHumanButton = new Rectangle(centerX - buttonWidth - 14, top, buttonWidth, buttonHeight);
        vsAiButton = new Rectangle(centerX + 14, top, buttonWidth, buttonHeight);

        int diffWidth = Math.Clamp(width / 7, 150, 220);
        int diffGap = 14;
        int total = diffWidth * 5 + diffGap * 4;
        int diffX = centerX - total / 2;
        int diffY = height / 2 - 20;
        easyButton = new Rectangle(diffX, diffY, diffWidth, buttonHeight);
        mediumButton = new Rectangle(diffX + diffWidth + diffGap, diffY, diffWidth, buttonHeight);
        hardButton = new Rectangle(diffX + (diffWidth + diffGap) * 2, diffY, diffWidth, buttonHeight);
        impossibleButton = new Rectangle(diffX + (diffWidth + diffGap) * 3, diffY, diffWidth, buttonHeight);
        superImpossibleButton = new Rectangle(diffX + (diffWidth + diffGap) * 4, diffY, diffWidth, buttonHeight);

        int trackWidth = Math.Clamp(width / 2, 420, 680);
        tensionTrack = new Rectangle(centerX - trackWidth / 2, height / 2 - 5, trackWidth, 18);
        tensionContinueButton = new Rectangle(centerX - 150, height / 2 + 92, 300, 64);

        bool leftDown = mouse.LeftButton == ButtonState.Pressed;
        bool clicked = leftDown && oldM.LeftButton == ButtonState.Released;
        bool released = !leftDown && oldM.LeftButton == ButtonState.Pressed;
        Point point = new(mouse.X, mouse.Y);

        if (pregameMode == PregameMode.ChooseTension)
        {
            Rectangle expandedTrack = new(tensionTrack.X - 12, tensionTrack.Y - 20, tensionTrack.Width + 24, tensionTrack.Height + 40);
            if (clicked && expandedTrack.Contains(point)) draggingTension = true;
            if (released) draggingTension = false;
            if (draggingTension)
            {
                float t = Math.Clamp((mouse.X - tensionTrack.X) / (float)Math.Max(1, tensionTrack.Width), 0f, 1f);
                float raw = StringTension.Minimum + t * (StringTension.Maximum - StringTension.Minimum);
                selectedStringTension = MathF.Round(raw * 100f) / 100f;
            }
            if (clicked && tensionContinueButton.Contains(point))
            {
                draggingTension = false;
                pregameMode = PregameMode.ChooseOpponent;
            }
            return;
        }

        if (!clicked) return;

        if (pregameMode == PregameMode.ChooseOpponent)
        {
            if (vsHumanButton.Contains(point))
            {
                aiModeSelected = false;
                StartNetwork(ComposeNetworkName(name, null));
            }
            else if (vsAiButton.Contains(point))
            {
                aiModeSelected = true;
                pregameMode = PregameMode.ChooseDifficulty;
            }
        }
        else if (pregameMode == PregameMode.ChooseDifficulty)
        {
            if (easyButton.Contains(point)) selectedAiDifficulty = AiDifficultyChoice.Easy;
            else if (mediumButton.Contains(point)) selectedAiDifficulty = AiDifficultyChoice.Medium;
            else if (hardButton.Contains(point)) selectedAiDifficulty = AiDifficultyChoice.Hard;
            else if (impossibleButton.Contains(point)) selectedAiDifficulty = AiDifficultyChoice.Impossible;
            else if (superImpossibleButton.Contains(point)) selectedAiDifficulty = AiDifficultyChoice.SuperImpossible;
            else return;

            StartNetwork(ComposeNetworkName(name, selectedAiDifficulty));
        }
    }

    private string ComposeNetworkName(string displayName, AiDifficultyChoice? aiDifficulty)
    {
        int tensionPercent = (int)MathF.Round(selectedStringTension * 100f);
        string prefix = $"__TENSION_{tensionPercent:D2}__::";
        if (aiDifficulty is AiDifficultyChoice difficulty)
            prefix += $"__AI_{difficulty.ToString().ToUpperInvariant()}__::";
        return prefix + displayName;
    }

    private void StartNetwork(string networkName)
    {
        if (net is not null) return;
        pregameMode = PregameMode.Connecting;
        net = new NetworkClient(host, GameConstants.Port, networkName);
        IsMouseVisible = false;
        CenterMouse();
        previousScrollWheel = Mouse.GetState().ScrollWheelValue;
    }

    private void DrawPregameMenu()
    {
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        Color panel = new Color(8, 18, 30, 230);
        Color border = new Color(210, 230, 245, 230);
        Color text = new Color(245, 249, 252);
        Color accent = new Color(242, 220, 84);
        Color good = new Color(120, 235, 145);

        hud.Begin();
        string title = "TENNIS 3D";
        int titleScale = width >= 1200 ? 3 : 2;
        hud.Text(title, new Vector2((width - hud.Measure(title, titleScale)) / 2f, Math.Max(70, height * 0.20f)), titleScale, text);

        if (pregameMode == PregameMode.ChooseTension)
        {
            string subtitle = "SET STRING TENSION";
            hud.Text(subtitle, new Vector2((width - hud.Measure(subtitle, 2)) / 2f, height / 2 - 135), 2, accent);
            string explanation = "SET ONCE FOR THIS GAME SESSION";
            hud.Text(explanation, new Vector2((width - hud.Measure(explanation, 1)) / 2f, height / 2 - 92), 1, text);

            hud.Panel(new Rectangle(tensionTrack.X - 2, tensionTrack.Y - 2, tensionTrack.Width + 4, tensionTrack.Height + 4), panel, border);
            float normalized = (selectedStringTension - StringTension.Minimum) / (StringTension.Maximum - StringTension.Minimum);
            int knobX = tensionTrack.X + (int)MathF.Round(normalized * tensionTrack.Width);
            hud.Panel(new Rectangle(knobX - 5, tensionTrack.Y - 9, 10, tensionTrack.Height + 18), accent, border);

            string minLabel = "10% MIN";
            string maxLabel = "70% MAX";
            string currentLabel = $"CURRENT  {selectedStringTension:P0}";
            hud.Text(minLabel, new Vector2(tensionTrack.Left, tensionTrack.Bottom + 14), 1, text);
            hud.Text(maxLabel, new Vector2(tensionTrack.Right - hud.Measure(maxLabel, 1), tensionTrack.Bottom + 14), 1, text);
            hud.Text(currentLabel, new Vector2((width - hud.Measure(currentLabel, 2)) / 2f, tensionTrack.Y - 47), 2, good);
            DrawMenuButton(tensionContinueButton, "LOCK TENSION", "CONTINUE TO OPPONENT", panel, border, text, good);
        }
        else if (pregameMode == PregameMode.ChooseOpponent)
        {
            string subtitle = "CHOOSE OPPONENT";
            hud.Text(subtitle, new Vector2((width - hud.Measure(subtitle, 2)) / 2f, height / 2 - 125), 2, accent);
            DrawMenuButton(vsHumanButton, "VS HUMAN", "ONLINE MULTIPLAYER", panel, border, text, good);
            DrawMenuButton(vsAiButton, "VS AI", "PLAY AGAINST BOT", panel, border, text, accent);
        }
        else if (pregameMode == PregameMode.ChooseDifficulty)
        {
            string subtitle = "SELECT AI DIFFICULTY";
            hud.Text(subtitle, new Vector2((width - hud.Measure(subtitle, 2)) / 2f, height / 2 - 105), 2, accent);
            DrawMenuButton(easyButton, "EASY", "AIMS NEAR YOU", panel, border, text, good);
            DrawMenuButton(mediumButton, "MEDIUM", "BALANCED RETURNS", panel, border, text, accent);
            DrawMenuButton(hardButton, "HARD", "SMART PLACEMENT", panel, border, text, new Color(245, 120, 105));
            DrawMenuButton(impossibleButton, "IMPOSSIBLE", "PERFECT TELEPORT AI", panel, border, text, new Color(255, 90, 90));
            DrawMenuButton(superImpossibleButton, "SUPER IMPOSSIBLE", "AIRBORNE SMASH AI", panel, border, text, new Color(125, 255, 145));
            string hint = "SUPER IMPOSSIBLE: HOVERS, AERIAL SMASHES, DIVINE SPARKLES";
            hud.Text(hint, new Vector2((width - hud.Measure(hint, 1)) / 2f, height / 2 + 78), 1, text);
        }
        hud.Text("F11 FULLSCREEN   ESC QUIT", new Vector2(18, height - 28), 1, text);
        hud.End();
    }

    private void DrawMenuButton(Rectangle rectangle, string title, string subtitle,
        Color fill, Color border, Color text, Color accent)
    {
        MouseState mouse = Mouse.GetState();
        bool hover = rectangle.Contains(mouse.Position);
        hud.Panel(rectangle, hover ? new Color(18, 38, 58, 240) : fill, hover ? accent : border);
        int titleScale = title.Length > 12 ? 1 : 2;
        int titleY = titleScale == 1 ? rectangle.Y + 20 : rectangle.Y + 15;
        hud.Text(title, new Vector2(rectangle.X + 18, titleY), titleScale, hover ? accent : text);
        hud.Text(subtitle, new Vector2(rectangle.X + 18, rectangle.Y + 48), 1, text);
    }

    private void UpdateGameAudio(WorldState world, bool pointJustEnded)
    {
        BallState ball = world.Ball;

        // A new point/serve creates a fresh ball, so reset edge detectors.
        if (world.Phase == MatchPhase.ServeSetup && lastAudioPhase != MatchPhase.ServeSetup)
        {
            lastAudioHitBy = -1;
            lastAudioBounceCount = 0;
            strongestRecentDownwardSpeed = 0f;
        }

        // Track the strongest downward speed observed immediately before contact
        // with the court. Network snapshots may arrive after the rebound, so using
        // the recent pre-impact maximum is more faithful than post-bounce +Y speed.
        if (ball.InPlay && ball.Velocity.Y < 0f)
            strongestRecentDownwardSpeed = MathF.Max(strongestRecentDownwardSpeed, -ball.Velocity.Y);

        // Racquet hit: LastHitBy changes only when the authoritative server accepts
        // a racquet/serve contact. Outgoing ball speed is a stable proxy for power.
        if (ball.InPlay && ball.LastHitBy >= 0 && ball.LastHitBy != lastAudioHitBy)
        {
            float speed = ball.Velocity.Length();
            float volume = Math.Clamp(0.20f + speed / 42f * 0.80f, 0.20f, 1.00f);
            racquetHitSound?.Play(volume, 0f, 0f);
            lastAudioHitBy = ball.LastHitBy;
            strongestRecentDownwardSpeed = 0f;
        }

        // Court bounce: use downward impact speed for loudness. If a snapshot skips
        // the exact falling frame, retain a conservative floor so every bounce is audible.
        if (ball.BounceCount > lastAudioBounceCount)
        {
            float reboundDerivedImpact = ball.Velocity.Y > 0f
                ? ball.Velocity.Y / MathF.Max(0.01f, GameConstants.GroundRestitution)
                : 0f;
            float impactSpeed = MathF.Max(2.5f, MathF.Max(strongestRecentDownwardSpeed, reboundDerivedImpact));
            float volume = Math.Clamp(0.16f + impactSpeed / 18f * 0.84f, 0.16f, 1.00f);
            courtBounceSound?.Play(volume, 0f, 0f);
            lastAudioBounceCount = ball.BounceCount;
            strongestRecentDownwardSpeed = 0f;
        }
        else if (ball.BounceCount < lastAudioBounceCount)
        {
            // A racquet hit/new ball resets BounceCount. Do not mistake that reset
            // for another court contact.
            lastAudioBounceCount = ball.BounceCount;
        }

        // OUT uses its own distinct error cue. Trigger from a new authoritative
        // scoring/fault message, including first-serve faults that do not end a point.
        if (!string.Equals(world.Message, lastAudioMessage, StringComparison.Ordinal))
        {
            string upper = world.Message.ToUpperInvariant();
            bool isOutCall = upper.Contains("LANDED OUT") ||
                             upper.Contains("LEFT THE PLAYABLE AREA") ||
                             (pointJustEnded && PointResultText(world.Message) == "OUT");
            if (isOutCall) outErrorSound?.Play(0.82f, 0f, 0f);
            lastAudioMessage = world.Message;
        }

        lastAudioPhase = world.Phase;
    }

    private static float EncodeServeTargetZ(float absoluteTargetZ)
    {
        float normalized = Math.Clamp(absoluteTargetZ / GameConstants.ServiceLine, 0f, 1f);
        return MathHelper.Lerp(GameConstants.MinimumTossApex, GameConstants.MaximumTossApex, normalized);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(115, 174, 224));
        if (net is null)
        {
            DrawPregameMenu();
            base.Draw(gameTime);
            return;
        }
        WorldState? w = net.Latest;
        PlayerState? me = w?.Players.FirstOrDefault(p => p.Id == net.PlayerId);
        Vector3 pos = me is null ? new Vector3(0, 0, net.PlayerId == 1 ? 9.5f : -9.5f) : V(me.Position);
        Vector3 courtForward = net.PlayerId == 1 ? Vector3.Forward : Vector3.Backward;
        Vector3 cam = pos - courtForward * 6.8f + new Vector3(0, 3.2f, 0);
        Vector3 look = pos + new Vector3(0, 1.25f, 0) + courtForward * 4.8f;
        Matrix view = Matrix.CreateLookAt(cam, look, Vector3.Up);
        Matrix proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(58f), GraphicsDevice.Viewport.AspectRatio, 0.04f, 180f);

        draw.Begin(view, proj);
        DrawEnvironment();
        DrawCourt();

        if (w is null)
        {
            DrawPlayer(DefaultPlayer(0), net.PlayerId == 0);
            DrawPlayer(DefaultPlayer(1), net.PlayerId == 1);
            DrawBall(new Vector3(0, 1.2f, 0));
        }
        else
        {
            foreach (PlayerState p in w.Players)
            {
                PlayerState visual = p.Id == net.PlayerId ? CreateLocalVisual(p) : p;
                DrawPlayer(visual, p.Id == net.PlayerId);
            }
            foreach (MinionState minion in w.Minions)
                DrawMinion(minion);
            // The ball is always rendered, including lobby and serve setup phases.
            DrawBall(V(w.Ball.Position));

            bool selectingServeToss = w.Phase == MatchPhase.ServeSetup &&
                                      w.Score.Server == net.PlayerId && net.PlayerId >= 0;
            // Serve placement is shown as a 2D top-down overlay in DrawHud.
        }
        DrawSuperImpossibleSparkles(view, proj);
        DrawHud(w, selectingServeToss: w is not null && w.Phase == MatchPhase.ServeSetup && w.Score.Server == net.PlayerId && net.PlayerId >= 0);
        base.Draw(gameTime);
    }

    private static bool IsSuperImpossiblePlayer(PlayerState player) =>
        player.Name.Contains("SUPERIMPOSSIBLE", StringComparison.OrdinalIgnoreCase) ||
        player.Name.Contains("SUPER IMPOSSIBLE", StringComparison.OrdinalIgnoreCase);

    private void UpdateSuperImpossibleSparkles(float dt, WorldState? world)
    {
        // Bounded client-only particle simulation. Sparkles never enter protocol or physics.
        for (int i = superSparkles.Count - 1; i >= 0; i--)
        {
            SparkleParticle p = superSparkles[i];
            p.Velocity += Vector3.Down * (GameConstants.Gravity * dt);
            p.Position += p.Velocity * dt;
            p.Life -= dt;
            if (p.Life <= 0f || p.Position.Y < -0.5f) superSparkles.RemoveAt(i);
        }
        if (world is null) return;

        PlayerState? superBot = world.Players.FirstOrDefault(IsSuperImpossiblePlayer);
        if (superBot is null) return;

        // Successful minion contact schedules its death after the committed swing finishes.
        // The server increments this sequence at that animation endpoint; each client turns
        // the event into a radial firework using the same green sparkle particle type as the boss.
        if (world.MinionExplosionSequence != lastMinionExplosionSequence)
        {
            lastMinionExplosionSequence = world.MinionExplosionSequence;
            if (lastMinionExplosionSequence > 0)
                EmitMinionSparkleExplosion(V(world.MinionExplosionPosition));
        }

        superSparkleEmitAccumulator += dt;

        // Divine airborne aura: particles continuously fall from beneath the floating bot.
        if (superBot.Position.Y > 0.05f)
        {
            while (superSparkleEmitAccumulator >= 0.022f)
            {
                superSparkleEmitAccumulator -= 0.022f;
                // Emit from the two rendered feet instead of the torso/body center.
                // The bot may be hovering high above the court, so these local offsets
                // deliberately stay close to its foot endpoints before gravity pulls the
                // particles downward into the divine falling trail.
                float footX = sparkleRandom.Next(2) == 0 ? -0.20f : 0.20f;
                EmitSparkle(V(superBot.Position) + new Vector3(
                    footX + RandomRange(-0.08f, 0.08f), RandomRange(0.02f, 0.13f), RandomRange(-0.06f, 0.10f)),
                    new Vector3(RandomRange(-0.14f, 0.14f), RandomRange(-0.42f, -0.04f), RandomRange(-0.14f, 0.14f)),
                    RandomRange(0.42f, 0.82f));
            }
        }
        else
        {
            superSparkleEmitAccumulator = Math.Min(superSparkleEmitAccumulator, 0.022f);
        }

        // A Super Impossible-struck ball carries its own falling green trail until
        // another player touches it or the point resets.
        bool superBall = world.Ball.InPlay && world.Ball.LastHitBy == superBot.Id;
        if (superBall)
        {
            EmitSparkle(V(world.Ball.Position) + new Vector3(RandomRange(-0.05f, 0.05f), -0.04f, RandomRange(-0.05f, 0.05f)),
                new Vector3(RandomRange(-0.12f, 0.12f), RandomRange(-0.45f, -0.10f), RandomRange(-0.12f, 0.12f)),
                RandomRange(0.30f, 0.58f));
            if (sparkleRandom.NextDouble() < 0.55)
                EmitSparkle(V(world.Ball.Position), new Vector3(0f, -0.25f, 0f), RandomRange(0.28f, 0.48f));
        }
        lastSparkleBallHitBy = world.Ball.LastHitBy;
    }

    private void EmitMinionSparkleExplosion(Vector3 center)
    {
        const int particleCount = 84;
        for (int i = 0; i < particleCount; i++)
        {
            float azimuth = RandomRange(0f, MathF.Tau);
            float y = RandomRange(-0.35f, 1f);
            float horizontal = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            Vector3 direction = new(MathF.Cos(azimuth) * horizontal, y, MathF.Sin(azimuth) * horizontal);
            float speed = RandomRange(2.2f, 6.8f);
            EmitSparkle(center + direction * RandomRange(0.02f, 0.14f), direction * speed, RandomRange(0.55f, 1.15f));
        }
    }

    private void EmitSparkle(Vector3 position, Vector3 velocity, float life)
    {
        if (superSparkles.Count >= 420)
            superSparkles.RemoveRange(0, Math.Min(30, superSparkles.Count));
        superSparkles.Add(new SparkleParticle { Position = position, Velocity = velocity, Life = life, MaxLife = life });
    }

    private float RandomRange(float min, float max) => min + (float)sparkleRandom.NextDouble() * (max - min);

    private void DrawSuperImpossibleSparkles(Matrix view, Matrix projection)
    {
        if (superSparkles.Count == 0) return;
        hud.Begin();
        foreach (SparkleParticle p in superSparkles)
        {
            Vector3 screen = GraphicsDevice.Viewport.Project(p.Position, projection, view, Matrix.Identity);
            if (screen.Z < 0f || screen.Z > 1f) continue;
            int alpha = (int)(220f * Math.Clamp(p.Life / Math.Max(0.001f, p.MaxLife), 0f, 1f));
            // Exactly 2x2 screen pixels as requested.
            hud.Panel(new Rectangle((int)MathF.Round(screen.X) - 1, (int)MathF.Round(screen.Y) - 1, 2, 2),
                new Color(105, 255, 120, alpha), new Color(105, 255, 120, alpha));
        }
        hud.End();
    }

    private void DrawHud(WorldState? world, bool selectingServeToss)
    {
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        int scale = width >= 1200 ? 2 : 1;
        Color panel = new Color(8, 18, 30, 205);
        Color border = new Color(210, 230, 245, 220);
        Color text = new Color(245, 249, 252);
        Color accent = new Color(242, 220, 84);
        Color good = new Color(120, 235, 145);

        hud.Begin();

        if (world is null)
        {
            string connection = net.ConnectionMessage.ToUpperInvariant();
            string serverDisplay = net.ServerDisplay.ToUpperInvariant();
            int boxWidth = Math.Min(width - 32, Math.Max(380, Math.Max(hud.Measure(connection, scale), hud.Measure(serverDisplay, 1)) + 32));
            Rectangle box = new((width - boxWidth) / 2, 24, boxWidth, 62 * scale);
            hud.Panel(box, panel, border);
            hud.Text(connection, new Vector2(box.X + 16, box.Y + 12), scale, accent);
            hud.Text(serverDisplay, new Vector2(box.X + 16, box.Y + 34 * scale), 1, text);
        }
        else
        {
            PlayerState? p0 = world.Players.FirstOrDefault(p => p.Id == 0);
            PlayerState? p1 = world.Players.FirstOrDefault(p => p.Id == 1);
            string n0 = ShortName(p0?.Name ?? "PLAYER 1");
            string n1 = ShortName(p1?.Name ?? "PLAYER 2");
            string points0 = PointText(world.Score, 0);
            string points1 = PointText(world.Score, 1);
            string scoreLine = $"{n0}  {world.Score.Sets[0]}S {world.Score.Games[0]}G {points0}P   -   {points1}P {world.Score.Games[1]}G {world.Score.Sets[1]}S  {n1}";
            int scoreWidth = Math.Min(width - 24, hud.Measure(scoreLine, scale) + 28);
            Rectangle scoreBox = new((width - scoreWidth) / 2, 14, scoreWidth, 24 + 8 * scale);
            hud.Panel(scoreBox, panel, border);
            hud.Text(scoreLine, new Vector2(scoreBox.X + 14, scoreBox.Y + 10), scale, text);

            string serverName = ShortName(world.Players.FirstOrDefault(p => p.Id == world.Score.Server)?.Name ?? $"PLAYER {world.Score.Server + 1}");
            bool deuce = (world.Score.Points[0] + world.Score.Points[1]) % 2 == 0;
            string serveLine = world.Phase == MatchPhase.Waiting
                ? "WAITING FOR OPPONENT"
                : $"SERVER: {serverName}   {world.Score.ServeNumber}{Ordinal(world.Score.ServeNumber)} SERVE   {(deuce ? "DEUCE" : "AD")} COURT";
            int serveWidth = Math.Min(width - 24, hud.Measure(serveLine, scale) + 28);
            Rectangle serveBox = new((width - serveWidth) / 2, scoreBox.Bottom + 7, serveWidth, 24 + 8 * scale);
            hud.Panel(serveBox, new Color(8, 18, 30, 190), world.Score.Server == net.PlayerId ? accent : border);
            hud.Text(serveLine, new Vector2(serveBox.X + 14, serveBox.Y + 10), scale, world.Score.Server == net.PlayerId ? accent : text);

            string role = world.Score.Server == net.PlayerId ? "YOU SERVE" : "OPPONENT SERVES";
            string phase = PhaseText(world.Phase);
            string left = $"{role}\n{phase}\nPING: {Math.Max(0, net.RoundTripMilliseconds)} MS";
            Rectangle status = new(14, 14, Math.Min(260, width / 3), 30 + 24 * scale);
            hud.Panel(status, panel, border);
            hud.Text(left, new Vector2(status.X + 12, status.Y + 10), scale, world.Score.Server == net.PlayerId ? good : text);

            // Everything that previously lived in the OS window title now lives
            // inside the game as a compact status card. Keep the actual title bar
            // minimal ("Tennis 3D") while preserving all useful live information.
            float statusRollDegrees = MathHelper.ToDegrees(racquetRollAngle);
            MouseState hudMouse = Mouse.GetState();
            bool forehandCharging = powerMeterActive && hudMouse.LeftButton == ButtonState.Pressed && hudMouse.RightButton != ButtonState.Pressed;
            bool sliceCharging = powerMeterActive && hudMouse.RightButton == ButtonState.Pressed && hudMouse.LeftButton != ButtonState.Pressed;
            string serveHint = selectingServeToss ? "CLICK LEGAL SERVE TARGET" : string.Empty;
            string shotStatus;
            if (powerMeterSawBothButtons && powerMeterActive)
                shotStatus = $"CHARGE CANCEL ARMED   POWER {powerMeterValue:P0}";
            else if (forehandCharging || sliceCharging)
                shotStatus = $"CHARGING {(forehandCharging ? "FOREHAND" : "SLICE")}   POWER {powerMeterValue:P0}";
            else if (swingAnimationRemaining > 0f)
                shotStatus = $"SWINGING {swingAnimationStyle.ToString().ToUpperInvariant()}   POWER {swingAnimationPower:P0}";
            else
                shotStatus = $"LAST {selectedShotStyle.ToString().ToUpperInvariant()}   POWER {lastShotPower:P0}";
            string opponentMode = aiModeSelected ? $"VS AI {selectedAiDifficulty.ToString().ToUpperInvariant()}" : "VS HUMAN";
            string tensionStatus = $"STRING {selectedStringTension:P0}";
            string statusLine1 = $"P{net.PlayerId + 1}   {opponentMode}   {shotStatus}";
            string statusLine2 = $"ANGLE {statusRollDegrees:+0;-0;0} DEG   {tensionStatus}   {world.Score.Display}";
            string statusLine3 = ShortMessage(world.Message);
            string statusLine4 = string.IsNullOrWhiteSpace(serveHint) ? net.ServerDisplay : $"{serveHint}   |   {net.ServerDisplay}";
            int liveStatusWidth = Math.Min(430, Math.Max(250, width / 3));
            Rectangle liveStatus = new(width - liveStatusWidth - 14, 14, liveStatusWidth, 92);
            hud.Panel(liveStatus, panel, border);
            hud.Text("STATUS", new Vector2(liveStatus.X + 12, liveStatus.Y + 8), 1, accent);
            hud.Text(statusLine1, new Vector2(liveStatus.X + 12, liveStatus.Y + 24), 1, text);
            hud.Text(statusLine2, new Vector2(liveStatus.X + 12, liveStatus.Y + 40), 1, text);
            hud.Text(statusLine3, new Vector2(liveStatus.X + 12, liveStatus.Y + 56), 1, text);
            hud.Text(statusLine4, new Vector2(liveStatus.X + 12, liveStatus.Y + 72), 1, text);

            string message = ShortMessage(world.Message);
            int messageWidth = Math.Min(width - 24, hud.Measure(message, scale) + 28);
            Rectangle messageBox = new((width - messageWidth) / 2, height - (46 + 16 * scale), messageWidth, 26 + 8 * scale);
            hud.Panel(messageBox, panel, border);
            hud.Text(message, new Vector2(messageBox.X + 14, messageBox.Y + 10), scale, accent);

            // Compact, task-oriented control HUD for the hold/release system.
            int selectorScale = 1;
            Color selectorFill = new Color(8, 18, 30, 190);
            Color selectedFill = new Color(242, 220, 84, 235);
            Color selectedText = new Color(16, 24, 32);
            int selectorTop = Math.Max(serveBox.Bottom + 14, height / 2 - 72);

            // LEFT: angle is continuous and controlled only by the wheel.
            int anglePanelWidth = 184;
            int anglePanelHeight = 76;
            Rectangle anglePanel = new(14, selectorTop, anglePanelWidth, anglePanelHeight);
            hud.Panel(anglePanel, selectorFill, border);
            hud.Text("RACQUET ANGLE", new Vector2(anglePanel.X + 10, anglePanel.Y + 9), selectorScale, text);
            int angleDegrees = (int)MathF.Round(MathHelper.ToDegrees(racquetRollAngle));
            string angleReadout = $"{angleDegrees:+0;-0;0} DEG";
            Rectangle angleValue = new(anglePanel.X + 8, anglePanel.Y + 29, anglePanel.Width - 16, 20);
            hud.Fill(angleValue, selectedFill);
            hud.Text(angleReadout, new Vector2(angleValue.X + 7, angleValue.Y + 6), selectorScale, selectedText);
            hud.Text("WHEEL UP -   WHEEL DOWN +", new Vector2(anglePanel.X + 10, anglePanel.Y + 56), 1, text);

            // RIGHT: shot type is chosen by which mouse button is held/released.
            int modePanelWidth = 184;
            int modePanelHeight = 76;
            Rectangle modePanel = new(width - modePanelWidth - 14, selectorTop, modePanelWidth, modePanelHeight);
            hud.Panel(modePanel, selectorFill, border);
            hud.Text("SHOT TYPE", new Vector2(modePanel.X + 10, modePanel.Y + 9), selectorScale, text);
            Rectangle forehandRow = new(modePanel.X + 8, modePanel.Y + 29, modePanel.Width - 16, 19);
            Rectangle sliceRow = new(modePanel.X + 8, modePanel.Y + 52, modePanel.Width - 16, 19);
            bool forehandSelected = forehandCharging || (!sliceCharging && selectedShotStyle == ShotStyle.Forehand);
            bool sliceSelected = sliceCharging || (!forehandCharging && selectedShotStyle == ShotStyle.Slice);
            hud.Fill(forehandRow, forehandSelected ? selectedFill : new Color(12, 25, 39, 220));
            hud.Fill(sliceRow, sliceSelected ? selectedFill : new Color(12, 25, 39, 220));
            hud.Border(forehandRow, 1, forehandSelected ? selectedFill : new Color(110, 135, 155, 180));
            hud.Border(sliceRow, 1, sliceSelected ? selectedFill : new Color(110, 135, 155, 180));
            hud.Text("LMB  FOREHAND", new Vector2(forehandRow.X + 6, forehandRow.Y + 6), selectorScale, forehandSelected ? selectedText : text);
            hud.Text("RMB  SLICE", new Vector2(sliceRow.X + 6, sliceRow.Y + 6), selectorScale, sliceSelected ? selectedText : text);

            // The power bar only appears while charging. It is a true green->yellow->red
            // gradient with a thin white cursor moving across it at the current power.
            if (powerMeterActive)
            {
                int meterWidth = Math.Min(620, Math.Max(360, width / 2));
                Rectangle meterPanel = new((width - meterWidth) / 2, messageBox.Y - 82, meterWidth, 66);
                hud.Panel(meterPanel, new Color(8, 18, 30, 225), border);
                string meterShot = powerMeterSawBothButtons ? "BOTH HELD - RELEASE CANCELS" : (forehandCharging ? "FOREHAND" : "SLICE");
                hud.Text($"POWER  {powerMeterValue:P0}   {meterShot}", new Vector2(meterPanel.X + 12, meterPanel.Y + 9), 1, text);

                Rectangle bar = new(meterPanel.X + 12, meterPanel.Y + 31, meterPanel.Width - 24, 20);
                const int segments = 40;
                for (int i = 0; i < segments; i++)
                {
                    float t = i / (float)(segments - 1);
                    Color c;
                    if (t < 0.5f)
                    {
                        float u = t / 0.5f;
                        c = new Color((byte)(60 + 190 * u), (byte)(210 + 25 * u), (byte)70);
                    }
                    else
                    {
                        float u = (t - 0.5f) / 0.5f;
                        c = new Color((byte)250, (byte)(235 - 175 * u), (byte)(70 - 30 * u));
                    }
                    int x0 = bar.X + i * bar.Width / segments;
                    int x1 = bar.X + (i + 1) * bar.Width / segments;
                    hud.Fill(new Rectangle(x0, bar.Y, Math.Max(1, x1 - x0), bar.Height), c);
                }
                hud.Border(bar, 1, new Color(245, 249, 252));
                int markerX = bar.X + (int)MathF.Round(powerMeterValue * (bar.Width - 1));
                hud.Fill(new Rectangle(markerX - 2, bar.Y - 3, 5, bar.Height + 6), new Color(250, 252, 255));
            }

            string actionText = selectingServeToss
                ? "LMB SELECT SERVE TARGET"
                : "HOLD LMB FOREHAND / RMB SLICE - RELEASE TO SWING";
            string compactControls = $"WASD MOVE   SHIFT SPRINT   SPACE JUMP   MOUSE RACQUET   {actionText}   WHEEL ANGLE   F11 FULLSCREEN   ESC QUIT";
            int stripWidth = Math.Min(width - 28, hud.Measure(compactControls, 1) + 20);
            Rectangle controlStrip = new((width - stripWidth) / 2, Math.Max(serveBox.Bottom + 8, messageBox.Y - 31), stripWidth, 25);
            hud.Panel(controlStrip, selectorFill, border);
            hud.Text(compactControls, new Vector2(controlStrip.X + 10, controlStrip.Y + 9), 1, text);

            if (pointResultOverlayRemaining > 0f && !string.IsNullOrWhiteSpace(pointResultOverlay))
            {
                float alpha = Math.Clamp(pointResultOverlayRemaining / PointResultOverlayDuration, 0f, 1f);
                // Hold solid briefly, then smoothly fade for the remainder.
                alpha = Math.Min(1f, alpha * 1.35f);
                int overlayScale = width >= 1200 ? 5 : 4;
                int overlayWidth = hud.Measure(pointResultOverlay, overlayScale);
                int overlayAlpha = (int)MathF.Round(255f * alpha);
                Color resultRed = new Color(245, 55, 55, overlayAlpha);
                Vector2 overlayPos = new((width - overlayWidth) / 2f, height * 0.43f);
                hud.Text(pointResultOverlay, overlayPos, overlayScale, resultRed);

                if (!string.IsNullOrWhiteSpace(pointWinnerOverlay))
                {
                    int winnerScale = width >= 1200 ? 2 : 1;
                    int winnerWidth = hud.Measure(pointWinnerOverlay, winnerScale);
                    Vector2 winnerPos = new(
                        (width - winnerWidth) / 2f,
                        overlayPos.Y + overlayScale * 9f);
                    hud.Text(pointWinnerOverlay, winnerPos, winnerScale, resultRed);
                }
            }
        }

        if (world is not null && world.Phase == MatchPhase.MatchOver && pointResultOverlayRemaining <= 0f)
        {
            DrawWinScreen(world, width, height);
        }

        if (selectingServeToss)
        {
            int x = (int)MathF.Round(serveTossGuideScreenX);
            int y = (int)MathF.Round(serveTossGuideScreenY);
            Color courtBlue = new(42, 92, 160, 245);
            Color legal = new(95, 220, 125, 150);
            Color line = new(245, 248, 250, 235);
            hud.Panel(serveTossTargetBox, courtBlue, line);
            hud.Fill(serveLandingLegalBox, legal);

            float sx = serveTossTargetBox.Width / (GameConstants.CourtHalfWidth * 2f);
            float sz = serveTossTargetBox.Height / (GameConstants.CourtHalfLength * 2f);
            int singlesLeft = serveTossTargetBox.Left + (int)MathF.Round((GameConstants.CourtHalfWidth - GameConstants.SinglesHalfWidth) * sx);
            int singlesRight = serveTossTargetBox.Right - (int)MathF.Round((GameConstants.CourtHalfWidth - GameConstants.SinglesHalfWidth) * sx);
            int netY = serveTossTargetBox.Top + serveTossTargetBox.Height / 2;
            int serviceOffset = (int)MathF.Round(GameConstants.ServiceLine * sz);
            hud.Fill(new Rectangle(singlesLeft, serveTossTargetBox.Top, 2, serveTossTargetBox.Height), line);
            hud.Fill(new Rectangle(singlesRight, serveTossTargetBox.Top, 2, serveTossTargetBox.Height), line);
            hud.Fill(new Rectangle(serveTossTargetBox.Left, netY - 2, serveTossTargetBox.Width, 4), new Color(25, 28, 34, 245));
            hud.Fill(new Rectangle(serveTossTargetBox.Left, netY - serviceOffset, serveTossTargetBox.Width, 2), line);
            hud.Fill(new Rectangle(serveTossTargetBox.Left, netY + serviceOffset, serveTossTargetBox.Width, 2), line);
            hud.Fill(new Rectangle(serveTossTargetBox.Left + serveTossTargetBox.Width / 2 - 1, netY - serviceOffset, 2, serviceOffset * 2), line);

            hud.Cross(x, y, 13, new Color(255, 242, 95));
            hud.Text("YOUR LEFT", new Vector2(serveTossTargetBox.Left + 8, netY + 10), 1, text);
            string rightLabel = "YOUR RIGHT";
            hud.Text(rightLabel,
                new Vector2(serveTossTargetBox.Right - hud.Measure(rightLabel, 1) - 8, netY + 10),
                1, text);
            hud.Text("CLICK SERVE LANDING SPOT", new Vector2(serveTossTargetBox.X + 10, serveTossTargetBox.Y + 9), 1, text);
            string help = "GREEN = IN   RANDOM ERROR RADIUS = 1/4 SERVICE-BOX LENGTH   EDGE AIM HAS FAULT RISK";
            int helpX = Math.Max(8, (width - hud.Measure(help, 1)) / 2);
            hud.Text(help, new Vector2(helpX, Math.Min(height - 20, serveTossTargetBox.Bottom + 10)), 1, text);
        }

        hud.End();
    }

    private void DrawWinScreen(WorldState world, int width, int height)
    {
        int winner = world.Score.Sets[0] > world.Score.Sets[1] ? 0 : 1;
        string winnerName = world.Players.FirstOrDefault(p => p.Id == winner)?.Name ?? $"PLAYER {winner + 1}";
        bool localWon = net.PlayerId == winner;

        string headline = localWon ? "YOU WIN" : $"{ShortName(winnerName)} WINS";
        string subhead = "MATCH COMPLETE";
        string score = $"SETS  {world.Score.Sets[0]} - {world.Score.Sets[1]}";
        string hint = "ESC QUIT";

        int headlineScale = width >= 1200 ? 5 : 4;
        int bodyScale = width >= 1200 ? 2 : 1;
        int panelWidth = Math.Min(width - 40, Math.Max(520, hud.Measure(headline, headlineScale) + 80));
        int panelHeight = 220;
        Rectangle panel = new((width - panelWidth) / 2, (height - panelHeight) / 2, panelWidth, panelHeight);
        Color panelFill = new Color(5, 12, 22, 238);
        Color border = localWon ? new Color(120, 235, 145, 245) : new Color(245, 249, 252, 235);
        Color title = localWon ? new Color(120, 235, 145) : new Color(245, 249, 252);
        Color body = new Color(245, 249, 252);
        Color accent = new Color(242, 220, 84);

        hud.Panel(panel, panelFill, border);
        hud.Text(subhead, new Vector2((width - hud.Measure(subhead, bodyScale)) / 2f, panel.Y + 28), bodyScale, accent);
        hud.Text(headline, new Vector2((width - hud.Measure(headline, headlineScale)) / 2f, panel.Y + 68), headlineScale, title);
        hud.Text(score, new Vector2((width - hud.Measure(score, bodyScale)) / 2f, panel.Y + 140), bodyScale, body);
        hud.Text(hint, new Vector2((width - hud.Measure(hint, 1)) / 2f, panel.Bottom - 30), 1, body);
    }

    private static string ShortName(string value) => value.Length <= 12 ? value.ToUpperInvariant() : value[..12].ToUpperInvariant();
    private static string PointResultText(string message)
    {
        string reason = message.ToUpperInvariant();
        if (reason.Contains("DOUBLE FAULT")) return "DOUBLE FAULT";
        if (reason.Contains("DOUBLE BOUNCE")) return "DOUBLE BOUNCE";
        if (reason.Contains("MISSED")) return "MISSED";
        if (reason.Contains("NET")) return "NET";
        if (reason.Contains("HITTER'S SIDE")) return "SHORT";
        if (reason.Contains("OUT") || reason.Contains("LEFT THE PLAYABLE AREA")) return "OUT";
        // Safety net: every point-ending condition gets visible red feedback even if
        // a future scoring reason is added without a dedicated label here.
        return "POINT";
    }

    private static string PointWinnerText(WorldState world, int localPlayerId)
    {
        int winner = world.Score.PointWinner;
        if (winner < 0 || winner > 1) return "";
        if (winner == localPlayerId) return "YOU GOT THE POINT";

        string winnerName = world.Players.FirstOrDefault(p => p.Id == winner)?.Name ?? $"PLAYER {winner + 1}";
        return $"{ShortName(winnerName)} GOT THE POINT";
    }

    private static string ShortMessage(string value) => value.Length <= 70 ? value.ToUpperInvariant() : value[..67].ToUpperInvariant() + "...";
    private static string Ordinal(int value) => value == 1 ? "ST" : value == 2 ? "ND" : "TH";
    private static string PhaseText(MatchPhase phase) => phase switch
    {
        MatchPhase.Waiting => "LOBBY",
        MatchPhase.ServeSetup => "AIM SERVE",
        MatchPhase.ServeToss => "AUTOMATIC SERVE",
        MatchPhase.Rally => "RALLY",
        MatchPhase.PointOver => "POINT OVER",
        MatchPhase.ChangeEnds => "CHANGE ENDS",
        MatchPhase.MatchOver => "MATCH OVER",
        _ => phase.ToString().ToUpperInvariant()
    };
    private static string PointText(ScoreState score, int player)
    {
        if (score.TieBreak) return score.Points[player].ToString();
        int mine = score.Points[player];
        int other = score.Points[1 - player];
        if (mine >= 3 && other >= 3)
        {
            if (mine == other) return "40";
            return mine > other ? "AD" : "40";
        }
        return mine switch { 0 => "0", 1 => "15", 2 => "30", _ => "40" };
    }

    private PlayerState CreateLocalVisual(PlayerState source)
    {
        bool localSwing = swingAnimationRemaining > 0f || swing;
        float localProgress = localSwing
            ? SwingAnimation.DurationSeconds - Math.Clamp(swingAnimationRemaining, 0f, SwingAnimation.DurationSeconds)
            : source.SwingProgress;
        HandedSwing visualHand = localSwing ? swingAnimationHand : hand;
        // The 0.5-second charge phase has a visual-only backswing. Snapshotting the
        // starting pose means 100% charge moves one full 90-degree control range
        // backward from where the racquet began, clamped at the same perpendicular
        // limit as normal mouse control. The authoritative racquetHorizontal value is
        // intentionally untouched so charge animation cannot alter shot steering.
        float visualHorizontal = RacquetControls.ToForwardness(racquetHorizontal, hand);
        if (powerMeterActive)
            visualHorizontal = PowerMeter.ChargeAnimationControl(chargeAnimationStartForwardness, powerMeterValue);

        return new PlayerState
        {
            Id = source.Id,
            Name = source.Name,
            Position = source.Position,
            Velocity = source.Velocity,
            FacingYaw = localSwing
                ? ShotControls.EncodeVisualFacingYaw(source.Id, swingAnimationStyle)
                : source.FacingYaw,
            Swinging = localSwing || source.SwingReleasedVisual,
            SwingProgress = localProgress,
            SwingHand = visualHand,
            SwingHeight = localSwing ? swingAnimationStartHeight : swingHeight,
            RacquetBehind = powerMeterActive ? true : racquetBehind,
            RacquetHorizontal = visualHorizontal,
            RacquetFaceAngle = localSwing ? swingAnimationAngle : racquetRollAngle,
            SwingReleasedVisual = source.SwingReleasedVisual,
            SwingVisualTimer = source.SwingVisualTimer,
            SwingPower = source.SwingPower,
            LastInputSequence = source.LastInputSequence,
            LastJumpSequence = source.LastJumpSequence,
            Grounded = source.Grounded
        };
    }

    private static PlayerState DefaultPlayer(int id) => new()
    {
        Id = id,
        Name = $"Player {id + 1}",
        Position = new N.Vector3(0, 0, id == 0 ? -9.5f : 9.5f),
        FacingYaw = id == 0 ? 0 : MathF.PI,
        SwingHand = HandedSwing.Right,
        SwingHeight = 1.05f
    };

    private void DrawEnvironment()
    {
        draw.Box(new Vector3(0, -0.14f, 0), new Vector3(42, 0.24f, 52), new Color(56, 125, 60));
    }

    private void DrawCourt()
    {
        draw.Box(new Vector3(0, -0.02f, 0), new Vector3(GameConstants.CourtHalfWidth * 2, .06f, GameConstants.CourtHalfLength * 2), new Color(48, 100, 171));
        float t = .055f;
        foreach (float x in new[] { -GameConstants.CourtHalfWidth, GameConstants.CourtHalfWidth, -GameConstants.SinglesHalfWidth, GameConstants.SinglesHalfWidth })
            draw.Box(new Vector3(x, .025f, 0), new Vector3(t, .035f, GameConstants.CourtHalfLength * 2), Color.White);
        foreach (float z in new[] { -GameConstants.CourtHalfLength, GameConstants.CourtHalfLength, -GameConstants.ServiceLine, GameConstants.ServiceLine })
            draw.Box(new Vector3(0, .025f, z), new Vector3(GameConstants.CourtHalfWidth * 2, .035f, t), Color.White);
        draw.Box(new Vector3(0, .025f, 0), new Vector3(t, .035f, GameConstants.ServiceLine * 2), Color.White);
        draw.Beam(new Vector3(-GameConstants.CourtHalfWidth - .28f, 0, 0), new Vector3(-GameConstants.CourtHalfWidth - .28f, 1.12f, 0), .09f, Color.Silver);
        draw.Beam(new Vector3(GameConstants.CourtHalfWidth + .28f, 0, 0), new Vector3(GameConstants.CourtHalfWidth + .28f, 1.12f, 0), .09f, Color.Silver);
        DrawDeformableNet();
    }

    private void UpdateNetRipple(float dt, WorldState? world)
    {
        netRippleAge += dt;
        if (world is null)
        {
            hasPreviousNetBallSample = false;
            lastNetVisualTick = -1;
            return;
        }

        // net.Latest is read every render frame but server snapshots arrive at 30 Hz.
        // Only process each authoritative tick once so one impact cannot retrigger.
        if (world.Tick == lastNetVisualTick) return;
        lastNetVisualTick = world.Tick;

        BallState ball = world.Ball;
        if (hasPreviousNetBallSample)
        {
            float netHeight = BallFlightModel.NetHeightAtX(ball.Position.X);
            bool nearNet = MathF.Abs(ball.Position.Z) <= GameConstants.NetThickness + 0.22f &&
                           MathF.Abs(ball.Position.X) <= GameConstants.CourtHalfWidth + GameConstants.BallRadius &&
                           ball.Position.Y <= netHeight + GameConstants.BallRadius * 1.5f;
            bool reversedAcrossMesh = previousNetBallVelocity.Z * ball.Velocity.Z < -0.05f;
            bool netCall = world.Message.Contains("net", StringComparison.OrdinalIgnoreCase);

            if (previousNetBallInPlay && nearNet && (reversedAcrossMesh || netCall))
            {
                float incomingNormalSpeed = MathF.Abs(previousNetBallVelocity.Z);
                netRippleCenterX = Math.Clamp(ball.Position.X, -GameConstants.CourtHalfWidth, GameConstants.CourtHalfWidth);
                netRippleCenterY = Math.Clamp(ball.Position.Y, 0.10f, netHeight);
                netRippleDirectionZ = MathF.Sign(previousNetBallVelocity.Z);
                if (netRippleDirectionZ == 0f)
                    netRippleDirectionZ = ball.Position.Z <= 0f ? 1f : -1f;

                // Faster impacts push the mesh farther, but keep the deformation inside
                // a believable range so the net never visually turns inside-out.
                float speed01 = Math.Clamp((incomingNormalSpeed - 3f) / 34f, 0f, 1f);
                netRippleAmplitude = MathHelper.Lerp(0.10f, 0.42f, speed01);
                netRippleAge = 0f;
            }
        }

        previousNetBallVelocity = ball.Velocity;
        previousNetBallInPlay = ball.InPlay;
        hasPreviousNetBallSample = true;
    }

    private float NetRippleDisplacement(float x, float y)
    {
        if (netRippleAge > 1.65f || netRippleAmplitude <= 0f) return 0f;

        float dx = (x - netRippleCenterX) * 0.82f;
        float dy = (y - netRippleCenterY) * 1.75f;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float decay = MathF.Exp(-2.35f * netRippleAge);

        // Immediate local pocket plus an outward-moving, rapidly damped wave front.
        float pocket = MathF.Exp(-distance * 2.1f) * MathF.Exp(-4.2f * netRippleAge);
        float waveDistance = netRippleAge * 4.2f;
        float waveDelta = (distance - waveDistance) / 0.42f;
        float wave = MathF.Exp(-(waveDelta * waveDelta)) * 0.58f;
        float oscillation = MathF.Cos(distance * 6.2f - netRippleAge * 18f) * 0.18f * MathF.Exp(-distance * 1.2f);
        return netRippleDirectionZ * netRippleAmplitude * decay * (pocket + wave + oscillation);
    }

    private Vector3 NetNode(float x, float y)
    {
        float top = BallFlightModel.NetHeightAtX(x);
        float clampedY = Math.Clamp(y, 0.12f, top);
        float verticalFreedom = Math.Clamp((top - clampedY) / Math.Max(0.08f, top - 0.12f), 0f, 1f);
        // The top cable is taut, so it moves much less than the mesh below it.
        float cableFactor = MathHelper.Lerp(0.28f, 1f, verticalFreedom);
        return new Vector3(x, clampedY, NetRippleDisplacement(x, clampedY) * cableFactor);
    }

    private void DrawDeformableNet()
    {
        Color mesh = new(40, 40, 45);
        Color tape = Color.White;
        const float xStep = 0.38f;
        const float yStep = 0.18f;

        // Segmented top cable follows the regulation center sag and can flex slightly
        // at the impact point instead of remaining a single rigid beam.
        for (float x = -GameConstants.CourtHalfWidth; x < GameConstants.CourtHalfWidth; x += xStep)
        {
            float x2 = MathF.Min(GameConstants.CourtHalfWidth, x + xStep);
            draw.Beam(NetNode(x, BallFlightModel.NetHeightAtX(x)),
                      NetNode(x2, BallFlightModel.NetHeightAtX(x2)), .055f, tape);
        }

        // Vertical mesh strands.
        for (float x = -GameConstants.CourtHalfWidth; x <= GameConstants.CourtHalfWidth + 0.001f; x += xStep)
        {
            float top = BallFlightModel.NetHeightAtX(x);
            float y = 0.12f;
            Vector3 previous = NetNode(x, y);
            while (y < top - 0.001f)
            {
                float nextY = MathF.Min(top, y + yStep);
                Vector3 next = NetNode(x, nextY);
                draw.Beam(previous, next, .012f, mesh);
                previous = next;
                y = nextY;
            }
        }

        // Horizontal mesh strands are drawn piecewise so neighboring nodes can move by
        // different amounts and form a visible traveling ripple.
        for (float y = 0.15f; y <= GameConstants.NetHeightPost + 0.001f; y += yStep)
        {
            for (float x = -GameConstants.CourtHalfWidth; x < GameConstants.CourtHalfWidth; x += xStep)
            {
                float x2 = MathF.Min(GameConstants.CourtHalfWidth, x + xStep);
                float top1 = BallFlightModel.NetHeightAtX(x);
                float top2 = BallFlightModel.NetHeightAtX(x2);
                if (y > MathF.Min(top1, top2)) continue;
                draw.Beam(NetNode(x, MathF.Min(y, top1)), NetNode(x2, MathF.Min(y, top2)), .012f, mesh);
            }
        }
    }

    private void DrawServeTossPreview(Vector3 playerPosition, float centerX, float centerApexHeight)
    {
        Vector3 center = new Vector3(centerX, centerApexHeight, playerPosition.Z);
        Vector3 top = center + Vector3.Up * VisualBallRadius;
        Color guide = new(255, 245, 120);

        // Ghost ball plus a small cross exactly at the selected apex point.
        draw.Sphere(center, VisualBallRadius, new Color(205, 220, 80), 8, 12);
        draw.Beam(top + Vector3.Left * .22f, top + Vector3.Right * .22f, .012f, guide);
        draw.Beam(top + Vector3.Down * .22f, top + Vector3.Up * .22f, .012f, guide);
    }

    private void DrawBall(Vector3 position)
    {
        draw.Sphere(position, VisualBallRadius, new Color(220, 238, 65), 12, 20);
        draw.Sphere(new Vector3(position.X, .025f, position.Z), .075f, new Color(40, 55, 45), 6, 12);
    }

    private void DrawMinion(MinionState m)
    {
        // Minions are intentionally smaller than a normal player and remain grounded.
        // Their replicated swing state is independent, but their hits are credited to
        // the Super Impossible owner by the server to preserve normal two-player rules.
        const float scale = 0.68f;
        Vector3 pp = V(m.Position);
        Vector3 forward = m.OwnerId == 0 ? Vector3.Backward : Vector3.Forward;
        Vector3 right = m.OwnerId == 0 ? Vector3.Right : Vector3.Left;
        Color shirt = new Color(82, 205, 98);
        Color skin = new Color(212, 174, 135);

        Vector3 headCenter = pp + new Vector3(0f, 1.78f * scale, 0f);
        draw.Sphere(headCenter, .23f * scale, skin, 8, 12);
        draw.Sphere(headCenter + forward * (.205f * scale), .055f * scale, new Color(198, 151, 116), 6, 8);
        draw.Box(pp + new Vector3(0f, 1.12f * scale, 0f),
            new Vector3(.58f * scale, 1.05f * scale, .34f * scale), shirt);
        draw.Beam(pp + new Vector3(-.17f * scale, .78f * scale, 0f),
            pp + new Vector3(-.20f * scale, .12f * scale, .03f), .17f * scale, new Color(34, 46, 75));
        draw.Beam(pp + new Vector3(.17f * scale, .78f * scale, 0f),
            pp + new Vector3(.20f * scale, .12f * scale, .03f), .17f * scale, new Color(34, 46, 75));

        float handSide = m.SwingHand == HandedSwing.Left ? 1f : -1f;
        Vector3 shoulder = pp + right * (.31f * scale * handSide) + new Vector3(0f, 1.38f * scale, 0f);
        Vector3 swingDirection;
        float animatedHeight = Math.Clamp(m.SwingHeight * scale, 0.35f, 1.65f);
        if (m.Swinging)
        {
            float progress = Math.Clamp(m.SwingProgress / SwingAnimation.DurationSeconds, 0f, 1f);
            SwingAnimationPose pose = SwingAnimation.GetPose(progress, ShotStyle.Forehand, animatedHeight);
            swingDirection = Vector3.Normalize(right * (handSide * pose.LateralFactor) + forward * pose.ForwardFactor);
            animatedHeight = pose.Height;
        }
        else
        {
            swingDirection = Vector3.Normalize(right * (-handSide) + forward * 0.08f);
        }

        Vector3 wrist = shoulder + swingDirection * (.62f * scale) +
                        new Vector3(0f, animatedHeight - 1.05f * scale, 0f);
        draw.Beam(shoulder, wrist, .115f * scale, skin);
        // Keep the racquet readable at roughly regulation size even though its wielder is smaller.
        DrawRacquet(wrist, swingDirection, right, forward, animatedHeight,
            m.RacquetFaceAngle, m.SwingHand, localPlayer: false);
    }

    private void DrawPlayer(PlayerState p, bool localPlayer)
    {
        Vector3 pp = V(p.Position);
        Color shirt = p.Id == 0 ? new Color(200, 55, 52) : new Color(238, 143, 42);
        Vector3 forward = p.Id == 0 ? Vector3.Backward : Vector3.Forward;
        Vector3 right = p.Id == 0 ? Vector3.Right : Vector3.Left;
        Vector3 headCenter = pp + new Vector3(0, 1.78f, 0);
        draw.Sphere(headCenter, .23f, new Color(228, 185, 145), 10, 16);
        // A small nose/face marker makes the player's net-facing direction unmistakable.
        draw.Sphere(headCenter + forward * .205f + new Vector3(0, -.015f, 0), .055f, new Color(218, 166, 126), 7, 10);
        draw.Box(pp + new Vector3(0, 1.12f, 0), new Vector3(.58f, 1.05f, .34f), shirt);
        draw.Beam(pp + new Vector3(-.17f, .78f, 0), pp + new Vector3(-.20f, .12f, .03f), .17f, new Color(34, 46, 75));
        draw.Beam(pp + new Vector3(.17f, .78f, 0), pp + new Vector3(.20f, .12f, .03f), .17f, new Color(34, 46, 75));

        if (IsSuperImpossiblePlayer(p))
        {
            // Super Impossible is a visual-only six-armed form. Three mirrored arm pairs
            // share the exact same authoritative swing progress and racquet controls.
            // This deliberately does NOT add hitboxes, inputs, network state, or extra
            // contacts: gameplay still uses the one existing authoritative racquet.
            for (int row = 0; row < 3; row++)
            {
                float shoulderHeightOffset = (row - 1) * 0.26f;
                DrawPlayerArmAndRacquet(p, pp, right, forward, HandedSwing.Right,
                    shoulderHeightOffset, localPlayer: false);
                DrawPlayerArmAndRacquet(p, pp, right, forward, HandedSwing.Left,
                    shoulderHeightOffset, localPlayer: false);
            }
            return;
        }

        DrawPlayerArmAndRacquet(p, pp, right, forward, p.SwingHand, 0f, localPlayer);
    }

    private void DrawPlayerArmAndRacquet(PlayerState p, Vector3 pp, Vector3 right, Vector3 forward,
                                         HandedSwing armHand, float shoulderHeightOffset, bool localPlayer)
    {
        float handSide = armHand == HandedSwing.Left ? 1f : -1f;
        Vector3 shoulder = pp + right * (.31f * handSide) + new Vector3(0, 1.38f + shoulderHeightOffset, 0);
        Vector3 swingDirection;
        bool isSwinging = p.Swinging || p.SwingReleasedVisual;
        float animatedHeight = p.SwingHeight;
        if (isSwinging)
        {
            ShotStyle visualStyle = ShotControls.DecodeVisualStyle(p.FacingYaw);
            float progress = Math.Clamp(p.SwingProgress / SwingAnimation.DurationSeconds, 0f, 1f);
            SwingAnimationPose pose = SwingAnimation.GetPose(progress, visualStyle, p.SwingHeight);
            swingDirection = Vector3.Normalize(
                right * (handSide * pose.LateralFactor) + forward * pose.ForwardFactor);
            animatedHeight = pose.Height;
        }
        else
        {
            // Every visual arm follows the same shared forward/back racquet control;
            // left-side arms mirror right-side arms but remain synchronized.
            RacquetHoopPose hoop = RacquetControls.GetPose(p.RacquetHorizontal, armHand);
            float shoulderLateral = .31f * handSide;
            float armLateral = hoop.LateralOffset - shoulderLateral;
            Vector3 armTarget = right * armLateral + forward * hoop.ForwardOffset;
            if (armTarget.LengthSquared() < 0.0001f)
                armTarget = right * (-handSide);
            swingDirection = Vector3.Normalize(armTarget);
        }

        Vector3 wrist = shoulder + swingDirection * .62f + new Vector3(0, animatedHeight - 1.05f, 0);
        draw.Beam(shoulder, wrist, .115f, new Color(228, 185, 145));
        DrawRacquet(wrist, swingDirection, right, forward, animatedHeight, p.RacquetFaceAngle, armHand, localPlayer);
    }

    private void DrawRacquet(Vector3 wrist, Vector3 armDirection, Vector3 playerRight, Vector3 playerForward,
                             float racquetHeight, float rollAngle, HandedSwing swingHand, bool localPlayer)
    {
        // Height controls only the handle/shaft pose. Low contact positions hold the
        // shaft approximately perpendicular to the upright body; at maximum reach,
        // the shaft points vertically upward. This transition is continuous.
        float heightT = MathHelper.SmoothStep(0f, 1f,
            Math.Clamp((racquetHeight - 0.35f) / (2.35f - 0.35f), 0f, 1f));

        Vector3 horizontalShaft = armDirection;
        horizontalShaft.Y = 0f;
        if (horizontalShaft.LengthSquared() < 0.0001f)
            horizontalShaft = playerRight;
        horizontalShaft.Normalize();

        // Preserve a slight outward component near the top so the racquet remains
        // readable and does not collapse into the player's vertical body axis.
        Vector3 raisedShaft = Vector3.Normalize(Vector3.Up * 0.985f + horizontalShaft * 0.17f);
        Vector3 shaft = Vector3.Normalize(Vector3.Lerp(horizontalShaft, raisedShaft, heightT));

        // At zero roll, the string-bed normal faces the net as closely as possible.
        // Project playerForward onto the plane perpendicular to the shaft.
        Vector3 baseNormal = playerForward - shaft * Vector3.Dot(playerForward, shaft);
        if (baseNormal.LengthSquared() < 0.0001f)
            baseNormal = Vector3.Cross(shaft, playerRight);
        baseNormal.Normalize();
        if (Vector3.Dot(baseNormal, playerForward) < 0f) baseNormal = -baseNormal;

        // Only the oval/string face rolls around the shaft. The shaft itself and all
        // racquet positions remain exactly unchanged.
        // A mirrored left-hand shaft reverses the apparent sign of an axis-angle roll.
        // Flip only the left-hand geometric roll so the same numeric face angle has
        // the same topspin/lob meaning and appearance on both hands.
        float geometricRoll = swingHand == HandedSwing.Left ? -rollAngle : rollAngle;
        Matrix rollRotation = Matrix.CreateFromAxisAngle(shaft, geometricRoll);
        Vector3 faceNormal = Vector3.Normalize(Vector3.TransformNormal(baseNormal, rollRotation));
        Vector3 across = Vector3.Normalize(Vector3.Cross(shaft, faceNormal));
        if (Vector3.Dot(across, playerRight) < 0f) across = -across;
        faceNormal = Vector3.Normalize(Vector3.Cross(across, shaft));

        float scale = RacquetGeometry.VisualScale;
        float handleLength = RacquetGeometry.HandleLengthBase * scale;
        float throatLength = RacquetGeometry.ThroatLengthBase * scale;
        float hoopHalfWidth = RacquetGeometry.HoopHalfWidthBase * scale;
        float hoopHalfLength = RacquetGeometry.HoopHalfLengthBase * scale;

        Vector3 handleEnd = wrist + shaft * handleLength;
        Vector3 throatBase = handleEnd + shaft * (RacquetGeometry.HandleGapBase * scale);
        Vector3 hoopBottomCenter = throatBase + shaft * throatLength;
        Vector3 hoopCenter = hoopBottomCenter + shaft * hoopHalfLength;

        draw.Beam(wrist, handleEnd, localPlayer ? .085f : .07f, new Color(105, 63, 30));
        draw.Beam(handleEnd, throatBase, .055f, Color.Silver);

        Vector3 lowerLeft = throatBase - across * (.045f * scale);
        Vector3 lowerRight = throatBase + across * (.045f * scale);
        Vector3 upperLeft = hoopBottomCenter - across * (hoopHalfWidth * .34f);
        Vector3 upperRight = hoopBottomCenter + across * (hoopHalfWidth * .34f);
        draw.Beam(lowerLeft, upperLeft, .035f, Color.Silver);
        draw.Beam(lowerRight, upperRight, .035f, Color.Silver);

        const int segments = 40;
        Vector3[] ring = new Vector3[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.Tau * i / segments;
            ring[i] = hoopCenter
                    + across * (MathF.Cos(a) * hoopHalfWidth)
                    + shaft * (MathF.Sin(a) * hoopHalfLength);
        }
        for (int i = 0; i < segments; i++)
            draw.Beam(ring[i], ring[(i + 1) % segments], localPlayer ? .035f : .028f, new Color(225, 235, 240));

        for (int i = -4; i <= 4; i++)
        {
            float x = i * (hoopHalfWidth / 5f);
            float halfLong = hoopHalfLength * MathF.Sqrt(MathF.Max(0f, 1f - (x * x) / (hoopHalfWidth * hoopHalfWidth)));
            draw.Beam(hoopCenter + across * x - shaft * halfLong,
                      hoopCenter + across * x + shaft * halfLong,
                      .010f, new Color(180, 205, 215));
        }
        for (int i = -5; i <= 5; i++)
        {
            float along = i * (hoopHalfLength / 6f);
            float halfAcross = hoopHalfWidth * MathF.Sqrt(MathF.Max(0f, 1f - (along * along) / (hoopHalfLength * hoopHalfLength)));
            draw.Beam(hoopCenter + shaft * along - across * halfAcross,
                      hoopCenter + shaft * along + across * halfAcross,
                      .010f, new Color(180, 205, 215));
        }
    }

    private void CenterMouse() => Mouse.SetPosition(Window.ClientBounds.Width / 2, Window.ClientBounds.Height / 2);
    private static Vector3 V(N.Vector3 v) => new(v.X, v.Y, v.Z);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            net?.Dispose();
            draw?.Dispose();
            hud?.Dispose();
        }
        base.Dispose(disposing);
    }
}
