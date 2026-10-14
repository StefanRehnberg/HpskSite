using System.Globalization;

namespace HpskSite.Models.Competitions
{
    /// <summary>Vad ett fält är, för kopieringen och flytten.</summary>
    public enum CompetitionFieldKind
    {
        /// <summary>Vad tävlingen ÄR. Följer med en kopia.</summary>
        Definition,

        /// <summary>Vad som hänt med den (publicerade patruller, officiella resultat, filer). Följer aldrig med.</summary>
        State,
    }

    /// <summary>Hur ett lagrat värde läses och skrivs.</summary>
    public enum CompetitionFieldShape
    {
        /// <summary>TrueFalse. Saknas = false.</summary>
        Bool,

        /// <summary>TrueFalse där ett saknat värde betyder sant (bara <c>isActive</c>, §9a).</summary>
        BoolDefaultTrue,

        /// <summary>Integer. Saknas = 0.</summary>
        Int,

        /// <summary>DateTime. Saknas eller <c>MinValue</c> = null.</summary>
        Date,

        /// <summary>Text eller JSON som den står. Tom = null.</summary>
        Text,

        /// <summary>FlexibleDropdown. Lagras som text eller som en JSON-array med ett värde; läses som värdet.</summary>
        Choice,
    }

    /// <summary>Ett fält: nodens alias, postens egenskap och regeln för hur värdet läses.</summary>
    public sealed class CompetitionFieldDef<T>
    {
        public required string Alias { get; init; }
        public required string Property { get; init; }
        public required CompetitionFieldKind Kind { get; init; }
        public required CompetitionFieldShape Shape { get; init; }
        public required Func<T, object?> Get { get; init; }
        public required Action<T, object?> Set { get; init; }
    }

    /// <summary>
    /// <b>Tävlingens fält — EN tabell.</b> Nodlagringen läser och skriver genom den, tabellagringen
    /// mappar kolumnerna genom den, kopieringen tar bara <see cref="CompetitionFieldKind.Definition"/>
    /// och jämförelsen mot de gamla läsningarna går fält för fält genom den. Ett fält som saknas här
    /// finns inte för lagringen.
    ///
    /// <para><b>⚠️ Nya tävlingsfält är frysta</b> från S1 tills noderna är borta (C3-planen). Läggs ett
    /// fält ändå till: en rad här, en egenskap på <see cref="CompetitionInfo"/>, en kolumn i tabellen.</para>
    ///
    /// <para>Fält som koden läser men som inte finns på doktypen (<c>teamResultSeriesCount</c>,
    /// <c>contactPerson</c>, <c>specialInstructions</c>, <c>patrolSize</c>, <c>patrolIntervalMinutes</c>,
    /// <c>autoNotifyParticipants</c>, <c>registrationRequired</c>, <c>seriesSortOrder</c>,
    /// <c>pageTitle</c>, <c>metaDescription</c>, <c>hideFromNavigation</c>) har ALDRIG haft ett värde, i
    /// dev eller i prod (mätt 2026-10-14). Läsarna får standardvärdet och ska ersättas med det i S3.</para>
    /// </summary>
    public static class CompetitionFieldTable
    {
        public const string NodeAlias = "competition";

        private static CompetitionFieldDef<CompetitionInfo> F(string alias, CompetitionFieldKind kind, CompetitionFieldShape shape,
            Func<CompetitionInfo, object?> get, Action<CompetitionInfo, object?> set, string? property = null) => new()
            {
                Alias = alias,
                Property = property ?? char.ToUpperInvariant(alias[0]) + alias[1..],
                Kind = kind,
                Shape = shape,
                Get = get,
                Set = set,
            };

        private const CompetitionFieldKind D = CompetitionFieldKind.Definition;
        private const CompetitionFieldKind S = CompetitionFieldKind.State;

