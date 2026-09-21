using System.Collections.Generic;

namespace HpskSite.Models
{
    /// <summary>
    /// Årets mästerskapsmedaljer för EN klubb eller EN krets: vad som ska beställas och graveras,
    /// och vem som ska kallas fram på årsmötet.
    ///
    /// <para><b>Avgränsningen är arrangörskapet, inte medlemskapet</b> (Stefan 2026-09-20):
    /// klubbens årsmöte delar ut medaljerna för klubbens egna <i>klubbmästerskap</i>, kretsens
    /// årsmöte medaljerna för kretsens <i>kretsmästerskap</i>. En medlem som tog kretsguld står
    /// alltså på kretsens lista och inte på klubbens — det är kretsen som beställer och delar ut
    /// den medaljen.</para>
    ///
    /// <para><b>⚠️ Listan är HÄRLEDD, aldrig lagrad.</b> Varje medaljör läses ur den tävlingens
    /// sparade resultatartefakt via <c>PrizeGivingService</c>, som är den enda kodväg som rankar
    /// medaljer. Att räkna om dem här skulle vara en andra rankning, och just den förväxlingen —
    /// mästerskapskategori mot skicklighetsklass — har uppstått flera gånger i den här kodbasen.
    /// Priset är att en tävling vars resultatlista inte räknats om saknas i listan; därför NAMNGES
    /// varje sådan tävling i <see cref="Warnings"/> i stället för att tyst utebli.</para>
    /// </summary>
    public class MedalHandoutList
    {
        public int Year { get; set; }

        /// <summary>"Club" eller "Region" — vilken sorts arrangör listan gäller.</summary>
        public string ScopeType { get; set; } = "Club";

        /// <summary>Klubbens nod-id, eller kretsens regionkod.</summary>
        public string ScopeId { get; set; } = "";

        public string ScopeName { get; set; } = "";

        /// <summary>Omfattningen som räknas in: "Klubbmästerskap" eller "Kretsmästerskap".</summary>
        public string Scope { get; set; } = "";

        /// <summary>Tävlingarna listan bygger på — en rad per mästerskap, även när det saknar medaljer.</summary>
        public List<MedalHandoutCompetition> Competitions { get; set; } = new();

        /// <summary>Att beställa — antal per valör, uppdelat på individuellt och lag.</summary>
        public List<MedalHandoutOrderLine> Order { get; set; } = new();

        /// <summary>Att dela ut — en post per mottagare, med allt hen ska få.</summary>
        public List<MedalHandoutRecipient> Handout { get; set; } = new();

        /// <summary>
        /// Medaljplatser som ska delas ut men ännu inte har någon mottagare — oavgjord särskjutning
        /// eller två lag som står lika.
        ///
        /// <para><b>⚠️ De räknas ÄNDÅ in i <see cref="Order"/></b> (Stefan 2026-09-20): medaljen ska
        /// beställas oavsett vem som till slut vinner den. Det är bara graveringen och uppropet som
        /// måste vänta, och därför står de här som egna rader i stället för att namnge fel person.</para>
        /// </summary>
        public List<MedalHandoutUnresolved> Unresolved { get; set; } = new();

        /// <summary>
        /// Det som gör listan osäker: en tävling utan resultatlista, en artefakt som räknats innan
        /// medaljfunktionen fanns, en medaljindelning som ändrats utan omräkning.
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Totalt antal medaljer att beställa (summan av <see cref="MedalHandoutOrderLine.Count"/>).</summary>
        public int TotalMedals { get; set; }

        /// <summary>Distinkta mottagare — en person kallas fram en gång även med flera medaljer.</summary>
        public int RecipientCount { get; set; }
    }

    /// <summary>En mästerskapstävling som ingår i underlaget.</summary>
    public class MedalHandoutCompetition
    {
        public int CompetitionId { get; set; }
        public string Name { get; set; } = "";
        public DateTime? Date { get; set; }

        /// <summary>Sista tävlingsdagen när tävlingen går över flera dagar, annars null.</summary>
        public DateTime? EndDate { get; set; }

        public string CompetitionType { get; set; } = "";

        /// <summary>
        /// Tävlingen har inte hunnit avgöras än — sista tävlingsdagen är i dag eller senare.
        ///
        /// <para><b>⚠️⚠️ EN KOMMANDE TÄVLING ÄR INGEN VARNING.</b> Första utsågan flaggade varje
        /// mästerskap utan resultatlista i gult, alltså även de som inte hade ägt rum — vilket gör
        /// att en klubb med några tävlingar kvar på året möts av en skärm full av gula rutor som
        /// alla säger "åtgärda". <b>En varning som alltid lyser slutar läsas</b>, och då missas den
        /// gång den betyder något: en tävling som VARIT men vars lista aldrig räknats om.</para>
        ///
        /// <para>Gränsen mäts på sista tävlingsdagen, inte på startdagen — en tvådagars tävling som
        /// pågår just nu saknar rimligen sina resultat än.</para>
        /// </summary>
        public bool IsUpcoming { get; set; }

        /// <summary>Antal medaljer ur den här tävlingen (individ + lag), oavgjorda inräknade.</summary>
        public int MedalCount { get; set; }

