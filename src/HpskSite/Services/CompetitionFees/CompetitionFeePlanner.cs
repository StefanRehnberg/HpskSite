using HpskSite.Models.CompetitionFees;
using HpskSite.Models.Ledger;

namespace HpskSite.Services.CompetitionFees
{
    /// <summary>En del av en anmälans avgift: vem som väntas betala den, och hur mycket.</summary>
    public readonly record struct FeePartAmount(string Part, decimal Amount);

    /// <summary>
    /// Vad en avgiftsrad redan täcks av när den buntats in i en faktura.
    /// <para><see cref="NetCovered"/> är fakturaradens belopp minus kreditnotornas rader för samma
    /// avgift — noll när fakturan är makulerad eller raden krediterad bort.</para>
    /// </summary>
    public readonly record struct ChargeCoverage(int PaymentId, int ChargeId, decimal NetCovered, bool ChargeSettled);

    /// <summary>
    /// Planen för EN anmälan eller ETT lag: vilka öppna rader som ska makuleras och vilka som ska
    /// skapas för att liggaren ska spegla vad som faktiskt är skyldigt.
    /// </summary>
    public sealed class FeeSyncPlan
    {
        public List<int> VoidPaymentIds { get; } = new();
        public List<FeePartAmount> Create { get; } = new();

        /// <summary>
        /// Mer mottaget eller fakturerat än avgiften — t.ex. en klass struken efter betalning.
        /// <para>⚠️ Planen gör INGENTING åt det. Pengar tillbaka är arrangörens beslut (ångra betalningen
        /// eller kreditera fakturan), och det ska synas på raden, inte ske tyst.</para>
        /// </summary>
        public decimal Overpaid { get; set; }

        public bool IsNoop => VoidPaymentIds.Count == 0 && Create.Count == 0;
    }

    /// <summary>
    /// Tävlingsavgifternas rena regler. <b>Ingen databas</b> — allt prövas i enhetstester.
    ///
    /// <para><b>⚠️⚠️ AVGIFTEN ÄR EN BEGÄRD BETALNING, INTE EN FORDRAN.</b> En rad i liggaren utan
    /// <c>ConfirmedUtc</c> är ingenting annat än "det här väntar vi på". Därför får planen makulera
    /// och ersätta öppna rader fritt när avgiften ändras — det är evenemangens mönster, redan i
    /// drift. Det som ALDRIG rörs: en påstådd rad (någon säger att hen betalat), en mottagen rad
    /// (pengar), och en rad som buntats in i en faktura (en utfärdad handling).</para>
    /// </summary>
    public static class CompetitionFeePlanner
    {
        /// <summary>
        /// Delar en anmälans avgift.
        ///
        /// <para>Utan "Klubben betalar" — eller när ingen av klasserna är en typ arrangören tillåtit —
        /// är det en enda del som skytten betalar. Annars delas den <b>per klass</b> (Stefan
        /// 2026-09-24): klubbens del är de tillåtna klasserna, skyttens del resten.</para>
        ///
        /// <para><b>⚠️ Deltävlingsavgiften per anmälan hör inte till någon klass</b> och läggs på
        /// skyttens del (Stefans beslut 2026-09-24). Finns ingen skyttedel — alla klasser är
        /// klubbetalda — följer den med klubbens.</para>
        ///
        /// <para>Delarna summerar ALLTID till <see cref="RegistrationFeeCalculator"/>s belopp; de
        /// byggs ur samma två funktioner.</para>
        /// </summary>
        public static List<FeePartAmount> SplitRegistration(
            RegistrationFeeCalculator.FeeConfig config,
            IReadOnlyCollection<string> classes,
            bool isSubCompetition,
            bool clubPays,
            IReadOnlySet<string> clubPayableTypes)
        {
            decimal club = 0m, self = 0m;
            int selfClasses = 0;

            foreach (var cls in classes ?? Array.Empty<string>())
            {
                var fee = RegistrationFeeCalculator.FeeForClass(config, cls, isSubCompetition);
                if (clubPays && IsClubPayableClass(cls, clubPayableTypes))
                    club += fee;
                else
                {
                    self += fee;
                    selfClasses++;
                }
            }

            var surcharge = RegistrationFeeCalculator.PerRegistrationSurcharge(config, isSubCompetition);
            if (selfClasses > 0 || club <= 0) self += surcharge;
            else club += surcharge;

            var parts = new List<FeePartAmount>();
            if (club > 0 && self > 0)
            {
                parts.Add(new FeePartAmount(CompetitionFeePart.Self, self));
                parts.Add(new FeePartAmount(CompetitionFeePart.Club, club));
            }
            else if (club > 0)
                parts.Add(new FeePartAmount(CompetitionFeePart.Club, club));
            else if (self > 0)
                parts.Add(new FeePartAmount(CompetitionFeePart.All, self));

            return parts;
        }

