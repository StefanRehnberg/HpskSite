using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Ett utkast till verifikation — ett halvfyllt formulär, inte bokföring.
    ///
    /// <para><b>⚠️⚠️ EGEN TABELL, INTE EN FLAGGA PÅ <see cref="LedgerJournalEntry"/>.</b> Två skäl:
    /// verifikationstabellen bär triggers som förbjuder <c>UPDATE</c> och <c>DELETE</c>, så en flagga
    /// där hade krävt ett hål i spärren; och ett utkast i samma tabell som bokföringen kan komma med
    /// i en summering, där en glömd <c>WHERE IsDraft = 0</c> blir en tyst lögn om föreningens
    /// ekonomi. Skilda tabeller gör båda felen omöjliga i stället för osannolika.</para>
    ///
    /// <para><b>⚠️ Utkastet har INGET verifikationsnummer</b>, och det är hela poängen. Numret delas
    /// ut när posten bokförs. En kassör kan alltså börja mata in, bli avbruten och återkomma i
    /// morgon utan att ett nummer står reserverat på något som kanske aldrig blir en post.</para>
    ///
    /// <para>Det är också vad som gör knappen <i>"Spara som utkast"</i> på bokför-ytan möjlig. Utan
    /// den här tabellen vore den knappen ett löfte modellen inte kan hålla.</para>
    /// </summary>
    [TableName("LedgerJournalEntryDraft")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerJournalEntryDraft
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>Null tills kassören fyllt i det. Obligatoriskt först vid bokföringen.</summary>
        public DateTime? AccountingDate { get; set; }

        public DateTime? EventDate { get; set; }

        public string Description { get; set; } = "";

        public int? CounterpartyType { get; set; }
        public int? CounterpartyId { get; set; }
        public string? CounterpartyName { get; set; }

        public string SourceType { get; set; } = LedgerSourceType.Manual;
        public int? SourceId { get; set; }
        public int? PaymentId { get; set; }

        public int CreatedByMemberId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        /// <summary>
        /// Sätts FÖRE bokföringen försöks; ett utkast med värde här vägrar bokföras igen.
        ///
        /// <para><b>⚠️ Claim-then-post, och riktningen på felet är hela skälet.</b> En krasch mellan
        /// anspråket och bokföringen lämnar ett utkast som sitter fast — irriterande, men går att
        /// reda ut. Motsatt ordning riskerar att samma post bokförs TVÅ gånger: pengar, tyst, och
        /// svårt att upptäcka i efterhand. Samma mönster och samma resonemang som
        /// <c>ScheduleReminderLog</c>.</para>
        /// </summary>
        public DateTime? CommitClaimedUtc { get; set; }

        /// <summary>Sätts efter lyckad bokföring, så en kvarglömd rad går att spåra till sin post.</summary>
        public int? PostedEntryId { get; set; }

        /// <summary>Utkastet är upptaget av ett bokföringsförsök som inte avslutats.</summary>
        [Ignore]
        public bool IsClaimed => CommitClaimedUtc is not null && PostedEntryId is null;
    }

    /// <summary>
    /// En rad i ett utkast.
    ///
    /// <para><b>⚠️ Rollen bevaras här, inte ett upplöst kontonummer.</b> Ändrar föreningen sin
    /// rollmappning mellan att utkastet sparas och att det bokförs ska posten följa den NYA
    /// mappningen — ett upplöst nummer hade fryst ett val kassören sedan ändrat.</para>
    ///
    /// <para><b>⚠️ Raden får vara tom och utkastet får vara obalanserat.</b> Det är vad ett utkast
    /// ÄR. Därför finns inga CHECK-constraints här, till skillnad från
    /// <see cref="LedgerJournalEntryLine"/> — kontrollerna hör till bokföringen.</para>
    /// </summary>
    [TableName("LedgerJournalEntryDraftLine")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerJournalEntryDraftLine
    {
        public int Id { get; set; }

        public int DraftId { get; set; }

        public int LineNumber { get; set; } = 1;

        /// <summary>Nyckel ur <see cref="LedgerAccountRoles"/>.</summary>
        public string? Role { get; set; }

        /// <summary>Uttryckligt konto, för den manuella bokför-ytan.</summary>
        public int? AccountNumber { get; set; }

        public decimal Debit { get; set; }

        public decimal Credit { get; set; }

        public string? Text { get; set; }

        public decimal? VatRate { get; set; }

        /// <summary>
        /// Projektet raden ska bokföras på. Se <see cref="LedgerProject"/>.
        ///
        /// <para>Bara id:t, ingen namnsnapshot. Ett utkast är mutabelt och läses alltid mot levande
        /// data, så en snapshot här hade kunnat hinna bli inaktuell innan posten ens bokförts.
        /// Namnet fryses först i <see cref="LedgerJournalEntryLine.ProjectName"/>, när raden
        /// skrivs.</para>
        /// </summary>
        public int? ProjectId { get; set; }
    }

    /// <summary>
    /// En dokumenterad lucka i en nummerserie.
    ///
    /// <para>Luckfrihet är ett lagkrav, men en förening som ansluter mitt i ett år bär med sig
    /// historik ur ett annat program — och där finns luckor vi varken orsakat eller kan fylla. Att
    /// låtsas att de inte finns är sämre än att förklara dem; BFNAR 2013:2 förutsätter att en lucka
    /// går att redogöra för.</para>
    ///
    /// <para><b>⚠️ Den här tabellen är TOM tills SIE-importen byggs, och det är meningen.</b> Den
    /// finns nu därför att den är gratis att ha innan historiken lästs in och besvärlig att införa
    /// efteråt.</para>
    /// </summary>
    [TableName("LedgerNumberGap")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerNumberGap
    {
        public int Id { get; set; }

        public int SeriesId { get; set; }

        /// <summary>Första numret i luckan.</summary>
        public int FromNumber { get; set; }

        /// <summary>Sista numret i luckan. Samma som <see cref="FromNumber"/> för en ensam lucka.</summary>
        public int ToNumber { get; set; }

        /// <summary>Varför luckan finns. Tom förklaring är samma sak som ingen förklaring.</summary>
        public string Explanation { get; set; } = "";

        public int RecordedByMemberId { get; set; }

        public DateTime RecordedUtc { get; set; }
    }
}
