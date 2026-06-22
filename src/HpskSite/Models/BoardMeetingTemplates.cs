namespace HpskSite.Models
{
    /// <summary>
    /// Meeting types + their default dagordning (agenda). Creating a meeting of a given type seeds
    /// these headings as agenda items. Mirrors the static-definition pattern of BoardRoleDefinitions.
    /// Agendas follow common Swedish ideell-förening practice; they're a starting point, fully editable.
    /// </summary>
    public static class BoardMeetingTemplates
    {
        public class MeetingTypeDef
        {
            public string Key { get; set; } = "";
            public string Label { get; set; } = "";
            public string[] Agenda { get; set; } = System.Array.Empty<string>();
        }

        public static readonly MeetingTypeDef[] Types = new[]
        {
            new MeetingTypeDef
            {
                Key = "Konstituerande",
                Label = "Konstituerande styrelsemöte",
                Agenda = new[]
                {
                    "Mötets öppnande",
                    "Val av mötesordförande och mötessekreterare",
                    "Val av justerare",
                    "Fastställande av dagordning",
                    "Konstituering av styrelsen (fördelning av poster)",
                    "Val av firmatecknare",
                    "Beslut om attesträtt",
                    "Fastställande av sammanträdesplan",
                    "Övriga frågor",
                    "Mötets avslutande",
                }
            },
            new MeetingTypeDef
            {
                Key = "Styrelsemote",
                Label = "Ordinarie styrelsemöte",
                Agenda = new[]
                {
                    "Mötets öppnande",
                    "Val av justerare",
                    "Fastställande av dagordning",
                    "Föregående mötesprotokoll",
                    "Ekonomisk rapport",
                    "Rapporter (kommittéer, sektioner, träning, tävling)",
                    "Inkomna skrivelser",
                    "Beslutsärenden",
                    "Övriga frågor",
                    "Nästa möte",
                    "Mötets avslutande",
                }
            },
            new MeetingTypeDef
            {
                Key = "Arsmote",
                Label = "Årsmöte",
                Agenda = new[]
                {
                    "Mötets öppnande",
                    "Fråga om mötet utlysts på rätt sätt",
                    "Fastställande av röstlängd",
                    "Val av mötesordförande och mötessekreterare",
                    "Val av två justerare tillika rösträknare",
                    "Fastställande av dagordning",
                    "Styrelsens verksamhetsberättelse",
                    "Styrelsens ekonomiska berättelse (resultat- och balansräkning)",
                    "Revisorernas berättelse",
                    "Fråga om ansvarsfrihet för styrelsen",
                    "Fastställande av medlemsavgift",
                    "Fastställande av verksamhetsplan och budget",
                    "Behandling av motioner och propositioner",
                    "Val av ordförande",
                    "Val av styrelseledamöter och suppleanter",
                    "Val av revisorer",
                    "Val av valberedning",
                    "Övriga frågor",
                    "Mötets avslutande",
                }
            },
            new MeetingTypeDef
            {
                Key = "ExtraArsmote",
                Label = "Extra årsmöte",
                Agenda = new[]
                {
                    "Mötets öppnande",
                    "Fråga om mötet utlysts på rätt sätt",
                    "Fastställande av röstlängd",
                    "Val av mötesordförande och mötessekreterare",
                    "Val av två justerare tillika rösträknare",
                    "Fastställande av dagordning",
                    "Ärende som föranlett det extra årsmötet",
                    "Mötets avslutande",
                }
            },
            new MeetingTypeDef
            {
                Key = "Custom",
                Label = "Eget möte",
                Agenda = new[]
                {
                    "Mötets öppnande",
                    "Övriga frågor",
                    "Mötets avslutande",
                }
            },
        };

        public static string GetLabel(string key)
        {
            var match = System.Array.Find(Types, t => t.Key == key);
            return match?.Label ?? key;
        }

        public static string[] GetAgenda(string key)
        {
            var match = System.Array.Find(Types, t => t.Key == key);
            return match?.Agenda ?? System.Array.Empty<string>();
        }
    }
}
