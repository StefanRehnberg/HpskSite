// Innehållet i kartan på /allt. Byggt 2026-09-27 ur en inventering av koden.
// ⚠️ Kartan är ett påstående om sajten: lägg bara in det som FINNS i koden, och ta bort det
// som tas bort. Varje sträng utan barn räknas som en funktion i sidans totalsiffra.
// [namn, beskrivning, barn?] eller bara "namn" för en detalj
window.OMFATTNING_DATA = ["pistol.nu","Pistolskyttarnas egen plattform. Skytten, klubben, kretsen, banan och tävlingarna på samma ställe, med samma inloggning och samma data.",[
 ["Tävlingar","Från första planeringsmötet till sista medaljen. Hela tävlingen, inte bara anmälningslistan.",[
  ["Discipliner","Varje gren har sina egna regler inbyggda, efter Skyttehandboken.",[
   ["Precision","",["Serier med X-räkning","Finalomgång","Handikappberäknare"]],
   ["Fältskytte","",["Stationer och patruller","Normal- och poängläge","Magnumfält","Rullande start","Självservice på stationen"]],
   ["Springskytte","",["Ringmål och fällmål","Ålders- och könsklasser","Stafett","Straff och tidsavdrag","Fattningskontroll"]],
   ["Nationell helmatch","",["Delmoment A, B och C","Särskiljning C före B"]],
   ["Milsnabb","",["Egen särskiljning","Egen startlistegenerator"]],
   ["Magnumprecision","",["Klasser M1–M9","Fasta medaljgränser"]],
   ["Sportpistol","",["Precisionshalva","Duellhalva"]],
   "Duell","Standardpistol",
   ["Klassregler","",["En klass per vapengrupp","Dubbel C-klass när det är tillåtet"]],
   ["Klassammanslagning","",["Förslag med skäl för varje klass","Egen motor för springskytte"]],
   ["Deltävlingar","Egen resultatlista, publicering och avgift."]]],
  ["Skapa tävlingen","",[
   "Tävlingsguide steg för steg",
   ["Arrangör","",["Klubb","Krets","Mästerskap","Endast för klubbens medlemmar"]],
   ["Avgifter","",["Individ","Junior","Lag","Stafett","Deltävling","Klubben betalar"]],
   ["Inställningar","",["Live-resultat","Lag och stafett","Standardmedaljgrundande","Notis vid publicering"]],
   "Skjutbana med karta","Kopiera till nästa år","Byt tävlingstyp","Extern tävling med inbjudan","Egen webbadress"]],
  ["Serier och säsonger","",[
   ["Poängstrategier","",["Summa","Bästa N","Antal segrar","Fasta poäng","Dynamiska poäng","Klubblag bästa X"]],
   "Serieresultat per klass","Seriebanner på tävlingen","Säsongssida","Kopiera serie med tävlingar"]],
  ["Planering och projektering","Det som händer veckorna före. Här finns det som ingen annan tjänst tar hand om.",[
   ["Förberedelser","",["Områden och uppgifter","Deadlines och försenade","Beroenden","Kommentarer och logg","Dokument och länkar","Påminn ansvariga"]],
   ["Mallar","",["Standard per gren och storlek","Egna mallar"]],
   "Budget och utfall per område",
   ["Materiel","",["Materiellista för fältskytte","Materielberäkning för övriga grenar"]],
   ["Dagsprogram","",["Programpunkter","Publikt program","Kontroll av krockar"]],
   "Planeringsblad för utskrift"]],
  ["Bemanning","Rätt funktionär på rätt plats, utan kalkylark och telefonkedjor.",[
   ["Bemanningsrutnät","",["Roll per dag","Förberedelse- och efterarbetsdagar","Behov mot bemannat","Luckor och dubbelbokning","Dela pass","Ersätt person"]],
   ["Roller","",["Rollkatalog","Egna rollnamn","Appbehörighet per roll","Stationschefer"]],
   ["Rekrytering","",["Hjälppass för självanmälan","Förfrågan till klubb eller krets","Push och mejl"]],
   ["Funktionärens sida","",["Tacka ja eller nej","Anmäl dig till pass","Ange tillgänglighet","Svara utan konto"]],
   ["Personmatchning","",["Koppla till medlem","Hitta dubbletter"]],
   "Utskrift och e-postlista"]],
  ["Anmälan","",[
   ["Individuell","",["En klass per vapengrupp","Önskemål om starttid","Tävlar för klubb","Anmäla en annan skytt"]],
   ["Egenbokning","",["Välj skjutlag själv","Fulla skjutlag visas"]],
   ["Lag","",["Skapa och gå med","Reserver","Inlånade skyttar"]],
   "Stafett","Avanmälan och byte","Stängs när startlistan publiceras"]],
  ["Avgifter och betalning","",["Swish-QR","Jag har betalat","Kvitto för friskvårdsbidrag","Samlingsfaktura per klubb","Kreditfaktura och makulering","Påminnelser","Bokförs per tävling"]],
  ["Startlistor","",[
   ["Precision","",["Generera och publicera","Dra och släpp","Välj skjutplats","Banor ur funktion","Skjutlag över flera dagar","Mixade vapengrupper"]],
   ["Finaler","",["En per vapengrupp","Frys kvalresultatet","Cut 1/6","Placera om efter resultat"]],
   ["Springskytte","",["Flera startlistor","Pass och pauser","Startnummerserier","Numrera om med historik","Stafettstartlista"]],
   ["Fältskyttets patruller","",["Generera per vapengrupp","Flytta skyttar","Numrera om","Walk-in till nästa patrull"]],
   "Direktplacering","Utskrift"]],
  ["Disken","Registreringsbordet på tävlingsdagen.",[
   ["Efteranmälan","",["Flera klasser på en gång","Lediga platser direkt","Lag och stafett"]],
   "Närvaro och incheckning","Redigera och överför anmälan","Kassan",
   ["Startlistetäckning","",["Oplacerade skyttar","Föräldralösa rader"]],
   "E-postlista till deltagarna","Export till CSV"]],
  ["Tävlingsledning live","",[
   ["Skjutledarvy","",["Röstkommandon","Skjuttider","Patron ur och visitation"]],
   ["Stationsplatta","",["Sifferpanel","Fältskyttestation","Springskyttets poäng och tid"]],
   ["Tidur","",["Figurtidslinjer","Röst","Håll skärmen vaken"]],
   ["Startlinjen","",["Storbildsklocka","Nedräkning med signaler","Startledarläge"]],
   ["Fältskyttets startledare","",["Skicka iväg patrull","Parkera och håll"]],
   ["Patrullista på väggen","",["Näst på tur","Avprickning"]],
   ["Stationsöversikt","",["Stationschef","Andel klart"]],
   ["QR-koder","",["Förutsättningar","Resultatregistrering"]],
   ["Funktionärsbelastning","",["Takt och status","Behöver hjälp"]],
   ["Meddelanden","",["Till station, klass, skjutlag eller roll","Brådska och säkerhet","Kvittens"]]]],
  ["Resultattavla","Live för publiken.",["Rotation mellan vapengrupper","Upp till fyra rutor","LIVE eller OFFICIELLT","Nyss startat i springskytte","QR till resultaten"]],
  ["Resultat","",[
   ["Inmatning","",["Serie för serie","Tangentbord och X","Per station"]],
   ["Publicering","",["Preliminär och officiell","Per vapengrupp"]],
   "Klassammanslagning i resultatet","Lagresultat",
   ["Resultatlista","",["Per klass eller vapengrupp","Utskrift och PDF"]],
   ["Distribuerade tävlingar","Skyttarna rapporterar från hemmabanan."],
   ["DNS och DNF","",["Startade inte","Bröt","Skäl, t.ex. vapenfel"]]]],
  ["Särskjutning","",["Bara mästerskap och finalister","Rundor tills avgjort","Fältskyttets särskjutningsstation","Fotnot i resultatlistan"]],
  ["Medaljer och rekord","",[
   ["Standardmedaljer","",["Per gren","Bevis och verifiering","Guldansökan"]],
   ["Mästerskapsmedaljer","",["Per mästerskapskategori","Valbar medaljindelning","Reduktion vid få deltagare"]],
   "Rekord med historik","Mästartitlar","Beställningslista med gravyr"]],
  ["Prisutdelning","",["Bord per vapengrupp","Guld eller brons först","Hederspriser","Lagmedaljer med namn","Utskrift"]],
  ["Fältskyttets banor","Bana, figurer och avstånd konstrueras i pistol.nu.",[
   ["Konfigurator","",["Enkelt och avancerat läge","Per vapenklass","Importera och länka stationer"]],
   ["SHB-regler","",["Föreslagen skjuttid","Svårighetsgrad","Mörkertillägg","Avståndstak"]],
   ["Figurkatalog","",["Varianter och bilder","Storleksgrupper"]],
   ["Sekretess","",["Synlighet","Hemlig till ett datum","Samarbetspartner"]],
   "Banläggarens godkännande","Projekt","Stationskort med QR",
   ["Flödesstatistik","",["Flaskhalsar","Genomströmning"]]]],
  ["Deltagarkommunikation","",[
   "Meddela deltagare","Notiser på tävlingssidan","Push vid publicering",
   ["Mitt schema","",["Tidslinje","Krockvarning","Kalenderexport","Påminnelse 30 min före"]]]],
  ["Mästerskapssidor","Egen webbplats för ett mästerskap.",["PM","Banor och kartor","Service och boende","Startlistor och resultat"]]
 ]],
 ["Skjutbanor","Banans hela liv: tillstånd, dokument, vallar, tider, incheckning och miljörapportering.",[
  ["Skjutbanedatabasen","",[
   ["Karta och lista","",["Sök","Filter","Klustrade markörer"]],
   "Lägg till bana på kartan","Import från OpenStreetMap","Gör anspråk på en bana","Förvaltare",
   ["Synlighet","Begränsad synlighet döljer koordinaterna."]]],
  ["Banans uppgifter","",[
   ["Adress och position","",["Dragbar karta","Adress från position"]],
   "Huvudman","Skjutbanechef","Status","Beskrivning"]],
  ["Banor och vallar","",["Avstånd","Antal skjutplatser","Skjutriktning","Tillåtna vapen och kalibrar","Kulfång"]],
  ["Tillstånd","",["Polistillstånd","Miljöanmälan","Miljötillstånd","Myndighet och diarienummer","Giltighetstid","Max skott per år","Villkor","Tillåtna skjuttider"]],
  ["Dokument","Varningar i god tid innan något går ut.",[
   ["Dokumenttyper","",["Besiktningsprotokoll","Skjutbaneinstruktion","Miljöbeslut","Bullerutredning","Markundersökning (bly)","Skötselplan","Försäkring"]],
   "Utgångsvarning","Påminnelse på startsidan"]],
  ["Klubbar och tider","",["Klubbens roll på banan","Klubbens skjuttider","Kontroll mot tillståndets tider","Klubbsidans banflik"]],
  ["In- och utcheckning","",["QR-skylt","Checka in","Checka ut med antal skott","Automatisk utcheckning","Räknas som aktivitet"]],
  ["Miljörapportering","",["Skott mot tillståndets tak","Skjutdagar","Skott per timme (buller)","Manuell registrering","CSV-export","Utan personuppgifter"]],
  ["Kopplat till tävlingar","",["Banan på tävlingssidan","Vägbeskrivning","Karta över tävlingar"]]
 ]],
 ["Klubben","En komplett klubb: hemsida, verksamhet, intyg och märken.",[
  ["Klubbsidan","",[
   ["Hem","",["Kommande händelser","Nyheter","Utvalda tävlingar","Snabblänkar"]],
   "Om klubben","Kalender","Tävlingar","Rekord och mästare","Dokument","Instruktörer","Banor"]],
  ["Evenemang","",[
   ["Typer","",["Träning","Städdag","Möte","Socialt","Nyhet"]],
   "Kopiera evenemang",
   ["Anmälan","",["Max antal och reservlista","Sista anmälningsdag","Vem får anmäla sig","Obligatoriskt deltagande","Gäster och familj","Lånevapen"]],
   ["Pris och Swish","",["Prisrader","Betalning per sällskap"]],
   ["Deltagarlistan","",["Närvaro","Giltig frånvaro","Lägg till vid dörren"]],
   "Närvaro via QR-affisch","Dela och lägg i kalender"]],
  ["Aktivitet och föreningsintyg","",[
   ["Aktivitetssammanställning","",["Per medlem och år","Underlagets styrka","Filter per gren"]],
   ["Föreningsintyg","",["Ärendekö","Begär komplettering","Förifylld blankett","Ögonblicksbild per intyg","Ansvariga och påminnelser"]]]],
  ["Märken och medaljer","Enligt SHB kapitel 5.",[
   "Märkeskrav","Valideringskö med QR","Historiska serier","Guldnummer och årtal","Mästarmärket",
   ["Standardmedaljer","",["Godkänn bevis","Guldansökan"]],
   ["Att beställa och dela ut","",["Beställningslista","Utdelningslista"]]]],
  ["Träningsgrupper","",["Grupper och tränare","Välkomstmejl","Lånevapen till kursen"]],
  ["Kommunikation","",["Mejl till medlemmar","Export av e-postadresser"]],
  ["Inställningar","",["Kontaktuppgifter","Swish och bankgiro","Logotyp och banner","Organisationsnummer"]],
  ["Dokumentarkiv","",["Kategorier","Offentliga dokument","Lagringskvot"]],
  ["Statistik","",["Medlemmar och aktivitet","Skyttetrappans nivåer","Tävlingar per gren","Händelser per månad"]],
  "Personuppgiftsbiträdesavtal"
 ]],
 ["Medlemmar","Registret, medlemskapen och avgifterna.",[
  ["Medlemsregister","",["Sök och sortera","Personuppgifter","Personnummerkontroll","Flera klubbar","Medlemsgrupper","Profilbild"]],
  ["Medlemskap","",["Medlemstyp","Aktiv, vilande, utträdd","Medlem sedan","Belastningsregistret kontrollerat","Aktiv i förbundet","Klubbintern anteckning","Avsluta medlemskap"]],
  ["Familj och hushåll","",["Huvudmedlem","Övriga ingår i avgiften"]],
  ["Medlemsavgifter","En avgift per medlemstyp och år.",[
   ["Medlemstyper","",["Senior","Pensionär","Junior","Familj","Hedersmedlem","Ständig medlem","Stödjande medlem"]],
   "Belopp per typ och år","Medlemmen väljer typ själv","Avgiftslista med förslag","Utskick och påminnelse",
   ["Betalsida utan inloggning","",["Swish-QR","Bankgiro-QR","Jag har betalat"]],
   "Markera betald","Bokförs i liggaren","Läsläge för styrelse och revisor"]],
  ["Import","",["Excel eller CSV","Förifyllt för Svenska Lag","Testkörning","Dubblettskydd"]],
  ["Dubbletter","",["Förslag med poäng","Välj fält","Historiken följer med","Revisionslogg"]],
  ["Nycklar och koder","",["Nyckeltyp","Deposition","Utlämnad och återlämnad"]],
  ["Nya medlemmar","",["Godkännandekö","Inbjudan","Begäran om saknad klubb","Snabbgodkännande från mejl"]],
  ["Roller","",["Klubbadmin","Skjutledare","Föreningsintygsansvarig","Instruktörer","Välkomstmejl vid ny roll"]],
  ["Medlemskatalog","",["Dashboard per medlem","Rekord och medaljer"]]
 ]],
 ["Vapen","Klubbens vapen och lånevapen, från valvet till bokningen.",[
  ["Klubbens vapenregister","",["Vapengrupp och typ","Förbund och grenar","Krypterade uppgifter","Läslogg","Licensvarning 90 och 30 dagar","Kontroll före borttagning"]],
  ["Lånevapen","",[
   "Boka vapen eller plats","Tillgänglighet per tidsfönster","Mina bokningar","Externt lån med medföljare",
   ["Klubbens regler","",["Bokningshorisont","Externa lån"]],
   "Alla lån och historik"]],
  ["Valvet","Utlämning på träningskvällen, från telefonen.",["Kvällens tavla","Direktlån","Kvällen är klar","Utskrift"]],
  ["QR","",["Etiketter på vapnen","Affisch i valvet","Skanna för utlämning"]],
  ["Medlemmens vapen","",["Eget krypterat register","Licenspåminnelser","Koppla vapen till pass och resultat","Begär föreningsintyg"]]
 ]],
 ["Ekonomi","Föreningens bokföring, byggd för pistolskytteklubbar.",[
  ["Kom igång","",["Hel bokföring eller bara avgifter","Räkenskapsår","Kontoplan","Sandlådan"]],
  ["Roller","",["Kassören bokför","Styrelsen läser","Revisorn läser","Tillfällig kassörsrätt"]],
  ["Översikt","",["Väntar något på mig?","Stämmer det?","Hur går året?","Vad hände senast?"]],
  ["Bokföring","",[
   "Vi betalade / Vi fick in","Momsförslag","Kvittofoto från mobilen",
   ["Verifikationer","",["Bilagor","Rättelsepost","Makulering","Kontroll av nummerluckor"]],
   "Kontoutdrag per konto"]],
  ["Bankavstämning","",["CSV-kontoutdrag","Automatisk matchning","Manuell matchning","Bokför bankrad","Massbokföring","Saldo mot banken"]],
  ["Budget och rapport","",["Budgetversioner","Anta budget med beslutsdatum","Utfall mot budget","Underlag till styrelsemötet"]],
  ["Moms","",["Momssats per konto","Deklarationsunderlag","År, kvartal eller månad"]],
  ["Projekt","",["Tävlingen blir ett projekt","Egna projekt","Projektgrupper","Resultat per projekt"]],
  ["Fakturor","",["Fakturera en klubb","Kreditnota","Fakturor från andra föreningar","Påminnelser","Bankgiro-QR på fakturan"]],
  ["Utgifter","",["Utlägg","Räkningar med förfallodag","Kvitto","Attest","Betala"]],
  ["Anläggningsregister","",["Tillgångar","Avskrivning per månad","Årets avskrivningar","Utrangering"]],
  ["Bokslut","",["Checklista i åtta steg","Resultaträkning","Balansräkning","Fastställ året","Handlingar till årsmötet"]],
  ["SIE","",["Export","Import av tidigare bokföring","Fordringsexport"]],
  ["Revision","",[
   ["Revisorns vy","",["Resultat konto för konto","Underlag utan kvitto","Bankavstämning","Justerade protokoll"]],
   "Inbjudan till revisorn","Revisionsberättelse"]],
  ["Kvitton","",["Betalkvitto","Kvitto för friskvårdsbidrag"]]
 ]],
 ["Styrelsen","Styrelsearbetet digitalt, från kallelse till justerat protokoll.",[
  ["Möten","",[["Mötestyper","",["Styrelsemöte","Årsmöte","Extra årsmöte"]],"Närvaro","Beslutsförhet","Att göra över alla möten"]],
  ["Dagordning","",[["Punkttyper","",["Anteckning","Beslut","Personval","Märkesutdelning"]],"Punktkatalog","Bilagor","Mötesmallar"]],
  ["Kallelse","",["Mejl med dagordning","Mottagare efter mötestyp","Förhandsvisning"]],
  ["Protokoll","",["Ordförande, sekreterare och justerare","Åtgärder med ansvarig","Utskrift"]],
  ["Digital justering","",["Låst protokoll","Godkänn i appen","QR för signering på plats","Påminnelse"]],
  ["Förtroendevalda","",["Roller och mandattid","Mandat som snart går ut","Revisorer"]],
  ["Årshjul","",["Mall","Bocka av","Försenade punkter"]],
  ["Valberedning","",["Ledamöter","Kandidater per post","Valförslag för utskrift"]],
  ["Årsmötet","",["Utdelning av märken och medaljer","Avprickning av mottagare","Bokslutshandlingar"]],
  "Styrelsens dokument","Kort på startsidan"
 ]],
 ["Skytten","Allt om din egen skytteutveckling, gratis, alltid.",[
  ["Min sida","",[
   ["Dashboard","",["Aktivitet","Form","Standardmedaljer","Personliga rekord","Diagram per gren"]],
   "Vem ser min dashboard",
   ["Profil","",["Målsman","Närmast anhörig","Pistolkortsnummer"]],
   "Certifieringar"]],
  ["Resultat","",["Skott för skott","Serietotal","Extern fältskyttetävling","Träning utan poäng","Tagga vapnet"]],
  ["Mina tävlingar","",["Anmälningar","Betala","Kvitto","Avanmäl"]],
  ["Handikapp","",["Skytteklass per gren","Handikappindex","Provisoriskt handikapp"]],
  ["Märken","",["Guldserie och snabbserie","QR-verifiering","Mästarmärket och Stormästarmärket","Standardmedaljer med bevis"]],
  ["Skyttetrappan","",["Nio nivåer","Godkänns av funktionär","Egen markering från nivå 4","Mina framsteg","Min träningsgrupp"]],
  ["Träningsmatch","Tävla mot klubbkompisarna på egen bana.",[
   ["Skapa match","",["Enskild eller lag","Öppen eller stängd","Handikapp"]],
   ["Gå med","",["Kod","QR","Länk"]],
   "Gäster","Live-tavla","Foto av måltavlan","Reaktioner","Resultat till Min sida",
   ["Topplista","",["Form","Förbättring 30 dagar"]]]],
  ["Mitt schema","",["Tidslinje","Krockar","Kalenderexport","Påminnelse"]],
  ["Notiser","",["Push","Nya matcher","Formförbättring","Licensförfall"]],
  ["Siktbild","",[["Simulator","",["Skärpeplan","Ljus","Brytningsfel","Diopter","Skytteglasögon","Amplitud"]],"Videolektion"]],
  ["Mobilappen","Android-appen, i test.",["Matcher","QR-skanner","Statistik"]]
 ]],
 ["Utbildning","Kurser byggda för pistolskyttet.",[
  ["Kurser","",["Kurskatalog","Moduler","Presentationsläge","Förkunskapskrav","Leder till certifiering",["Kursutbud","",["Pistolskyttekortet","Föreningsinstruktör"]]]],
  ["Prov","",["Onlineprov","Provversioner","Pappersprov med facit","Provadmin per grupp","Registrera pappersresultat"]],
  ["Certifiering","",["Tilldela och återkalla","Certnummer och förfallodatum","Ansökan och kö","Publika instruktörslistor"]],
  ["Guider","",["Videoguider per roll","Sett-markering","Första gången här?"]]
 ]],
 ["Kretsen","Helheten över klubbarna i kretsen.",[
  ["Kretssidan","",["Klubbar","Tävlingar","Rekord","Dokument","Kretsinstruktörer","Om kretsen"]],
  ["Kretsadmin","",["Händelser","Serier","Mästare","Mästerskapsmedaljer","Statistik","Mejl till klubbarna"]],
  ["Kretsavgift","",[["Taxa","",["Grundavgift","Per medlem","Per start","Per tävling"]],"Starterna räknas fram","Eget belopp per klubb","Utskick och påminnelse","Klubben ser den som utgift"]],
  ["Kretsens styrelse och ekonomi","Samma verktyg som klubbens."],
  ["Det här händer","",["Pågår nu","Veckor och månad","Kretsfilter"]]
 ]],
 ["Gemensamt","Det som bär allt annat.",[
  ["AI-assistent","",["Svarar ur kunskapsbasen","Anpassad efter din roll"]],
  ["Svar via mejl","",["Svara utan inloggning","Rätt svarsadress"]],
  ["Konto","",["Registrering","Godkännande","Glömt lösenord","Inbjudan"]],
  ["GDPR","",["Integritetspolicy","Personuppgiftsbiträdesavtal","Radering av data","Krypterade vapenuppgifter"]],
  "Push-notiser","Mörkt läge","Rapportera fel med bilder","Klubbkatalog",
  ["Tillgänglig källkod","Alla kan läsa, granska och föreslå förbättringar."]
 ]]
]];
