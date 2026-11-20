using HpskSite.Models.CompetitionCalls;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Klubbprogrammet (F7): reglerna som rena funktioner, så att medlemmens ruta, adminlistan och mejlen läser SAMMA svar.
    /// Ordet i gränssnittet är <b>Tävlingsledare</b>; aldrig "tävlingschef".
    /// </summary>
    public static class CompetitionManagerRules
    {
        public const int MaxAppointAtOnce = 20;

        /// <summary>
        /// Söker klubben tävlingsledare till tävlingen? Ja när rollen inte stängts, dagen inte passerat och antingen ingen
        /// tävlingsledare finns ännu eller någon redan önskat/utsetts genom den här funktionen (då pågår tillsättningen tills adminen
        /// trycker Klart). En tävling som redan har tävlingsledare utan att funktionen använts söker ingen.
        /// </summary>
        public static bool IsSeeking(bool closed, DateTime? competitionDate, DateTime today, bool hasAnyManager, bool hasAnyWishRow)
        {
            if (closed) return false;
            if (competitionDate.HasValue && competitionDate.Value.Date < today.Date) return false;
            return !hasAnyManager || hasAnyWishRow;
        }

        /// <summary>
        /// Medlemmens läge på en rad: <c>open</c> (kan önska), <c>wished</c> (kan ångra), <c>appointed</c> (kan tacka nej),
        /// <c>filled-not-you</c> (rollen stängd, medlemmen önskade men utsågs inte), annars null (raden visas inte).
        /// ⚠️ Inget läge bär andras namn eller antal.
        /// </summary>
        public static string? MemberState(bool seeking, bool closed, string? myWishStatus, bool iAmManager)
        {
            if (iAmManager) return "appointed";
            if (closed) return myWishStatus is CompetitionManagerWishStatus.Aktiv or CompetitionManagerWishStatus.Avslutad ? "filled-not-you" : null;
            if (!seeking) return null;
            return myWishStatus == CompetitionManagerWishStatus.Aktiv ? "wished" : "open";
        }

        /// <summary>Adminens läge per tävling.</summary>
        public static string AdminState(int managerCount, bool closed, int activeWishes) =>
            closed ? "closed" : managerCount > 0 ? "partial" : "missing";

        /// <summary>Varför går det inte att önska? Null = det går.</summary>
        public static string? WhyNotWish(bool seeking, bool closed, bool isMemberOfClub, bool alreadyManager)
        {
            if (!isMemberOfClub) return "Bara aktiva medlemmar i klubben kan önska.";
            if (alreadyManager) return "Du är redan tävlingsledare för den här tävlingen.";
            if (closed) return "Tävlingsledare är redan tillsatta.";
            if (!seeking) return "Klubben söker inte tävlingsledare till den här tävlingen.";
            return null;
        }

        /// <summary>
        /// De som ska få beskedet "tillsatt" när adminen stänger rollen: de som önskat (Aktiv) och inte utsetts.
        /// Utsedda och dem som redan fått beskedet räknas inte. Ren funktion över önskemålen.
        /// </summary>
        public static List<int> WhoGetsFilledNotice(IEnumerable<CompetitionManagerWish> wishes) =>
            wishes.Where(w => w.Status == CompetitionManagerWishStatus.Aktiv).Select(w => w.MemberId).Distinct().ToList();

        /// <summary>Ett önskemål som inte ledde till något gallras när tävlingen är genomförd (dagen efter).</summary>
        public static bool ShouldPurge(string status, DateTime? competitionDate, DateTime today) =>
            status != CompetitionManagerWishStatus.Utsedd && competitionDate.HasValue && competitionDate.Value.Date < today.Date;
    }
}
