namespace QAToolkit.Models
{
    public class HermesActivity
    {
        public int Id { get; set; }
        public string UserName { get; set; } = "";
        public string ActivityType { get; set; } = ""; // ScriptCreated, ScriptEdited, ScriptRun, TestNoteCreated
        public int EntityId { get; set; }
        public string EntityName { get; set; } = "";
        public string? Tags { get; set; }
        public string? Extra { get; set; } // run status, exit code, etc.
        public DateTime CreatedAt { get; set; }
    }
}
