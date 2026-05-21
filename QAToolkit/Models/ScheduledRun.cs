using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class ScheduledRun
    {
        public int Id { get; set; }

        public int ScriptId { get; set; }

        [Required, StringLength(200)]
        public string Name { get; set; } = string.Empty;

        // "once" | "daily" | "interval"
        [StringLength(20)]
        public string ScheduleType { get; set; } = "once";

        // For "once": specific BD datetime to run
        public DateTime? RunOnce { get; set; }

        // For "daily": time of day in "HH:mm" format (BD time)
        [StringLength(10)]
        public string? DailyTime { get; set; }

        // For "interval": repeat every N minutes
        public int? IntervalMinutes { get; set; }

        public string? ParamsJson { get; set; }

        public bool IsEnabled { get; set; } = true;

        public DateTime? LastRunAt { get; set; }

        // "ok" | "error" | "timeout" | "running"
        [StringLength(20)]
        public string? LastRunStatus { get; set; }

        public DateTime? NextRunAt { get; set; }

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }
}
