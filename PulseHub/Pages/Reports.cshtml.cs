using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace PulseHub.Pages
{
    public class ReportsModel : PageModel
    {
        private readonly PulseHubContext _context;

        public ReportsModel(PulseHubContext context)
        {
            _context = context;
        }

        // --- Filters ---
        [BindProperty(SupportsGet = true)]
        public DateTime? StartDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? EndDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public string ActiveReport { get; set; } = "recognition";

        // --- Recognition Report ---
        public List<RecognitionRow> RecognitionRows { get; set; } = new();

        // --- Followup Report ---
        public List<FollowupRow> FollowupRows { get; set; } = new();

        public async Task OnGetAsync()
        {
            // Default date range: last 30 days
            StartDate ??= DateTime.Today.AddDays(-30);
            EndDate ??= DateTime.Today;

            if (ActiveReport == "recognition")
                await LoadRecognitionReportAsync();
            else if (ActiveReport == "followup")
                await LoadFollowupReportAsync();
        }

        private async Task LoadRecognitionReportAsync()
        {
            var sql = @$"
                SELECT
                    r.ResponseID,
                    r.ResponseSessionID,
                    r.RecognitionOf        AS EmployeeID,
                    e.EmployeeName,
                    e.StoreName,
                    s.StoreNumber,
                    s.TransactionDate,
                    s.Email                AS CustomerEmail,
                    s.FirstName            AS CustomerName,
                    r.AnswerText
                FROM PulseHub_Response r
                INNER JOIN PulseHub_ResponseSession s ON r.ResponseSessionID = s.ResponseSessionID
                LEFT JOIN [LSCentral].[dbo].[vw_LSStoreEmployees] e
                    ON CAST(e.EmployeeID AS VARCHAR) = CAST(r.RecognitionOf AS VARCHAR)
                WHERE r.StatusID = 2
                  AND s.TransactionDate >= '{StartDate:yyyy-MM-dd}'
                  AND s.TransactionDate <= '{EndDate:yyyy-MM-dd} 23:59:59'
                ORDER BY s.TransactionDate DESC";

            RecognitionRows = await _context.Database
                .SqlQueryRaw<RecognitionRow>(sql)
                .ToListAsync();
        }

        private async Task LoadFollowupReportAsync()
        {
            var sql = @$"
                SELECT
                    r.ResponseID,
                    r.ResponseSessionID,
                    r.AssignedTo,
                    e.EmployeeName         AS ManagerName,
                    s.StoreNumber,
                    s.TransactionDate,
                    s.Email                AS CustomerEmail,
                    s.FirstName            AS CustomerName,
                    r.AnswerText,
                    r.CuratedAt,
                    r.RespondedAt,
                    CASE
                        WHEN r.RespondedAt IS NOT NULL THEN 'Actioned'
                        ELSE 'Pending'
                    END AS ActionStatus
                FROM PulseHub_Response r
                INNER JOIN PulseHub_ResponseSession s ON r.ResponseSessionID = s.ResponseSessionID
                LEFT JOIN [LSCentral].[dbo].[vw_LSStoreEmployees] e
                    ON CAST(e.EmployeeID AS VARCHAR) = r.AssignedTo
                WHERE r.StatusID = 1
                  AND s.TransactionDate >= '{StartDate:yyyy-MM-dd}'
                  AND s.TransactionDate <= '{EndDate:yyyy-MM-dd} 23:59:59'
                ORDER BY r.RespondedAt ASC, s.TransactionDate DESC";

            FollowupRows = await _context.Database
                .SqlQueryRaw<FollowupRow>(sql)
                .ToListAsync();
        }

        // --- Result DTOs ---

        public class RecognitionRow
        {
            public int ResponseID { get; set; }
            public int ResponseSessionID { get; set; }
            public string? EmployeeID { get; set; }
            public string? EmployeeName { get; set; }
            public string? StoreName { get; set; }
            public string? StoreNumber { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? CustomerEmail { get; set; }
            public string? CustomerName { get; set; }
            public string? AnswerText { get; set; }
        }

        public class FollowupRow
        {
            public int ResponseID { get; set; }
            public int ResponseSessionID { get; set; }
            public string? AssignedTo { get; set; }
            public string? ManagerName { get; set; }
            public string? StoreNumber { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? CustomerEmail { get; set; }
            public string? CustomerName { get; set; }
            public string? AnswerText { get; set; }
            public DateTime? CuratedAt { get; set; }
            public DateTime? RespondedAt { get; set; }
            public string? ActionStatus { get; set; }
        }
    }
}
