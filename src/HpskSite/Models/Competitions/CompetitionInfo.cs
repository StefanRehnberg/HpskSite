using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;

namespace HpskSite.Models.Competitions
{
    /// <summary>
    /// <b>Tävlingen som DATA</b> — det som i dag ligger på <c>competition</c>-noden (C3-planen S1,
    /// <c>notes/c3-plan-2026-10-13.md</c>). Läs och skriv aldrig noden direkt; gå genom
    /// <c>ICompetitionStore</c>.
    ///
    /// <para><b>Posten bär det LAGRADE värdet, normaliserat men inte omtolkat.</b> Tävlingstypen står som
    /// den står ("Magnum Precision" och "MagnumPrecision" förekommer båda), avgifterna som text ("120:-"
    /// finns), klasserna och tävlingsansvariga som sin JSON. Tolkningarna — typens id, beloppet, klasslistan
    /// — är egenskaper här och finns på ETT ställe, så att en läsare aldrig skriver en egen. Att normalisera
    /// det lagrade görs en gång, i flytten till tabellen, med en lista över varje värde som ändras.</para>
    ///
    /// <para>Normaliseringen som ÄR gjord, för alla fält: ett tomt värde blir <c>null</c>, ett datum som
    /// Umbraco lagrar som <c>DateTime.MinValue</c> blir <c>null</c>, ett saknat boolskt värde blir
    /// <c>false</c> och ett saknat heltal <c>0</c> — det läsarna redan får av <c>Value&lt;T&gt;</c>. Enda
    /// undantaget: <see cref="IsVisible"/> (nodens <c>isActive</c>) är <c>true</c> när värdet saknas
    /// (Stefans beslut 2026-10-13, §9a).</para>
    ///
    /// <para>Varje fält är märkt i <see cref="CompetitionFieldTable"/>: <b>Definition</b> (vad tävlingen
    /// ÄR — följer med en kopia), <b>Tillstånd</b> (vad som hänt med den — följer aldrig med) eller nodens
    /// egna uppgifter.</para>
    /// </summary>
    public sealed class CompetitionInfo
    {
        // ── Nodens egna uppgifter ────────────────────────────────────────────────────────────

        /// <summary>Tävlingens id. I nodlagringen nodens id, och id:t bevaras vid flytten (LAGEN).</summary>
        public int Id { get; set; }

        public Guid Key { get; set; }

        /// <summary>Nodens namn. Visningsnamnet är <see cref="DisplayName"/>.</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Versionen som <c>Update</c> jämför mot (nodens <c>VersionId</c>, tabellens <c>ROWVERSION</c>).
        /// Ändras vid varje sparning.
        /// </summary>
        public long Version { get; set; }

        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }
        public int SortOrder { get; set; }

        /// <summary>
        /// Om tävlingen är publicerad i Umbraco. Bara nodlagringen har ett publiceringsläge — tabellen bär
        /// <see cref="IsVisible"/> och inget annat (§9a). De opublicerade i prod 2026-10-14 är demorester.
        /// </summary>
        public bool IsPublished { get; set; }

        /// <summary>Förälderns id (serien, säsongen, hubben eller en vanlig sida).</summary>
        public int ParentId { get; set; }

        /// <summary>Förälderns dokumenttyp — <c>competitionSeries</c>, <c>competitionSeason</c>, …</summary>
        public string ParentKind { get; set; } = "";

        /// <summary>Serien tävlingen ingår i — föräldern när den är en serie, annars null.</summary>
        public int? SeriesId { get; set; }

        /// <summary>Nodens adressegment. Adressen själv byggs av adressreglerna, aldrig av posten.</summary>
        public string UrlSegment { get; set; } = "";

        // ── Grunduppgifter (Definition) ──────────────────────────────────────────────────────

        public string? CompetitionName { get; set; }

        /// <summary>Som det står lagrat — id ("Faltskytte") eller visningsnamn ("Magnum Fält"). Läs <see cref="TypeId"/>.</summary>
        public string? CompetitionType { get; set; }