        public static readonly IReadOnlyList<CompetitionFieldDef<CompetitionInfo>> Fields = new[]
        {
            F("competitionName", D, CompetitionFieldShape.Text, c => c.CompetitionName, (c, v) => c.CompetitionName = (string?)v),
            F("competitionType", D, CompetitionFieldShape.Text, c => c.CompetitionType, (c, v) => c.CompetitionType = (string?)v),
            F("competitionDate", D, CompetitionFieldShape.Date, c => c.CompetitionDate, (c, v) => c.CompetitionDate = (DateTime?)v),
            F("competitionEndDate", D, CompetitionFieldShape.Date, c => c.CompetitionEndDate, (c, v) => c.CompetitionEndDate = (DateTime?)v),
            F("venue", D, CompetitionFieldShape.Text, c => c.Venue, (c, v) => c.Venue = (string?)v),
            F("rangeId", D, CompetitionFieldShape.Int, c => c.RangeId, (c, v) => c.RangeId = (int)v!),
            F("clubId", D, CompetitionFieldShape.Int, c => c.ClubId, (c, v) => c.ClubId = (int)v!),
            F("regionalFederation", D, CompetitionFieldShape.Text, c => c.RegionalFederation, (c, v) => c.RegionalFederation = (string?)v),
            F("competitionScope", D, CompetitionFieldShape.Choice, c => c.CompetitionScope, (c, v) => c.CompetitionScope = (string?)v),
            F("competitionLevel", D, CompetitionFieldShape.Text, c => c.CompetitionLevel, (c, v) => c.CompetitionLevel = (string?)v),
            F("isClubOnly", D, CompetitionFieldShape.Bool, c => c.IsClubOnly, (c, v) => c.IsClubOnly = (bool)v!),
            F("isExternal", D, CompetitionFieldShape.Bool, c => c.IsExternal, (c, v) => c.IsExternal = (bool)v!),
            F("externalUrl", D, CompetitionFieldShape.Text, c => c.ExternalUrl, (c, v) => c.ExternalUrl = (string?)v),
            F("externalRegistrationEmail", D, CompetitionFieldShape.Text, c => c.ExternalRegistrationEmail, (c, v) => c.ExternalRegistrationEmail = (string?)v),
            F("isActive", D, CompetitionFieldShape.BoolDefaultTrue, c => c.IsVisible, (c, v) => c.IsVisible = (bool)v!, "IsVisible"),
            F("addToMenu", D, CompetitionFieldShape.Bool, c => c.AddToMenu, (c, v) => c.AddToMenu = (bool)v!),
            F("description", D, CompetitionFieldShape.Text, c => c.Description, (c, v) => c.Description = (string?)v),
            F("competitionDirector", D, CompetitionFieldShape.Text, c => c.CompetitionDirector, (c, v) => c.CompetitionDirector = (string?)v),
            F("contactEmail", D, CompetitionFieldShape.Text, c => c.ContactEmail, (c, v) => c.ContactEmail = (string?)v),
            F("contactPhone", D, CompetitionFieldShape.Text, c => c.ContactPhone, (c, v) => c.ContactPhone = (string?)v),
            F("competitionManagers", D, CompetitionFieldShape.Text, c => c.CompetitionManagers, (c, v) => c.CompetitionManagers = (string?)v),

            F("registrationOpenDate", D, CompetitionFieldShape.Date, c => c.RegistrationOpenDate, (c, v) => c.RegistrationOpenDate = (DateTime?)v),
            F("registrationCloseDate", D, CompetitionFieldShape.Date, c => c.RegistrationCloseDate, (c, v) => c.RegistrationCloseDate = (DateTime?)v),
            F("maxParticipants", D, CompetitionFieldShape.Int, c => c.MaxParticipants, (c, v) => c.MaxParticipants = (int)v!),
            F("registrationFee", D, CompetitionFieldShape.Text, c => c.RegistrationFee, (c, v) => c.RegistrationFee = (string?)v),
            F("juniorRegistrationFee", D, CompetitionFieldShape.Text, c => c.JuniorRegistrationFee, (c, v) => c.JuniorRegistrationFee = (string?)v),
            F("teamRegistrationFee", D, CompetitionFieldShape.Text, c => c.TeamRegistrationFee, (c, v) => c.TeamRegistrationFee = (string?)v),
            F("stafettRegistrationFee", D, CompetitionFieldShape.Text, c => c.StafettRegistrationFee, (c, v) => c.StafettRegistrationFee = (string?)v),
            F("subCompetitionFee", D, CompetitionFieldShape.Text, c => c.SubCompetitionFee, (c, v) => c.SubCompetitionFee = (string?)v),
            F("subCompetitionFeeMode", D, CompetitionFieldShape.Text, c => c.SubCompetitionFeeMode, (c, v) => c.SubCompetitionFeeMode = (string?)v),
            F("subCompetitionName", D, CompetitionFieldShape.Text, c => c.SubCompetitionName, (c, v) => c.SubCompetitionName = (string?)v),
            F("swishNumber", D, CompetitionFieldShape.Text, c => c.SwishNumber, (c, v) => c.SwishNumber = (string?)v),
            F("shootingClassIds", D, CompetitionFieldShape.Text, c => c.ShootingClassIds, (c, v) => c.ShootingClassIds = (string?)v),
            F("allowDualCClassRegistration", D, CompetitionFieldShape.Bool, c => c.AllowDualCClassRegistration, (c, v) => c.AllowDualCClassRegistration = (bool)v!),
            F("allowSelfReporting", D, CompetitionFieldShape.Bool, c => c.AllowSelfReporting, (c, v) => c.AllowSelfReporting = (bool)v!),
            F("allowStafett", D, CompetitionFieldShape.Bool, c => c.AllowStafett, (c, v) => c.AllowStafett = (bool)v!),
            F("allowTeams", D, CompetitionFieldShape.Bool, c => c.AllowTeams, (c, v) => c.AllowTeams = (bool)v!),
            F("closeRegistrationOnStartList", D, CompetitionFieldShape.Bool, c => c.CloseRegistrationOnStartList, (c, v) => c.CloseRegistrationOnStartList = (bool)v!),

            F("isAwardingStandardMedals", D, CompetitionFieldShape.Bool, c => c.IsAwardingStandardMedals, (c, v) => c.IsAwardingStandardMedals = (bool)v!),
            F("isAwardingHonoraryAward", D, CompetitionFieldShape.Bool, c => c.IsAwardingHonoraryAward, (c, v) => c.IsAwardingHonoraryAward = (bool)v!),
            F("numberOfSeriesOrStations", D, CompetitionFieldShape.Int, c => c.NumberOfSeriesOrStations, (c, v) => c.NumberOfSeriesOrStations = (int)v!),
            F("numberOfFinalSeries", D, CompetitionFieldShape.Int, c => c.NumberOfFinalSeries, (c, v) => c.NumberOfFinalSeries = (int)v!),
            F("maxReshoots", D, CompetitionFieldShape.Int, c => c.MaxReshoots, (c, v) => c.MaxReshoots = (int)v!),
            F("showLiveResults", D, CompetitionFieldShape.Bool, c => c.ShowLiveResults, (c, v) => c.ShowLiveResults = (bool)v!),
            F("scoringMode", D, CompetitionFieldShape.Choice, c => c.ScoringMode, (c, v) => c.ScoringMode = (string?)v),
            F("stationConfig", D, CompetitionFieldShape.Text, c => c.StationConfig, (c, v) => c.StationConfig = (string?)v),
            F("direktplaceringConfig", D, CompetitionFieldShape.Text, c => c.DirektplaceringConfig, (c, v) => c.DirektplaceringConfig = (string?)v),
            F("rollingStart", D, CompetitionFieldShape.Text, c => c.RollingStart, (c, v) => c.RollingStart = (string?)v),
            F("faltskytteSelfServiceResults", D, CompetitionFieldShape.Bool, c => c.FaltskytteSelfServiceResults, (c, v) => c.FaltskytteSelfServiceResults = (bool)v!),

            F("faltskyttePatrolsPublished", S, CompetitionFieldShape.Bool, c => c.FaltskyttePatrolsPublished, (c, v) => c.FaltskyttePatrolsPublished = (bool)v!),
            F("faltskytteResultsOfficial", S, CompetitionFieldShape.Bool, c => c.FaltskytteResultsOfficial, (c, v) => c.FaltskytteResultsOfficial = (bool)v!),
            F("faltskytteStationManagers", S, CompetitionFieldShape.Text, c => c.FaltskytteStationManagers, (c, v) => c.FaltskytteStationManagers = (string?)v),
            F("mergeConfig", S, CompetitionFieldShape.Text, c => c.MergeConfig, (c, v) => c.MergeConfig = (string?)v),
            F("medalsPerWeaponGroup", S, CompetitionFieldShape.Bool, c => c.MedalsPerWeaponGroup, (c, v) => c.MedalsPerWeaponGroup = (bool)v!),
            F("invitationFile", S, CompetitionFieldShape.Text, c => c.InvitationFile, (c, v) => c.InvitationFile = (string?)v),
            F("resultListFile", S, CompetitionFieldShape.Text, c => c.ResultListFile, (c, v) => c.ResultListFile = (string?)v),
        };

