using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// En återkommande uppgift i årshjulet (2026-10-21, plan notes/arshjul-aterkommande-plan-2026-10-14.md).
    ///
    /// <para><b>⚠️ Regeln är vad som GÄLLER, tillfällena räknas fram.</b> Ett år har inga rader förrän någon
    /// bockar av, ändrar eller hoppar över ett tillfälle — då sparas det som en <see cref="BoardYearWheelItem"/>
    /// med <c>RuleId</c>. Läsning skriver alltså ingenting, och 2035 finns utan att någon öppnat det.
    /// Reglerna bor i <c>BoardWheelPlanner</c>.</para>
    ///
    /// <para><b>Intervallet är hela år</b> (Stefans beslut 2026-10-14). Ett tillfälle infaller de år där
    /// <c>(år − FirstYear) % IntervalYears == 0</c>, från <c>FirstYear</c> till <c>LastYear</c>.</para>
    /// </summary>
    [TableName("BoardWheelRule")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardWheelRule
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public string Title { get; set; } = string.Empty;

        /// <summary>Månad och dag för tillfället; null = utan datum.</summary>
        public int? Month { get; set; }
        public int? Day { get; set; }

        /// <summary>1 = varje år, 5 = vart femte år. Aldrig mindre än 1.</summary>
        public int IntervalYears { get; set; } = 1;
        public int FirstYear { get; set; }
        /// <summary>Sista året regeln gäller; null = tills vidare.</summary>
        public int? LastYear { get; set; }

        /// <summary>Standardpunkternas nyckel (<see cref="BoardYearWheelTemplate"/>), t.ex. <c>kallelse</c>.
        /// Kallelsen bockar av sin punkt på nyckeln, inte på titeln — en klubb som döper om punkten ska inte
        /// tappa avbockningen.</summary>
        public string? TemplateKey { get; set; }

        public int SortOrder { get; set; }

        /// <summary>Mejla en påminnelse 90 dagar före (del C). Av för de årliga standardpunkterna, på för egna
        /// punkter och allt med intervall ≥ 2 år (Stefans beslut 2026-10-21).</summary>
        public bool Remind { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedDate { get; set; }
        public int? CreatedBy { get; set; }

        /// <summary>Förvalet för påminnelsen — se <see cref="Remind"/>.</summary>
        public static bool DefaultRemind(string? templateKey, int intervalYears) =>
            string.IsNullOrEmpty(templateKey) || intervalYears >= 2;
    }
}
