using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    // An HTML test-case document (same render pattern as Workflow), organized by project/module
    public class TestCaseDoc
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [StringLength(100)]
        public string? Project { get; set; } // e.g. UMS, Student Portal

        [StringLength(200)]
        public string? Module { get; set; } // e.g. Teacher → Q&A Payment → Payment Entry

        public string HtmlContent { get; set; } = string.Empty;

        public bool IsPublic { get; set; }

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }
}
