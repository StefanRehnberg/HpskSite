using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// EN beskrivning av tävlingens redigerbara fält — vilka de är, vad de heter för
    /// användaren, vilken flik de hör hemma på och vilka grenar de gäller.
    ///
    /// VARFÖR: fälten var handskrivna som markup i TRE filer — CompetitionEditModal,
    /// SpringskytteEditModal och CompetitionWizardModal. 52 av dem fanns i två eller
    /// fler, 27 i alla tre. Följden är inte bara dubbelarbete: ett fält kan läggas till
    /// i guiden och glömmas i redigeringen, och de två modalerna hade redan HUNNIT
    /// glida isär (Springskyttes flikrubriker saknade å/ä/ö, och tre fält låg på olika
    /// flikar). Ett register gör "vilka fält finns" till en fråga med ett svar.
    ///
    /// ⚠️ INNEHÅLLET ÄR MASKINHÄRLETT, inte handskrivet. Det extraherades ur de
    /// renderade modalerna (hpsk-verify/_extract-field-catalog.mjs) just för att en
    /// handskriven kopia hade kunnat drifta från markupen från dag ett.
    /// `compedit-catalog-verify.mjs` asserterar att katalogen fortfarande stämmer med
    /// vad modalerna faktiskt renderar — den är kontraktet, inte dokumentationen.
    ///
    /// ⚠️ Registret äger de ENKLA fälten. Sammansatta kontroller — klassväljaren,
    /// skjutbaneväljaren, Fältskyttes konfigurationskoppling, CKEditor för
    /// beskrivningen, funktionärsväljaren och Swish-valideringen — förblir egen markup
    /// och placeras via <see cref="FieldControl.Slot"/>. Att generalisera dem också är
    /// vad som hade gjort det här till ett träsk.
    /// </summary>
    public static class CompetitionFieldCatalog
    {
        /// <summary>Flikarnas ordning. Den som lägger till en flik gör det HÄR.</summary>
        public static readonly string[] TabOrder =
        {
            "Grundinformation",
            "Plats & skjutbana",
            "Omfattning & serie",
            "Deltävling",
            "Datum",
            "Anmälansinformation",
            "Konfiguration",
            "Egenbokning",
            "Fältskytte-inställningar",
            "Arrangör / Synlighet",
            "Tävlingsledning & Betalning"
        };

        // Grenar. Springskytte har en egen modal med FÄRRE fält; Fältskytte och
        // MagnumFält delar en uppsättning som de andra inte visar.
        public const string Springskytte = "Springskytte";
        private static readonly string[] FaltFamily = { "Faltskytte", "MagnumFalt" };

        private static readonly List<CompetitionField> All = new()
        {
            // ── Grundinformation ────────────────────────────────────────────────
            new("competitionName", "Tävlingsnamn", FieldControl.Text, "Grundinformation", 1, required: true),
            new("description", "Beskrivning", FieldControl.Slot, "Grundinformation", 2,
                slot: "description-rte",
                note: "CKEditor. Innehållet synkas till textarean innan formuläret läses."),

            // ── Plats & skjutbana ───────────────────────────────────────────────
            new("venue", "Plats", FieldControl.Text, "Plats & skjutbana", 1, required: true),
            new("rangeId", "Tävlingsplats (skjutbana)", FieldControl.Slot, "Plats & skjutbana", 2,
                slot: "range-picker",
                note: "_CompetitionRangePicker — klubblista, sök och karta."),

            // ── Omfattning & serie ──────────────────────────────────────────────
            new("competitionScope", "Omfattning", FieldControl.Select, "Omfattning & serie", 1,
                help: "Avgör hur standardmedaljer beräknas"),
            new("seriesId", "Tävlingsserie (valfritt)", FieldControl.Select, "Omfattning & serie", 2),

            // ── Deltävling ──────────────────────────────────────────────────────
            new("subCompetitionName", "Deltävling i tävlingen (valfritt)", FieldControl.Text, "Deltävling", 1,
                help: "Om ifyllt visas en kryssruta vid anmälan."),
            new("subCompetitionFee", "Anmälningsavgift för Deltävling", FieldControl.Number, "Deltävling", 2),
            // ⚠️ Tom etikett = "katalogen påstår ingenting om etiketten här", och det är
            // avsiktligt: en radiogrupp har ingen enskild <label for>, bara en per
            // alternativ. Kontraktstestet hoppar över tomma etiketter i stället för att
            // jämföra mot ett godtyckligt valt alternativ — ett påstående som inte går
            // att pröva ärligt ska inte stå i katalogen alls.
            new("subCompetitionFeeMode", "", FieldControl.Radio, "Deltävling", 3,
                note: "perClass | perRegistration"),

            // ── Datum ───────────────────────────────────────────────────────────
            new("competitionDate", "Tävlingsdatum", FieldControl.DateTime, "Datum", 1, required: true),
            new("competitionEndDate", "Slutdatum", FieldControl.Date, "Datum", 2,
                help: "Lämna tomt för endagstävlingar"),

            // ── Anmälansinformation ─────────────────────────────────────────────
            new("registrationOpenDate", "Anmälan öppnar", FieldControl.DateTime, "Anmälansinformation", 1, required: true),
            new("registrationCloseDate", "Anmälan stänger", FieldControl.DateTime, "Anmälansinformation", 2, required: true),
            new("maxParticipants", "Max antal deltagare", FieldControl.Number, "Anmälansinformation", 3, required: true),
            new("registrationFee", "Anmälningsavgift", FieldControl.Number, "Anmälansinformation", 4),
            new("juniorRegistrationFee", "Junioravgift", FieldControl.Number, "Anmälansinformation", 5),
            new("allowTeams", "Tillåt laganmälan", FieldControl.Checkbox, "Anmälansinformation", 6),
            new("teamRegistrationFee", "Lagavgift", FieldControl.Number, "Anmälansinformation", 7),
            new("teamResultSeriesCount", "Antal serier i lagresultat", FieldControl.Number, "Anmälansinformation", 8,
                notFor: new[] { Springskytte },
                help: "0 eller tomt = automatiskt (kvalseriernas antal)"),
            new("allowStafett", "Tillåt stafettanmälan", FieldControl.Checkbox, "Anmälansinformation", 9),
            new("stafettRegistrationFee", "Stafettavgift", FieldControl.Number, "Anmälansinformation", 10),

            // ── Konfiguration ───────────────────────────────────────────────────
            // ⚠ INTE required i HTML-mening: det dolda faltet bar inget required-attribut.
            // Kravet drivs av valjaren sjalv ("Valj minst en klass") och av servern.
            // Katalogen far inte pasta nagot markupen inte gor — da ljuger kontraktet.
            new("shootingClassIds", "Skytteklasser", FieldControl.Slot, "Konfiguration", 1,
                slot: "class-picker",
                note: "_ShootingClassPicker — DELAD med tävlingsguiden. Obligatorisk via väljaren."),
            new("numberOfSeriesOrStations", "Antal serier/stationer", FieldControl.Number, "Konfiguration", 2,
                required: true, notFor: new[] { Springskytte },
                note: "Springskytte räknar fram detta ur klass A:s stationsantal."),
            new("numberOfFinalSeries", "Varav finalserier", FieldControl.Number, "Konfiguration", 3,
                notFor: new[] { Springskytte }),
            new("allowDualCClass", "Tillåt dubbel C-klassregistrering", FieldControl.Checkbox, "Konfiguration", 4,
                notFor: new[] { Springskytte }),
            new("showLiveResults", "Visa live-resultat", FieldControl.Checkbox, "Konfiguration", 5),
            new("isAwardingStandardMedals", "Standardmedaljsgrundande", FieldControl.Checkbox, "Konfiguration", 6,
                help: "Standardmedaljer får inte delas ut vid klubbtävlingar (BR-PS.1.3)."),
            new("allowSelfReporting", "Tillåt resultatrapportering (hemmabana)", FieldControl.Checkbox, "Konfiguration", 7,
                notFor: new[] { Springskytte }),

            // ── Fältskytte-inställningar (bara fältfamiljen) ────────────────────
            new("scoringMode", "Tävlingstyp", FieldControl.Slot, "Fältskytte-inställningar", 1,
                onlyFor: FaltFamily, slot: "falt-scoringmode",
                note: "Läs ALDRIG competition.scoringMode ensamt — använd FaltskytteScoringMode.Resolve.",
                inteIGuiden: "Tävlingstypen bor i den sparade fältkonfigurationen — guiden kopplar en konfiguration i stället."),
            new("maxReshoots", "Max omskjutningar", FieldControl.Number, "Fältskytte-inställningar", 2,
                onlyFor: FaltFamily,
                inteIGuiden: "Fältskytteinställning som sätts efter att tävlingen kopplats till en konfiguration."),
            new("rollingStart", "Rullande start", FieldControl.Slot, "Fältskytte-inställningar", 3,
                onlyFor: FaltFamily, slot: "falt-rollingstart",
                note: "JSON, byggs av kryssruta + patrullstorlek.",
                inteIGuiden: "Rullande start konfigureras efter att patrullerna finns, inte vid skapandet."),
            new("faltskytteSelfServiceResults", "Tillåt skyttar i laget att fylla i resultat (självservice)",
                FieldControl.Checkbox, "Fältskytte-inställningar", 4, onlyFor: FaltFamily),
            new("stationConfig", "Stationskonfiguration", FieldControl.Slot, "Fältskytte-inställningar", 5,
                onlyFor: FaltFamily, slot: "falt-config",
                note: "_FaltskytteCompetitionPicker.",
                inteIGuiden: "Stationerna kommer från den sparade konfigurationen (_FaltskytteCompetitionPicker)."),

            // ── Arrangör / Synlighet ────────────────────────────────────────────
            new("clubId", "Ansvarig klubb", FieldControl.Select, "Arrangör / Synlighet", 1,
                note: "⚠ Klubb ELLER krets ELLER mästerskapstyp måste vara satt, annars kan " +
                      "CompetitionUrlProvider inte bilda någon URL."),
            new("regionalFederation", "Krets (endast om ingen klubb)", FieldControl.Select, "Arrangör / Synlighet", 2),
            new("isClubOnly", "Endast för specifik klubb", FieldControl.Checkbox, "Arrangör / Synlighet", 3),

            // ── Tävlingsledning & Betalning ─────────────────────────────────────
            new("competitionDirector", "Tävlingsledare", FieldControl.Text, "Tävlingsledning & Betalning", 1, required: true),
            new("contactEmail", "Kontakt e-post", FieldControl.Text, "Tävlingsledning & Betalning", 2, required: true),
            new("contactPhone", "Kontakt telefon", FieldControl.Text, "Tävlingsledning & Betalning", 3),
            new("competitionManagerIds", "Tävlingsledare", FieldControl.Slot, "Tävlingsledning & Betalning", 4,
                slot: "manager-picker",
                inteIGuiden: "Tävlingsansvariga utses efter att tävlingen skapats."),
            new("swishNumber", "Swish-nummer", FieldControl.Text, "Tävlingsledning & Betalning", 5,
                slot: "swish-validation",
                note: "Bär egen formatvalidering, se _SwishNumberValidation."),
            new("addToMenu", "Lägg till genväg till tävlingen i menyn", FieldControl.Checkbox, "Tävlingsledning & Betalning", 6,
                inteIGuiden: "Genvägen i menyn är ett publiceringsbeslut, inte en egenskap man tar ställning till när tävlingen skapas.")
        };

        /// <summary>Alla fält som gäller en viss gren, i flik- och fältordning.</summary>
        public static IReadOnlyList<CompetitionField> For(string competitionType)
        {
            var t = (competitionType ?? "").Trim();
            return All.Where(f => f.AppliesTo(t))
                      .OrderBy(f => TabIndex(f.Tab))
                      .ThenBy(f => f.Order)
                      .ToList();
        }

        /// <summary>Fälten grupperade per flik, i flikordning. Tomma flikar utelämnas.</summary>
        public static IReadOnlyList<(string Tab, IReadOnlyList<CompetitionField> Fields)> TabsFor(string competitionType)
            => For(competitionType)
                .GroupBy(f => f.Tab)
                .OrderBy(g => TabIndex(g.Key))
                .Select(g => (g.Key, (IReadOnlyList<CompetitionField>)g.OrderBy(f => f.Order).ToList()))
                .ToList();

        public static CompetitionField? Find(string name)
            => All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Varje fältnamn katalogen känner till — oavsett gren.</summary>
        public static IReadOnlyCollection<string> AllFieldNames
            => All.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private static int TabIndex(string tab)
        {
            var i = Array.IndexOf(TabOrder, tab);
            return i < 0 ? int.MaxValue : i;   // okänd flik hamnar sist, inte först
        }
    }

    public enum FieldControl
    {
        Text, Number, Date, DateTime, Checkbox, Radio, Select, Textarea,
        /// <summary>Sammansatt kontroll med egen markup; katalogen placerar den bara.</summary>
        Slot
    }

    public sealed class CompetitionField
    {
        public CompetitionField(string name, string label, FieldControl control, string tab, int order,
                                bool required = false, string? help = null, string? note = null,
                                string? slot = null, string[]? onlyFor = null, string[]? notFor = null,
                                string? inteIGuiden = null)
        {
            Name = name; Label = label; Control = control; Tab = tab; Order = order;
            Required = required; Help = help; Note = note; Slot = slot;
            OnlyFor = onlyFor; NotFor = notFor; InteIGuiden = inteIGuiden;
        }

        public string Name { get; }
        public string Label { get; }
        public FieldControl Control { get; }
        public string Tab { get; }
        public int Order { get; }
        public bool Required { get; }
        public string? Help { get; }
        /// <summary>Anteckning till utvecklaren, renderas aldrig.</summary>
        public string? Note { get; }
        /// <summary>Namn på den sammansatta kontroll som ska placeras här.</summary>
        public string? Slot { get; }
        public string[]? OnlyFor { get; }
        public string[]? NotFor { get; }

        /// <summary>
        /// Satt till ett SKÄL när fältet medvetet saknas i tävlingsguiden. Null = fältet
        /// ska finnas där.
        ///
        /// ⚠️ Guiden och redigeringsmodalerna hade glidit isär på sju fält utan att något
        /// märkte det — ingenting jämförde guiden mot katalogen. Ett fält som läggs till i
        /// redigeringen och glöms i guiden är precis den tysta driften registret finns för
        /// att stoppa. Att utelämna ett fält är helt i sin ordning; att göra det UTAN skäl
        /// är det inte, och <c>wizard-catalog-verify</c> kräver att listan stämmer.
        /// </summary>
        public string? InteIGuiden { get; }

        /// <summary>Fältet ska renderas i tävlingsguiden.</summary>
        public bool FinnsIGuiden => InteIGuiden == null;

        public bool AppliesTo(string competitionType)
        {
            if (OnlyFor is { Length: > 0 } &&
                !OnlyFor.Contains(competitionType, StringComparer.OrdinalIgnoreCase)) return false;
            if (NotFor is { Length: > 0 } &&
                NotFor.Contains(competitionType, StringComparer.OrdinalIgnoreCase)) return false;
            return true;
        }
    }
}
