namespace HpskSite.Models.Staffing
{
    /// <summary>One suggested materiel line for the order/estimate list. Quantity is null for
    /// "efter behov"-items with no derivable number.</summary>
    public class MaterielEstimateRow
    {
        public string Category { get; set; } = "";
        public string Item { get; set; } = "";
        public int? Quantity { get; set; }
        public string Unit { get; set; } = "st";
        public string Basis { get; set; } = "";
    }

    public class MaterielEstimateResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public string Discipline { get; set; } = "";
        public int ParticipantCount { get; set; }   // people registered
        public int StartCount { get; set; }          // class-starts (a shooter can enter several classes)
        public int ClassCount { get; set; }          // distinct classes
        public int Series { get; set; }              // numberOfSeriesOrStations
        public List<MaterielEstimateRow> Rows { get; set; } = new();
    }
}
