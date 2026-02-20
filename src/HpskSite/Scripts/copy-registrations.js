// ============================================================
// Copy registrations from first Hallandsserien competition to all others
// Run this in the browser console while logged in as admin on the admin page
// ============================================================

(async function() {
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    if (!token) { console.error('No anti-forgery token found. Are you on the admin page?'); return; }

    const headers = { 'Content-Type': 'application/json', 'RequestVerificationToken': token };

    // Step 1: Find competitions in the Hallandsserien 2025 series
    console.log('Step 1: Finding competitions in Hallandsserien 2025...');
    const compResp = await fetch('/umbraco/surface/CompetitionAdmin/GetCompetitionsList?year=2025&includeCompleted=true', { headers, credentials: 'include' });
    const compData = await compResp.json();
    if (!compData.success) { console.error('Failed to load competitions:', compData.message); return; }

    const seriesComps = compData.data.filter(c => c.seriesName && c.seriesName.includes('Hallandsserien'));
    if (seriesComps.length === 0) { console.error('No competitions found in Hallandsserien series'); return; }

    // Sort by date to find the first one
    seriesComps.sort((a, b) => new Date(a.startDate) - new Date(b.startDate));
    const firstComp = seriesComps[0];
    const otherComps = seriesComps.slice(1);

    console.log(`Found ${seriesComps.length} competitions in series:`);
    seriesComps.forEach((c, i) => console.log(`  ${i === 0 ? '>>> SOURCE:' : '    Target: '} [${c.id}] ${c.name} (${c.startDate?.substring(0, 10) || 'no date'})`));

    if (otherComps.length === 0) { console.error('Only one competition found, nothing to copy to'); return; }

    // Step 2: Get registrations from first competition
    console.log(`\nStep 2: Loading registrations from "${firstComp.name}" (ID: ${firstComp.id})...`);
    const regResp = await fetch(`/umbraco/surface/RegistrationAdmin/GetCompetitionRegistrations?competitionId=${firstComp.id}`, { headers, credentials: 'include' });
    const regData = await regResp.json();
    if (!regData.success) { console.error('Failed to load registrations:', regData.message); return; }

    const registrations = regData.registrations;
    console.log(`Found ${registrations.length} registrations to copy:`);
    registrations.forEach(r => console.log(`  - ${r.memberName} (ID: ${r.memberId}, Class: ${r.shootingClass})`));

    // Step 3: Copy registrations to each other competition
    const totalOps = registrations.length * otherComps.length;
    console.log(`\nStep 3: Creating ${totalOps} registrations (${registrations.length} shooters x ${otherComps.length} competitions)...`);

    let created = 0, skipped = 0, failed = 0;

    for (const comp of otherComps) {
        console.log(`\n  Competition: ${comp.name} (ID: ${comp.id})`);
        for (const reg of registrations) {
            const shootingClass = reg.shootingClass || 'C1';
            try {
                const resp = await fetch('/umbraco/surface/RegistrationAdmin/AddLateRegistration', {
                    method: 'POST',
                    headers,
                    credentials: 'include',
                    body: JSON.stringify({
                        competitionId: comp.id,
                        memberId: reg.memberId,
                        shootingClass: shootingClass,
                        startPreference: reg.startPreference || 'Inget',
                        notes: 'Bulk-kopierad från ' + firstComp.name
                    })
                });
                const result = await resp.json();
                if (result.success) {
                    created++;
                    console.log(`    ✓ ${reg.memberName} (${shootingClass})`);
                } else if (result.message && result.message.includes('already registered')) {
                    skipped++;
                    console.log(`    - ${reg.memberName} (redan registrerad)`);
                } else {
                    failed++;
                    console.warn(`    ✗ ${reg.memberName}: ${result.message}`);
                }
            } catch (err) {
                failed++;
                console.error(`    ✗ ${reg.memberName}: ${err.message}`);
            }
        }
    }

    console.log(`\n=== Done! Created: ${created}, Skipped: ${skipped}, Failed: ${failed} ===`);
})();
