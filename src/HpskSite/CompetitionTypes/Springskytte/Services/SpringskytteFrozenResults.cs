using System.Text.Json.Nodes;

namespace HpskSite.CompetitionTypes.Springskytte.Services
{
    /// <summary>
    /// Utanför mästerskapet, F0b: den publika springskyttelistan serveras ur en frusen bild (hela live-svaret,
    /// alla vapengrupper, som skrevs när en admin senast räknade om eller publicerade). Bilden filtreras på de
    /// vapengrupper som är publicerade JUST NU, så "Gör preliminär" döljer en grupp direkt utan att något räknas om,
    /// och en grupp som publiceras visas ur bilden som den admin-handlingen skrev. Ren funktion, prövad av
    /// <c>SpringskytteFrozenResultsTests</c>.
    /// </summary>
    public static class SpringskytteFrozenResults
    {
        /// <summary>Det publika svaret ur bilden, eller null om bilden inte går att använda (då räknas listan live).</summary>
        public static string? Serve(string snapshotJson, IReadOnlyCollection<string> officialWeaponClasses)
        {
            if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
            JsonNode? root;
            try { root = JsonNode.Parse(snapshotJson); } catch { return null; }
            if (root is not JsonObject obj) return null;
            if (obj["classGroups"] is not JsonArray groups) return null;

            var official = new HashSet<string>(officialWeaponClasses, StringComparer.OrdinalIgnoreCase);
            var keep = new JsonArray();
            foreach (var g in groups.ToList())
            {
                var wc = (g as JsonObject)?["weaponClass"]?.GetValue<string>() ?? "";
                if (official.Contains(wc)) { groups.Remove(g); keep.Add(g); }
            }
            obj["classGroups"] = keep;
            obj["resultsOfficial"] = official.Count > 0;
            // Bilden byggs innan resultatraden hunnit skrivas (första publiceringen), så "resultsExist" kan ha varit falskt då — nu finns den.
            obj["resultsExist"] = true;
            var names = new JsonArray();
            foreach (var s in official.OrderBy(s => s)) names.Add(s);
            obj["officialWeaponClasses"] = names;
            return obj.ToJsonString();
        }
    }
}
