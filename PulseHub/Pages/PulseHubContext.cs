using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PulseHub
{
    public class PulseHubContext : DbContext
    {
        public PulseHubContext(DbContextOptions<PulseHubContext> options)
            : base(options)
        {
        }

        public DbSet<PulseHub_ResponseSession> PulseHub_ResponseSession { get; set; }
        public DbSet<PulseHub_Response> PulseHub_Response { get; set; }
        public DbSet<PulseHub_Question> PulseHub_Question { get; set; }

        // New tables for dropdowns
        public DbSet<PulseHub_Category> PulseHub_Category { get; set; }
        public DbSet<PulseHub_SubCategory> PulseHub_SubCategory { get; set; }
        public DbSet<PulseHub_Department> PulseHub_Department { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure PulseHub_ResponseSession
            modelBuilder.Entity<PulseHub_ResponseSession>(entity =>
            {
                entity.HasKey(s => s.ResponseSessionID);
                entity.ToTable("PulseHub_ResponseSession");
            });

            // Configure PulseHub_Response
            modelBuilder.Entity<PulseHub_Response>(entity =>
            {
                entity.HasKey(r => r.ResponseID);
                entity.ToTable("PulseHub_Response");

                entity.Property(r => r.StatusID).HasColumnName("StatusID");
                entity.Property(r => r.RecognitionOf).HasMaxLength(255);

                entity.HasOne(r => r.ResponseSession)
                    .WithMany(s => s.Responses)
                    .HasForeignKey(r => r.ResponseSessionID)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // Configure PulseHub_Question
            modelBuilder.Entity<PulseHub_Question>(entity =>
            {
                entity.HasKey(q => q.QuestionID);
                entity.ToTable("PulseHub_Question");
            });

            // Configure Reference Tables
            modelBuilder.Entity<PulseHub_Category>(e => { e.HasKey(x => x.CategoryID); e.ToTable("PulseHub_Category"); });
            modelBuilder.Entity<PulseHub_SubCategory>(e => { e.HasKey(x => x.SubCategoryID); e.ToTable("PulseHub_SubCategory"); });
            modelBuilder.Entity<PulseHub_Department>(e => { e.HasKey(x => x.DepartmentID); e.ToTable("PulseHub_Department"); });
        }
    }

    public class PulseHub_ResponseSession
    {
        public int ResponseSessionID { get; set; }
        public DateTime TransactionDate { get; set; }
        public string Email { get; set; }
        public string FirstName { get; set; }
        public string Language { get; set; }
        public string OrderNumber { get; set; }
        public string? StoreNumber { get; set; }
        public DateTime CreatedAt { get; set; }

        //// FIXED TYPES: Changed from string? to int? to match DB and support Dropdowns
        public int? CategoryID { get; set; }
        public int? SubCategoryID { get; set; }
        public int? DepartmentID { get; set; }

        public string? CuratorComment { get; set; }
        public string? ManagerComment { get; set; }
        public string? AssociateComment { get; set; }
        public string? CustomerComment { get; set; }
        public bool Actionable { get; set; } = true;

        public List<PulseHub_Response> Responses { get; set; } = new List<PulseHub_Response>();
    }

    // --- New Reference Classes for Dropdowns ---

    public class PulseHub_Category
    {
        public int CategoryID { get; set; }
        public string Category { get; set; } // Matches your column name 'Category'
        public int? Status { get; set; }
    }

    public class PulseHub_SubCategory
    {
        public int SubCategoryID { get; set; }
        public int CategoryID { get; set; }
        public string SubCategory { get; set; } // Matches your column name 'SubCategory'
        public int? Status { get; set; }
    }

    public class PulseHub_Department
    {
        public int DepartmentID { get; set; }
        public string Department { get; set; } // Matches your column name 'Department'
        public int? Status { get; set; }
    }

    // --- Existing Classes ---

    public class PulseHub_Response
    {
        public int ResponseID { get; set; }
        public int? ResponseSessionID { get; set; }
        public int? QuestionIndex { get; set; }
        public string? QuestionText { get; set; }
        public string? AnswerText { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? CuratedAt { get; set; }
        public DateTime? FlaggedAt { get; set; }
        public DateTime? RespondedAt { get; set; }
        public string? AssignedTo { get; set; }
        public string? RecognitionOf { get; set; }
        public int? StatusID { get; set; }
        public PulseHub_ResponseSession ResponseSession { get; set; }
    }

    public class PulseHub_Question
    {
        public int QuestionID { get; set; }
        public string? Question { get; set; }
        public string? Language { get; set; }
        public int? GroupID { get; set; }
    }
}