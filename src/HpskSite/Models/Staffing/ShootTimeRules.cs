using System;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Planera dagen, steg 0 (Stefan 2026-10-26, Expertgruppen): <b>hur lång tid en skytt är upptagen av sin egen start</b>, per gren.
    /// Förut var det en konstant på 2 h för alla (<c>ShootIntentService.EstimatedStart</c>) — fält tar i verkligheten ~3 h, och varje
    /// krock och varje förslag byggde på den siffran. Nu tre tal: <b>före</b> (förberedelse, vapenkontroll, vatten), <b>skjuttid</b>
    /// (när slutet inte är känt) och <b>efter</b> (vila och förflyttning). Planeringen kan skriva över förvalet (kolumnerna
    /// <c>Planning.ShootBeforeMin/ShootDurationMin/ShootAfterMin</c>, null = förvalet).
    /// </summary>
    public readonly record struct ShootTime(int BeforeMinutes, int DurationMinutes, int AfterMinutes)
    {
        public TimeSpan Before => TimeSpan.FromMinutes(BeforeMinutes);
        public TimeSpan Duration => TimeSpan.FromMinutes(DurationMinutes);
        public TimeSpan After => TimeSpan.FromMinutes(AfterMinutes);

        /// <summary>
        /// Intervallet en start upptar: från <c>före</c> minuter innan starten till <c>efter</c> minuter efter slutet.
        /// Slutet är det kända (precisionens skjutlag har ett) — annars start + skjuttiden.
        /// </summary>
        public StaffInterval Window(DateTime start, DateTime? knownEnd)
        {
            var end = knownEnd is DateTime e && e > start ? e : start + Duration;
            return new StaffInterval(start - Before, end + After, AllDay: false);
        }

        /// <summary>"3 h", "2 h 30 min", "45 min" — så som en människa säger skjuttiden.</summary>
        public static string Text(int minutes) =>
            minutes % 60 == 0 ? $"{minutes / 60} h" : minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }

    public static class ShootTimeRules
    {
        /// <summary>Högsta värde som sparas (12 timmar) — ett tal utöver det är ett skrivfel.</summary>
        public const int MaxMinutes = 720;

        /// <summary>
        /// Förvalet per gren. Fält: patrullen är ute ~3 h (SHB D.9.11 och fältrapporten 2026-07-22). Precision: skjutlagen har oftast
        /// egna tider, så skjuttiden används bara när slutet saknas. Övriga grenar behåller den gamla gissningen på 2 h.
        /// </summary>
        public static ShootTime Default(string? discipline)
        {
            if (Has(FunctionaryRoles.FaltFamily, discipline)) return new ShootTime(45, 180, 30);
            if (Has(FunctionaryRoles.PrecisionFamily, discipline)) return new ShootTime(30, 60, 15);
            return new ShootTime(30, 120, 15);
        }

        /// <summary>Förvalet med planeringens egna värden över. Ett värde utanför 0–720 (eller en skjuttid på 0) ignoreras.</summary>
        public static ShootTime Resolve(string? discipline, int? before, int? duration, int? after)
        {
            var d = Default(discipline);
            return new ShootTime(
                before is >= 0 and <= MaxMinutes ? before.Value : d.BeforeMinutes,
                duration is > 0 and <= MaxMinutes ? duration.Value : d.DurationMinutes,
                after is >= 0 and <= MaxMinutes ? after.Value : d.AfterMinutes);
        }

        /// <summary>Null = giltigt. Tomt värde (null) betyder "använd förvalet".</summary>
        public static string? Validate(int? before, int? duration, int? after)
        {
            if (before is < 0 or > MaxMinutes) return "Tiden före starten ska vara mellan 0 och 720 minuter.";
            if (duration is <= 0 or > MaxMinutes) return "Skjuttiden ska vara mellan 1 och 720 minuter.";
            if (after is < 0 or > MaxMinutes) return "Tiden efter starten ska vara mellan 0 och 720 minuter.";
            return null;
        }

        private static bool Has(string[] family, string? discipline) =>
            !string.IsNullOrWhiteSpace(discipline) && Array.Exists(family, f => string.Equals(f, discipline.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
