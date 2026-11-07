namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// EN synlighetsmodell för klubbens och kretsens sidor och block (klubbsidorna plan v4, avsnitt 4).
    /// Lagrade strängar byts aldrig. <see cref="Utloggad"/> finns bara för systemblock (dagens inloggningsruta
    /// och Om-text på klubbens startsida) och kan aldrig väljas av en klubb.
    /// </summary>
    public enum SiteVisibility
    {
        /// <summary>Alla, även utloggade.</summary>
        Alla = 0,
        /// <summary>Vilken inloggad som helst (dagens flikar kräver bara inloggning, inte medlemskap).</summary>
        Inloggade = 1,
        /// <summary>Aktiv klubbmedlem (<c>ClubMembership</c>), klubbens eller kretsens admin.</summary>
        Klubbmedlemmar = 2,
        /// <summary>Styrelsen i ägaren, eller ägarens admin.</summary>
        Styrelse = 3,
        /// <summary>Bara ägarens admin.</summary>
        Dold = 4,
        /// <summary>Bara den som INTE är inloggad. Endast systemblock.</summary>
        Utloggad = 5,
    }

    /// <summary>
    /// Den som tittar, sett från EN ägare (en klubb eller en krets). Byggs av
    /// <see cref="SiteViewerResolver"/>; reglerna nedan är rena och testas utan databas.
    /// </summary>
    /// <param name="LoggedIn">Inloggad medlem.</param>
    /// <param name="IsOwnerMember">Aktiv rad i <c>ClubMembership</c> för ägaren (aldrig de råa egenskaperna).</param>
    /// <param name="IsOwnerBoard">Styrelseledamot i ägaren.</param>
    /// <param name="IsOwnerAdmin">Ägarens admin enligt <c>IsClubAdminForClub</c>/<c>IsRegionalAdminForRegion</c> (inkl. kretsens och sajtens admin).</param>
    public readonly record struct SiteViewer(bool LoggedIn, bool IsOwnerMember, bool IsOwnerBoard, bool IsOwnerAdmin)
    {
        public static SiteViewer Anonymous => new(false, false, false, false);
    }

    public static class SiteAccess
    {
        /// <summary>Får <paramref name="who"/> se något med synligheten <paramref name="visibility"/>?</summary>
        public static bool CanSee(SiteVisibility visibility, SiteViewer who) => visibility switch
        {
            SiteVisibility.Alla => true,
            SiteVisibility.Utloggad => !who.LoggedIn,
            SiteVisibility.Inloggade => who.LoggedIn,
            SiteVisibility.Klubbmedlemmar => who.LoggedIn && (who.IsOwnerMember || who.IsOwnerAdmin),
            SiteVisibility.Styrelse => who.LoggedIn && (who.IsOwnerBoard || who.IsOwnerAdmin),
            SiteVisibility.Dold => who.LoggedIn && who.IsOwnerAdmin,
            _ => false, // okänt värde: aldrig synligt (oklart = Dold)
        };

        /// <summary>
        /// Den striktaste av två synligheter (kopian i steg 2 mot mallens/blockets tak). Rangordning
        /// Alla &lt; Inloggade &lt; Klubbmedlemmar &lt; Styrelse &lt; Dold. <see cref="SiteVisibility.Utloggad"/> är ett
        /// systemvärde utan plats i rangordningen: ingår något Utloggad blir det Utloggad.
        /// </summary>
        public static SiteVisibility Strictest(SiteVisibility a, SiteVisibility b)
        {
            if (a == SiteVisibility.Utloggad || b == SiteVisibility.Utloggad) return SiteVisibility.Utloggad;
            return (int)a >= (int)b ? a : b;
        }

        /// <summary>
        /// Tak för block som bär personuppgifter (medlemmar, instruktörer, styrelse): aldrig "Alla".
        /// Anropas vid VARJE rendering, inte bara vid skrivning, så att en ändrad lagrad synlighet aldrig kan göra ett
        /// personblock publikt.
        /// </summary>
        public static SiteVisibility CapPersonal(SiteVisibility requested) =>
            requested == SiteVisibility.Alla ? SiteVisibility.Inloggade : requested;

        /// <summary>Synligheter en klubbadmin får välja mellan (aldrig <see cref="SiteVisibility.Utloggad"/>).</summary>
        public static bool IsSelectable(SiteVisibility v) => v != SiteVisibility.Utloggad && Enum.IsDefined(v);
    }
}
