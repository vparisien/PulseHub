using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Novell.Directory.Ldap;

namespace PulseHub.Pages
{
    public class LoginModel : PageModel
    {
        [BindProperty]
        public string Username { get; set; } = string.Empty;

        [BindProperty]
        public string Password { get; set; } = string.Empty;

        [BindProperty(SupportsGet = true)]
        public string? ReturnUrl { get; set; }

        public string? ErrorMessage { get; set; }

        private const string LdapServer = "LSDC02.lauracanada.ca";
        private const int LdapPort = 389;
        private const string DomainSuffix = "laura.ca";
        private const string NetbiosDomain = "LAURACANADA";

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
            {
                ErrorMessage = "Please enter your username and password.";
                return Page();
            }

            bool isValid = await ValidateDomainUserAsync(Username, Password);

            if (isValid)
            {
                HttpContext.Session.SetString("Username", Username.ToLower());
                // TODO: Query AD for display name / manager group membership here
                HttpContext.Session.SetString("DisplayName", Username);

                var redirect = ReturnUrl;
                if (!string.IsNullOrEmpty(redirect) && Url.IsLocalUrl(redirect))
                    return Redirect(redirect);

                return RedirectToPage("/Index");
            }

            ErrorMessage = "Invalid domain username or password.";
            return Page();
        }

        private async Task<bool> ValidateDomainUserAsync(string username, string password)
        {
            try
            {
                using var ldapConnection = new LdapConnection();
                await ldapConnection.ConnectAsync(LdapServer, LdapPort);

                // Try UPN format: user@laura.ca
                try
                {
                    await ldapConnection.BindAsync($"{username}@{DomainSuffix}", password);
                    if (ldapConnection.Bound)
                        return true;
                }
                catch { /* try next */ }

                // Try DOMAIN\username format
                try
                {
                    await ldapConnection.BindAsync($"{NetbiosDomain}\\{username}", password);
                    if (ldapConnection.Bound)
                        return true;
                }
                catch { /* both failed */ }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
