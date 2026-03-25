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

        [BindProperty(SupportsGet = true)]
        public bool Debug { get; set; }

        public List<RecognitionRow> Rows { get; set; } = new();
        public List<DebugRow> DebugLog { get; set; } = new();

        public async Task OnGetAsync()
        {
            StartDate ??= DateTime.Today.AddDays(-30);
            EndDate ??= DateTime.Today;

            // Step 1 — pull recognition responses from LSDEV
            var sql = @$"
                SELECT
                    r.ResponseID,
                    r.ResponseSessionID,
                    s.StoreNumber,
                    s.TransactionDate,
                    s.OrderNumber,
                    s.Email         AS CustomerEmail,
                    s.FirstName     AS CustomerName,
                    r.AnswerText,
                    r.RecognitionOf AS RecognitionOf
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
                Rows = new();
                return;
            }

            // Step 2 — batch Shopify lookup
            var orderNumbers = baseRows
                .Select(r => r.OrderNumber)
                .Where(o => !string.IsNullOrEmpty(o))
                .Distinct()
                .ToList();

            var minDate = baseRows.Min(r => r.TransactionDate).AddMonths(-3);
            var maxDate = baseRows.Max(r => r.TransactionDate).AddMonths(3);

            var (shopifyMap, shopifyDebug) = await LookupShopifyAssociatesAsync(orderNumbers, minDate, maxDate);

            // Step 3 — batch employee lookup from LSCentral
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
                        CAST(employeeID AS VARCHAR)  AS EmployeeID,
                        employeeName                 AS EmployeeName,
                        storeID                      AS EmployeeStoreID,
                        jobTitleDescriptionEN        AS JobTitle
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

            // Step 4 — merge, persist RecognitionOf, build debug log
            Rows = new();
            DebugLog = new();

            foreach (var r in baseRows)
            {
                var order = r.OrderNumber ?? "";
                shopifyMap.TryGetValue(order, out var strippedId);
                employeeMap.TryGetValue(strippedId ?? "", out var emp);

                // Persist associateId into RecognitionOf if not already set or changed
                if (!string.IsNullOrEmpty(strippedId) && r.RecognitionOf != strippedId)
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        $"UPDATE PulseHub_Response SET RecognitionOf = '{strippedId}' WHERE ResponseID = {r.ResponseID}");
                }

                Rows.Add(new RecognitionRow
                {
                    ResponseID        = r.ResponseID,
                    ResponseSessionID = r.ResponseSessionID,
                    EmployeeID        = strippedId,
                    EmployeeName      = emp?.EmployeeName,
                    EmployeeStoreID   = emp?.EmployeeStoreID,
                    JobTitle          = emp?.JobTitle,
                    StoreNumber       = r.StoreNumber,
                    TransactionDate   = r.TransactionDate,
                    OrderNumber       = r.OrderNumber,
                    CustomerEmail     = r.CustomerEmail,
                    CustomerName      = r.CustomerName,
                    AnswerText        = r.AnswerText
                });

                DebugLog.Add(new DebugRow
                {
                    ResponseID         = r.ResponseID,
                    OrderNumber        = order,
                    RecognitionOfBefore = r.RecognitionOf,
                    ShopifyRawId       = shopifyDebug.GetValueOrDefault(order),
                    AssocIdStripped    = strippedId,
                    EmployeeFound      = emp != null,
                    EmployeeName       = emp?.EmployeeName ?? "(not found in LSCentral)",
                    Note               = string.IsNullOrEmpty(order) ? "No order number" :
                                         string.IsNullOrEmpty(strippedId) ? "No Shopify match" :
                                         emp == null ? "Shopify ID found but no LSCentral match" :
                                         "OK"
                });
            }
        }

        // ── Shopify Lookup ──────────────────────────────────────────────────────
        private async Task<(Dictionary<string, string> stripped, Dictionary<string, string> raw)>
            LookupShopifyAssociatesAsync(List<string> orderNumbers, DateTime minDate, DateTime maxDate)
        {
            var stripped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var raw      = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!orderNumbers.Any()) return (stripped, raw);

            var connStr = _config.GetConnectionString("LSInterfaceDB")!;
            var inList  = string.Join(",", orderNumbers.Select(o => $"'{o.Replace("'", "''")}'"));

            var sql = @$"
                SELECT
                    a.orderName,
                    b.associateId,
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

            try
            {
                using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();
                using var cmd    = new SqlCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var orderName  = reader["orderName"]?.ToString() ?? "";
                    var assocRaw   = reader["associateId"]?.ToString() ?? "";
                    var assocStrip = reader["associateIdStripped"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(orderName))
                    {
                        raw.TryAdd(orderName, assocRaw);
                        stripped.TryAdd(orderName, assocStrip);
                    }
                }
            }
            catch (Exception ex)
            {
                // Surface the error in debug log — add a sentinel entry
                raw["__error__"]     = ex.Message;
                stripped["__error__"] = ex.Message;
            }

            return (stripped, raw);
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
            public string? RecognitionOf { get; set; }
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

        public class DebugRow
        {
            public int ResponseID { get; set; }
            public string? OrderNumber { get; set; }
            public string? RecognitionOfBefore { get; set; }
            public string? ShopifyRawId { get; set; }
            public string? AssocIdStripped { get; set; }
            public bool EmployeeFound { get; set; }
            public string? EmployeeName { get; set; }
            public string? Note { get; set; }
        }
    }
}
