using System.Collections.Generic;

namespace HpskSite.Models
{
    /// <summary>
    /// A club's per-year "att beställa / att dela ut"-underlag: what physical märken and
    /// standardmedaljer the club's members earned during one year, counted per valör for the
    /// SPSF order and listed per member for the utdelning.
    ///
    /// <b>The definition is ÅRETS FÖRVÄRVADE MÄRKEN</b> (Stefan 2026-08-31), deliberately not
    /// "det klubben ännu inte beställt". Nothing here books an order, so the same year can be
    /// produced again unchanged — which is the point: the list is derived, never stored, and
    /// therefore cannot drift from the ledgers. The consequence the club must live with is that
    /// reconciling against what was actually ordered stays on paper with the kassör.
    /// </summary>
    public class MarkenOrderList
    {
        public int Year { get; set; }
        public int ClubId { get; set; }
        public string ClubName { get; set; } = "";

        /// <summary>Att beställa — one line per (grupp, artikel) with a count.</summary>
        public List<MarkenOrderLine> Order { get; set; } = new();

        /// <summary>Att dela ut — one entry per member, with everything they earned that year.</summary>
        public List<MarkenHandoutMember> Handout { get; set; } = new();

        /// <summary>
        /// Things that block or muddy the order (a Guld with no registration number, an
        /// unverified self-reported medal). Named rather than silently included or excluded —
        /// a list that quietly drops an item reads as complete.
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Total physical items to order (sum of <see cref="MarkenOrderLine.Count"/>).</summary>
        public int TotalItems { get; set; }

        /// <summary>How many handout items are not yet verified (counted, not hidden).</summary>
        public int UnverifiedItems { get; set; }
    }

    /// <summary>One order line: N of this article.</summary>
    public class MarkenOrderLine
    {
        public string Group { get; set; } = "";
        public string Item { get; set; } = "";
        public int Count { get; set; }

        /// <summary>Sorting key within the group (valör order, then alphabetical).</summary>
        public int Sort { get; set; }

        /// <summary>Free-text note shown next to the line (e.g. that guldnummer is required).</summary>
        public string Note { get; set; } = "";
    }

    /// <summary>One member's årsskörd, for the utdelningslistan.</summary>
    public class MarkenHandoutMember
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public List<MarkenHandoutItem> Items { get; set; } = new();
    }

    /// <summary>One thing a member earned that year.</summary>
    public class MarkenHandoutItem
    {
        public string Group { get; set; } = "";
        public string Item { get; set; } = "";

        /// <summary>Context for the ceremony — guldnummer, tävling, disciplin, inteckningsläge.</summary>
        public string Detail { get; set; } = "";

        /// <summary>
        /// False for achievements that carry no physical badge (a fulfilled Guldfodring between two
        /// årtalsmärke-steg). Kept on the list because it IS read out at the årsmöte, but it must
        /// never turn into an order line.
        /// </summary>
        public bool Orderable { get; set; } = true;

        /// <summary>Reported but not yet verified by a functionary.</summary>
        public bool Unverified { get; set; }
    }
}