        /// <summary>
        /// En klass anmälningstyp: junior eller individuell. Junior avgörs av samma regel som
        /// junioravgiften (<see cref="RegistrationFeeCalculator.IsJuniorClass"/>) — två tolkningar
        /// hade låtit en klass vara junior för avgiften men inte för vem som betalar.
        /// </summary>
        public static string TypeOfClass(string cls)
            => RegistrationFeeCalculator.IsJuniorClass(cls) ? CompetitionFeeTypes.Junior : CompetitionFeeTypes.Individual;

        public static bool IsClubPayableClass(string cls, IReadOnlySet<string> clubPayableTypes)
            => clubPayableTypes.Contains(TypeOfClass(cls));

        /// <summary>Får skytten alls välja "Klubben betalar" för de här klasserna?</summary>
        public static bool AnyClubPayable(IEnumerable<string> classes, IReadOnlySet<string> clubPayableTypes)
            => (classes ?? Array.Empty<string>()).Any(c => IsClubPayableClass(c, clubPayableTypes));

        /// <summary>
        /// Lagets avgift. Laget betalas alltid av sin klubb (<c>CompetitionTeam.ClubId</c>); frågan
        /// är bara om den måste betalas direkt när laget anmäls (<see cref="CompetitionFeePart.All"/>)
        /// eller får vänta på arrangörens faktura (<see cref="CompetitionFeePart.Club"/>).
        /// </summary>
        public static List<FeePartAmount> SplitTeam(decimal fee, bool isRelay, IReadOnlySet<string> clubPayableTypes)
        {
            if (fee <= 0) return new List<FeePartAmount>();
            var type = isRelay ? CompetitionFeeTypes.Relay : CompetitionFeeTypes.Team;
            return new List<FeePartAmount>
            {
                new(clubPayableTypes.Contains(type) ? CompetitionFeePart.Club : CompetitionFeePart.All, fee)
            };
        }

        /// <summary>
        /// Jämför det som är skyldigt med raderna som finns, och säger vad som ska ändras.
        ///
        /// <para><b>Täckning</b> är det som redan är gjort åt avgiften: mottagna pengar, påstådda
        /// betalningar och fakturerat belopp. Varje rads täckning räknas först mot sin egen del; det
        /// som blir över räknas mot de andra delarna. Det är vad som gör att en skytt som redan betalat
        /// allt, och sedan väljer "Klubben betalar", inte får en ny begäran för sin egen del.</para>
        ///
        /// <para><b>⚠️ Öppna, opåstådda rader är det enda planen makulerar.</b> En öppen rad med rätt
        /// belopp för rätt del behålls — annars hade varje omräkning bytt id på raden och gjort en
        /// QR-kod skytten redan har ogiltig.</para>
        /// </summary>
        public static FeeSyncPlan Plan(
            IReadOnlyList<FeePartAmount> desired,
            IReadOnlyList<LedgerPayment> rows,
            IReadOnlyDictionary<int, ChargeCoverage> coverage)
        {
            var plan = new FeeSyncPlan();

            // Täckning per del.
            var coveredByPart = new Dictionary<string, decimal>();
            void AddCover(string? part, decimal amount)
            {
                var key = part ?? CompetitionFeePart.All;
                coveredByPart[key] = coveredByPart.GetValueOrDefault(key) + amount;
            }

            var open = new List<LedgerPayment>();
            foreach (var r in rows)
            {
                if (r.VoidedUtc is null)
                {
                    if (r.ConfirmedUtc is not null) AddCover(r.FeePart, r.SettledAmount);
                    else if (r.ClaimedUtc is not null) AddCover(r.FeePart, r.Amount);
                    else open.Add(r);
                }
                else if (r.CoveredByChargeId is not null && coverage.TryGetValue(r.Id, out var c))
                {
                    AddCover(r.FeePart, c.NetCovered);
                }
            }

            // Egen täckning först, sedan poolen av överskott.
            var remainder = new Dictionary<string, decimal>();
            decimal pool = 0m;
            var desiredParts = desired.Select(d => d.Part).ToHashSet();

            foreach (var (part, amount) in coveredByPart)
                if (!desiredParts.Contains(part)) pool += amount;

            foreach (var d in desired)
            {
                var own = coveredByPart.GetValueOrDefault(d.Part);
                var need = d.Amount - own;
                if (need < 0) { pool += -need; need = 0; }
                remainder[d.Part] = need;
            }

            // Poolen fyller på delarna i den ordning de står (skyttens del före klubbens).
            foreach (var d in desired)
            {
                if (pool <= 0) break;
                var take = Math.Min(pool, remainder[d.Part]);
                remainder[d.Part] -= take;
                pool -= take;
            }

            plan.Overpaid = pool;

            // Öppna rader: behåll en per del med exakt rätt belopp, makulera resten.
            foreach (var d in desired)
            {
                var need = remainder[d.Part];
                var mine = open.Where(o => (o.FeePart ?? CompetitionFeePart.All) == d.Part).ToList();
                var keep = need > 0 ? mine.FirstOrDefault(o => o.Amount == need) : null;

                foreach (var o in mine.Where(o => o != keep))
                    plan.VoidPaymentIds.Add(o.Id);

                if (need > 0 && keep is null)
                    plan.Create.Add(new FeePartAmount(d.Part, need));
            }

            foreach (var o in open.Where(o => !desiredParts.Contains(o.FeePart ?? CompetitionFeePart.All)))
                plan.VoidPaymentIds.Add(o.Id);

            return plan;
        }
    }

