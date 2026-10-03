namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// "Nästa steg" på tävlingssidan: var står tävlingen, och vad ska göras härnäst? ENDA stället
    /// regeln bor. Ren funktion — controllern samlar ihop talen, den här avgör fasen.
    ///
    /// Byggd efter KM 3468 (2026-10-03), där skjutledaren med telefonen i handen inte hittade
    /// finalen. Kortet erbjuder EN knapp för det som ska göras, och varje knapp öppnar något som
    /// redan finns — regeln här äger ingen egen logik för startlistor, inmatning eller final.
    ///
    /// ⚠️ Med flera vapengrupper gäller fasen för den grupp som ligger LÄNGST BAK. Att visa den
    /// som ligger först hade lärt arrangören att resten är klart. Varje grupp får också en egen rad.
    /// </summary>
    public static class CompetitionNextStep
    {
        // Ordningen är bärande: lägst värde = längst bak i tävlingsdagen.
        public enum Phase
        {
            CreateStartList = 1,
            PublishStartList = 2,
            EnterQualification = 3,
            CreateFinals = 4,
            EnterFinals = 5,
            ShootOff = 6,
            PublishResults = 7,
            Done = 8
        }

        /// <summary>Kortets steglinje: fem steg, oavsett om tävlingen har final.</summary>
        public static int StepIndex(Phase p) => p switch
        {
            Phase.CreateStartList => 0,
            Phase.PublishStartList => 1,
            Phase.EnterQualification => 2,
            Phase.CreateFinals or Phase.EnterFinals => 3,
            _ => 4
        };

        public sealed class GroupInput
        {
            public string Group { get; set; } = "";
            public int Starts { get; set; }
            public int QualExpected { get; set; }
            public int QualEntered { get; set; }
            public bool HasFinalsList { get; set; }
            public int FinalsExpected { get; set; }
            public int FinalsEntered { get; set; }
        }

        public sealed class Input
        {
            public bool HasStartList { get; set; }
            public bool StartListPublished { get; set; }
            public int FinalSeries { get; set; }
            public List<GroupInput> Groups { get; set; } = new();
            public bool ResultsOfficial { get; set; }

            /// <summary>
            /// Finns en oavgjord medaljplats? Null = vet inte (inte uträknat, eller fel vid
            /// uträkningen). ⚠️ "Vet inte" ger ALDRIG Särskjutning — kortet går då på
            /// publiceringsflaggan, och resultatfliken visar särskjutningskortet ändå.
            /// </summary>
            public bool? UnresolvedTies { get; set; }
        }

        /// <summary>Fasen för EN vapengrupp, efter att startlistan är publicerad.</summary>
        public static Phase ForGroup(GroupInput g, int finalSeries)
        {
            if (g.QualEntered < g.QualExpected) return Phase.EnterQualification;
            if (finalSeries > 0)
            {
                if (!g.HasFinalsList) return Phase.CreateFinals;
                if (g.FinalsEntered < g.FinalsExpected) return Phase.EnterFinals;
            }
            return Phase.PublishResults;
        }

        public static Phase Resolve(Input input)
        {
            if (!input.HasStartList) return Phase.CreateStartList;
            if (!input.StartListPublished) return Phase.PublishStartList;

            var groups = input.Groups.Where(g => g.QualExpected > 0).ToList();
            // En publicerad lista utan en enda start går inte att mata in mot. Inmatningen är
            // ändå nästa steg — kortet säger då att skjutlagen är tomma.
            if (groups.Count == 0) return Phase.EnterQualification;

            var behind = groups.Select(g => ForGroup(g, input.FinalSeries)).Min();
            if (behind < Phase.PublishResults) return behind;

            // Särskjutningen före publiceringen: en oavgjord medaljplats är fel även på en lista
            // som redan publicerats, och prisutdelningen kan inte namnge medaljören.
            if (input.UnresolvedTies == true) return Phase.ShootOff;
            return input.ResultsOfficial ? Phase.Done : Phase.PublishResults;
        }
    }
}
