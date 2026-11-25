using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SandFireFDM.Simulation;

/// <summary>Layer 2: Materialzustand jedes Pixels.</summary>
public enum Material : byte
{
    Air = 0,
    Sand,
    Lava,        // Lava / Glut (flüssig, T > 800 °C)
    Stone,       // unbrennbar, statisch
    Wood,        // brennbar
    BurningWood, // brennendes Holz (Zwischenzustand, 5 s)
    Ash,         // unbrennbare Asche (Rückstand von Holz, rieselt wie Sand)
    Fire         // Doom-Fire-Partikel (steigt auf, kühlt zufällig ab)
}

public enum BoundaryMode
{
    /// <summary>Feste Randtemperatur (Dirichlet).</summary>
    DirichletFixed,
    /// <summary>Isolierter Rand (Neumann, dT/dn = 0).</summary>
    NeumannAdiabatic
}

public struct WorldStats
{
    public float MaxT;
    public float MinT;
    public int NonFiniteCount;
}

/// <summary>
/// Doppellagiges 200×200-Gitter:
///   Layer 1: Temperaturfeld (FDM, 5-Punkt-Stern, explizit, Double-Buffering)
///   Layer 2: Materialzustand (zellulärer Automat)
/// Alle Arrays sind flach (Index = y * Width + x) und werden nur einmal im Konstruktor allokiert.
/// </summary>
public sealed class SandWorld
{
    public const int Width = 200;
    public const int Height = 200;
    public const int CellCount = Width * Height;

    // ---------------- Materialkonstanten (Automatenregeln) ----------------
    public const float AmbientTemperature = 20f;
    public const float LavaPourTemperature = 1400f;
    public const float LavaSolidifyTemperature = 800f;
    public const float LavaContactCooling = 0.5f;   // °C pro Frame und Luft-Kontaktfläche
    public const float LavaSpreadProbability = 0.5f;
    public const float IgnitionTemperature = 280f;
    public const float BurnTemperature = 900f;
    public const float BurnDuration = 5f;     // s
    public const float FireSpawnProbability = 0.25f;
    public const float FireDecayMax = 20f;
    public const float FireExtinguishTemperature = 350f;
    public const float FireTrailFactor = 0.5f;
    public const float HeaterTemperature = 1200f;
    public const float DivergenceLimit = 5000f;

    // ---------------- Physikalische Parameter (live veränderbar) ----------------
    /// <summary>Gitterweite Δx = Δy [m].</summary>
    public float Dx { get; set; } = 0.005f;
    /// <summary>Temperaturleitfähigkeit a [m²/s] (Referenz: Luft).</summary>
    public float Alpha { get; set; } = 1.0e-4f;
    /// <summary>Zeitschritt Δt [s].</summary>
    public float Dt { get; set; } = 0.05f;
    /// <summary>Abkühlkoeffizient γ [1/s].</summary>
    public float Gamma { get; set; } = 0.1f;
    public BoundaryMode Boundary { get; set; } = BoundaryMode.DirichletFixed;

    /// <summary>Diffusionszahl s = a·Δt/Δx². Stabil nur für s ≤ 0,25.</summary>
    public float StabilityFactor => Alpha * Dt / (Dx * Dx);
    public bool IsStable => StabilityFactor <= 0.25f + 1e-5f;
    /// <summary>Δt_krit = Δx² / (4·a).</summary>
    public float CriticalDt => Dx * Dx / (4f * Math.Max(Alpha, 1e-12f));

    // Relative Temperaturleitfähigkeit je Material (<= 1, damit s der Luft das Kriterium bleibt).
    // Lava und Feuer sind "Wärmeträger" mit großer thermischer Trägheit.
    //                                    Air   Sand  Lava   Stone Wood  Burn  Ash   Fire
    private static readonly float[] AlphaFactor = { 1.0f, 0.5f, 0.003f, 0.7f, 0.1f, 0.1f, 0.2f, 0.02f };
    // Relative Abkühlung an die Umgebung
    private static readonly float[] CoolFactor = { 4.0f, 1.0f, 0.15f, 1.0f, 1.0f, 1.0f, 1.0f, 4.0f };

    // ---------------- Layer-Daten ----------------
    private float[] _tOld = new float[CellCount];
    private float[] _tNew = new float[CellCount];
    private readonly Material[] _mat = new Material[CellCount];
    private readonly float[] _burn = new float[CellCount];
    private readonly ushort[] _stamp = new ushort[CellCount];
    private readonly int[] _burnList = new int[CellCount];
    private int _burnCount;
    private uint _frame;
    private uint _rng = 2463534242u;

