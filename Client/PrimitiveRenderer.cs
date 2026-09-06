using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Tennis3D.Client;

public sealed class PrimitiveRenderer : IDisposable
{
    private readonly GraphicsDevice gd;
    private readonly BasicEffect fx;
    private readonly RasterizerState solid = new() { CullMode = CullMode.None };

    public PrimitiveRenderer(GraphicsDevice gd)
    {
        this.gd = gd;
        fx = new BasicEffect(gd)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            FogEnabled = false
        };
    }

    public void Begin(Matrix view, Matrix projection)
    {
        fx.View = view;
        fx.Projection = projection;
        gd.RasterizerState = solid;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.BlendState = BlendState.Opaque;
        gd.SamplerStates[0] = SamplerState.LinearClamp;
    }

    public void Box(Vector3 center, Vector3 size, Color color) => Box(center, size, color, Matrix.Identity);

    public void Box(Vector3 center, Vector3 size, Color color, Matrix rotation)
    {
        var h = size / 2f;
        Vector3[] p =
        {
            new(-h.X,-h.Y,-h.Z), new(h.X,-h.Y,-h.Z), new(h.X,h.Y,-h.Z), new(-h.X,h.Y,-h.Z),
            new(-h.X,-h.Y,h.Z),  new(h.X,-h.Y,h.Z),  new(h.X,h.Y,h.Z),  new(-h.X,h.Y,h.Z)
        };
        for (int n = 0; n < p.Length; n++) p[n] = Vector3.Transform(p[n], rotation) + center;
        int[] i = { 0,1,2,0,2,3, 5,4,7,5,7,6, 4,0,3,4,3,7, 1,5,6,1,6,2, 3,2,6,3,6,7, 4,5,1,4,1,0 };
        Draw(p, i, color);
    }

    public void Sphere(Vector3 c, float r, Color color, int rings = 12, int seg = 20)
    {
        var v = new List<Vector3>();
        var idx = new List<int>();
        for (int y = 0; y <= rings; y++)
        {
            float phi = MathF.PI * y / rings;
            for (int x = 0; x <= seg; x++)
            {
                float th = MathF.Tau * x / seg;
                v.Add(c + r * new Vector3(MathF.Sin(phi) * MathF.Cos(th), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(th)));
            }
        }
        for (int y = 0; y < rings; y++)
        for (int x = 0; x < seg; x++)
        {
            int a = y * (seg + 1) + x, b = a + seg + 1;
            idx.AddRange(new[] { a,b,a+1, a+1,b,b+1 });
        }
        Draw(v.ToArray(), idx.ToArray(), color);
    }

    // A rectangular beam that works between any two 3D points.
    public void Beam(Vector3 a, Vector3 b, float width, Color color)
    {
        Vector3 delta = b - a;
        float length = delta.Length();
        if (length < 0.0001f) return;

        Vector3 direction = delta / length;
        Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.Up)) > 0.96f ? Vector3.Forward : Vector3.Up;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, direction));
        Vector3 correctedUp = Vector3.Normalize(Vector3.Cross(direction, right));
        var rotation = new Matrix(
            right.X, right.Y, right.Z, 0,
            correctedUp.X, correctedUp.Y, correctedUp.Z, 0,
            direction.X, direction.Y, direction.Z, 0,
            0, 0, 0, 1);
        Box((a + b) * 0.5f, new Vector3(width, width, length), color, rotation);
    }

    private void Draw(Vector3[] p, int[] idx, Color c)
    {
        var verts = p.Select(x => new VertexPositionColor(x, c)).ToArray();
        foreach (var pass in fx.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, idx, 0, idx.Length / 3);
        }
    }

    public void Dispose()
    {
        fx.Dispose();
        solid.Dispose();
    }
}
