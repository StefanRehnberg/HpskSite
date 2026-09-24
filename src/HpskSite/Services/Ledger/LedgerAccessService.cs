using HpskSite.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Vem som får göra vad i föreningens ekonomi.
    ///
    /// <para><b>⚠️⚠️ EN UPPLÖSNING, TVÅ KONSUMENTER.</b> Sidan (<c>EkonomiController</c>) och
    /// endpointarna (<c>EkonomiAdminController</c>) frågar samma metod. Skrevs grinden på två
    /// ställen skulle de förr eller senare svara olika — och den sortens glidning betyder här
    /// antingen en sida full av knappar som nekas, eller en endpoint som släpper in någon sidan
    /// gömde. Kodbasen har gjort om det misstaget flera gånger; se
    /// <c>competition-host-shape-auth</c>.</para>
    ///
    /// <para><b>⚠️ STYRELSELEDAMOT ÄR INTE KLUBBADMIN.</b> Det är två register:
    /// styrelseuppdraget ligger i <c>BoardRoles</c>, klubbadmin är medlemsgruppen
    /// <c>ClubAdmin_{id}</c>. Mätt i dev 2026-09-22: <b>10 av 12 aktiva styrelseledamöter var
    /// INTE klubbadmin</b>. Att ekonomidelen bara gatade på klubbadmin var därför inget beslut —
    /// den ärvde grinden från resten av adminpanelen, och den passade dåligt: <b>ansvarsfriheten
    /// beviljas styrelsen, inte kassören.</b> En styrelse som inte kan se siffrorna kan inte bära
    /// det ansvaret, och Rapport-ytans egen underrubrik är "underlag till styrelsemötet".</para>
    ///
    /// <para><b>Delningen:</b> styrelsen LÄSER, kassören SKRIVER. En ledamot som bokför
    /// verifikationer är inte en sak; en ledamot som läser resultatet är hela poängen. Samma form
    /// som <c>/styrelse</c> redan har.</para>
    /// </summary>
    public class LedgerAccessService
    {
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _boardRoles;
        private readonly LedgerAuditorService _auditors;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        public LedgerAccessService(
            AdminAuthorizationService auth,
            BoardRoleService boardRoles,
            LedgerAuditorService auditors,
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService)
        {
            _auth = auth;
            _boardRoles = boardRoles;
            _auditors = auditors;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        /// <summary>
        /// Vad den inloggade får göra i <paramref name="ownerId"/>:s ekonomi.
        ///
        /// <para><b>⚠️ Tar ÄGARENS nod-id, aldrig ett utställar-id.</b> En sandlåda har negativt
        /// id och är ingen klubb — anroparen måste slå upp ägaren först.</para>
        /// </summary>
        public async Task<LedgerAccessResult> ResolveAsync(int ownerType, int ownerId)
        {
            if (ownerId <= 0) return LedgerAccessResult.None;

            _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
            var node = ctx?.Content?.GetById(ownerId);
            if (node is null) return LedgerAccessResult.None;

            string name;
            bool isAdmin;

            if (ownerType == DocumentOwnerType.Club)
            {
                // ⚠️ IsClubAdminForClub viker in klubbens KRETSADMINISTRATÖRER och sajtens
                // administratörer — befintligt och avsiktligt, se metodens egen dokumentation.
                isAdmin = await _auth.IsClubAdminForClub(ownerId);
                name = node.Value<string>("clubName") ?? node.Name ?? "";
            }
            else if (ownerType == DocumentOwnerType.Region)
            {
                // ⚠️ Koden ur NODEN. Kom den ur anropet vore grinden bara en fråga om vilken
                // sträng klienten råkade skicka.
                var regionCode = node.Value<string>("regionCode") ?? "";
                if (string.IsNullOrWhiteSpace(regionCode)) return LedgerAccessResult.None;

                isAdmin = await _auth.IsRegionalAdminForRegion(regionCode);
                name = node.Name ?? "";
            }
            else
            {
                // Ett tredje ägarslag finns inte. Att svara nej är rätt svar, inte ett fel.
                return LedgerAccessResult.None;
            }

            var memberId = await CurrentMemberIdAsync();

            // ── Revisorn skriver ALDRIG ─────────────────────────────────────────────────────
            // ⚠️⚠️ FÖRST, före skrivrätten. En revisor som råkar vara klubbadmin (eller kassör i
            //    registret av misstag) hade annars kunnat bokföra i det hen ska granska — ett
            //    oberoendebrott. Revisorn läser, via revisionsgrenen, och det är allt.
            //    Frågan täcker både inbjudan och det VALDA uppdraget i föreningens uppgifter.
            if (memberId > 0 && _auditors.HasAccess(ownerType, ownerId, memberId))
                return new LedgerAccessResult(LedgerAccess.Read, name, isAuditor: true,
                                              basis: LedgerAccessBasis.Auditor);

            // ── Sajtens administratör skriver ──────────────────────────────────────────────
            // ⚠️ Stefans beslut 2026-09-24: sajtadmin ska kunna bokföra även där föreningen har
            //    en kassör — det är supportvägen när en förening ber om hjälp. Efter revisorn (en
            //    revisor bokför aldrig) men före kassörsregeln, som annars gör sajtadmin till läsare.
            //    ⚠️ BARA sajtadmin, inte krets- eller klubbadmin: IsClubAdminForClub viker in dem
            //    alla, därför en egen fråga här.
            if (await _auth.IsCurrentUserAdminAsync())
                return new LedgerAccessResult(LedgerAccess.Write, name, basis: LedgerAccessBasis.SiteAdmin);

            // ── Kassören skriver ───────────────────────────────────────────────────────────
            // ⚠️⚠️ SKRIVRÄTTEN FÖLJER KASSÖRSUPPDRAGET (2026-09-24, Michael Henriksson: "Kassören
            //    borde vara den enda som kan komma in och göra allt i bokföringen"). Förut skrev
            //    klubbadmin — en teknisk roll på sajten, oftast en annan person än kassören —
            //    och via IsClubAdminForClub även varje kretsadministratör i kretsen.
            //
            // ⚠️⚠️ ÖVERGÅNGEN: finns INGEN aktiv kassör i föreningens uppgifter skriver
            //    administratören som förut, och ytan säger åt hen att lägga in kassören. En hård
            //    omläggning hade låst ute varje förening som inte fört in sin kassör — alltså
            //    nästan alla, den dag det deployas.
            var treasurers = _boardRoles.GetActiveRoleHolders(ownerType, ownerId, BoardRoleDefinitions.RoleKassor);

            if (memberId > 0 && treasurers.Any(t => t.MemberId == memberId))
                return new LedgerAccessResult(LedgerAccess.Write, name, basis: LedgerAccessBasis.Treasurer);

            var treasurerName = string.Join(", ", treasurers.Select(t => t.MemberName).Where(n => !string.IsNullOrWhiteSpace(n)));

            if (isAdmin && treasurers.Count == 0)
                return new LedgerAccessResult(LedgerAccess.Write, name, basis: LedgerAccessBasis.AdminWithoutTreasurer);

            // ── Styrelsen läser ──────────────────────────────────────────────────────────
            // ⚠️ IsBoardMemberOf kräver IsActive = 1 OCH IsBoardMember = 1. Revisorn och
            // valberedningen är alltså UTE här — de har IsBoardMember = 0.
            // ⚠️ REVISORN LÖSTES 2026-09-23, i den EGNA grenen nedan (LedgerAuditorService), inte
            // genom att vidga den här. Valberedningen har fortfarande ingen åtkomst till
            // ekonomin, och ska inte ha det: de bereder val, de granskar inte räkenskaper.
            // ⚠️ Ett utgånget mandat revoquerar inte: en styrelse sitter kvar till nästa årsmöte,
            // och en lucka där hade lämnat föreningen utan läsare i just det fönstret. Samma
            // resonemang som vapenregistrets behörighet.
            if (memberId > 0 && _boardRoles.IsBoardMemberOf(ownerType, ownerId, memberId))
                return new LedgerAccessResult(LedgerAccess.Read, name, basis: LedgerAccessBasis.Board,
                                              treasurerName: treasurerName);

            // ── Administratören läser när det finns en kassör ──────────────────────────────
            // ⚠️ Läser, inte nekas: klubbadmin sköter föreningens sidor och ska kunna se att
            //    ekonomin är uppsatt och hjälpa kassören — men bokföringen är kassörens.
            //    Samma för krets- och sajtadministratörer (IsClubAdminForClub viker in dem).
            if (isAdmin)
                return new LedgerAccessResult(LedgerAccess.Read, name, basis: LedgerAccessBasis.Admin,
                                              treasurerName: treasurerName);

            // (Revisorn prövades först — se ovan. Den grenen är ALDRIG en vidgning av
            //  IsBoardMemberOf: den flaggan styr också vilka som räknas i BESLUTSFÖRHETEN, och en
            //  revisor som blir beslutsför i den styrelse hen granskar är ett allvarligare fel.)
            return new LedgerAccessResult(LedgerAccess.None, name);
        }

        private async Task<int> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email is null) return 0;
            return _memberService.GetByEmail(current.Email)?.Id ?? 0;
        }
    }

    /// <summary>
    /// ⚠️ Tre lägen, inte en boolean. "Får inte" och "får läsa men inte skriva" är olika svar, och
    /// en yta som slår ihop dem visar antingen för mycket eller för lite.
    /// </summary>
    public enum LedgerAccess
    {
        None = 0,
        Read = 1,
        Write = 2
    }

    /// <summary>
    /// VARFÖR den inloggade får det hen får. Ytan behöver det för att förklara frånvaron av
    /// knappar — "du läser som styrelseledamot" och "du läser som administratör, kassören bokför"
    /// är olika meningar — och för att be administratören lägga in en kassör.
    /// </summary>
    public enum LedgerAccessBasis
    {
        None = 0,
        /// <summary>Aktiv kassör i föreningens uppgifter — skriver.</summary>
        Treasurer,
        /// <summary>Administratör i en förening UTAN registrerad kassör — skriver, tills vidare.</summary>
        AdminWithoutTreasurer,
        /// <summary>Aktiv styrelseledamot — läser.</summary>
        Board,
        /// <summary>Administratör när föreningen har en kassör — läser.</summary>
        Admin,
        /// <summary>Revisor (vald eller inbjuden) — läser, skriver aldrig.</summary>
        Auditor,
        /// <summary>Sajtens administratör — skriver alltid (support), utom som revisor.</summary>
        SiteAdmin
    }

    public readonly struct LedgerAccessResult
    {
        public LedgerAccessResult(LedgerAccess access, string ownerName, bool isAuditor = false,
                                  LedgerAccessBasis basis = LedgerAccessBasis.None, string? treasurerName = null)
        {
            Access = access;
            OwnerName = ownerName;
            IsAuditor = isAuditor;
            Basis = basis;
            TreasurerName = treasurerName ?? "";
        }

        public LedgerAccessBasis Basis { get; }

        /// <summary>Kassören(s) namn, när den inloggade läser — "Det är Anna Svensson som bokför."</summary>
        public string TreasurerName { get; }

        /// <summary>
        /// Sant när läsrätten kommer från ett REVISORSUPPDRAG och inte från styrelsen.
        ///
        /// <para><b>⚠️ Ytan måste kunna skilja dem åt.</b> Båda läser, men revisorn ska mötas av
        /// revisionssidan — räkenskaper, verifikationer och protokoll samlade — medan
        /// styrelseledamoten möts av kassörsappen i läsläge. Samma rättighet, olika ärende.</para>
        /// </summary>
        public bool IsAuditor { get; }

        public LedgerAccess Access { get; }

        /// <summary>Föreningens namn, ur noden — aldrig ur något klienten skickat.</summary>
        public string OwnerName { get; }

        public bool CanRead => Access >= LedgerAccess.Read;

        public bool CanWrite => Access == LedgerAccess.Write;

        public static LedgerAccessResult None => new(LedgerAccess.None, "");
    }
}
