namespace QAToolkit.Models
{
    public class HermesKnowledge
    {
        public int Id { get; set; }
        public string Project { get; set; } = "";
        public string? Module { get; set; }
        public string Summary { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }
}
