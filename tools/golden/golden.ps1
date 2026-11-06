<#
  golden.ps1 -- golden-master runner/comparer for the "Utanfor masterskapet" work (plan v3, S0).
  ASCII only on purpose (a BOM-less .ps1 with Swedish letters turns into mojibake).

  Needs: the app running with the profile "Umbraco.Web.UI (Rehearsal)" (database Umbraco_rehearsal,
  HPSK_VERIFY_ENDPOINTS=1) and, for fixtures, tools/golden/make-fixtures.sql run once.

  Usage:
    .\golden.ps1 -Mode Capture -Out C:\temp\golden\ref-<label>.json     # take a reference
    .\golden.ps1 -Mode Compare -Ref C:\temp\golden\ref-<label>.json     # run now, compare with reference
  Exit code 0 = identical, 1 = differences (or the endpoint wrote something), 2 = could not run.

  Row ids of cloned fixtures are noise: ids ending in ".Id" and CompetitionId are ignored when listing diffs,
  but the HASH compare is exact (hash of the canonical JSON as the endpoint produced it).
#>
param(
  [ValidateSet('Capture','Compare')] [string]$Mode = 'Compare',
  [string]$Ref,
  [string]$Out,
  [string]$Base = 'https://localhost:44380',
  [string]$Server = 'localhost\SQLEXPRESS',
  [string]$Database = 'Umbraco_rehearsal'
)
$ErrorActionPreference = 'Stop'

function Get-Ids {
  $q = "SET NOCOUNT ON; SELECT Id FROM dbo.Competition ORDER BY Id"
  $ids = sqlcmd -S $Server -E -C -d $Database -h -1 -W -Q $q
  if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed" }
  ($ids | Where-Object { $_ -match '^\d+$' }) -join ','
}

function Invoke-Golden([string]$file) {
  $ids = Get-Ids
  $url = "$Base/umbraco/surface/competitionresults/verifyrecompute?confirm=golden-master&full=true&ids=$ids"
  $code = curl.exe -sk -m 580 -o $file -w "%{http_code}" $url
  if ($code -ne '200') { Write-Error "endpoint answered HTTP $code (404 = guard refused, see the log for 'VerifyRecompute avvisad')"; exit 2 }
}

function Get-JsonDiff($a, $b, $path, $out) {
  if ($a -is [System.Management.Automation.PSCustomObject] -and $b -is [System.Management.Automation.PSCustomObject]) {
    foreach ($n in ($a.PSObject.Properties.Name + $b.PSObject.Properties.Name | Select-Object -Unique)) { Get-JsonDiff $a.$n $b.$n "$path.$n" $out }
  } elseif ($a -is [array] -and $b -is [array]) {
    if ($a.Count -ne $b.Count) { [void]$out.Add("$path : count $($a.Count) vs $($b.Count)") }
    for ($i = 0; $i -lt [Math]::Min($a.Count, $b.Count); $i++) { Get-JsonDiff $a[$i] $b[$i] "$path[$i]" $out }
  } else { if ("$a" -ne "$b") { [void]$out.Add("$path : '$a' vs '$b'") } }
}

if ($Mode -eq 'Capture') {
  if (-not $Out) { Write-Error "-Out is required"; exit 2 }
  New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
  Invoke-Golden $Out
  $j = Get-Content $Out -Raw -Encoding UTF8 | ConvertFrom-Json
  "captured $($j.header.competitions) competitions into $Out; wrote something: $($j.header.WROTE_SOMETHING)"
  if ($j.header.WROTE_SOMETHING) { exit 1 }
  exit 0
}

if (-not (Test-Path $Ref)) { Write-Error "reference not found: $Ref"; exit 2 }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("golden-now-" + [guid]::NewGuid().ToString('N') + ".json")
Invoke-Golden $tmp
# NB: PowerShell variable names are case-insensitive -- never name a local $ref/$out ($Ref/$Out are typed parameters).
$baseline = Get-Content $Ref -Raw -Encoding UTF8 | ConvertFrom-Json
$current = Get-Content $tmp -Raw -Encoding UTF8 | ConvertFrom-Json
$bad = 0
if ($current.header.WROTE_SOMETHING) { "FAIL: the endpoint reports that something was written"; $bad++ }
# A competition that THROWS in both runs hashes identically ({exception: type}) and would look "identical" -- an assertion that cannot fail.
# So any 'fel' (exception) in either run is a failure in itself, whatever the hashes say.
$throwing = @($current.competitions.PSObject.Properties | Where-Object { $_.Value.kind -eq 'fel' } | ForEach-Object { $_.Name })
$throwingRef = @($baseline.competitions.PSObject.Properties | Where-Object { $_.Value.kind -eq 'fel' } | ForEach-Object { $_.Name })
if ($throwing.Count -gt 0 -or $throwingRef.Count -gt 0) {
  "FAIL: competitions that threw an exception: now=[$($throwing -join ',')] reference=[$($throwingRef -join ',')]"; $bad++
}
$missingNow = @($current.competitions.PSObject.Properties | Where-Object { $_.Value.kind -eq 'saknas' }).Count
"competitions not found ('saknas', expected to be the same in both runs): $missingNow"
$allIds = @($baseline.competitions.PSObject.Properties.Name) + @($current.competitions.PSObject.Properties.Name) | Select-Object -Unique
$same = 0; $shown = 0
foreach ($id in $allIds) {
  $r = $baseline.competitions.$id; $n = $current.competitions.$id
  if ($null -eq $r -or $null -eq $n) { if ($shown++ -lt 25) { "DIFF $id : present in only one of the runs" }; $bad++; continue }
  if ($r.hash -eq $n.hash) { $same++; continue }
  $bad++
  $diffs = New-Object System.Collections.ArrayList
  Get-JsonDiff $r.json $n.json '' $diffs
  $real = @($diffs | Where-Object { $_ -notmatch '\.Id :|CompetitionId' })
  if ($shown++ -lt 25) {
    "DIFF $id ($($n.kind)): $($real.Count) differences"
    $real | Select-Object -First 5 | ForEach-Object { "    $_" }
  }
}
if ($shown -gt 25) { "... and $($shown - 25) more differing competitions not listed" }
"identical: $same of $($allIds.Count); different: $bad"
Remove-Item $tmp -ErrorAction SilentlyContinue
if ($bad -gt 0) { exit 1 } else { exit 0 }