        private static readonly Dictionary<string, CompetitionFieldDef<CompetitionInfo>> ByAlias =
            Fields.ToDictionary(f => f.Alias, StringComparer.OrdinalIgnoreCase);

        public static CompetitionFieldDef<CompetitionInfo>? ForAlias(string alias) =>
            ByAlias.TryGetValue(alias, out var f) ? f : null;

        /// <summary>De fält som skiljer sig mellan två poster.</summary>
        public static IEnumerable<CompetitionFieldDef<CompetitionInfo>> Changed(CompetitionInfo before, CompetitionInfo after) =>
            Fields.Where(f => !Equals(f.Get(before), f.Get(after)));

        /// <summary>
        /// En ny post med bara definitionen kopierad — det en kopia av tävlingen ska få. Nodens egna
        /// uppgifter och tillståndet (patruller, officiella resultat, filer) följer aldrig med.
        /// </summary>
        public static CompetitionInfo CopyDefinition(CompetitionInfo source)
        {
            var copy = new CompetitionInfo();
            foreach (var f in Fields.Where(f => f.Kind == CompetitionFieldKind.Definition))
                f.Set(copy, f.Get(source));
            return copy;
        }
    }

    /// <summary>Seriens fält — samma form som <see cref="CompetitionFieldTable"/>.</summary>
    public static class CompetitionSeriesFieldTable
    {
        public const string NodeAlias = "competitionSeries";

