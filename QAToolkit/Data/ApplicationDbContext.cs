using Microsoft.EntityFrameworkCore;
using QAToolkit.Models;

namespace QAToolkit.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<UrlResource> UrlResources { get; set; }
        public DbSet<TestNote> TestNotes { get; set; }
        public DbSet<Credential> Credentials { get; set; }
        public DbSet<ReleaseNote> ReleaseNotes { get; set; }
        public DbSet<MeetingNote> MeetingNotes { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Workflow> Workflows { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Add indexes for better performance
            modelBuilder.Entity<UrlResource>()
                .HasIndex(u => u.Category);

            modelBuilder.Entity<UrlResource>()
                .HasIndex(u => u.Project);

            modelBuilder.Entity<TestNote>()
                .HasIndex(t => t.Project);

            modelBuilder.Entity<Credential>()
                .HasIndex(c => c.Project);

            modelBuilder.Entity<Credential>()
                .HasIndex(c => c.Environment);

            modelBuilder.Entity<ReleaseNote>()
                .HasIndex(r => r.Project);

            modelBuilder.Entity<ReleaseNote>()
                .HasIndex(r => r.Version);

            modelBuilder.Entity<MeetingNote>()
                .HasIndex(m => m.MeetingDate);

            modelBuilder.Entity<Workflow>()
                .HasIndex(w => w.Category);
        }
    }
}
