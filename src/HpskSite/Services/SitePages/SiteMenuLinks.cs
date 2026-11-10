using NPoco;

namespace HpskSite.Services.SitePages
{
    /// <summary>En extern länk längst ned i ägarens meny (en rad i <c>SiteMenuItem</c> med Kind = 2).</summary>
    public sealed class SiteMenuLinkRow
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
        public byte Visibility { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>En extern länk som vyn ritar: etikett, adress och nivå. Nivån prövas för tittaren vid varje rendering (<see cref="SitePageView.Links"/>).</summary>
    public sealed record SiteExternalLink(int Id, string Label, string Url, SiteVisibility Visibility);

    /// <summary>
    /// Reglerna för en extern länk i menyn (klubbens eller kretsens Hemsida, "Länkar i menyn"). Ren kod, ingen databas.
    /// Bara <c>https://</c> (en öppen webbadress till en annan sajt), aldrig en adress med inloggningsuppgifter, mellanrum, kontrolltecken
    /// eller backslash, och aldrig en intern eller lokal adress. Etiketten är vanlig text (ritas alltid kodad).
    /// </summary>
    public static class SiteMenuLinkRules
    {
        public const int MaxLinks = 15;
        public const int MaxLabel = 80;
        public const int MaxUrl = 500;

        /// <summary>Prövar och rensar. <c>Label</c> och <c>Url</c> i svaret är de som ska lagras (trimmade); fel ger ett svenskt meddelande.</summary>
        public static (bool Ok, string? Error, string Label, string Url) Check(string? label, string? url, int visibility)
        {
            var l = (label ?? "").Trim();
            var u = (url ?? "").Trim();
            if (l.Length == 0) return (false, "Skriv ett namn på länken.", l, u);
            if (l.Length > MaxLabel) return (false, $"Namnet får vara högst {MaxLabel} tecken.", l, u);
            if (l.Any(char.IsControl)) return (false, "Namnet får inte innehålla styrtecken.", l, u);
            if (u.Length == 0) return (false, "Skriv adressen till länken.", l, u);
            if (u.Length > MaxUrl) return (false, $"Adressen får vara högst {MaxUrl} tecken.", l, u);
            if (u.Any(c => c <= ' ' || c == '\u007f' || c == '\\')) return (false, "Adressen får inte innehålla mellanrum eller snedstreck åt fel håll.", l, u);
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return (false, "Adressen måste börja med https://", l, u);
            if (!string.IsNullOrEmpty(uri.UserInfo)) return (false, "Adressen får inte innehålla användarnamn eller lösenord.", l, u);
            var host = uri.Host;
            if (host.Length == 0 || !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || uri.HostNameType == UriHostNameType.IPv6
                || System.Net.IPAddress.TryParse(host, out _))
                return (false, "Ange en vanlig webbadress, till exempel https://exempel.se/sida", l, u);
            if (!HpskSite.Extensions.SiteHtmlSanitizer.IsAllowedLink(u)) return (false, "Adressen går inte att använda.", l, u);
            if (visibility < 0 || visibility > 4 || !SiteAccess.IsSelectable((SiteVisibility)visibility)) return (false, "Välj vem som ser länken.", l, u);
            return (true, null, l, u);
        }
    }

    /// <summary>Svaret på en skrivning av en länk: gick det bra (och vilket id), annars ett svenskt meddelande.</summary>
    public sealed record SiteLinkWriteResult(bool Ok, string? Message = null, int? Id = null);

    /// <summary>Skriver externa länkar: prövar reglerna (<see cref="SiteMenuLinkRules"/>), skriver och loggar i samma transaktion (anroparen äger scopet).</summary>
    public static class SiteMenuLinkWriter
    {
        private static readonly SiteLinkWriteResult NotFound = new(false, "Länken finns inte.");

        public static SiteLinkWriteResult Save(IDatabase db, string ownerType, int ownerId, int? linkId, string? label, string? url, int visibility, SiteActor actor, DateTime now)
        {
            var c = SiteMenuLinkRules.Check(label, url, visibility);
            if (!c.Ok) return new(false, c.Error);
            if (linkId is null or 0)
            {
                var id = SiteMenuLinkStore.Add(db, ownerType, ownerId, c.Label, c.Url, visibility, now, out var full);
                if (full) return new(false, $"Ni har nått taket på {SiteMenuLinkRules.MaxLinks} länkar i menyn. Ta bort en länk först.");
                if (id == null) return NotFound;
                SitePageWriter.Audit(db, ownerType, ownerId, actor, "menulink", id.Value, "link-add", null, now);
                return new(true, Id: id);
            }
            if (!SiteMenuLinkStore.Update(db, linkId.Value, ownerType, ownerId, c.Label, c.Url, visibility, now)) return NotFound;
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "menulink", linkId.Value, "link-edit", null, now);
            return new(true, Id: linkId);
        }

        public static SiteLinkWriteResult Remove(IDatabase db, string ownerType, int ownerId, int linkId, SiteActor actor, DateTime now)
        {
            if (!SiteMenuLinkStore.Remove(db, linkId, ownerType, ownerId)) return NotFound;
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "menulink", linkId, "link-remove", null, now);
            return new(true, Id: linkId);
        }

