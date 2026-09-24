namespace HpskSite.Models
{
    /// <summary>
    /// Kretsavgiften för en klubb, som REN funktion. Ingen databas — regeln som avgör vad en klubb
    /// ska betala går att pröva utan att skapa ett enda krav.
    ///
    /// <para><b>⚠️⚠️ ALLA TRE SÄTTEN ANVÄNDS AV KRETSARNA</b> (Stefan 2026-09-23): fast belopp per
    /// klubb, belopp per medlem, och ett belopp kretsen skriver för hand. De två första är samma
    /// formel — <c>grund + pris × medlemmar</c> — med den ena delen noll; det tredje ersätter formeln
    /// för just den klubben. Därför EN formel och ett handskrivet belopp, inte tre lägen att välja
    /// mellan: en förening som tar 200 kr per klubb plus 20 kr per medlem (vilket Hallands siffror
    /// tyder på) skulle annars inte kunna uttryckas alls.</para>
    ///
    /// <para><b>⚠️ Medlemsantalet är ett FÖRSLAG ur registret</b> som kretsen får rätta, eftersom
    /// registret kan vara ofullständigt. Det tal som används sparas på kravet.</para>
    /// </summary>
    public static class RegionFeeCalculator
    {
        /// <summary>Kategorinyckeln för grundavgiften i <see cref="MembershipFeeCategory"/>.</summary>
        public const string BaseCategory = "krets-grund";

        /// <summary>Kategorinyckeln för priset per medlem.</summary>
        public const string PerMemberCategory = "krets-per-medlem";

        /// <summary>Priset per start i de valda tävlingarna. Kategorins Label bär radens namn.</summary>
        public const string PerStartCategory = "krets-per-start";

        /// <summary>Priset per vald tävling där klubben hade minst en start. Label = radens namn.</summary>
        public const string PerCompetitionCategory = "krets-per-tavling";

        /// <summary>
        /// Raderna för en klubb. <paramref name="manualAmount"/> satt ersätter formeln helt — kretsen
        /// har bestämt beloppet för just den klubben, och att dessutom lägga på grundavgiften vore
        /// att ta betalt två gånger.
        /// </summary>
        public static List<MembershipFeeChargeLine> Lines(
            int year, decimal baseAmount, decimal perMember, int memberCount, decimal? manualAmount)
            => Lines(year, new RegionFeeRate(baseAmount, perMember), memberCount, 0, 0, manualAmount);

        /// <summary>
        /// Raderna med hela taxan: grund, per medlem, per start och per tävling (Hallands tre rader:
        /// årsavgift per medlem, lagavgift per tävling, startavgifter per start). En del som är noll,
        /// eller vars antal är noll, ger ingen rad.
        /// </summary>
        public static List<MembershipFeeChargeLine> Lines(
            int year, RegionFeeRate rate, int memberCount, int startCount, int competitionCount, decimal? manualAmount)
        {
            var baseAmount = rate.BaseAmount;
            var perMember = rate.PerMember;
            var lines = new List<MembershipFeeChargeLine>();

            if (manualAmount is decimal manual)
            {
                if (manual > 0)
                    lines.Add(new MembershipFeeChargeLine
                    {
                        Kind = MembershipFeeLineKind.Manual,
                        Description = $"Kretsavgift {year}",
                        Amount = Round(manual)
                    });
                return Number(lines);
            }

            if (baseAmount > 0)
                lines.Add(new MembershipFeeChargeLine
                {
                    Kind = MembershipFeeLineKind.Base,
                    Description = $"Grundavgift {year}",
                    Amount = Round(baseAmount)
                });

            // ⚠️ Raden finns bara när det finns något att räkna. "0 medlemmar à 20 kr" på en räkning
            //    läser som ett fel i klubbens register, inte som att priset inte gällde dem.
            if (perMember > 0 && memberCount > 0)
                lines.Add(new MembershipFeeChargeLine
                {
                    Kind = MembershipFeeLineKind.PerMember,
                    // ⚠️ INTE "Medlemsavgift" — på kretsens räkning läses det som klubbens egen
                    //    medlemsavgift, alltså något helt annat.
                    Description = PerMemberText(year, memberCount, perMember),
                    Quantity = memberCount,
                    UnitPrice = perMember,
                    Amount = Round(perMember * memberCount)
                });

            // Samma regel som per medlem: ingen rad för "0 starter à 20 kr".
            if (rate.PerCompetition > 0 && competitionCount > 0)
                lines.Add(CountLine(MembershipFeeLineKind.PerCompetition, year, rate.CompetitionLabel,
                    competitionCount, CompetitionUnit(competitionCount), rate.PerCompetition));

            if (rate.PerStart > 0 && startCount > 0)
                lines.Add(CountLine(MembershipFeeLineKind.PerStart, year, rate.StartLabel,
                    startCount, StartUnit(startCount), rate.PerStart));

            return Number(lines);
        }

        /// <summary>
        /// Räknar om start- och tävlingsraderna när kretsen rättar antalen på en SKICKAD avgift — med
        /// det pris avgiften skickades med, precis som <see cref="SetMemberCount"/>. Övriga rader står kvar.
        /// </summary>
        /// <returns>Felet i klartext, eller null när det gick.</returns>
        public static string? SetCompetitionCounts(
            List<MembershipFeeChargeLine> lines, int year, RegionFeeRate rate, int startCount, int competitionCount)
        {
            if (startCount < 0 || competitionCount < 0) return "Antalet kan inte vara negativt.";
            if (lines.Any(l => l.Kind == MembershipFeeLineKind.Manual))
                return "Beloppet för klubben är skrivet för hand och räknas inte på starter. "
                     + "Ta bort det egna beloppet först.";

            var oldComp = lines.FirstOrDefault(l => l.Kind == MembershipFeeLineKind.PerCompetition);
            var oldStart = lines.FirstOrDefault(l => l.Kind == MembershipFeeLineKind.PerStart);
            var compPrice = oldComp?.UnitPrice ?? rate.PerCompetition;
            var startPrice = oldStart?.UnitPrice ?? rate.PerStart;
            var compLabel = oldComp is null ? rate.CompetitionLabel : LabelOf(oldComp.Description, rate.CompetitionLabel);
            var startLabel = oldStart is null ? rate.StartLabel : LabelOf(oldStart.Description, rate.StartLabel);

            // Raderna står efter grund/per medlem och före tilläggen.
            lines.RemoveAll(l => l.Kind is MembershipFeeLineKind.PerCompetition or MembershipFeeLineKind.PerStart);
            var at = lines.FindIndex(l => l.Kind == MembershipFeeLineKind.Extra);
            if (at < 0) at = lines.Count;

            var add = new List<MembershipFeeChargeLine>();
            if (compPrice > 0 && competitionCount > 0)
                add.Add(CountLine(MembershipFeeLineKind.PerCompetition, year, compLabel, competitionCount, CompetitionUnit(competitionCount), compPrice));
            if (startPrice > 0 && startCount > 0)
                add.Add(CountLine(MembershipFeeLineKind.PerStart, year, startLabel, startCount, StartUnit(startCount), startPrice));
            lines.InsertRange(at, add);

            Number(lines);
            return null;
        }

        private static MembershipFeeChargeLine CountLine(string kind, int year, string label, int count, string unit, decimal price)
            => new()
            {
                Kind = kind,
                Description = CountText(year, label, count, unit, price),
                Quantity = count,
                UnitPrice = price,
                Amount = Round(price * count)
            };

        /// <summary>"Startavgifter 2024, 140 starter à 20 kr" — samma form som per-medlem-raden.</summary>
        public static string CountText(int year, string label, int count, string unit, decimal price)
            => $"{(string.IsNullOrWhiteSpace(label) ? "Avgift" : label.Trim())} {year}, {count} {unit} à "
             + price.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("sv-SE")) + " kr";

        public static string StartUnit(int n) => n == 1 ? "start" : "starter";
        public static string CompetitionUnit(int n) => n == 1 ? "tävling" : "tävlingar";

        // Etiketten ur en befintlig rad ("Startavgifter 2024, 140 …" → "Startavgifter"), så att en
        // skickad räkning behåller sitt namn när bara antalet rättas.
        private static string LabelOf(string description, string fallback)
        {
            var i = description.LastIndexOf(' ', Math.Max(0, description.IndexOf(',') - 1));
            return i > 0 ? description[..i] : fallback;
        }

        /// <summary>
        /// Kravets belopp. <b>Alltid summan av raderna</b> — ett krav vars rader och belopp säger
        /// olika saker går inte att förklara, och beloppet är det klubben swishar.
        /// </summary>
        public static decimal Total(IEnumerable<MembershipFeeChargeLine> lines)
            => lines.Sum(l => l.Amount);

        /// <summary>
        /// Räknar om per-medlem-raden när kretsen rättar antalet på ett befintligt krav. Grundavgiften
        /// och tilläggen står kvar; ett handskrivet belopp rörs inte (det räknades aldrig per medlem).
        /// </summary>
        /// <returns>Felet i klartext, eller null när det gick.</returns>
        public static string? SetMemberCount(
            List<MembershipFeeChargeLine> lines, int year, decimal perMember, int memberCount)
        {
            if (memberCount < 0) return "Antalet medlemmar kan inte vara negativt.";

            if (lines.Any(l => l.Kind == MembershipFeeLineKind.Manual))
                return "Beloppet för klubben är skrivet för hand och räknas inte per medlem. "
                     + "Ta bort raden och skapa kravet igen om det ska räknas per medlem.";

            var existing = lines.FirstOrDefault(l => l.Kind == MembershipFeeLineKind.PerMember);
            var price = existing?.UnitPrice ?? perMember;

            lines.RemoveAll(l => l.Kind == MembershipFeeLineKind.PerMember);

            if (price > 0 && memberCount > 0)
            {
                var insertAt = lines.FindIndex(l => l.Kind != MembershipFeeLineKind.Base);
                var line = new MembershipFeeChargeLine
                {
                    Kind = MembershipFeeLineKind.PerMember,
                    Description = PerMemberText(year, memberCount, price),
                    Quantity = memberCount,
                    UnitPrice = price,
                    Amount = Round(price * memberCount)
                };
                if (insertAt < 0) lines.Add(line); else lines.Insert(insertAt, line);
            }

            Number(lines);
            return null;
        }

        /// <summary>Valideringen av ett tillägg. Null = det duger.</summary>
        public static string? ValidateExtra(string? description, decimal amount)
        {
            if (string.IsNullOrWhiteSpace(description)) return "Skriv vad tillägget avser, t.ex. \"Lagavgift\".";
            if (description.Trim().Length > 200) return "Texten får vara högst 200 tecken.";
            if (amount <= 0) return "Ett tillägg måste vara större än noll. Ta bort raden i stället.";
            return null;
        }

        /// <summary>
        /// Texten på per-medlem-raden. Svensk tusen- och decimalform oavsett serverns kultur — raden
        /// står på en räkning, och "12.5 kr" är inte svenska.
        /// </summary>
        public static string PerMemberText(int year, int memberCount, decimal price)
            => $"Avgift per medlem {year}, {memberCount} medlemmar à "
             + price.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("sv-SE")) + " kr";

        private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

        private static List<MembershipFeeChargeLine> Number(List<MembershipFeeChargeLine> lines)
        {
            for (var i = 0; i < lines.Count; i++) lines[i].SortOrder = i;
            return lines;
        }
    }
}
