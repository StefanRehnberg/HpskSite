using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En not till resultat- och balansräkningen — en förklaring till en siffra, eller en
    /// upplysning som inte hör till något konto ("Redovisningsprinciper").
    ///
    /// <para><b>Född ur ett önskemål från en kassör (Michael Henriksson, 2026-09-28):</b> styrelsen
    /// och årsmötet ska kunna läsa <i>varför</i> inventarierna minskat eller en fordran står kvar,
    /// intill siffran. Utan noter hamnade förklaringen i ett separat dokument som aldrig följde
    /// med handlingarna.</para>
    ///
    /// <para><b>⚠️⚠️ NUMRET LAGRAS INTE.</b> Det härleds vid varje läsning av
    /// <see cref="LedgerNoteNumbering"/> ur kontonas ordning i räkningarna. Ett lagrat nummer hade
    /// behövt skrivas om för varje not efter den som läggs till eller tas bort — och en missad
    /// omskrivning ger två noter med samma nummer på ett papper till årsmötet.</para>
    ///
    /// <para><b>⚠️ Hör till ETT räkenskapsår</b> och låses när året fastställs: noterna är en del av
    /// det årsmötet fastställde.</para>
    /// </summary>
    [TableName("LedgerNote")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerNote
    {
        public int Id { get; set; }
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int FiscalYearId { get; set; }

        /// <summary>Notens rubrik, t.ex. "Inventarier". Obligatorisk.</summary>
        public string Title { get; set; } = "";

        /// <summary>Själva förklaringen.</summary>
        public string Body { get; set; } = "";

        public DateTime CreatedUtc { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        public int? ModifiedByMemberId { get; set; }
    }

    /// <summary>
    /// Att en not hänvisas till från ett konto. En not utan rader är en allmän not; en not med
    /// flera rader delas av flera konton (inventarier 1220 och deras avskrivningar 1229).
    /// </summary>
    [TableName("LedgerNoteAccount")]
    [PrimaryKey("NoteId,AccountNumber", AutoIncrement = false)]
    public class LedgerNoteAccount
    {
        public int NoteId { get; set; }
        public int AccountNumber { get; set; }
    }

    /// <summary>
    /// Numreringen av noterna. Ren funktion, ingen databas.
    ///
    /// <para>Regeln, i den ordning en läsare möter noterna:</para>
    /// <list type="number">
    /// <item><b>Allmänna noter först</b> (utan konto — redovisningsprinciper, händelser efter
    /// årets slut), i den ordning de skrevs.</item>
    /// <item><b>Därefter kontonoterna</b>, i den ordning deras FÖRSTA konto står i räkningarna:
    /// resultaträkningen uppifrån, sedan balansräkningen. Samma ordning som i en årsredovisning,
    /// där noterna numreras efter första hänvisningen.</item>
    /// <item><b>Sist noter vars konton inte syns i årets räkningar</b> (saldot är noll). De numreras
    /// ändå — texten kan vara riktig — men flaggas, så att ingen skriver ut en not som ingenting
    /// hänvisar till utan att veta om det.</item>
    /// </list>
    /// </summary>
    public static class LedgerNoteNumbering
    {
        public sealed record Input(int Id, IReadOnlyCollection<int> AccountNumbers);

        public sealed class Numbered
        {
            public int Id { get; init; }
            public int Number { get; init; }
            public bool IsGeneral { get; init; }

            /// <summary>Konton noten pekar på som inte syns i årets räkningar.</summary>
            public List<int> MissingAccounts { get; init; } = new();

            /// <summary>Kontonot där INGET av kontona syns — ingenting hänvisar till den.</summary>
            public bool IsUnreferenced { get; init; }
        }

        /// <param name="notes">Noterna, i den ordning de skrevs (stigande id).</param>
        /// <param name="statementOrder">Kontona som de står i räkningarna, uppifrån.</param>
        public static List<Numbered> Number(IEnumerable<Input> notes, IReadOnlyList<int> statementOrder)
        {
            var position = new Dictionary<int, int>();
            for (var i = 0; i < statementOrder.Count; i++)
                position.TryAdd(statementOrder[i], i);

            var ordered = notes
                .Select(n =>
                {
                    var accounts = n.AccountNumbers.Distinct().ToList();
                    var present = accounts.Where(position.ContainsKey).ToList();
                    return new
                    {
                        n.Id,
                        General = accounts.Count == 0,
                        Missing = accounts.Where(a => !position.ContainsKey(a)).OrderBy(a => a).ToList(),
                        Unreferenced = accounts.Count > 0 && present.Count == 0,
                        First = present.Count == 0 ? int.MaxValue : present.Min(a => position[a])
                    };
                })
                .OrderBy(n => n.General ? 0 : n.Unreferenced ? 2 : 1)
                .ThenBy(n => n.General ? 0 : n.First)
                .ThenBy(n => n.Id)
                .ToList();

            return ordered.Select((n, i) => new Numbered
            {
                Id = n.Id,
                Number = i + 1,
                IsGeneral = n.General,
                MissingAccounts = n.Missing,
                IsUnreferenced = n.Unreferenced
            }).ToList();
        }

        /// <summary>Kontona i räkningarnas ordning: intäkter, kostnader, tillgångar, kapital och skulder.</summary>
        public static List<int> StatementOrder(LedgerFinancialStatements s)
            => s.Revenue.Concat(s.Costs).Concat(s.Assets).Concat(s.EquityAndLiabilities)
                .Select(r => r.AccountNumber).ToList();
    }

    /// <summary>Noterna för ett räkenskapsår, numrerade och klara att rita.</summary>
    public class LedgerNotesView
    {
        public int FiscalYearId { get; set; }
        public int Year { get; set; }

        /// <summary>Året är fastställt — noterna kan läsas men inte ändras.</summary>
        public bool Locked { get; set; }

        /// <summary>Föregående räkenskapsår har noter och det här har inga.</summary>
        public int? CopyFromYear { get; set; }
        public int CopyFromCount { get; set; }

        public List<Note> Notes { get; } = new();

        /// <summary>Kontonummer → notnumren som hänvisas till därifrån, stigande.</summary>
        public Dictionary<int, List<int>> AccountRefs { get; } = new();

        public class Note
        {
            public int Id { get; set; }
            public int Number { get; set; }
            public string Title { get; set; } = "";
            public string Body { get; set; } = "";
            public bool IsGeneral { get; set; }
            public bool IsUnreferenced { get; set; }
            public List<NoteAccount> Accounts { get; } = new();
            public List<int> MissingAccounts { get; set; } = new();
        }

        public class NoteAccount
        {
            public int Number { get; set; }
            public string Name { get; set; } = "";
        }
    }
}
