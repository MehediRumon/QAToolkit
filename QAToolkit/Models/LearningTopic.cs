using System.ComponentModel.DataAnnotations;

namespace QAToolkit.Models
{
    public class LearningTopic
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [Display(Name = "How We Work Now")]
        public string? CurrentPractice { get; set; }

        [Display(Name = "World Standard")]
        public string? WorldStandard { get; set; }

        [StringLength(50)]
        public string? Status { get; set; } // Not Started, Learning, Implemented, Using in Work

        [Display(Name = "How We Use It")]
        public string? UsageNotes { get; set; }

        [StringLength(100)]
        [Display(Name = "Class Held By")]
        public string? HeldBy { get; set; }

        [Display(Name = "Class Date")]
        [DataType(DataType.Date)]
        public DateTime? ClassDate { get; set; }

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }
}
