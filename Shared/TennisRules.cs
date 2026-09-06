namespace Tennis3D.Shared;

public sealed class TennisRules
{
    public static float ServeFromXSign(ScoreState score, int server)
    {
        bool deuceCourt = (score.Points[0] + score.Points[1]) % 2 == 0;
        float serverRightSign = server == 0 ? 1f : -1f;
        return serverRightSign * (deuceCourt ? 1f : -1f);
    }

    public static bool IsLegalServeX(ScoreState score, int server, float x)
    {
        float sign = ServeFromXSign(score, server);
        return sign > 0f
            ? x > 0f && x <= GameConstants.SinglesHalfWidth
            : x < 0f && x >= -GameConstants.SinglesHalfWidth;
    }

    public void AwardPoint(WorldState world, int winner, string reason)
    {
        winner = Math.Clamp(winner, 0, 1);
        ScoreState score = world.Score;
        score.PointWinner = winner;

        if (score.TieBreak)
        {
            score.Points[winner]++;
            int total = score.Points[0] + score.Points[1];
            if (total == 1 || (total > 1 && total % 2 == 1)) score.Server = 1 - score.Server;
            if (score.Points[winner] >= 7 && score.Points[winner] - score.Points[1 - winner] >= 2)
            {
                // The player who RECEIVED the first tie-break point serves first in
                // the next set. score.Server has already been advanced for the just-
                // completed tie-break point, so reconstruct the original tie-break
                // server from the number of server toggles and select the opposite.
                int serverToggles = (total + 1) / 2;
                int firstTieBreakServer = (serverToggles & 1) == 0
                    ? score.Server
                    : 1 - score.Server;
                score.Server = 1 - firstTieBreakServer;
                score.ServeNumber = 1;

                score.Games[winner]++;
                FinishSet(world, winner);
            }
        }
        else
        {
            score.Points[winner]++;
            if (score.Points[winner] >= 4 && score.Points[winner] - score.Points[1 - winner] >= 2)
            {
                score.Games[winner]++;
                score.Points[0] = score.Points[1] = 0;
                score.Server = 1 - score.Server;
                score.ServeNumber = 1;

                if (score.Games[0] == 6 && score.Games[1] == 6)
                {
                    score.TieBreak = true;
                }
                else if (score.Games[winner] >= 6 && score.Games[winner] - score.Games[1 - winner] >= 2)
                {
                    FinishSet(world, winner);
                }
            }
        }

        score.Display = Format(score);
        bool matchOver = score.Sets[winner] >= 2;
        world.Phase = matchOver ? MatchPhase.MatchOver : MatchPhase.PointOver;
        world.Message = matchOver
            ? $"{PlayerName(world, winner)} wins the match — {reason}."
            : $"{PlayerName(world, winner)} wins the point — {reason}.";
        // Keep the dead ball physically moving during the point-result display.
        // PrepareNextPoint replaces the ball when the next point begins.
        world.Ball.IsServeToss = false;
    }

    public void ServeFault(WorldState world, int server, string reason)
    {
        if (world.Score.ServeNumber == 1)
        {
            world.Score.ServeNumber = 2;
            world.Phase = MatchPhase.ServeSetup;
            world.Message = $"Fault: {reason}. Second serve.";
            world.Ball.InPlay = false;
            world.Ball.IsServeToss = false;
        }
        else
        {
            world.Score.ServeNumber = 1;
            AwardPoint(world, 1 - server, $"double fault ({reason})");
        }
    }

    public void PrepareNextPoint(WorldState world)
    {
        if (world.Phase == MatchPhase.MatchOver) return;
        world.Score.ServeNumber = 1;
        world.Score.PointWinner = -1;
        world.Phase = MatchPhase.ServeSetup;
        ResetPlayersAndBall(world);
        world.Message = $"{PlayerName(world, world.Score.Server)} to serve. Click a landing spot in the highlighted service box.";
    }

    public void ResetPlayersAndBall(WorldState world)
    {
        int server = world.Score.Server;
        float serverX = ServeFromXSign(world.Score, server) * GameConstants.ServeSideOffset;

        foreach (PlayerState player in world.Players)
        {
            float z = player.Id == 0
                ? -GameConstants.CourtHalfLength - GameConstants.ServeBaselineClearance
                : GameConstants.CourtHalfLength + GameConstants.ServeBaselineClearance;
            float x = player.Id == server ? serverX : 0f;
            player.Position = new System.Numerics.Vector3(x, 0, z);
            player.Velocity = default;
            player.Grounded = true;
            player.FacingYaw = player.Id == 0 ? 0f : MathF.PI;
            player.Swinging = false;
            player.SwingReleasedVisual = false;
            player.SwingVisualTimer = 0f;
            player.SwingProgress = 0f;
        }
        PlayerState? serverPlayer = world.Players.FirstOrDefault(p => p.Id == server);
        world.Ball = new BallState
        {
            Position = (serverPlayer?.Position ?? new System.Numerics.Vector3(serverX, 0, server == 0 ? -GameConstants.CourtHalfLength - GameConstants.ServeBaselineClearance : GameConstants.CourtHalfLength + GameConstants.ServeBaselineClearance))
                       + new System.Numerics.Vector3(server == 0 ? -0.55f : 0.55f, 1.15f, 0),
            TossOwner = server,
            ServeTargetXSign = serverX > 0 ? -1 : 1,
            LastHitBy = -1
        };
    }

    private static void FinishSet(WorldState world, int winner)
    {
        ScoreState score = world.Score;
        score.Sets[winner]++;
        score.Games[0] = score.Games[1] = 0;
        score.Points[0] = score.Points[1] = 0;
        score.TieBreak = false;
    }

    private static string PlayerName(WorldState world, int id) =>
        world.Players.FirstOrDefault(p => p.Id == id)?.Name ?? $"Player {id + 1}";

    public static string Format(ScoreState score)
    {
        string PointText(int player)
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

        string serve = score.ServeNumber == 2 ? "  2nd serve" : "";
        return $"Sets {score.Sets[0]}-{score.Sets[1]}  Games {score.Games[0]}-{score.Games[1]}  Points {PointText(0)}-{PointText(1)}{serve}";
    }
}
