using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class Workflow
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [StringLength(100)]
        public string? Category { get; set; }

        public string HtmlContent { get; set; } = string.Empty;

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? UpdatedAt { get; set; }

        public bool IsPublic { get; set; }
    }
}
