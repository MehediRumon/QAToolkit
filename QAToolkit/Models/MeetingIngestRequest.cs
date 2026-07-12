namespace QAToolkit.Models
{
    // JSON payload pushed by the AI meeting desktop app to POST /api/meetings/ingest
    public class MeetingIngestRequest
    {
        public string? Title { get; set; }
        public DateTime? MeetingDate { get; set; }
        public int? DurationMinutes { get; set; }
        public string? Participants { get; set; }
        public string? Source { get; set; }
        public string? Summary { get; set; }
        public string? Transcript { get; set; }
        public string? CreatedBy { get; set; }
        public List<MeetingIngestScreenNote>? ScreenNotes { get; set; }
    }

    public class MeetingIngestScreenNote
    {
        public DateTime? CapturedAt { get; set; }
        public string? Note { get; set; }
        public string? ImageFileName { get; set; }
        public string? ImageBase64 { get; set; } // raw base64 or data URI
    }
}
