using System;
using System.Collections.Generic;

namespace MemoryEase
{
    internal static class RefreshPolicy
    {
        internal static int Interval(bool visible) { return visible ? 2000 : 10000; }
        internal static bool Home(bool visible, int page) { return visible && page == 0; }
        internal static bool Processes(bool visible, int page, bool busy, long elapsed)
        { return visible && page == 1 && !busy && elapsed >= 10000; }
    }
    internal sealed class HistorySample
    { internal long At; internal double Usage; }
    internal sealed class MemoryHistory
    {
        internal const int Duration = 120000;
        internal readonly List<HistorySample> Samples = new List<HistorySample>();
        internal void Add(long at, double usage)
        {
            usage = Math.Max(0, Math.Min(100, usage));
            if (Samples.Count > 0 && at <= Samples[Samples.Count - 1].At) { Samples[Samples.Count - 1].Usage = usage; return; }
            Samples.Add(new HistorySample { At = at, Usage = Math.Max(0, Math.Min(100, usage)) });
            while (Samples.Count > 1 && (Samples[0].At < at - Duration || Samples.Count > 128)) Samples.RemoveAt(0);
        }
    }
}
