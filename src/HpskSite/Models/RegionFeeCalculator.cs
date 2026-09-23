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

        /// <summary>
        /// Raderna för en klubb. <paramref name="manualAmount"/> satt ersätter formeln helt — kretsen
        /// har bestämt beloppet för just den klubben, och att dessutom lägga på grundavgiften vore
        /// att ta betalt två gånger.
        /// </summary>
        public static List<MembershipFeeChargeLine> Lines(
            int year, decimal baseAmount, decimal perMember, int memberCount, decimal? manualAmount)
        {
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

            return Number(lines);
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
