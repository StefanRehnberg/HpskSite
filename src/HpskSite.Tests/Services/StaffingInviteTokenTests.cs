using System;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Omgång 3 (Expertgruppen K3): en överlämning gör svarslänkarna till den förra personen ogiltiga.</summary>
    public class StaffingInviteTokenTests
    {
        private static readonly IDataProtectionProvider P = new EphemeralDataProtectionProvider();

        [Fact]
        public void En_rad_som_aldrig_lamnats_over_far_en_v1_lank_som_fortsatter_galla()
        {
            var a = new StaffAssignment { Id = 42 };
            var t = StaffingInviteToken.Protect(P, a);
            var r = StaffingInviteToken.Read(P, t);
            r.Should().NotBeNull();
            r!.Value.Id.Should().Be(42);
            r.Value.Nonce.Should().BeNull();
            StaffingInviteToken.StillValid(r.Value.Nonce, a.InviteNonce).Should().BeTrue();
        }

        [Fact]
        public void Efter_en_overlamning_galler_bara_den_nya_lanken()
        {
            var a = new StaffAssignment { Id = 42 };
            var old = StaffingInviteToken.Read(P, StaffingInviteToken.Protect(P, a))!.Value;
            a.InviteNonce = Guid.NewGuid();
            StaffingInviteToken.StillValid(old.Nonce, a.InviteNonce).Should().BeFalse();
            var fresh = StaffingInviteToken.Read(P, StaffingInviteToken.Protect(P, a))!.Value;
            fresh.Id.Should().Be(42);
            StaffingInviteToken.StillValid(fresh.Nonce, a.InviteNonce).Should().BeTrue();
            a.InviteNonce = Guid.NewGuid();
            StaffingInviteToken.StillValid(fresh.Nonce, a.InviteNonce).Should().BeFalse();
        }

        [Fact]
        public void En_trasig_lank_ar_ingen_lank()
        {
            StaffingInviteToken.Read(P, "inte-en-token").Should().BeNull();
        }
    }
}
