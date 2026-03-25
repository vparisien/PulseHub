using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PulseHub.Pages
{
    /// <summary>
    /// Base class for pages that require an authenticated session.
    /// Inherit from this instead of PageModel to enforce login redirect.
    /// </summary>
    public abstract class AuthenticatedPageModel : PageModel
    {
        public string? CurrentUsername => HttpContext.Session.GetString("Username");
        public string? CurrentDisplayName => HttpContext.Session.GetString("DisplayName");

        public override void OnPageHandlerExecuting(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context)
        {
            if (string.IsNullOrEmpty(HttpContext.Session.GetString("Username")))
            {
                context.Result = new RedirectToPageResult("/Login", new { returnUrl = Request.Path });
                return;
            }
            base.OnPageHandlerExecuting(context);
        }
    }
}
