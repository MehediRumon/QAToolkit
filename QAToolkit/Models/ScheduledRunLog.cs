namespace QAToolkit.Models
{
    public class ScheduledRunLog
    {
        public int Id { get; set; }

        public int ScheduledRunId { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime? FinishedAt { get; set; }

        // "ok" | "error" | "timeout"
        public string Status { get; set; } = string.Empty;

        public string? Output { get; set; }

        public int? ExitCode { get; set; }

        public long DurationMs { get; set; }
    }
}
