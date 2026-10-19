using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SandFireFDM.Simulation;

namespace SandFireFDM.Rendering;

public enum ViewMode
{
    /// <summary>Materialansicht mit Black-Body-Glühen (Spielansicht).</summary>
    Material,
    /// <summary>Reine Temperaturansicht (Stabilitätsanalyse).</summary>
    HeatMap
}

public sealed unsafe class WorldColorizer
{
    public const int LutMax = 1599;
    private const uint Magenta = 0xFFFF00FF;           // NaN / ∞ = Instabilitätsalarm

    private readonly uint[] _glow = new uint[LutMax + 1];
    private readonly ushort[] _glowWeight = new ushort[LutMax + 1];
    private readonly uint[] _heat = new uint[LutMax + 1];
    private readonly uint[] _shaded = new uint[8 * 32];
    private readonly byte[] _noise = new byte[SandWorld.CellCount];
    private readonly bool[] _emissive = new bool[8];

    public WorldColorizer()
    {
        // ---- Black-Body-Stützstellen (°C -> RGB) ----
        (float t, int r, int g, int b)[] stops =
        {
            (20f, 0, 0, 0), (350f, 70, 5, 0), (600f, 150, 15, 0), (800f, 230, 60, 0),
            (1000f, 255, 130, 10), (1200f, 255, 210, 60), (1400f, 255, 245, 170), (1600f, 255, 255, 255)
        };
        for (int T = 0; T <= LutMax; T++)
        {
            int k = 0;
            while (k < stops.Length - 2 && T > stops[k + 1].t) k++;
            var a = stops[k]; var b2 = stops[k + 1];
            float f = Math.Clamp((T - a.t) / (b2.t - a.t), 0f, 1f);
            int r = (int)(a.r + (b2.r - a.r) * f);
            int g = (int)(a.g + (b2.g - a.g) * f);
            int bl = (int)(a.b + (b2.b - a.b) * f);
            _glow[T] = Pack(r, g, bl);

            float u = Math.Clamp((T - 250f) / 300f, 0f, 1f);
            _glowWeight[T] = (ushort)(u * u * (3f - 2f * u) * 256f);
        }

        // ---- Heatmap-Palette: Blau -> Cyan -> Grün -> Gelb -> Rot ----
        for (int T = 0; T <= LutMax; T++)
        {
            float n = Math.Clamp((T - 20f) / 1380f, 0f, 1f);
            int r, g, b;
            if (n < 0.25f) { float f = n / 0.25f; r = 0; g = (int)(f * 255); b = 255; }
            else if (n < 0.5f) { float f = (n - 0.25f) / 0.25f; r = 0; g = 255; b = (int)((1 - f) * 255); }
            else if (n < 0.75f) { float f = (n - 0.5f) / 0.25f; r = (int)(f * 255); g = 255; b = 0; }
            else { float f = (n - 0.75f) / 0.25f; r = 255; g = (int)((1 - f) * 255); b = 0; }
            _heat[T] = Pack(r, g, b);
        }

        // ---- Materialfarben + je 32 Helligkeitsstufen als Pixelrauschen ----
        (int r, int g, int b)[] baseColor =
        {
            (4, 4, 10),       // Air
            (212, 178, 100),  // Sand
            (255, 90, 0),     // Lava
            (105, 108, 118),  // Stone
            (112, 72, 36),    // Wood
            (112, 72, 36),    // BurningWood
            (140, 140, 140),  // Ash
            (255, 120, 0)     // Fire
        };
        for (int m = 0; m < 8; m++)
            for (int n = 0; n < 32; n++)
            {
                int d = m == 0 ? 0 : (n - 16) * 2;
                _shaded[(m << 5) | n] = Pack(
                    Math.Clamp(baseColor[m].r + d, 0, 255),
                    Math.Clamp(baseColor[m].g + d, 0, 255),
                    Math.Clamp(baseColor[m].b + d, 0, 255));
            }

        _emissive[(int)Simulation.Material.Lava] = true;
        _emissive[(int)Simulation.Material.Fire] = true;
        _emissive[(int)Simulation.Material.BurningWood] = true;

        // deterministisches Pixelrauschen
        uint h = 2166136261u;
        for (int i = 0; i < _noise.Length; i++)
        {
            h = (h ^ (uint)i) * 16777619u;
            h ^= h >> 15;
            _noise[i] = (byte)(h & 31u);
        }
    }

    private static uint Pack(int r, int g, int b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    /// <summary>Schreibt das Gitter in einen Bgr32-Speicherbereich (z. B. BackBuffer). Keine Allokation.</summary>
    public void Fill(SandWorld world, uint* dst, int strideInPixels, ViewMode mode)
    {
        float[] temp = world.Temperature;
        Material[] mat = world.Materials;
        const int W = SandWorld.Width;
        const int H = SandWorld.Height;

        for (int y = 0; y < H; y++)
        {
            uint* line = dst + y * strideInPixels;
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                int i = row + x;
                float t = temp[i];

                if (!float.IsFinite(t)) { line[x] = Magenta; continue; }

                int ti = t <= 0f ? 0 : (t >= LutMax ? LutMax : (int)t);

                if (mode == ViewMode.HeatMap)
                {
                    if (t < SandWorld.AmbientTemperature - 0.5f)
                    {
                        // Unterschwinger = Instabilität: grau, je kälter desto heller
                        int v = (int)Math.Min(255f, 90f + (SandWorld.AmbientTemperature - t) * 0.6f);
                        line[x] = Pack(v, v, v);
                    }
                    else line[x] = _heat[ti];
                    continue;
                }

                int m = (int)mat[i];
                uint baseCol = _shaded[(m << 5) | _noise[i]];
                int w = _emissive[m] ? 256 : _glowWeight[ti];
                if (w == 0) { line[x] = baseCol; continue; }

                uint g = _glow[ti];
                int inv = 256 - w;
                int r = ((int)((baseCol >> 16) & 255u) * inv + (int)((g >> 16) & 255u) * w) >> 8;
                int gr = ((int)((baseCol >> 8) & 255u) * inv + (int)((g >> 8) & 255u) * w) >> 8;
                int b = ((int)(baseCol & 255u) * inv + (int)(g & 255u) * w) >> 8;
                line[x] = 0xFF000000u | ((uint)r << 16) | ((uint)gr << 8) | (uint)b;
            }
        }
    }
}
