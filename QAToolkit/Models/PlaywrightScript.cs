using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class PlaywrightScript
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }

        [StringLength(300)]
        public string? Tags { get; set; }

        public string ScriptContent { get; set; } = string.Empty;

        [StringLength(10)]
        public string FileExtension { get; set; } = ".js";

        [StringLength(20)]
        public string RunMode { get; set; } = "node";

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? UpdatedAt { get; set; }

        public bool IsPublic { get; set; }
    }
}
