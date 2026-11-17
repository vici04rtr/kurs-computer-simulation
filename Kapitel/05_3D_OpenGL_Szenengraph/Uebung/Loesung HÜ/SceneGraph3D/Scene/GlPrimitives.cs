using SharpGL;

namespace SceneGraph3D.Scene;

/// <summary>
/// Geometrie-Primitive der Fixed-Function-Pipeline (OpenGL 2.1) mit korrekten Flächennormalen.
/// Wird von Stufe 1 (SCARA) und Stufe 2 (Claw Crane) gemeinsam genutzt.
/// </summary>
public static class GlPrimitives
{
    /// <summary>Einheitswürfel (Kantenlänge 1, Mittelpunkt im Ursprung) mit Normalen je Seitenfläche.</summary>
    public static void Cube(OpenGL gl)
    {
        float s = 0.5f;
        gl.Begin(OpenGL.GL_QUADS);

        // Vorne
        gl.Normal(0.0f, 0.0f, 1.0f);
        gl.Vertex(-s, -s, s); gl.Vertex(s, -s, s); gl.Vertex(s, s, s); gl.Vertex(-s, s, s);

        // Hinten
        gl.Normal(0.0f, 0.0f, -1.0f);
        gl.Vertex(-s, -s, -s); gl.Vertex(-s, s, -s); gl.Vertex(s, s, -s); gl.Vertex(s, -s, -s);

        // Oben
        gl.Normal(0.0f, 1.0f, 0.0f);
        gl.Vertex(-s, s, -s); gl.Vertex(-s, s, s); gl.Vertex(s, s, s); gl.Vertex(s, s, -s);

        // Unten
        gl.Normal(0.0f, -1.0f, 0.0f);
        gl.Vertex(-s, -s, -s); gl.Vertex(s, -s, -s); gl.Vertex(s, -s, s); gl.Vertex(-s, -s, s);

        // Rechts
        gl.Normal(1.0f, 0.0f, 0.0f);
        gl.Vertex(s, -s, -s); gl.Vertex(s, s, -s); gl.Vertex(s, s, s); gl.Vertex(s, -s, s);

        // Links
        gl.Normal(-1.0f, 0.0f, 0.0f);
        gl.Vertex(-s, -s, -s); gl.Vertex(-s, -s, s); gl.Vertex(-s, s, s); gl.Vertex(-s, s, -s);

        gl.End();
    }

    /// <summary>
    /// Quader mit Mittelpunkt (cx, cy, cz) und Kantenlängen (sx, sy, sz).
    /// Die Skalierung wirkt nur lokal (Push/Pop) und verzerrt keine Kindknoten.
    /// </summary>
    public static void Box(OpenGL gl, float cx, float cy, float cz, float sx, float sy, float sz)
    {
        gl.PushMatrix();
        gl.Translate(cx, cy, cz);
        gl.Scale(sx, sy, sz);
        Cube(gl);
        gl.PopMatrix();
    }

    /// <summary>Kugel (Normalen = normierte Ortsvektoren).</summary>
    public static void Sphere(OpenGL gl, float radius, int slices = 16, int stacks = 16)
    {
        for (int i = 0; i < stacks; i++)
        {
            float phi1 = MathF.PI * (-0.5f + (float)i / stacks);
            float phi2 = MathF.PI * (-0.5f + (float)(i + 1) / stacks);

            gl.Begin(OpenGL.GL_QUAD_STRIP);
            for (int j = 0; j <= slices; j++)
            {
                float theta = 2.0f * MathF.PI * (float)j / slices;

                float x1 = MathF.Cos(phi1) * MathF.Cos(theta);
                float y1 = MathF.Sin(phi1);
                float z1 = MathF.Cos(phi1) * MathF.Sin(theta);

                float x2 = MathF.Cos(phi2) * MathF.Cos(theta);
                float y2 = MathF.Sin(phi2);
                float z2 = MathF.Cos(phi2) * MathF.Sin(theta);

                gl.Normal(x1, y1, z1);
                gl.Vertex(radius * x1, radius * y1, radius * z1);

                gl.Normal(x2, y2, z2);
                gl.Vertex(radius * x2, radius * y2, radius * z2);
            }
            gl.End();
        }
    }