    /// <summary>En avgifts läge som arrangören och skytten ser det. Härlett, aldrig lagrat.</summary>
    public sealed class FeeItemStatus
    {
        public decimal Fee { get; set; }
        public decimal Paid { get; set; }
        public decimal Claimed { get; set; }
        /// <summary>Buntat in i en faktura som ännu inte är betald.</summary>
        public decimal Invoiced { get; set; }
        /// <summary>Buntat in i en faktura som är betald.</summary>
        public decimal InvoicePaid { get; set; }
        /// <summary>Öppet som SKYTTEN ska betala.</summary>
        public decimal Open { get; set; }

        /// <summary>
        /// Öppet som KLUBBEN ska betala — klubbens del, som väntar på arrangörens faktura.
        /// <para>⚠️ Skild från <see cref="Open"/>: en skytt som betalat sin egen del ska inte stå som
        /// "obetald" bara för att klubbens del inte är fakturerad ännu. Det var första versionens fel.</para>
        /// </summary>
        public decimal AwaitingInvoice { get; set; }

        public decimal Overpaid { get; set; }

        /// <summary>
        /// Skyldigt men utan någon rad alls — t.ex. innan omräkningen hunnit köras, eller efter att
        /// avgiften höjts. ⚠️ Räknas som obetalt: utan den här termen hade en avgift utan rader
        /// visats som betald.
        /// </summary>
        public decimal Missing => Math.Max(0m, Fee - (Paid + Claimed + Invoiced + InvoicePaid + Open + AwaitingInvoice));

        public List<int> ChargeIds { get; } = new();

        /// <summary>
        /// Ur <see cref="FeeStatusKeys"/>. Det som väntar på någon avgör: öppet slår påstått, påstått
        /// slår fakturerat — arrangörens arbetslista ska visa det som kräver en handling.
        /// </summary>
        public string Key =>
            Fee <= 0 && Paid + Claimed + Invoiced + InvoicePaid <= 0 ? FeeStatusKeys.NoFee
            : Overpaid > 0 ? FeeStatusKeys.Overpaid
            : Open > 0 || Missing > 0 ? FeeStatusKeys.Unpaid
            : Claimed > 0 ? FeeStatusKeys.Claimed
            : AwaitingInvoice > 0 ? FeeStatusKeys.AwaitingInvoice
            : Invoiced > 0 ? FeeStatusKeys.Invoiced
            : FeeStatusKeys.Paid;

        public static FeeItemStatus For(
            decimal fee, IReadOnlyList<LedgerPayment> rows, IReadOnlyDictionary<int, ChargeCoverage> coverage)
        {
            var s = new FeeItemStatus { Fee = fee };
            foreach (var r in rows)
            {
                if (r.VoidedUtc is null)
                {
                    if (r.ConfirmedUtc is not null) s.Paid += r.SettledAmount;
                    else if (r.ClaimedUtc is not null) s.Claimed += r.Amount;
                    else if (r.FeePart == CompetitionFeePart.Club) s.AwaitingInvoice += r.Amount;
                    else s.Open += r.Amount;
                }
                else if (r.CoveredByChargeId is not null && coverage.TryGetValue(r.Id, out var c) && c.NetCovered > 0)
                {
                    if (c.ChargeSettled) s.InvoicePaid += c.NetCovered;
                    else s.Invoiced += c.NetCovered;
                    if (!s.ChargeIds.Contains(c.ChargeId)) s.ChargeIds.Add(c.ChargeId);
                }
            }
            s.Overpaid = Math.Max(0m, s.Paid + s.Claimed + s.Invoiced + s.InvoicePaid - fee);
            return s;
        }
    }

    public static class FeeStatusKeys
    {
        public const string NoFee = "no-fee";
        public const string Unpaid = "unpaid";
        public const string Claimed = "claimed";
        public const string AwaitingInvoice = "awaiting-invoice";
        public const string Invoiced = "invoiced";
        public const string Paid = "paid";
        public const string Overpaid = "overpaid";

        public static string Label(string key) => key switch
        {
            NoFee => "Ingen avgift",
            Unpaid => "Obetald",
            Claimed => "Betalning anmäld",
            AwaitingInvoice => "Klubben ska faktureras",
            Invoiced => "Fakturerad klubben",
            Paid => "Betald",
            Overpaid => "För mycket betalt",
            _ => key
        };
    }
}
