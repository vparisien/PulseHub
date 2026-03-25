using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PulseHub.Pages
{
    public class IndexModel : AuthenticatedPageModel
    {
        // Permission properties
        public bool CanImportSurveys { get; private set; }
        public bool CanCurateResponses { get; private set; }
        public bool CanNotifyManagers { get; private set; }
        public bool CanViewReports { get; private set; }
        public bool CanAccessAdmin { get; private set; }
        public bool CanAccessManagerView { get; private set; }

        public void OnGet()
        {
            // TODO: Replace this with real user permission logic
            var userRole = User.IsInRole("Admin") ? "Admin" : "Admin";

            CanImportSurveys = userRole == "Admin" || userRole == "Manager";
            CanCurateResponses = userRole == "Admin" || userRole == "Manager";
            CanNotifyManagers = userRole == "Admin" || userRole == "Manager";
            CanViewReports = userRole == "Admin" || userRole == "Manager" || userRole == "Analyst";
            CanAccessAdmin = userRole == "Admin";
            // TODO: Restrict to managers only once login/identity is implemented
            CanAccessManagerView = true;
        }
    }
}