using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services.Mail
{
    /// <summary>
    /// Slår upp VEM ett svar ska gå till. Ett ställe, så de ~35 mejltyperna inte bär var sin kopia
    /// av regeln.
    ///
    /// <para><b>⚠️ TJÄNSTEN LIGGER UTANFÖR <c>EmailService</c> med flit.</b> <c>EmailService</c> är
    /// registrerad som singleton och känner bara konfigurationen; att injicera klubb- och
    /// medlemsuppslag där hade dragit in halva domänen i mejllagret (och riskerat en DI-cykel,
    /// eftersom flera av de tjänsterna själva mejlar). Anroparen — som redan har klubb-id:t —
    /// löser upp adressen och skickar in ett färdigt <see cref="MailReplyTo"/>.</para>
    ///
    /// <para><b>⚠️ FALLER ALDRIG TILLBAKA TYST PÅ INGENTING.</b> Varje väg slutar i en adress
    /// eller i <see cref="MailReplyTo.SiteAdmin"/>. Ett mejl utan svarsadress är precis det fel
    /// som gav upphov till hela arbetet.</para>
    /// </summary>
    public class ReplyContactResolver
    {
        private readonly ClubService _clubs;
        private readonly IMemberService _members;
        private readonly MemberClubService _memberClubs;
        private readonly IContentService _content;
        private readonly ILogger<ReplyContactResolver> _logger;

        public ReplyContactResolver(
            ClubService clubs,
            IMemberService members,
            MemberClubService memberClubs,
            IContentService content,
            ILogger<ReplyContactResolver> logger)
        {
            _clubs = clubs;
            _members = members;
            _memberClubs = memberClubs;
            _content = content;
            _logger = logger;
        }

        /// <summary>
        /// MOTTAGARENS egen klubb — svarsadressen på besked som klubben skickar till en av sina
        /// medlemmar: godkännande, avslag, inbjudan, välkomstmejl.
        ///
        /// <para><b>⚠️ Går via <c>MemberClubService.GetPrimaryClubId</c>, aldrig
        /// <c>GetValue&lt;int&gt;("primaryClubId")</c>.</b> Egenskapen är en STRÄNG, så den generiska
        /// läsningen konverterar inte utan ger tyst <b>0</b> — vilket här skulle betyda "ingen
        /// klubb" och skicka svaret till sajtens adress. Samma fälla gav varje walk-in-anmälan
        /// <c>clubId=0</c>.</para>
        /// </summary>
        public MailReplyTo ForMembersOwnClub(int memberId)
        {
            if (memberId <= 0) return MailReplyTo.SiteAdmin;

            try
            {
                return ForMembersOwnClub(_members.GetById(memberId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReplyContactResolver: kunde inte läsa medlem {MemberId}.", memberId);
                return MailReplyTo.SiteAdmin;
            }
        }

        /// <inheritdoc cref="ForMembersOwnClub(int)"/>
        public MailReplyTo ForMembersOwnClub(IMember? member)
        {
            if (member is null) return MailReplyTo.SiteAdmin;

            var clubId = _memberClubs.GetPrimaryClubId(member);
            return clubId > 0 ? ForClub(clubId) : MailReplyTo.SiteAdmin;
        }

        /// <summary>
        /// Klubbens egen kontaktadress, med klubbens namn som visningsnamn.
        ///
        /// <para>Används för besked som kommer FRÅN klubben som organisation — och som reserv när
        /// den handläggande personen inte går att peka ut.</para>
        /// </summary>
        public MailReplyTo ForClub(int clubId)
        {
            if (clubId <= 0) return MailReplyTo.SiteAdmin;

            try
            {
                var club = _clubs.GetClubById(clubId);
                if (club is null)
                {
                    _logger.LogDebug("ReplyContactResolver: klubb {ClubId} hittades inte.", clubId);
                    return MailReplyTo.SiteAdmin;
                }
                return MailReplyTo.FromClub(club.Name, club.ContactEmail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReplyContactResolver: kunde inte läsa klubb {ClubId}.", clubId);
                return MailReplyTo.SiteAdmin;
            }
        }

        /// <summary>
        /// En namngiven medlems egen adress.
        ///
        /// <para>Det här är svarsadressen på mejl som går TILL klubbens funktionärer OM en medlem:
        /// den som läser "Kalle har begärt ett föreningsintyg" vill svara Kalle, inte sig själv.</para>
        /// </summary>
        public MailReplyTo ForMember(int memberId)
        {
            if (memberId <= 0) return MailReplyTo.SiteAdmin;

            try
            {
                var m = _members.GetById(memberId);
                if (m is null || string.IsNullOrWhiteSpace(m.Email)) return MailReplyTo.SiteAdmin;

                var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                if (name.Length == 0) name = m.Name ?? "";
                return MailReplyTo.To(m.Email, name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReplyContactResolver: kunde inte läsa medlem {MemberId}.", memberId);
                return MailReplyTo.SiteAdmin;
            }
        }

        /// <summary>
        /// Den HANDLÄGGANDE personens egen adress, men presenterad som klubbens mejl.
        ///
        /// <para><b>Varför personen och inte klubbadressen:</b> hen är den som klickade, och den
        /// som sitter och väntar på svaret. Klubbens kontaktadress är ofta en brevlåda någon tittar
        /// i på söndagar. Klubbadministratören får dock välja själv vid varje tillfälle — se
        /// <see cref="ForClubAdminChoice"/>.</para>
        ///
        /// <para>Saknar personen adress faller den tillbaka på klubbens, inte på sajtens.</para>
        /// </summary>
        public MailReplyTo ForPersonInClub(int memberId, int clubId)
        {
            var person = ForMember(memberId);
            var club = ForClub(clubId);

            if (person.Kind != MailReplyTo.ReplyKind.Address) return club;

            // Personens adress, men avsändarnamnet ska säga vilken klubb beskedet kommer från.
            return MailReplyTo.To(person.Email, person.Name, club.FromDisplayName);
        }

        /// <summary>
        /// Klubbadministratörens val vid det enskilda tillfället: hens egen adress eller klubbens.
        ///
        /// <para><b>⚠️ VALET GÖRS VID VARJE UTSKICK, inte en gång i en inställning.</b> Stefans
        /// begäran 2026-09-09: en komplettering om ett vapenärende kan vara något hen vill ha till
        /// sin egen inkorg, medan ett formellt besked hör till klubbens brevlåda — och det beror på
        /// ärendet, inte på personen. En sparad inställning hade tvingat ett svar för alla ärenden.</para>
        ///
        /// <para><b>⚠️ OKÄNT VÄRDE = personen.</b> Standardläget är den som handlägger, eftersom
        /// hen bevisligen är vid tangentbordet just nu. En klubbadress som ingen läser är den
        /// tystnad vi försöker bli av med.</para>
        /// </summary>
        /// <param name="replyToClub">
        /// Klientens val. <c>true</c> = klubbens kontaktadress, allt annat = handläggaren.
        /// </param>
        public MailReplyTo ForClubAdminChoice(int actingMemberId, int clubId, bool replyToClub)
            => replyToClub ? ForClub(clubId) : ForPersonInClub(actingMemberId, clubId);

        /// <summary>
        /// Tävlingens ARRANGÖR — svarsadressen på betalkrav, kvitton och påminnelser.
        ///
        /// <para><b>⚠️⚠️ BÅDA VÄRDFORMERNA, och det är inte valfritt.</b> En tävling arrangeras
        /// antingen av en KLUBB (<c>clubId</c> satt) eller av KRETSEN själv (<c>clubId</c> tomt,
        /// <c>regionalFederation</c> satt) — ett SM är den senare. En upplösning som bara läser
        /// <c>clubId</c> hamnar på sajtens adress för varje kretsarrangerad tävling, alltså precis
        /// den tystnad som gav upphov till det här arbetet. Samma fälla har den här kodbasen gått i
        /// fyra gånger på behörighetssidan.</para>
        ///
        /// <para><b>Ordningen:</b> tävlingens egen <c>contactEmail</c> först (den är satt just för
        /// att någon ska kunna nås om DEN HÄR tävlingen), sedan värdens kontaktadress.</para>
        /// </summary>
        public MailReplyTo ForCompetitionOrganiser(int competitionId)
        {
            if (competitionId <= 0) return MailReplyTo.SiteAdmin;

            try
            {
                var comp = _content.GetById(competitionId);
                if (comp is null || comp.ContentType.Alias != "competition")
                {
                    _logger.LogDebug("ReplyContactResolver: tävling {Id} hittades inte.", competitionId);
                    return MailReplyTo.SiteAdmin;
                }

                // Värdens namn behövs som avsändarnamn även när tävlingens egen adress används.
                var hostName = "";
                var hostEmail = "";

                var clubId = comp.GetValue<int>("clubId");
                if (clubId > 0)
                {
                    var club = _clubs.GetClubById(clubId);
                    hostName = club?.Name ?? "";
                    hostEmail = club?.ContactEmail ?? "";
                }
                else
                {
                    var region = FindRegionByCode(comp.GetValue<string>("regionalFederation") ?? "");
                    if (region is not null)
                    {
                        hostName = region.GetValue<string>("regionName") ?? region.Name ?? "";
                        hostEmail = region.GetValue<string>("contactEmail") ?? "";
                    }
                }

                var compEmail = comp.GetValue<string>("contactEmail") ?? "";
                var email = string.IsNullOrWhiteSpace(compEmail) ? hostEmail : compEmail;

                return MailReplyTo.FromClub(hostName, email);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReplyContactResolver: kunde inte läsa tävling {Id}.", competitionId);
                return MailReplyTo.SiteAdmin;
            }
        }

        /// <summary>
        /// Kretsnoden för en regionkod.
        ///
        /// <para>Samma uppslagning som <c>ReceiptModelBuilder.FindRegionByCode</c>. Jämförelsen är
        /// skiftlägesokänslig, eftersom <c>NormalizeRegionCode</c> gemenar koden på en del
        /// kodvägar medan nodens egen kod bär versal begynnelsebokstav.</para>
        /// </summary>
        private IContent? FindRegionByCode(string regionCode)
        {
            if (string.IsNullOrWhiteSpace(regionCode)) return null;

            var root = _content.GetRootContent().FirstOrDefault();
            if (root is null) return null;

            var children = _content.GetPagedChildren(root.Id, 0, int.MaxValue, out _);
            return children.FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage"
                && (c.GetValue<string>("regionCode") ?? "").Equals(regionCode, StringComparison.OrdinalIgnoreCase));
        }
    }
}
