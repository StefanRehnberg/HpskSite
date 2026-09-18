using System.Text.Json;
using System.Text.RegularExpressions;

namespace HpskSite.Models
{
    /// <summary>
    /// En namngiven prisrad på ett evenemang: "Vuxen 180", "Barn 7–15 90", "Under 7 år 0".
    ///
    /// <para><b>⚠️⚠️ <see cref="Id"/> ÄR STABILT OCH ÅTERANVÄNDS ALDRIG.</b> Deltagarraden
    /// snapshottar id, etikett och belopp vid anmälan. Döper arrangören om "Barn 7–15" till
    /// "Barn 7–14" MÅSTE id:t stå still — annars tappar varje redan anmäld barnfamilj sin koppling
    /// till den rad de faktiskt valde. Generera id EN gång när raden skapas, aldrig när etiketten
    /// ändras.</para>
    /// </summary>
    public sealed record EventPrice(string Id, string Label, decimal Amount);

    /// <summary>
    /// Vad som lästes ur evenemangets prisegenskap.
    ///
    /// <para><b>⚠️⚠️ "INGEN AVGIFT" OCH "GICK INTE ATT LÄSA" ÄR OLIKA SVAR och får aldrig slås
    /// ihop.</b> En trasig JSON som tolkas som tom lista gör evenemanget GRATIS på skärmen — en
    /// lögn som ingen upptäcker förrän någon står vid kassan. <see cref="Unreadable"/> finns för att
    /// ytan ska kunna säga "avgiften kunde inte läsas" i stället för att hitta på ett pris.</para>
    /// </summary>
    public sealed class EventPriceList
    {
        public IReadOnlyList<EventPrice> Rows { get; init; } = Array.Empty<EventPrice>();

        /// <summary>Sant när egenskapen bar något som inte gick att tolka. Rows är då tom.</summary>
        public bool Unreadable { get; init; }

        /// <summary>Sant när evenemanget inte tar någon avgift alls — det normala fallet.</summary>
        public bool IsFree => !Unreadable && Rows.Count == 0;

        /// <summary>
        /// Ett enda pris, alltså den enkla formen. Ytan visar då en vanlig beloppsruta i stället för
        /// en radredigerare.
        /// </summary>
        public bool IsSingle => !Unreadable && Rows.Count == 1;

        public EventPrice? ById(string? id)
            => string.IsNullOrWhiteSpace(id) ? null : Rows.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>
    /// Prisraderna på ett evenemang — läsning, skrivning och validering.
    ///
    /// <para><b>Varför en LISTA och inte ett fält.</b> Ett evenemang kan vara vad som helst: en kurs,
    /// en fest, en bussresa, ett träningspass. Prod bar
    /// <i>"180 spänn per vuxen, 90 för barn och småttingar gratis."</i> i ett fritextfält — tre
    /// priser som inget enskilt tal kan uttrycka. Fasta fält (vuxen/junior) fungerar för tävlingar,
    /// där SHB ger klasserna, men här finns ingen sådan lista att luta sig mot. Arrangören namnger
    /// därför raderna själv.</para>
    ///
    /// <para><b>⚠️ EN representation, aldrig två.</b> Ett enda pris är en lista med EN rad. Det
    /// fanns en `eventFee`-kolumn bredvid under ett dygn; två fält som båda påstår vad något kostar
    /// blir förr eller senare oense, och då debiteras medlemmen efter det ena medan hen läste det
    /// andra. Samma fel som `feeAmount` och som `WorkItem.ActualCost`.</para>
    /// </summary>
    public static class EventPrices
    {
        /// <summary>Doctype-egenskapen på <c>clubSimpleEvent</c>. Textarea med JSON.</summary>
        public const string Property = "eventPrices";

        /// <summary>
        /// Rimlighetstak. En prislista med femtio rader är inte en prislista, det är ett formulär
        /// någon fyllt i fel — och den vore oläsbar i anmälningsväljaren.
        /// </summary>
        public const int MaxRows = 12;

        public const int MaxLabelLength = 60;

        /// <summary>Högsta belopp per rad. Samma rimlighetsgräns som fritextmigreringen använder.</summary>
        public const decimal MaxAmount = 5000m;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Läser prisegenskapen. Tom eller saknad egenskap = ingen avgift; oläsbart innehåll ger
        /// <see cref="EventPriceList.Unreadable"/> och ALDRIG en tom lista — se den klassens varning.
        /// </summary>
        public static EventPriceList Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new EventPriceList();

            List<Row>? rows;
            try
            {
                rows = JsonSerializer.Deserialize<List<Row>>(raw, Json);
            }
            catch (JsonException)
            {
                return new EventPriceList { Unreadable = true };
            }

