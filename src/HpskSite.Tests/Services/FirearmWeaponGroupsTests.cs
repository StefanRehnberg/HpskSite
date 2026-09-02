using HpskSite.Models;
using HpskSite.Models.Firearms;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Vad vapenfältet får bära, och vad väljarna erbjuder.
    ///
    /// <para><b>Varför reglerna är värda test:</b> listan matade tidigare bara
    /// <c>Enum.GetNames&lt;WeaponClass&gt;()</c>, så ett magnumvapen kunde bara beskrivas som
    /// gruppen <c>"M"</c> — och M1–M9 är inte kompetensnivåer utan <b>olika vapen</b> (SA respektive
    /// DA revolver 41-44, 357, fri 9mm). Rapporterat 2026-09-02.</para>
    /// </summary>
    public class FirearmWeaponGroupsTests
    {
        // ── Grupperna finns kvar ─────────────────────────────────────────────────────────────
        // Magnumtillägget får inte ha trängt ut något. A_Opt är en egen grupp (optiksikte är inte
        // samma tävling som öppet sikte) och måste vara valbar.

        [Fact]
        public void Options_InnehallerVarjeVapengrupp()
        {
            var values = FirearmWeaponGroups.Options.Select(o => o.Value).ToList();

            foreach (var name in Enum.GetNames<WeaponClass>())
            {
                Assert.Contains(name, values);
            }
        }

        [Fact]
        public void Options_HarIngaDubbletter()
        {
            var values = FirearmWeaponGroups.Options.Select(o => o.Value).ToList();
            Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        }

        // ── Magnumklasserna ──────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("M1")]
        [InlineData("M2")]
        [InlineData("M3")]
        [InlineData("M9")]
        public void Options_InnehallerMagnumklasserna(string id)
        {
            Assert.Contains(id, FirearmWeaponGroups.Options.Select(o => o.Value));
        }

        /// <summary>
        /// ⚠️ Strukturellt svep: VARJE klass i registret med vapengrupp M ska erbjudas. Byggs
        /// listan för hand i stället tystnar en framtida magnumklass i stället för att märkas.
        /// </summary>
        [Fact]
        public void Options_TackerVARJEMagnumklassIRegistret()
        {
            var offered = FirearmWeaponGroups.Options.Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
            var magnum = ShootingClasses.All.Where(c => c.Weapon == WeaponClass.M).ToList();

            // Kontrollprov: finns det inga magnumklasser alls mäter svepet ingenting.
            Assert.NotEmpty(magnum);
            foreach (var sc in magnum)
            {
                Assert.Contains(sc.Id, offered);
            }
        }

        [Fact]
        public void Options_MagnumEtikettenBarBeskrivningen()
        {
            // Koden ensam säger inte vilket vapen det är — det är hela skälet klasserna finns i
            // listan. "M2" utan "DA Revolver 41-44 Magnum" är ett val användaren inte kan göra.
            var m2 = FirearmWeaponGroups.Options.Single(o => o.Value == "M2");

            Assert.Contains("M2", m2.Label);
            Assert.Contains("Revolver", m2.Label);
            Assert.NotEqual("M2", m2.Label);
        }

        [Fact]
        public void Options_GruppkodernasEtiketterArKodenSjalv()
        {
            // Kontrollprov åt andra hållet: en beskrivning på "C" vore fel — gruppkoden ÄR namnet.
            Assert.Equal("C", FirearmWeaponGroups.Options.Single(o => o.Value == "C").Label);
        }

        // ── IsValid ──────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("C")]
        [InlineData("A_Opt")]
        [InlineData("M")]
        [InlineData("M2")]
        [InlineData("M9")]
        public void IsValid_GodtarGrupperOchMagnumklasser(string v)
        {
            Assert.True(FirearmWeaponGroups.IsValid(v));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsValid_TomtArTillatet(string? v)
        {
            // Vapengruppen är frivillig — aliaset är det enda obligatoriska fältet.
            Assert.True(FirearmWeaponGroups.IsValid(v));
        }

        [Theory]
        [InlineData("C1")]
        [InlineData("A_opt_1")]
        [InlineData("C_Vet_Y")]
        public void IsValid_AVVISAR_kompetensnivaer(string v)
        {
            // ⚠️ MEDVETET. C1/C2/C3 är samma pistol och olika SKYTT — nivån ändras när skytten
            // avancerar, vapnet gör det inte. Att släppa in dem i ett vapenfält vore ett
            // kategorifel, och magnum är undantaget just för att M1/M2 verkligen är olika vapen.
            Assert.False(FirearmWeaponGroups.IsValid(v));
        }

        [Theory]
        [InlineData("Z9")]
        [InlineData("hittepa")]
        [InlineData("m2 ")]   // efterföljande blanksteg trimmas — men gemener gör det inte
        public void IsValid_AvvisarOkantVarde(string v)
        {
            // Skiftläget är signifikant: värdet lagras och jämförs Ordinal på andra ytor.
            Assert.Equal(v.Trim() == "M2", FirearmWeaponGroups.IsValid(v));
        }

        // ── GroupCodeOf ──────────────────────────────────────────────────────────────────────
        // ⚠️ Det här är vad som håller kopplingen till MemberActivityEntry.WeaponGroups hel.
        // Jämförs Firearm.WeaponClass literalt mot en gruppkod slutar magnumvapnen matcha, tyst.

        [Theory]
        [InlineData("M1", "M")]
        [InlineData("M2", "M")]
        [InlineData("M9", "M")]
        public void GroupCodeOf_MagnumklassGerGruppen(string stored, string expected)
        {
            Assert.Equal(expected, FirearmWeaponGroups.GroupCodeOf(stored));
        }

        [Theory]
        [InlineData("C")]
        [InlineData("M")]
        [InlineData("A_Opt")]
        public void GroupCodeOf_GruppkodArSigSjalv(string v)
        {
            // ⚠️ Kontrolleras FÖRST i implementationen: GetWeaponClassCode svarar TOMT på en ren
            // gruppkod (den slår upp klasser, inte grupper), så utan den ordningen skulle "C" bli "".
            Assert.Equal(v, FirearmWeaponGroups.GroupCodeOf(v));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void GroupCodeOf_TomtGerTomt(string? v)
        {
            Assert.Equal("", FirearmWeaponGroups.GroupCodeOf(v));
        }

        [Fact]
        public void GroupCodeOf_OkantVardeBehallsOforandrat()
        {
            // Ett värde vi inte känner igen ska gruppera med SIG SJÄLV, inte försvinna — samma
            // regel som ShootingClasses.ToCanonicalName.
            Assert.Equal("Zz9", FirearmWeaponGroups.GroupCodeOf(" Zz9 "));
        }

        [Fact]
        public void GroupCodeOf_VarjeErbjudetVardeGerEnKandGrupp()
        {
            // Strukturellt: allt väljaren erbjuder måste kunna resolvas till en riktig vapengrupp,
            // annars kan ett sparat värde aldrig kopplas till aktivitet i den gruppen.
            foreach (var o in FirearmWeaponGroups.Options)
            {
                var group = FirearmWeaponGroups.GroupCodeOf(o.Value);
                Assert.True(Enum.TryParse<WeaponClass>(group, out _),
                            $"{o.Value} gav gruppen '{group}', som inte finns i WeaponClass");
            }
        }
    }
}
