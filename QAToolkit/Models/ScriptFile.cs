namespace QAToolkit.Models
{
    public class ScriptFile
    {
        public int Id { get; set; }
        public string FileName { get; set; } = "";        // stored name on disk
        public string OriginalName { get; set; } = "";    // original upload name
        public string? Description { get; set; }
        public long FileSizeBytes { get; set; }
        public string? ContentType { get; set; }
        public string StoredPath { get; set; } = "";      // absolute path on server
        public string UploadedBy { get; set; } = "";
        public DateTime UploadedAt { get; set; }
    }
}