        private static CompetitionFieldDef<CompetitionSeriesInfo> F(string alias, CompetitionFieldShape shape,
            Func<CompetitionSeriesInfo, object?> get, Action<CompetitionSeriesInfo, object?> set, string? property = null) => new()
            {
                Alias = alias,
                Property = property ?? char.ToUpperInvariant(alias[0]) + alias[1..],
                Kind = CompetitionFieldKind.Definition,
                Shape = shape,
                Get = get,
                Set = set,
            };

        public static readonly IReadOnlyList<CompetitionFieldDef<CompetitionSeriesInfo>> Fields = new[]
        {
            F("seriesName", CompetitionFieldShape.Text, s => s.SeriesName, (s, v) => s.SeriesName = (string?)v),
            F("seriesShortDescription", CompetitionFieldShape.Text, s => s.SeriesShortDescription, (s, v) => s.SeriesShortDescription = (string?)v),
            F("seriesDescription", CompetitionFieldShape.Text, s => s.SeriesDescription, (s, v) => s.SeriesDescription = (string?)v),
            F("seriesStartDate", CompetitionFieldShape.Date, s => s.SeriesStartDate, (s, v) => s.SeriesStartDate = (DateTime?)v),
            F("seriesEndDate", CompetitionFieldShape.Date, s => s.SeriesEndDate, (s, v) => s.SeriesEndDate = (DateTime?)v),
            F("seriesLogo", CompetitionFieldShape.Text, s => s.SeriesLogo, (s, v) => s.SeriesLogo = (string?)v),
            F("clubId", CompetitionFieldShape.Int, s => s.ClubId, (s, v) => s.ClubId = (int)v!),
            F("regionalFederation", CompetitionFieldShape.Text, s => s.RegionalFederation, (s, v) => s.RegionalFederation = (string?)v),
            F("isActive", CompetitionFieldShape.BoolDefaultTrue, s => s.IsVisible, (s, v) => s.IsVisible = (bool)v!, "IsVisible"),
            F("showInMenu", CompetitionFieldShape.Bool, s => s.ShowInMenu, (s, v) => s.ShowInMenu = (bool)v!),
            F("seriesCalculationStrategy", CompetitionFieldShape.Text, s => s.SeriesCalculationStrategy, (s, v) => s.SeriesCalculationStrategy = (string?)v),
            F("seriesCalculationConfig", CompetitionFieldShape.Text, s => s.SeriesCalculationConfig, (s, v) => s.SeriesCalculationConfig = (string?)v),
        };

