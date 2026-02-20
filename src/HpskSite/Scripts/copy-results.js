// ============================================================
// Copy results from first Hallandsserien competition to all others
// with randomized score variation per competition.
// Run this in the browser console while logged in as admin on the admin page.
// ============================================================

(async function() {
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    if (!token) { console.error('No anti-forgery token found. Are you on the admin page?'); return; }

    const headers = { 'Content-Type': 'application/json', 'RequestVerificationToken': token };

    // --- Helpers ---

    // Parse shots JSON string -> array of strings
    function parseShots(shotsJson) {
        try { return JSON.parse(shotsJson); }
        catch { return []; }
    }

    // Convert a shot string to numeric value (X=10)
    function shotToNum(shot) {
        if (shot.toUpperCase() === 'X') return 10;
        const v = parseInt(shot, 10);
        return isNaN(v) ? 0 : v;
    }

    // Randomize a single shot value by delta, clamp 0-10, return as string
    // delta range: -3 to +1 per shot (slight tendency to score lower = more realistic)
    function randomizeShot(shot) {
        const val = shotToNum(shot);
        const delta = Math.floor(Math.random() * 5) - 3; // -3, -2, -1, 0, +1
        const newVal = Math.max(0, Math.min(10, val + delta));
        // If original was X and result is still 10, keep as X sometimes
        if (shot.toUpperCase() === 'X' && newVal === 10 && Math.random() < 0.4) return 'X';
        // If result is 10 and original wasn't X, small chance of X
        if (newVal === 10 && Math.random() < 0.15) return 'X';
        return String(newVal);
    }

    function randomizeShots(shots) {
        return shots.map(s => randomizeShot(s));
    }

    // --- Step 1: Find competitions ---
    console.log('Step 1: Finding competitions in Hallandsserien 2025...');
    const compResp = await fetch('/umbraco/surface/CompetitionAdmin/GetCompetitionsList?year=2025&includeCompleted=true', { headers, credentials: 'include' });
    const compData = await compResp.json();
    if (!compData.success) { console.error('Failed to load competitions:', compData.message); return; }

    const seriesComps = compData.data.filter(c => c.seriesName && c.seriesName.includes('Hallandsserien'));
    if (seriesComps.length === 0) { console.error('No competitions found in Hallandsserien series'); return; }

    seriesComps.sort((a, b) => new Date(a.startDate) - new Date(b.startDate));
    const firstComp = seriesComps[0];
    const otherComps = seriesComps.slice(1);

    console.log(`Found ${seriesComps.length} competitions in series:`);
    seriesComps.forEach((c, i) => console.log(`  ${i === 0 ? '>>> SOURCE:' : '    Target: '} [${c.id}] ${c.name} (${c.startDate?.substring(0, 10) || 'no date'})`));

    if (otherComps.length === 0) { console.error('Only one competition found, nothing to copy to'); return; }

    // --- Step 2: Get results from first competition ---
    console.log(`\nStep 2: Loading results from "${firstComp.name}" (ID: ${firstComp.id})...`);
    const resResp = await fetch(`/umbraco/surface/CompetitionResults/GetCompetitionResults?competitionId=${firstComp.id}`, { headers, credentials: 'include' });
    const resData = await resResp.json();
    if (!resData.Success && !resData.success) { console.error('Failed to load results:', resData.Message || resData.message); return; }

    const results = resData.Results || resData.results;
    if (!results || results.length === 0) { console.error('No results found for source competition'); return; }

    // Group results by MemberId to show summary
    const shooterMap = {};
    results.forEach(r => {
        if (!shooterMap[r.MemberId || r.memberId]) {
            shooterMap[r.MemberId || r.memberId] = { series: 0, class: r.ShootingClass || r.shootingClass };
        }
        shooterMap[r.MemberId || r.memberId].series++;
    });
    const shooterCount = Object.keys(shooterMap).length;
    console.log(`Found ${results.length} result rows (${shooterCount} shooters, ${results.length / shooterCount} series each)`);

    // --- Step 3: Copy results to each other competition ---
    const totalOps = results.length * otherComps.length;
    console.log(`\nStep 3: Saving ${totalOps} result rows across ${otherComps.length} competitions...`);

    let saved = 0, failed = 0;

    for (const comp of otherComps) {
        console.log(`\n  Competition: ${comp.name} (ID: ${comp.id})`);
        let compSaved = 0;

        for (const r of results) {
            const memberId = r.MemberId || r.memberId;
            const seriesNumber = r.SeriesNumber || r.seriesNumber;
            const teamNumber = r.TeamNumber || r.teamNumber || 1;
            const position = r.Position || r.position || 1;
            const shootingClass = r.ShootingClass || r.shootingClass || 'C1';
            const originalShots = parseShots(r.Shots || r.shots);

            if (originalShots.length === 0) {
                console.warn(`    ✗ MemberId ${memberId} series ${seriesNumber}: no shots data`);
                failed++;
                continue;
            }

            const newShots = randomizeShots(originalShots);

            try {
                const resp = await fetch('/umbraco/surface/CompetitionResults/SaveResult', {
                    method: 'POST',
                    headers,
                    credentials: 'include',
                    body: JSON.stringify({
                        competitionId: comp.id,
                        seriesNumber: seriesNumber,
                        teamNumber: teamNumber,
                        position: position,
                        shots: newShots,
                        rangeOfficerId: memberId, // use self as range officer for test data
                        shooterMemberId: memberId,
                        shooterClass: shootingClass
                    })
                });
                const result = await resp.json();
                if (result.Success || result.success) {
                    saved++;
                    compSaved++;
                } else {
                    failed++;
                    console.warn(`    ✗ MemberId ${memberId} series ${seriesNumber}: ${result.Message || result.message}`);
                }
            } catch (err) {
                failed++;
                console.error(`    ✗ MemberId ${memberId} series ${seriesNumber}: ${err.message}`);
            }
        }

        const originalTotal = results.reduce((sum, r) => {
            const shots = parseShots(r.Shots || r.shots);
            return sum + shots.reduce((s, shot) => s + shotToNum(shot), 0);
        }, 0);
        console.log(`    Saved ${compSaved}/${results.length} result rows for this competition`);
    }

    console.log(`\n=== Done! Saved: ${saved}, Failed: ${failed} ===`);
})();
