using System.Globalization;
using System.Net.Mail;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NPoco;

namespace HpskSite.Services.SitePages
{
    public enum SiteSignupStatus : byte { New = 0, Queued = 1, Denied = 2, Approved = 3 }

    public enum SiteSignupOpenState { Open, NotYet, Closed }

    /// <summary>
    /// Anmälan/ansökan-blockets inställningar (ur blockets data, aldrig ur besökarens inmatning). <see cref="Phone"/>, <see cref="Address"/> och
    /// <see cref="PersonalNumber"/> betyder "be om det, och kräv det"; <see cref="Gender"/> och <see cref="Message"/> är frivilliga för besökaren.
    /// Namn och e-post krävs alltid. <see cref="Choices"/> tom = inget val.
    /// </summary>
    public sealed record SiteSignupConfig(string Title, string? Intro, IReadOnlyList<string> Choices, bool Phone, bool Address, bool Gender,
        bool PersonalNumber, bool Message, DateTime? Opens, DateTime? Closes, string? Thanks)
    {
        public const string DefaultTitle = "Anmälan";
        public const string DefaultThanks = "Tack! Vi har tagit emot din anmälan och hör av oss.";

        public static SiteSignupConfig FromBlock(SiteBlock b)
        {
            string? S(string key) => b.Data[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
            bool B(string key) => b.Data[key] is JsonValue v && v.TryGetValue<bool>(out var f) && f;
            DateTime? D(string key) => S(key) is string s && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;
            var choices = b.Data["choices"] is JsonArray arr
                ? arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).ToList()
                : new List<string>();
            return new SiteSignupConfig(S("title") ?? DefaultTitle, S("intro"), choices, B("phone"), B("address"), B("gender"), B("pnr"), B("message"), D("opens"), D("closes"), S("thanks"));
        }

        /// <summary>Tar anmälan emot <paramref name="today"/>? Öppningsdagen och stängningsdagen räknas som öppna.</summary>
        public SiteSignupOpenState StateOn(DateTime today)
        {
            if (Opens != null && today.Date < Opens.Value.Date) return SiteSignupOpenState.NotYet;
            if (Closes != null && today.Date > Closes.Value.Date) return SiteSignupOpenState.Closed;
            return SiteSignupOpenState.Open;
        }
    }

    /// <summary>Det besökaren skickade in (råa värden).</summary>
    public sealed record SiteSignupInput(string? Name, string? Email, string? Phone, string? Gender, string? PersonalNumber, string? Address,
        string? PostalCode, string? City, string? Message, string? Choice, bool Consent);

    /// <summary>Det som ska lagras: rensat och normaliserat. Fält som formuläret inte bad om är alltid null (ett fält som inte frågades efter lagras aldrig, hur dumt det än skickades).</summary>
    public sealed record SiteSignupData(string Name, string Email, string? Phone, string? Gender, string? PersonalNumber, string? Address,
        string? PostalCode, string? City, string? Message, string? Choice);

    public sealed record SiteSignupCheck(bool Ok, IReadOnlyList<(string Field, string Message)> Errors, SiteSignupData? Data);

    /// <summary>Vad sidan säger efter en inskickning (koden ligger i adressen: <c>?anmalan=blockId&amp;r=ok</c> eller <c>&amp;r=err&amp;e=kod</c>). Ingen text ur adressen skrivs någonsin ut: bara kända koder.</summary>
    public sealed record SiteFormFlash(string BlockId, bool Success, string? ErrorCode);

    /// <summary>Det ett anmälningsblock behöver veta om begäran: sidan (för formulärets mål), skyddstoken och eventuellt besked efter en inskickning. Null = en förhandsvisning: formuläret visas men går inte att skicka.</summary>
    public sealed record SiteFormContext(int PageId, string? Token, SiteFormFlash? Flash);

    /// <summary>Reglerna för en anmälan. Ren kod, ingen databas.</summary>
    public static class SiteSignupRules
    {
        public const int MaxChoices = 10;
        public const int MaxChoiceLength = 80;
        public static readonly string[] Genders = { "Kvinna", "Man", "Annat", "Vill inte ange" };

        private static readonly Regex PhoneRx = new(@"^[+0-9 ()\-]{6,20}$", RegexOptions.Compiled);
        private static readonly Regex PostalRx = new(@"^\d{3} ?\d{2}$", RegexOptions.Compiled);