        public static IEnumerable<CompetitionFieldDef<CompetitionSeriesInfo>> Changed(CompetitionSeriesInfo before, CompetitionSeriesInfo after) =>
            Fields.Where(f => !Equals(f.Get(before), f.Get(after)));
    }

    /// <summary>
    /// <b>Läsreglerna för ett lagrat värde</b> — rena funktioner, så att de går att pröva utan Umbraco.
    /// Nodlagringen ger dem RÅVÄRDET (<c>GetValue</c> på utkastet, <c>GetSourceValue</c> på det
    /// publicerade), aldrig Umbracos typade läsning: FlexibleDropdown kastar på ett rent strängvärde
    /// (mätt: 24 tävlingar i prod har <c>competitionScope</c> som kastar på <c>Value&lt;string&gt;</c>).
    /// </summary>
    public static class CompetitionFieldValues
    {
        public static object? Read(CompetitionFieldShape shape, object? raw) => shape switch
        {
            CompetitionFieldShape.Bool => ReadBool(raw) ?? false,
            CompetitionFieldShape.BoolDefaultTrue => ReadBool(raw) ?? true,
            CompetitionFieldShape.Int => ReadInt(raw) ?? 0,
            CompetitionFieldShape.Date => ReadDate(raw),
            CompetitionFieldShape.Text => ReadText(raw),
            CompetitionFieldShape.Choice => ReadChoice(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        /// <summary>Värdet som nodlagringen skriver (det de befintliga skrivarna skriver).</summary>
        public static object? ToNode(CompetitionFieldShape shape, object? value) => shape switch
        {
            CompetitionFieldShape.Bool or CompetitionFieldShape.BoolDefaultTrue => (bool)(value ?? false) ? 1 : 0,
            CompetitionFieldShape.Int => (int)(value ?? 0),
            CompetitionFieldShape.Date => value is DateTime d ? d : null,
            _ => value as string,
        };

        public static bool? ReadBool(object? raw) => raw switch
        {
            null => null,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            decimal m => m != 0,
            string s when string.IsNullOrWhiteSpace(s) => null,
            string s => s.Trim() == "1" || s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => null,
        };

        public static int? ReadInt(object? raw) => raw switch
        {
            null => null,
            int i => i,
            long l => (int)l,
            decimal m => (int)m,
            string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };

        /// <summary>Lokal väggtid. <c>MinValue</c> och år före 1900 = null (Umbracos "inget värde").</summary>
        public static DateTime? ReadDate(object? raw)
        {
            DateTime? d = raw switch
            {
                DateTime dt => dt,
                DateTimeOffset dto => dto.DateTime,
                string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
                _ => null,
            };
            return d is null || d.Value == DateTime.MinValue || d.Value.Year < 1900 ? null : d;
        }

        public static string? ReadText(object? raw)
        {
            var s = raw switch
            {
                null => null,
                string str => str,
                _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
            };
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        /// <summary>Text, eller en JSON-array med ett värde (<c>["Poang"]</c>) som skalas av. Tomt = null.</summary>
        public static string? ReadChoice(object? raw)
        {
            string? text = raw switch
            {
                null => null,
                string s => s,
                string[] arr => arr.FirstOrDefault(),
                IEnumerable<string> e => e.FirstOrDefault(),
                _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
            };
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            if (t.StartsWith("[") && t.EndsWith("]")) t = t.Trim('[', ']').Trim().Trim('"').Trim();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }

        /// <summary>En JSON-array av heltal ("[123,456]"). Oläsbart = tom lista.</summary>
        public static IReadOnlyList<int> ReadIntArray(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<int>();
            try { return System.Text.Json.JsonSerializer.Deserialize<int[]>(json) ?? Array.Empty<int>(); }
            catch (System.Text.Json.JsonException) { return Array.Empty<int>(); }
        }

        /// <summary>Ett belopp — samma tolkning som <c>RegistrationFeeCalculator</c>. Null = inget belopp.</summary>
        public static decimal? ReadAmount(string? text) => HpskSite.Services.RegistrationFeeCalculator.ParseFeeOrNull(text);
    }
}
