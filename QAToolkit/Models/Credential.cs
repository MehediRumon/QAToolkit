using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class Credential
    {
        public int Id { get; set; }

        [StringLength(100)]
        public string? Type { get; set; } // Type of credential

        [Required]
        [StringLength(200)]
        public string Username { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Password { get; set; } = string.Empty;

        [StringLength(100)]
        public string? Project { get; set; }

        [StringLength(50)]
        public string? Environment { get; set; } // Dev, QA, Staging, Prod

        [StringLength(500)]
        public string? Url { get; set; } // Login URL

        [StringLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [StringLength(100)]
        public string? CreatedBy { get; set; } // User email who created this

        public DateTime? UpdatedAt { get; set; }
    }
}
