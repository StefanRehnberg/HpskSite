using HpskSite.Models;
using HpskSite.Models.CompetitionFees;
using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services.CompetitionFees
{
    /// <summary>
    /// Tävlingsavgifterna i den nya modellen (P3/P4). Design: <c>notes/fakturamodellen-design-2026-09-24.md</c>.
    ///
    /// <para><b>⚠️⚠️ AVGIFT FÖRST, FAKTURA BARA NÄR ARRANGÖREN BUNTAR.</b> Varje anmälan och varje lag
    /// har en eller två <b>begärda</b> betalningar i liggaren (<see cref="LedgerPayment"/> utan
    /// <c>ConfirmedUtc</c>). De är inte pengar och inte intäkter. Skytten betalar direkt; en klubb
    /// som ska betala får en faktura (<see cref="LedgerChargeService"/>) som arrangören ställer ut i
    /// efterhand, och klubben behöver aldrig logga in.</para>
    ///
    /// <para><b>⚠️ Den enda skrivvägen för avgiftsraderna är <see cref="SyncRegistration"/> och
    /// <see cref="SyncTeam"/>.</b> De läser vad som är skyldigt ur anmälan och tävlingen, jämför med
    /// liggaren och gör planens ändringar (<see cref="CompetitionFeePlanner.Plan"/>). Anropas efter
    /// varje ändring av en anmälan — idempotent, så ett extra anrop kostar inget.</para>
    ///
    /// <para><b>⚠️ Utställaren är alltid den SKARPA liggaren</b> (tävlingens klubb eller krets, via
    /// <see cref="LedgerIssuerResolver.ResolveForCompetition"/>) — samma som evenemangen. En tävling
    /// har riktiga skyttar och riktiga pengar; sandlådan provar bokföringen, inte anmälan.</para>
    /// </summary>
    public class CompetitionFeeService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly LedgerPaymentService _payments;
        private readonly LedgerIssuerResolver _issuers;
        private readonly CompetitionPaymentModelService _models;
        private readonly MemberClubService _memberClubs;
        private readonly ILogger<CompetitionFeeService> _logger;

        public CompetitionFeeService(
            IUmbracoDatabaseFactory databaseFactory,
            IContentService contentService,
            IMemberService memberService,
            ClubService clubService,
            LedgerPaymentService payments,
            LedgerIssuerResolver issuers,
            CompetitionPaymentModelService models,
            MemberClubService memberClubs,
            ILogger<CompetitionFeeService> logger)
        {
            _memberClubs = memberClubs;
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _memberService = memberService;
            _clubService = clubService;
            _payments = payments;
            _issuers = issuers;
            _models = models;
            _logger = logger;
        }

        // ── Inställningar och val ────────────────────────────────────────────────────────────

        public CompetitionFeeSettings GetSettings(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.SingleOrDefault<CompetitionFeeSettings>(
                       "SELECT * FROM dbo.CompetitionFeeSettings WHERE CompetitionId = @0", competitionId)
                   ?? new CompetitionFeeSettings { CompetitionId = competitionId };
        }

        /// <summary>
        /// Sparar vilka anmälningstyper klubben får betala för.
        /// <para>⚠️ Räknar INTE om befintliga anmälningar. En skytt som redan betalat hela sin avgift
        /// ska inte plötsligt ha en klubbdel — valet gäller från nästa anmälan, och den som vill
        /// ändra en befintlig gör det på anmälan.</para>
        /// </summary>
        public CompetitionFeeSettings SaveSettings(int competitionId, IEnumerable<string>? types, int byMemberId)
        {
            var csv = CompetitionFeeTypes.Format(types);
            using var db = _databaseFactory.CreateDatabase();
            db.Execute(
                @"MERGE dbo.CompetitionFeeSettings AS t
                  USING (SELECT @0 AS CompetitionId) AS s ON t.CompetitionId = s.CompetitionId
                  WHEN MATCHED THEN UPDATE SET ClubPayableTypes = @1, UpdatedUtc = GETUTCDATE(), UpdatedByMemberId = @2
                  WHEN NOT MATCHED THEN INSERT (CompetitionId, ClubPayableTypes, UpdatedUtc, UpdatedByMemberId)
                       VALUES (@0, @1, GETUTCDATE(), @2);",
                competitionId, csv, byMemberId);
            return GetSettings(competitionId);
        }

        public bool GetClubPaysChoice(int registrationId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM dbo.CompetitionFeeChoice WHERE RegistrationId = @0 AND ClubPays = 1",
                registrationId) > 0;
        }

        public void SetClubPaysChoice(int competitionId, int registrationId, bool clubPays, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            db.Execute(
                @"MERGE dbo.CompetitionFeeChoice AS t
                  USING (SELECT @0 AS RegistrationId) AS s ON t.RegistrationId = s.RegistrationId
                  WHEN MATCHED THEN UPDATE SET ClubPays = @2, ChosenByMemberId = @3, ChosenUtc = GETUTCDATE()
                  WHEN NOT MATCHED THEN INSERT (RegistrationId, CompetitionId, ClubPays, ChosenByMemberId, ChosenUtc)
                       VALUES (@0, @1, @2, @3, GETUTCDATE());",
                registrationId, competitionId, clubPays, byMemberId);
        }

        // ── Läsning av källorna ──────────────────────────────────────────────────────────────

        /// <summary>En anmälan som avgifterna ser den.</summary>
        public sealed class RegistrationInfo
        {
            public int Id { get; init; }
            public int CompetitionId { get; init; }
            public int MemberId { get; init; }
            public string MemberName { get; init; } = "";
            public int ClubId { get; init; }
            public List<string> Classes { get; init; } = new();
            public bool IsSubCompetition { get; init; }
        }

        public sealed class TeamInfo
        {
            public int Id { get; set; }
            public int CompetitionId { get; set; }
            public string TeamName { get; set; } = "";
            public int ClubId { get; set; }
            public bool IsRelay { get; set; }
        }

        /// <summary>
        /// Anmälan ur INNEHÅLLSTJÄNSTEN, inte den publicerade cachen.
        /// <para>⚠️ Anmälan publiceras i bakgrunden tio sekunder efter att den sparats, och avgiften
        /// ska finnas från första sekunden. Samma skäl som resten av anmälningslistorna läser
        /// utkastträdet (se <c>competition-registrations-unpublished</c>).</para>
        /// </summary>
        public RegistrationInfo? LoadRegistration(int registrationId)
        {
            var reg = _contentService.GetById(registrationId);
            if (reg == null || reg.Trashed || reg.ContentType.Alias != "competitionRegistration") return null;
            return WithPrimaryClubFallback(new List<RegistrationInfo> { ToInfo(reg) })[0];
        }

        /// <summary>
        /// En anmälan utan klubb får medlemmens primärklubb — samma återfall som anmälningslistorna.
        /// <para>⚠️ Anmälningar vid disken lagrades länge med <c>clubId = 0</c> (<c>primaryClubId</c> är en
        /// STRÄNG och lästes som int). Utan återfallet hade de aldrig kunnat faktureras en klubb.
        /// Medlemmarna läses i EN omgång, aldrig en fråga per anmälan.</para>
        /// </summary>
        private List<RegistrationInfo> WithPrimaryClubFallback(List<RegistrationInfo> list)
        {
            var missing = list.Where(r => r.ClubId <= 0 && r.MemberId > 0).Select(r => r.MemberId).Distinct().ToArray();
            if (missing.Length == 0) return list;

            var primary = _memberService.GetAllMembers(missing)
                .ToDictionary(m => m.Id, m => _memberClubs.GetPrimaryClubId(m));

            return list.Select(r => r.ClubId > 0 || !primary.TryGetValue(r.MemberId, out var club) || club <= 0
                    ? r
                    : new RegistrationInfo
                    {
                        Id = r.Id, CompetitionId = r.CompetitionId, MemberId = r.MemberId, MemberName = r.MemberName,
                        ClubId = club, Classes = r.Classes, IsSubCompetition = r.IsSubCompetition
                    })
                .ToList();
        }

        private static RegistrationInfo ToInfo(IContent reg)
        {
            var classes = new List<string>();
            try
            {
                classes = CompetitionRegistrationDocument
                    .DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? "")
                    .Select(e => e.Class)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();
            }
            catch
            {
                // En oläsbar klasslista ger ingen avgift — och det syns som "Ingen avgift" på raden.
            }

            return new RegistrationInfo
            {
                Id = reg.Id,
                CompetitionId = ReadInt(reg, "competitionId"),
                MemberId = ReadInt(reg, "memberId"),
                MemberName = reg.GetValue<string>("memberName") ?? reg.Name ?? "",
                // ⚠️ clubId är ANMÄLANS klubb, inte medlemmens primärklubb — se PayerClubId.
                ClubId = ReadInt(reg, "clubId"),
                Classes = classes,
                IsSubCompetition = reg.HasProperty("isSubCompetition") && reg.GetValue<bool>("isSubCompetition")
            };
        }

        /// <summary>
        /// Alla anmälningar på tävlingen. <b>⚠️ Utkastträdet</b> — se <see cref="LoadRegistration"/>.
        /// </summary>
        public List<RegistrationInfo> LoadRegistrations(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return new List<RegistrationInfo>();

            var hubs = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistrationsHub")
                .ToList();

            var list = new List<RegistrationInfo>();
            foreach (var hub in hubs)
            {
                long page = 0;
                while (true)
                {
                    var children = _contentService.GetPagedChildren(hub.Id, page, 500, out var total).ToList();
                    list.AddRange(children
                        .Where(c => c.ContentType.Alias == "competitionRegistration" && !c.Trashed)
                        .Select(ToInfo));
                    if ((page + 1) * 500 >= total || children.Count == 0) break;
                    page++;
                }
            }
            return WithPrimaryClubFallback(list);
        }

        public List<TeamInfo> LoadTeams(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<TeamInfo>(
                "SELECT Id, CompetitionId, TeamName, ClubId, IsRelay FROM dbo.CompetitionTeam WHERE CompetitionId = @0",
                competitionId);
        }

        public TeamInfo? LoadTeam(int teamId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.FirstOrDefault<TeamInfo>(
                "SELECT Id, CompetitionId, TeamName, ClubId, IsRelay FROM dbo.CompetitionTeam WHERE Id = @0", teamId);
        }

        /// <summary>Alla avgiftsrader på tävlingen — anmälningar och lag, alla lägen.</summary>
        public List<LedgerPayment> LoadFeeRows(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            // LEDGER-SEAM-OK: tävlingsavgifter bokförs alltid i den skarpa liggaren (se klassens
            // kommentar). dbo med flit; utställaren är tävlingens klubb eller krets.
            return db.Fetch<LedgerPayment>(
                @"SELECT * FROM dbo.LedgerPayment
                   WHERE SourceId = @0 AND SourceType IN (@1, @2)
                   ORDER BY Id",
                competitionId, LedgerSourceType.CompetitionRegistration, LedgerSourceType.TeamFee);
        }

        /// <summary>
        /// Vad fakturorna täcker, per buntad avgiftsrad.
        /// <para>Nettot är fakturaradens belopp plus kreditnotornas (negativa) rader för samma avgift,
        /// över fakturor som inte är makulerade. En makulerad faktura täcker ingenting — och då ger
        /// planen avgiften en ny begäran.</para>
        /// </summary>
        public Dictionary<int, ChargeCoverage> LoadCoverage(IEnumerable<LedgerPayment> rows)
        {
            var ids = rows.Where(r => r.CoveredByChargeId is not null).Select(r => r.Id).Distinct().ToList();
            var result = new Dictionary<int, ChargeCoverage>();
            if (ids.Count == 0) return result;

            using var db = _databaseFactory.CreateDatabase();
            // ⚠️ IN-listan i omgångar om 500 — SQL Servers parametertak (se sql-tysta-fallor).
            foreach (var chunk in ids.Chunk(500))
            {
                var lines = db.Fetch<CoverageRow>(
                    @"SELECT l.PaymentId, c.Id AS ChargeId, c.Kind, c.CreditsChargeId, l.Amount
                        FROM dbo.LedgerChargeLine l
                        JOIN dbo.LedgerCharge c ON c.Id = l.ChargeId
                       WHERE c.VoidedUtc IS NULL AND l.PaymentId IN (@0)",
                    chunk.ToList());

                foreach (var g in lines.GroupBy(l => l.PaymentId))
                {
                    var invoiceId = g.Where(l => l.Kind == LedgerChargeKind.Invoice).Select(l => l.ChargeId).FirstOrDefault();
                    result[g.Key] = new ChargeCoverage(g.Key, invoiceId, Math.Max(0m, g.Sum(l => l.Amount)), false);
                }
            }

            // Är fakturan betald? Räknas en gång per faktura.
            var settled = new Dictionary<int, bool>();
            foreach (var chargeId in result.Values.Select(v => v.ChargeId).Where(i => i != 0).Distinct())
                settled[chargeId] = IsChargeSettled(db, chargeId);

            foreach (var key in result.Keys.ToList())
            {
                var c = result[key];
                result[key] = c with { ChargeSettled = settled.GetValueOrDefault(c.ChargeId) };
            }
            return result;
        }

        private static bool IsChargeSettled(IUmbracoDatabase db, int chargeId)
        {
            var invoice = db.SingleOrDefault<LedgerCharge>("SELECT * FROM dbo.LedgerCharge WHERE Id = @0", chargeId);
            if (invoice == null) return false;
            var credits = db.Fetch<LedgerCharge>("SELECT * FROM dbo.LedgerCharge WHERE CreditsChargeId = @0", chargeId);
            var pays = db.Fetch<LedgerPayment>("SELECT * FROM dbo.LedgerPayment WHERE ChargeId = @0", chargeId);
            return LedgerChargeBalance.For(invoice, credits, pays).IsSettled;
        }

        private sealed class CoverageRow
        {
            public int PaymentId { get; set; }
            public int ChargeId { get; set; }
            public string Kind { get; set; } = "";
            public int? CreditsChargeId { get; set; }
            public decimal Amount { get; set; }
        }

        // ── Det som är skyldigt ──────────────────────────────────────────────────────────────

        public List<FeePartAmount> DesiredForRegistration(IContent competition, RegistrationInfo reg, CompetitionFeeSettings settings, bool clubPays)
        {
            if (competition.GetValue<bool>("isExternal")) return new List<FeePartAmount>();
            var config = RegistrationFeeCalculator.ReadConfig(competition);
            return CompetitionFeePlanner.SplitRegistration(config, reg.Classes, reg.IsSubCompetition, clubPays, settings.ClubPayable);
        }

        public static decimal TeamFee(IContent competition, bool isRelay)
            => competition.GetValue<bool>("isExternal") ? 0m
               : RegistrationFeeCalculator.ReadFeeOrNull(competition, isRelay ? "stafettRegistrationFee" : "teamRegistrationFee") ?? 0m;

        // ── Synken ───────────────────────────────────────────────────────────────────────────

        public sealed class SyncResult
        {
            public bool Skipped { get; set; }
            public string? Error { get; set; }
            public int Voided { get; set; }
            public int Created { get; set; }
            public decimal Overpaid { get; set; }
        }

        /// <summary>
        /// Gör anmälans avgiftsrader rätt. Anmälan borta = inget skyldigt (öppna rader makuleras).
        /// <para>⚠️ Gör ingenting för en tävling i den gamla modellen — där är fakturorna sanningen.</para>
        /// </summary>
        public SyncResult SyncRegistration(int competitionId, int registrationId, int byMemberId)
        {
            // ⚠️ Här beslutas tävlingens modell vid första anmälan — varje anmälningsväg (självanmälan,
            // efteranmälan vid disken, laganmälan) passerar synken, så ingen väg kan glömma beslutet.
            if (_models.DecideAtFirstRegistration(competitionId) != CompetitionPaymentModels.Ledger)
                return new SyncResult { Skipped = true };

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return new SyncResult { Error = "Tävlingen finns inte." };

            var reg = LoadRegistration(registrationId);
            if (reg != null && reg.CompetitionId != 0 && reg.CompetitionId != competitionId)
                return new SyncResult { Error = "Anmälan hör inte till tävlingen." };

            var settings = GetSettings(competitionId);
            var desired = reg == null
                ? new List<FeePartAmount>()
                : DesiredForRegistration(competition, reg, settings, GetClubPaysChoice(registrationId));

            var rows = LoadFeeRows(competitionId)
                .Where(r => r.SourceType == LedgerSourceType.CompetitionRegistration && r.SourceItemId == registrationId)
                .ToList();

            return Apply(competitionId, desired, rows, byMemberId, part => new LedgerPayment
            {
                SourceType = LedgerSourceType.CompetitionRegistration,
                SourceId = competitionId,
                SourceItemId = registrationId,
                FeePart = part,
                PayerMemberId = reg!.MemberId > 0 ? reg.MemberId : null,
                PayerName = PayerNameFor(reg, part),
                PayerClubId = reg.ClubId > 0 ? reg.ClubId : null,
                Method = LedgerPaymentMethod.Swish
            });
        }

        /// <summary>Samma som <see cref="SyncRegistration"/>, för ett lag. Betalaren är lagets klubb.</summary>
        public SyncResult SyncTeam(int competitionId, int teamId, int byMemberId)
        {
            if (_models.DecideAtFirstRegistration(competitionId) != CompetitionPaymentModels.Ledger)
                return new SyncResult { Skipped = true };

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return new SyncResult { Error = "Tävlingen finns inte." };

            var team = LoadTeam(teamId);
            if (team != null && team.CompetitionId != competitionId)
                return new SyncResult { Error = "Laget hör inte till tävlingen." };

            var desired = team == null
                ? new List<FeePartAmount>()
                : CompetitionFeePlanner.SplitTeam(TeamFee(competition, team.IsRelay), team.IsRelay, GetSettings(competitionId).ClubPayable);

            var rows = LoadFeeRows(competitionId)
                .Where(r => r.SourceType == LedgerSourceType.TeamFee && r.SourceItemId == teamId)
                .ToList();

            var clubName = team == null ? "" : _clubService.GetClubNameById(team.ClubId) ?? "";
            return Apply(competitionId, desired, rows, byMemberId, part => new LedgerPayment
            {
                SourceType = LedgerSourceType.TeamFee,
                SourceId = competitionId,
                SourceItemId = teamId,
                FeePart = part,
                PayerMemberId = null,
                PayerName = string.IsNullOrWhiteSpace(clubName) ? team!.TeamName : $"{clubName} — {team!.TeamName}",
                PayerClubId = team.ClubId > 0 ? team.ClubId : null,
                Method = LedgerPaymentMethod.Swish
            });
        }

        /// <summary>
        /// Räknar om hela tävlingen: varje anmälan, varje lag, och varje rad vars anmälan eller lag
        /// inte längre finns. Knappen "Räkna om avgifterna" och efterarbetet efter en avgiftsändring.
        /// </summary>
        public (int Items, int Voided, int Created) SyncCompetition(int competitionId, int byMemberId)
        {
            if (!_models.IsLedger(competitionId)) return (0, 0, 0);

            var regIds = LoadRegistrations(competitionId).Select(r => r.Id).ToHashSet();
            var teamIds = LoadTeams(competitionId).Select(t => t.Id).ToHashSet();
            var rows = LoadFeeRows(competitionId);

            // Rader vars källa försvunnit — synkas som "ingenting skyldigt".
            foreach (var r in rows.Where(r => r.SourceItemId is > 0))
            {
                if (r.SourceType == LedgerSourceType.CompetitionRegistration) regIds.Add(r.SourceItemId!.Value);
                else if (r.SourceType == LedgerSourceType.TeamFee) teamIds.Add(r.SourceItemId!.Value);
            }

            int voided = 0, created = 0;
            foreach (var id in regIds)
            {
                var res = SyncRegistration(competitionId, id, byMemberId);
                voided += res.Voided; created += res.Created;
            }
            foreach (var id in teamIds)
            {
                var res = SyncTeam(competitionId, id, byMemberId);
                voided += res.Voided; created += res.Created;
            }
            return (regIds.Count + teamIds.Count, voided, created);
        }

        private SyncResult Apply(int competitionId, List<FeePartAmount> desired, List<LedgerPayment> rows,
            int byMemberId, Func<string, LedgerPayment> template)
        {
            var coverage = LoadCoverage(rows);
            var plan = CompetitionFeePlanner.Plan(desired, rows, coverage);
            var result = new SyncResult { Overpaid = plan.Overpaid };
            if (plan.IsNoop) return result;

            var issuer = _issuers.ResolveForCompetition(competitionId);
            if (issuer == null && plan.Create.Count > 0)
            {
                _logger.LogWarning("Tävling {CompetitionId} saknar utställare — avgiften kunde inte begäras.", competitionId);
                return new SyncResult { Error = "Arrangören går inte att avgöra." };
            }

            foreach (var id in plan.VoidPaymentIds)
                if (_payments.Void(id, byMemberId, "Avgiften ändrades — ersatt av en ny begäran."))
                    result.Voided++;

            foreach (var part in plan.Create)
            {
                var row = template(part.Part);
                row.IssuerType = issuer!.Value.Type;
                row.IssuerId = issuer.Value.Id;
                row.Amount = part.Amount;
                if (_payments.Request(row) != null) result.Created++;
            }
            return result;
        }

        private static string PayerNameFor(RegistrationInfo reg, string part)
            => string.IsNullOrWhiteSpace(reg.MemberName) ? $"Medlem {reg.MemberId}" : reg.MemberName;

        // ── Läsningen för ytorna ─────────────────────────────────────────────────────────────

        /// <summary>En rad i arrangörens avprickningslista: en anmälan eller ett lag.</summary>
        public sealed class FeeItem
        {
            public string ItemType { get; set; } = "";
            public int ItemId { get; set; }
            public string Name { get; set; } = "";
            public int? MemberId { get; set; }
            public int ClubId { get; set; }
            public string ClubName { get; set; } = "";
            public bool ClubPaysHint { get; set; }
            public FeeItemStatus Status { get; set; } = new();
            public List<LedgerPayment> Rows { get; set; } = new();
        }

        public sealed class CompetitionFeeOverview
        {
            public int CompetitionId { get; set; }
            public string CompetitionName { get; set; } = "";
            public string Model { get; set; } = "";
            public CompetitionFeeSettings Settings { get; set; } = new();
            public List<FeeItem> Items { get; set; } = new();
        }

        /// <summary>
        /// Arrangörens lista: varje anmälan och lag med sitt läge. Läser ALLA rader en gång och
        /// grupperar i minnet — aldrig en fråga per anmälan.
        /// </summary>
        public CompetitionFeeOverview BuildOverview(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            var overview = new CompetitionFeeOverview
            {
                CompetitionId = competitionId,
                CompetitionName = competition?.GetValue<string>("competitionName") ?? competition?.Name ?? "",
                Model = _models.Get(competitionId),
                Settings = GetSettings(competitionId)
            };
            if (competition == null) return overview;

            var regs = LoadRegistrations(competitionId);
            var teams = LoadTeams(competitionId);
            var rows = LoadFeeRows(competitionId);
            var coverage = LoadCoverage(rows);
            var config = RegistrationFeeCalculator.ReadConfig(competition);

            HashSet<int> clubPays;
            using (var db = _databaseFactory.CreateDatabase())
                clubPays = db.Fetch<int>(
                        "SELECT RegistrationId FROM dbo.CompetitionFeeChoice WHERE CompetitionId = @0 AND ClubPays = 1",
                        competitionId)
                    .ToHashSet();

            var clubNames = new Dictionary<int, string>();
            string ClubName(int id)
            {
                if (id <= 0) return "";
                if (!clubNames.TryGetValue(id, out var n))
                    clubNames[id] = n = _clubService.GetClubNameById(id) ?? $"Förening #{id}";
                return n;
            }

            var rowsByItem = rows
                .Where(r => r.SourceItemId is > 0)
                .GroupBy(r => (r.SourceType, r.SourceItemId!.Value))
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var reg in regs)
            {
                var myRows = rowsByItem.GetValueOrDefault((LedgerSourceType.CompetitionRegistration, reg.Id)) ?? new List<LedgerPayment>();
                var fee = competition.GetValue<bool>("isExternal") ? 0m
                    : reg.Classes.Sum(c => RegistrationFeeCalculator.FeeForClass(config, c, reg.IsSubCompetition))
                      + RegistrationFeeCalculator.PerRegistrationSurcharge(config, reg.IsSubCompetition);

                overview.Items.Add(new FeeItem
                {
                    ItemType = LedgerSourceType.CompetitionRegistration,
                    ItemId = reg.Id,
                    Name = reg.MemberName,
                    MemberId = reg.MemberId,
                    ClubId = reg.ClubId,
                    ClubName = ClubName(reg.ClubId),
                    ClubPaysHint = clubPays.Contains(reg.Id),
                    Status = FeeItemStatus.For(fee, myRows, coverage),
                    Rows = myRows
                });
            }

            foreach (var team in teams)
            {
                var myRows = rowsByItem.GetValueOrDefault((LedgerSourceType.TeamFee, team.Id)) ?? new List<LedgerPayment>();
                overview.Items.Add(new FeeItem
                {
                    ItemType = LedgerSourceType.TeamFee,
                    ItemId = team.Id,
                    Name = team.TeamName + (team.IsRelay ? " (stafett)" : ""),
                    ClubId = team.ClubId,
                    ClubName = ClubName(team.ClubId),
                    Status = FeeItemStatus.For(TeamFee(competition, team.IsRelay), myRows, coverage),
                    Rows = myRows
                });
            }

            // Rader vars anmälan eller lag är borta, men som fortfarande bär pengar eller en faktura.
            var known = overview.Items.Select(i => (i.ItemType, i.ItemId)).ToHashSet();
            foreach (var (key, orphanRows) in rowsByItem.Where(kv => !known.Contains(kv.Key)))
            {
                var status = FeeItemStatus.For(0m, orphanRows, coverage);
                if (status.Paid + status.Claimed + status.Invoiced + status.InvoicePaid <= 0) continue;
                overview.Items.Add(new FeeItem
                {
                    ItemType = key.SourceType,
                    ItemId = key.Value,
                    Name = (orphanRows.First().PayerName ?? "") + " (borttagen)",
                    MemberId = orphanRows.First().PayerMemberId,
                    ClubId = orphanRows.First().PayerClubId ?? 0,
                    ClubName = ClubName(orphanRows.First().PayerClubId ?? 0),
                    Status = status,
                    Rows = orphanRows
                });
            }

            overview.Items = overview.Items.OrderBy(i => i.ItemType == LedgerSourceType.TeamFee)
                .ThenBy(i => i.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), true))
                .ToList();
            return overview;
        }

        /// <summary>
        /// Vad en medlem själv ska betala på tävlingen: öppna rader på hennes anmälan som inte är
        /// klubbens del. Det är exakt det betalsteget visar.
        /// </summary>
        public List<LedgerPayment> OpenRowsForMember(int competitionId, int memberId)
            => LoadFeeRows(competitionId)
                .Where(r => r.SourceType == LedgerSourceType.CompetitionRegistration
                            && r.PayerMemberId == memberId
                            && r.VoidedUtc is null && r.ConfirmedUtc is null
                            && r.FeePart != CompetitionFeePart.Club)
                .ToList();

        /// <summary>Lagets öppna rad som ska betalas direkt (del <c>all</c>), eller null.</summary>
        public LedgerPayment? OpenRowForTeam(int competitionId, int teamId)
            => LoadFeeRows(competitionId)
                .FirstOrDefault(r => r.SourceType == LedgerSourceType.TeamFee && r.SourceItemId == teamId
                                     && r.VoidedUtc is null && r.ConfirmedUtc is null);

        /// <summary>
        /// Referensen på en avgiftsrad: tävlingens id, ett P och betalningens id. Kort nog för
        /// bankgirots 25 tecken, entydig, och går att slå upp i Anmälningars referenssök.
        /// </summary>
        public static string ReferenceFor(LedgerPayment row) => $"{row.SourceId}-P{row.Id}";

        private static int ReadInt(IContent content, string alias)
        {
            if (!content.HasProperty(alias)) return 0;
            var raw = content.GetValue<object>(alias)?.ToString();
            return int.TryParse(raw, out var v) ? v : 0;
        }
    }
}
