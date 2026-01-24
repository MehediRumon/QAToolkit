namespace QAToolkit.Models.Omr;

public class OmrFillRequest
{
    public string RollNumbers { get; set; } = string.Empty;
    public string RegistrationNumbers { get; set; } = string.Empty;
    public string SaqExtraText { get; set; } = string.Empty;
    public string? XmlConfigName { get; set; }
    public int? DotSize { get; set; }
}

public class StudentData
{
    public string RollNumber { get; set; } = string.Empty;
    public string RegistrationNumber { get; set; } = string.Empty;
}

public class OmrProcessingResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ProcessedImageResult> ProcessedImages { get; set; } = new();
    public string? MasterZipFileName { get; set; }
    public string? OutputSessionId { get; set; }
    public Dictionary<string, string> RegistrationZipFiles { get; set; } = new();
}

public class ProcessedImageResult
{
    public string OriginalFileName { get; set; } = string.Empty;
    public string OutputFileName { get; set; } = string.Empty;
    public string? DetectedPageId { get; set; }
    public string? AssignedRoll { get; set; }
    public string? AssignedRegistration { get; set; }
    public string? OutputSubFolder { get; set; }
    public List<string> Logs { get; set; } = new();
}

public class GridSystemData
{
    public List<float> XLines { get; set; } = new();
    public List<float> YLines { get; set; } = new();
    public float StepX { get; set; }
    public float StepY { get; set; }
}

public class OmrRegion
{
    public string MemberName { get; set; } = string.Empty;
    public string Direction { get; set; } = "y";
    public string CharSet { get; set; } = string.Empty;
    public int StartX { get; set; }
    public int StartY { get; set; }
    public float PaddingX { get; set; }
    public float PaddingY { get; set; }
    public float SpacingX { get; set; }
    public float SpacingY { get; set; }
    public int Lines { get; set; } = 1;
    public float ResultCountCellX { get; set; }
    public float ResultCountCellY { get; set; }
    public double BlackPixelPercent { get; set; } = 20.0;
}
