namespace HpskSite.Models.Events
{
    /// <summary>
    /// En klubbs eller krets händelse, oberoende av hur den lagras (sajtstrukturen fas 2 —
    /// <c>notes/sajtstruktur-plan-2026-10-09.md</c>). I dag en <c>clubSimpleEvent</c>-nod under klubben
    /// eller kretsen, efter bytet en rad i <c>ClubEvent</c>. Kod utanför
    /// <see cref="HpskSite.Services.Events.IClubEventStore"/> ska bara se den här typen, aldrig noden.
    ///
    /// <para><b>⚠️ Ägaren är (typ, id)</b>, aldrig "nodens förälder". Ägarens id är klubb- eller
    /// kretsnodens id — de ligger kvar i Umbraco.</para>
    /// </summary>
    public sealed class ClubEventRecord
    {
        public const string OwnerClub = "Club";
        public const string OwnerRegion = "Region";

        /// <summary>0 för en händelse som ännu inte sparats.</summary>
        public int Id { get; set; }
        public string OwnerType { get; set; } = OwnerClub;
        public int OwnerId { get; set; }

        /// <summary>Händelsens namn (eventName). Faller tillbaka på <see cref="PageName"/>.</summary>
        public string Name { get; set; } = "";
        /// <summary>Sidnamnet — det adressens sista del byggs av. Redigeras som "pageName".</summary>
        public string PageName { get; set; } = "";

        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public string EventType { get; set; } = "";
        public string Venue { get; set; } = "";
        /// <summary>RTE-HTML, sanerad av den som skriver.</summary>
        public string Description { get; set; } = "";
        public string ContactPerson { get; set; } = "";
        public string ContactEmail { get; set; } = "";
        public string ContactPhone { get; set; } = "";
        public string EquipmentRequired { get; set; } = "";
        public string TargetAudience { get; set; } = "";

        /// <summary>Bildens referens (mediets UDI). <see cref="ImageUrl"/> är upplöst av lagringen.</summary>
        public string ImageRef { get; set; } = "";
        public string ImageUrl { get; set; } = "";

        public bool IsActive { get; set; } = true;
        /// <summary>Synlig för besökare. Lagringen sätter den; en händelse skapas publicerad.</summary>
        public bool IsPublished { get; set; } = true;

        public bool RegistrationRequired { get; set; }
        public string RegistrationUrl { get; set; } = "";
        public int MaxParticipants { get; set; }
        public DateTime? RegistrationDeadline { get; set; }
        public bool IsMandatory { get; set; }
        public bool LanevapenOffered { get; set; }
        /// <summary>Prisraderna som lagrad JSON (<see cref="HpskSite.Models.EventPrices"/>).</summary>
        public string PricesJson { get; set; } = "";
        /// <summary>Vem som får anmäla sig (<see cref="HpskSite.Models.EventAudience"/>), normaliserad.</summary>
        public string Audience { get; set; } = HpskSite.Models.EventAudience.Club;
        /// <summary>Händelsens eget Swish-nummer. Tomt = ägarens.</summary>
        public string SwishNumber { get; set; } = "";

        /// <summary>Sidans innehållsblock och snabblänkar (JSON). Blir <c>LandingPage</c> efter bytet.</summary>
        public string ContentBlocksJson { get; set; } = "";
        public string QuickLinksJson { get; set; } = "";

        /// <summary>Händelsens publika adress, upplöst av lagringen. Tom när den inte går att avgöra.</summary>
        public string Url { get; set; } = "";

        public DateTime CreatedDate { get; set; }
        public DateTime UpdatedDate { get; set; }

        public bool IsClubOwned => OwnerType == OwnerClub;
        public bool IsRegionOwned => OwnerType == OwnerRegion;
        /// <summary>Namnet som visas: eventName, annars sidnamnet.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? PageName : Name;

        public ClubEventRecord Clone() => (ClubEventRecord)MemberwiseClone();
    }

    /// <summary>
    /// Vilka av de operatörstillagda fälten lagringen kan bära. Nodlagringen svarar ur händelsetypens
    /// egenskaper; tabellen bär alla. Ytorna visar en förklaring i stället för en kontroll som tyst
    /// inte sparar.
    /// </summary>
    public sealed record ClubEventFieldSupport(
        bool Mandatory, bool LanevapenOffered, bool Deadline, bool Prices, bool Swish, bool Audience)
    {
        public static readonly ClubEventFieldSupport All = new(true, true, true, true, true, true);
    }
}
