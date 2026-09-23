using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// One member's inputs for fee-charge generation. HouseholdId/HouseholdPrimary drive
    /// familjeavgift (§4.1); an empty HouseholdId means the member is billed individually.
    /// </summary>
    public class MemberFeeInput
    {
        public int MemberId { get; set; }
        public string? MembershipType { get; set; }
        public string? HouseholdId { get; set; }
        public bool HouseholdPrimary { get; set; }
    }

    /// <summary>
    /// Membership-fee (medlemsavgift) data access — fee categories per club/year and
    /// per-member charges. Follows the IScopeProvider CRUD pattern (see BoardRoleService)
    /// and reuses the invoice two-state claim/received model.
    /// See Documentation/MEMBER_DATABASE.md §4.
    /// </summary>
    public class MembershipFeeService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;

        /// <summary>
        /// Bron till verifikationsliggaren. ⚠️ <b>Lat med flit</b> — bryggan hänger på
        /// <c>LedgerPostingService</c>, och en direkt konstruktorberoende hade gjort avgiftsmodulen
        /// beroende av hela liggaren även för en klubb som inte bokför hos oss.
        /// </summary>
        private readonly Lazy<Ledger.LedgerMembershipFeeBridge> _ledgerBridge;
        private readonly ClubService _clubService;

        public MembershipFeeService(
            IScopeProvider scopeProvider,
            IMemberService memberService,
            Lazy<Ledger.LedgerMembershipFeeBridge> ledgerBridge,
            ClubService clubService)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _ledgerBridge = ledgerBridge;
            _clubService = clubService;
        }

        // ── Categories ────────────────────────────────────────────────

        public List<MembershipFeeCategory> GetCategories(int clubId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<MembershipFeeCategory>(
                "SELECT * FROM MembershipFeeCategory WHERE ClubId = @0 AND Year = @1 ORDER BY MembershipType, Label",
                clubId, year);
        }

        /// <summary>Insert (Id == 0) or update a fee category. Returns the saved row.</summary>
        public MembershipFeeCategory SaveCategory(MembershipFeeCategory cat)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            if (cat.Id > 0)
            {
                db.Update(cat);
            }
            else
            {
                cat.CreatedDate = DateTime.UtcNow;
                db.Insert(cat);
            }
            return cat;
        }

        public bool DeleteCategory(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var cat = db.SingleOrDefaultById<MembershipFeeCategory>(id);
            if (cat == null) return false;
            db.Delete(cat);
            return true;
        }

        // ── Charges ───────────────────────────────────────────────────

        public MembershipFeeCharge? GetCharge(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var charge = scope.Database.SingleOrDefaultById<MembershipFeeCharge>(id);
            if (charge == null) return null;

            if (charge.IsRegionFee)
            {
                charge.Lines = LoadLines(scope.Database, new[] { charge.Id })
                    .GetValueOrDefault(charge.Id) ?? new List<MembershipFeeChargeLine>();
                charge.PayerClubName = _clubService.GetClubNameById(charge.PayerClubId ?? 0);
            }
            else
            {
                ResolveMemberInfo(new List<MembershipFeeCharge> { charge });
            }
            return charge;
        }

        public List<MembershipFeeCharge> GetChargesForClubYear(int clubId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var charges = scope.Database.Fetch<MembershipFeeCharge>(
                "SELECT * FROM MembershipFeeCharge WHERE ClubId = @0 AND Year = @1 ORDER BY Id",
                clubId, year);
            ResolveMemberInfo(charges);
            return charges;
        }

        public MembershipFeeCharge? GetChargeForMemberYear(int memberId, int clubId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var charge = scope.Database.FirstOrDefault<MembershipFeeCharge>(
                "SELECT * FROM MembershipFeeCharge WHERE MemberId = @0 AND ClubId = @1 AND Year = @2",
                memberId, clubId, year);
            if (charge != null) ResolveMemberInfo(new List<MembershipFeeCharge> { charge });
            return charge;
        }

        /// <summary>
        /// Resolve MemberName + MemberEmail for a set of charges, batching distinct member
        /// lookups to avoid an N+1 cascade (same pattern as BoardRoleService.ResolveMemberNames).
        /// </summary>
        private void ResolveMemberInfo(List<MembershipFeeCharge> charges)
        {
            var byId = new Dictionary<int, (string Name, string Email)>();
            foreach (var memberId in charges.Select(c => c.MemberId).Distinct())
            {
                var member = _memberService.GetById(memberId);
                if (member == null) continue;
                var first = member.GetValue<string>("firstName") ?? "";
                var last = member.GetValue<string>("lastName") ?? "";
                var name = $"{first} {last}".Trim();
                byId[memberId] = (string.IsNullOrEmpty(name) ? member.Name : name, member.Email ?? "");
            }

            foreach (var charge in charges)
                if (byId.TryGetValue(charge.MemberId, out var info))
                {
                    charge.MemberName = info.Name;
                    charge.MemberEmail = info.Email;
                }
        }

        /// <summary>
        /// Create a charge for each supplied member that does not yet have one for the year,
        /// using the club's fee-category amount matching the member's membershipType. Members
        /// with no matching category are skipped. Returns the number of charges created.
        ///
        /// Familjeavgift (§4.1): members sharing a non-empty HouseholdId are billed as one
        /// household — the primary member (HouseholdPrimary, else the lowest MemberId) gets a
        /// single charge for the household's fee category; the other household members get a
        /// 0 kr "covered" charge referencing the primary's charge via HouseholdCoveredByChargeId,
        /// so they show as included rather than owing a separate fee.
        /// </summary>
        public int GenerateChargesForClub(int clubId, int year, IEnumerable<MemberFeeInput> members)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var categories = db.Fetch<MembershipFeeCategory>(
                "SELECT * FROM MembershipFeeCategory WHERE ClubId = @0 AND Year = @1", clubId, year);
            if (categories.Count == 0) return 0;

            // Case-insensitive lookup: membershipType -> category.
            var catByType = new Dictionary<string, MembershipFeeCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in categories)
                if (!string.IsNullOrWhiteSpace(c.MembershipType))
                    catByType[c.MembershipType.Trim()] = c;

            // Members that already have a charge this year (skip them).
            var existingMemberIds = db.Fetch<int>(
                "SELECT MemberId FROM MembershipFeeCharge WHERE ClubId = @0 AND Year = @1", clubId, year)
                .ToHashSet();

            var memberList = members.ToList();
            var created = 0;

            // Household groups: members with a non-empty HouseholdId, billed together.
            var households = memberList
                .Where(m => !string.IsNullOrWhiteSpace(m.HouseholdId))
                .GroupBy(m => m.HouseholdId!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1); // a lone member in a "household" is just an individual

            var householdMemberIds = new HashSet<int>();
            foreach (var group in households)
            {
                // Primary = the flagged member, else the lowest MemberId (deterministic).
                var primary = group.FirstOrDefault(m => m.HouseholdPrimary)
                              ?? group.OrderBy(m => m.MemberId).First();
                var primaryType = (primary.MembershipType ?? "").Trim();
                if (string.IsNullOrEmpty(primaryType) || !catByType.TryGetValue(primaryType, out var famCat))
                    continue; // no category for the household's membership type → leave as individuals

                foreach (var m in group) householdMemberIds.Add(m.MemberId);

                // Ensure the primary has a charge, and capture its id to link the covered members.
                int primaryChargeId;
                var existingPrimary = db.FirstOrDefault<MembershipFeeCharge>(
                    "SELECT * FROM MembershipFeeCharge WHERE MemberId = @0 AND ClubId = @1 AND Year = @2",
                    primary.MemberId, clubId, year);
                if (existingPrimary != null)
                {
                    primaryChargeId = existingPrimary.Id;
                }
                else
                {
                    var primaryCharge = new MembershipFeeCharge
                    {
                        MemberId = primary.MemberId,
                        ClubId = clubId,
                        Year = year,
                        CategoryId = famCat.Id,
                        Amount = famCat.Amount,
                        PaymentStatus = "Pending",
                        CreatedDate = DateTime.UtcNow
                    };
                    db.Insert(primaryCharge);
                    primaryChargeId = primaryCharge.Id;
                    existingMemberIds.Add(primary.MemberId);
                    created++;
                }

                // Covered members: 0 kr charge referencing the primary's charge.
                foreach (var m in group)
                {
                    if (m.MemberId == primary.MemberId) continue;
                    if (existingMemberIds.Contains(m.MemberId)) continue;
                    var covered = new MembershipFeeCharge
                    {
                        MemberId = m.MemberId,
                        ClubId = clubId,
                        Year = year,
                        CategoryId = famCat.Id,
                        Amount = 0m,
                        PaymentStatus = "Pending",
                        HouseholdCoveredByChargeId = primaryChargeId,
                        CreatedDate = DateTime.UtcNow
                    };
                    db.Insert(covered);
                    existingMemberIds.Add(m.MemberId);
                    created++;
                }
            }

            // Individuals (no household, or household without a matching category).
            foreach (var m in memberList)
            {
                if (householdMemberIds.Contains(m.MemberId)) continue;
                if (existingMemberIds.Contains(m.MemberId)) continue;

                var type = (m.MembershipType ?? "").Trim();
                if (string.IsNullOrEmpty(type)) continue;
                if (!catByType.TryGetValue(type, out var cat)) continue; // no matching category → skip

                var charge = new MembershipFeeCharge
                {
                    MemberId = m.MemberId,
                    ClubId = clubId,
                    Year = year,
                    CategoryId = cat.Id,
                    Amount = cat.Amount,
                    PaymentStatus = "Pending",
                    CreatedDate = DateTime.UtcNow
                };
                db.Insert(charge);
                existingMemberIds.Add(m.MemberId);
                created++;
            }

            return created;
        }

        /// <summary>
        /// Payer claim: record that the payer says they've paid. Does NOT set Paid — only the
        /// club admin confirms received (see MarkPaid).
        /// </summary>
        public bool SetPaymentSent(int chargeId, string sentBy)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var charge = db.SingleOrDefaultById<MembershipFeeCharge>(chargeId);
            if (charge == null) return false;
            if (charge.PaymentStatus == "Paid") return true; // already settled — nothing to claim

            charge.PaymentSentDate = DateTime.UtcNow;
            charge.PaymentSentBy = sentBy;
            db.Update(charge);
            return true;
        }

        public bool MarkPaid(int chargeId, int byMemberId)
        {
            // ⚠️⚠️ SCOPET MÅSTE STÄNGAS INNAN BOKFÖRINGEN. Bryggan öppnar en EGEN anslutning, och
            // körs den innanför det här scopet blockerar den på scopets egen oavslutade transaktion
            // mot MembershipFeeCharge — SQL Server väntar, och efter ~30 sekunder faller anropet på
            // "Execution Timeout Expired". Kassören fick alltså en yta som hängde en halv minut och
            // sedan bokförde ingenting, tyst. Mätt 2026-09-22, hittat av ett A/B som letade efter
            // något helt annat.
            //
            // Samma familj som ambient-scope-fällan som låste prod i tre timmar: ett andra
            // anslutningsgrepp inuti någon annans scope är alltid en låsning som väntar.
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                var charge = db.SingleOrDefaultById<MembershipFeeCharge>(chargeId);
                if (charge == null) return false;

                charge.PaymentStatus = "Paid";
                charge.PaidDate = DateTime.UtcNow;
                charge.PaidConfirmedByMemberId = byMemberId;
                db.Update(charge);
            }

            // ⚠️⚠️ MEDLEMSAVGIFTEN NÅDDE INTE BOKFÖRINGEN FÖRRÄN 2026-09-22. Avgiftsmodulen
            // skeppades juli 2026 med egen tabell och egen betalvägg, och MarkPaid satte bara en
            // status — klubbens STÖRSTA intäktspost fanns alltså inte i resultaträkningen.
            //
            // ⚠️ Bokföringen får ALDRIG fälla kvitteringen. Bryggan sväljer sina egna fel och
            // loggar; går posten inte igenom hamnar avgiften i "att bokföra" i stället, och
            // pengarna står kvar som mottagna. Att en klubb inte skulle kunna kvittera en
            // mottagen avgift för att liggaren säger ifrån vore att låta bokföringen stoppa
            // verkligheten.
            //
            // ⚠️ Anropas SYNKRONT, med flit. Ett Task.Run här hade tagit med sig den omgivande
            // Umbraco-scopen in i en bakgrundstråd — se ambient-scope-fällan som låste prod i 3 h.
            _ledgerBridge.Value.PostCharge(chargeId, byMemberId);

            return true;
        }

        public bool MarkUnpaid(int chargeId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var charge = db.SingleOrDefaultById<MembershipFeeCharge>(chargeId);
            if (charge == null) return false;

            charge.PaymentStatus = "Pending";
            charge.PaidDate = null;
            charge.PaidConfirmedByMemberId = null;
            db.Update(charge);
            return true;
        }

        // ══ KRETSAVGIFTEN ═══════════════════════════════════════════════════════════════════
        //
        // ⚠️⚠️ SAMMA MOTOR, EN ANNAN PARTSTYP. Planens regel: "Kretsavgiften är samma motor som
        //    medlemsavgiften … Bygg dem inte som två funktioner." Kraven ligger i samma tabell,
        //    betalas via samma /medlemsavgift/{token}-sida, kvitteras med samma MarkPaid och bokförs
        //    av samma brygga. Det enda som är eget är TAXAN och FÖRSLAGET, för en krets tar betalt
        //    per klubb, inte per medlemstyp.

        /// <summary>Kretsens taxa för ett år: grundavgift per klubb och pris per medlem.</summary>
        public (decimal Base, decimal PerMember) GetRegionRate(int regionId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var cats = scope.Database.Fetch<MembershipFeeCategory>(
                "SELECT * FROM MembershipFeeCategory WHERE IssuerType = @0 AND RegionId = @1 AND Year = @2",
                MembershipFeeIssuer.Region, regionId, year);

            return (cats.FirstOrDefault(c => c.MembershipType == RegionFeeCalculator.BaseCategory)?.Amount ?? 0m,
                    cats.FirstOrDefault(c => c.MembershipType == RegionFeeCalculator.PerMemberCategory)?.Amount ?? 0m);
        }

        /// <summary>
        /// Sparar kretsens taxa. <b>Ändrar inga befintliga krav</b> — ett utskickat krav är vad
        /// klubben har fått, och ett nytt pris gäller de krav som skapas efter det.
        /// </summary>
        public void SaveRegionRate(int regionId, int year, decimal baseAmount, decimal perMember)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            void Upsert(string key, string label, decimal amount)
            {
                var cat = db.FirstOrDefault<MembershipFeeCategory>(
                    "SELECT * FROM MembershipFeeCategory WHERE IssuerType = @0 AND RegionId = @1 AND Year = @2 AND MembershipType = @3",
                    MembershipFeeIssuer.Region, regionId, year, key);

                if (cat is null)
                {
                    db.Insert(new MembershipFeeCategory
                    {
                        IssuerType = MembershipFeeIssuer.Region,
                        RegionId = regionId,
                        ClubId = 0,
                        Year = year,
                        MembershipType = key,
                        Label = label,
                        Amount = amount,
                        CreatedDate = DateTime.UtcNow
                    });
                }
                else
                {
                    cat.Amount = amount;
                    db.Update(cat);
                }
            }

            Upsert(RegionFeeCalculator.BaseCategory, "Grundavgift per klubb", Math.Max(0m, baseAmount));
            Upsert(RegionFeeCalculator.PerMemberCategory, "Per medlem", Math.Max(0m, perMember));
        }

        /// <summary>Kretsens krav för ett år, med rader och klubbnamn.</summary>
        public List<MembershipFeeCharge> GetChargesForRegionYear(int regionId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var charges = scope.Database.Fetch<MembershipFeeCharge>(
                "SELECT * FROM MembershipFeeCharge WHERE IssuerType = @0 AND RegionId = @1 AND Year = @2 ORDER BY Id",
                MembershipFeeIssuer.Region, regionId, year);

            var lines = LoadLines(scope.Database, charges.Select(c => c.Id));
            foreach (var c in charges)
            {
                c.Lines = lines.GetValueOrDefault(c.Id) ?? new List<MembershipFeeChargeLine>();
                c.PayerClubName = _clubService.GetClubNameById(c.PayerClubId ?? 0);
            }
            return charges;
        }

        /// <summary>
        /// Skapar kretsens krav för de klubbar som inte redan har ett för året.
        ///
        /// <para><b>⚠️ Ett krav på noll kronor skapas aldrig.</b> En klubb utan medlemmar och utan
        /// grundavgift ska inte få en räkning på 0 kr — det läser som ett fel, och avgift 0 betyder
        /// gratis, inte "ofylld". Den räknas i stället i <c>SkippedZero</c> så ytan kan säga det.</para>
        /// </summary>
        public RegionFeeGenerateResult GenerateRegionCharges(
            int regionId, int year, IEnumerable<RegionFeeClubInput> clubs, int byMemberId)
        {
            var (baseAmount, perMember) = GetRegionRate(regionId, year);
            var result = new RegionFeeGenerateResult();

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var existing = db.Fetch<int>(
                    "SELECT PayerClubId FROM MembershipFeeCharge WHERE IssuerType = @0 AND RegionId = @1 AND Year = @2",
                    MembershipFeeIssuer.Region, regionId, year)
                .ToHashSet();

            foreach (var club in clubs.Where(c => c.ClubId > 0).GroupBy(c => c.ClubId).Select(g => g.First()))
            {
                if (existing.Contains(club.ClubId)) { result.SkippedExisting++; continue; }

                var count = Math.Max(0, club.MemberCount);
                var lines = RegionFeeCalculator.Lines(year, baseAmount, perMember, count, club.ManualAmount);
                if (lines.Count == 0) { result.SkippedZero++; continue; }

                var charge = new MembershipFeeCharge
                {
                    IssuerType = MembershipFeeIssuer.Region,
                    RegionId = regionId,
                    PayerClubId = club.ClubId,
                    // ⚠️ 0 och 0 — CK_MembershipFeeCharge_Shape kräver det. En kretsavgift får aldrig
                    //    synas i klubbens egen medlemsavgiftslista, som läser ClubId.
                    ClubId = 0,
                    MemberId = 0,
                    Year = year,
                    Amount = RegionFeeCalculator.Total(lines),
                    // Ett handskrivet belopp räknades inte på medlemmarna, så inget antal påstås.
                    MemberCount = club.ManualAmount is null ? count : null,
                    PaymentStatus = "Pending",
                    CreatedDate = DateTime.UtcNow
                };
                db.Insert(charge);

                foreach (var line in lines)
                {
                    line.ChargeId = charge.Id;
                    line.CreatedUtc = DateTime.UtcNow;
                    line.CreatedByMemberId = byMemberId;
                    db.Insert(line);
                }

                existing.Add(club.ClubId);
                result.Created++;
                result.TotalAmount += charge.Amount;
            }

            return result;
        }

        /// <summary>Lägger ett tillägg på ett obetalt krav, t.ex. en lagavgift.</summary>
        public string? AddRegionChargeLine(int chargeId, string description, decimal amount, int byMemberId)
        {
            var invalid = RegionFeeCalculator.ValidateExtra(description, amount);
            if (invalid is not null) return invalid;

            return EditRegionChargeLines(chargeId, (charge, lines) =>
            {
                lines.Add(new MembershipFeeChargeLine
                {
                    Kind = MembershipFeeLineKind.Extra,
                    Description = description.Trim(),
                    Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero),
                    CreatedByMemberId = byMemberId
                });
                return null;
            });
        }

        /// <summary>
        /// Tar bort en rad. <b>Den sista raden går inte att ta bort</b> — ett krav utan rader är ett
        /// krav på noll kronor som ändå står som obetalt. Ta bort kravet i stället.
        /// </summary>
        public string? RemoveRegionChargeLine(int chargeId, int lineId)
            => EditRegionChargeLines(chargeId, (charge, lines) =>
            {
                var line = lines.FirstOrDefault(l => l.Id == lineId);
                if (line is null) return "Raden hittades inte.";
                if (lines.Count == 1) return "Det är kravets enda rad. Ta bort hela kravet i stället.";
                lines.Remove(line);
                return null;
            });

        /// <summary>Rättar medlemsantalet på ett obetalt krav och räknar om per-medlem-raden.</summary>
        public string? SetRegionChargeMemberCount(int chargeId, int memberCount)
            => EditRegionChargeLines(chargeId, (charge, lines) =>
            {
                var (_, perMember) = GetRegionRate(charge.RegionId ?? 0, charge.Year);
                var error = RegionFeeCalculator.SetMemberCount(lines, charge.Year, perMember, memberCount);
                if (error is not null) return error;
                if (lines.Count == 0)
                    return "Kravet skulle bli noll kronor. Ta bort kravet i stället.";
                charge.MemberCount = memberCount;
                return null;
            });

        /// <summary>
        /// Tar bort ett krav som skapats fel. <b>Bara så länge klubben inte betalat eller sagt att
        /// den betalat</b> — efter det är kravet en del av vad parterna kommit överens om, och då ska
        /// det rättas, inte försvinna.
        /// </summary>
        public string? DeleteRegionCharge(int chargeId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var charge = db.SingleOrDefaultById<MembershipFeeCharge>(chargeId);

            if (charge is null || !charge.IsRegionFee) return "Kravet hittades inte.";
            if (charge.PaymentStatus == "Paid") return "Kravet är betalt och kan inte tas bort.";
            if (charge.PaymentSentDate is not null)
                return "Klubben har meddelat att den betalat. Stäm av med klubben innan kravet ändras.";

            // Raderna följer med genom FK-kaskaden.
            db.Delete(charge);
            return null;
        }

        /// <summary>
        /// Den gemensamma skrivvägen för radändringar: läser kravet och raderna, låter
        /// <paramref name="edit"/> ändra listan, skriver tillbaka ALLA rader och räknar om beloppet.
        ///
        /// <para><b>⚠️ Beloppet räknas om HÄR, en gång</b>, så att ingen ändring kan lämna ett krav
        /// vars rader och belopp säger olika saker.</para>
        /// </summary>
        private string? EditRegionChargeLines(
            int chargeId, Func<MembershipFeeCharge, List<MembershipFeeChargeLine>, string?> edit)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var charge = db.SingleOrDefaultById<MembershipFeeCharge>(chargeId);
            if (charge is null || !charge.IsRegionFee) return "Kravet hittades inte.";
            if (charge.PaymentStatus == "Paid") return "Kravet är betalt. Ett betalt krav ändras inte.";

            var lines = LoadLines(db, new[] { chargeId }).GetValueOrDefault(chargeId) ?? new List<MembershipFeeChargeLine>();

            var error = edit(charge, lines);
            if (error is not null) return error;

            db.Execute("DELETE FROM MembershipFeeChargeLine WHERE ChargeId = @0", chargeId);
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                l.Id = 0;
                l.ChargeId = chargeId;
                l.SortOrder = i;
                if (l.CreatedUtc == default) l.CreatedUtc = DateTime.UtcNow;
                db.Insert(l);
            }

            charge.Amount = RegionFeeCalculator.Total(lines);
            db.Update(charge);
            return null;
        }

        private static Dictionary<int, List<MembershipFeeChargeLine>> LoadLines(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, IEnumerable<int> chargeIds)
        {
            var ids = chargeIds.ToList();
            if (ids.Count == 0) return new Dictionary<int, List<MembershipFeeChargeLine>>();

            // ⚠️ Id:na skrivs in i SQL:en, inte som parametrar: IN (@0) tar slut kring 2100 parametrar
            //    och gör det tyst. Talen är heltal ur databasen, aldrig indata.
            return db.Fetch<MembershipFeeChargeLine>(
                    $"SELECT * FROM MembershipFeeChargeLine WHERE ChargeId IN ({string.Join(",", ids)}) ORDER BY ChargeId, SortOrder")
                .GroupBy(l => l.ChargeId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }
    }

    /// <summary>En klubb i kretsens förslag: registrets antal (eventuellt rättat) eller ett handskrivet belopp.</summary>
    public class RegionFeeClubInput
    {
        public int ClubId { get; set; }
        public int MemberCount { get; set; }

        /// <summary>Satt = kretsen har bestämt beloppet för klubben; formeln används inte.</summary>
        public decimal? ManualAmount { get; set; }
    }

    public class RegionFeeGenerateResult
    {
        public int Created { get; set; }
        public int SkippedExisting { get; set; }
        public int SkippedZero { get; set; }
        public decimal TotalAmount { get; set; }
    }
}
