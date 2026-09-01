namespace HpskSite.Models
{
    /// <summary>
    /// Meeting types + their default dagordning, expressed as ordered <see cref="BoardAgendaItemCatalog"/>
    /// keys (so each seeded item carries its type: note / text / election). Creating a meeting seeds the
    /// club/region's saved template for that type if it has one, otherwise these built-in defaults.
    /// Agendas follow common Swedish ideell-förening practice; they're a starting point, fully editable.
    /// </summary>
    public static class BoardMeetingTemplates
    {
        public class MeetingTypeDef
        {
            public string Key { get; set; } = "";
            public string Label { get; set; } = "";
            public string[] AgendaKeys { get; set; } = System.Array.Empty<string>();
        }

        public static readonly MeetingTypeDef[] Types = new[]
        {
            new MeetingTypeDef
            {
                Key = "Konstituerande",
                Label = "Konstituerande styrelsemöte",
                AgendaKeys = new[]
                {
                    "motets-oppnande", "val-ordforande", "val-sekreterare", "val-justerare",
                    "faststall-dagordning", "konstituering", "firmatecknare", "attestratt",
                    "sammantradesplan", "ovriga-fragor", "motets-avslutande",
                }
            },
            new MeetingTypeDef
            {
                Key = "Styrelsemote",
                Label = "Ordinarie styrelsemöte",
                AgendaKeys = new[]
                {
                    "motets-oppnande", "val-justerare", "faststall-dagordning", "foregaende-protokoll",
                    "ekonomisk-rapport", "rapporter", "skrivelser", "beslutsarenden", "ovriga-fragor",
                    "nasta-mote", "motets-avslutande",
                }
            },
            new MeetingTypeDef
            {
                Key = "Arsmote",
                Label = "Årsmöte",
                AgendaKeys = new[]
                {
                    "motets-oppnande", "kallelse-ok", "rostlangd", "val-ordforande", "val-sekreterare",
                    "val-justerare-2", "faststall-dagordning", "verksamhetsberattelse", "ekonomisk-berattelse",
                    "revisionsberattelse", "ansvarsfrihet", "medlemsavgift", "verksamhetsplan-budget",
                    "motioner", "val-foreningsordforande", "val-ledamoter", "val-revisorer", "val-valberedning",
                    // Efter valen och före övriga frågor: utdelningen är ceremoniell och hör sist i
                    // mötet, men den ska ligga FÖRE "Övriga frågor" så den inte blir en punkt som
                    // faller bort när mötet börjar avrundas.
                    "utmarkelser",
                    "ovriga-fragor", "motets-avslutande",
                }
            },
            new MeetingTypeDef
            {
                Key = "ExtraArsmote",
                Label = "Extra årsmöte",
                AgendaKeys = new[]
                {
                    "motets-oppnande", "kallelse-ok", "rostlangd", "val-ordforande", "val-sekreterare",
                    "val-justerare-2", "faststall-dagordning", "extra-arende", "motets-avslutande",
                }
            },
            new MeetingTypeDef
            {
                Key = "Custom",
                Label = "Eget möte",
                AgendaKeys = new[] { "motets-oppnande", "ovriga-fragor", "motets-avslutande" }
            },
        };

        public static string GetLabel(string key)
        {
            var match = System.Array.Find(Types, t => t.Key == key);
            return match?.Label ?? key;
        }

        /// <summary>Resolve a meeting type's built-in default agenda to typed catalog item definitions.</summary>
        public static List<BoardAgendaItemCatalog.AgendaItemDef> GetDefaultAgenda(string key)
        {
            var match = System.Array.Find(Types, t => t.Key == key);
            var keys = match?.AgendaKeys ?? System.Array.Empty<string>();
            var list = new List<BoardAgendaItemCatalog.AgendaItemDef>();
            foreach (var k in keys)
            {
                var def = BoardAgendaItemCatalog.Get(k);
                if (def != null) list.Add(def);
            }
            return list;
        }
    }
}