    public long StepCount { get; private set; }
    public long ParticleFrames => _frame;
    public int BurningCount => _burnCount;
    /// <summary>FDM-Schritt der ersten erkannten Instabilität (-1 = nie).</summary>
    public long FirstInstabilityStep { get; private set; } = -1;

    public float[] Temperature => _tOld;
    public Material[] Materials => _mat;

    public SandWorld(uint seed = 12345u)
    {
        Seed(seed);
        Reset();
    }

    public void Seed(uint seed) => _rng = seed == 0 ? 1u : seed;

    public void Reset()
    {
        Array.Fill(_mat, Material.Air);
        Array.Fill(_tOld, AmbientTemperature);
        Array.Fill(_tNew, AmbientTemperature);
        Array.Clear(_burn);
        Array.Clear(_stamp);
        _burnCount = 0;
        _frame = 0;
        StepCount = 0;
        FirstInstabilityStep = -1;
    }

    // ---------------- Zufall (xorshift32, allokationsfrei) ----------------
    private uint NextU()
    {
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return _rng = x;
    }
    private float NextF() => (NextU() & 0xFFFFFFu) * (1f / 16777216f);

    // =====================================================================
    //  LAYER 1: FDM-Diffusion
    //  T* = T^k + s_ij * (T_E + T_W + T_N + T_S - 4 T^k);  T^{k+1} = T* - γ_ij·Δt·(T* - T_Luft)
    //  Strikt T_old -> T_new (Double-Buffering), danach Pointer-Swap.
    // =====================================================================
    public void StepDiffusion()
    {
        float sBase = StabilityFactor;
        float coolBase = Gamma * Dt;
        float[] told = _tOld;
        float[] tnew = _tNew;
        Material[] mat = _mat;
        float[] aF = AlphaFactor;
        float[] cF = CoolFactor;

        for (int y = 1; y < Height - 1; y++)
        {
            int row = y * Width;
            for (int x = 1; x < Width - 1; x++)
            {
                int i = row + x;
                int m = (int)mat[i];
                float c = told[i];
                float laplace = told[i - 1] + told[i + 1] + told[i - Width] + told[i + Width] - 4f * c;

                // 1) Diffusion (5-Punkt-Stern, explizit)
                float tStar = c + sBase * aF[m] * laplace;
                // 2) Abkühlung auf den diffundierten Wert (Operator-Splitting):
                //    Stabilitätsgrenze bleibt exakt s <= 0.25.
                tnew[i] = tStar - coolBase * cF[m] * (tStar - AmbientTemperature);
            }
        }

        ApplyBoundary(tnew);

        // Dauer-Wärmequelle: brennendes Holz bleibt auf BurnTemperature
        for (int k = 0; k < _burnCount; k++)
        {
            int idx = _burnList[k];
            if (tnew[idx] < BurnTemperature) tnew[idx] = BurnTemperature;
        }

        (_tOld, _tNew) = (_tNew, _tOld);
        StepCount++;
    }

    private void ApplyBoundary(float[] g)
    {
        if (Boundary == BoundaryMode.DirichletFixed)
        {
            for (int x = 0; x < Width; x++)
            {
                g[x] = AmbientTemperature;
                g[(Height - 1) * Width + x] = AmbientTemperature;
            }
            for (int y = 0; y < Height; y++)
            {
                g[y * Width] = AmbientTemperature;
                g[y * Width + Width - 1] = AmbientTemperature;
            }
        }
        else
        {
            for (int x = 0; x < Width; x++)
            {
                g[x] = g[Width + x];
                g[(Height - 1) * Width + x] = g[(Height - 2) * Width + x];
            }
            for (int y = 0; y < Height; y++)
            {
                g[y * Width] = g[y * Width + 1];
                g[y * Width + Width - 1] = g[y * Width + Width - 2];
            }
        }
    }

