using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Precision.Models
{
    // Configuration classes for JSON data
    public class StartListConfiguration
    {
        public StartListSettings? Settings { get; set; }
        public List<StartListTeam>? Teams { get; set; }
    }

    public class StartListSettings
    {
        public string Format { get; set; } = "";
        public int MaxShootersPerTeam { get; set; } = 30;
        public string StartInterval { get; set; } = "1:45";
        public string FirstStartTime { get; set; } = "09:00";
        public DateTime Generated { get; set; } = DateTime.Now;

        /// <summary>
        /// Vapengruppen listan gäller — sätts bara på FINALstartlistor, där det finns en
        /// lista per vapengrupp. Tom på varje kvalstartlista och på varje finallista
        /// genererad före 2026-09, där gruppen i stället härleds ur skyttarnas klasser.
        ///
        /// Ligger här och inte som en doctype-egenskap med flit: <c>SetValue</c> på en
        /// saknad egenskap är en TYST no-op, och en uppdelning som tyst tappar sin
        /// gruppmärkning slår ihop alla finaler igen. <c>configurationData</c> läses
        /// ändå av varje yta som rör listan, så den behöver inget operatörssteg.
        /// </summary>
        public string WeaponGroup { get; set; } = "";
    }

    public class StartListTeam
    {
        public int TeamNumber { get; set; }
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
        public string Label { get; set; } = "";

        /// <summary>
        /// Which calendar day this skjutlag shoots on, "yyyy-MM-dd". Optional — empty on every list
        /// generated before 2026-07 and on single-day competitions, where the competition's own date
        /// is the safe fallback.
        ///
        /// It exists because StartTime is a bare "HH:mm": without a date, a two-day competition's
        /// Sunday skjutlag cannot be ordered after Saturday's, which broke personal itineraries
        /// (/mitt-schema) and calendar export. The freeform <see cref="Label"/> is a HUMAN hint
        /// ("Lördag fm"), never a machine-readable day — don't parse it.
        ///
        /// DUAL-RENDERER WARNING: anything user-visible on a team must be wired in BOTH
        /// StartListHtmlRenderer.GenerateStartListHtml (the cached startListContent blob) AND
        /// Views/PrecisionStartList.cshtml (the public page, which reads this JSON via dynamic).
        /// </summary>
        public string Date { get; set; } = "";
        public List<string> WeaponClasses { get; set; } = new List<string>();
        public int ShooterCount { get; set; }
        public List<StartListShooter>? Shooters { get; set; }

        // Finals-only: display string for merged classes, e.g. "C + C Dam + C Jun".
        // Null/empty for regular qualifying teams.
        public string? ChampionshipClasses { get; set; }

        /// <summary>
        /// Lägger skytten på första lediga plats i skjutlaget — en lucka efter en skytt som
        /// flyttats eller tagits bort, annars sist.
        ///
        /// ⚠️⚠️ <see cref="StartListShooter.Position"/> ÄR EN SKJUTPLATS, inte ett radnummer.
        /// Redigeraren numrerade om hela skjutlaget 1..n efter varje borttagning och flytt, så att
        /// ta bort skytten på plats 3 flyttade alla efter hen till en annan tavla — mitt i en
        /// tävling, för någon annans skull (rapporterat från tävling 5574, 2026-09-26). Luckan
        /// står nu kvar, precis som när en anmälan raderas (StartListCleanup). Numrera ALDRIG om
        /// ett skjutlag här; en ny generering gör det, medvetet.
        ///
        /// "Antal + 1" går inte längre att använda som ny plats: med en lucka kan den platsen
        /// redan vara upptagen.
        /// </summary>
        public void PlaceInFirstFreePosition(StartListShooter shooter, int maxPerTeam, ISet<int>? brokenLanes = null)
        {
            Shooters ??= new List<StartListShooter>();
            var taken = Shooters.Select(s => s.Position).ToHashSet();

            var position = 0;
            var limit = maxPerTeam > 0 ? maxPerTeam : int.MaxValue;
            for (var p = 1; p <= limit; p++)
            {
                if (!taken.Contains(p) && brokenLanes?.Contains(p) != true) { position = p; break; }
            }
            // Fullt (eller överfullt) skjutlag: efter den högsta platsen, aldrig på en upptagen
            // och aldrig på en trasig bana.
            if (position == 0)
            {
                position = (taken.Count == 0 ? 0 : taken.Max()) + 1;
                while (brokenLanes?.Contains(position) == true) position++;
            }

            shooter.Position = position;
            Shooters.Add(shooter);
            SortByPosition();
        }

        /// <summary>
        /// Antal skjutplatser som går att använda: 1..max minus de trasiga banorna inom det
        /// intervallet. Det är skjutlagets verkliga kapacitet.
        /// </summary>
        public static int UsableLanes(int maxPerTeam, ISet<int>? brokenLanes) =>
            Math.Max(0, maxPerTeam - (brokenLanes?.Count(l => l >= 1 && l <= maxPerTeam) ?? 0));

        /// <summary>
        /// Efter en generering: skyttarna står på 1..n. Flytta dem till de banor som går att
        /// använda, i samma ordning — den n:te skytten till den n:te hela banan. Görs bara på en
        /// NYGENERERAD lista (där omnumrering ändå sker); en befintlig lista rörs aldrig av att en
        /// bana markeras trasig, då visas en varning i stället.
        /// </summary>
        public void MoveOffBrokenLanes(ISet<int>? brokenLanes)
        {
            if (Shooters == null || Shooters.Count == 0 || brokenLanes == null || brokenLanes.Count == 0) return;
            var lane = 0;
            foreach (var s in Shooters.OrderBy(s => s.Position).ToList())
            {
                do { lane++; } while (brokenLanes.Contains(lane));
                s.Position = lane;
            }
            SortByPosition();
        }

        /// <summary>
        /// Håller listan i platsordning. Renderarna och redigeraren går igenom listan i tur och
        /// ordning, så en skytt som lagts i en lucka måste också ligga där i listan.
        /// </summary>
        public void SortByPosition()
        {
            if (Shooters == null) return;
            Shooters = Shooters.OrderBy(s => s.Position).ToList();
            ShooterCount = Shooters.Count;
        }
    }

    public class StartListShooter
    {
        public int Position { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string WeaponClass { get; set; } = "";
        public int MemberId { get; set; }

        // Finals-only: leaderboard rank within the shooter's championship class (1 = top).
        // Null when this row is from a qualifying start list.
        public int? QualificationRank { get; set; }
        public int? QualificationScore { get; set; }
        public int? QualificationXCount { get; set; }
        // The shooter's original championship class even after merge ("C", "C Dam", "C Jun", ...).
        public string? ChampionshipClass { get; set; }
    }
}
