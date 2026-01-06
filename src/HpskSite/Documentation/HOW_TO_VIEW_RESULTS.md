# How to View Competition Results

## Quick Steps to See Results

### For Range Officers / Administrators

#### 1. Enter Results on Management Page
Go to: `https://yoursite.com/competitionmanagement?competitionId=2171`

#### 2. Use "Registrera Resultat" Section
- Enter shot results series by series
- Click "Skapa Resultatlista" to generate/update the results list
- Click "Markera som Officiell" when ready to publish final results

### For Spectators / Public

#### 1. Navigate to the Competition Page
Go to: `https://yoursite.com/competitions/2026/1a-maj-skjutningen/`

#### 2. Click the "Resultat" Tab
At the top of the competition page, you'll see several navigation cards. Click on the **"Resultat"** card.

#### 3. View Available Results
You'll see cards for each result page:
- **"Slutresultat"** - Final results (with green "Officiellt" badge)
- **"Live Resultat"** - Preliminary results (with yellow "Preliminärt" badge)

#### 4. Click "Visa Resultat" Button
Click the button on any result card to view the full results with:
- All shooters grouped by shooting class (C1, C2, B1, etc.)
- Individual series scores
- Total scores and X counts
- Current placement within each class

#### 5. Auto-Refresh for Preliminary Results
- Preliminary results automatically refresh every 15 seconds
- Official results are static and don't auto-refresh

## Understanding the Display

### Resultatlista Section
- **Collapsed by default**: Click the header to expand/collapse
- **Status Badge**: Shows if results are PRELIMINÄR (yellow) or OFFICIELL (green)
- **Summary**: Shows number of participants and classes
- **Filter**: Dropdown to filter by specific shooting class
- **Action Buttons**:
  - **Markera som Officiell**: Makes results final (green badge, no more changes)
  - **Återställ till Preliminär**: Reverts to draft status if corrections needed
  - **Skriv ut**: Print the results

### Results Table
For each shooting class, you'll see:
- **Plac.**: Placement/rank
- **Namn**: Shooter name
- **Klubb**: Club name
- **S1, S2, S3...**: Individual series scores (with X count in red)
- **Totalt**: Total score across all series
- **X**: Total number of X shots

## Troubleshooting

### "I don't see the Resultat tab"
- Make sure you're on the competition page itself, not the hub page
- URL should look like: `/competitions/YEAR/competition-name/`

### "The Resultatlista section doesn't appear"
- You need to click **"Skapa Resultatlista"** first
- Make sure results have been entered in the database
- Check browser console for errors (F12)

### "Results are empty or outdated"
- Click **"Skapa Resultatlista"** again to regenerate with latest data
- Verify that results have actually been entered by range officers
- Check the "Registrera Resultat" section to see if any results exist

### "I can't see the buttons to mark as official"
- The buttons are inside the collapsed **"Resultatlista"** section
- Click the section header to expand it
- If still not visible, you may need admin permissions

## For Different User Types

### Spectators/Public
- Navigate to competition page
- Click "Resultat" tab
- View the preliminary results as they update
- Results refresh automatically every 15 seconds when preliminary

### Range Officers
- Use the "Registrera Resultat" section to enter shot results
- Click "Skapa Resultatlista" periodically to update the public view
- Results save immediately to database

### Competition Administrators
- Enter results using "Registrera Resultat"
- Generate/update list with "Skapa Resultatlista"
- When competition complete, click "Markera som Officiell"
- Results become final and stop auto-updating

## Next Steps

Once results are official:
1. Share the competition URL with participants
2. Results can be printed using the "Skriv ut" button
3. Export functionality (coming soon) for archival
4. Results remain permanently accessible at the competition page

## Notes

- **Preliminary results**: Update in real-time during competition
- **Official results**: Final, no further changes, no auto-refresh
- **Multiple range officers**: Can work simultaneously without conflicts
- **Auto-refresh**: Only active for preliminary results (every 15 seconds)

## URL Structure

**Management (Admin Only)**:
- Competition management: `/competitionmanagement?competitionId=2171`
  - This is where you enter results and generate the results list

**Public (Everyone)**:
- Competition page: `/competitions/YEAR/competition-name/`
- Results tab: `/competitions/YEAR/competition-name/#results`
- Standalone results hub: `/competitions/YEAR/competition-name/resultat/`
- Individual result page: `/competitions/YEAR/competition-name/resultat/slutresultat/`

## Workflow Summary

1. **Admin enters results** at `/competitionmanagement?competitionId=2171`
2. **Admin generates list** by clicking "Skapa Resultatlista"
3. **Public views results** at `/competitions/YEAR/competition-name/#results`
4. **Auto-refresh** keeps preliminary results updated every 15 seconds
5. **Admin marks official** when competition complete
6. **Results are final** and accessible permanently

For the best experience during a live competition, keep the results page open on a display screen for all participants to view.

