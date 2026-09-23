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
            bool canWrite;

            if (ownerType == DocumentOwnerType.Club)
            {
                // ⚠️ IsClubAdminForClub viker in klubbens KRETSADMINISTRATÖRER — det är
                // befintligt och avsiktligt, se metodens egen dokumentation.
                canWrite = await _auth.IsClubAdminForClub(ownerId);
                name = node.Value<string>("clubName") ?? node.Name ?? "";
            }
            else if (ownerType == DocumentOwnerType.Region)
            {
                // ⚠️ Koden ur NODEN. Kom den ur anropet vore grinden bara en fråga om vilken
                // sträng klienten råkade skicka.
                var regionCode = node.Value<string>("regionCode") ?? "";
                if (string.IsNullOrWhiteSpace(regionCode)) return LedgerAccessResult.None;

                canWrite = await _auth.IsRegionalAdminForRegion(regionCode);
                name = node.Name ?? "";
            }
            else
            {
                // Ett tredje ägarslag finns inte. Att svara nej är rätt svar, inte ett fel.
                return LedgerAccessResult.None;
            }

            if (canWrite) return new LedgerAccessResult(LedgerAccess.Write, name);

            // ── Styrelsen läser ──────────────────────────────────────────────────────────
            // ⚠️ IsBoardMemberOf kräver IsActive = 1 OCH IsBoardMember = 1. Revisorn och
            // valberedningen är alltså UTE här — de har IsBoardMember = 0.
            // ⚠️ REVISORN LÖSTES 2026-09-23, i den EGNA grenen nedan (LedgerAuditorService), inte
            // genom att vidga den här. Valberedningen har fortfarande ingen åtkomst till
            // ekonomin, och ska inte ha det: de bereder val, de granskar inte räkenskaper.
            // ⚠️ Ett utgånget mandat revoquerar inte: en styrelse sitter kvar till nästa årsmöte,
            // och en lucka där hade lämnat föreningen utan läsare i just det fönstret. Samma
            // resonemang som vapenregistrets behörighet.
            var memberId = await CurrentMemberIdAsync();
            if (memberId > 0 && _boardRoles.IsBoardMemberOf(ownerType, ownerId, memberId))
                return new LedgerAccessResult(LedgerAccess.Read, name);

            // ── Revisorn läser också ──────────────────────────────────────────────────────
            // ⚠️⚠️ EN EGEN GREN, ALDRIG EN VIDGNING AV IsBoardMemberOf. Den frågan kräver
            // IsBoardMember = 1, och den flaggan styr också vilka som seedas som närvarande på
            // styrelsemöten och RÄKNAS I BESLUTSFÖRHETEN. En revisor som blir beslutsför i den
            // styrelse hen granskar är ett allvarligare fel än det man löste.
            //
            // ⚠️ Uppdraget är scopat till EN förening och har en utgångstid — se
            // LedgerAuditorGrant.IsActive. Ett fel i uppslaget betyder NEKAD, aldrig öppet.
            if (memberId > 0 && _auditors.HasAccess(ownerType, ownerId, memberId))
                return new LedgerAccessResult(LedgerAccess.Read, name, isAuditor: true);

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

    public readonly struct LedgerAccessResult
    {
        public LedgerAccessResult(LedgerAccess access, string ownerName, bool isAuditor = false)
        {
            Access = access;
            OwnerName = ownerName;
            IsAuditor = isAuditor;
        }

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
