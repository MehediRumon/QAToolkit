using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class MeetingNote
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Topics { get; set; } = string.Empty; // Meeting Topics (renamed from Title)

        [Required]
        public string Decisions { get; set; } = string.Empty; // Meeting Decisions (renamed from Notes)

        [StringLength(100)]
        public string? MeetingWith { get; set; } // Dev Team, Rnd Team, UX Team, Frontend Team, Accounts Team, Udvash, Other

        public string? Attendees { get; set; } // Comma-separated attendees

        [StringLength(100)]
        public string? DecisionBy { get; set; } // Decision maker

        [StringLength(50)]
        public string? Status { get; set; } // Need to Implement, Implement Later, Invalid, Closed, Cancelled

        public DateTime MeetingDate { get; set; } = DateTime.Now;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [StringLength(100)]
        public string? CreatedBy { get; set; } // User email who created this

        public DateTime? UpdatedAt { get; set; }

        [StringLength(100)]
        public string? ModifiedBy { get; set; } // User email who last modified this
    }
}
