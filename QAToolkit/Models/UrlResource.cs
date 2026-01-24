using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class UrlResource
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [Url]
        public string Url { get; set; } = string.Empty;

        [StringLength(50)]
        public string? Category { get; set; } // Staging, Production, API, etc.

        [StringLength(100)]
        public string? Project { get; set; }

        [StringLength(50)]
        public string? Environment { get; set; } // Dev, QA, Staging, Prod

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsFavorite { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [StringLength(100)]
        public string? CreatedBy { get; set; } // User email who created this

        public DateTime? LastChecked { get; set; }

        public bool? IsAlive { get; set; }
    }
}
