using System.Globalization;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models.Competitions;
using Newtonsoft.Json;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>EN regel för hur formulärens fältpåse blir en tävling</b> (C3-planen S5b). Guiden, redigeringen,
    /// annonsdialogerna och kopiorna skriver genom den; förut hade de tre olika regler, och annonsens
    /// sparning skrev den råa nyckeln som alias (så <c>allowDualCClass</c> gick aldrig att spara där).
    ///
    /// <para><b>Vägen för ett fält:</b> formulärnyckel → egenskapens alias (katalogen bär avvikelserna,
    /// <see cref="CompetitionFieldCatalog.PropertyAliasFor"/>) → fältet i <see cref="CompetitionFieldTable"/> →
    /// värdet i nodens form genom <see cref="ToNodeValue"/> (redigeringens konvertering, som alltid varit den
    /// mest prövade) → <see cref="CompetitionFieldValues.Read"/> → postens egenskap.</para>
    ///
    /// <para><b>⚠️ Bara definitionen.</b> Ett tillståndsfält (patruller, officiella resultat, stationschefer,
    /// sammanslagning, medaljindelning, filerna) skrivs aldrig ur ett formulär — de har egna vägar. En nyckel
    /// som inte är ett definitionsfält hamnar i <see cref="BagResult.Ignored"/>, så anroparen kan säga ifrån.</para>
    /// </summary>
    public static class CompetitionFieldBag
    {
        /// <summary>Belopp — lagras som text i nodens form, men tolkas som decimaltal först (negativt = inget).</summary>
        private static readonly HashSet<string> AmountFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "registrationFee", "teamRegistrationFee", "stafettRegistrationFee",
            "juniorRegistrationFee", "subCompetitionFee"
        };

        public sealed class BagResult
        {
            /// <summary>Nycklar som inte är något definitionsfält (och inte bad om att hoppas över).</summary>
            public List<string> Ignored { get; } = new();
        }

        /// <summary>
        /// Egenskapens alias för en formulärnyckel, om nyckeln är ett definitionsfält. Null annars.
        /// </summary>
        public static CompetitionFieldDef<CompetitionInfo>? FieldFor(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var alias = CompetitionFieldCatalog.PropertyAliasFor(key);
            var f = CompetitionFieldTable.ForAlias(alias);
            return f is not null && f.Kind == CompetitionFieldKind.Definition ? f : null;
        }

        /// <summary>
        /// Lägger fältpåsens värden på posten. <paramref name="skip"/> = nycklar anroparen sköter själv
        /// (t.ex. <c>competitionType</c>, som byts genom den egna grenbytesvägen).
        /// </summary>
        public static BagResult Apply(CompetitionInfo target, IEnumerable<KeyValuePair<string, object?>> fields, ICollection<string>? skip = null)
        {
            var result = new BagResult();
            if (fields is null) return result;
            foreach (var (key, value) in fields)
            {
                if (skip is not null && skip.Contains(key)) continue;
                var f = FieldFor(key);
                if (f is null) { result.Ignored.Add(key); continue; }
                f.Set(target, CompetitionFieldValues.Read(f.Shape, ToNodeValue(key, value)));
            }
            return result;
        }

        /// <summary>Samma som <see cref="Apply(CompetitionInfo, IEnumerable{KeyValuePair{string, object?}}, ICollection{string}?)"/> för en <c>Dictionary&lt;string, object&gt;</c>.</summary>
        public static BagResult Apply(CompetitionInfo target, IDictionary<string, object>? fields, ICollection<string>? skip = null) =>
            Apply(target, fields?.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)) ?? Enumerable.Empty<KeyValuePair<string, object?>>(), skip);

        /// <summary>
        /// Värdet i nodens form för en formulärnyckel. Redigeringens konvertering, flyttad hit (förut
        /// <c>PrecisionCompetitionEditService.ConvertFieldValue</c>): tomt = null, belopp som decimaltal,
        /// antal som heltal (negativt = null), datum invariant, klasserna som JSON-array, tävlingsansvariga som
        /// JSON-array av heltal och nivån kanonisk. Fält utanför katalogen tolkas efter tabellens form.
        /// </summary>
        public static object? ToNodeValue(string key, object? value)
        {
            if (value == null) return null;
            var text = value.ToString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            if (key.Equals("shootingClassIds", StringComparison.OrdinalIgnoreCase))
                return HpskSite.Models.ShootingClassIdsValue.FromText(text);
            if (key.Equals("competitionManagers", StringComparison.OrdinalIgnoreCase))
                return ManagersJson(value);

            if (key.Equals(CompetitionLevel.PropertyAlias, StringComparison.OrdinalIgnoreCase))
            {
                var level = CompetitionLevel.Normalize(text);
                return level == "" ? null : level;
            }

            if (AmountFields.Contains(key))
                return decimal.TryParse(text, out var dec) && dec >= 0 ? dec : null;

            if (key.Equals("clubId", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(text, out var club) && club >= 0 ? club : null;

            var catalog = CompetitionFieldCatalog.Find(key);
            if (catalog != null)
            {
                switch (catalog.Control)
                {
                    case FieldControl.Checkbox:
                        return bool.TryParse(text, out var b) && b;
                    case FieldControl.Date:
                    case FieldControl.DateTime:
                        return Date(text);
                    case FieldControl.Number:
                        return int.TryParse(text, out var i) && i >= 0 ? i : null;
                }
                return text;
            }

            // Fält utanför katalogen (isActive, isExternal, externalUrl, direktplaceringConfig …):
            // tabellens form avgör.
            var f = CompetitionFieldTable.ForAlias(key);
            return f?.Shape switch
            {
                CompetitionFieldShape.Bool or CompetitionFieldShape.BoolDefaultTrue => CompetitionFieldValues.ReadBool(text) ?? false,
                CompetitionFieldShape.Int => int.TryParse(text, out var n) && n >= 0 ? n : null,
                CompetitionFieldShape.Date => Date(text),
                _ => text,
            };
        }

        /// <summary>"2025-01-20T14:30" och liknande, invariant. Utanför SQL Servers intervall = null.</summary>
        private static DateTime? Date(string text)
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                && d >= new DateTime(1753, 1, 1) && d <= new DateTime(9999, 12, 31))
                return d;
            return null;
        }

        /// <summary>Tävlingsansvariga som JSON-array av heltal. Oläsbart = tom array.</summary>
        private static string ManagersJson(object value)
        {
            if (value is int[] ints) return JsonConvert.SerializeObject(ints);
            var text = value.ToString() ?? "";
            if (text.TrimStart().StartsWith("["))
            {
                try { return JsonConvert.SerializeObject(JsonConvert.DeserializeObject<int[]>(text) ?? Array.Empty<int>()); }
                catch { return "[]"; }
            }
            var ids = text.Split(',').Select(s => s.Trim()).Where(s => int.TryParse(s, out _)).Select(int.Parse).ToArray();
            return JsonConvert.SerializeObject(ids);
        }
    }
}
