using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace PulseHub.Pages.Reports
{
    public class RecognitionModel : PageModel
    {
        private readonly PulseHubContext _context;
        private readonly IConfiguration _config;

        public RecognitionModel(PulseHubContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
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

            // Step 1 — pull recognition responses from LSDEV (PulseHub tables)
            var sql = @$"
                SELECT
                    r.ResponseID,
                    r.ResponseSessionID,
                    s.StoreNumber,
                    s.TransactionDate,
                    s.OrderNumber,
                    s.Email         AS CustomerEmail,
                    s.FirstName     AS CustomerName,
                    r.AnswerText
                FROM PulseHub_Response r
                INNER JOIN PulseHub_ResponseSession s ON r.ResponseSessionID = s.ResponseSessionID
                WHERE r.StatusID = 2
                  AND s.TransactionDate >= '{StartDate:yyyy-MM-dd}'
                  AND s.TransactionDate <= '{EndDate:yyyy-MM-dd} 23:59:59'
                ORDER BY s.TransactionDate DESC";

            var baseRows = await _context.Database
                .SqlQueryRaw<RecognitionBaseRow>(sql)
                .ToListAsync();

            if (!baseRows.Any())
            {
                Rows = new List<RecognitionRow>();
                return;
            }

            // Step 2 — batch lookup associateId from LSShopify (LSInterfaceDB)
            // associateId has trailing zeros (e.g. 6141500000 represents employee 61415)
            // The 3-month window guards against stale/unrelated orders with the same number
            var orderNumbers = baseRows
                .Select(r => r.OrderNumber)
                .Where(o => !string.IsNullOrEmpty(o))
                .Distinct()
                .ToList();

            // Build date range for Shopify: widest window across all rows ± 3 months
            var minDate = baseRows.Min(r => r.TransactionDate).AddMonths(-3);
            var maxDate = baseRows.Max(r => r.TransactionDate).AddMonths(3);

            // Map: orderNumber -> associateId (stripped of trailing zeros)
            var shopifyMap = await LookupShopifyAssociatesAsync(orderNumbers, minDate, maxDate);

            // Step 3 — batch lookup employee details from LSCentral (on LSDEV)
            var employeeIds = shopifyMap.Values
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct()
                .ToList();

            var employeeMap = new Dictionary<string, EmployeeInfo>(StringComparer.OrdinalIgnoreCase);
            if (employeeIds.Any())
            {
                var idList = string.Join(",", employeeIds.Select(id => $"'{id}'"));
                var empSql = @$"
                    SELECT
                        CAST(employeeID AS VARCHAR) AS EmployeeID,
                        employeeName                AS EmployeeName,
                        storeID                     AS EmployeeStoreID,
                        jobTitleDescriptionEN       AS JobTitle
                    FROM [LSCentral].[dbo].[vw_LSStoreEmployees]
                    WHERE CAST(employeeID AS VARCHAR) IN ({idList})
                      AND endDate = '9999-12-31'";

                var empRows = await _context.Database
                    .SqlQueryRaw<EmployeeInfo>(empSql)
                    .ToListAsync();

                foreach (var e in empRows)
                    if (!string.IsNullOrEmpty(e.EmployeeID))
                        employeeMap[e.EmployeeID] = e;
            }

            // Step 4 — merge
            Rows = baseRows.Select(r =>
            {
                var rawAssocId = shopifyMap.GetValueOrDefault(r.OrderNumber ?? "");
                var emp = !string.IsNullOrEmpty(rawAssocId)
                    ? employeeMap.GetValueOrDefault(rawAssocId)
                    : null;

                return new RecognitionRow
                {
                    ResponseID        = r.ResponseID,
                    ResponseSessionID = r.ResponseSessionID,
                    EmployeeID        = rawAssocId,
                    EmployeeName      = emp?.EmployeeName,
                    EmployeeStoreID   = emp?.EmployeeStoreID,
                    JobTitle          = emp?.JobTitle,
                    StoreNumber       = r.StoreNumber,
                    TransactionDate   = r.TransactionDate,
                    OrderNumber       = r.OrderNumber,
                    CustomerEmail     = r.CustomerEmail,
                    CustomerName      = r.CustomerName,
                    AnswerText        = r.AnswerText
                };
            }).ToList();
        }

        // ── Shopify Lookup (LSInterfaceDB) ──────────────────────────────────────
        private async Task<Dictionary<string, string>> LookupShopifyAssociatesAsync(
            List<string> orderNumbers,
            DateTime minDate,
            DateTime maxDate)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!orderNumbers.Any()) return result;

            var connStr = _config.GetConnectionString("LSInterfaceDB")!;
            var inList = string.Join(",", orderNumbers.Select(o => $"'{o.Replace("'", "''")}'"));

            // Strip trailing zeros from associateId:
            // Shopify stores employee IDs with trailing zeros (e.g. 6141500000 -> 61415).
            // We divide by powers of 10 until no trailing zeros remain via integer cast trick.
            // CAST(TRY_CAST(b.associateId AS BIGINT) AS VARCHAR) preserves the number,
            // then REPLACE(RTRIM(REPLACE(...)) strips trailing zeros safely for purely numeric IDs.
            var sql = @$"
                SELECT
                    a.orderName,
                    -- Strip trailing zeros: '6141500000' -> '61415'
                    REVERSE(SUBSTRING(
                        REVERSE(CAST(TRY_CAST(b.associateId AS BIGINT) AS VARCHAR(20))),
                        PATINDEX('%[^0]%', REVERSE(CAST(TRY_CAST(b.associateId AS BIGINT) AS VARCHAR(20)))),
                        20
                    )) AS associateIdStripped
                FROM [LSShopify].[dbo].[shopifyOrder] a
                INNER JOIN [LSShopify].[dbo].[shopifyOrderline] b ON a.OrderID = b.OrderID
                WHERE a.orderName IN ({inList})
                  AND b.associateId IS NOT NULL
                  AND b.associateId <> ''
                  AND TRY_CAST(b.associateId AS BIGINT) > 0
                  AND a.CreatedAt BETWEEN '{minDate:yyyy-MM-dd}' AND '{maxDate:yyyy-MM-dd}'";

            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var orderName = reader["orderName"]?.ToString();
                var assocId   = reader["associateIdStripped"]?.ToString();
                if (!string.IsNullOrEmpty(orderName) && !string.IsNullOrEmpty(assocId))
                    result.TryAdd(orderName, assocId);
            }

            return result;
        }

        // ── DTOs ────────────────────────────────────────────────────────────────

        private class RecognitionBaseRow
        {
            public int ResponseID { get; set; }
            public int ResponseSessionID { get; set; }
            public string? StoreNumber { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? OrderNumber { get; set; }
            public string? CustomerEmail { get; set; }
            public string? CustomerName { get; set; }
            public string? AnswerText { get; set; }
        }

        private class EmployeeInfo
        {
            public string? EmployeeID { get; set; }
            public string? EmployeeName { get; set; }
            public string? EmployeeStoreID { get; set; }
            public string? JobTitle { get; set; }
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
            public string? OrderNumber { get; set; }
            public string? CustomerEmail { get; set; }
            public string? CustomerName { get; set; }
            public string? AnswerText { get; set; }
        }
    }
}
