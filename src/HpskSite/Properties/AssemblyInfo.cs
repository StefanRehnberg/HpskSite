using System.Runtime.CompilerServices;

// Testprojektet får se internals.
//
// ⚠️ Finns för verifikationsliggarens skull. Bokföringens farligaste logik — vilket projekt en rad
// hamnar på, att momsraden ärver det, att en rättelse bär det ur originalet — ligger i metoder som
// tar färdiga uppslagstabeller och inte rör databasen. De är alltså prövbara som rena funktioner,
// på samma sätt som LedgerAmounts, men bara om testprojektet kommer åt dem.
//
// Alternativet vore att göra dem publika, vilket hade inbjudit anropare att gå förbi
// LedgerPostingService.Post — och Post är avsiktligt den ENDA vägen in i liggaren.
//
// ⚠️ <InternalsVisibleTo> som MSBuild-item fungerar INTE här: projektet kör
// GenerateAssemblyInfo=false, så attributet måste stå i kod.
[assembly: InternalsVisibleTo("HpskSite.Tests")]
