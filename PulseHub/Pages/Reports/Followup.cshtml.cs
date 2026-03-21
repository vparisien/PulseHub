using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace PulseHub.Pages.Reports
{
    public class FollowupModel : PageModel
    {
        private readonly PulseHubContext _context;

        public FollowupModel(PulseHubContext context)
        {
            _context = context;
        }

        [BindProperty(SupportsGet = true)]
        public DateTime? StartDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? EndDate { get; set; }

        public List<FollowupRow> Rows { get; set; } = new();
        public int PendingCount => Rows.Count(r => r.ActionStatus == "Pending");
        public int ActionedCount => Rows.Count(r => r.ActionStatus == "Actioned");

        public async Task OnGetAsync()
        {
            StartDate ??= DateTime.Today.AddDays(-30);
            EndDate ??= DateTime.Today;

            var sql = @$"
                SELECT
                    r.ResponseID,
                    r.ResponseSessionID,
                    r.AssignedTo,
                    e.employeeName                  AS ManagerName,
                    e.jobTitleDescriptionEN         AS ManagerTitle,
                    s.StoreNumber,
                    s.TransactionDate,
                    s.Email                         AS CustomerEmail,
                    s.FirstName                     AS CustomerName,
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
                    ON CAST(e.employeeID AS VARCHAR) = r.AssignedTo
                    AND e.endDate = '9999-12-31'
                WHERE r.StatusID = 1
                  AND s.TransactionDate >= '{StartDate:yyyy-MM-dd}'
                  AND s.TransactionDate <= '{EndDate:yyyy-MM-dd} 23:59:59'
                ORDER BY r.RespondedAt ASC, s.TransactionDate DESC";

            Rows = await _context.Database
                .SqlQueryRaw<FollowupRow>(sql)
                .ToListAsync();
        }

        public class FollowupRow
        {
            public int ResponseID { get; set; }
            public int ResponseSessionID { get; set; }
            public string? AssignedTo { get; set; }
            public string? ManagerName { get; set; }
            public string? ManagerTitle { get; set; }
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
