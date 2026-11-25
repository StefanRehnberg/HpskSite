namespace HpskSite.Models
{
    /// <summary>En klubbs åldersgräns för en medlemstyp: typen gäller från och med <see cref="FromAge"/> år (undre gräns).</summary>
    public sealed record AgeStep(string Key, int FromAge);

    /// <summary>
    /// Rena regler för klubbens åldersgränser per medlemstyp (steg 3b). Alltid uttryckta som UNDRE gräns ("Senior från 21");
    /// Junior är "från 0". Typen med högsta <see cref="AgeStep.FromAge"/> ≤ ålder gäller, och en typs åldersgrupp är
    /// [FromAge, nästa högre FromAge). Inga steg = regeln är AV för klubben.
    ///
    /// <para>⚠️ Det här är KLUBBENS val för medlemstypen. SHB:s åldersgränser (junior till och med det år man fyller 20,
    /// Vet Y/Ä 60/70) gäller tävlingsklass och är bara ett förslag till förval. Visa aldrig "junior/vet" som tävlingsklass här.</para>
    /// </summary>
    public static class AgeTypeRules
    {
        /// <summary>Ålder på KALENDERÅR: avgiftsåret minus födelseåret (den som fyller 60 i år är 60 från 1 januari).</summary>
        public static int Age(int feeYear, int birthYear) => feeYear - birthYear;

        /// <summary>Typen med högsta gräns som medlemmen uppnått, eller null när åldern ligger under lägsta gräns.</summary>
        public static string? TypeFor(int age, IReadOnlyList<AgeStep> steps)
        {
            AgeStep? best = null;
            foreach (var s in steps)
                if (s.FromAge <= age && (best is null || s.FromAge > best.FromAge)) best = s;
            return best?.Key;
        }

        /// <summary>Typens åldersgrupp [från, till) — till är null för den översta. Null när typen saknar gräns.</summary>
        public static (int From, int? To)? GroupOf(string? key, IReadOnlyList<AgeStep> steps)
        {
            var mine = steps.FirstOrDefault(s => MembershipTypeKeys.Same(s.Key, key));
            if (mine is null) return null;
            int? next = null;
            foreach (var s in steps)
                if (s.FromAge > mine.FromAge && (next is null || s.FromAge < next)) next = s.FromAge;
            return (mine.FromAge, next);
        }

        /// <summary>
        /// Får en medlem med den här åldern välja typen? Sant när klubben inte har någon stege, åldern inte är känd, eller typen
        /// inte har någon gräns (t.ex. Stödjande). En typ MED gräns är tillåten bara inom sin åldersgrupp. Servern är grinden —
        /// vyn döljer bara de otillåtna alternativen.
        /// </summary>
        public static bool IsAllowedForAge(string? typeKey, int? age, IReadOnlyList<AgeStep> steps)
        {
            if (steps.Count == 0 || age is null) return true;
            if (!steps.Any(s => MembershipTypeKeys.Same(s.Key, typeKey))) return true;
            return MembershipTypeKeys.Same(TypeFor(age.Value, steps), typeKey);
        }

        /// <summary>
        /// Prövar en hel stege innan den sparas (hela stegen sparas på en gång). Null = godkänt.
        /// Typer som aldrig åldersstyrs (Familj, Hedersmedlem, Ständig, Stödjande) får ingen gräns; gränserna är 0–120 och unika;
        /// varje typ högst en gång; och när det finns steg ska lägsta gräns vara 0 (annars faller en ung medlem mellan stolarna).
        /// </summary>
        public static string? ValidateSteps(IReadOnlyList<AgeStep> steps, IReadOnlyCollection<string> validTypeKeys)
        {
            if (steps.Count == 0) return null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in steps)
            {
                var key = s.Key?.Trim() ?? "";
                if (!validTypeKeys.Any(v => MembershipTypeKeys.Same(v, key))) return "Okänd medlemstyp bland åldersgränserna.";
                if (MembershipTypeKeys.IsNeverAgeBased(key)) return "Familj, Hedersmedlem, Ständig och Stödjande kan inte få en åldersgräns.";
                if (!seen.Add(key)) return "En medlemstyp kan bara ha en åldersgräns.";
                if (s.FromAge < 0 || s.FromAge > MemberBirthYear.MaxAge) return $"Åldersgränsen ska vara mellan 0 och {MemberBirthYear.MaxAge} år.";
            }
            if (steps.Select(s => s.FromAge).Distinct().Count() != steps.Count)
                return "Två medlemstyper kan inte börja vid samma ålder.";
            if (steps.Min(s => s.FromAge) != 0)
                return "Någon typ måste gälla från 0 år, annars får de yngsta medlemmarna ingen typ.";
            return null;
        }
    }

    public enum AgeProposalKind
    {
        /// <summary>Har en typ i stegen och bör byta till en annan.</summary>
        Change,
        /// <summary>Saknar typ; ålder ger ett förslag (en gissning — förbockas inte).</summary>
        Untyped,
        /// <summary>Går inte att placera (se <see cref="AgeProposalRow.Reason"/>).</summary>
        CannotPlace,
        /// <summary>Ingår i ett hushåll (Familj): rörs aldrig, listas bara som information.</summary>
        Household,
        /// <summary>Kassören har valt att behålla typen: ingen ändring föreslås.</summary>
        Kept
    }

    /// <summary>En klubbmedlem som indata till förslaget. <see cref="BirthYear"/> kommer ur <see cref="MemberBirthYear"/>.</summary>
    public sealed record AgeMemberInput(int MemberId, string? CurrentType, bool IsActive, bool InHousehold, bool KeptByTreasurer, BirthYearResult BirthYear);

    public sealed record AgeProposalRow(int MemberId, AgeProposalKind Kind, string? FromKey, string? ToKey, int? Age, BirthYearStatus BirthStatus, string? Reason);

    /// <summary>Förslagslistan: en ren funktion av (avgiftsår, medlemmar, stege). Sparas aldrig; räknas om vid varje öppning och vid godkännandet.</summary>
    public static class AgeTypeProposal
    {
        /// <param name="activeTypeKeys">Klubbens SYNLIGA typer — en dold typ är aldrig mål.</param>
        public static List<AgeProposalRow> Build(int feeYear, IEnumerable<AgeMemberInput> members,
            IReadOnlyList<AgeStep> steps, IReadOnlyCollection<string> activeTypeKeys)
        {
            var rows = new List<AgeProposalRow>();
            if (steps.Count == 0) return rows;   // regeln är av

            foreach (var m in members)
            {
                if (!m.IsActive) continue;
                var cur = string.IsNullOrWhiteSpace(m.CurrentType) ? null : m.CurrentType.Trim();
                int? age = m.BirthYear.IsUsable ? AgeTypeRules.Age(feeYear, m.BirthYear.Year!.Value) : null;

                if (m.InHousehold || MembershipTypeKeys.IsFamilj(cur))
                {
                    rows.Add(new AgeProposalRow(m.MemberId, AgeProposalKind.Household, cur, null, age, m.BirthYear.Status, "Ingår i ett hushåll."));
                    continue;
                }
                // En typ utanför stegen (Hedersmedlem, Stödjande, egen typ utan gräns …) rörs aldrig.
                if (cur is not null && !steps.Any(s => MembershipTypeKeys.Same(s.Key, cur))) continue;
                if (m.KeptByTreasurer)
                {
                    rows.Add(new AgeProposalRow(m.MemberId, AgeProposalKind.Kept, cur, null, age, m.BirthYear.Status, "Typen behålls."));
                    continue;
                }
                if (age is null)
                {
                    rows.Add(new AgeProposalRow(m.MemberId, AgeProposalKind.CannotPlace, cur, null, null, m.BirthYear.Status, ReasonFor(m.BirthYear.Status)));
                    continue;
                }
                if (age < 0)
                {
                    rows.Add(new AgeProposalRow(m.MemberId, AgeProposalKind.CannotPlace, cur, null, age, BirthYearStatus.Future, ReasonFor(BirthYearStatus.Future)));
                    continue;
                }
                var target = AgeTypeRules.TypeFor(age.Value, steps);
                if (target is null)
                {
                    rows.Add(new AgeProposalRow(m.MemberId, AgeProposalKind.CannotPlace, cur, null, age, m.BirthYear.Status, "Åldern ligger under den lägsta åldersgränsen."));
                    continue;
                }
                if (!activeTypeKeys.Any(k => MembershipTypeKeys.Same(k, target)))
                {
                    rows.Add(new AgeProposalRow(m.MemberId, AgeProposalKind.CannotPlace, cur, null, age, m.BirthYear.Status, "Typen som åldern ger är dold. Visa den först."));
                    continue;
                }
                if (cur is null)
                    rows.Add(new AgeProposalRow(m.MemberId, AgeProposalKind.Untyped, null, target, age, m.BirthYear.Status, null));
                else if (!MembershipTypeKeys.Same(cur, target))
                    rows.Add(new AgeProposalRow(m.MemberId, AgeProposalKind.Change, cur, target, age, m.BirthYear.Status, null));
            }
            return rows;
        }

        public static string ReasonFor(BirthYearStatus s) => s switch
        {
            BirthYearStatus.Missing => "Saknar personnummer och födelsedatum.",
            BirthYearStatus.Invalid => "Personnummer eller födelsedatum går inte att tolka.",
            BirthYearStatus.Future => "Födelseåret ligger i framtiden.",
            BirthYearStatus.Implausible => "Orimlig ålder.",
            BirthYearStatus.Conflict => "Personnummer och födelsedatum säger emot varandra.",
            _ => "Går inte att placera."
        };
    }
}