            if (rows == null) return new EventPriceList { Unreadable = true };

            var result = new List<EventPrice>();
            foreach (var r in rows)
            {
                // ⚠️ En rad utan id eller etikett kan inte snapshottas meningsfullt på en anmälan.
                // Hellre "kunde inte läsas" än en halv prislista som ser komplett ut.
                if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Label))
                    return new EventPriceList { Unreadable = true };
                if (r.Amount < 0)
                    return new EventPriceList { Unreadable = true };

                result.Add(new EventPrice(r.Id.Trim(), r.Label.Trim(), r.Amount));
            }

            // Dubblerade id:n gör uppslaget tvetydigt — och uppslaget är hur en anmälan hittar
            // tillbaka till sin rad.
            if (result.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
                return new EventPriceList { Unreadable = true };

            return new EventPriceList { Rows = result };
        }

        public static string Serialize(IEnumerable<EventPrice> rows)
            => JsonSerializer.Serialize(
                rows.Select(r => new Row { Id = r.Id, Label = r.Label, Amount = r.Amount }).ToList(), Json);

        /// <summary>
        /// Validerar en uppsättning rader innan de sparas. Returnerar null när allt är i sin
        /// ordning, annars ett felmeddelande i klartext.
        ///
        /// <para>⚠️ Namnger ALLTID vad som är fel och på vilken rad. "Ogiltig prislista" skickar
        /// arrangören att gissa.</para>
        /// </summary>
        public static string? Validate(IReadOnlyList<EventPrice> rows)
        {
            if (rows.Count == 0) return null;                 // ingen avgift är giltigt
            if (rows.Count > MaxRows)
                return $"Högst {MaxRows} prisrader per evenemang. Ta bort några och försök igen.";

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Label))
                    return "Varje prisrad måste ha ett namn, t.ex. \"Vuxen\" eller \"Barn 7–15\".";
                if (r.Label.Length > MaxLabelLength)
                    return $"Namnet \"{r.Label[..20]}…\" är för långt (högst {MaxLabelLength} tecken).";
                if (r.Amount < 0)
                    return $"Priset för \"{r.Label}\" kan inte vara negativt.";
                if (r.Amount > MaxAmount)
                    return $"Priset för \"{r.Label}\" ({r.Amount:0} kr) är ovanligt högt för ett "
                         + "evenemang — kontrollera att det blev rätt.";
                if (string.IsNullOrWhiteSpace(r.Id))
                    return $"Prisraden \"{r.Label}\" saknar id. Ladda om sidan och försök igen.";
                if (!seen.Add(r.Id))
                    return $"Två prisrader delar id ({r.Id}). Ladda om sidan och försök igen.";
            }

            // ⚠️ Två rader med samma NAMN är inte ett datafel men en omöjlig valsituation: medlemmen
            // ser två likadana alternativ och kan inte veta vilket som är vilket.
            var dupLabel = rows.GroupBy(r => r.Label.Trim(), StringComparer.OrdinalIgnoreCase)
                               .FirstOrDefault(g => g.Count() > 1);
            if (dupLabel != null)
                return $"Det finns två prisrader som heter \"{dupLabel.Key}\". Ge dem olika namn.";

            return null;
        }

        /// <summary>
        /// Ett id ur en etikett: gemener, bindestreck, inga diakriter borttagna på ett sätt som slår
        /// ihop olika ord. Kollisioner får ett löpnummer.
        ///
        /// <para>⚠️ Anropas BARA när en rad skapas. Etiketten får ändras efteråt utan att id:t rör
        /// sig — se <see cref="EventPrice"/>.</para>
        /// </summary>
        public static string NewId(string? label, IEnumerable<string> existing)
        {
            var baseId = Regex.Replace((label ?? "").ToLowerInvariant().Trim(), @"[^a-z0-9åäö]+", "-")
                              .Trim('-');
            if (string.IsNullOrEmpty(baseId)) baseId = "pris";
            if (baseId.Length > 24) baseId = baseId[..24].Trim('-');

            var taken = new HashSet<string>(existing, StringComparer.Ordinal);
            if (!taken.Contains(baseId)) return baseId;

            for (var n = 2; n < 100; n++)
            {
                var candidate = $"{baseId}-{n}";
                if (!taken.Contains(candidate)) return candidate;
            }
            return $"{baseId}-{Guid.NewGuid().ToString("N")[..6]}";
        }

        /// <summary>JSON-formen. Egen typ så serialiseringen inte hänger på record-konstruktorn.</summary>
        private sealed class Row
        {
            public string? Id { get; set; }
            public string? Label { get; set; }
            public decimal Amount { get; set; }
        }
    }
}
