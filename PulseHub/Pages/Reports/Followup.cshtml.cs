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

        public List<FollowupSession> Sessions { get; set; } = new();
        public int PendingCount => Sessions.Count(s => s.ActionStatus == "Pending");
        public int ActionedCount => Sessions.Count(s => s.ActionStatus == "Actioned");

        // Dropdown lists for modal
        public List<PulseHub_Category> Categories { get; set; } = new();
        public List<PulseHub_SubCategory> SubCategories { get; set; } = new();
        public List<PulseHub_Department> Departments { get; set; } = new();

        public async Task OnGetAsync()
        {
            StartDate ??= DateTime.Today.AddDays(-30);
            EndDate ??= DateTime.Today;

            // Load dropdown data for modal
            Categories = await _context.PulseHub_Category.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.Category).ToListAsync();
            SubCategories = await _context.PulseHub_SubCategory.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.SubCategory).ToListAsync();
            Departments = await _context.PulseHub_Department.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.Department).ToListAsync();

            // Query grouped by session — one row per order number
            var sql = @$"
                SELECT
                    s.ResponseSessionID,
                    s.OrderNumber,
                    s.StoreNumber,
                    s.TransactionDate,
                    s.Email                                         AS CustomerEmail,
                    s.FirstName                                     AS CustomerName,
                    s.CategoryID,
                    s.SubCategoryID,
                    s.DepartmentID,
                    MAX(CAST(s.CuratorComment   AS NVARCHAR(MAX)))  AS CuratorComment,
                    MAX(CAST(s.ManagerComment   AS NVARCHAR(MAX)))  AS ManagerComment,
                    MAX(CAST(s.AssociateComment AS NVARCHAR(MAX)))  AS AssociateComment,
                    MAX(CAST(s.CustomerComment  AS NVARCHAR(MAX)))  AS CustomerComment,
                    s.Actionable,
                    r.AssignedTo,
                    e.employeeName                                  AS ManagerName,
                    e.jobTitleDescriptionEN                         AS ManagerTitle,
                    MIN(r.CuratedAt)                                AS CuratedAt,
                    MAX(r.RespondedAt)                              AS RespondedAt,
                    COUNT(r.ResponseID)                             AS FlaggedCount,
                    STRING_AGG(CAST(r.AnswerText AS NVARCHAR(MAX)), ' | ') AS AnswerText,
                    CASE
                        WHEN MAX(r.RespondedAt) IS NOT NULL THEN 'Actioned'
                        ELSE 'Pending'
                    END AS ActionStatus
                FROM PulseHub_ResponseSession s
                INNER JOIN PulseHub_Response r ON r.ResponseSessionID = s.ResponseSessionID AND r.StatusID = 1
                LEFT JOIN [LSCentral].[dbo].[vw_LSStoreEmployees] e
                    ON CAST(e.employeeID AS VARCHAR) = r.AssignedTo
                    AND e.endDate = '9999-12-31'
                WHERE s.TransactionDate >= '{StartDate:yyyy-MM-dd}'
                  AND s.TransactionDate <= '{EndDate:yyyy-MM-dd} 23:59:59'
                GROUP BY
                    s.ResponseSessionID, s.OrderNumber, s.StoreNumber, s.TransactionDate,
                    s.Email, s.FirstName, s.CategoryID, s.SubCategoryID, s.DepartmentID,
                    s.Actionable, r.AssignedTo, e.employeeName, e.jobTitleDescriptionEN
                ORDER BY MAX(r.RespondedAt) ASC, s.TransactionDate DESC";

            Sessions = await _context.Database
                .SqlQueryRaw<FollowupSession>(sql)
                .ToListAsync();
        }

        // ── Update session handler (same as CurateResponses) ─────────────
        public async Task<IActionResult> OnPostUpdateSessionAsync([FromBody] UpdateSessionRequest request)
        {
            var session = await _context.PulseHub_ResponseSession.FindAsync(request.ResponseSessionId);
            if (session == null) return NotFound();

            session.CategoryID = request.CategoryID;
            session.SubCategoryID = request.SubCategoryID;
            session.DepartmentID = request.DepartmentID;
            session.CuratorComment = request.CuratorComment;
            session.ManagerComment = request.ManagerComment;
            session.AssociateComment = request.AssociateComment;
            session.CustomerComment = request.CustomerComment;
            session.Actionable = request.Actionable;

            await _context.SaveChangesAsync();
            return new JsonResult(new { success = true });
        }

        // ── Mark as responded ─────────────────────────────────────────────
        public async Task<IActionResult> OnPostMarkRespondedAsync([FromBody] MarkRespondedRequest request)
        {
            var responses = await _context.PulseHub_Response
                .Where(r => r.ResponseSessionID == request.ResponseSessionId && r.StatusID == 1)
                .ToListAsync();

            foreach (var r in responses)
                r.RespondedAt = request.Responded ? DateTime.Now : null;

            await _context.SaveChangesAsync();
            return new JsonResult(new { success = true });
        }

        public class FollowupSession
        {
            public int ResponseSessionID { get; set; }
            public string? OrderNumber { get; set; }
            public string? StoreNumber { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? CustomerEmail { get; set; }
            public string? CustomerName { get; set; }
            public int? CategoryID { get; set; }
            public int? SubCategoryID { get; set; }
            public int? DepartmentID { get; set; }
            public string? CuratorComment { get; set; }
            public string? ManagerComment { get; set; }
            public string? AssociateComment { get; set; }
            public string? CustomerComment { get; set; }
            public bool? Actionable { get; set; }
            public string? AssignedTo { get; set; }
            public string? ManagerName { get; set; }
            public string? ManagerTitle { get; set; }
            public DateTime? CuratedAt { get; set; }
            public DateTime? RespondedAt { get; set; }
            public int FlaggedCount { get; set; }
            public string? AnswerText { get; set; }
            public string? ActionStatus { get; set; }
        }

        public class UpdateSessionRequest
        {
            public int ResponseSessionId { get; set; }
            public int? CategoryID { get; set; }
            public int? SubCategoryID { get; set; }
            public int? DepartmentID { get; set; }
            public string? CuratorComment { get; set; }
            public string? ManagerComment { get; set; }
            public string? AssociateComment { get; set; }
            public string? CustomerComment { get; set; }
            public bool Actionable { get; set; }
        }

        public class MarkRespondedRequest
        {
            public int ResponseSessionId { get; set; }
            public bool Responded { get; set; }
        }
    }
}