    /// <summary>
    /// Zylinder entlang +Y (Boden bei y = 0, Deckel bei y = height) mit radialen Mantelnormalen
    /// und optionalen Deckflächen (Normalen +Y / -Y).
    /// </summary>
    public static void Cylinder(OpenGL gl, float radius, float height, int slices = 16, bool caps = true)
    {
        gl.Begin(OpenGL.GL_QUAD_STRIP);
        for (int i = 0; i <= slices; i++)
        {
            float theta = 2.0f * MathF.PI * i / slices;
            float nx = MathF.Cos(theta);
            float nz = MathF.Sin(theta);

            gl.Normal(nx, 0.0f, nz);
            gl.Vertex(radius * nx, 0.0f, radius * nz);
            gl.Vertex(radius * nx, height, radius * nz);
        }
        gl.End();

        if (!caps) return;

        for (int side = 0; side < 2; side++)
        {
            float y = side == 0 ? height : 0.0f;
            gl.Begin(OpenGL.GL_TRIANGLE_FAN);
            gl.Normal(0.0f, side == 0 ? 1.0f : -1.0f, 0.0f);
            gl.Vertex(0.0f, y, 0.0f);
            for (int i = 0; i <= slices; i++)
            {
                float theta = 2.0f * MathF.PI * i / slices;
                gl.Vertex(radius * MathF.Cos(theta), y, radius * MathF.Sin(theta));
            }
            gl.End();
        }
    }

    /// <summary>Einzelnes Rechteck mit expliziter Normale (4 Eckpunkte).</summary>
    public static void Quad(OpenGL gl, float nx, float ny, float nz,
        float x1, float y1, float z1, float x2, float y2, float z2,
        float x3, float y3, float z3, float x4, float y4, float z4)
    {
        gl.Begin(OpenGL.GL_QUADS);
        gl.Normal(nx, ny, nz);
        gl.Vertex(x1, y1, z1); gl.Vertex(x2, y2, z2); gl.Vertex(x3, y3, z3); gl.Vertex(x4, y4, z4);
        gl.End();
    }

    /// <summary>Koordinatensystem (X = Rot, Y = Grün, Z = Blau), unbeleuchtet.</summary>
    public static void Axes(OpenGL gl, float length)
    {
        gl.Disable(OpenGL.GL_LIGHTING);
        gl.LineWidth(2.5f);
        gl.Begin(OpenGL.GL_LINES);

        gl.Color(1.0f, 0.1f, 0.1f);
        gl.Vertex(0.0f, 0.0f, 0.0f);
        gl.Vertex(length, 0.0f, 0.0f);

        gl.Color(0.1f, 1.0f, 0.1f);
        gl.Vertex(0.0f, 0.0f, 0.0f);
        gl.Vertex(0.0f, length, 0.0f);

        gl.Color(0.1f, 0.4f, 1.0f);
        gl.Vertex(0.0f, 0.0f, 0.0f);
        gl.Vertex(0.0f, 0.0f, length);

        gl.End();
        gl.Enable(OpenGL.GL_LIGHTING);
    }

    /// <summary>Hallenboden-Gitter in der Ebene y = <paramref name="y"/>, unbeleuchtet.</summary>
    public static void FloorGrid(OpenGL gl, float size, float step, float y = 0.0f)
    {
        gl.Disable(OpenGL.GL_LIGHTING);
        gl.LineWidth(1.0f);
        gl.Color(0.2f, 0.2f, 0.2f);
        gl.Begin(OpenGL.GL_LINES);

        for (float v = -size; v <= size; v += step)
        {
            gl.Vertex(-size, y, v);
            gl.Vertex(size, y, v);

            gl.Vertex(v, y, -size);
            gl.Vertex(v, y, size);
        }

        gl.End();
        gl.Enable(OpenGL.GL_LIGHTING);
    }
}
