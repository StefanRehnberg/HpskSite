using System.Globalization;

namespace HpskSite.Models
{
    /// <summary>
    /// Ett klockslag som någon har skrivit för hand, till "HH:mm" (2026-10-11, efter Anders på
    /// Hästveda: "Det borde räcka att skriva 11 och 13" — kolon kräver ett extra tangentbord på
    /// telefonen). EN tolkning för webbläsaren (<c>wwwroot/js/time-input.js</c>, samma regler) och
    /// servern, så att det som visas efter att man lämnat fältet är det som sparas.
    ///
    /// <para>Godtas: <c>11</c> → 11:00 · <c>930</c> → 09:30 · <c>1130</c> → 11:30 ·
    /// <c>11:30</c>, <c>11.30</c>, <c>11,30</c>, <c>11 30</c> → 11:30 · <c>9:05</c> → 09:05.
    /// Avvisas: timme över 23, minut över 59, och en ensiffrig minut (<c>11:5</c>) — den kan vara
    /// 11:05 eller 11:50, och en gissad tid är värre än en fråga.</para>
    ///
    /// <para>⚠️ Använd aldrig <c>TimeSpan.TryParse</c> på ett inskrivet klockslag: den läser "11" som
    /// ELVA DAGAR.</para>
    /// </summary>
    public static class ClockTime
    {
        /// <summary>Tomt = true med <paramref name="hhmm"/> null (inget klockslag). Oläsligt = false.</summary>
        public static bool TryNormalize(string? input, out string? hhmm)
        {
            hhmm = null;
            var v = (input ?? "").Trim();
            if (v.Length == 0) return true;
            if (!TryParts(v, out var h, out var m)) return false;
            hhmm = h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Som <see cref="TryNormalize"/>, men som tid på dygnet. Tomt eller oläsligt = null.</summary>
        public static TimeSpan? ToTimeSpan(string? input)
            => TryNormalize(input, out var t) && t != null
                ? new TimeSpan(int.Parse(t[..2], CultureInfo.InvariantCulture), int.Parse(t[3..], CultureInfo.InvariantCulture), 0)
                : null;

        private static bool TryParts(string v, out int h, out int m)
        {
            h = m = -1;
            var sep = v.IndexOfAny(new[] { ':', '.', ',', ' ' });
            string hs, ms;
            if (sep >= 0)
            {
                hs = v[..sep];
                ms = v[(sep + 1)..];
                if (ms.Length != 2) return false;            // "11:5" är tvetydigt
            }
            else
            {
                if (v.Length is < 1 or > 4) return false;
                if (v.Length <= 2) { hs = v; ms = "00"; }    // "11" → 11:00
                else { hs = v[..^2]; ms = v[^2..]; }        // "930" → 9:30, "1130" → 11:30
            }
            if (hs.Length is < 1 or > 2 || !hs.All(char.IsAsciiDigit) || !ms.All(char.IsAsciiDigit)) return false;
            h = int.Parse(hs, CultureInfo.InvariantCulture);
            m = int.Parse(ms, CultureInfo.InvariantCulture);
            return h <= 23 && m <= 59;
        }
    }
}
