using HpskSite.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Common.Models;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Custom member login handler.
    ///
    /// Replaces Umbraco's built-in <c>UmbLoginController.HandleLogin</c> so we can show the member a
    /// clear, actionable, Swedish message for each sign-in outcome — in particular the lockout case,
    /// which the stock controller reports with the same generic "invalid credentials" message as a
    /// wrong password (leaving locked-out members with no idea why they can't get in).
    ///
    /// The actual sign-in call (<see cref="SignInManager{TUser}.PasswordSignInAsync(string,string,bool,bool)"/>)
    /// is identical to Umbraco's, so authentication behaviour (cookie, security stamp, 2FA, external
    /// logins) is unchanged — we only branch on the <see cref="SignInResult"/> to set the view state.
    /// The outcome is surfaced via ViewData["LoginErrorType"] which Views/Partials/Login.cshtml renders.
    /// </summary>
    public class LoginController : SurfaceController
    {
        private readonly SignInManager<MemberIdentityUser> _signInManager;
        private readonly IMemberManager _memberManager;
        private readonly ITwoFactorLoginService _twoFactorLoginService;
        private readonly EmailService _emailService;
        private readonly AppCaches _appCaches;

        public LoginController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            SignInManager<MemberIdentityUser> signInManager,
            IMemberManager memberManager,
            ITwoFactorLoginService twoFactorLoginService,
            EmailService emailService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _signInManager = signInManager;
            _memberManager = memberManager;
            _twoFactorLoginService = twoFactorLoginService;
            _emailService = emailService;
            _appCaches = appCaches;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> HandleLogin([Bind(Prefix = "loginModel")] LoginModel model)
        {
            if (!ModelState.IsValid)
            {
                // Missing email/password (e.g. JavaScript validation bypassed).
                ViewData["LoginErrorType"] = "missing";
                return CurrentUmbracoPage();
            }

            var result = await _signInManager.PasswordSignInAsync(
                model.Username, model.Password, model.RememberMe, lockoutOnFailure: true);

            if (result.Succeeded)
            {
                TempData["LoginSuccess"] = true;

                if (model.RedirectUrl is not null && Url.IsLocalUrl(model.RedirectUrl))
                {
                    return Redirect(model.RedirectUrl);
                }

                return RedirectToCurrentUmbracoPage();
            }

            // Keep the attempted email so the view can pre-fill the password-reset modal
            // (so a phone user doesn't have to retype it).
            ViewData["LoginAttemptedEmail"] = model.Username;

            // Look up the attempted member once (the username is the email address).
            var attemptedUser = await _memberManager.FindByNameAsync(model.Username)
                                ?? await _memberManager.FindByEmailAsync(model.Username);

            if (result.RequiresTwoFactor)
            {
                // Preserve Umbraco's stock two-factor flow: hand the enabled provider names to the view.
                if (attemptedUser is not null)
                {
                    var providerNames =
                        await _twoFactorLoginService.GetEnabledTwoFactorProviderNamesAsync(attemptedUser.Key);
                    ViewData.SetTwoFactorProviderNames(providerNames);
                }

                return CurrentUmbracoPage();
            }

            // "Locked now" covers both cases: the attempt that just tripped the lock (the result is
            // Failed but the member is now locked) and subsequent attempts while still locked
            // (result.IsLockedOut). See MemberIdentityComposer for the policy (10 attempts / 5 min).
            var lockedNow = result.IsLockedOut
                            || (attemptedUser is not null && await _memberManager.IsLockedOutAsync(attemptedUser));

            if (lockedNow)
            {
                ViewData["LoginErrorType"] = "locked";
                if (attemptedUser is not null)
                {
                    await TrySendLockoutEmailOnceAsync(attemptedUser);
                }
                return CurrentUmbracoPage();
            }

            if (result.IsNotAllowed)
            {
                // Member exists but isn't approved yet (Umbraco IsApproved = false).
                ViewData["LoginErrorType"] = "notallowed";
                return CurrentUmbracoPage();
            }

            // Wrong email or password.
            ViewData["LoginErrorType"] = "invalid";
            return CurrentUmbracoPage();
        }

        /// <summary>
        /// Send the "account locked" email at most once per lockout window. PasswordSignInAsync
        /// reports IsLockedOut on every attempt while locked, so we dedupe on a runtime-cache key
        /// whose lifetime exceeds the lockout span — otherwise the member would be emailed on every
        /// retry.
        /// </summary>
        private async Task TrySendLockoutEmailOnceAsync(MemberIdentityUser user)
        {
            var email = user.Email;
            if (string.IsNullOrEmpty(email))
            {
                return;
            }

            var cacheKey = $"lockout-email-sent:{user.Key}";
            if (_appCaches.RuntimeCache.Get(cacheKey) is not null)
            {
                return;
            }

            var displayName = string.IsNullOrWhiteSpace(user.Name) ? email : user.Name;

            await _emailService.SendAccountLockedEmailAsync(email, displayName);

            // Dedupe window: don't email again for an hour of retries. (Umbraco member lockout
            // itself lasts far longer — Security:MemberDefaultLockoutTimeInMinutes, 30 days by
            // default — so a re-notification after an hour is acceptable, not spammy.)
            _appCaches.RuntimeCache.Insert(cacheKey, () => (object)true, TimeSpan.FromHours(1));
        }
    }
}
