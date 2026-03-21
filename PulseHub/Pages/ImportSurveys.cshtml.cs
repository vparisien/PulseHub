using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PulseHub
{
    public class ImportSurveyModel : PageModel
    {
        private readonly PulseHubContext _context;

        public ImportSurveyModel(PulseHubContext context)
        {
            _context = context;
        }

        [BindProperty]
        public IFormFile? UploadFile { get; set; }

        [BindProperty]
        public string SurveyType { get; set; } = "";

        public string? ImportSummary { get; set; }
        public bool ShowOverwritePrompt { get; set; }
        public int DuplicateCount { get; set; }

        // ── Normal import (first pass) ──────────────────────────────────────
        public async Task<IActionResult> OnPostAsync()
        {
            if (UploadFile == null || UploadFile.Length == 0)
            {
                ImportSummary = "No file selected.";
                return Page();
            }

            if (string.IsNullOrEmpty(SurveyType))
            {
                ImportSummary = "Survey Type is required.";
                return Page();
            }

            try
            {
                var records = ParseCsv(UploadFile);
                var grouped = GroupRecords(records);

                // Detect duplicates
                var duplicates = new List<string>();
                foreach (var group in grouped)
                {
                    var first = group.First();
                    bool exists = await _context.PulseHub_ResponseSession
                        .AnyAsync(s => s.Email == first.Email && s.OrderNumber == first.OrderNumber);
                    if (exists)
                        duplicates.Add($"{first.Email}|{first.OrderNumber}");
                }

                if (duplicates.Count > 0)
                {
                    // Cache parsed records in TempData for the overwrite pass
                    TempData["PendingRecords"] = JsonSerializer.Serialize(records);
                    TempData["PendingSurveyType"] = SurveyType;
                    ShowOverwritePrompt = true;
                    DuplicateCount = duplicates.Count;
                    return Page();
                }

                // No duplicates — import straight away
                var (sessions, responses) = await ImportGroupsAsync(grouped, overwrite: false);
                ImportSummary = BuildSummary(sessions, responses, 0);
            }
            catch (Exception ex)
            {
                ImportSummary = $"Import Failed:\n\n{ex.Message}";
            }

            return Page();
        }

        // ── Overwrite confirmed ─────────────────────────────────────────────
        public async Task<IActionResult> OnPostOverwriteAsync()
        {
            var json = TempData["PendingRecords"] as string;
            SurveyType = TempData["PendingSurveyType"] as string ?? "";

            if (string.IsNullOrEmpty(json))
            {
                ImportSummary = "Session expired. Please re-upload the file.";
                return Page();
            }

            try
            {
                var records = JsonSerializer.Deserialize<List<NpsCsvRow>>(json) ?? new();
                var grouped = GroupRecords(records);
                var (sessions, responses) = await ImportGroupsAsync(grouped, overwrite: true);
                ImportSummary = BuildSummary(sessions, responses, 0, overwrite: true);
            }
            catch (Exception ex)
            {
                ImportSummary = $"Import Failed:\n\n{ex.Message}";
            }

            return Page();
        }

        // ── Core import logic ───────────────────────────────────────────────
        private async Task<(int sessions, int responses)> ImportGroupsAsync(
            IEnumerable<IGrouping<(string Email, DateTime TransactionDate, string OrderNumber), NpsCsvRow>> grouped,
            bool overwrite)
        {
            int sessionsCreated = 0;
            int responsesCreated = 0;

            foreach (var group in grouped)
            {
                var first = group.First();

                if (overwrite)
                {
                    // Delete existing session (cascade deletes responses)
                    var existing = await _context.PulseHub_ResponseSession
                        .FirstOrDefaultAsync(s => s.Email == first.Email && s.OrderNumber == first.OrderNumber);
                    if (existing != null)
                    {
                        _context.PulseHub_ResponseSession.Remove(existing);
                        await _context.SaveChangesAsync();
                    }
                }
                else
                {
                    bool exists = await _context.PulseHub_ResponseSession
                        .AnyAsync(s => s.Email == first.Email && s.OrderNumber == first.OrderNumber);
                    if (exists) continue;
                }

                var purchasePlace = first.PurchasePlace?.Trim() ?? "";
                var isWeb = purchasePlace.Equals("WEB", StringComparison.OrdinalIgnoreCase)
                         || SurveyType.Equals("Online", StringComparison.OrdinalIgnoreCase);

                var session = new PulseHub_ResponseSession
                {
                    TransactionDate = first.TransactionDate,
                    Email = first.Email,
                    FirstName = first.FirstName,
                    Language = first.Language,
                    OrderNumber = first.OrderNumber,
                    StoreNumber = isWeb ? "WEB" : ExtractStoreNumber(purchasePlace),
                    CreatedAt = DateTime.UtcNow
                };

                _context.PulseHub_ResponseSession.Add(session);
                await _context.SaveChangesAsync();
                sessionsCreated++;

                foreach (var row in group)
                {
                    _context.PulseHub_Response.Add(new PulseHub_Response
                    {
                        ResponseSessionID = session.ResponseSessionID,
                        QuestionIndex = row.QuestionIndex,
                        QuestionText = row.Question,
                        AnswerText = row.Answer,
                        CreatedAt = DateTime.UtcNow
                    });
                    responsesCreated++;
                }

                await _context.SaveChangesAsync();
            }

            return (sessionsCreated, responsesCreated);
        }

        // ── CSV Parsing ─────────────────────────────────────────────────────
        private static List<NpsCsvRow> ParseCsv(IFormFile file)
        {
            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                BadDataFound = null,
                MissingFieldFound = null,
                HeaderValidated = null
            });
            csv.Context.RegisterClassMap<NpsCsvRowMap>();
            return csv.GetRecords<NpsCsvRow>().ToList();
        }

        private static IEnumerable<IGrouping<(string Email, DateTime TransactionDate, string OrderNumber), NpsCsvRow>>
            GroupRecords(List<NpsCsvRow> records) =>
            records.GroupBy(r => (r.Email ?? "", r.TransactionDate, r.OrderNumber ?? ""));

        private static string BuildSummary(int sessions, int responses, int errors, bool overwrite = false) =>
            $"Import {(overwrite ? "(Overwrite) " : "")}Completed Successfully\n\n" +
            $"Sessions Created: {sessions}\n" +
            $"Responses Created: {responses}\n" +
            $"Errors: {errors}";

        private static string? ExtractStoreNumber(string? purchasePlace)
        {
            if (string.IsNullOrEmpty(purchasePlace)) return null;
            var match = Regex.Match(purchasePlace, @"\((\d+)\)");
            return match.Success ? match.Groups[1].Value : purchasePlace;
        }
    }

    // ── CSV DTO ─────────────────────────────────────────────────────────────
    public class NpsCsvRow
    {
        public DateTime Timestamp { get; set; }
        public string? Email { get; set; }
        public string? FirstName { get; set; }
        public string? Language { get; set; }
        public int QuestionIndex { get; set; }
        public string? Question { get; set; }
        public string? Answer { get; set; }
        public DateTime TransactionDate { get; set; }
        public string? OrderNumber { get; set; }
        public string? PurchasePlace { get; set; }
    }

    // ── CSV Mapping — handles both Store and Online column names ────────────
    public class NpsCsvRowMap : ClassMap<NpsCsvRow>
    {
        public NpsCsvRowMap()
        {
            Map(m => m.Timestamp).Name("#0 timestamp");
            Map(m => m.Email).Name("#1 email");
            Map(m => m.FirstName).Name("#2 first_name");
            Map(m => m.Language).Name("#3 language");
            Map(m => m.QuestionIndex).Name("#4 question_index");
            Map(m => m.Question).Name("#5 question");
            Map(m => m.Answer).Name("#6 answer");
            Map(m => m.TransactionDate).Name("#7 Transaction Date");
            // Store: "#8 Store Order Name"  |  Online: "#8 Purchase Name"
            Map(m => m.OrderNumber).Name("#8 Store Order Name", "#8 Purchase Name");
            // Store: "#9 Purchase Place"    |  Online: "#9 Last Purchase Place"
            Map(m => m.PurchasePlace).Name("#9 Purchase Place", "#9 Last Purchase Place");
        }
    }
}
