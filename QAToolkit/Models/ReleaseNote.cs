using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class ReleaseNote
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty; // Release title

        [Required]
        [StringLength(50)]
        public string Version { get; set; } = string.Empty; // e.g., "1.2.3", "2.0.0-beta"

        [Required]
        [StringLength(100)]
        public string Project { get; set; } = string.Empty;

        [Required]
        public string Features { get; set; } = string.Empty; // New features in this release

        public string? BugFixes { get; set; } // Bug fixes in this release

        public string? KnownIssues { get; set; } // Known issues

        public string? TestingNotes { get; set; } // What to focus on testing

        [StringLength(50)]
        public string? Environment { get; set; } // Deployed to which environment

        public DateTime ReleaseDate { get; set; } = DateTime.Now;

        public bool IsTested { get; set; } = false;

        [StringLength(500)]
        public string? TestingStatus { get; set; } // Summary of testing done

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [StringLength(100)]
        public string? CreatedBy { get; set; } // User email who created this

        public DateTime? ModifiedAt { get; set; }

        [StringLength(100)]
        public string? ModifiedBy { get; set; } // User email who last modified this
    }
}
