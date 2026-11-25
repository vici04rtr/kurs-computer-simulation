using System;
using System.Threading;
using System.Threading.Tasks;

namespace MultithreadingBenchmark.Simulation
{
    /// <summary>Agententyp: langsame "Walker" und schnelle "Runner" (Zombie-Horde).</summary>
    public enum AgentType : byte
    {
        Walker = 0,
        Runner = 1
    }

    /// <summary>
    /// Zustand eines Agenten (Position, Geschwindigkeit, Typ). Kompakter Werttyp (20 Byte) für Cache-Lokalität.
    /// </summary>
    public struct Agent
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public AgentType Type;

        public Agent(float x, float y, float vx, float vy, AgentType type)
        {
            X = x;
            Y = y;
            Vx = vx;
            Vy = vy;
            Type = type;
        }
    }

    /// <summary>Kreishindernis auf der Karte (Weltkoordinaten).</summary>
    public readonly record struct CircleObstacle(float X, float Y, float Radius);

    /// <summary>
    /// Track B: Schwarm- &amp; Zombie-Horden-Simulation mit striktem Double-Buffering.
    ///
    /// Zeitschritt:
    ///   1. Spatial Grid aus <see cref="StateCurrent"/> aufbauen (Counting Sort, deterministisch).
    ///   2. Jeder Agent i LIEST ausschließlich aus StateCurrent[] (eigener Zustand + Nachbarn j)
    ///      und SCHREIBT ausschließlich nach StateNext[i]  =&gt; keine Race Conditions / kein Tearing.
    ///   3. Nach der Schleife werden die Array-Referenzen getauscht (Swap).
    ///
    /// Da jeder Index exakt von einem Thread geschrieben wird und alle Summationen pro Agent in fester
    /// Reihenfolge laufen, liefern StepSequential und StepParallel bitgleiche Ergebnisse (siehe Unit-Tests).
    /// Wird ein Schritt abgebrochen, bleibt StateCurrent unverändert konsistent (StateNext ist nur Scratch).
    /// </summary>
    public sealed class SwarmField
    {
        public const float WorldSize = 1000f;
        public const float RepulsionRadius = 6f;

        private const int ChunkSize = 256;               // Agenten pro Parallel.For-Iteration (weniger Delegate-Overhead)
        private const float InvCell = 1.0f / RepulsionRadius;

        // ---- Modellparameter (pro Instanz einstellbar) -------------------------------------------------
        public float WalkerMaxSpeed { get; set; } = 22f;
        public float RunnerMaxSpeed { get; set; } = 45f;
        public float WalkerAccel { get; set; } = 30f;
        public float RunnerAccel { get; set; } = 60f;
        public float Damping { get; set; } = 0.6f;          // 1/s, lineare Geschwindigkeitsdämpfung
        public float RepulsionStrength { get; set; } = 220f;
        public float SwirlStrength { get; set; } = 14f;     // Wirbelkraft um das Ziel
        public float SwirlRadius { get; set; } = 200f;
        public float ObstacleMargin { get; set; } = 12f;    // Reichweite der weichen Hindernis-Umgehung
        public float ObstacleAvoid { get; set; } = 120f;
        public float Restitution { get; set; } = 0.5f;      // Abprallen an Kreishindernissen

        // ---- Zustand -------------------------------------------------------------------------------------
        private Agent[] _current;
        private Agent[] _next;

        private readonly int _cols;
        private readonly int _cellCount;
        private readonly int[] _agentCell;
        private readonly int[] _cellStart;   // Länge cellCount + 1
        private readonly int[] _cursor;      // Länge cellCount (Scratch für Counting Sort)
        private readonly int[] _cellItems;   // Agentenindizes, nach Zelle sortiert

        /// <summary>Aktueller (lesbarer) Zustand. Nur von dem Thread lesen, der auch steppt, bzw. nach Schrittende.</summary>
        public Agent[] StateCurrent => _current;

        /// <summary>Scratch-Puffer des nächsten Schritts (nur zu Dokumentations-/Testzwecken sichtbar).</summary>
        public Agent[] StateNext => _next;

        public int Count => _current.Length;
        public CircleObstacle[] Obstacles { get; }
        public float TargetX { get; private set; } = WorldSize / 2f;
        public float TargetY { get; private set; } = WorldSize / 2f;
        public long StepCount { get; private set; }

        public SwarmField(int count, int seed = 42, CircleObstacle[]? obstacles = null)
            : this(count, obstacles)
        {
            Initialize(seed);
        }

        private SwarmField(int count, CircleObstacle[]? obstacles)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            Obstacles = obstacles ?? DefaultObstacles();
            _current = new Agent[count];
            _next = new Agent[count];

            _cols = (int)Math.Ceiling(WorldSize / RepulsionRadius);
            _cellCount = _cols * _cols;
            _agentCell = new int[count];
            _cellStart = new int[_cellCount + 1];
            _cursor = new int[_cellCount];
            _cellItems = new int[count];
        }

        public static CircleObstacle[] DefaultObstacles() => new[]
        {
            new CircleObstacle(250f, 250f, 55f),
            new CircleObstacle(750f, 250f, 45f),
            new CircleObstacle(250f, 750f, 50f),
            new CircleObstacle(750f, 750f, 60f),
            new CircleObstacle(500f, 150f, 35f),
            new CircleObstacle(500f, 850f, 35f),
            new CircleObstacle(150f, 500f, 30f),
            new CircleObstacle(850f, 500f, 30f)
        };

        /// <summary>Deterministische Initialisierung (30 % Runner, Rest Walker, nicht in Hindernissen).</summary>
        public void Initialize(int seed = 42, double runnerFraction = 0.30)
        {
            var rnd = new Random(seed);
            for (int i = 0; i < _current.Length; i++)
            {
                float x = 0f, y = 0f;
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    x = (float)(rnd.NextDouble() * WorldSize);
                    y = (float)(rnd.NextDouble() * WorldSize);
                    if (!InsideAnyObstacle(x, y, 2f)) break;
                }

                _current[i] = new Agent(
                    x, y,
                    (float)(rnd.NextDouble() * 10.0 - 5.0),
                    (float)(rnd.NextDouble() * 10.0 - 5.0),
                    rnd.NextDouble() < runnerFraction ? AgentType.Runner : AgentType.Walker);
            }

            Array.Clear(_next);
            StepCount = 0;
        }

        private bool InsideAnyObstacle(float x, float y, float margin)
        {
            foreach (var o in Obstacles)
            {
                float dx = x - o.X, dy = y - o.Y;
                float r = o.Radius + margin;
                if (dx * dx + dy * dy < r * r) return true;
            }
            return false;
        }

        /// <summary>Setzt den Spieler-Zielpunkt P_target (Weltkoordinaten). Nur zwischen Schritten aufrufen.</summary>
        public void SetTarget(double x, double y)
        {
            TargetX = (float)Math.Clamp(x, 0.0, WorldSize);
            TargetY = (float)Math.Clamp(y, 0.0, WorldSize);
        }

        /// <summary>Tiefe Kopie (Zustand, Ziel, Parameter) – z. B. für Referenzmessungen oder Äquivalenztests.</summary>
        public SwarmField Clone()
        {
            var copy = new SwarmField(Count, (CircleObstacle[])Obstacles.Clone())
            {
                WalkerMaxSpeed = WalkerMaxSpeed,
                RunnerMaxSpeed = RunnerMaxSpeed,
                WalkerAccel = WalkerAccel,
                RunnerAccel = RunnerAccel,
                Damping = Damping,
                RepulsionStrength = RepulsionStrength,
                SwirlStrength = SwirlStrength,
                SwirlRadius = SwirlRadius,
                ObstacleMargin = ObstacleMargin,
                ObstacleAvoid = ObstacleAvoid,
                Restitution = Restitution
            };
            Array.Copy(_current, copy._current, Count);
            copy.TargetX = TargetX;
            copy.TargetY = TargetY;
            copy.StepCount = StepCount;
            return copy;
        }

        // ====================================================================
        // Zeitschritt
        // ====================================================================

        /// <summary>Sequentieller Zeitschritt (1 Thread) – Referenz für Speedup-Messungen.</summary>
        public void StepSequential(float dt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BuildGrid(null, cancellationToken);

            float tx = TargetX, ty = TargetY;
            Agent[] cur = _current;
            Agent[] nxt = _next;

            for (int i = 0; i < cur.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                UpdateAgent(i, dt, tx, ty, cur, nxt);
            }

            Swap();
        }

        /// <summary>
        /// Paralleler Zeitschritt mit <see cref="Parallel.For(int,int,ParallelOptions,Action{int})"/>.
        /// Lesen nur aus StateCurrent, Schreiben nur in StateNext[i] (disjunkte Indizes je Chunk).
        /// </summary>
        public void StepParallel(float dt, int maxDegreeOfParallelism = -1, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var options = new ParallelOptions { CancellationToken = cancellationToken };
            if (maxDegreeOfParallelism > 0)
            {
                options.MaxDegreeOfParallelism = maxDegreeOfParallelism;
            }

            BuildGrid(options, cancellationToken);

            float tx = TargetX, ty = TargetY;
            Agent[] cur = _current;
            Agent[] nxt = _next;
            int n = cur.Length;
            int chunks = (n + ChunkSize - 1) / ChunkSize;

            Parallel.For(0, chunks, options, c =>
            {
                int from = c * ChunkSize;
                int to = Math.Min(from + ChunkSize, n);
                for (int i = from; i < to; i++)
                {
                    UpdateAgent(i, dt, tx, ty, cur, nxt);
                }
            });

            Swap();
        }

        /// <summary>Referenztausch Current &lt;-&gt; Next (nach Abschluss der Schleife, also single-threaded).</summary>
        private void Swap()
        {
            (_current, _next) = (_next, _current);
            StepCount++;
        }

        // ====================================================================
        // Spatial Grid (Uniform Grid, Zellgröße = Repulsionsradius, Counting Sort)
        // ====================================================================

        private int CellCoord(float v)
        {
            int c = (int)(v * InvCell);
            if (c < 0) return 0;
            return c >= _cols ? _cols - 1 : c;
        }

        /// <summary>
        /// Baut das Grid aus StateCurrent. Zellindex-Berechnung ist parallelisierbar; der Counting Sort
        /// läuft bewusst seriell in Indexreihenfolge (deterministisch) – das ist der inhärent serielle
        /// Codeanteil s im Amdahl-Modell dieser Simulation.
        /// </summary>
        private void BuildGrid(ParallelOptions? options, CancellationToken ct)
        {
            Agent[] cur = _current;
            int n = cur.Length;
            int[] agentCell = _agentCell;

            if (options == null)
            {
                for (int i = 0; i < n; i++)
                {
                    agentCell[i] = CellCoord(cur[i].Y) * _cols + CellCoord(cur[i].X);
                }
            }
            else
            {
                int chunks = (n + ChunkSize - 1) / ChunkSize;
                Parallel.For(0, chunks, options, c =>
                {
                    int from = c * ChunkSize;
                    int to = Math.Min(from + ChunkSize, n);
                    for (int i = from; i < to; i++)
                    {
                        agentCell[i] = CellCoord(cur[i].Y) * _cols + CellCoord(cur[i].X);
                    }
                });
            }

            ct.ThrowIfCancellationRequested();

            Array.Clear(_cellStart);
            for (int i = 0; i < n; i++)
            {
                _cellStart[agentCell[i] + 1]++;
            }
            for (int c = 0; c < _cellCount; c++)
            {
                _cellStart[c + 1] += _cellStart[c];
            }
            Array.Copy(_cellStart, _cursor, _cellCount);
            for (int i = 0; i < n; i++)
            {
                _cellItems[_cursor[agentCell[i]]++] = i;
            }
        }

        // ====================================================================
        // Agenten-Update (identischer Code für sequentiell und parallel)
        // ====================================================================

        private void UpdateAgent(int i, float dt, float tx, float ty, Agent[] cur, Agent[] nxt)
        {
            Agent a = cur[i];
            bool runner = a.Type == AgentType.Runner;
            float accel = runner ? RunnerAccel : WalkerAccel;
            float vmax = runner ? RunnerMaxSpeed : WalkerMaxSpeed;

            float ax = 0f, ay = 0f;

            // 1) Zielverfolgung (Attraktion zu P_target, nahe am Ziel sanft abgeschwächt)
            float dx = tx - a.X;
            float dy = ty - a.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy) + 1e-3f;
            float pull = accel * MathF.Min(1f, dist / 25f);
            ax += dx / dist * pull;
            ay += dy / dist * pull;

            // 1b) Wirbelkraft (tangential) – verhindert ein Kollabieren der Horde auf einen Punkt
            if (dist < SwirlRadius)
            {
                float sw = SwirlStrength * (1f - dist / SwirlRadius);
                ax += -dy / dist * sw;
                ay += dx / dist * sw;
            }

            // 2) Lokale Abstoßung von Nachbarn (3x3 Zellen des Spatial Grid, je Zeile zusammenhängend)
            const float r = RepulsionRadius;
            const float r2 = r * r;
            int cx = CellCoord(a.X);
            int cy = CellCoord(a.Y);
            int x0 = cx > 0 ? cx - 1 : 0;
            int x1 = cx < _cols - 1 ? cx + 1 : _cols - 1;
            int y0 = cy > 0 ? cy - 1 : 0;
            int y1 = cy < _cols - 1 ? cy + 1 : _cols - 1;

            for (int gy = y0; gy <= y1; gy++)
            {
                int rowBase = gy * _cols;
                int kFrom = _cellStart[rowBase + x0];
                int kTo = _cellStart[rowBase + x1 + 1];
                for (int k = kFrom; k < kTo; k++)
                {
                    int j = _cellItems[k];
                    if (j == i) continue;

                    Agent b = cur[j];
                    float ox = a.X - b.X;
                    float oy = a.Y - b.Y;
                    float d2 = ox * ox + oy * oy;
                    if (d2 >= r2) continue;

                    if (d2 < 1e-8f)
                    {
                        // Deckungsgleiche Agenten: deterministisch entlang x trennen (Reihenfolge nach Index)
                        ox = i < j ? 0.01f : -0.01f;
                        oy = 0f;
                        d2 = ox * ox;
                    }

                    float d = MathF.Sqrt(d2);
                    float k2 = RepulsionStrength * (1f - d / r) / d;
                    ax += ox * k2;
                    ay += oy * k2;
                }
            }

            // 3) Weiche Hindernis-Umgehung (radial + tangential in Richtung Ziel)
            CircleObstacle[] obs = Obstacles;
            for (int q = 0; q < obs.Length; q++)
            {
                CircleObstacle o = obs[q];
                float ox = a.X - o.X;
                float oy = a.Y - o.Y;
                float d = MathF.Sqrt(ox * ox + oy * oy) + 1e-4f;
                float gap = d - o.Radius;
                if (gap < ObstacleMargin)
                {
                    float push = ObstacleAvoid * (1f - MathF.Max(gap, 0f) / ObstacleMargin);
                    float nxr = ox / d, nyr = oy / d;
                    float tgx = -nyr, tgy = nxr;
                    if (tgx * dx + tgy * dy < 0f)
                    {
                        tgx = -tgx;
                        tgy = -tgy;
                    }
                    ax += (nxr + tgx) * push;
                    ay += (nyr + tgy) * push;
                }
            }

            // 4) Integration, Dämpfung, Geschwindigkeitsbegrenzung ||v|| <= v_max
            float vx = a.Vx + dt * ax;
            float vy = a.Vy + dt * ay;
            float damp = MathF.Max(0f, 1f - Damping * dt);
            vx *= damp;
            vy *= damp;
            float sp2 = vx * vx + vy * vy;
            if (sp2 > vmax * vmax)
            {
                float s = vmax / MathF.Sqrt(sp2);
                vx *= s;
                vy *= s;
            }

            float nx = a.X + dt * vx;
            float ny = a.Y + dt * vy;

            // 5) Hartes Abprallen an Kreishindernissen
            for (int q = 0; q < obs.Length; q++)
            {
                CircleObstacle o = obs[q];
                float ox = nx - o.X;
                float oy = ny - o.Y;
                float d2 = ox * ox + oy * oy;
                if (d2 < o.Radius * o.Radius)
                {
                    float d = MathF.Sqrt(d2) + 1e-6f;
                    float nxr = ox / d, nyr = oy / d;
                    nx = o.X + nxr * (o.Radius + 0.01f);
                    ny = o.Y + nyr * (o.Radius + 0.01f);
                    float vn = vx * nxr + vy * nyr;
                    if (vn < 0f)
                    {
                        vx -= (1f + Restitution) * vn * nxr;
                        vy -= (1f + Restitution) * vn * nyr;
                    }
                }
            }

            // 6) Abprallen an den Kartenrändern
            if (nx < 0f) { nx = -nx; vx = -vx; }
            else if (nx > WorldSize) { nx = 2f * WorldSize - nx; vx = -vx; }
            if (ny < 0f) { ny = -ny; vy = -vy; }
            else if (ny > WorldSize) { ny = 2f * WorldSize - ny; vy = -vy; }
            nx = Math.Clamp(nx, 0f, WorldSize);
            ny = Math.Clamp(ny, 0f, WorldSize);

            // Exklusiver Schreibzugriff auf StateNext[i]
            nxt[i] = new Agent(nx, ny, vx, vy, a.Type);
        }

        // ====================================================================
        // Rendering (reiner CPU-Code, darf im Hintergrund-Thread laufen – keine WPF-Objekte!)
        // ====================================================================

        private const uint ColorBackground = 0xFF0E1117u;
        private const uint ColorWalker = 0xFF66BB6Au;
        private const uint ColorRunner = 0xFFFF7043u;
        private const uint ColorObstacle = 0xFF3A3F4Bu;
        private const uint ColorObstacleEdge = 0xFF8A93A6u;
        private const uint ColorTarget = 0xFF00E5FFu;

        /// <summary>Zeichnet Hindernisse, Agenten und den Spieler-Zielpunkt in einen BGRA32-Pixelpuffer.</summary>
        public void RenderToPixelBuffer(uint[] pixelBuffer, int width, int height)
        {
            Array.Fill(pixelBuffer, ColorBackground);

            float sx = (width - 1) / WorldSize;
            float sy = (height - 1) / WorldSize;

            // Hindernisse (gefüllte Kreise mit Rand)
            foreach (var o in Obstacles)
            {
                float cxp = o.X * sx, cyp = o.Y * sy;
                float rpx = o.Radius * sx, rpy = o.Radius * sy;
                int xMin = Math.Max(0, (int)(cxp - rpx) - 1), xMax = Math.Min(width - 1, (int)(cxp + rpx) + 1);
                int yMin = Math.Max(0, (int)(cyp - rpy) - 1), yMax = Math.Min(height - 1, (int)(cyp + rpy) + 1);
                for (int py = yMin; py <= yMax; py++)
                {
                    for (int px = xMin; px <= xMax; px++)
                    {
                        float ux = (px - cxp) / rpx;
                        float uy = (py - cyp) / rpy;
                        float u2 = ux * ux + uy * uy;
                        if (u2 <= 1f)
                        {
                            pixelBuffer[py * width + px] = u2 > 0.88f ? ColorObstacleEdge : ColorObstacle;
                        }
                    }
                }
            }

            // Agenten
            Agent[] cur = _current;
            for (int i = 0; i < cur.Length; i++)
            {
                int px = (int)(cur[i].X * sx);
                int py = (int)(cur[i].Y * sy);
                if ((uint)px < (uint)width && (uint)py < (uint)height)
                {
                    pixelBuffer[py * width + px] = cur[i].Type == AgentType.Runner ? ColorRunner : ColorWalker;
                }
            }

            // Spieler-Zielpunkt (Fadenkreuz)
            int tx = (int)Math.Clamp(TargetX * sx, 6f, width - 7f);
            int ty = (int)Math.Clamp(TargetY * sy, 6f, height - 7f);
            for (int d = -6; d <= 6; d++)
            {
                pixelBuffer[ty * width + (tx + d)] = ColorTarget;
                pixelBuffer[(ty + d) * width + tx] = ColorTarget;
            }
        }
    }
}
