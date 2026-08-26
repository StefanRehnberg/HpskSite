namespace HpskSite.Models
{
    /// <summary>
    /// Normaliserar ett inkommande <c>shootingClassIds</c>-värde till den lagringsform CLAUDE.md
    /// kräver: en **JSON-array-sträng** (<c>["C1","C2"]</c>), aldrig CSV.
    ///
    /// THE one place den konverteringen bor. Den låg i **fyra** kopior — tre i
    /// <c>CompetitionAdminController</c> (skapa, kopiera, annons) och en i
    /// <c>PrecisionCompetitionEditService</c> — och de tre första delade en lucka:
    ///
    /// ⚠️ De testade <c>value is string</c> och <c>JsonElement</c> med
    /// <c>ValueKind == Array</c>. Men <c>fields</c> deserialiseras till
    /// <c>Dictionary&lt;string, object&gt;</c>, så System.Text.Json ger **JsonElement** och aldrig
    /// <c>string</c> — och tävlingsguiden skickar klasserna som en CSV-**sträng**
    /// (<c>wizard_shootingClassIds.value = selected.join(',')</c>). Det värdet blir alltså ett
    /// JsonElement med <c>ValueKind == String</c>, vilket matchade INGEN gren: det råa elementet
    /// lagrades, och dess <c>ToString()</c> är CSV:n. Varje tävling skapad via guiden fick därför
    /// <c>C1,C2,C3</c> i stället för JSON, tysta bara därför att varje LÄSARE har en CSV-fallback.
    /// (Redigeringsvägen var korrekt hela tiden — den gör <c>value.ToString()</c> först.)
    ///
    /// Läsarna tolererar CSV, så inget var synligt trasigt; men konventionen finns för att
    /// klassnamn kan innehålla komma i framtiden, och en migreringsendpoint
    /// (<c>ConvertShootingClassIdsToJson</c>) finns just för att städa CSV-rader.
    /// </summary>
    public static class ShootingClassIdsValue
    {
        /// <summary>
        /// Tar värdet som det kom in (string, JsonElement av typen String eller Array, string[],
        /// eller något annat) och returnerar en JSON-array-sträng. Returnerar null för tomt, så
        /// anroparen kan hoppa över att skriva egenskapen alls.
        /// </summary>
        public static string? Normalize(object? value)
        {
            if (value == null) return null;

            // ── JsonElement: den form fields-ordlistan faktiskt bär ─────────────────────────────
            if (value is System.Text.Json.JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Array:
                        var fromArray = el.EnumerateArray()
                            .Select(e => e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : e.ToString())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s!.Trim())
                            .ToArray();
                        return fromArray.Length == 0 ? null : System.Text.Json.JsonSerializer.Serialize(fromArray);

                    case System.Text.Json.JsonValueKind.String:
                        // Den grenen som saknades. Kan vara CSV ELLER en redan JSON-kodad array
                        // som skickats som sträng — FromText hanterar båda.
                        return FromText(el.GetString());

                    case System.Text.Json.JsonValueKind.Null:
                    case System.Text.Json.JsonValueKind.Undefined:
                        return null;

                    default:
                        return FromText(el.ToString());
                }
            }

            if (value is string[] arr)
            {
                var cleaned = arr.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
                return cleaned.Length == 0 ? null : System.Text.Json.JsonSerializer.Serialize(cleaned);
            }

            if (value is IEnumerable<string> seq)
            {
                var cleaned = seq.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
                return cleaned.Length == 0 ? null : System.Text.Json.JsonSerializer.Serialize(cleaned);
            }

            return FromText(value.ToString());
        }

        /// <summary>
        /// CSV eller redan-JSON → JSON-array-sträng. En redan giltig JSON-array skickas igenom
        /// oförändrad, vilket är hela skälet att kontrollen är "börjar med [" och inte en parse:
        /// ett dubbelkodat värde ska inte uppstå av att någon sparar en gång till.
        /// </summary>
        public static string? FromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var trimmed = text.Trim();
            if (trimmed.StartsWith("[")) return trimmed;

            var classIds = trimmed.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

            return classIds.Length == 0 ? null : System.Text.Json.JsonSerializer.Serialize(classIds);
        }
    }
}
