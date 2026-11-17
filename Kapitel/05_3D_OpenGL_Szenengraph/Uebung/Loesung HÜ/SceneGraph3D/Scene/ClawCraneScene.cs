using System.Numerics;
using SharpGL;

namespace SceneGraph3D.Scene;

/// <summary>Phasen des vollautomatischen Greifzyklus (Leertaste).</summary>
public enum ClawPhase
{
    Idle,           // Handsteuerung möglich
    Lowering,       // 1. Klaue senkt sich ab (und öffnet sich)
    Closing,        // 2. Finger schließen sich
    Raising,        // 3. Klaue fährt nach ganz oben
    MovingToChute,  // 4a. Laufkatze fährt über den Auswurfschacht
    Opening         // 4b. Klaue öffnet sich -> Preis wird losgelassen
}

public enum PrizeState { OnFloor, Held, Falling, InChute }

/// <summary>Spielobjekt (bunter Würfel oder bunte Kugel) als Szenenknoten.</summary>
public sealed class Prize
{
    public SceneNode Node { get; }
    public bool IsSphere { get; }

    /// <summary>Höhe des Mittelpunkts über dem Boden im Ruhezustand [m].</summary>
    public float RestHeight { get; }

    public PrizeState State { get; set; } = PrizeState.OnFloor;
    public float VelocityY { get; set; }

    /// <summary>Versatz zum Greifer-Zentrum, solange der Preis gehalten wird (Klauen-lokal).</summary>
    public Vector3 HeldOffset { get; set; }

    public Prize(string name, bool isSphere, float restHeight, float r, float g, float b, float yawDeg)
    {
        IsSphere = isSphere;
        RestHeight = restHeight;
        Node = new SceneNode(name) { Ry = yawDeg };
        Node.RenderCustomGeometry = gl =>
        {
            gl.Color(r, g, b);
            if (IsSphere)
            {
                GlPrimitives.Sphere(gl, RestHeight, 20, 20);
            }
            else
            {
                // Skalierung nur lokal (Push/Pop) -> Normalen bleiben via GL_NORMALIZE korrekt
                float edge = RestHeight * 2.0f;
                GlPrimitives.Box(gl, 0, 0, 0, edge, edge, edge);
            }
        };
    }

    public Vector3 Position
    {
        get => new(Node.Tx, Node.Ty, Node.Tz);
        set { Node.Tx = value.X; Node.Ty = value.Y; Node.Tz = value.Z; }
    }
}

/// <summary>
/// Stufe 2 / Track B: 3D Arcade Claw Crane.
///
/// Hierarchischer Szenengraph (alle Transformationen laufen über den OpenGL-Matrix-Stack):
///
///   Gehäuse (_machineNode)
///    ├─ Preise (_prizes, solange sie am Boden liegen / fallen)
///    └─ X-Brücke (_bridgeNode, Tx = BridgeX)
///        └─ Laufkatze (_trolleyNode, Tz = TrolleyZ)
///            └─ Seilzug / Klauenkopf (_ropeNode, Ty = ClawY + HeadToCenter - BridgeY)
///                ├─ Greifer-Zentrum (_clawCenterNode)  ← gegriffene Preise hängen hier
///                └─ 3 x Fingerarm (Ry = i * 120°)
///                    └─ Fingergelenk (Tx = Hub-Radius, Rz = gamma)
/// </summary>
public sealed class ClawCraneScene
{
    // ------------------------------------------------------------------ Geometrie [m]
    public const float HalfX = 2.0f;          // Innenmaß des Glaskastens in X
    public const float HalfZ = 1.5f;          // Innenmaß des Glaskastens in Z
    public const float WallHeight = 2.8f;     // Höhe des Glaskastens
    public const float BridgeY = 2.65f;       // Höhe der Schienen / der X-Brücke
    public const float HeadToCenter = 0.30f;  // Abstand Klauenkopf -> Greifer-Zentrum
    public const float HubRadius = 0.12f;     // Abstand der Fingergelenke von der Klauenachse

