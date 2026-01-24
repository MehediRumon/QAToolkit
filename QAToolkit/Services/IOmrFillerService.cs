using QAToolkit.Models.Omr;

namespace QAToolkit.Services;

public interface IOmrFillerService
{
    Task<OmrProcessingResult> ProcessImagesAsync(
        IEnumerable<Stream> imageStreams,
        IEnumerable<string> fileNames,
        string xmlConfigPath,
        string[] rollNumbers,
        string[] registrationNumbers,
        string saqExtraText,
        int? dotSize = null);

    List<string> GetAvailableXmlConfigs();

    OmrRegion? LoadOmrInfoRegion(string xmlPath);
    List<OmrRegion> LoadRegionsForPage(string xmlPath, string pageId);

    List<StudentData> ParseExcelFile(Stream excelStream);
    Task<string> CreateRegistrationZipAsync(string sessionId, string registrationNumber);
    Task<string> CreateMasterZipAsync(string sessionId);
}
