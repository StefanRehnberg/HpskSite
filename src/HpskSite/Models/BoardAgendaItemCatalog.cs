namespace HpskSite.Models
{
    /// <summary>
    /// Ready-made, typed agenda items. The agenda editor offers these in a dropdown (plus the free-named
    /// "Övrigt"); meeting-type templates and saved club templates are just ordered lists of these keys.
    /// Each item carries its ItemType (note/text/election) so it renders + prints with the right fields.
    /// "election" items with a role (chairman/secretary/adjuster) drive who signs/justerar the protokoll.
    /// </summary>
    public static class BoardAgendaItemCatalog
    {
        public class AgendaItemDef
        {
            public string Key { get; set; } = "";
            public string Heading { get; set; } = "";
            public string ItemType { get; set; } = "text";   // note / text / election
            public string? ElectionRole { get; set; }         // chairman / secretary / adjuster / "" (generic)
            public int ElectionCount { get; set; } = 1;
            public string ElectionSource { get; set; } = "attendees";   // attendees / members
            public string? Hint { get; set; }                 // optional explanatory help for the editor
        }

        public static readonly AgendaItemDef[] Items = new[]
        {
            // --- Opening / formalia (note: anteckningar only) ---
            new AgendaItemDef { Key = "motets-oppnande",     Heading = "Mötets öppnande",                          ItemType = "note" },
            new AgendaItemDef { Key = "kallelse-ok",         Heading = "Fråga om mötet utlysts på rätt sätt",      ItemType = "note" },
            new AgendaItemDef { Key = "rostlangd",           Heading = "Fastställande av röstlängd",                ItemType = "note" },
            new AgendaItemDef { Key = "faststall-dagordning",Heading = "Fastställande av dagordning",               ItemType = "note" },
            new AgendaItemDef { Key = "foregaende-protokoll",Heading = "Föregående mötesprotokoll",                 ItemType = "note" },

            // --- Meeting-role elections (set who signs the protokoll) ---
            // Styrelsemötets justerare väljs bland styrelsen (närvarande); årsmötets funktionärer väljs bland medlemmarna.
            new AgendaItemDef { Key = "val-ordforande",      Heading = "Val av mötesordförande",                   ItemType = "election", ElectionRole = "chairman",  ElectionCount = 1, ElectionSource = "members",   Hint = "Blir mötesordförande på protokollet." },
            new AgendaItemDef { Key = "val-sekreterare",     Heading = "Val av sekreterare för mötet",             ItemType = "election", ElectionRole = "secretary", ElectionCount = 1, ElectionSource = "members",   Hint = "Blir mötessekreterare på protokollet." },
            new AgendaItemDef { Key = "val-justerare",       Heading = "Val av justerare",                          ItemType = "election", ElectionRole = "adjuster",  ElectionCount = 1, ElectionSource = "attendees", Hint = "De som väljs justerar (skriver under) protokollet." },
            new AgendaItemDef { Key = "val-justerare-2",     Heading = "Val av två justerare tillika rösträknare",  ItemType = "election", ElectionRole = "adjuster",  ElectionCount = 2, ElectionSource = "members",   Hint = "De som väljs justerar protokollet och räknar röster. Väljs bland mötets medlemmar." },

            // --- Reports / information (note) ---
            new AgendaItemDef { Key = "ekonomisk-rapport",   Heading = "Ekonomisk rapport",                         ItemType = "note" },
            new AgendaItemDef { Key = "rapporter",           Heading = "Rapporter (kommittéer, sektioner, träning, tävling)", ItemType = "note" },
            new AgendaItemDef { Key = "skrivelser",          Heading = "Inkomna skrivelser",                        ItemType = "note" },
            new AgendaItemDef { Key = "verksamhetsberattelse",Heading = "Styrelsens verksamhetsberättelse",        ItemType = "note" },
            new AgendaItemDef { Key = "ekonomisk-berattelse",Heading = "Styrelsens ekonomiska berättelse (resultat- och balansräkning)", ItemType = "note" },
            new AgendaItemDef { Key = "revisionsberattelse", Heading = "Revisorernas berättelse",                   ItemType = "note" },

            // --- Decision items (text: anteckningar + beslut) ---
            new AgendaItemDef { Key = "beslutsarenden",      Heading = "Beslutsärenden",                            ItemType = "text" },
            // Polisens blankett PM 551.24 säger uttryckligen att beslutet att utfärda ett
            // föreningsintyg ska fattas av STYRELSEN och bör noteras i mötesprotokollet. Utan en egen
            // punkt hamnar besluten under "Beslutsärenden" eller "Övriga frågor", och då går de inte
            // att hitta den dag någon frågar vilket möte som beslutade om ett visst intyg.
            new AgendaItemDef { Key = "foreningsintyg",      Heading = "Föreningsintyg",                            ItemType = "text" },
            new AgendaItemDef { Key = "ansvarsfrihet",       Heading = "Fråga om ansvarsfrihet för styrelsen",      ItemType = "text" },
            new AgendaItemDef { Key = "medlemsavgift",       Heading = "Fastställande av medlemsavgift",            ItemType = "text" },
            new AgendaItemDef { Key = "verksamhetsplan-budget",Heading = "Fastställande av verksamhetsplan och budget", ItemType = "text" },
            new AgendaItemDef { Key = "motioner",            Heading = "Behandling av motioner och propositioner",  ItemType = "text" },
            new AgendaItemDef { Key = "val-foreningsordforande",Heading = "Val av ordförande",                      ItemType = "text", Hint = "Val av föreningens ordförande (antecknas som beslut)." },
            new AgendaItemDef { Key = "val-ledamoter",       Heading = "Val av styrelseledamöter och suppleanter",  ItemType = "text" },
            new AgendaItemDef { Key = "val-revisorer",       Heading = "Val av revisorer",                          ItemType = "text" },
            new AgendaItemDef { Key = "val-valberedning",    Heading = "Val av valberedning",                       ItemType = "text" },
            new AgendaItemDef { Key = "konstituering",       Heading = "Konstituering av styrelsen (fördelning av poster)", ItemType = "text" },
            new AgendaItemDef { Key = "firmatecknare",       Heading = "Val av firmatecknare",                      ItemType = "text" },
            new AgendaItemDef { Key = "attestratt",          Heading = "Beslut om attesträtt",                      ItemType = "text" },
            new AgendaItemDef { Key = "sammantradesplan",    Heading = "Fastställande av sammanträdesplan",         ItemType = "text" },
            new AgendaItemDef { Key = "extra-arende",        Heading = "Ärende som föranlett det extra årsmötet",   ItemType = "text" },
            new AgendaItemDef { Key = "ovriga-fragor",       Heading = "Övriga frågor",                             ItemType = "text" },

            // --- Closing ---
            new AgendaItemDef { Key = "nasta-mote",          Heading = "Nästa möte",                                ItemType = "note" },
            new AgendaItemDef { Key = "motets-avslutande",   Heading = "Mötets avslutande",                         ItemType = "note" },

            // --- Free-named custom item (heading typed by the user) ---
            new AgendaItemDef { Key = "ovrigt",              Heading = "Egen punkt",                                ItemType = "text", Hint = "Egen punkt med valfri rubrik." },
        };

        public static AgendaItemDef? Get(string key) => System.Array.Find(Items, i => i.Key == key);
    }
}