        /// <summary>Lokal väggtid, som allt annat på sajten.</summary>
        public DateTime? CompetitionDate { get; set; }
        public DateTime? CompetitionEndDate { get; set; }

        public string? Venue { get; set; }
        public int RangeId { get; set; }

        /// <summary>Värden: klubben (0 = ingen) och kretsens kod. Ingen ärvning från serien (F5).</summary>
        public int ClubId { get; set; }
        public string? RegionalFederation { get; set; }

        /// <summary>Mästerskapet, normaliserat (<c>["Kretsmästerskap"]</c> skalas av). Null = inget satt.</summary>
        public string? CompetitionScope { get; set; }
        public string? CompetitionLevel { get; set; }

        public bool IsClubOnly { get; set; }
        public bool IsExternal { get; set; }
        public string? ExternalUrl { get; set; }
        public string? ExternalRegistrationEmail { get; set; }

        /// <summary>Nodens <c>isActive</c> — tävlingen syns. Saknas = synlig (§9a).</summary>
        public bool IsVisible { get; set; } = true;

        public bool AddToMenu { get; set; }
        public string? Description { get; set; }
        public string? CompetitionDirector { get; set; }
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }

        /// <summary>Tävlingsansvariga, som JSON-arrayen av medlems-id ("[123,456]"). Läs <see cref="ManagerIds"/>.</summary>
        public string? CompetitionManagers { get; set; }

        // ── Anmälan och avgifter (Definition) ────────────────────────────────────────────────

        public DateTime? RegistrationOpenDate { get; set; }
        public DateTime? RegistrationCloseDate { get; set; }
        public int MaxParticipants { get; set; }

        /// <summary>Avgifterna som text, som de skrivits. Beloppen: <see cref="Fee"/>.</summary>
        public string? RegistrationFee { get; set; }
        public string? JuniorRegistrationFee { get; set; }
        public string? TeamRegistrationFee { get; set; }
        public string? StafettRegistrationFee { get; set; }
        public string? SubCompetitionFee { get; set; }
        public string? SubCompetitionFeeMode { get; set; }
        public string? SubCompetitionName { get; set; }
        public string? SwishNumber { get; set; }

        /// <summary>Klasserna som de står lagrade (JSON-array, äldre CSV). Läs <see cref="ClassIds"/>.</summary>
        public string? ShootingClassIds { get; set; }

        public bool AllowDualCClassRegistration { get; set; }
        public bool AllowSelfReporting { get; set; }
        public bool AllowStafett { get; set; }
        public bool AllowTeams { get; set; }
        public bool CloseRegistrationOnStartList { get; set; }

        // ── Formatet (Definition) ────────────────────────────────────────────────────────────

        public bool IsAwardingStandardMedals { get; set; }
        public bool IsAwardingHonoraryAward { get; set; }
        public int NumberOfSeriesOrStations { get; set; }
        public int NumberOfFinalSeries { get; set; }
        public int MaxReshoots { get; set; }
        public bool ShowLiveResults { get; set; }

        /// <summary>Spegeln av konfigurationens räknesätt — läs via <c>FaltskytteScoringMode.Resolve</c>.</summary>
        public string? ScoringMode { get; set; }

        /// <summary>Konfigurationerna som JSON.</summary>
        public string? StationConfig { get; set; }
        public string? DirektplaceringConfig { get; set; }
        public string? RollingStart { get; set; }
        public bool FaltskytteSelfServiceResults { get; set; }

        // ── Tillstånd (följer aldrig med en kopia) ───────────────────────────────────────────

        public bool FaltskyttePatrolsPublished { get; set; }
        public bool FaltskytteResultsOfficial { get; set; }
        public string? FaltskytteStationManagers { get; set; }

        /// <summary>Fält- och springskyttets klassammanslagning (precisionens ligger på resultatlistan).</summary>
        public string? MergeConfig { get; set; }

        public bool MedalsPerWeaponGroup { get; set; }

        /// <summary>Filerna som de lagrade mediareferenserna (MediaPicker3-JSON).</summary>
        public string? InvitationFile { get; set; }
        public string? ResultListFile { get; set; }

        // ── Tolkningarna — på ett ställe ─────────────────────────────────────────────────────

        /// <summary>Det namn som visas: <c>competitionName</c>, annars nodens namn.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(CompetitionName) ? Name : CompetitionName!;

        /// <summary>Typens id ur katalogen (samma regel som <c>CompetitionTypes.GetFuzzy</c>). Null = okänd.</summary>
        public string? TypeId => string.IsNullOrWhiteSpace(CompetitionType) ? null : HpskSite.Models.CompetitionTypes.GetFuzzy(CompetitionType)?.Id;

        /// <summary>Sista tävlingsdagen: slutdatumet när det är satt och inte före start, annars startdagen.</summary>
        public DateTime? LastDay =>
            CompetitionEndDate.HasValue && CompetitionDate.HasValue && CompetitionEndDate.Value.Date >= CompetitionDate.Value.Date
                ? CompetitionEndDate.Value.Date
                : CompetitionDate?.Date ?? CompetitionEndDate?.Date;

        public bool IsChampionship => CompetitionScopeHelper.IsChampionshipScope(CompetitionScope ?? "");

        /// <summary>Klasserna, samma läsning som <c>ShootingClassIdsValue.ReadIds</c>.</summary>
        public IReadOnlyList<string> ClassIds => ShootingClassIdsValue.ReadIds(ShootingClassIds);

        /// <summary>Tävlingsansvarigas medlems-id. Ett oläsbart värde ger en tom lista.</summary>
        public IReadOnlyList<int> ManagerIds => CompetitionFieldValues.ReadIntArray(CompetitionManagers);

        /// <summary>Ett belopp, samma tolkning som <c>RegistrationFeeCalculator</c>. Null = inget belopp skrivet.</summary>
        public decimal? Fee(CompetitionFeeKind kind) => CompetitionFieldValues.ReadAmount(kind switch
        {
            CompetitionFeeKind.Registration => RegistrationFee,
            CompetitionFeeKind.Junior => JuniorRegistrationFee,
            CompetitionFeeKind.Team => TeamRegistrationFee,
            CompetitionFeeKind.Stafett => StafettRegistrationFee,
            CompetitionFeeKind.SubCompetition => SubCompetitionFee,
            _ => null,
        });

        public CompetitionInfo Clone() => (CompetitionInfo)MemberwiseClone();
    }

    public enum CompetitionFeeKind { Registration, Junior, Team, Stafett, SubCompetition }

    /// <summary>
    /// <b>Serien som DATA</b> — det som i dag ligger på <c>competitionSeries</c>-noden. Samma regler som
    /// <see cref="CompetitionInfo"/>.
    /// </summary>
    public sealed class CompetitionSeriesInfo
    {
        public int Id { get; set; }
        public Guid Key { get; set; }
        public string Name { get; set; } = "";
        public long Version { get; set; }
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }
        public int SortOrder { get; set; }
        public bool IsPublished { get; set; }
        public int ParentId { get; set; }
        public string ParentKind { get; set; } = "";
        public string UrlSegment { get; set; } = "";

        public string? SeriesName { get; set; }
        public string? SeriesShortDescription { get; set; }
        public string? SeriesDescription { get; set; }
        public DateTime? SeriesStartDate { get; set; }
        public DateTime? SeriesEndDate { get; set; }

        /// <summary>Loggan som lagrad mediareferens (MediaPicker3-JSON).</summary>
        public string? SeriesLogo { get; set; }

        public int ClubId { get; set; }
        public string? RegionalFederation { get; set; }

        /// <summary>Nodens <c>isActive</c>. Saknas = synlig (§9a).</summary>
        public bool IsVisible { get; set; } = true;

        public bool ShowInMenu { get; set; }
        public string? SeriesCalculationStrategy { get; set; }
        public string? SeriesCalculationConfig { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(SeriesName) ? Name : SeriesName!;

        public CompetitionSeriesInfo Clone() => (CompetitionSeriesInfo)MemberwiseClone();
    }
}
