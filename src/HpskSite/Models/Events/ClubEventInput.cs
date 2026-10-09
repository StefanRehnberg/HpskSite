namespace HpskSite.Models.Events
{
    /// <summary>
    /// <b>Enda tolkningen av händelseformulärets anmälningsfält</b> (sajtstrukturen fas 2a). Låg som
    /// <c>ClubController.ApplyEventRegistrationFields</c> och skrev direkt på noden; nu skriver den på
    /// <see cref="ClubEventRecord"/>, och lagringen avgör om fälten går att spara.
    ///
    /// <para><b>⚠️ EN skrivväg för alla anmälningsfält, och det är inte städning.</b> Fälten redigeras
    /// från TRE dialoger (klubbens panel, kretsens panel, händelsens egen sida), och en egenskap som
    /// bara skrivs i en av dem försvinner tyst vid nästa sparning från en annan.</para>
    ///
    /// <para>Returnerar ett färdigt felmeddelande, eller null. Vid fel är posten delvis ändrad — anroparen
    /// sparar den inte (meddelandena slutar med "Ingenting sparades.").</para>
    /// </summary>
    public static class ClubEventInput
    {
        private sealed class IncomingPrice
        {
            public string? Id { get; set; }
            public string? Label { get; set; }
            public decimal? Amount { get; set; }
        }

        public static string? ApplyRegistrationFields(
            ClubEventRecord r,
            bool registrationRequired, int maxParticipants, bool isMandatory, string? registrationUrl,
            bool lanevapenOffered = false, string? registrationDeadline = "", string? eventPrices = "",
            string? eventAudience = "", string? eventSwishNumber = "")
        {
            r.RegistrationRequired = registrationRequired;
            r.RegistrationUrl = registrationUrl ?? "";
            r.MaxParticipants = maxParticipants > 0 ? maxParticipants : 0;
            r.IsMandatory = isMandatory;
            r.LanevapenOffered = lanevapenOffered;

            // Sista anmälningsdag. Tom sträng = ingen deadline, och den ska kunna TAS BORT igen.
            // ⚠️ Ett oparsbart datum blir INTE null — det hade sett ut som "arrangören tog bort
            // deadlinen" och tyst öppnat anmälan igen.
            DateTime? deadline = null;
            if (!string.IsNullOrWhiteSpace(registrationDeadline))
            {
                if (!DateTime.TryParse(registrationDeadline, out var parsedDeadline))
                    return $"\"{registrationDeadline}\" går inte att läsa som ett datum. "
                         + "Skriv sista anmälningsdag som ÅÅÅÅ-MM-DD, eller lämna fältet tomt. "
                         + "Ingenting sparades.";
                deadline = parsedDeadline.Date;
            }
            r.RegistrationDeadline = deadline;

            // Prisraderna. Tom lista = ingen avgift. ⚠️ Återanvänd aldrig EventFeeMigration.Parse här
            // (den tolkar gammal fritext tolerant). ⚠️ SERVERN ÄGER ID-GENERERINGEN: tomt id = ny rad,
            // ett befintligt id behålls — det binder en gjord anmälan till sin prisrad. En ogiltig lista
            // blir INTE tom; det hade tyst gjort evenemanget gratis.
            var priceRows = new List<EventPrice>();
            if (!string.IsNullOrWhiteSpace(eventPrices))
            {
                List<IncomingPrice>? incoming;
                try
                {
                    incoming = System.Text.Json.JsonSerializer.Deserialize<List<IncomingPrice>>(
                        eventPrices, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (System.Text.Json.JsonException)
                {
                    return "Prisraderna kunde inte lasas. Ladda om sidan och forsok igen. Ingenting sparades.";
                }

                foreach (var row in incoming ?? new List<IncomingPrice>())
                {
                    var label = (row.Label ?? "").Trim();
                    if (label.Length == 0 && row.Amount == null) continue;   // helt tom rad = struken
                    var id = string.IsNullOrWhiteSpace(row.Id)
                        ? EventPrices.NewId(label, priceRows.Select(x => x.Id))
                        : row.Id.Trim();
                    priceRows.Add(new EventPrice(id, label, row.Amount ?? 0m));
                }

                var priceError = EventPrices.Validate(priceRows);
                if (priceError != null) return priceError + " Ingenting sparades.";
            }
            r.PricesJson = priceRows.Count == 0 ? "" : EventPrices.Serialize(priceRows);

            // Swish-numret. ⚠️ TOMT ÄR ETT GILTIGT VÄRDE och betyder "använd ägarens nummer". Formatet
            // valideras där betalningen begärs (SwishQrCodeGenerator), inte här.
            r.SwishNumber = (eventSwishNumber ?? "").Trim();

            // Vem som får anmäla sig. ⚠️⚠️ ETT OKÄNT VÄRDE FÅR ALDRIG SKRIVAS — varken som sig självt eller
            // tyst normaliserat till den smalaste nivån (då tror arrangören att grannklubben är inbjuden).
            var audience = EventAudience.Normalise(eventAudience);
            if (!string.IsNullOrWhiteSpace(eventAudience)
                && !string.Equals(audience, eventAudience.Trim(), StringComparison.OrdinalIgnoreCase))
                return $"\"{eventAudience}\" är inte ett giltigt val för vem som får anmäla sig. "
                     + "Ladda om sidan och välj igen. Ingenting sparades.";
            r.Audience = audience;
            return null;
        }

        /// <summary>
        /// Vilka fält i posten som har ett värde som lagringen INTE kan bära. Saknas en egenskap
        /// rapporteras det bara när någon försökte sätta något annat än standard — att larma om ett val
        /// ingen gjort vore brus.
        /// </summary>
        public static List<string> UnsupportedValues(ClubEventRecord r, ClubEventFieldSupport s)
        {
            var missing = new List<string>();
            if (!s.Mandatory && r.IsMandatory) missing.Add(HpskSite.Models.ClubEvents.MandatoryProperty);
            if (!s.LanevapenOffered && r.LanevapenOffered) missing.Add(HpskSite.Services.Firearms.LoanWeaponClubRules.EventOfferedProperty);
            if (!s.Deadline && r.RegistrationDeadline != null) missing.Add(HpskSite.Models.ClubEvents.DeadlineProperty);
            if (!s.Prices && !string.IsNullOrWhiteSpace(r.PricesJson)) missing.Add(EventPrices.Property);
            if (!s.Swish && !string.IsNullOrWhiteSpace(r.SwishNumber)) missing.Add(HpskSite.Models.ClubEvents.SwishProperty);
            if (!s.Audience && r.Audience != EventAudience.Club) missing.Add(EventAudience.Property);
            return missing;
        }

        public static string MissingPropertiesMessage(IReadOnlyCollection<string> missing)
            => $"Egenskapen '{string.Join("' och '", missing)}' saknas på händelsetypen i "
             + "Umbraco — kontakta en administratör. Ingenting sparades.";
    }
}
