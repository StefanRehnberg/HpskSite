-- Fix: unique index must include ShootingClass for multi-class shooters
-- A member can have results in BOTH A1 and C1 for the same series number
-- Without ShootingClass in the index, entering results for one class
-- silently overwrites the other class's results.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-02-20

-- Drop the old index that only uses (CompetitionId, MemberId, SeriesNumber)
DROP INDEX [UX_PrecisionResultEntry_CompetitionMemberSeries] ON [PrecisionResultEntry];

-- Create new index including ShootingClass
CREATE UNIQUE INDEX [UX_PrecisionResultEntry_CompetitionMemberClassSeries]
ON [PrecisionResultEntry] ([CompetitionId], [MemberId], [ShootingClass], [SeriesNumber]);
