namespace HpskSite.Services.Events;

/// <summary>
/// Vilka personuppgifter en händelselista får bära. Listorna <c>GetClubEvents</c> och <c>GetRegionEvents</c>
/// är REDIGERINGSLISTOR (samma projektion fyller redigeringsdialogen) men svarar utan inloggningskrav,
/// eftersom klubbens och kretsens sidor hämtar dem. Kontaktperson, kontakt-e-post, kontakttelefon och
/// Swish-nummer får därför bara följa med för ägarens administratör; alla andra får tomma fält.
/// Expertgruppen 2026-11-09 (S2): en utloggad kunde läsa kontakt-e-post och telefon för varje händelse
/// som tittaren fick se.
/// </summary>
public static class EventRowPrivacy
{
    /// <summary>Värdet för ägarens administratör, annars en tom sträng.</summary>
    public static string Contact(string? value, bool includeContacts)
        => includeContacts ? value ?? "" : "";
}
