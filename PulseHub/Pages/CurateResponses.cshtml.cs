using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace PulseHub
{
    public class CurateResponsesPivotModel : PageModel
    {
        private readonly PulseHubContext _context;

        public CurateResponsesPivotModel(PulseHubContext context)
        {
            _context = context;
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
            var response = await _context.PulseHub_Response.FindAsync(request.ResponseId);
            if (response == null) return NotFound();

            response.StatusID = request.StatusId;
            response.CuratedAt = DateTime.Now;

            await _context.SaveChangesAsync();
            return new JsonResult(new { success = true });
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
            Categories = await _context.PulseHub_Category.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.Category).ToListAsync();
            SubCategories = await _context.PulseHub_SubCategory.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.SubCategory).ToListAsync();
            Departments = await _context.PulseHub_Department.AsNoTracking().Where(x => x.Status == 1).OrderBy(x => x.Department).ToListAsync();

            // 2. Get question mappings
            var questionMappings = await _context.PulseHub_Question.AsNoTracking().ToListAsync();

            var lookup = questionMappings
                .Where(q => q.GroupID.HasValue && !string.IsNullOrEmpty(q.Question))
                .GroupBy(q => q.GroupID)
                .SelectMany(g => {
                    var header = g.FirstOrDefault(x => x.Language == "ENG")?.Question ?? g.First().Question;
                    return g.Select(x => new { Raw = x.Question, Canonical = header });
                }).ToDictionary(x => x.Raw, x => x.Canonical);

            // 3. RAW SQL — keep IDs as int? to avoid VARCHAR cast issues
            var rawSql = @"
                SELECT 
                    r.ResponseID, 
                    r.ResponseSessionID, 
                    r.QuestionText, 
                    r.AnswerText, 
                    r.StatusID,
                    s.TransactionDate, 
                    s.Email, 
                    s.FirstName, 
                    s.Language, 
                    s.OrderNumber,
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
                (!EndDate.HasValue || r.TransactionDate.Date <= EndDate.Value.Date))
                .ToList();

            Questions = filtered.Select(r => lookup.ContainsKey(r.QuestionText ?? "") ? lookup[r.QuestionText!] : r.QuestionText ?? "")
                                .Where(q => !string.IsNullOrEmpty(q))
                                .Distinct().OrderBy(q => q).ToList();

            PivotedResponses = filtered.GroupBy(r => r.ResponseSessionID)
                .Select(g => {
                    var f = g.First();
                    return new PivotedResponse
                    {
                        ResponseSessionID = f.ResponseSessionID,
                        TransactionDate = f.TransactionDate,
                        Email = f.Email,
                        FirstName = f.FirstName,
                        Language = f.Language,
                        OrderNumber = f.OrderNumber,
                        CategoryID = f.CategoryID,
                        SubCategoryID = f.SubCategoryID,
                        DepartmentID = f.DepartmentID,
                        CuratorComment = f.CuratorComment,
                        ManagerComment = f.ManagerComment,
                        AssociateComment = f.AssociateComment,
                        CustomerComment = f.CustomerComment,
                        Actionable = f.Actionable,
                        AnswersByQuestion = Questions.ToDictionary(
                            q => q,
                            q => g.Where(r => (lookup.ContainsKey(r.QuestionText ?? "") ? lookup[r.QuestionText!] : r.QuestionText ?? "") == q)
                                  .Select(r => new ResponseData
                                  {
                                      ResponseID = r.ResponseID,
                                      AnswerText = r.AnswerText,
                                      StatusID = r.StatusID
                                  }).FirstOrDefault()
                        )
                    };
                }).OrderByDescending(r => r.TransactionDate).ToList();
        }

        public class PivotedResponse
        {
            public int ResponseSessionID { get; set; }
            public DateTime TransactionDate { get; set; }
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? Language { get; set; }
            public string? OrderNumber { get; set; }
            public int? CategoryID { get; set; }
            public int? SubCategoryID { get; set; }
            public int? DepartmentID { get; set; }
            public string? CuratorComment { get; set; }
            public string? ManagerComment { get; set; }
            public string? AssociateComment { get; set; }
            public string? CustomerComment { get; set; }
            public bool Actionable { get; set; }
            public Dictionary<string, ResponseData?> AnswersByQuestion { get; set; } = new();
        }

        public class ResponseData
        {
            public int ResponseID { get; set; }
            public string? AnswerText { get; set; }
            public int? StatusID { get; set; }
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
            public DateTime TransactionDate { get; set; }
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? Language { get; set; }
            public string? OrderNumber { get; set; }
            public int? CategoryID { get; set; }
            public int? SubCategoryID { get; set; }
            public int? DepartmentID { get; set; }
            public string? CuratorComment { get; set; }
            public string? ManagerComment { get; set; }
            public string? AssociateComment { get; set; }
            public string? CustomerComment { get; set; }
            public bool Actionable { get; set; }
        }
    }
}
