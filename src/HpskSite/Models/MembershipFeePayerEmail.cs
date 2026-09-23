using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Vart en avgift ska mejlas, när det INTE är betalarens vanliga adress.
    ///
    /// <para><b>⚠️ EN mekanism för båda formerna</b> (Stefan 2026-09-23): kretsen behöver ett eget
    /// adressregister till klubbarna — kassörens adress är sällan klubbens allmänna kontaktadress, och
    /// "allmän förfrågan" och "betalningar" går ofta till olika personer. Klubben behöver samma sak för
    /// sina medlemmar (en junior vars förälder betalar). Utställare = kretsen eller klubben; betalare =
    /// klubben respektive medlemmen.</para>
    ///
    /// <para>Saknas en rad används den vanliga adressen: klubbens <c>contactEmail</c> respektive
    /// medlemmens e-post. Raden ändrar ALDRIG den vanliga adressen — den är någon annans uppgift.</para>
    /// </summary>
    [TableName("MembershipFeePayerEmail")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MembershipFeePayerEmail
    {
        public int Id { get; set; }

        /// <summary>0 = klubbens medlemsavgift, 1 = kretsavgiften (samma som MembershipFeeCharge).</summary>
        public int IssuerType { get; set; }

        /// <summary>Klubbens respektive kretsens nod-id.</summary>
        public int IssuerId { get; set; }

        /// <summary>Medlemmens id (klubbavgift) respektive klubbens nod-id (kretsavgift).</summary>
        public int PayerId { get; set; }

        public string Email { get; set; } = "";

        public DateTime UpdatedUtc { get; set; }

        public int UpdatedByMemberId { get; set; }
    }
}
