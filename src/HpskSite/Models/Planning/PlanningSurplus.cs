using System;
using System.Collections.Generic;
using System.Linq;
using NPoco;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// P4 (Stefan 2026-10-24, fråga 15): hur ett överskott fördelas mellan de föreningar som hjälpt till. "Det finns ingen
    /// regel — det måste vara flexibelt." Därför ett UNDERLAG, ingen bokföring: beloppet, fasta poster först (förbundet,
    /// hyra …), och en nyckel för resten — personer, uppdrag, dagar, lika per förening, eller egna andelar. Talen kommer ur
    /// bemanningen ("Ställer upp för"). Utbetalningarna bokför var och en som vanligt.
    /// </summary>
    [TableName("PlanningSurplus")]
    [PrimaryKey("PlanningId", AutoIncrement = false)]
    public class PlanningSurplusRow
    {
        public int PlanningId { get; set; }
        public string DataJson { get; set; } = "{}";
        public DateTime UpdatedAt { get; set; }
        public int UpdatedBy { get; set; }
    }

    public static class SurplusKey
    {
        public const string People = "people";
        public const string Assignments = "assignments";
        public const string Days = "days";
        public const string Equal = "equal";
        public const string Manual = "manual";
        public static readonly string[] All = { People, Assignments, Days, Equal, Manual };
        public static string Normalize(string? k) => Array.Find(All, a => a == (k ?? "").Trim()) ?? People;
        public static string Label(string k) => k switch
        {
            Assignments => "Efter uppdrag",
            Days => "Efter dagar",
            Equal => "Lika per förening",
            Manual => "Egna andelar",
            _ => "Efter personer",
        };
    }

    /// <summary>Det som sparas: arrangörens val. Föreningarnas tal läses om ur bemanningen varje gång.</summary>
    public class SurplusData
    {
        public decimal Amount { get; set; }
        public List<SurplusFixed> Fixed { get; set; } = new();
        public string Key { get; set; } = SurplusKey.People;
        /// <summary>Val per förening: med eller inte, och egen andel. Nyckeln: "club:12", "region:34", "text:Namn", "none".</summary>
        public List<SurplusOrgChoice> Orgs { get; set; } = new();
        public string? Note { get; set; }
    }

    public class SurplusFixed
    {
        public string Label { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class SurplusOrgChoice
    {
        public string Key { get; set; } = "";
        /// <summary>Bara för en förening som lagts till för hand (finns inte i bemanningen).</summary>
        public string? Name { get; set; }
        public bool Include { get; set; } = true;
        public decimal Weight { get; set; }
    }

    /// <summary>En förening i fördelningen: talen ur bemanningen, valen och beloppet.</summary>
    public sealed record SurplusOrg(string Key, string Name, int People, int Assignments, int Days, bool Include, decimal Weight, bool Manual);

    public sealed record SurplusShare(string Key, decimal Basis, decimal Amount);

    public static class PlanningSurplusRules
    {
        public const int MaxFixed = 20;

        /// <summary>
        /// Fördelar det som blir kvar efter de fasta posterna, i hela kronor. Avrundningen: varje förening får sin andel
        /// nedåtavrundad, och de kronor som blir över går till dem med störst rest — summan blir alltid exakt.
        /// </summary>
        public static (List<SurplusShare> Shares, decimal Remaining, string? Error) Distribute(decimal amount, IEnumerable<SurplusFixed> fixedLines,
            string key, IReadOnlyList<SurplusOrg> orgs)
        {
            if (amount < 0) return (new(), 0, "Överskottet kan inte vara negativt.");
            var fixedSum = fixedLines.Sum(f => f.Amount);
            if (fixedLines.Any(f => f.Amount < 0)) return (new(), 0, "En fast post kan inte vara negativ.");
            var remaining = Math.Round(amount - fixedSum, 0, MidpointRounding.AwayFromZero);
            if (remaining < 0) return (new(), remaining, "De fasta posterna är större än överskottet.");
            var k = SurplusKey.Normalize(key);
            decimal Basis(SurplusOrg o) => !o.Include ? 0 : k switch
            {
                SurplusKey.Assignments => o.Assignments,
                SurplusKey.Days => o.Days,
                SurplusKey.Equal => 1,
                SurplusKey.Manual => Math.Max(0, o.Weight),
                _ => o.People,
            };
            var bases = orgs.Select(o => (o.Key, B: Basis(o))).ToList();
            var total = bases.Sum(b => b.B);
            if (remaining > 0 && total <= 0)
                return (bases.Select(b => new SurplusShare(b.Key, b.B, 0)).ToList(), remaining,
                    k == SurplusKey.Manual ? "Skriv en andel för minst en förening." : "Ingen av föreningarna har något att fördela efter.");
            if (total <= 0) return (bases.Select(b => new SurplusShare(b.Key, b.B, 0)).ToList(), remaining, null);
            var raw = bases.Select(b => (b.Key, b.B, Exact: remaining * b.B / total)).ToList();
            var floors = raw.Select(r => Math.Floor(r.Exact)).ToList();
            var left = (int)(remaining - floors.Sum());
            var order = raw.Select((r, i) => (i, Frac: r.Exact - floors[i])).OrderByDescending(x => x.Frac).ThenBy(x => x.i).ToList();
            for (var n = 0; n < left && n < order.Count; n++) floors[order[n].i] += 1;
            return (raw.Select((r, i) => new SurplusShare(r.Key, r.B, floors[i])).ToList(), remaining, null);
        }
    }
}
