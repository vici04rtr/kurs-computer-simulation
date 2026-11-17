using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SandFireFDM.Simulation;

namespace SandFireFDM.Rendering;

public sealed class WorldRenderer
{
    private readonly WriteableBitmap _bitmap =
        new(SandWorld.Width, SandWorld.Height, 96, 96, PixelFormats.Bgr32, null);
    private readonly WorldColorizer _colorizer = new();
    private readonly Int32Rect _full = new(0, 0, SandWorld.Width, SandWorld.Height);

    public WriteableBitmap Bitmap => _bitmap;

    public unsafe void Render(SandWorld world, ViewMode mode)
    {
        _bitmap.Lock();
        try
        {
            _colorizer.Fill(world, (uint*)_bitmap.BackBuffer, _bitmap.BackBufferStride / 4, mode);
            _bitmap.AddDirtyRect(_full);
        }
        finally
        {
            _bitmap.Unlock();
        }
    }
}
