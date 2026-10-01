using HpskSite.Models;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Kretsens uppdrag (kretsgranskning fas 1): Resultatgranskare, Bangranskare, Tävlingsansvarig.
    ///
    /// <para><b>⚠️ ENDA STÄLLET</b> där frågan "har den här medlemmen uppdraget X i krets Y?"
    /// besvaras och där mottagarlistan byggs. Granskningen av resultat (fas 2), av banan (fas 3)
    /// och ansökningarna (fas 4) frågar alla här. En andra kopia av regeln hade glidit isär — och
    /// felläget är att ett ärende hamnar hos ingen.</para>
    ///
    /// <para><b>Behörigheten är HÄRLEDD ur den aktiva rollraden.</b> <c>BoardRoles</c> mjukraderar
    /// (<c>IsActive = 0</c>) när någon avgår, så uppdraget upphör i samma sekund — inga grupper att
    /// hålla i takt, inget städjobb. Samma princip som föreningsintygsansvarig.</para>
    ///
    /// <para><b>Flera personer kan ha samma uppdrag.</b> Alla aviseras och vem som helst av dem kan
    /// besluta, annars fastnar ett ärende när en person är bortrest.</para>
    /// </summary>
    public class KretsUppdragService
    {
        private readonly BoardRoleService _roles;
        private readonly IMemberService _members;
        private readonly IContentService _content;
        private readonly ILogger<KretsUppdragService> _logger;

        public KretsUppdragService(
            BoardRoleService roles,
            IMemberService members,
            IContentService content,
            ILogger<KretsUppdragService> logger)
        {
            _roles = roles;
            _members = members;
            _content = content;
            _logger = logger;
        }

        /// <summary>Har medlemmen uppdraget, AKTIVT, i kretsen?</summary>
        public bool HasUppdrag(int regionId, int memberId, string roleKey)
        {
            if (regionId <= 0 || memberId <= 0 || !BoardRoleDefinitions.IsKretsUppdrag(roleKey)) return false;
            return _roles.HasActiveRole(DocumentOwnerType.Region, regionId, memberId, roleKey);
        }

        /// <summary>Aktiva innehavare av uppdraget, med namn och e-post.</summary>
        public List<KretsUppdragHolder> Holders(int regionId, string roleKey)
        {
            if (regionId <= 0 || !BoardRoleDefinitions.IsKretsUppdrag(roleKey)) return new();

            return _roles.GetActiveRoleHolders(DocumentOwnerType.Region, regionId, roleKey)
                .GroupBy(r => r.MemberId)               // en person kan ha samma roll två gånger (Custom-titel)
                .Select(g => g.First())
                .Select(r => new KretsUppdragHolder(r.MemberId, r.MemberName ?? "", EmailOf(r.MemberId)))
                .ToList();
        }

        /// <summary>
        /// Vilka av kretsens tre uppdrag saknar innehavare? Driver varningen i kretsens adminpanel
        /// och länkläget (en krets utan granskare får ärendet via länk).
        /// </summary>
        public List<KretsUppdragStatus> Status(int regionId)
        {
            return BoardRoleDefinitions.KretsUppdragRoleKeys
                .Select(k => new KretsUppdragStatus(k, BoardRoleDefinitions.GetLabel(k), Holders(regionId, k)))
                .ToList();
        }

        /// <summary>
        /// Vart en avisering om ett ärende för uppdraget ska gå.
        ///
        /// <para><b>⚠️ FALLER ALDRIG TILLBAKA PÅ INGENTING.</b> Saknar kretsen innehavare går
        /// aviseringen till kretsens kontaktadress OCH kretsens administratörer, och svaret säger
        /// att det är en reserv (<see cref="KretsRecipients.IsFallback"/>) så att anroparen kan
        /// välja länkläget. Ett tomt svar hade betytt ett ärende som ingen någonsin får veta om.</para>
        ///
        /// <para>Adresser utan e-post hoppas över men räknas (<see cref="KretsRecipients.MissingEmail"/>)
        /// — en innehavare som inte kan nås ska synas, inte tyst falla bort.</para>
        /// </summary>
        public KretsRecipients Recipients(int regionId, string roleKey)
        {
            var region = Region(regionId);
            var regionName = region?.GetValue<string>("regionName") ?? region?.Name ?? "";
            var holders = Holders(regionId, roleKey);

            if (holders.Count > 0)
            {
                var withEmail = holders.Where(h => !string.IsNullOrWhiteSpace(h.Email)).ToList();
                var missing = holders.Where(h => string.IsNullOrWhiteSpace(h.Email)).Select(h => h.Name).ToList();
                if (withEmail.Count > 0)
                    return new KretsRecipients(regionName,
                        withEmail.Select(h => new KretsRecipient(h.Email!, h.Name, h.MemberId)).ToList(),
                        IsFallback: false, MissingEmail: missing);
                // Innehavare finns men ingen kan nås: behandla som reserv, men behåll namnen.
                return Fallback(region, regionName, missing);
            }

            return Fallback(region, regionName, new List<string>());
        }

        private KretsRecipients Fallback(IContent? region, string regionName, List<string> missing)
        {
            var list = new List<KretsRecipient>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var contact = region?.GetValue<string>("contactEmail") ?? "";
            if (!string.IsNullOrWhiteSpace(contact) && seen.Add(contact.Trim()))
                list.Add(new KretsRecipient(contact.Trim(), regionName, 0));

            var code = region?.GetValue<string>("regionCode") ?? "";
            if (!string.IsNullOrWhiteSpace(code))
            {
                foreach (var m in MembersInGroup($"RegionalAdmin_{code}"))
                {
                    if (string.IsNullOrWhiteSpace(m.Email) || !seen.Add(m.Email.Trim())) continue;
                    list.Add(new KretsRecipient(m.Email.Trim(), DisplayName(m), m.Id));
                }
            }

            return new KretsRecipients(regionName, list, IsFallback: true, MissingEmail: missing);
        }

        /// <summary>
        /// Kretsar där medlemmen har uppdraget — granskarens egen vy listar ärenden från dem.
        /// </summary>
        public List<int> RegionsWhereMemberHas(int memberId, string roleKey)
        {
            if (memberId <= 0 || !BoardRoleDefinitions.IsKretsUppdrag(roleKey)) return new();
            return _roles.GetBoardMembershipsAnyRole(memberId, roleKey)
                .Where(x => x.OwnerType == DocumentOwnerType.Region)
                .Select(x => x.OwnerId)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Vilken väg behörigheten kom, för stämpeln på ett beslut. En sajtadmin passerar (support),
        /// men det ska synas att det inte var kretsens egen granskare.
        /// </summary>
        public KretsAuthority Authority(int regionId, int memberId, string roleKey, bool isSiteAdmin)
        {
            if (HasUppdrag(regionId, memberId, roleKey)) return KretsAuthority.Uppdrag;
            if (isSiteAdmin) return KretsAuthority.SiteAdmin;
            return KretsAuthority.None;
        }

        // ── Internt ──────────────────────────────────────────────────────────────────────────

        private IContent? Region(int regionId)
        {
            if (regionId <= 0) return null;
            try
            {
                var node = _content.GetById(regionId);
                return node?.ContentType.Alias == "regionalPage" ? node : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KretsUppdragService: kunde inte läsa krets {Id}.", regionId);
                return null;
            }
        }

        private string? EmailOf(int memberId)
        {
            try { return _members.GetById(memberId)?.Email; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KretsUppdragService: kunde inte läsa medlem {Id}.", memberId);
                return null;
            }
        }

        private IEnumerable<IMember> MembersInGroup(string group)
        {
            try { return _members.GetMembersByGroup(group) ?? Enumerable.Empty<IMember>(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KretsUppdragService: kunde inte läsa gruppen {Group}.", group);
                return Enumerable.Empty<IMember>();
            }
        }

        private static string DisplayName(IMember m)
        {
            var n = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            return n.Length > 0 ? n : (m.Name ?? "");
        }
    }

    public record KretsUppdragHolder(int MemberId, string Name, string? Email);

    public record KretsUppdragStatus(string RoleKey, string Label, List<KretsUppdragHolder> Holders)
    {
        public bool IsMissing => Holders.Count == 0;
    }

    public record KretsRecipient(string Email, string Name, int MemberId);

    public record KretsRecipients(string RegionName, List<KretsRecipient> To, bool IsFallback, List<string> MissingEmail)
    {
        public bool IsEmpty => To.Count == 0;
    }

    public enum KretsAuthority
    {
        None = 0,
        /// <summary>Medlemmen har uppdraget i kretsen.</summary>
        Uppdrag = 1,
        /// <summary>Sajtadministratör (support) — stämplas så att det syns.</summary>
        SiteAdmin = 2
    }
}
