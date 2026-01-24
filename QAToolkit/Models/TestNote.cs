using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class TestNote
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Description { get; set; } = string.Empty; // Description instead of Content

        [StringLength(100)]
        public string? Project { get; set; } // UMS, Student Portal, Teacher Portal, Scheduler, OM, API, App

        [StringLength(100)]
        public string? Server { get; set; }

        [StringLength(50)]
        public string? Status { get; set; } // Posted, Not Posted, Fixed, Not Fixed

        public string? Remarks { get; set; }

        public string? ImageVideoUrl { get; set; } // Image/Video URL instead of ScreenshotPath

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [StringLength(100)]
        public string? CreatedBy { get; set; } // User email who created this

        public DateTime? UpdatedAt { get; set; }
    }
}