    // ------------------------------------------------------------------ Achsgrenzen
    public const float MinX = -1.5f, MaxX = 1.5f;       // X-Brücke
    public const float MinZ = -1.0f, MaxZ = 1.0f;       // Laufkatze
    public const float MinClawY = 0.2f, MaxClawY = 2.0f; // Höhe des Greifer-Zentrums über dem Boden
    public const float MinGamma = 0.0f, MaxGamma = 45.0f; // Fingerwinkel [°] (0 = geschlossen, 45 = offen)

    // ------------------------------------------------------------------ Auswurfschacht
    public const float ChuteX = -1.3f;
    public const float ChuteZ = 0.8f;
    public const float ChuteHalf = 0.3f;
    public const float PitDepth = 0.6f;

    // ------------------------------------------------------------------ Greifmechanik
    public const float GrabRadius = 0.30f;        // r_grab [m]
    public const float GripClosedDeg = 8.0f;      // darunter gilt die Klaue als geschlossen
    public const float ReleaseDeg = 20.0f;        // darüber wird der Preis losgelassen

    // ------------------------------------------------------------------ Geschwindigkeiten
    private const float ManualSpeed = 1.0f;       // [m/s] Pfeiltasten
    private const float AutoHorizontalSpeed = 1.1f;
    private const float AutoVerticalSpeed = 0.9f;
    private const float GammaSpeed = 70.0f;       // [°/s]
    private const float Gravity = 9.81f;

    // ------------------------------------------------------------------ Knoten
    private readonly SceneNode _machineNode = new("Gehaeuse");
    private readonly SceneNode _bridgeNode = new("X-Bruecke");
    private readonly SceneNode _trolleyNode = new("Laufkatze");
    private readonly SceneNode _ropeNode = new("Seilzug");
    private readonly SceneNode _clawCenterNode = new("GreiferZentrum");
    private readonly SceneNode[] _fingerArmNodes = new SceneNode[3];
    private readonly SceneNode[] _fingerJointNodes = new SceneNode[3];

    private readonly List<Prize> _prizes = new();
    private Prize? _held;

    // ------------------------------------------------------------------ Zustand
    private float _bridgeX;
    private float _trolleyZ;
    private float _clawY = MaxClawY;
    private float _gammaDeg = MaxGamma;

    public ClawPhase Phase { get; private set; } = ClawPhase.Idle;
    public bool SpotlightEnabled { get; set; } = true;
    public IReadOnlyList<Prize> Prizes => _prizes;
    public Prize? HeldPrize => _held;

    public float BridgeX { get => _bridgeX; set => _bridgeX = Math.Clamp(value, MinX, MaxX); }
    public float TrolleyZ { get => _trolleyZ; set => _trolleyZ = Math.Clamp(value, MinZ, MaxZ); }
    public float ClawY { get => _clawY; set => _clawY = Math.Clamp(value, MinClawY, MaxClawY); }
    public float GammaDeg { get => _gammaDeg; set => _gammaDeg = Math.Clamp(value, MinGamma, MaxGamma); }

    /// <summary>Anzahl der Preise, die im Auswurfschacht gelandet sind.</summary>
    public int Score => _prizes.Count(p => p.State == PrizeState.InChute);

    public SceneNode Root => _machineNode;

    public ClawCraneScene()
    {
        BuildSceneGraph();
        Reset();
    }

