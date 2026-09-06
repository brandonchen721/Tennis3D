using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Tennis3D.Client;

/// <summary>Small dependency-free bitmap HUD so the game has readable UI without a Content Pipeline font asset.</summary>
public sealed class HudRenderer : IDisposable
{
    private readonly SpriteBatch batch;
    private readonly Texture2D pixel;

    public HudRenderer(GraphicsDevice device)
    {
        batch = new SpriteBatch(device);
        pixel = new Texture2D(device, 1, 1);
        pixel.SetData(new[] { Color.White });
    }

    public void Begin() => batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
                                       DepthStencilState.None, RasterizerState.CullNone);
    public void End() => batch.End();

    public void Fill(Rectangle rectangle, Color color) => batch.Draw(pixel, rectangle, color);

    public void Border(Rectangle rectangle, int thickness, Color color)
    {
        Fill(new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, thickness), color);
        Fill(new Rectangle(rectangle.X, rectangle.Bottom - thickness, rectangle.Width, thickness), color);
        Fill(new Rectangle(rectangle.X, rectangle.Y, thickness, rectangle.Height), color);
        Fill(new Rectangle(rectangle.Right - thickness, rectangle.Y, thickness, rectangle.Height), color);
    }

    public void Panel(Rectangle rectangle, Color fill, Color border)
    {
        Fill(rectangle, fill);
        Border(rectangle, 2, border);
    }

    public void Text(string text, Vector2 position, int scale, Color color)
    {
        int x = (int)position.X;
        int y = (int)position.Y;
        int originX = x;
        foreach (char raw in text.ToUpperInvariant())
        {
            if (raw == '\n')
            {
                x = originX;
                y += 8 * scale;
                continue;
            }
            string[] glyph = Glyph(raw);
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < 5; col++)
                    if (glyph[row][col] == '1')
                        Fill(new Rectangle(x + col * scale, y + row * scale, scale, scale), color);
            x += 6 * scale;
        }
    }

    public int Measure(string text, int scale)
    {
        int max = 0;
        foreach (string line in text.Split('\n')) max = Math.Max(max, line.Length * 6 * scale);
        return max;
    }

    public void Cross(int x, int y, int radius, Color color)
    {
        Fill(new Rectangle(x - radius, y - 1, radius * 2 + 1, 3), color);
        Fill(new Rectangle(x - 1, y - radius, 3, radius * 2 + 1), color);
    }

    private static string[] Glyph(char c) => c switch
    {
        'A' => G("01110","10001","10001","11111","10001","10001","10001"),
        'B' => G("11110","10001","10001","11110","10001","10001","11110"),
        'C' => G("01111","10000","10000","10000","10000","10000","01111"),
        'D' => G("11110","10001","10001","10001","10001","10001","11110"),
        'E' => G("11111","10000","10000","11110","10000","10000","11111"),
        'F' => G("11111","10000","10000","11110","10000","10000","10000"),
        'G' => G("01111","10000","10000","10111","10001","10001","01111"),
        'H' => G("10001","10001","10001","11111","10001","10001","10001"),
        'I' => G("11111","00100","00100","00100","00100","00100","11111"),
        'J' => G("00111","00010","00010","00010","10010","10010","01100"),
        'K' => G("10001","10010","10100","11000","10100","10010","10001"),
        'L' => G("10000","10000","10000","10000","10000","10000","11111"),
        'M' => G("10001","11011","10101","10101","10001","10001","10001"),
        'N' => G("10001","11001","10101","10011","10001","10001","10001"),
        'O' => G("01110","10001","10001","10001","10001","10001","01110"),
        'P' => G("11110","10001","10001","11110","10000","10000","10000"),
        'Q' => G("01110","10001","10001","10001","10101","10010","01101"),
        'R' => G("11110","10001","10001","11110","10100","10010","10001"),
        'S' => G("01111","10000","10000","01110","00001","00001","11110"),
        'T' => G("11111","00100","00100","00100","00100","00100","00100"),
        'U' => G("10001","10001","10001","10001","10001","10001","01110"),
        'V' => G("10001","10001","10001","10001","10001","01010","00100"),
        'W' => G("10001","10001","10001","10101","10101","10101","01010"),
        'X' => G("10001","10001","01010","00100","01010","10001","10001"),
        'Y' => G("10001","10001","01010","00100","00100","00100","00100"),
        'Z' => G("11111","00001","00010","00100","01000","10000","11111"),
        '0' => G("01110","10001","10011","10101","11001","10001","01110"),
        '1' => G("00100","01100","00100","00100","00100","00100","01110"),
        '2' => G("01110","10001","00001","00010","00100","01000","11111"),
        '3' => G("11110","00001","00001","01110","00001","00001","11110"),
        '4' => G("00010","00110","01010","10010","11111","00010","00010"),
        '5' => G("11111","10000","10000","11110","00001","00001","11110"),
        '6' => G("01110","10000","10000","11110","10001","10001","01110"),
        '7' => G("11111","00001","00010","00100","01000","01000","01000"),
        '8' => G("01110","10001","10001","01110","10001","10001","01110"),
        '9' => G("01110","10001","10001","01111","00001","00001","01110"),
        ':' => G("00000","00100","00100","00000","00100","00100","00000"),
        '-' => G("00000","00000","00000","11111","00000","00000","00000"),
        '/' => G("00001","00010","00010","00100","01000","01000","10000"),
        '.' => G("00000","00000","00000","00000","00000","00110","00110"),
        '+' => G("00000","00100","00100","11111","00100","00100","00000"),
        '%' => G("11001","11010","00100","01000","10110","00110","00000"),
        '?' => G("01110","10001","00001","00010","00100","00000","00100"),
        '!' => G("00100","00100","00100","00100","00100","00000","00100"),
        ' ' => G("00000","00000","00000","00000","00000","00000","00000"),
        _ => G("01110","10001","00001","00010","00100","00000","00100")
    };

    private static string[] G(params string[] rows) => rows;

    public void Dispose()
    {
        batch.Dispose();
        pixel.Dispose();
    }
}
