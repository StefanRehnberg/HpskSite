using System.Reflection;
using System.Runtime.CompilerServices;
using HpskSite.Models;
using HpskSite.Models.Registrations;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// En klassanmälan (ShootingClassEntry) skrivs om vid varje sparning (DELETE + INSERT av klassraderna) och ersätts hela vid omanmälan.
    /// Ett fält som någon kopierar för hand försvinner därför tyst. Här fäller bygget varje fält som inte bärs av Copy, av Clone och av tabellens
    /// läs- och skrivväg, så nästa fält (kompisen i patrullen) inte kan tappas utan att något blir rött.
    /// </summary>
    public class ShootingClassEntryCopyTests
    {
        static readonly PropertyInfo[] Props = typeof(ShootingClassEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        static string SourceDir([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;
        static string AppSource(params string[] parts) => File.ReadAllText(Path.GetFullPath(Path.Combine(new[] { SourceDir(), "..", "HpskSite" }.Concat(parts).ToArray())));

        static ShootingClassEntry Filled()
        {
            var e = new ShootingClassEntry();
            foreach (var p in Props)
            {
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                p.SetValue(e, t == typeof(string) ? "värde-" + p.Name : t == typeof(int) ? 7 + p.Name.Length : t == typeof(bool) ? true : throw new NotSupportedException("Lägg till " + t + " i testet"));
            }
            return e;
        }

        [Fact]
        public void Copy_carries_every_property()
        {
            var src = Filled();
            var copy = src.Copy();
            Assert.NotSame(src, copy);
            foreach (var p in Props) Assert.True(Equals(p.GetValue(src), p.GetValue(copy)), $"Copy() tappar {p.Name}");
        }

        [Fact]
        public void Registration_clone_carries_every_class_property()
        {
            var r = new RegistrationRecord { Classes = new List<ShootingClassEntry> { Filled() } };
            var clone = r.Clone();
            foreach (var p in Props) Assert.True(Equals(p.GetValue(r.Classes[0]), p.GetValue(clone.Classes[0])), $"RegistrationRecord.Clone tappar {p.Name}");
        }

        [Fact]
        public void The_registration_table_reads_and_writes_every_class_property()
        {
            // Tabellen skriver med en handskriven INSERT/SELECT; varje fält ska nämnas där (kolumnnamnet bär fältets namn, t.ex. ClassKey för Class).
            var sql = AppSource("Services", "Registrations", "SqlRegistrationStore.cs");
            var column = new Dictionary<string, string> { ["Class"] = "ClassKey" };
            foreach (var p in Props)
            {
                var name = column.TryGetValue(p.Name, out var c) ? c : p.Name;
                Assert.True(sql.Contains("c." + p.Name) || sql.Contains(name), $"SqlRegistrationStore nämner inte {p.Name}: fältet skulle försvinna vid nästa sparning");
            }
        }
    }
}
