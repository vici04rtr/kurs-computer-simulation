using System.Collections.Concurrent;

namespace MultithreadingBenchmark.Infrastructure
{
    /// <summary>
    /// Pool wiederverwendbarer Pixelpuffer (BGRA32 als uint[]).
    ///
    /// Zweck: Der Hintergrund-Thread rendert das Bild in einen eigenen Puffer (reiner CPU-Code, keine WPF-Objekte)
    /// und reicht ihn per IProgress&lt;T&gt; an den UI-Thread weiter. Dieser kopiert ihn per WritePixels in die
    /// WriteableBitmap und gibt den Puffer anschließend zurück. So greift der UI-Thread NIE auf Simulationsdaten zu,
    /// die der Worker gleichzeitig verändert (kein Tearing), und es entsteht kein GC-Druck durch 1,4-MB-Arrays.
    /// </summary>
    public sealed class FrameBufferPool
    {
        private readonly ConcurrentBag<uint[]> _free = new();
        private readonly int _length;

        public FrameBufferPool(int length)
        {
            _length = length;
        }

        public uint[] Rent() => _free.TryTake(out var buffer) ? buffer : new uint[_length];

        public void Return(uint[] buffer) => _free.Add(buffer);
    }
}