        /// <summary>
        /// Vilken INDELNING medaljerna räknades i — en uppsättning per vapengrupp, eller delade i
        /// mästerskapsklasser (<c>medalsPerWeaponGroup</c>).
        ///
        /// <para><b>⚠️ Beskriver ARTEFAKTEN, inte tävlingens nuvarande inställning.</b> Talen och
        /// kategorinamnen i listan kommer ur den sparade resultatartefakten, och en etikett ur
        /// nuläget över siffror ur artefakten är en tyst lögn. Indelningen är dessutom arrangörens
        /// val per tävling, så EN sammanställning kan mycket väl innehålla både "C" och
        /// "C Dam" — och utan den här texten går det inte att se om ett saknat damguld är ett val
        /// eller ett fel.</para>
        /// </summary>
        public string MedalGroupingText { get; set; } = "";

        /// <summary>Satt när tävlingens indelning ändrats sedan listan räknades ut.</summary>
        public string? MedalGroupingStale { get; set; }

        public DateTime? ResultsUpdatedAt { get; set; }

        /// <summary>False när tävlingen inte bidrar med några medaljer och varför står i <see cref="Problem"/>.</summary>
        public bool HasMedals { get; set; }

        /// <summary>Klartext om varför tävlingen inte bidrar, eller null.</summary>
        public string? Problem { get; set; }
    }

    /// <summary>En beställningsrad: N medaljer av den här valören.</summary>
    public class MedalHandoutOrderLine
    {
        /// <summary>"Individuella medaljer" eller "Lagmedaljer".</summary>
        public string Group { get; set; } = "";

        /// <summary>"Guld", "Silver" eller "Brons".</summary>
        public string Medal { get; set; } = "";

        public int Count { get; set; }

        /// <summary>Sorteringsnyckel: grupp först, sedan guld före silver före brons.</summary>
        public int Sort { get; set; }

        /// <summary>Notering, t.ex. att någon av medaljerna ännu saknar mottagare.</summary>
        public string Note { get; set; } = "";
    }

    /// <summary>En mottagare och allt hen ska få på årsmötet.</summary>
    public class MedalHandoutRecipient
    {
        /// <summary>0 för en mottagare vi bara har namnet på (en lagmedlem utan medlemskoppling).</summary>
        public int MemberId { get; set; }

        public string Name { get; set; } = "";

        /// <summary>Klubben skytten tävlade för. Står på kretsens lista, där mottagarna kommer från flera klubbar.</summary>
        public string Club { get; set; } = "";

        public List<MedalHandoutItem> Items { get; set; } = new();
    }

    /// <summary>En medalj en person ska få.</summary>
    public class MedalHandoutItem
    {
        /// <summary>"Guld", "Silver" eller "Brons".</summary>
        public string Medal { get; set; } = "";

        /// <summary>Mästerskapskategorin ("C Dam") för individ, lagklassen ("C Öppen") för lag.</summary>
        public string Category { get; set; } = "";

        public string CompetitionName { get; set; } = "";
        public int CompetitionId { get; set; }
        public DateTime? CompetitionDate { get; set; }

        /// <summary>True för en lagmedalj — den delas ut till varje lagmedlem.</summary>
        public bool IsTeam { get; set; }

        /// <summary>Lagets namn, för lagmedaljer.</summary>
        public string TeamName { get; set; } = "";

        /// <summary>
        /// Skicklighetsklass och resultat, eller lagets resultat. Läses upp vid utdelningen och
        /// visas intill medaljen — men det är INTE graveringstexten, se <see cref="Engraving"/>.
        /// </summary>
        public string Detail { get; set; } = "";

        /// <summary>
        /// Förslag till graveringstext för EN medalj.
        ///
        /// <para><b>⚠️ Ett FÖRSLAG, inte en standard.</b> Varje förening graverar på sitt sätt —
        /// somliga sätter bara tävling och år, andra namnet också — och vi känner inte den
        /// konventionen. Därför ligger delarna (valör, kategori, tävling, år, namn, lag) också
        /// som egna kolumner i exporten, så klubben kan sätta ihop sin egen text utan att behöva
        /// plocka isär vår.</para>
        ///
        /// <para>⚠️ ÅRET kommer ur TÄVLINGENS datum, aldrig ur dagens. En medalj som graveras i
        /// februari hör till förra säsongen, och fel årtal på en graverad medalj går inte att
        /// rätta.</para>
        /// </summary>
        public string Engraving(string recipientName)
        {
            var year = CompetitionDate?.Year;
            var parts = new List<string>();

            var comp = (CompetitionName ?? "").Trim();
            if (comp.Length > 0) parts.Add(year.HasValue ? $"{comp} {year}" : comp);
            else if (year.HasValue) parts.Add(year.Value.ToString());

            var cat = (Category ?? "").Trim();
            if (cat.Length > 0) parts.Add(cat);

            if (IsTeam && !string.IsNullOrWhiteSpace(TeamName)) parts.Add(TeamName.Trim());

            var name = (recipientName ?? "").Trim();
            if (name.Length > 0) parts.Add(name);

            return string.Join(" · ", parts);
        }
    }

    /// <summary>En medaljplats som ska delas ut men ännu inte har någon mottagare.</summary>
    public class MedalHandoutUnresolved
    {
        public int CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";

        /// <summary>Mästerskapskategorin eller lagklassen striden står i.</summary>
        public string Category { get; set; } = "";

        /// <summary>Hela förklaringen som prisutdelningen skriver den, t.ex.
        /// "Brons — särskjutning krävs mellan Ivan Slabiak och Markus Henningsson".</summary>
        public string Text { get; set; } = "";

        public bool IsTeam { get; set; }
    }
}