    // =====================================================================================
    // Szenengraph (Arbeitspaket B.1)
    // =====================================================================================
    private void BuildSceneGraph()
    {
        _machineNode.AddChild(_bridgeNode);
        _bridgeNode.AddChild(_trolleyNode);
        _trolleyNode.AddChild(_ropeNode);
        _ropeNode.AddChild(_clawCenterNode);

        _machineNode.RenderCustomGeometry = DrawMachine;
        _bridgeNode.Ty = BridgeY;
        _bridgeNode.RenderCustomGeometry = DrawBridge;
        _trolleyNode.RenderCustomGeometry = DrawTrolley;
        _ropeNode.RenderCustomGeometry = DrawRopeAndHub;

        _clawCenterNode.Ty = -HeadToCenter;
        _clawCenterNode.RenderCustomGeometry = gl =>
        {
            // kleiner gelber Marker = Greifer-Zentrum (Bezugspunkt der Abstandsprüfung)
            gl.Disable(OpenGL.GL_LIGHTING);
            gl.Color(1.0f, 1.0f, 0.0f);
            GlPrimitives.Sphere(gl, 0.02f, 8, 8);
            gl.Enable(OpenGL.GL_LIGHTING);
        };

        for (int i = 0; i < 3; i++)
        {
            // Zwei Knoten pro Finger: der Arm dreht um die Klauenachse (120°-Teilung),
            // das Gelenk sitzt radial außen und schwenkt den Finger um gamma.
            var arm = new SceneNode($"FingerArm{i}") { Ry = i * 120.0f };
            var joint = new SceneNode($"FingerGelenk{i}") { Tx = HubRadius };
            joint.RenderCustomGeometry = DrawFinger;

            _ropeNode.AddChild(arm);
            arm.AddChild(joint);
            _fingerArmNodes[i] = arm;
            _fingerJointNodes[i] = joint;
        }
    }

    /// <summary>Preise neu verteilen, Klaue in Startposition (oben, offen) bringen.</summary>
    public void Reset()
    {
        _held = null;
        Phase = ClawPhase.Idle;

        foreach (var p in _prizes)
        {
            _machineNode.RemoveChild(p.Node);
            _clawCenterNode.RemoveChild(p.Node);
        }
        _prizes.Clear();

        // (x, z, Kugel?, Farbe) - alle Positionen liegen im Greifbereich und abseits des Schachts
        (float x, float z, bool sphere, float r, float g, float b)[] layout =
        {
            (-0.90f, -0.60f, false, 0.90f, 0.15f, 0.15f),
            (-0.30f, -0.85f, true,  0.15f, 0.55f, 0.95f),
            ( 0.40f, -0.70f, false, 0.95f, 0.80f, 0.10f),
            ( 1.10f, -0.45f, true,  0.70f, 0.20f, 0.85f),
            (-0.70f,  0.05f, true,  0.15f, 0.80f, 0.35f),
            ( 0.10f, -0.05f, false, 0.95f, 0.45f, 0.10f),
            ( 0.85f,  0.25f, false, 0.20f, 0.75f, 0.85f),
            ( 1.30f,  0.70f, true,  0.95f, 0.30f, 0.60f),
            ( 0.35f,  0.75f, true,  0.95f, 0.95f, 0.95f),
            (-0.35f,  0.70f, false, 0.30f, 0.35f, 0.95f),
        };

        for (int i = 0; i < layout.Length; i++)
        {
            var l = layout[i];
            float rest = l.sphere ? 0.11f : 0.10f;
            var prize = new Prize($"Preis{i}", l.sphere, rest, l.r, l.g, l.b, yawDeg: i * 37.0f)
            {
                Position = new Vector3(l.x, rest, l.z)
            };
            _prizes.Add(prize);
            _machineNode.AddChild(prize.Node);
        }

        _bridgeX = 0.0f;
        _trolleyZ = 0.0f;
        _clawY = MaxClawY;
        _gammaDeg = MaxGamma;
        ApplyTransforms();
    }

    private void ApplyTransforms()
    {
        _bridgeNode.Tx = _bridgeX;
        _trolleyNode.Tz = _trolleyZ;
        _ropeNode.Ty = _clawY + HeadToCenter - BridgeY;   // negativ: Klaue hängt unter der Laufkatze
        for (int i = 0; i < 3; i++)
            _fingerJointNodes[i].Rz = _gammaDeg;

        if (_held != null)
            _held.Position = _held.HeldOffset;            // lokal im Knoten _clawCenterNode
    }

    // =====================================================================================
    // Steuerung (Arbeitspaket B.2)
    // =====================================================================================

