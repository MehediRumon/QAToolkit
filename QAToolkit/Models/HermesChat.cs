namespace QAToolkit.Models
{
    public class HermesChat
    {
        public int Id { get; set; }
        public string UserName { get; set; } = "";
        public string Title { get; set; } = "New Chat";
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
