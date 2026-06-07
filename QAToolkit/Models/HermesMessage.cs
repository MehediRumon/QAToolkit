namespace QAToolkit.Models
{
    public class HermesMessage
    {
        public int Id { get; set; }
        public int ChatId { get; set; }
        public string Role { get; set; } = "user"; // user / assistant
        public string Content { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}
