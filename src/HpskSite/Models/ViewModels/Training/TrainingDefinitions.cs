namespace HpskSite.Models.ViewModels.Training
{
    /// <summary>
    /// Static definitions for all Skyttetrappan training levels and steps
    /// Based on "Skyttetrappa v2.pdf"
    /// </summary>
    public static class TrainingDefinitions
    {
        private static readonly List<TrainingLevel> _levels = new List<TrainingLevel>();

        static TrainingDefinitions()
        {
            InitializeLevels();
        }

        /// <summary>
        /// Get all training levels
        /// </summary>
        public static List<TrainingLevel> GetAllLevels()
        {
            return _levels.ToList();
        }

        /// <summary>
        /// Get a specific training level by ID
        /// </summary>
        public static TrainingLevel? GetLevel(int levelId)
        {
            return _levels.FirstOrDefault(l => l.LevelId == levelId);
        }

        /// <summary>
        /// Get a specific training step
        /// </summary>
        public static TrainingStep? GetStep(int levelId, int stepNumber)
        {
            var level = GetLevel(levelId);
            return level?.Steps.FirstOrDefault(s => s.StepNumber == stepNumber);
        }

        /// <summary>
        /// Get total number of steps across all levels
        /// </summary>
        public static int GetTotalStepCount()
        {
            return _levels.Sum(l => l.Steps.Count);
        }

        /// <summary>
        /// Initialize all training levels and steps
        /// </summary>
        private static void InitializeLevels()
        {
            _levels.Clear();

            // Level 1: Nybörjartrappa Brons (3 × 34 p)
            _levels.Add(new TrainingLevel
            {
                LevelId = 1,
                Name = "Nybörjartrappa Brons",
                Description = "Mål: 3 × 34 p",
                Badge = "🥉",
                Goal = "Skjuta minst tre serier på minst 34 poäng",
                Purpose = "Lära sig grunderna och etablera träffbild i det svarta",
                Order = 1,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut en serie med minst 30 poäng." },
                    new TrainingStep { StepNumber = 2, Description = "Skjut en serie där alla skott sitter i det svarta (≥7) eller minst 32 poäng." },
                    new TrainingStep { StepNumber = 3, Description = "Skjut två serier i följd med minst 32 poäng." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut en serie med minst 3 st 8:or eller högre." },
                    new TrainingStep { StepNumber = 5, Description = "Skjut en serie med minst 34 poäng." },
                    new TrainingStep { StepNumber = 6, Description = "Skjut totalt 3 serier på minst 34 poäng (märkeskrav Brons uppfyllt)." }
                }
            });

            // Level 2: Nybörjartrappa Silver (3 × 40 p)
            _levels.Add(new TrainingLevel
            {
                LevelId = 2,
                Name = "Nybörjartrappa Silver",
                Description = "Mål: 3 × 40 p",
                Badge = "🥈",
                Goal = "Skjuta minst tre serier på minst 40 poäng",
                Purpose = "Utveckla jämnhet och förbättra precisionen",
                Order = 2,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut en serie med minst 36 poäng." },
                    new TrainingStep { StepNumber = 2, Description = "Skjut en serie med minst 4 st 8:or eller högre eller minst 38 poäng." },
                    new TrainingStep { StepNumber = 3, Description = "Skjut två serier i följd med minst 38 poäng." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut en serie där alla skott sitter i det svarta (≥7) och minst ett skott är en 10:a." },
                    new TrainingStep { StepNumber = 5, Description = "Skjut en serie med minst 40 poäng." },
                    new TrainingStep { StepNumber = 6, Description = "Skjut totalt 3 serier på minst 40 poäng (märkeskrav Silver uppfyllt)." }
                }
            });

            // Level 3: Nybörjartrappa Guld (3 × 46 p)
            _levels.Add(new TrainingLevel
            {
                LevelId = 3,
                Name = "Nybörjartrappa Guld",
                Description = "Mål: 3 × 46 p",
                Badge = "🥇",
                Goal = "Skjuta minst tre serier på minst 46 poäng",
                Purpose = "Sikta mot hög precision och fler 9–10-träffar",
                Order = 3,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut en serie med minst 42 poäng." },
                    new TrainingStep { StepNumber = 2, Description = "Skjut en serie med minst 44 poäng inklusive minst 3 st 9:or." },
                    new TrainingStep { StepNumber = 3, Description = "Skjut två serier i följd med minst 44 poäng eller en på 46 poäng." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut en serie med minst 4 st 9:or eller högre eller en på 46 poäng." },
                    new TrainingStep { StepNumber = 5, Description = "Skjut en serie med minst 46 poäng." },
                    new TrainingStep { StepNumber = 6, Description = "Skjut totalt 3 serier på minst 46 poäng (märkeskrav Guld uppfyllt)." }
                }
            });

            // Level 4: Guldmärkesskytt 1 (regelb. skjuta 46 p)
            _levels.Add(new TrainingLevel
            {
                LevelId = 4,
                Name = "Guldmärkesskytt 1",
                Description = "Regelb. skjuta 46 p",
                Badge = "🥇",
                Goal = "Lyckas skjuta ett stabilt snitt på minst 46 poäng",
                Purpose = "Utveckla jämnhet över flera serier, bygga grund inför högre nivåer",
                Order = 4,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut tre serier med minst 44 poäng vardera." },
                    new TrainingStep { StepNumber = 2, Description = "Skjut tre serier med minst 45 poäng vardera." },
                    new TrainingStep { StepNumber = 3, Description = "Skjut tre serier med minst 45 poäng med minst en 46:a." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut fyra serier med snitt på minst 45 poäng med minst två 46:or." },
                    new TrainingStep { StepNumber = 5, Description = "Skjut tre serier på minst 46 poäng." },
                    new TrainingStep { StepNumber = 6, Description = "Skjut tre serier med minst 46 poäng med minst 6 st 10:or." },
                    new TrainingStep { StepNumber = 7, Description = "Skjut en serie med minst 48 poäng." },
                    new TrainingStep { StepNumber = 8, Description = "Skjut en 6-skotts serie på minst 276 poäng (snitt 46)." },
                    new TrainingStep { StepNumber = 9, Description = "Skjut en tävling om 6 serier på minst 270 poäng.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 10, Description = "Skjut fyra tävlingsserier på minst 46 poäng.", IsCompetitionRequired = true }
                }
            });

            // Level 5: Guldmärkesskytt 2 (mål: skjuta 50 p)
            _levels.Add(new TrainingLevel
            {
                LevelId = 5,
                Name = "Guldmärkesskytt 2",
                Description = "Mål: skjuta 50 p",
                Badge = "🥇",
                Goal = "Lyckas med en perfekt serie (50 p)",
                Purpose = "Bygga självförtroende och precision i toppnivå",
                Order = 5,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut tre serier med minst 47 poäng (minst två med 3 st 10:or vardera)." },
                    new TrainingStep { StepNumber = 2, Description = "Skjut fem serier i rad med minst 46 poäng." },
                    new TrainingStep { StepNumber = 3, Description = "Skjut en serie med minst 48 poäng med minst 3 st 10:or." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut sex serier på minst 278 poäng (snitt ≥46,3)." },
                    new TrainingStep { StepNumber = 5, Description = "Skjut en serie med minst 49 poäng." },
                    new TrainingStep { StepNumber = 6, Description = "Skjut tre serier under samma pass med snitt ≥48 poäng." },
                    new TrainingStep { StepNumber = 7, Description = "Skjut 6 serier på minst 278 poäng (snitt 46,3)." },
                    new TrainingStep { StepNumber = 8, Description = "Skjut tio serier totalt med snitt på minst 48 poäng." },
                    new TrainingStep { StepNumber = 9, Description = "Skjut en tävling om 6 serier på minst 276 poäng (snitt 46).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 10, Description = "Skjut en serie med 50 poäng." }
                }
            });

            // Level 6: Guldmärkesskytt 3 (Guldmästare)
            _levels.Add(new TrainingLevel
            {
                LevelId = 6,
                Name = "Guldmärkesskytt 3 (Guldmästare)",
                Description = "Guldmästare",
                Badge = "🥇",
                Goal = "Stabilisera skyttet på hög nivå och bygga uthållighet inför elitnivå",
                Purpose = "Förbättra snitt, precision och långserieresultat",
                Order = 6,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut tre serier i följd på minst 47 poäng." },
                    new TrainingStep { StepNumber = 2, Description = "Skjut tre serier under samma pass med snitt på minst 47 poäng." },
                    new TrainingStep { StepNumber = 3, Description = "Skjut två serier på minst 49 poäng." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut en tävling där 3 av serierna snittar minst 48 poäng.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 5, Description = "Skjut en 6-seriematch på minst 284 poäng (snitt ≥47,3)." },
                    new TrainingStep { StepNumber = 6, Description = "Skjut tio serier totalt med snitt på minst 48 poäng (ingen under 46)." },
                    new TrainingStep { StepNumber = 7, Description = "Skjut en tävling om 6 serier minst 279 poäng (snitt ≥46,5).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 8, Description = "Skjut två 6-seriematcher på minst 280 poäng vardera, under samma vecka." },
                    new TrainingStep { StepNumber = 9, Description = "Skjut en 50 poängare med minst 1 st X." },
                    new TrainingStep { StepNumber = 10, Description = "Skjut en tävling om 10 serier på minst 460 poäng.", IsCompetitionRequired = true }
                }
            });

            // Level 7: Elit-trappan
            _levels.Add(new TrainingLevel
            {
                LevelId = 7,
                Name = "Elit-trappan",
                Description = "Elite level",
                Badge = "🏆",
                Goal = "Att som skytt mer eller mindre regelbundet skjuta 50-poängsserier och samtidigt hålla en mycket hög nivå över hela matcher",
                Purpose = "Bygga förmågan att producera flera 50-poängsserier under samma träning och befästa elitnivå genom precision, X-träffar och höga matchresultat",
                Order = 7,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut två serier i följd på minst 49 poäng." },
                    new TrainingStep { StepNumber = 2, Description = "Skjut minst en serie på 50 poäng under tävling.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 3, Description = "Skjut två serier på 50 poäng." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut tre serier på minst 49 poäng." },
                    new TrainingStep { StepNumber = 5, Description = "Skjut tre serier på 50 poäng." },
                    new TrainingStep { StepNumber = 6, Description = "Skjut en serie på 50 poäng med minst 3 st X." },
                    new TrainingStep { StepNumber = 7, Description = "Skjut två serier på 50 poäng med minst 3 st X totalt under samma tävling.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 8, Description = "Skjut en 6-skotts serie på minst 288 poäng (snitt 48)." },
                    new TrainingStep { StepNumber = 9, Description = "Skjut en tävling om 10 serier på minst 470 poäng (snitt 47).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 10, Description = "Skjut en 6-skotts serie på minst 290 poäng (snitt 48,3)." }
                }
            });

            // Level 8: Mästartrappan (mål: 490 p)
            _levels.Add(new TrainingLevel
            {
                LevelId = 8,
                Name = "Mästartrappan",
                Description = "Mål: 490 p",
                Badge = "🏆",
                Goal = "Stabilisera skyttet på extrem elitnivå med målsättning 480–490 på 10-seriematcher",
                Purpose = "Bygga uthållighet och förmåga att prestera flera toppresultat under samma säsong",
                Order = 8,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut en tävling om 6 serier på minst 288 poäng (snitt 48,0).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 2, Description = "Skjut två tävlingar om 6 serier på minst 289 poäng (snitt 48,2).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 3, Description = "Tangera Svenskt Vä rekord: Skjut en 10-seriematch på minst 481 poäng." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut en tävling om 6 serier på minst 290 poäng (snitt 48,3).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 5, Description = "Skjut en 10-seriematch på minst 482 poäng." },
                    new TrainingStep { StepNumber = 6, Description = "Skjut två tävlingar om 6 serier på minst 290 poäng.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 7, Description = "Tangera Svenskt Vy rekord: Skjut en 10-seriematch på minst 484 poäng." },
                    new TrainingStep { StepNumber = 8, Description = "Skjut en tävling om 6 serier på minst 292 poäng (snitt 48,7).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 9, Description = "Skjut två 10-seriematcher på minst 485 poäng varav en under tävling.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 10, Description = "Skjut en 10-seriematch på minst 490 poäng." }
                }
            });

            // Level 9: Rekordtrappan (mål: Svenskt rekord ≥495 p)
            _levels.Add(new TrainingLevel
            {
                LevelId = 9,
                Name = "Rekordtrappan",
                Description = "Mål: Svenskt rekord ≥495 p",
                Badge = "🏅",
                Goal = "Förädla toppnivå och skapa förutsättningar för historiska prestationer",
                Purpose = "Stegen leder mot stabila 488–494 och slutligen rekordnivå",
                Order = 9,
                Steps = new List<TrainingStep>
                {
                    new TrainingStep { StepNumber = 1, Description = "Skjut en tävling om 6 serier på minst 293 poäng (snitt 48,8).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 2, Description = "Skjut tre tävlingar om 6 serier på minst 292 poäng under samma säsong.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 3, Description = "Skjut en 10-seriematch på minst 488 poäng." },
                    new TrainingStep { StepNumber = 4, Description = "Skjut en tävling om 6 serier på minst 294 poäng (snitt 49,0).", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 5, Description = "Tangera Svenskt Dam rekord: Skjut en 10-seriematch på minst 490 poäng under tävling.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 6, Description = "Skjut två tävlingar om 6 serier på minst 294 poäng under samma månad.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 7, Description = "Skjut en 10-seriematch på minst 492 poäng." },
                    new TrainingStep { StepNumber = 8, Description = "Skjut en tävling om 6 serier på minst 295 poäng.", IsCompetitionRequired = true },
                    new TrainingStep { StepNumber = 9, Description = "Tangera Svenskt rekord: Skjut en 10-seriematch på minst 494 poäng." },
                    new TrainingStep { StepNumber = 10, Description = "Slå Svenskt rekord: Skjut en 10-seriematch på minst 495 poäng på SM eller Landsdelsnivå.", IsCompetitionRequired = true }
                }
            });
        }
    }
}