        public static SiteLinkWriteResult Move(IDatabase db, string ownerType, int ownerId, int linkId, int direction, SiteActor actor, DateTime now)
        {
            if (direction is not (-1 or 1)) return new(false, "Okänd riktning.");
            if (SiteMenuLinkStore.Get(db, linkId, ownerType, ownerId) == null) return NotFound;
            if (!SiteMenuLinkStore.Move(db, linkId, ownerType, ownerId, direction, now)) return new(true, Id: linkId);   // redan ytterst: inget att göra, inget fel
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "menulink", linkId, "link-move", null, now);
            return new(true, Id: linkId);
        }
    }

    /// <summary>Lagring av externa länkar i menyn. Varje fråga gäller EN ägare (ägaren kommer ur begäran, aldrig ur länken) och varje id slås upp mot ägaren (id-lagen).</summary>
    public static class SiteMenuLinkStore
    {
        public static List<SiteMenuLinkRow> List(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<SiteMenuLinkRow>(
                @"SELECT Id, Label, Url, Visibility, SortOrder FROM dbo.SiteMenuItem
                  WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = 2 AND IsRemoved = 0 ORDER BY SortOrder, Id", ownerType, ownerId);

        public static SiteMenuLinkRow? Get(IDatabase db, int id, string ownerType, int ownerId) =>
            db.FirstOrDefault<SiteMenuLinkRow>(
                @"SELECT Id, Label, Url, Visibility, SortOrder FROM dbo.SiteMenuItem
                  WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Kind = 2 AND IsRemoved = 0", id, ownerType, ownerId);

        /// <summary>Lägger en länk sist. Returnerar id, eller null när ägaren inte finns eller taket är nått (<see cref="SiteMenuLinkRules.MaxLinks"/>).</summary>
        public static int? Add(IDatabase db, string ownerType, int ownerId, string label, string url, int visibility, DateTime now, out bool full)
        {
            full = false;
            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return null;
            var count = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteMenuItem WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = 2 AND IsRemoved = 0", ownerType, ownerId);
            if (count >= SiteMenuLinkRules.MaxLinks) { full = true; return null; }
            var sort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM dbo.SiteMenuItem WHERE OwnerType = @0 AND OwnerId = @1 AND Kind = 2", ownerType, ownerId) ?? 0;
            return db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SiteMenuItem (OwnerType, OwnerId, Kind, Url, Label, SortOrder, Visibility, CreatedDate, UpdatedDate)
                  VALUES (@0, @1, 2, @2, @3, @4, @5, @6, @6);
                  SELECT CAST(SCOPE_IDENTITY() AS int)", ownerType, ownerId, url, label, sort + 10, visibility, now);
        }

        public static bool Update(IDatabase db, int id, string ownerType, int ownerId, string label, string url, int visibility, DateTime now) =>
            db.Execute(
                @"UPDATE dbo.SiteMenuItem SET Label = @3, Url = @4, Visibility = @5, UpdatedDate = @6
                  WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Kind = 2 AND IsRemoved = 0", id, ownerType, ownerId, label, url, visibility, now) > 0;

        public static bool Remove(IDatabase db, int id, string ownerType, int ownerId) =>
            db.Execute("DELETE FROM dbo.SiteMenuItem WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Kind = 2", id, ownerType, ownerId) > 0;

        /// <summary>Flyttar en länk ett steg (-1 upp, +1 ner) genom att byta ordning med grannen. Ordningen numreras om (10, 20, …) så att två länkar aldrig delar ordning. Returnerar false om länken inte finns eller redan står ytterst.</summary>
        public static bool Move(IDatabase db, int id, string ownerType, int ownerId, int direction, DateTime now)
        {
            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return false;
            var all = List(db, ownerType, ownerId);
            var at = all.FindIndex(x => x.Id == id);
            var to = at + direction;
            if (at < 0 || to < 0 || to >= all.Count) return false;
            (all[at], all[to]) = (all[to], all[at]);
            for (var i = 0; i < all.Count; i++)
                db.Execute("UPDATE dbo.SiteMenuItem SET SortOrder = @3, UpdatedDate = @4 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND Kind = 2", all[i].Id, ownerType, ownerId, (i + 1) * 10, now);
            return true;
        }
    }
}
