using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace PulseHub.Pages.Reports
{
    public class RecognitionModel : PageModel
    {
        private readonly PulseHubContext _context;

        public RecognitionModel(PulseHubContext context)
        {
            _context = context;
        }

        [BindProperty(SupportsGet = true)]
        public DateTime? StartDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? EndDate { get; set; }

        public List<RecognitionRow> Rows { get; set; } = new();

        public async Task OnGetAsync()
        {
            StartDate ??= DateTime.Today.AddDays(-30);
            EndDate ??= DateTime.Today;

            var sql = @$"
                SELECT
                    r.ResponseID,
                    r.ResponseSessionID,
                    r.RecognitionOf                 AS EmployeeID,
                    e.employeeName                  AS EmployeeName,
                    e.storeID                       AS EmployeeStoreID,
                    e.jobTitleDescriptionEN         AS JobTitle,
                    s.StoreNumber,
                    s.TransactionDate,
                    s.Email                         AS CustomerEmail,
                    s.FirstName                     AS CustomerName,
                    r.AnswerText
                FROM PulseHub_Response r
                INNER JOIN PulseHub_ResponseSession s ON r.ResponseSessionID = s.ResponseSessionID
                LEFT JOIN [LSCentral].[dbo].[vw_LSStoreEmployees] e
                    ON CAST(e.employeeID AS VARCHAR) = CAST(r.RecognitionOf AS VARCHAR)
                    AND e.endDate = '9999-12-31'
                WHERE r.StatusID = 2
                  AND s.TransactionDate >= '{StartDate:yyyy-MM-dd}'
                  AND s.TransactionDate <= '{EndDate:yyyy-MM-dd} 23:59:59'
                ORDER BY s.TransactionDate DESC";

            Rows = await _context.Database
                .SqlQueryRaw<RecognitionRow>(sql)
                .ToListAsync();
        }

        public class RecognitionRow
        {
            public int ResponseID { get; set; }
            public int ResponseSessionID { get; set; }
            public string? EmployeeID { get; set; }
            public string? EmployeeName { get; set; }
            public string? EmployeeStoreID { get; set; }
            public string? JobTitle { get; set; }
            public string? StoreNumber { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? CustomerEmail { get; set; }
            public string? CustomerName { get; set; }
            public string? AnswerText { get; set; }
        }
    }
}