    /// <summary>Handsteuerung der Laufkatze in der horizontalen Ebene (nur im Leerlauf).</summary>
    public void MoveManual(float dirX, float dirZ, float dt)
    {
        if (Phase != ClawPhase.Idle) return;
        BridgeX += dirX * ManualSpeed * dt;
        TrolleyZ += dirZ * ManualSpeed * dt;
    }

    /// <summary>Startet den automatischen Greifzyklus (Leertaste).</summary>
    public bool StartCycle()
    {
        if (Phase != ClawPhase.Idle) return false;
        Phase = ClawPhase.Lowering;
        return true;
    }

    /// <summary>Simulationsschritt: Zyklus-Automat, Greiflogik, fallende Preise.</summary>
    public void Update(float dt)
    {
        dt = Math.Clamp(dt, 0.0f, 0.1f);

        UpdateCycle(dt);
        UpdateGrip(dt);
        UpdatePrizes(dt);
        ApplyTransforms();
    }

    private void UpdateCycle(float dt)
    {
        switch (Phase)
        {
            case ClawPhase.Lowering:
            {
                bool y = MoveToward(ref _clawY, MinClawY, AutoVerticalSpeed * dt);
                bool g = MoveToward(ref _gammaDeg, MaxGamma, GammaSpeed * dt);
                if (y && g) Phase = ClawPhase.Closing;
                break;
            }
            case ClawPhase.Closing:
                if (MoveToward(ref _gammaDeg, MinGamma, GammaSpeed * dt)) Phase = ClawPhase.Raising;
                break;

            case ClawPhase.Raising:
                if (MoveToward(ref _clawY, MaxClawY, AutoVerticalSpeed * dt)) Phase = ClawPhase.MovingToChute;
                break;

            case ClawPhase.MovingToChute:
            {
                bool x = MoveToward(ref _bridgeX, ChuteX, AutoHorizontalSpeed * dt);
                bool z = MoveToward(ref _trolleyZ, ChuteZ, AutoHorizontalSpeed * dt);
                if (x && z) Phase = ClawPhase.Opening;
                break;
            }
            case ClawPhase.Opening:
                if (MoveToward(ref _gammaDeg, MaxGamma, GammaSpeed * dt)) Phase = ClawPhase.Idle;
                break;
        }
    }

    /// <summary>Bewegt value um höchstens step auf target zu. true, wenn target erreicht ist.</summary>
    private static bool MoveToward(ref float value, float target, float step)
    {
        float diff = target - value;
        if (MathF.Abs(diff) <= step)
        {
            value = target;
            return true;
        }
        value += MathF.Sign(diff) * step;
        return false;
    }

    // =====================================================================================
    // Greifmechanik / Kollisions-Snap (Arbeitspaket B.3)
    // =====================================================================================

    /// <summary>Weltposition des Greifer-Zentrums, direkt aus der Szenengraph-Matrix.</summary>
    public Vector3 ClawCenterWorld() => _clawCenterNode.GetWorldPosition();

    /// <summary>Euklidischer 3D-Abstand d = ||p_claw - p_objekt|| zum nächsten liegenden Preis.</summary>
    public (Prize? Prize, float Distance) NearestPrize()
    {
        Vector3 claw = ClawCenterWorld();
        Prize? best = null;
        float bestD = float.MaxValue;
        foreach (var p in _prizes)
        {
            if (p.State != PrizeState.OnFloor) continue;
            float d = Vector3.Distance(claw, p.Position);
            if (d < bestD) { bestD = d; best = p; }
        }
        return (best, bestD);
    }

    private void UpdateGrip(float dt)
    {
        if (_held == null)
        {
            // Klaue geschlossen + Preis innerhalb des Greifradius -> Snap
            if (_gammaDeg <= GripClosedDeg)
            {
                var (prize, d) = NearestPrize();
                if (prize != null && d < GrabRadius) Grab(prize);
            }
        }
        else if (_gammaDeg >= ReleaseDeg)
        {
            Release();
        }
        else
        {
            // Preis gleitet weich in die Mitte der Klaue (exponentielle Annäherung)
            float k = 1.0f - MathF.Exp(-12.0f * dt);
            _held.HeldOffset = Vector3.Lerp(_held.HeldOffset, SnapTarget, k);
        }
    }