        /// <summary>Felkoder som får stå i adressen efter en inskickning, och texten besökaren ser.</summary>
        public static string? ErrorText(string? code) => code switch
        {
            "closed" => "Anmälan är inte öppen just nu.",
            "rate" => "Det har kommit många anmälningar från din uppkoppling. Vänta en stund och försök igen.",
            "invalid" => "Det gick inte att ta emot anmälan. Kontrollera uppgifterna och försök igen.",
            "full" => "Anmälan kan inte ta emot fler just nu. Kontakta föreningen.",
            _ => code == null ? null : "Det gick inte att ta emot anmälan. Försök igen om en stund.",
        };

        public static SiteSignupCheck Validate(SiteSignupConfig cfg, SiteSignupInput i, DateTime today)
        {
            var errors = new List<(string, string)>();
            string? Clean(string? s, int max, string field, string label, bool required, int min = 0)
            {
                var v = (s ?? "").Trim();
                if (v.Length == 0) { if (required) errors.Add((field, $"Fyll i {label}.")); return null; }
                if (v.Length > max) { errors.Add((field, $"{Cap(label)} får vara högst {max} tecken.")); return null; }
                if (v.Length < min) { errors.Add((field, $"{Cap(label)} är för kort.")); return null; }
                if (v.Any(char.IsControl)) { errors.Add((field, $"{Cap(label)} innehåller tecken som inte går att ta emot.")); return null; }
                return v;
            }

            if (cfg.StateOn(today) != SiteSignupOpenState.Open) errors.Add(("", ErrorText("closed")!));
            var name = Clean(i.Name, 120, "name", "ditt namn", true, 2);
            if (name != null && (name.Contains('<') || name.Contains('>'))) { errors.Add(("name", "Namnet innehåller tecken som inte går att ta emot.")); name = null; }
            var email = Clean(i.Email, 200, "email", "din e-postadress", true, 5);
            if (email != null && !IsEmail(email)) { errors.Add(("email", "Ange en giltig e-postadress.")); email = null; }

            string? choice = null;
            if (cfg.Choices.Count > 0)
            {
                var c = (i.Choice ?? "").Trim();
                choice = cfg.Choices.FirstOrDefault(x => string.Equals(x, c, StringComparison.Ordinal));
                if (choice == null) errors.Add(("choice", "Välj vad din anmälan gäller."));
            }

            string? phone = null, address = null, postal = null, city = null, gender = null, pnr = null, message = null;
            if (cfg.Phone)
            {
                phone = Clean(i.Phone, 20, "phone", "ditt telefonnummer", true);
                if (phone != null && !PhoneRx.IsMatch(phone)) { errors.Add(("phone", "Ange ett giltigt telefonnummer.")); phone = null; }
            }
            if (cfg.Address)
            {
                address = Clean(i.Address, 200, "address", "din adress", true, 2);
                postal = Clean(i.PostalCode, 10, "postalCode", "ditt postnummer", true);
                if (postal != null && !PostalRx.IsMatch(postal)) { errors.Add(("postalCode", "Ange ett postnummer med fem siffror.")); postal = null; }
                else if (postal != null) postal = postal.Replace(" ", "")[..3] + " " + postal.Replace(" ", "")[3..];
                city = Clean(i.City, 80, "city", "din ort", true, 2);
            }
            if (cfg.Gender)
            {
                var g = (i.Gender ?? "").Trim();
                if (g.Length > 0) { gender = Genders.FirstOrDefault(x => x == g); if (gender == null) errors.Add(("gender", "Välj ett av alternativen.")); }
            }
            if (cfg.PersonalNumber)
            {
                var raw = (i.PersonalNumber ?? "").Trim();
                if (raw.Length == 0) errors.Add(("personalNumber", "Fyll i ditt personnummer."));
                else if ((pnr = NormalizePersonalNumber(raw, today)) == null) errors.Add(("personalNumber", "Ange personnumret som ÅÅÅÅMMDD-NNNN."));
            }
            if (cfg.Message) message = Clean(i.Message, 1000, "message", "meddelandet", false);
            if (!i.Consent) errors.Add(("consent", "Du måste godkänna att uppgifterna sparas för att vi ska kunna ta emot anmälan."));

            if (errors.Count > 0) return new SiteSignupCheck(false, errors, null);
            return new SiteSignupCheck(true, errors, new SiteSignupData(name!, email!, phone, gender, pnr, address, postal, city, message, choice));
        }

        private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.GetCultureInfo("sv-SE")) + s[1..];

        public static bool IsEmail(string s)
        {
            if (s.Length > 200 || s.Any(c => c <= ' ' || c == ',' || c == ';')) return false;
            try { var a = new MailAddress(s); return a.Address == s && a.Host.Contains('.') && !a.Host.StartsWith('.') && !a.Host.EndsWith('.'); }
            catch (FormatException) { return false; }
        }

