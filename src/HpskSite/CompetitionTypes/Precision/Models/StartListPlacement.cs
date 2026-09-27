namespace HpskSite.CompetitionTypes.Precision.Models
{
    /// <summary>
    /// Flytta en skytt till en bestämd skjutplats (bana), i samma eller ett annat skjutlag.
    /// Önskemål från Michael Henriksson (tävling 5574, 2026-09-26): när en bana går sönder mitt i
    /// tävlingen ska funktionären kunna flytta EN skytt, och i redigeraren kunna lägga en skytt
    /// direkt på en bana i stället för att klicka upp/ner.
    ///
    /// Är banan upptagen avgör funktionären hur, och systemet gissar aldrig:
    /// <list type="bullet">
    /// <item><b>Byt</b> — skytten på banan tar den flyttandes gamla plats.</item>
    /// <item><b>Flytta ner</b> — skyttarna från banan och framåt flyttar en bana, men bara fram
    /// till närmaste lucka. Resten av skjutlaget står kvar; en plats är en skjutplats och ingen
    /// ska byta tavla för någon annans skull i onödan.</item>
    /// </list>
    /// Trasiga banor hoppas alltid över och kan aldrig väljas.
    ///
    /// Ren klass utan databas — samma regel för dialogen, dra-och-släpp och testerna.
    /// </summary>
    public static class StartListPlacement
    {
        public const string ModeSwap = "swap";
        public const string ModeShift = "shift";

        public enum Outcome { Moved, NeedsChoice, NoChange, Refused }

        public sealed record Result(Outcome Outcome, string Message, StartListShooter? Occupant = null);

        public static Result MoveTo(
            StartListTeam sourceTeam, StartListShooter shooter, StartListTeam targetTeam,
            int lane, string? mode, int maxPerTeam, ISet<int>? brokenLanes)
        {
            brokenLanes ??= new HashSet<int>();
            sourceTeam.Shooters ??= new List<StartListShooter>();
            targetTeam.Shooters ??= new List<StartListShooter>();

            if (lane < 1)
                return new(Outcome.Refused, "Ange en bana från 1 och uppåt.");
            if (maxPerTeam > 0 && lane > maxPerTeam)
                return new(Outcome.Refused, $"Skjutlaget har {maxPerTeam} banor — bana {lane} finns inte.");
            if (brokenLanes.Contains(lane))
                return new(Outcome.Refused, $"Bana {lane} är ur funktion och kan inte väljas.");

            var sameTeam = ReferenceEquals(sourceTeam, targetTeam);
            var oldLane = shooter.Position;

            // ⚠️ En skytt kan inte stå på två banor i samma skjutlag — skjutlaget skjuter
            // samtidigt. I formatet med blandade vapengrupper har samma medlem flera starter i
            // olika skjutlag, och en flytt in i ett skjutlag där hen redan står hade gett två rader
            // för samma person — och gjort varje senare flytt tvetydig.
            if (!sameTeam)
            {
                var already = targetTeam.Shooters.FirstOrDefault(s => s.MemberId == shooter.MemberId);
                if (already != null)
                    return new(Outcome.Refused,
                        $"{shooter.Name} står redan i skjutlag {targetTeam.TeamNumber} (bana {already.Position}, {HpskSite.Models.ShootingClasses.DisplayName(already.WeaponClass)}) — en skytt kan inte stå på två banor i samma skjutlag.");
            }

            if (sameTeam && oldLane == lane)
                return new(Outcome.NoChange, $"{shooter.Name} står redan på bana {lane}.");

            var occupant = targetTeam.Shooters.FirstOrDefault(s => s.Position == lane && !ReferenceEquals(s, shooter));

            if (occupant == null)
            {
                Relocate(sourceTeam, shooter, targetTeam, lane);
                return new(Outcome.Moved, $"{shooter.Name} står nu på bana {lane} i skjutlag {targetTeam.TeamNumber}.");
            }

            if (string.IsNullOrWhiteSpace(mode))
                return new(Outcome.NeedsChoice,
                    $"Bana {lane} i skjutlag {targetTeam.TeamNumber} är upptagen av {occupant.Name}.", occupant);

            if (mode == ModeSwap)
            {
                if (sameTeam)
                {
                    occupant.Position = oldLane;
                    shooter.Position = lane;
                    targetTeam.SortByPosition();
                }
                else
                {
                    // Samma spärr åt andra hållet: skytten på banan får inte hamna i ett skjutlag
                    // där hen redan står.
                    var occupantAlready = sourceTeam.Shooters.FirstOrDefault(s =>
                        s.MemberId == occupant.MemberId && !ReferenceEquals(s, shooter));
                    if (occupantAlready != null)
                        return new(Outcome.Refused,
                            $"{occupant.Name} står redan i skjutlag {sourceTeam.TeamNumber} (bana {occupantAlready.Position}) — välj Flytta ner i stället.");

                    // Skytten på banan tar den flyttandes gamla plats i det andra skjutlaget.
                    targetTeam.Shooters.Remove(occupant);
                    Relocate(sourceTeam, shooter, targetTeam, lane);
                    occupant.Position = oldLane;
                    sourceTeam.Shooters.Add(occupant);
                    sourceTeam.SortByPosition();
                }
                return new(Outcome.Moved,
                    $"{shooter.Name} och {occupant.Name} har bytt plats.");
            }

            if (mode == ModeShift)
            {
                // Den flyttandes gamla plats räknas som ledig i samma skjutlag — den lämnas ju.
                var vacated = sameTeam ? oldLane : (int?)null;

                // Riktning: mot den lediga platsen om skytten flyttas inom skjutlaget (så att en
                // flytt från bana 10 till 1 skjuter 1–9 ett steg ner), annars nedåt i listan.
                var directions = sameTeam
                    ? new[] { oldLane > lane ? +1 : -1, oldLane > lane ? -1 : +1 }
                    : new[] { +1, -1 };

                List<StartListShooter>? chain = null;
                var dir = 0;
                foreach (var d in directions)
                {
                    chain = Chain(targetTeam, lane, d, maxPerTeam, brokenLanes, shooter, vacated);
                    if (chain != null) { dir = d; break; }
                }
                if (chain == null)
                    return new(Outcome.Refused,
                        $"Det finns ingen ledig bana i skjutlag {targetTeam.TeamNumber} att flytta skyttarna till. Välj Byt plats i stället.");

                // Flytta från kedjans slut, så ingen hamnar på en plats som ännu är upptagen.
                for (var i = chain.Count - 1; i >= 0; i--)
                    chain[i].Position = NextUsable(chain[i].Position, dir, brokenLanes);

                Relocate(sourceTeam, shooter, targetTeam, lane);
                var moved = chain.Count == 1 ? chain[0].Name : $"{chain.Count} skyttar";
                return new(Outcome.Moved,
                    $"{shooter.Name} står nu på bana {lane}; {moved} flyttades en bana.");
            }

            return new(Outcome.Refused, "Okänt val — välj Byt plats eller Flytta ner.");
        }

        /// <summary>
        /// Skyttarna som måste flytta ett steg i riktning <paramref name="dir"/> för att bana
        /// <paramref name="lane"/> ska bli ledig: från banan och framåt, fram till första lucka.
        /// Null när ingen lucka finns inom skjutlagets banor.
        /// </summary>
        private static List<StartListShooter>? Chain(
            StartListTeam team, int lane, int dir, int maxPerTeam, ISet<int> broken,
            StartListShooter moving, int? vacated)
        {
            var chain = new List<StartListShooter>();
            var p = lane;
            while (true)
            {
                var at = team.Shooters!.FirstOrDefault(s => s.Position == p && !ReferenceEquals(s, moving));
                if (at == null || p == vacated) return chain;          // lucka: kedjan tar slut
                chain.Add(at);
                p = NextUsable(p, dir, broken);
                if (p < 1 || (maxPerTeam > 0 && p > maxPerTeam)) return null;
            }
        }

        private static int NextUsable(int lane, int dir, ISet<int> broken)
        {
            var p = lane + dir;
            while (p >= 1 && broken.Contains(p)) p += dir;
            return p;
        }

        private static void Relocate(StartListTeam source, StartListShooter shooter, StartListTeam target, int lane)
        {
            if (!ReferenceEquals(source, target))
            {
                source.Shooters!.Remove(shooter);
                source.SortByPosition();
                target.Shooters!.Add(shooter);
            }
            shooter.Position = lane;
            target.SortByPosition();
        }
    }
}