    private static readonly Vector3 SnapTarget = new(0.0f, -0.02f, 0.0f);

    private void Grab(Prize prize)
    {
        // Versatz relativ zum Greifer-Zentrum. Zwischen Gehäuse und Greifer-Zentrum gibt es nur
        // Translationen -> lokaler Versatz = Weltversatz.
        Vector3 offset = prize.Position - ClawCenterWorld();

        _machineNode.RemoveChild(prize.Node);
        _clawCenterNode.AddChild(prize.Node);   // Reparenting: Preis folgt nun der Klaue
        prize.HeldOffset = offset;
        prize.Position = offset;
        prize.State = PrizeState.Held;
        _held = prize;
    }

    private void Release()
    {
        var prize = _held!;
        Vector3 world = ClawCenterWorld() + prize.HeldOffset;

        _clawCenterNode.RemoveChild(prize.Node);
        _machineNode.AddChild(prize.Node);
        prize.Position = world;
        prize.VelocityY = 0.0f;
        prize.State = PrizeState.Falling;
        _held = null;
    }

    private static bool OverChute(Vector3 p) =>
        MathF.Abs(p.X - ChuteX) < ChuteHalf - 0.05f && MathF.Abs(p.Z - ChuteZ) < ChuteHalf - 0.05f;

    private void UpdatePrizes(float dt)
    {
        foreach (var p in _prizes)
        {
            if (p.State != PrizeState.Falling) continue;

            Vector3 pos = p.Position;
            bool inHole = OverChute(pos);

            p.VelocityY -= Gravity * dt;
            pos.Y += p.VelocityY * dt;

            float floorY = inHole ? -PitDepth + p.RestHeight : p.RestHeight;
            if (pos.Y <= floorY)
            {
                pos.Y = floorY;
                p.VelocityY = 0.0f;
                p.State = inHole ? PrizeState.InChute : PrizeState.OnFloor;
            }
            p.Position = pos;
        }
    }

    // =====================================================================================
    // Rendering
    // =====================================================================================

    /// <summary>Rendert Gehäuse, Mechanik und Preise (Szenengraph) und danach das transparente Glas.</summary>
    public void Render(OpenGL gl, double eyeX, double eyeY, double eyeZ)
    {
        _machineNode.Render(gl);
        DrawGlass(gl, eyeX, eyeY, eyeZ);
    }

    /// <summary>
    /// Arbeitspaket B.4: lokaler Spotlight (GL_LIGHT1) an der Decke, senkrecht nach unten.
    /// Muss nach dem Anwenden der Kamera aufgerufen werden, damit die Position in Weltkoordinaten gilt.
    /// </summary>
    public void ApplySpotlight(OpenGL gl)
    {
        if (!SpotlightEnabled)
        {
            gl.Disable(OpenGL.GL_LIGHT1);
            return;
        }

        gl.Enable(OpenGL.GL_LIGHT1);
        gl.Light(OpenGL.GL_LIGHT1, OpenGL.GL_POSITION, new float[] { 0.0f, WallHeight - 0.05f, 0.0f, 1.0f });
        gl.Light(OpenGL.GL_LIGHT1, OpenGL.GL_SPOT_DIRECTION, new float[] { 0.0f, -1.0f, 0.0f });
        gl.Light(OpenGL.GL_LIGHT1, OpenGL.GL_SPOT_CUTOFF, new float[] { 42.0f });
        gl.Light(OpenGL.GL_LIGHT1, OpenGL.GL_SPOT_EXPONENT, new float[] { 5.0f });
        gl.Light(OpenGL.GL_LIGHT1, OpenGL.GL_AMBIENT, new float[] { 0.0f, 0.0f, 0.0f, 1.0f });
        gl.Light(OpenGL.GL_LIGHT1, OpenGL.GL_DIFFUSE, new float[] { 1.0f, 0.93f, 0.75f, 1.0f });
        gl.Light(OpenGL.GL_LIGHT1, OpenGL.GL_SPECULAR, new float[] { 1.0f, 1.0f, 1.0f, 1.0f });
    }

