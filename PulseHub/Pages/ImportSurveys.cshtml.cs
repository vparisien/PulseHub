using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
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
        public IFormFile UploadFile { get; set; }

        [BindProperty]
        public string SurveyType { get; set; }

        public string ImportSummary { get; set; }

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

            int sessionsCreated = 0;
            int responsesCreated = 0;
            int errors = 0;

            try
            {
                using var stream = UploadFile.OpenReadStream();
                using var reader = new StreamReader(stream);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    BadDataFound = null,
                    MissingFieldFound = null,
                    HeaderValidated = null
                });

                // Register the mapping to match CSV headers
                csv.Context.RegisterClassMap<NpsCsvRowMap>();

                var records = csv.GetRecords<NpsCsvRow>().ToList();

                // Group by Email + Transaction + Order
                var grouped = records
                    .GroupBy(r => new
                    {
                        r.Email,
                        r.TransactionDate,
                        r.OrderNumber
                    });

                foreach (var group in grouped)
                {
                    var first = group.First();

                    bool exists = await _context.PulseHub_ResponseSession
                        .AnyAsync(s =>
                            s.Email == first.Email &&
                            s.OrderNumber == first.OrderNumber);

                    if (exists)
                        continue;

                    var session = new PulseHub_ResponseSession
                    {
                        TransactionDate = first.TransactionDate,
                        Email = first.Email,
                        FirstName = first.FirstName,
                        Language = first.Language,
                        OrderNumber = first.OrderNumber,
                        // StoreNumber is string in DB, use "WEB" for online surveys
                        StoreNumber = first.PurchasePlace?.ToUpper() == "WEB"
                            ? "WEB"
                            : ExtractStoreNumber(first.PurchasePlace),
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.PulseHub_ResponseSession.Add(session);
                    await _context.SaveChangesAsync();
                    sessionsCreated++;

                    foreach (var row in group)
                    {
                        var response = new PulseHub_Response
                        {
                            ResponseSessionID = session.ResponseSessionID,
                            QuestionIndex = row.QuestionIndex,
                            QuestionText = row.Question,
                            AnswerText = row.Answer,
                            CreatedAt = DateTime.UtcNow
                        };

                        _context.PulseHub_Response.Add(response);
                        responsesCreated++;
                    }

                    await _context.SaveChangesAsync();
                }

                ImportSummary =
                    $"Import Completed Successfully\n\n" +
                    $"Sessions Created: {sessionsCreated}\n" +
                    $"Responses Created: {responsesCreated}\n" +
                    $"Errors: {errors}";
            }
            catch (Exception ex)
            {
                errors++;
                ImportSummary = $"Import Failed:\n\n{ex.Message}";
            }

            return Page();
        }

        /// <summary>
        /// Extract numeric store number from "PurchasePlace" if it's not "WEB"
        /// </summary>
        private string? ExtractStoreNumber(string? purchasePlace)
        {
            if (string.IsNullOrEmpty(purchasePlace))
                return null;

            var match = Regex.Match(purchasePlace, @"\((\d+)\)");
            if (match.Success)
                return match.Groups[1].Value;

            return purchasePlace; // fallback to original string if not numeric
        }
    }

    // DTO for CSV rows
    public class NpsCsvRow
    {
        public DateTime Timestamp { get; set; }
        public string Email { get; set; }
        public string FirstName { get; set; }
        public string Language { get; set; }
        public int QuestionIndex { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public DateTime TransactionDate { get; set; }
        public string OrderNumber { get; set; }
        public string PurchasePlace { get; set; }
    }

    // CSV header mapping
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
            Map(m => m.OrderNumber).Name("#8 Store Order Name");
            Map(m => m.PurchasePlace).Name("#9 Purchase Place");
            // "#10 count(events)" is ignored
        }
    }
}