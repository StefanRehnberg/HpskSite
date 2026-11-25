using System.Globalization;
using System.Text.RegularExpressions;

namespace HpskSite.Models
{
    public enum BirthYearStatus
    {
        /// <summary>Året är säkert (komplett personnummer, eller födelsedatum, eller båda i samklang).</summary>
        Ok,
        /// <summary>Bara ett 10-siffrigt personnummer: århundradet är en gissning (äldre än 99 felbedöms).</summary>
        Uncertain,
        /// <summary>Varken personnummer eller födelsedatum finns.</summary>
        Missing,
        /// <summary>Något finns men går inte att tolka som ett år.</summary>
        Invalid,
        /// <summary>Året ligger efter innevarande år.</summary>
        Future,
        /// <summary>Äldre än <see cref="MemberBirthYear.MaxAge"/> år.</summary>
        Implausible,
        /// <summary>Personnummer och födelsedatum säger olika år. Inget byts på en motsägelse.</summary>
        Conflict
    }

    /// <summary>Födelseår (null när det inte går att placera) och varför.</summary>
    public readonly record struct BirthYearResult(int? Year, BirthYearStatus Status)
    {
        /// <summary>Går att räkna ålder på (säkert eller osäkert).</summary>
        public bool IsUsable => Year.HasValue && Status is BirthYearStatus.Ok or BirthYearStatus.Uncertain;
    }

    /// <summary>
    /// EN tolkning av en medlems födelseår ur de två källorna appen har: <c>personNumber</c> (kanonisk) och <c>birthDate</c>
    /// (importen och klubbadmins dialog). Rent — ingen databas, ingen klocka. Personnummertolkningen är
    /// <see cref="Marken.BirthYearFromPersonNumber"/> (en implementation); den här klassen lägger till statusen och
    /// sammanvägningen av de två källorna.
    ///
    /// <para>Ålder räknas på KALENDERÅR (SHB D.2.8): <c>avgiftsår − födelseår</c>, se <see cref="AgeTypeRules.Age"/>.
    /// <c>currentYear</c> är innevarande år (för århundradesgissningen och "framtid"), inte avgiftsåret.</para>
    ///
    /// <para>Regler: komplett (12/13 siffror) personnummer är säkert. Ett 10-siffrigt är osäkert och ger vika för ett
    /// läsbart födelsedatum. Säger ett komplett personnummer och ett födelsedatum olika år blir det
    /// <see cref="BirthYearStatus.Conflict"/> — ingen placering.</para>
    /// </summary>
    public static class MemberBirthYear
    {
        public const int MaxAge = 120;
        public const int MinYear = 1900;

        private static readonly Regex IsoDate = new(@"^\s*(\d{4})-(\d{1,2})-(\d{1,2})", RegexOptions.Compiled);

        public static BirthYearResult From(string? birthDate, string? personNumber, int currentYear)
        {
            var pn = FromPersonNumber(personNumber, currentYear, out var pnComplete);
            var bd = FromBirthDate(birthDate, currentYear);

            // Båda läsbara
            if (pn.Year.HasValue && bd.Year.HasValue)
            {
                if (pn.Year == bd.Year) return Check(new BirthYearResult(pn.Year, BirthYearStatus.Ok), currentYear);
                // Ett 10-siffrigt personnummer är en gissning och ger vika för ett uttryckligt födelsedatum.
                if (!pnComplete) return Check(new BirthYearResult(bd.Year, BirthYearStatus.Ok), currentYear);
                return new BirthYearResult(null, BirthYearStatus.Conflict);
            }
            if (pn.Year.HasValue) return Check(pn, currentYear);
            if (bd.Year.HasValue) return Check(bd, currentYear);

            // Ingen källa gav ett år: berätta det mest talande skälet.
            if (pn.Status == BirthYearStatus.Missing && bd.Status == BirthYearStatus.Missing)
                return new BirthYearResult(null, BirthYearStatus.Missing);
            if (pn.Status == BirthYearStatus.Future || bd.Status == BirthYearStatus.Future)
                return new BirthYearResult(null, BirthYearStatus.Future);
            return new BirthYearResult(null, BirthYearStatus.Invalid);
        }

        private static BirthYearResult Check(BirthYearResult r, int currentYear)
        {
            if (r.Year is int y && currentYear - y > MaxAge) return new BirthYearResult(null, BirthYearStatus.Implausible);
            return r;
        }

        private static BirthYearResult FromPersonNumber(string? pn, int currentYear, out bool complete)
        {
            complete = false;
            if (string.IsNullOrWhiteSpace(pn)) return new BirthYearResult(null, BirthYearStatus.Missing);
            var digits = pn.Count(char.IsDigit);
            if (digits == 0) return new BirthYearResult(null, BirthYearStatus.Invalid);
            complete = digits is 12 or 13;
            if (!complete && digits != 10) { complete = false; return new BirthYearResult(null, BirthYearStatus.Invalid); }

            var year = Marken.BirthYearFromPersonNumber(pn, currentYear);
            if (year > 0) return new BirthYearResult(year, complete ? BirthYearStatus.Ok : BirthYearStatus.Uncertain);

            // 0 = oläsligt. Ett komplett personnummer med år efter i år är "framtid", annars ogiltigt.
            if (complete)
            {
                var all = new string(pn.Where(char.IsDigit).ToArray());
                if (int.TryParse(all.AsSpan(0, 4), out var y4) && y4 > currentYear)
                    return new BirthYearResult(null, BirthYearStatus.Future);
            }
            return new BirthYearResult(null, BirthYearStatus.Invalid);
        }

        private static BirthYearResult FromBirthDate(string? text, int currentYear)
        {
            if (string.IsNullOrWhiteSpace(text)) return new BirthYearResult(null, BirthYearStatus.Missing);
            int year;
            var m = IsoDate.Match(text);
            if (m.Success) year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out var dt)) year = dt.Year;
            else return new BirthYearResult(null, BirthYearStatus.Invalid);

            if (year < MinYear) return new BirthYearResult(null, BirthYearStatus.Invalid);
            if (year > currentYear) return new BirthYearResult(null, BirthYearStatus.Future);
            return new BirthYearResult(year, BirthYearStatus.Ok);
        }
    }
}
