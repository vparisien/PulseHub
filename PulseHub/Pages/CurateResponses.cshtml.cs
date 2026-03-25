using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace PulseHub
{
    public class CurateResponsesPivotModel : PageModel
    {
        private readonly PulseHubContext _context;
        private readonly IConfiguration _config;

        public CurateResponsesPivotModel(PulseHubContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        [BindProperty]
        public DateTime? StartDate { get; set; }

        [BindProperty]
        public DateTime? EndDate { get; set; }

        public List<PivotedResponse> PivotedResponses { get; set; } = new();
        public List<string> Questions { get; set; } = new();

        // Dropdown Lists
        public List<PulseHub_Category> Categories { get; set; } = new();
        public List<PulseHub_SubCategory> SubCategories { get; set; } = new();
        public List<PulseHub_Department> Departments { get; set; } = new();

        public async Task OnGetAsync() => await LoadPivotedResponsesAsync();

        public async Task OnPostFilterAsync() => await LoadPivotedResponsesAsync();

        public async Task<IActionResult> OnPostUpdateStatusAsync([FromBody] UpdateStatusRequest request)
        {
            var response = await _context.PulseHub_Response
                .Include(r => r.ResponseSession)
                .FirstOrDefaultAsync(r => r.ResponseID == request.ResponseId);
            if (response == null) return NotFound();

            response.StatusID = request.StatusId;
            response.CuratedAt = DateTime.Now;

            string? assignedTo = null;
            RecognitionDebugInfo? recognitionDebug = null;

            // ── Red flag (StatusID = 1): assign to store manager ──────────────
            if (request.StatusId == 1 && response.ResponseSession != null)
            {
                var storeNumber = response.ResponseSession.StoreNumber;
                if (!string.IsNullOrEmpty(storeNumber) && storeNumber != "WEB")
                {
                    var storeId = storeNumber.Trim().TrimStart('0');
                    var managerSql = $@"
                        SELECT TOP 1 StoreManagerID
                        FROM [LSCentral].[dbo].[vw_LSStoreMgmtInfo]
                        WHERE CAST(StoreID AS VARCHAR) = '{storeId}'";

                    var result = await _context.Database
                        .SqlQueryRaw<StoreManagerResult>(managerSql)
                        .ToListAsync();

                    var managerId = result.FirstOrDefault()?.StoreManagerID;
                    if (managerId.HasValue)
                    {
                        response.AssignedTo = managerId.Value.ToString();
                        assignedTo = response.AssignedTo;
                    }
                }
            }

            // ── Green flag (StatusID = 2): look up recognized associate ───────
            else if (request.StatusId == 2 && response.ResponseSession != null)
            {
                var orderNumber = response.ResponseSession.OrderNumber;
                var transDate = response.ResponseSession.TransactionDate;

                recognitionDebug = new RecognitionDebugInfo { OrderNumber = orderNumber };

                if (!string.IsNullOrEmpty(orderNumber))
                {
                    var (strippedId, rawId, error) = await LookupShopifyAssociateAsync(
                        orderNumber,
                        transDate.AddMonths(-3),
                        transDate.AddMonths(3));

                    recognitionDebug.ShopifyRawId = rawId;
                    recognitionDebug.AssocIdStripped = strippedId;
                    recognitionDebug.ShopifyError = error;

                    if (!string.IsNullOrEmpty(strippedId))
                    {
                        response.RecognitionOf = strippedId;

                        // Look up employee name for debug display
                        try
                        {
                            var empSql = $@"
                                SELECT TOP 1
                                    CAST(employeeID AS VARCHAR)  AS EmployeeID,
                                    employeeName                 AS EmployeeName,
                                    jobTitleDescriptionEN        AS JobTitle
                                FROM [LSCentral].[dbo].[vw_LSStoreEmployees]
                                WHERE CAST(employeeID AS VARCHAR) = '{strippedId}'
                                  AND endDate = '9999-12-31'";

                            var empRows = await _context.Database
                                .SqlQueryRaw<EmployeeLookupResult>(empSql)
                                .ToListAsync();

                            var emp = empRows.FirstOrDefault();
                            recognitionDebug.EmployeeName = emp?.EmployeeName ?? "(not found in LSCentral)";
                            recognitionDebug.JobTitle = emp?.JobTitle;
                            recognitionDebug.EmployeeFound = emp != null;
                        }
                        catch (Exception ex)
                        {
                            recognitionDebug.EmployeeName = $"LSCentral error: {ex.Message}";
                        }
                    }
                    else
                    {
                        recognitionDebug.Note = string.IsNullOrEmpty(error)
                            ? "No Shopify match found for this order number"
                            : $"Shopify error: {error}";
                    }
                }
                else
                {
                    recognitionDebug.Note = "No order number on this session";
                }
            }

            // ── Clearing status: remove assignments ───────────────────────────
            else if (request.StatusId != 1)
            {
                response.AssignedTo = null;
            }

            await _context.SaveChangesAsync();

            return new JsonResult(new
            {
                success = true,
                assignedTo,
                recognitionOf = response.RecognitionOf,
                recognitionDebug
            });
        }

        // ── Shopify single-order lookup (LSInterfaceDB) ─────────────────────
        private async Task<(string? stripped, string? raw, string? error)> LookupShopifyAssociateAsync(
            string orderNumber, DateTime minDate, DateTime maxDate)
        {
            try
            {
                var connStr = _config.GetConnectionString("LSInterfaceDB")!;
                var safeOrder = orderNumber.Replace("'", "''");

                var sql = @$"
                    SELECT TOP 1
                        b.associateId,
                        REVERSE(SUBSTRING(
                            REVERSE(CAST(TRY_CAST(b.associateId AS BIGINT) AS VARCHAR(20))),
                            PATINDEX('%[^0]%', REVERSE(CAST(TRY_CAST(b.associateId AS BIGINT) AS VARCHAR(20)))),
                            20
                        )) AS associateIdStripped
                    FROM [LSShopify].[dbo].[shopifyOrder] a
                    INNER JOIN [LSShopify].[dbo].[shopifyOrderline] b ON a.OrderID = b.OrderID
                    WHERE a.orderName = '{safeOrder}'
                      AND b.associateId IS NOT NULL
                      AND b.associateId <> ''
                      AND TRY_CAST(b.associateId AS BIGINT) > 0
                      AND a.CreatedAt BETWEEN '{minDate:yyyy-MM-dd}' AND '{maxDate:yyyy-MM-dd}'";

                using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var raw = reader["associateId"]?.ToString();
                    var stripped = reader["associateIdStripped"]?.ToString();
                    return (stripped, raw, null);
                }
                return (null, null, null);
            }
            catch (Exception ex)
            {
                return (null, null, ex.Message);
            }
        }

        private class StoreManagerResult  { public int? StoreManagerID { get; set; } }
        private class EmployeeLookupResult
        {
            public string? EmployeeID { get; set; }
            public string? EmployeeName { get; set; }
            public string? JobTitle { get; set; }
        }

        public class RecognitionDebugInfo
        {
            public string? OrderNumber { get; set; }
            public string? ShopifyRawId { get; set; }
            public string? AssocIdStripped { get; set; }
            public string? ShopifyError { get; set; }
            public bool EmployeeFound { get; set; }
            public string? EmployeeName { get; set; }
            public string? JobTitle { get; set; }
            public string? Note { get; set; }
        }

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

        private async Task LoadPivotedResponsesAsync()
        {
            // 1. Fetch Dropdown Data
            Categories    = await _context.PulseHub_Category.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.Category).ToListAsync();
            SubCategories = await _context.PulseHub_SubCategory.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.SubCategory).ToListAsync();
            Departments   = await _context.PulseHub_Department.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.Department).ToListAsync();

            // 2. Get question mappings
            var questionMappings = await _context.PulseHub_Question.AsNoTracking().ToListAsync();

            var lookup = questionMappings
                .Where(q => q.GroupID.HasValue && !string.IsNullOrEmpty(q.Question))
                .GroupBy(q => q.GroupID)
                .SelectMany(g => {
                    var header = g.FirstOrDefault(x => x.Language == "ENG")?.Question ?? g.First().Question;
                    return g.Select(x => new { Raw = x.Question, Canonical = header });
                }).ToDictionary(x => x.Raw, x => x.Canonical);

            // 3. Raw SQL
            var rawSql = @"
                SELECT
                    r.ResponseID,
                    r.ResponseSessionID,
                    r.QuestionText,
                    r.AnswerText,
                    r.StatusID,
                    r.RecognitionOf,
                    s.TransactionDate,
                    s.Email,
                    s.FirstName,
                    s.Language,
                    s.OrderNumber,
                    s.StoreNumber,
                    s.CategoryID,
                    s.SubCategoryID,
                    s.DepartmentID,
                    s.CuratorComment,
                    s.ManagerComment,
                    s.AssociateComment,
                    s.CustomerComment,
                    s.Actionable
                FROM PulseHub_Response r
                INNER JOIN PulseHub_ResponseSession s ON r.ResponseSessionID = s.ResponseSessionID";

            var rawData = await _context.Database.SqlQueryRaw<RawFlatResponse>(rawSql).ToListAsync();

            // 4. Filter and Pivot
            var filtered = rawData.Where(r =>
                (!StartDate.HasValue || r.TransactionDate.Date >= StartDate.Value.Date) &&
                (!EndDate.HasValue   || r.TransactionDate.Date <= EndDate.Value.Date))
                .ToList();

            Questions = filtered
                .Select(r => lookup.ContainsKey(r.QuestionText ?? "") ? lookup[r.QuestionText!] : r.QuestionText ?? "")
                .Where(q => !string.IsNullOrEmpty(q))
                .Distinct().OrderBy(q => q).ToList();

            PivotedResponses = filtered.GroupBy(r => r.ResponseSessionID)
                .Select(g => {
                    var f = g.First();
                    return new PivotedResponse
                    {
                        ResponseSessionID = f.ResponseSessionID,
                        TransactionDate   = f.TransactionDate,
                        Email             = f.Email,
                        FirstName         = f.FirstName,
                        Language          = f.Language,
                        OrderNumber       = f.OrderNumber,
                        StoreNumber       = f.StoreNumber,
                        CategoryID        = f.CategoryID,
                        SubCategoryID     = f.SubCategoryID,
                        DepartmentID      = f.DepartmentID,
                        CuratorComment    = f.CuratorComment,
                        ManagerComment    = f.ManagerComment,
                        AssociateComment  = f.AssociateComment,
                        CustomerComment   = f.CustomerComment,
                        Actionable        = f.Actionable,
                        AnswersByQuestion = Questions.ToDictionary(
                            q => q,
                            q => g.Where(r => (lookup.ContainsKey(r.QuestionText ?? "") ? lookup[r.QuestionText!] : r.QuestionText ?? "") == q)
                                  .Select(r => new ResponseData
                                  {
                                      ResponseID   = r.ResponseID,
                                      AnswerText   = r.AnswerText,
                                      StatusID     = r.StatusID,
                                      RecognitionOf = r.RecognitionOf
                                  }).FirstOrDefault()
                        )
                    };
                }).OrderByDescending(r => r.TransactionDate).ToList();
        }

        // ── View Models ─────────────────────────────────────────────────────────

        public class PivotedResponse
        {
            public int ResponseSessionID { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? Language { get; set; }
            public string? OrderNumber { get; set; }
            public string? StoreNumber { get; set; }
            public int? CategoryID { get; set; }
            public int? SubCategoryID { get; set; }
            public int? DepartmentID { get; set; }
            public string? CuratorComment { get; set; }
            public string? ManagerComment { get; set; }
            public string? AssociateComment { get; set; }
            public string? CustomerComment { get; set; }
            public bool? Actionable { get; set; }
            public Dictionary<string, ResponseData?> AnswersByQuestion { get; set; } = new();
        }

        public class ResponseData
        {
            public int ResponseID { get; set; }
            public string? AnswerText { get; set; }
            public int? StatusID { get; set; }
            public string? RecognitionOf { get; set; }
        }

        public class UpdateStatusRequest
        {
            public int ResponseId { get; set; }
            public int StatusId { get; set; }
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

        public class RawFlatResponse
        {
            public int ResponseID { get; set; }
            public int ResponseSessionID { get; set; }
            public string? QuestionText { get; set; }
            public string? AnswerText { get; set; }
            public int? StatusID { get; set; }
            public string? RecognitionOf { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? Language { get; set; }
            public string? OrderNumber { get; set; }
            public string? StoreNumber { get; set; }
            public int? CategoryID { get; set; }
            public int? SubCategoryID { get; set; }
            public int? DepartmentID { get; set; }
            public string? CuratorComment { get; set; }
            public string? ManagerComment { get; set; }
            public string? AssociateComment { get; set; }
            public string? CustomerComment { get; set; }
            public bool? Actionable { get; set; }
        }
    }
}