    // =====================================================================
    //  LAYER 2: Zellulärer Automat (einmal pro Frame)
    // =====================================================================
    public void StepParticles(float frameDt)
    {
        _frame++;
        ushort stampNow = (ushort)_frame;
        bool leftToRight = (_frame & 1u) == 0;
        Material[] mat = _mat;
        float[] t = _tOld;
        _burnCount = 0;

        // ---- Pass 1: Schwerkraft (Sand, Asche, Lava), von unten nach oben ----
        // Die unterste Zeile wird mitgeprüft, damit Lava dort ebenfalls erstarren kann.
        for (int y = Height - 1; y >= 0; y--)
        {
            int row = y * Width;
            for (int k = 0; k < Width; k++)
            {
                int x = leftToRight ? k : Width - 1 - k;
                int i = row + x;
                Material m = mat[i];
                if (m != Material.Sand && m != Material.Ash && m != Material.Lava) continue;
                if (_stamp[i] == stampNow) continue;

                if (m == Material.Lava)
                {
                    if (CoolOrSolidifyLava(i, x, y)) continue;
                    if (y < Height - 1) FallOrFlow(i, x, stampNow, flowSideways: true);
                }
                else if (y < Height - 1)
                {
                    FallOrFlow(i, x, stampNow, flowSideways: false);
                }
            }
        }

        // ---- Pass 2: Aufsteigendes (Feuer) und Holzbrand, von oben nach unten ----
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int k = 0; k < Width; k++)
            {
                int x = leftToRight ? Width - 1 - k : k;
                int i = row + x;
                switch (mat[i])
                {
                    case Material.Fire:
                        UpdateFire(i, x, y);
                        break;
                    case Material.Wood:
                        if (t[i] >= IgnitionTemperature)
                        {
                            mat[i] = Material.BurningWood;
                            _burn[i] = BurnDuration;
                            _burnList[_burnCount++] = i;
                        }
                        break;
                    case Material.BurningWood:
                        UpdateBurningWood(i, x, y, frameDt);
                        break;
                }
            }
        }
    }

    private void Swap(int i, int j, ushort stampNow)
    {
        (_mat[i], _mat[j]) = (_mat[j], _mat[i]);
        (_tOld[i], _tOld[j]) = (_tOld[j], _tOld[i]);
        _stamp[j] = stampNow;
    }

    /// <summary>Falling Sand: gerade runter, sonst schräg links/rechts (Lava zusätzlich seitlich).</summary>
    private void FallOrFlow(int i, int x, ushort stampNow, bool flowSideways)
    {
        Material[] mat = _mat;
        int b = i + Width;
        if (mat[b] == Material.Air) { Swap(i, b, stampNow); return; }

        bool dl = x > 0 && mat[b - 1] == Material.Air;
        bool dr = x < Width - 1 && mat[b + 1] == Material.Air;
        if (dl && dr) { Swap(i, (NextU() & 1u) == 0 ? b - 1 : b + 1, stampNow); return; }
        if (dl) { Swap(i, b - 1, stampNow); return; }
        if (dr) { Swap(i, b + 1, stampNow); return; }

        if (flowSideways && NextF() < LavaSpreadProbability)
        {
            bool l = x > 0 && mat[i - 1] == Material.Air;
            bool r = x < Width - 1 && mat[i + 1] == Material.Air;
            if (l && r) Swap(i, (NextU() & 1u) == 0 ? i - 1 : i + 1, stampNow);
            else if (l) Swap(i, i - 1, stampNow);
            else if (r) Swap(i, i + 1, stampNow);
        }
    }

    /// <summary>Lava kühlt an Luftkontakt zusätzlich ab; unter 800 °C erstarrt sie zu Stein.</summary>
    private bool CoolOrSolidifyLava(int i, int x, int y)
    {
        Material[] mat = _mat;
        int air = 0;
        if (y > 0 && mat[i - Width] == Material.Air) air++;
        if (y < Height - 1 && mat[i + Width] == Material.Air) air++;
        if (x > 0 && mat[i - 1] == Material.Air) air++;
        if (x < Width - 1 && mat[i + 1] == Material.Air) air++;

        float temp = _tOld[i] - air * LavaContactCooling;
        _tOld[i] = temp;
        if (temp < LavaSolidifyTemperature)
        {
            mat[i] = Material.Stone;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Doom Fire: T[i ± wind, j-1] = T[i,j] - Decay(Zufall).
    /// </summary>
    private void UpdateFire(int i, int x, int y)
    {
        Material[] mat = _mat;
        float[] t = _tOld;
        float cur = t[i];
        float next = cur - NextF() * FireDecayMax;

        if (y == 0 || next < FireExtinguishTemperature)
        {
            mat[i] = Material.Air;
            return;
        }

        int wind = (int)(NextU() % 3u) - 1;
        int nx = x + wind;
        if (nx < 0 || nx >= Width) nx = x;
        int target = i - Width + (nx - x);
        if (mat[target] != Material.Air) target = i - Width;

        Material tm = mat[target];
        if (tm == Material.Air)
        {
            mat[target] = Material.Fire;
            t[target] = Math.Max(t[target], next);
            mat[i] = Material.Air;
            t[i] = Math.Max(AmbientTemperature, cur * FireTrailFactor);
        }
        else
        {
            if (tm == Material.Wood && t[target] < IgnitionTemperature + 1f)
                t[target] = IgnitionTemperature + 1f;
            mat[i] = Material.Air;
        }
    }

    private void UpdateBurningWood(int i, int x, int y, float frameDt)
    {
        float remaining = _burn[i] - frameDt;
        if (remaining <= 1e-4f)
        {
            _mat[i] = Material.Ash;
            _burn[i] = 0f;
            return;
        }
        _burn[i] = remaining;
        _burnList[_burnCount++] = i;
        if (_tOld[i] < BurnTemperature) _tOld[i] = BurnTemperature;

        if (y > 0 && NextF() < FireSpawnProbability)
        {
            int nx = x + (int)(NextU() % 3u) - 1;
            if (nx < 0 || nx >= Width) nx = x;
            int target = i - Width + (nx - x);
            if (_mat[target] == Material.Air)
            {
                _mat[target] = Material.Fire;
                _tOld[target] = BurnTemperature + NextF() * 300f;
            }
        }
    }

    // =====================================================================
    //  Interaktion (Maus-Werkzeuge)
    // =====================================================================
    public void Paint(int cx, int cy, int radius, Material material, float density = 1f)
    {
        bool overwrite = material == Material.Stone || material == Material.Air;
        int r2 = radius * radius;
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(Height - 1, cy + radius); y++)
        {
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(Width - 1, cx + radius); x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r2) continue;
                int i = y * Width + x;
                if (!overwrite && _mat[i] != Material.Air) continue;
                if (density < 1f && NextF() > density) continue;

                _mat[i] = material;
                _burn[i] = 0f;
                if (material == Material.Lava) _tOld[i] = LavaPourTemperature;
            }
        }
    }

    /// <summary>Hitzebrenner: setzt Pixel auf mindestens 'temp' (Standard 1200 °C).</summary>
    public void Heat(int cx, int cy, int radius, float temp = HeaterTemperature)
    {
        int r2 = radius * radius;
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(Height - 1, cy + radius); y++)
        {
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(Width - 1, cx + radius); x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r2) continue;
                int i = y * Width + x;
                if (_tOld[i] < temp) _tOld[i] = temp;
            }
        }
    }

    /// <summary>Demo-Szene: Steinplattform, Holzstapel, Sandhaufen.</summary>
    public void LoadDemoScene()
    {
        Reset();
        for (int x = 20; x <= 120; x++)
            for (int y = 110; y <= 113; y++) SetCell(x, y, Material.Stone);
        for (int x = 125; x <= 175; x++)
            for (int y = 170; y <= 198; y++) SetCell(x, y, Material.Wood);
        Paint(60, 70, 12, Material.Sand);
    }

    private void SetCell(int x, int y, Material m) => _mat[y * Width + x] = m;

    // =====================================================================
    //  Diagnose
    // =====================================================================
    public WorldStats ComputeStats()
    {
        float max = float.NegativeInfinity, min = float.PositiveInfinity;
        int nonFinite = 0;
        float[] t = _tOld;
        for (int i = 0; i < CellCount; i++)
        {
            float v = t[i];
            if (!float.IsFinite(v)) { nonFinite++; continue; }
            if (v > max) max = v;
            if (v < min) min = v;
        }

        // Stabiles Verfahren erfüllt das Maximumprinzip: nie unter Umgebungstemperatur.
        if (FirstInstabilityStep < 0 &&
            (nonFinite > 0 || min < AmbientTemperature - 1f || max > DivergenceLimit))
            FirstInstabilityStep = StepCount;

        return new WorldStats { MaxT = max, MinT = min, NonFiniteCount = nonFinite };
    }

    public Material GetMaterial(int x, int y) => _mat[y * Width + x];
    public float GetTemperature(int x, int y) => _tOld[y * Width + x];
    public void SetTemperature(int x, int y, float v) => _tOld[y * Width + x] = v;
    public void SetMaterial(int x, int y, Material m) => _mat[y * Width + x] = m;
}
