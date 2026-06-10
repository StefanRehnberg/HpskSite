# Registerförteckning över personuppgiftsbehandling

**Organisation:** Hallands Pistolskyttekrets (HPSK), org.nr 849202-2416
**Tjänst:** Pistol.nu
**Kontakt i dataskyddsfrågor:** admin@pistol.nu · _[fyll i namn på ansvarig i styrelsen]_
**Senast uppdaterad:** 2026-06-05 · **Version:** 1.0

> Detta är HPSK:s förteckning enligt artikel 30 GDPR. HPSK behandlar personuppgifter i **två roller**:
> dels som **personuppgiftsansvarig** för sin egen verksamhet och för plattformens drift, dels som
> **personuppgiftsbiträde** åt de föreningar som använder Pistol.nu. Båda rollerna redovisas nedan.
>
> Dataskyddsombud (DSO) bedöms **inte krävas** (art. 37): ingen storskalig övervakning eller storskalig
> behandling av känsliga uppgifter. Bedömningen omprövas vid behov. _[Bekräfta med er expert.]_

---

## Del 1 – HPSK som personuppgiftsansvarig (art. 30.1)

Gäller HPSK:s egna medlemmar/funktionärer samt drift- och säkerhetsuppgifter för plattformen.

| Fält | Innehåll |
|---|---|
| **Ändamål** | Administrera HPSK:s egen verksamhet och kretsens medlemmar/funktionärer; drift, säkerhet och felsökning av Pistol.nu; uppfylla krav från idrotts-/skytteförbund och myndigheter. |
| **Rättslig grund** | Avtal (medlemskap), berättigat intresse (drift/säkerhet/administration), rättslig förpliktelse (bokföring m.m.). |
| **Kategorier av registrerade** | Medlemmar och funktionärer i HPSK; administratörer; besökare på webbplatsen. |
| **Kategorier av personuppgifter** | Namn, kontaktuppgifter, klubbtillhörighet, roller/behörigheter; tekniska drift- och säkerhetsloggar (t.ex. inloggning, senaste aktivitet, IP-adress i loggar). |
| **Mottagare** | Underbiträden enligt tabell nedan. Inga uppgifter säljs eller delas för marknadsföring. |
| **Tredjelandsöverföring** | Nej – all behandling sker inom EU/EES. |
| **Lagringstid** | Medlems-/funktionärsuppgifter: under aktivt engagemang + rimlig tid därefter. Drift-/säkerhetsloggar: kort tid, normalt _[fyll i, t.ex. 12 mån]_. Bokföringsunderlag: 7 år (bokföringslagen). |
| **Säkerhetsåtgärder** | Se gemensam beskrivning sist i dokumentet. |

---

## Del 2 – HPSK som personuppgiftsbiträde (art. 30.2)

Gäller behandling som HPSK utför **på uppdrag av** de anslutna föreningarna (personuppgiftsansvariga).

| Fält | Innehåll |
|---|---|
| **Personuppgiftsansvariga** | De föreningar som godkänt personuppgiftsbiträdesavtalet på pistol.nu. Aktuell lista + godkännanden finns i tabellen `ClubDpaAcceptance`. |
| **Kategorier av behandling som utförs** | Lagring och tillhandahållande av medlems-, tränings- och tävlingsadministration: medlemsregister, tävlingsanmälningar och resultat, träningsresultat/Skyttetrappan, betalningar för anmälningsavgifter, märken/medaljer, skjutbanedata. |
| **Kategorier av personuppgifter** | Kontaktuppgifter (namn, e-post, telefon, adress); personnummer (för tävlingsregistrering); pistolkortnummer; klubbtillhörighet och roller; tävlings-/träningsdata; betalningsuppgifter; tekniska uppgifter (senaste aktivitet). |
| **Tredjelandsöverföring** | Nej – ingen överföring utanför EU/EES. |
| **Underbiträden** | Se tabell nedan. |
| **Reglerat genom** | Personuppgiftsbiträdesavtal (`/personuppgiftsbitradesavtal`, version enligt `DpaInfo`), godkänt elektroniskt per förening. |
| **Säkerhetsåtgärder** | Se gemensam beskrivning sist i dokumentet. |

---

## Underbiträden (driftleverantörer)

| Underbiträde | Tjänst | Plats | Tredjeland | Biträdesavtal |
|---|---|---|---|---|
| Simply.com A/S | Webb- och databasdrift (hosting) | Danmark (EU) | Nej | _[bifoga/länka signerat databehandleraftale]_ |
| Brevo (Sendinblue SAS) | E-post (aviseringar, kvitton, utskick) | Frankrike (EU) | Nej | _[spara kopia av DPA från Brevos villkor]_ |
| Mistral AI | AI-assistenten (frivillig hjälpfunktion) | Frankrike (EU) | Nej | _[acceptera/spara Mistrals DPA]_ |

> Vid byte/tillägg av underbiträde: uppdatera denna tabell + Bilaga B i biträdesavtalet och informera
> föreningarnas administratörer (30 dagars invändningsrätt enligt avtalets avsnitt 5).

---

## Säkerhetsåtgärder (art. 32) – gemensam beskrivning

- Krypterad överföring (HTTPS/TLS) för all kommunikation med tjänsten.
- Roll- och klubbaserad behörighetsstyrning – användare når endast de uppgifter de har rätt till.
- Lösenordsautentisering; lösenord lagras aldrig i klartext.
- Loggning av administrativa åtgärder där det är relevant (t.ex. betalnings- och medlemsändringar).
- Säkerhetskopiering hos driftleverantören; återställning möjlig.
- Begränsad personkrets med åtkomst, omfattad av tystnadsplikt.

---

_Förvaras hos HPSK:s styrelse. Visas upp på begäran av en förening (personuppgiftsansvarig) eller IMY.
HPSK och anslutna klubbar tillhör SPSF (frivillig försvarsorganisation), inte RF – RF:s mallar/rutiner
gäller inte formellt, men kan användas som förlaga. Kontrollera om SPSF har egen vägledning._
