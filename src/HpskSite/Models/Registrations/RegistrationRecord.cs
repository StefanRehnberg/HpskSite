namespace HpskSite.Models.Registrations
{
    /// <summary>
    /// En tävlingsanmälan, oberoende av hur den lagras (sajtstrukturen fas 1 —
    /// <c>notes/sajtstruktur-plan-2026-10-09.md</c>). I dag en <c>competitionRegistration</c>-nod,
    /// efter bytet en rad i <c>CompetitionRegistration</c>. Kod utanför
    /// <see cref="HpskSite.Services.Registrations.ICompetitionRegistrationStore"/> ska bara se den här
    /// typen, aldrig noden.
    ///
    /// <para><b>⚠️ En anmälan identifieras av (tävling, id)</b>, aldrig av id:t ensamt — se
    /// <see cref="HpskSite.Services.Registrations.ICompetitionRegistrationStore.Get"/>.</para>
    /// </summary>
    public sealed class RegistrationRecord
    {
        /// <summary>0 för en anmälan som ännu inte sparats.</summary>
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";

        /// <summary>Klubben anmälan gäller ("Tävlar för"). 0 = inte satt, läsare faller tillbaka
        /// på <see cref="LegacyClubText"/> och sedan medlemmens huvudklubb.</summary>
        public int ClubId { get; set; }

        /// <summary>Äldre anmälningar bar klubben som text eller id i <c>memberClub</c>. Bara läsning.</summary>
        public string LegacyClubText { get; set; } = "";

        /// <summary>Klasserna med startönskemål och skjutlag. Formatregeln för den lagrade listan bor i
        /// <see cref="CompetitionRegistrationDocument"/>; den här listan är redan tolkad.</summary>
        public List<ShootingClassEntry> Classes { get; set; } = new();

        /// <summary>
        /// Sann när klasserna lästes ur de äldre skalära egenskaperna (<c>shootingClass</c> +
        /// <c>startPreference</c>) i stället för klasslistan. Nodlagringen skriver då tillbaka i
        /// samma form, så att en anmälan inte tyst byter lagringsform när någon rättar ett
        /// startönskemål.
        /// </summary>
        public bool ClassesFromLegacyScalar { get; set; }

        /// <summary>Senast anmälan registrerades eller uppdaterades av skytten (skrivs om vid varje ny anmälan).</summary>
        public DateTime RegistrationDate { get; set; }

        /// <summary>När anmälan skapades första gången. Sätts av lagringen, ändras aldrig.</summary>
        public DateTime CreatedDate { get; set; }
        public string RegisteredBy { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public bool IsSubCompetition { get; set; }
        public bool IsCheckedIn { get; set; }

        /// <summary>Den gamla fakturamodellens länk till fakturanoden. Nya tävlingar fakturerar i liggaren.</summary>
        public int? InvoiceId { get; set; }

        // Egenskaper som bara läses (ingen kod skriver dem i dag).
        public string MemberEmail { get; set; } = "";
        public string Notes { get; set; } = "";
        public string ShooterNotes { get; set; } = "";

        /// <summary>Klass-id:na, utan startönskemål och skjutlag.</summary>
        public List<string> ClassIds => Classes.Select(c => c.Class).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        /// <summary>En kopia som kan ändras utan att originalet påverkas.</summary>
        public RegistrationRecord Clone() => new()
        {
            Id = Id, CompetitionId = CompetitionId, MemberId = MemberId, MemberName = MemberName,
            ClubId = ClubId, LegacyClubText = LegacyClubText,
            Classes = Classes.Select(c => new ShootingClassEntry
            {
                Class = c.Class, StartPreference = c.StartPreference, TeamNumber = c.TeamNumber
            }).ToList(),
            ClassesFromLegacyScalar = ClassesFromLegacyScalar,
            RegistrationDate = RegistrationDate, CreatedDate = CreatedDate, RegisteredBy = RegisteredBy, IsActive = IsActive,
            IsSubCompetition = IsSubCompetition, IsCheckedIn = IsCheckedIn, InvoiceId = InvoiceId,
            MemberEmail = MemberEmail, Notes = Notes, ShooterNotes = ShooterNotes
        };
    }
}
