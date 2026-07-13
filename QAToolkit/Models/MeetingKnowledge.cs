namespace QAToolkit.Models
{
    // Single-row store for the knowledge markdown file the meeting desktop app fetches
    public class MeetingKnowledge
    {
        public int Id { get; set; }
        public string FileName { get; set; } = "knowledge.md";
        public string Content { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }
}