    // --- Gehäuse (statisch) ---------------------------------------------------------------
    private void DrawMachine(OpenGL gl)
    {
        // Unterbau (Schrank), nur die vier Außenwände - die Oberseite ist der Spielboden mit Loch
        gl.Color(0.55f, 0.10f, 0.45f);
        float skirtH = 0.7f;
        GlPrimitives.Box(gl, 0, -skirtH / 2, HalfZ + 0.1f, 2 * HalfX + 0.4f, skirtH, 0.2f);
        GlPrimitives.Box(gl, 0, -skirtH / 2, -HalfZ - 0.1f, 2 * HalfX + 0.4f, skirtH, 0.2f);
        GlPrimitives.Box(gl, HalfX + 0.1f, -skirtH / 2, 0, 0.2f, skirtH, 2 * HalfZ);
        GlPrimitives.Box(gl, -HalfX - 0.1f, -skirtH / 2, 0, 0.2f, skirtH, 2 * HalfZ);

        DrawFloorWithHole(gl);
        DrawChute(gl);

        // Eckprofile
        gl.Color(0.90f, 0.75f, 0.20f);
        foreach (float sx in new[] { -1.0f, 1.0f })
            foreach (float sz in new[] { -1.0f, 1.0f })
                GlPrimitives.Box(gl, sx * HalfX, WallHeight / 2, sz * HalfZ, 0.12f, WallHeight, 0.12f);

        // Deckengehäuse
        gl.Color(0.40f, 0.08f, 0.35f);
        GlPrimitives.Box(gl, 0, WallHeight + 0.2f, 0, 2 * HalfX + 0.4f, 0.4f, 2 * HalfZ + 0.4f);

        // Längsschienen (entlang X) der X-Brücke
        gl.Color(0.75f, 0.77f, 0.80f);
        float railZ = HalfZ - 0.08f;
        GlPrimitives.Box(gl, 0, BridgeY, railZ, 2 * HalfX - 0.2f, 0.04f, 0.05f);
        GlPrimitives.Box(gl, 0, BridgeY, -railZ, 2 * HalfX - 0.2f, 0.04f, 0.05f);

        // Scheinwerfer-Fassung (selbstleuchtend dargestellt)
        gl.Disable(OpenGL.GL_LIGHTING);
        gl.Color(SpotlightEnabled ? 1.0f : 0.35f, SpotlightEnabled ? 0.95f : 0.35f, SpotlightEnabled ? 0.7f : 0.3f);
        gl.PushMatrix();
        gl.Translate(0.0f, WallHeight - 0.06f, 0.0f);
        GlPrimitives.Cylinder(gl, 0.16f, 0.06f, 20);
        gl.PopMatrix();
        gl.Enable(OpenGL.GL_LIGHTING);
    }

