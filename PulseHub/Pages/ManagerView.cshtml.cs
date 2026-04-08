using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace PulseHub.Pages
{
    public class ManagerViewModel : AuthenticatedPageModel
    {
        private readonly PulseHubContext _context;

        public ManagerViewModel(PulseHubContext context)
        {
            _context = context;
        }

        public List<ManagerSession> StoreSessions { get; set; } = new();
        public List<ManagerSession> RegionSessions { get; set; } = new();
        public bool IsRegionManager { get; set; }

        public async Task OnGetAsync()
        {
            var username = CurrentUsername?.ToLower() ?? "";

            // 1. Find this user's employee ID by matching AD username pattern (first initial + last name)
            //    e.g. "John Smith" → "jsmith"
            var employees = await _context.Database
                .SqlQueryRaw<EmployeeResult>(@"
                    SELECT DISTINCT
                        CAST(employeeID AS VARCHAR) AS EmployeeID,
                        employeeName                AS EmployeeName
                    FROM [LSCentral].[dbo].[vw_LSStoreEmployees]
                    WHERE endDate = '9999-12-31'
                      AND employeeName IS NOT NULL")
                .ToListAsync();

            var myEmployee = employees.FirstOrDefault(e => BuildAdUsername(e.EmployeeName) == username);

            // 2. Load sessions red-flagged and assigned to this manager
            if (myEmployee != null)
            {
                // Values come from the DB, not user input — no injection risk
                StoreSessions = await _context.Database
                    .SqlQueryRaw<ManagerSession>($@"
                        SELECT
                            s.ResponseSessionID,
                            s.OrderNumber,
                            s.StoreNumber,
                            s.TransactionDate,
                            s.Email       AS CustomerEmail,
                            s.FirstName   AS CustomerName,
                            s.CuratorComment,
                            s.ManagerComment,
                            s.Actionable,
                            r.AssignedTo,
                            NULL          AS AssignedToName,
                            MIN(r.CuratedAt)   AS CuratedAt,
                            MAX(r.RespondedAt)  AS RespondedAt,
                            COUNT(r.ResponseID) AS FlaggedCount,
                            STUFF((
                                SELECT '||' + CAST(r2.AnswerText AS NVARCHAR(MAX))
                                FROM PulseHub_Response r2
                                WHERE r2.ResponseSessionID = s.ResponseSessionID
                                  AND r2.StatusID = 1
                                FOR XML PATH(''), TYPE
                            ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS AnswerText,
                            CASE WHEN MAX(r.RespondedAt) IS NOT NULL THEN 'Actioned' ELSE 'Pending' END AS ActionStatus
                        FROM PulseHub_ResponseSession s
                        INNER JOIN PulseHub_Response r
                            ON r.ResponseSessionID = s.ResponseSessionID AND r.StatusID = 1
                        WHERE r.AssignedTo = '{myEmployee.EmployeeID}'
                        GROUP BY
                            s.ResponseSessionID, s.OrderNumber, s.StoreNumber, s.TransactionDate,
                            s.Email, s.FirstName, s.CuratorComment, s.ManagerComment,
                            s.Actionable, r.AssignedTo
                        ORDER BY MAX(r.RespondedAt) ASC, s.TransactionDate DESC")
                    .ToListAsync();
            }

            // 3. Check if this user is a Region Manager via vw_LSStoreStructure (DistrictManagerId column)
            var regionRows = await _context.Database
                .SqlQueryRaw<RegionStoreResult>(@"
                    SELECT DISTINCT
                        CAST(storeID AS VARCHAR) AS StoreID,
                        districtManagerName      AS DistrictManagerName
                    FROM [LSCentral].[dbo].[vw_LSStoreStructure]
                    WHERE districtManagerName IS NOT NULL")
                .ToListAsync();

            var myStoreInts = regionRows
                .Where(r => BuildAdUsername(r.DistrictManagerName) == username)
                .Select(r => r.StoreID?.TrimStart('0'))
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => int.TryParse(id, out var n) ? n : (int?)null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .Distinct()
                .ToList();

            IsRegionManager = myStoreInts.Any();

            // 4. Load all red-flagged sessions across this region's stores
            if (IsRegionManager)
            {
                var storeList = string.Join(",", myStoreInts); // safe: integers from DB
                RegionSessions = await _context.Database
                    .SqlQueryRaw<ManagerSession>($@"
                        SELECT
                            s.ResponseSessionID,
                            s.OrderNumber,
                            s.StoreNumber,
                            s.TransactionDate,
                            s.Email          AS CustomerEmail,
                            s.FirstName      AS CustomerName,
                            s.CuratorComment,
                            s.ManagerComment,
                            s.Actionable,
                            r.AssignedTo,
                            e.employeeName   AS AssignedToName,
                            MIN(r.CuratedAt)   AS CuratedAt,
                            MAX(r.RespondedAt)  AS RespondedAt,
                            COUNT(r.ResponseID) AS FlaggedCount,
                            STUFF((
                                SELECT '||' + CAST(r2.AnswerText AS NVARCHAR(MAX))
                                FROM PulseHub_Response r2
                                WHERE r2.ResponseSessionID = s.ResponseSessionID
                                  AND r2.StatusID = 1
                                FOR XML PATH(''), TYPE
                            ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS AnswerText,
                            CASE WHEN MAX(r.RespondedAt) IS NOT NULL THEN 'Actioned' ELSE 'Pending' END AS ActionStatus
                        FROM PulseHub_ResponseSession s
                        INNER JOIN PulseHub_Response r
                            ON r.ResponseSessionID = s.ResponseSessionID AND r.StatusID = 1
                        LEFT JOIN [LSCentral].[dbo].[vw_LSStoreEmployees] e
                            ON CAST(e.employeeID AS VARCHAR) = r.AssignedTo
                           AND e.endDate = '9999-12-31'
                        WHERE TRY_CAST(s.StoreNumber AS INT) IN ({storeList})
                        GROUP BY
                            s.ResponseSessionID, s.OrderNumber, s.StoreNumber, s.TransactionDate,
                            s.Email, s.FirstName, s.CuratorComment, s.ManagerComment,
                            s.Actionable, r.AssignedTo, e.employeeName
                        ORDER BY MAX(r.RespondedAt) ASC, s.TransactionDate DESC")
                    .ToListAsync();
            }
        }

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

        // Builds an AD-style username from a full name: "John Smith" → "jsmith"
        private static string BuildAdUsername(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "";
            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].ToLower();
            return (parts[0][0].ToString() + parts[^1]).ToLower();
        }

        public class ManagerSession
        {
            public int ResponseSessionID { get; set; }
            public string? OrderNumber { get; set; }
            public string? StoreNumber { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? CustomerEmail { get; set; }
            public string? CustomerName { get; set; }
            public string? CuratorComment { get; set; }
            public string? ManagerComment { get; set; }
            public bool? Actionable { get; set; }
            public string? AssignedTo { get; set; }
            public string? AssignedToName { get; set; }
            public DateTime? CuratedAt { get; set; }
            public DateTime? RespondedAt { get; set; }
            public int FlaggedCount { get; set; }
            public string? AnswerText { get; set; }
            public string? ActionStatus { get; set; }
        }

        public class MarkRespondedRequest
        {
            public int ResponseSessionId { get; set; }
            public bool Responded { get; set; }
        }

        private class EmployeeResult
        {
            public string? EmployeeID { get; set; }
            public string? EmployeeName { get; set; }
        }

        private class RegionStoreResult
        {
            public string? StoreID { get; set; }
            public string? DistrictManagerName { get; set; }
        }
    }
}
