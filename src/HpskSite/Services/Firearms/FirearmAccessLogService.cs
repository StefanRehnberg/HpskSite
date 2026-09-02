using HpskSite.Models.Firearms;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Skriver och läser <c>FirearmAccessLog</c>.
    ///
    /// <para><b>⚠️ Skrivningen är BEST-EFFORT och får aldrig fälla läsningen.</b> Att vägra visa en
    /// medlem hens egna vapenuppgifter för att en loggrad inte gick att skriva vore att göra en
    /// bokföringsmiss till ett driftavbrott. Ett misslyckande loggas i applikationsloggen i stället,
    /// där det syns utan att någon blir utelåst.</para>
    ///
    /// <para><b>Men den är inte VALFRI.</b> Anropas den inte finns läsningen inte, och löftet
    /// "du ser vem som läst" är då tomt. Regeln är: samma metod som lämnar ut klartext skriver
    /// raden — aldrig ett separat anrop en ny kodväg kan glömma.</para>
    /// </summary>
    public class FirearmAccessLogService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly ILogger<FirearmAccessLogService> _logger;

        public FirearmAccessLogService(
            IScopeProvider scopeProvider,
            IMemberService memberService,
            ClubService clubService,
            ILogger<FirearmAccessLogService> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _clubService = clubService;
            _logger = logger;
        }

        /// <summary>Registrerar en läsning. Kastar inte.</summary>
        public void Record(
            int readerMemberId,
            string reason,
            int? subjectMemberId = null,
            int? firearmId = null,
            int? readerClubId = null,
            string? note = null)
        {
            if (readerMemberId <= 0)
            {
                // En läsning utan läsare betyder att anropande lager tappat identiteten. Loggraden
                // hade blivit oanvändbar, och tystnad här hade gömt en riktig bugg i behörighetsvägen.
                _logger.LogError(
                    "FirearmAccessLog: läsning utan läsar-id (reason={Reason}, subject={Subject}, firearm={Firearm}). " +
                    "Anropande kod har tappat den inloggade medlemmen.",
                    reason, subjectMemberId, firearmId);
                return;
            }

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                uow.Database.Insert(new FirearmAccessLogEntry
                {
                    SubjectMemberId = subjectMemberId,
                    FirearmId = firearmId,
                    ReaderMemberId = readerMemberId,
                    ReaderClubId = readerClubId,
                    Reason = reason,
                    Note = Trim(note, 400),
                    OccurredAt = DateTime.Now,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "FirearmAccessLog: kunde inte registrera läsning (reader={Reader}, reason={Reason}, " +
                    "subject={Subject}). Läsningen genomfördes ändå.",
                    readerMemberId, reason, subjectMemberId);
            }
        }

        /// <summary>
        /// Medlemmens egen logg — "vem har läst mina uppgifter". Nyaste först.
        ///
        /// <para><b>⚠️ Medlemmens EGNA läsningar tas bort som standard.</b> De är den
        /// överväldigande majoriteten av raderna, och en logg där ens egna 200 besök begraver
        /// klubbens enda läsning svarar inte på den fråga den finns för. De går att visa med
        /// <paramref name="includeOwnReads"/>.</para>
        /// </summary>
        public List<FirearmAccessLogEntry> GetForSubject(int subjectMemberId, bool includeOwnReads = false, int max = 100)
        {
            if (subjectMemberId <= 0) return new List<FirearmAccessLogEntry>();

            List<FirearmAccessLogEntry> rows;
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);

                var sql = "SELECT TOP (@0) * FROM FirearmAccessLog WHERE SubjectMemberId = @1";
                if (!includeOwnReads) sql += " AND ReaderMemberId <> @1";
                sql += " ORDER BY OccurredAt DESC, Id DESC";

                rows = uow.Database.Fetch<FirearmAccessLogEntry>(sql, Math.Clamp(max, 1, 500), subjectMemberId);
            }
            catch (Exception ex)
            {
                // En omigrerad miljö har ingen tabell. En tom logg är rätt svar där — men den får
                // inte förväxlas med "ingen har läst": anroparen visar texten, vi loggar orsaken.
                _logger.LogWarning(ex, "FirearmAccessLog: kunde inte läsa loggen för medlem {MemberId}.", subjectMemberId);
                return new List<FirearmAccessLogEntry>();
            }

            ResolveNames(rows);
            return rows;
        }

        /// <summary>Senaste läsningen av någon ANNAN än medlemmen själv. Null = ingen har läst.</summary>
        public DateTime? LastForeignReadFor(int subjectMemberId)
        {
            if (subjectMemberId <= 0) return null;
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.ExecuteScalar<DateTime?>(
                    "SELECT MAX(OccurredAt) FROM FirearmAccessLog " +
                    "WHERE SubjectMemberId = @0 AND ReaderMemberId <> @0", subjectMemberId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FirearmAccessLog: kunde inte läsa senaste läsning för {MemberId}.", subjectMemberId);
                return null;
            }
        }

        /// <summary>
        /// Batchar namnuppslagen per distinkt medlem. Loggen renderas i en lista, och ett uppslag per
        /// rad är samma N+1-kaskad som redan städats bort i <c>BoardRoleService</c>.
        /// </summary>
        private void ResolveNames(List<FirearmAccessLogEntry> rows)
        {
            if (rows.Count == 0) return;

            var names = new Dictionary<int, string>();
            foreach (var id in rows.Select(r => r.ReaderMemberId).Distinct())
            {
                var member = _memberService.GetById(id);
                if (member is null) continue;

                var name = $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}".Trim();
                names[id] = string.IsNullOrEmpty(name) ? member.Name ?? $"Medlem {id}" : name;
            }

            var clubs = new Dictionary<int, string>();
            foreach (var id in rows.Where(r => r.ReaderClubId > 0).Select(r => r.ReaderClubId!.Value).Distinct())
            {
                // ⚠️ ClubService, aldrig IMemberService — klubbar är innehållsnoder, inte medlemmar.
                var clubName = _clubService.GetClubNameById(id);
                if (!string.IsNullOrEmpty(clubName)) clubs[id] = clubName;
            }

            foreach (var row in rows)
            {
                if (names.TryGetValue(row.ReaderMemberId, out var n)) row.ReaderName = n;
                if (row.ReaderClubId is int cid && clubs.TryGetValue(cid, out var c)) row.ReaderClubName = c;
            }
        }

        private static string? Trim(string? value, int max)
            => string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);
    }
}