    /// <summary>Spielboden: fein unterteilt (0,2 m), damit der Spotlight per Vertex-Beleuchtung sichtbar wird.</summary>
    private static void DrawFloorWithHole(OpenGL gl)
    {
        const float cell = 0.2f;
        int nx = (int)MathF.Round(2 * HalfX / cell);
        int nz = (int)MathF.Round(2 * HalfZ / cell);
        const float eps = 1e-3f;

        gl.Color(0.62f, 0.62f, 0.68f);
        gl.Begin(OpenGL.GL_QUADS);
        gl.Normal(0.0f, 1.0f, 0.0f);
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < nz; j++)
            {
                float x0 = -HalfX + i * cell, x1 = x0 + cell;
                float z0 = -HalfZ + j * cell, z1 = z0 + cell;

                bool inHole = x0 >= ChuteX - ChuteHalf - eps && x1 <= ChuteX + ChuteHalf + eps &&
                              z0 >= ChuteZ - ChuteHalf - eps && z1 <= ChuteZ + ChuteHalf + eps;
                if (inHole) continue;

                gl.Vertex(x0, 0.0f, z1); gl.Vertex(x1, 0.0f, z1);
                gl.Vertex(x1, 0.0f, z0); gl.Vertex(x0, 0.0f, z0);
            }
        }
        gl.End();
    }

    /// <summary>Auswurfschacht: dunkle Grube unter dem Loch plus gelbe Umrandung.</summary>
    private static void DrawChute(OpenGL gl)
    {
        float x0 = ChuteX - ChuteHalf, x1 = ChuteX + ChuteHalf;
        float z0 = ChuteZ - ChuteHalf, z1 = ChuteZ + ChuteHalf;
        float d = -PitDepth;

        gl.Color(0.12f, 0.12f, 0.15f);
        GlPrimitives.Quad(gl, 0, 0, 1, x0, 0, z0, x0, d, z0, x1, d, z0, x1, 0, z0);   // Wand bei z0, Normale nach innen
        GlPrimitives.Quad(gl, 0, 0, -1, x0, 0, z1, x0, d, z1, x1, d, z1, x1, 0, z1);  // Wand bei z1
        GlPrimitives.Quad(gl, 1, 0, 0, x0, 0, z0, x0, d, z0, x0, d, z1, x0, 0, z1);   // Wand bei x0
        GlPrimitives.Quad(gl, -1, 0, 0, x1, 0, z0, x1, d, z0, x1, d, z1, x1, 0, z1);  // Wand bei x1
        GlPrimitives.Quad(gl, 0, 1, 0, x0, d, z0, x1, d, z0, x1, d, z1, x0, d, z1);   // Grubenboden

        gl.Color(0.95f, 0.80f, 0.10f);
        float w = 2 * ChuteHalf + 0.08f;
        GlPrimitives.Box(gl, ChuteX, 0.01f, z0 - 0.02f, w, 0.02f, 0.04f);
        GlPrimitives.Box(gl, ChuteX, 0.01f, z1 + 0.02f, w, 0.02f, 0.04f);
        GlPrimitives.Box(gl, x0 - 0.02f, 0.01f, ChuteZ, 0.04f, 0.02f, w);
        GlPrimitives.Box(gl, x1 + 0.02f, 0.01f, ChuteZ, 0.04f, 0.02f, w);
    }

    // --- Bewegliche Teile -------------------------------------------------------------------
    private static void DrawBridge(OpenGL gl)
    {
        // lokales System: Ursprung auf Schienenhöhe, Brücke spannt entlang Z
        float railZ = HalfZ - 0.08f;
        gl.Color(0.85f, 0.87f, 0.90f);
        GlPrimitives.Box(gl, 0, 0, 0, 0.10f, 0.10f, 2 * railZ);

        gl.Color(0.20f, 0.35f, 0.80f);
        GlPrimitives.Box(gl, 0, 0, railZ, 0.24f, 0.12f, 0.16f);
        GlPrimitives.Box(gl, 0, 0, -railZ, 0.24f, 0.12f, 0.16f);
    }

    private static void DrawTrolley(OpenGL gl)
    {
        gl.Color(0.15f, 0.60f, 0.35f);
        GlPrimitives.Box(gl, 0, 0, 0, 0.26f, 0.20f, 0.24f);
    }

    private void DrawRopeAndHub(OpenGL gl)
    {
        // Seil reicht von der Unterseite der Laufkatze (y = -0.1 im Katzensystem) bis zum Klauenkopf
        float ropeLen = -_ropeNode.Ty - 0.10f;

        gl.Color(0.20f, 0.20f, 0.22f);
        gl.PushMatrix();
        gl.Translate(0.0f, 0.04f, 0.0f);
        GlPrimitives.Cylinder(gl, 0.012f, MathF.Max(ropeLen, 0.01f), 8);
        gl.PopMatrix();

        // Klauenkopf (Nabe)
        gl.Color(0.55f, 0.57f, 0.60f);
        gl.PushMatrix();
        gl.Translate(0.0f, -0.05f, 0.0f);
        GlPrimitives.Cylinder(gl, HubRadius + 0.03f, 0.10f, 20);
        gl.PopMatrix();
    }

    /// <summary>Ein Greiffinger im Koordinatensystem seines Gelenks (Drehachse = lokale Z-Achse).</summary>
    private static void DrawFinger(OpenGL gl)
    {
        gl.Color(0.85f, 0.20f, 0.20f);

        // Oberer Schenkel: gerade nach unten
        GlPrimitives.Box(gl, 0, -0.11f, 0, 0.035f, 0.22f, 0.05f);

        // Unterer Schenkel: 40° nach innen abgewinkelt (Fingerspitze zeigt zur Klauenachse)
        gl.PushMatrix();
        gl.Translate(0.0f, -0.22f, 0.0f);
        gl.Rotate(-40.0f, 0.0f, 0.0f, 1.0f);
        GlPrimitives.Box(gl, 0, -0.09f, 0, 0.03f, 0.18f, 0.045f);
        gl.PopMatrix();

        // Gelenkbolzen
        gl.Color(0.9f, 0.9f, 0.9f);
        GlPrimitives.Sphere(gl, 0.03f, 10, 10);
    }

    /// <summary>
    /// Glaswände: halbtransparent, unbeleuchtet, von hinten nach vorn sortiert (Painter's Algorithm),
    /// damit Alpha-Blending ohne Depth-Mask-Tricks korrekt funktioniert.
    /// </summary>
    private static void DrawGlass(OpenGL gl, double ex, double ey, double ez)
    {
        // Wandmittelpunkte (Wand-ID: 0=+X, 1=-X, 2=+Z, 3=-Z)
        var walls = new (int Id, double Dist)[4];
        Vector3[] centers =
        {
            new(HalfX, WallHeight / 2, 0), new(-HalfX, WallHeight / 2, 0),
            new(0, WallHeight / 2, HalfZ), new(0, WallHeight / 2, -HalfZ)
        };
        for (int i = 0; i < 4; i++)
        {
            double dx = centers[i].X - ex, dy = centers[i].Y - ey, dz = centers[i].Z - ez;
            walls[i] = (i, dx * dx + dy * dy + dz * dz);
        }
        Array.Sort(walls, (a, b) => b.Dist.CompareTo(a.Dist));   // weit entfernte zuerst

        gl.Disable(OpenGL.GL_LIGHTING);
        gl.Enable(OpenGL.GL_BLEND);
        gl.BlendFunc(OpenGL.GL_SRC_ALPHA, OpenGL.GL_ONE_MINUS_SRC_ALPHA);
        gl.Color(0.55f, 0.80f, 1.0f, 0.13f);

        float h = WallHeight;
        foreach (var (id, _) in walls)
        {
            gl.Begin(OpenGL.GL_QUADS);
            switch (id)
            {
                case 0: gl.Vertex(HalfX, 0, -HalfZ); gl.Vertex(HalfX, h, -HalfZ); gl.Vertex(HalfX, h, HalfZ); gl.Vertex(HalfX, 0, HalfZ); break;
                case 1: gl.Vertex(-HalfX, 0, -HalfZ); gl.Vertex(-HalfX, h, -HalfZ); gl.Vertex(-HalfX, h, HalfZ); gl.Vertex(-HalfX, 0, HalfZ); break;
                case 2: gl.Vertex(-HalfX, 0, HalfZ); gl.Vertex(HalfX, 0, HalfZ); gl.Vertex(HalfX, h, HalfZ); gl.Vertex(-HalfX, h, HalfZ); break;
                default: gl.Vertex(-HalfX, 0, -HalfZ); gl.Vertex(HalfX, 0, -HalfZ); gl.Vertex(HalfX, h, -HalfZ); gl.Vertex(-HalfX, h, -HalfZ); break;
            }
            gl.End();
        }

        gl.Disable(OpenGL.GL_BLEND);
        gl.Enable(OpenGL.GL_LIGHTING);
    }
}
