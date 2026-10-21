namespace HpskSite.Models
{
    /// <summary>
    /// Ett tillfälle i årshjulet så som det visas: antingen ett sparat (en engångspunkt eller ett sparat år av
    /// en regel) eller ett framräknat ur en regel.
    /// </summary>
    public sealed class WheelOccurrence
    {
        /// <summary><c>item:{id}</c> för en sparad rad, <c>rule:{regel}:{år}</c> för ett framräknat tillfälle.
        /// Klienten skickar tillbaka nyckeln; servern sparar tillfället när någon rör det.</summary>
        public string Key { get; set; } = string.Empty;
        public int? ItemId { get; set; }
        public int? RuleId { get; set; }
        public int Year { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime? TargetDate { get; set; }
        public bool Done { get; set; }
        public DateTime? DoneDate { get; set; }
        public int SortOrder { get; set; }
        public string? TemplateKey { get; set; }
        /// <summary>Regelns intervall; null för en engångspunkt.</summary>
        public int? IntervalYears { get; set; }
        /// <summary>Nästa år regeln slår till efter det här året; null när regeln upphör.</summary>
        public int? NextYear { get; set; }

        /// <summary>Ett krav från en bana (B2): skrivskyddat i årshjulet, bockas av genom att banan uppdateras.
        /// Nyckeln är <c>range:{sort}:{id}</c> och tolkas aldrig av <see cref="WheelKey"/>.</summary>
        public bool FromRange { get; set; }

        /// <summary>Mejla en påminnelse 90 dagar före (del C) — regelns val; en engångspunkt alltid.</summary>
        public bool Remind { get; set; }
        public string? RangeName { get; set; }
        public string? Url { get; set; }

        public bool IsOverdueOn(DateTime today) => !Done && TargetDate.HasValue && TargetDate.Value.Date < today.Date;
        public bool IsOverdue => IsOverdueOn(DateTime.Today);
    }

    /// <summary>Nyckeln från klienten, tolkad. Exakt ett av <see cref="ItemId"/> och <see cref="RuleId"/> är satt.</summary>
    public sealed record WheelKey(int? ItemId, int? RuleId, int Year)
    {
        public static WheelKey? Parse(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var p = key.Trim().Split(':');
            if (p.Length == 2 && p[0] == "item" && int.TryParse(p[1], out var id) && id > 0)
                return new WheelKey(id, null, 0);
            if (p.Length == 3 && p[0] == "rule" && int.TryParse(p[1], out var rid) && rid > 0
                && int.TryParse(p[2], out var y) && y is >= 1900 and <= 2999)
                return new WheelKey(null, rid, y);
            return null;
        }

        public static string ForItem(int id) => "item:" + id;
        public static string ForRule(int ruleId, int year) => $"rule:{ruleId}:{year}";
    }

    /// <summary>
    /// Årshjulets regler som rena funktioner (2026-10-21) — inga databasberoenden, så varje regel går att
    /// pröva (<c>BoardWheelPlannerTests</c>). Tjänsten (<c>BoardGovernanceService</c>) hämtar raderna och
    /// lämnar över hit; alla ytor som läser årshjulet går genom <see cref="ForYear"/>.
    /// </summary>
    public static class BoardWheelPlanner
    {
        public const int MaxIntervalYears = 50;

        /// <summary>Egna punkter sparas med SortOrder 100; den gamla kopieringen av standardpunkterna gav 0–8.</summary>
        public const int LegacyTemplateSortLimit = 100;

        /// <summary>Slår regeln till det här året?</summary>
        public static bool Occurs(BoardWheelRule rule, int year)
        {
            var interval = Math.Max(1, rule.IntervalYears);
            if (year < rule.FirstYear) return false;
            if (rule.LastYear.HasValue && year > rule.LastYear.Value) return false;
            return (year - rule.FirstYear) % interval == 0;
        }

        /// <summary>Första året EFTER <paramref name="afterYear"/> som regeln slår till; null när den upphört.</summary>
        public static int? Next(BoardWheelRule rule, int afterYear)
        {
            var interval = Math.Max(1, rule.IntervalYears);
            int y;
            if (afterYear < rule.FirstYear) y = rule.FirstYear;
            else y = rule.FirstYear + ((afterYear - rule.FirstYear) / interval + 1) * interval;
            if (rule.LastYear.HasValue && y > rule.LastYear.Value) return null;
            return y;
        }

        /// <summary>Tillfällets datum ett visst år. 29 februari blir 28 februari ett år som inte är skottår —
        /// ett återkommande tillfälle ska inte tappa sitt datum vart fjärde år.</summary>
        public static DateTime? DateFor(int? month, int? day, int year)
        {
            if (month is not (>= 1 and <= 12) || day is not >= 1) return null;
            var d = Math.Min(day.Value, DateTime.DaysInMonth(year, month.Value));
            return new DateTime(year, month.Value, d);
        }

        /// <summary>
        /// Listan som visas för ett år: regelns tillfällen, där ett sparat tillfälle ERSÄTTER det framräknade och
        /// en gravsten (sparat, <c>IsActive = 0</c>) TAR BORT det, plus årets engångspunkter.
        ///
        /// <para>⚠️ Ett sparat aktivt tillfälle visas även om regeln inte längre slår till det året — det är en
        /// sparad uppgift (ofta avbockad), och att låta den försvinna för att regeln ändrats efteråt vore att
        /// tappa historik.</para>
        /// </summary>
        public static List<WheelOccurrence> ForYear(IEnumerable<BoardWheelRule> rules, IEnumerable<BoardYearWheelItem> saved, int year)
        {
            var ruleList = rules.ToList();
            var ruleById = ruleList.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
            var yearRows = saved.Where(s => s.Year == year).ToList();

            // Ett sparat tillfälle per regel och år — det unika indexet garanterar det; lägsta id om data ändå bär två.
            var savedByRule = yearRows.Where(s => s.RuleId.HasValue)
                .GroupBy(s => s.RuleId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).First());

            var result = new List<WheelOccurrence>();

            foreach (var row in yearRows.Where(s => !s.RuleId.HasValue && s.IsActive))
                result.Add(FromSaved(row, null));

            foreach (var rule in ruleList.Where(r => r.IsActive && Occurs(r, year)))
            {
                if (savedByRule.TryGetValue(rule.Id, out var row))
                {
                    if (row.IsActive) result.Add(FromSaved(row, rule));
                    continue; // gravsten: året är överhoppat
                }
                result.Add(new WheelOccurrence
                {
                    Key = WheelKey.ForRule(rule.Id, year),
                    RuleId = rule.Id,
                    Year = year,
                    Title = rule.Title,
                    TargetDate = DateFor(rule.Month, rule.Day, year),
                    SortOrder = rule.SortOrder,
                    TemplateKey = rule.TemplateKey,
                    Remind = rule.Remind,
                    IntervalYears = Math.Max(1, rule.IntervalYears),
                    NextYear = Next(rule, year)
                });
            }

            // Sparade tillfällen vars regel inte (längre) slår till det här året.
            foreach (var (ruleId, row) in savedByRule)
            {
                if (!row.IsActive) continue;
                ruleById.TryGetValue(ruleId, out var rule);
                if (rule != null && rule.IsActive && Occurs(rule, year)) continue; // redan med ovan
                result.Add(FromSaved(row, rule));
            }

            // Samma ordning som tabellen alltid haft: utan datum först, sedan datum, sortering, titel.
            return result
                .OrderBy(o => o.TargetDate.HasValue ? 1 : 0)
                .ThenBy(o => o.TargetDate)
                .ThenBy(o => o.SortOrder)
                .ThenBy(o => o.Title, StringComparer.CurrentCulture)
                .ToList();
        }

        /// <summary>
        /// Ej avklarade tillfällen med datum senast <paramref name="days"/> dagar fram (försenade inräknade).
        /// Anroparen lämnar tillfällen för BÅDA åren kring årsskiftet — annars ser startsidan inte januari i december.
        /// </summary>
        public static List<WheelOccurrence> DueSoon(IEnumerable<WheelOccurrence> occurrences, DateTime today, int days) =>
            occurrences
                .Where(o => !o.Done && o.TargetDate.HasValue && o.TargetDate.Value.Date <= today.Date.AddDays(days))
                .OrderBy(o => o.TargetDate)
                .ThenBy(o => o.SortOrder)
                .ToList();

        /// <summary>
        /// Banornas krav som tillfällen (B2). Skrivskyddade: inget id i <c>BoardYearWheelItems</c>, och en bock i
        /// årshjulet vore en andra sanning — kravet "bockas av" genom att banan uppdateras.
        /// </summary>
        public static List<WheelOccurrence> FromRangeRequirements(IEnumerable<RangeRequirement> requirements) =>
            requirements.Select(r => new WheelOccurrence
            {
                Key = $"range:{r.Kind}:{r.SourceId}",
                Year = r.Due.Year,
                Title = r.Title,
                TargetDate = r.Due.Date,
                SortOrder = 50,
                FromRange = true,
                RangeName = r.RangeName,
                Url = r.Url
            }).ToList();

        /// <summary>Lägg till tillfällen i en lista och behåll årshjulets ordning (se <see cref="ForYear"/>).</summary>
        public static List<WheelOccurrence> Merge(IEnumerable<WheelOccurrence> a, IEnumerable<WheelOccurrence> b) =>
            a.Concat(b)
                .OrderBy(o => o.TargetDate.HasValue ? 1 : 0)
                .ThenBy(o => o.TargetDate)
                .ThenBy(o => o.SortOrder)
                .ThenBy(o => o.Title, StringComparer.CurrentCulture)
                .ToList();

        /// <summary>
        /// "Kommande längre fram": nästa tillfälle EFTER <paramref name="afterYear"/> för varje regel med intervall
        /// ≥ 2 år, och varje ej avklarad engångspunkt ett senare år. Det är svaret på "glömmer vi besiktningen" —
        /// en uppgift fem år bort får inte bara finnas på sitt år. Ett överhoppat år (gravsten) hoppas över.
        /// </summary>
        public static List<WheelOccurrence> Upcoming(IEnumerable<BoardWheelRule> rules, IEnumerable<BoardYearWheelItem> saved,
            int afterYear, int max = 10)
        {
            var ruleList = rules.ToList();
            var savedList = saved.ToList();
            var result = new List<WheelOccurrence>();

            foreach (var rule in ruleList.Where(r => r.IsActive && Math.Max(1, r.IntervalYears) >= 2))
            {
                var y = Next(rule, afterYear);
                for (var guard = 0; y.HasValue && guard < 20; guard++)
                {
                    var occ = ForYear(new[] { rule }, savedList, y.Value).FirstOrDefault(o => o.RuleId == rule.Id);
                    if (occ != null) { if (!occ.Done) result.Add(occ); break; }
                    y = Next(rule, y.Value); // gravsten — nästa gång
                }
            }

            foreach (var row in savedList.Where(s => !s.RuleId.HasValue && s.IsActive && !s.Done && s.Year > afterYear))
                result.Add(FromSaved(row, null));

            return result
                .OrderBy(o => o.Year)
                .ThenBy(o => o.TargetDate ?? DateTime.MaxValue)
                .ThenBy(o => o.Title, StringComparer.CurrentCulture)
                .Take(max)
                .ToList();
        }

        /// <summary>
        /// Tillfället som kallelsen till årsmötet bockar av: standardpunkten med nyckeln <c>kallelse</c> — inte
        /// titeln, så en omdöpt punkt bockas ändå av. Äldre sparade år (rader utan regel) matchas på titeln som förut.
        /// </summary>
        public static WheelOccurrence? KallelseTarget(IEnumerable<WheelOccurrence> occurrences)
        {
            var list = occurrences.Where(o => !o.Done && !o.FromRange).ToList();
            return list.FirstOrDefault(o => o.TemplateKey == BoardYearWheelTemplate.KallelseKey)
                ?? list.FirstOrDefault(o => !o.RuleId.HasValue && o.Title.Contains("Kallelse", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Första året standardpunkterna gäller som regler för en ägare. Redan sparade år står kvar som historik
        /// (rader utan RuleId), så reglerna börjar året efter det senaste sparade — men aldrig före i år: ett
        /// passerat år ingen planerade ska inte fyllas med försenade standardpunkter.
        /// </summary>
        public static int SeedFirstYear(int? maxSavedYear, int currentYear) =>
            maxSavedYear.HasValue ? Math.Max(maxSavedYear.Value + 1, currentYear) : currentYear;

        /// <summary>Standardpunkterna som årliga regler för en ägare.</summary>
        public static List<BoardWheelRule> TemplateRules(int ownerType, int ownerId, int firstYear, DateTime now)
        {
            var list = new List<BoardWheelRule>();
            int sort = 0;
            foreach (var (key, month, day, title) in BoardYearWheelTemplate.Items)
            {
                list.Add(new BoardWheelRule
                {
                    OwnerType = ownerType, OwnerId = ownerId, Title = title, Month = month, Day = day,
                    IntervalYears = 1, FirstYear = firstYear, TemplateKey = key, SortOrder = sort++,
                    Remind = BoardWheelRule.DefaultRemind(key, 1), IsActive = true, CreatedDate = now
                });
            }
            return list;
        }

        private static WheelOccurrence FromSaved(BoardYearWheelItem row, BoardWheelRule? rule) => new()
        {
            Key = WheelKey.ForItem(row.Id),
            ItemId = row.Id,
            RuleId = row.RuleId,
            Year = row.Year,
            Title = row.Title,
            TargetDate = row.TargetDate,
            Done = row.Done,
            DoneDate = row.DoneDate,
            SortOrder = row.SortOrder,
            TemplateKey = rule?.TemplateKey,
            // En egen engångspunkt: påminnelse på. ⚠️ Men rader utan regel från FÖRE reglerna är ofta standardpunkter
            // som den gamla koden kopierade in per år (SortOrder 0–8; egna punkter har alltid fått 100) — de årliga
            // standardpunkterna mejlar inte (Stefans beslut 2026-10-21).
            Remind = rule?.Remind ?? row.SortOrder >= LegacyTemplateSortLimit,
            IntervalYears = rule == null ? null : Math.Max(1, rule.IntervalYears),
            NextYear = rule == null ? null : Next(rule, row.Year)
        };
    }
}
