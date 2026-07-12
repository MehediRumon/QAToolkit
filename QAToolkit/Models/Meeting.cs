using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class Meeting
    {
        public int Id { get; set; }

        [Required]
        [StringLength(300)]
        public string Title { get; set; } = string.Empty;

        public DateTime MeetingDate { get; set; }

        public int? DurationMinutes { get; set; }

        [StringLength(1000)]
        public string? Participants { get; set; }

        [StringLength(100)]
        public string? Source { get; set; } // e.g. desktop app name/version

        public string? Summary { get; set; }

        public string? Transcript { get; set; }

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        public List<MeetingScreenNote> ScreenNotes { get; set; } = new();
    }

    public class MeetingScreenNote
    {
        public int Id { get; set; }

        public int MeetingId { get; set; }

        public DateTime? CapturedAt { get; set; }

        public string? Note { get; set; }

        public string? ImagePath { get; set; } // web path under /uploads/meetings/{meetingId}/

        public Meeting? Meeting { get; set; }
    }
}