        /// <summary>
        /// Personnummer som <c>ÅÅÅÅMMDD-NNNN</c> eller <c>ÅÅÅÅMMDDNNNN</c> (tolv siffror): giltigt datum, inte i framtiden, och kontrollsiffran (Luhn över de sista tio)
        /// stämmer. Svaret är alltid <c>ÅÅÅÅMMDD-NNNN</c>, eller null. Samordningsnummer (dag 61–91) godtas.
        /// </summary>
        public static string? NormalizePersonalNumber(string raw, DateTime today)
        {
            var s = raw.Trim();
            var m = Regex.Match(s, @"^(\d{8})-?(\d{4})$");
            if (!m.Success) return null;
            var date = m.Groups[1].Value; var tail = m.Groups[2].Value;
            var day = int.Parse(date[6..8]);
            if (day >= 61) day -= 60;   // samordningsnummer
            if (!DateTime.TryParseExact($"{date[..6]}{day:00}", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
            if (d.Date > today.Date || d.Year < 1900) return null;
            return Luhn(date[2..] + tail) ? date + "-" + tail : null;
        }

        private static bool Luhn(string digits)
        {
            var sum = 0;
            for (var i = 0; i < digits.Length; i++)
            {
                var n = digits[i] - '0';
                if (i % 2 == 0) { n *= 2; if (n > 9) n -= 9; }
                sum += n;
            }
            return sum % 10 == 0;
        }
    }

    /// <summary>En rad i <c>SiteFormEntry</c>.</summary>
    public sealed class SiteSignupRow
    {
        public int Id { get; set; }
        public string OwnerType { get; set; } = "";
        public int OwnerId { get; set; }
        public int PageId { get; set; }
        public string BlockId { get; set; } = "";
        public string FormTitle { get; set; } = "";
        public string? Choice { get; set; }
        public byte Status { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public string? Gender { get; set; }
        public string? PersonalNumber { get; set; }
        public string? Address { get; set; }
        public string? PostalCode { get; set; }
        public string? City { get; set; }
        public string? Message { get; set; }
        public string? AdminNote { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime? HandledDate { get; set; }
        public int? HandledByMemberId { get; set; }
        public DateTime? ArchivedDate { get; set; }
    }

    public enum SiteSignupInsert { Ok, Duplicate, Full, NoOwner }

    /// <summary>Ett formulär i listan över formulär (en sida + ett block): rubrik och antal.</summary>
    public sealed class SiteSignupFormRow
    {
        public int PageId { get; set; }
        public string BlockId { get; set; } = "";
        public string FormTitle { get; set; } = "";
        public int Total { get; set; }
        public int NewCount { get; set; }
    }

    /// <summary>
    /// Lagringen av anmälningar. Varje fråga gäller EN ägare (ägaren kommer ur begäran, aldrig ur raden) och ett id utifrån slås alltid upp mot ägaren (id-lagen).
    /// Skrivningen sker under ägarens lås, så att taken och dubblettkontrollen inte kan passeras av två samtidiga anmälningar.
    /// </summary>
    public static class SiteSignupStore
    {
        public const int MaxPerForm = 2000;
        public const int MaxPerOwner = 5000;
        public const int PageSize = 50;
        private const string Cols = "Id, OwnerType, OwnerId, PageId, BlockId, FormTitle, Choice, Status, Name, Email, Phone, Gender, PersonalNumber, Address, PostalCode, City, Message, AdminNote, CreatedDate, HandledDate, HandledByMemberId, ArchivedDate";

        public static SiteSignupInsert Insert(IDatabase db, string ownerType, int ownerId, int pageId, string blockId, string formTitle, SiteSignupData d, DateTime now, out int id)
        {
            id = 0;
            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return SiteSignupInsert.NoOwner;
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId) >= MaxPerOwner) return SiteSignupInsert.Full;
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerType = @0 AND OwnerId = @1 AND PageId = @2 AND BlockId = @3", ownerType, ownerId, pageId, blockId) >= MaxPerForm) return SiteSignupInsert.Full;
            // Samma e-postadress har redan en väntande anmälan i det här formuläret: inget nytt (och besökaren får samma tack, så att ingen kan pröva vilka adresser som finns).
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerType = @0 AND OwnerId = @1 AND PageId = @2 AND BlockId = @3 AND Email = @4 AND Status IN (0, 1) AND ArchivedDate IS NULL",
                    ownerType, ownerId, pageId, blockId, d.Email) > 0) return SiteSignupInsert.Duplicate;
            id = db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SiteFormEntry (OwnerType, OwnerId, PageId, BlockId, FormTitle, Choice, Status, Name, Email, Phone, Gender, PersonalNumber, Address, PostalCode, City, Message, CreatedDate)
                  VALUES (@0, @1, @2, @3, @4, @5, 0, @6, @7, @8, @9, @10, @11, @12, @13, @14, @15);
                  SELECT CAST(SCOPE_IDENTITY() AS int)",
                ownerType, ownerId, pageId, blockId, Trunc(formTitle, 120), d.Choice, d.Name, d.Email, d.Phone, d.Gender, d.PersonalNumber, d.Address, d.PostalCode, d.City, d.Message, now);
            return SiteSignupInsert.Ok;
        }

        private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

        public sealed record Filter(byte? Status = null, int? PageId = null, string? BlockId = null, string? Search = null, bool Archived = false);

        private static (string Where, List<object> Args) Where(string ownerType, int ownerId, Filter f)
        {
            var args = new List<object> { ownerType, ownerId };
            var w = "OwnerType = @0 AND OwnerId = @1";
            w += f.Archived ? " AND ArchivedDate IS NOT NULL" : " AND ArchivedDate IS NULL";
            if (f.Status != null) { w += $" AND Status = @{args.Count}"; args.Add((int)f.Status.Value); }
            if (f.PageId != null && f.BlockId != null) { w += $" AND PageId = @{args.Count} AND BlockId = @{args.Count + 1}"; args.Add(f.PageId.Value); args.Add(f.BlockId); }
            if (!string.IsNullOrWhiteSpace(f.Search))
            {
                var like = "%" + f.Search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
                w += $" AND (Name LIKE @{args.Count} OR Email LIKE @{args.Count})"; args.Add(like);
            }
            return (w, args);
        }

        public static List<SiteSignupRow> List(IDatabase db, string ownerType, int ownerId, Filter f, int skip = 0, int take = PageSize)
        {
            var (w, args) = Where(ownerType, ownerId, f);
            args.Add(Math.Max(0, skip)); args.Add(Math.Clamp(take, 1, 500));
            return db.Fetch<SiteSignupRow>($"SELECT {Cols} FROM dbo.SiteFormEntry WHERE {w} ORDER BY CreatedDate DESC, Id DESC OFFSET @{args.Count - 2} ROWS FETCH NEXT @{args.Count - 1} ROWS ONLY", args.ToArray());
        }

        public static int Count(IDatabase db, string ownerType, int ownerId, Filter f)
        {
            var (w, args) = Where(ownerType, ownerId, f);
            return db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE {w}", args.ToArray());
        }

        /// <summary>Antal per status bland icke-arkiverade (för flikarna), och antal arkiverade.</summary>
        public static (int[] ByStatus, int Archived) Counts(IDatabase db, string ownerType, int ownerId)
        {
            var rows = db.Fetch<StatusCount>("SELECT Status AS S, COUNT(*) AS N FROM dbo.SiteFormEntry WHERE OwnerType = @0 AND OwnerId = @1 AND ArchivedDate IS NULL GROUP BY Status", ownerType, ownerId);
            var by = new int[4]; foreach (var r in rows) if (r.S is >= 0 and <= 3) by[r.S] = r.N;
            var archived = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerType = @0 AND OwnerId = @1 AND ArchivedDate IS NOT NULL", ownerType, ownerId);
            return (by, archived);
        }

        private sealed class StatusCount { public int S { get; set; } public int N { get; set; } }

        public static List<SiteSignupFormRow> Forms(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<SiteSignupFormRow>(
                @"SELECT PageId, BlockId, MAX(FormTitle) AS FormTitle, COUNT(*) AS Total, SUM(CASE WHEN Status = 0 AND ArchivedDate IS NULL THEN 1 ELSE 0 END) AS NewCount
                  FROM dbo.SiteFormEntry WHERE OwnerType = @0 AND OwnerId = @1 GROUP BY PageId, BlockId ORDER BY MAX(CreatedDate) DESC", ownerType, ownerId);

        public static SiteSignupRow? Get(IDatabase db, int id, string ownerType, int ownerId) =>
            db.FirstOrDefault<SiteSignupRow>($"SELECT {Cols} FROM dbo.SiteFormEntry WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId);

        public static int NewCount(IDatabase db, string ownerType, int ownerId) =>
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerType = @0 AND OwnerId = @1 AND Status = 0 AND ArchivedDate IS NULL", ownerType, ownerId);

        /// <summary>
        /// Sätter status. Avgjord (nekad/godkänd) får datum och vem; ny/köad rensar avgörandet så att raden inte gallras som avgjord. Personnumret rörs inte här:
        /// det nollas av gallringen 30 dagar efter avgörandet.
        /// </summary>
        public static bool SetStatus(IDatabase db, int id, string ownerType, int ownerId, SiteSignupStatus status, int? memberId, DateTime now)
        {
            var decided = status is SiteSignupStatus.Denied or SiteSignupStatus.Approved;
            return db.Execute(
                @"UPDATE dbo.SiteFormEntry SET Status = @3, HandledDate = @4, HandledByMemberId = @5 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2",
                id, ownerType, ownerId, (int)status, decided ? now : null, decided ? memberId : null) > 0;
        }

        public static bool SetNote(IDatabase db, int id, string ownerType, int ownerId, string? note) =>
            db.Execute("UPDATE dbo.SiteFormEntry SET AdminNote = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId, string.IsNullOrWhiteSpace(note) ? null : Trunc(note.Trim(), 500)) > 0;

        public static bool SetArchived(IDatabase db, int id, string ownerType, int ownerId, bool archived, DateTime now) =>
            db.Execute("UPDATE dbo.SiteFormEntry SET ArchivedDate = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId, archived ? now : null) > 0;

        public static bool Delete(IDatabase db, int id, string ownerType, int ownerId) =>
            db.Execute("DELETE FROM dbo.SiteFormEntry WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId) > 0;

        /// <summary>CSV för Excel: semikolon, citerade fält, UTF-8 med BOM läggs på av anroparen. Ett fält som börjar med = + - @ eller ett styrtecken får ett inledande ' (formelinjektion).</summary>
        public static string ToCsv(IEnumerable<SiteSignupRow> rows)
        {
            static string Cell(string? s)
            {
                var v = (s ?? "").Replace("\r", " ").Replace("\n", " ");
                if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@' or '\t')) v = "'" + v;
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            }
            var names = new[] { "Inkom", "Formulär", "Gäller", "Status", "Namn", "E-post", "Telefon", "Kön", "Personnummer", "Adress", "Postnummer", "Ort", "Meddelande", "Anteckning", "Avgjord" };
            var sb = new System.Text.StringBuilder(string.Join(";", names.Select(Cell))).Append("\r\n");
            foreach (var r in rows)
                sb.Append(string.Join(";", new[]
                {
                    Cell(r.CreatedDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)), Cell(r.FormTitle), Cell(r.Choice), Cell(StatusName(r.Status)), Cell(r.Name), Cell(r.Email), Cell(r.Phone), Cell(r.Gender),
                    Cell(r.PersonalNumber), Cell(r.Address), Cell(r.PostalCode), Cell(r.City), Cell(r.Message), Cell(r.AdminNote), Cell(r.HandledDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                })).Append("\r\n");
            return sb.ToString();
        }

        public static string StatusName(byte s) => s switch { 0 => "Ny", 1 => "Köande", 2 => "Nekad", 3 => "Godkänd", _ => "Okänd" };
    }

    /// <summary>Skriver anmälningar: loggar (utan text, bara id och åtgärd) i samma transaktion. Anroparen äger scopet.</summary>
    public static class SiteSignupWriter
    {
        public static bool Status(IDatabase db, string ownerType, int ownerId, int id, SiteSignupStatus status, SiteActor actor, DateTime now)
        {
            if (!SiteSignupStore.SetStatus(db, id, ownerType, ownerId, status, actor.MemberId, now)) return false;
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "form", id, "entry-status-" + (int)status, null, now);
            return true;
        }

        public static bool Note(IDatabase db, string ownerType, int ownerId, int id, string? note, SiteActor actor, DateTime now)
        {
            if (!SiteSignupStore.SetNote(db, id, ownerType, ownerId, note)) return false;
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "form", id, "entry-note", null, now);
            return true;
        }

        public static bool Archive(IDatabase db, string ownerType, int ownerId, int id, bool archive, SiteActor actor, DateTime now)
        {
            if (!SiteSignupStore.SetArchived(db, id, ownerType, ownerId, archive, now)) return false;
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "form", id, archive ? "entry-archive" : "entry-unarchive", null, now);
            return true;
        }

        public static bool Delete(IDatabase db, string ownerType, int ownerId, int id, SiteActor actor, DateTime now)
        {
            if (!SiteSignupStore.Delete(db, id, ownerType, ownerId)) return false;
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "form", id, "entry-delete", null, now);
            return true;
        }
    }
}
