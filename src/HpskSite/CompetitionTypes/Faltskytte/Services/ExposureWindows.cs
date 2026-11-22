using HpskSite.CompetitionTypes.Faltskytte.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// EN ren funktion för när en figur syns (fas 4, 2026-11-21). Visning nr 1 härleds ur de gamla fälten
    /// (Fast = hela tiden, Fram = [fram, fram+visas], Bort = [0, bort] och [igen, slutet]); figurens extra visningar
    /// (<see cref="FaltskytteFigure.Exposures"/>) läggs till efteråt. En gammal station utan extra visningar får
    /// exakt samma fönster som förut. JS-spegeln finns i FaltskytteStationEntry (tidur) och konfiguratorn.
    /// </summary>
    public static class ExposureWindows
    {
        /// <summary>Rörligt använder samma fält som Framsvängande, men bara när någon sparat en tid.</summary>
        public static bool UsesFramFields(FaltskytteFigure f) =>
            f.Behavior == "Framsvängande"
            || (f.Behavior == "Rörligt" && (f.DelayBeforeShowSec != null || f.ShowTimeSec != null || HasExtra(f)));

        public static bool HasExtra(FaltskytteFigure f) => f.Exposures != null && f.Exposures.Any(e => e.DurSec > 0);

        /// <summary>Fönstren {från, till} i sekunder inom [0, skjuttid], sorterade på start.</summary>
        public static List<int[]> For(FaltskytteFigure fig, int shootSec)
        {
            var bands = new List<int[]>();
            var b = fig.Behavior ?? "Fast";
            if (UsesFramFields(fig))
            {
                int delay = fig.DelayBeforeShowSec ?? 0;
                int dur = fig.ShowTimeSec ?? 8;
                if (dur > 0) Add(bands, delay, delay + dur, shootSec);
                foreach (var e in fig.Exposures ?? new List<FaltskytteExposure>())
                    if (e.DurSec > 0) Add(bands, e.FromSec, e.FromSec + e.DurSec, shootSec);
                bands.Sort((x, y) => x[0].CompareTo(y[0]));
                return bands;
            }
            if (b == "Bortsvängande")
            {
                int hideAt = fig.HideAfterSec ?? 8;
                int reappear = fig.ReappearSec ?? 0;
                if (hideAt > 0) bands.Add(new[] { 0, Math.Min(shootSec, hideAt) });
                if (reappear > 0 && reappear < shootSec) bands.Add(new[] { reappear, shootSec });
                return bands;
            }
            bands.Add(new[] { 0, shootSec }); // Fast (och Rörligt utan tider): synlig hela skjuttiden
            return bands;
        }

        /// <summary>När sista visningen slutar, oklämt mot skjuttiden (för varningen "går förbi skjuttiden").</summary>
        public static int LastEndUnclamped(FaltskytteFigure fig)
        {
            if (!UsesFramFields(fig)) return 0;
            var end = (fig.ShowTimeSec ?? 8) > 0 ? (fig.DelayBeforeShowSec ?? 0) + (fig.ShowTimeSec ?? 8) : 0;
            foreach (var e in fig.Exposures ?? new List<FaltskytteExposure>())
                if (e.DurSec > 0) end = Math.Max(end, e.FromSec + e.DurSec);
            return end;
        }

        static void Add(List<int[]> bands, int from, int to, int shootSec)
        {
            var f = Math.Max(0, Math.Min(shootSec, from));
            var t = Math.Max(f, Math.Min(shootSec, to));
            if (f < t) bands.Add(new[] { f, t });
        }
    }
}
