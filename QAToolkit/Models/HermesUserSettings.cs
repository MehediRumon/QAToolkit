namespace QAToolkit.Models
{
    public class HermesUserSettings
    {
        public int Id { get; set; }
        public string UserName { get; set; } = "";
        public string Provider { get; set; } = "Groq"; // Groq, Gemini, Proxy
        public string ApiKey { get; set; } = "";
        public string? Model { get; set; }
    }
}
