using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml;
using QAToolkit.Models.Omr;
using QAToolkit.Services;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace QAToolkit.Controllers;

[Authorize]
public class ToolsController : Controller
{
    private readonly IOmrFillerService _omrFillerService;
    private readonly IWebHostEnvironment _environment;

    public ToolsController(IOmrFillerService omrFillerService, IWebHostEnvironment environment)
    {
        _omrFillerService = omrFillerService;
        _environment = environment;
    }

    // OMR Filler
    public IActionResult OmrFiller()
    {
        ViewBag.AvailableConfigs = _omrFillerService.GetAvailableXmlConfigs();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OmrFiller(OmrFillRequest request, List<IFormFile> images, IFormFile? excelFile)
    {
        ViewBag.AvailableConfigs = _omrFillerService.GetAvailableXmlConfigs();

        if (string.IsNullOrWhiteSpace(request.XmlConfigName))
        {
            TempData["Error"] = "Please select an XML configuration file.";
            return View(request);
        }

        if (images == null || !images.Any())
        {
            TempData["Error"] = "Please upload at least one image.";
            return View(request);
        }

        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
        foreach (var image in images)
        {
            var ext = Path.GetExtension(image.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(ext))
            {
                TempData["Error"] = $"Invalid file type: {image.FileName}. Only JPG, PNG, and BMP files are allowed.";
                return View(request);
            }
        }

        string[] rollNumbers;
        string[] registrationNumbers;

        if (excelFile != null && excelFile.Length > 0)
        {
            var excelExt = Path.GetExtension(excelFile.FileName).ToLowerInvariant();
            if (excelExt != ".xlsx" && excelExt != ".xls")
            {
                TempData["Error"] = "Invalid Excel file type. Only .xlsx and .xls files are allowed.";
                return View(request);
            }

            using var excelStream = excelFile.OpenReadStream();
            var students = _omrFillerService.ParseExcelFile(excelStream);

            if (!students.Any())
            {
                TempData["Error"] = "No student data found in the Excel file.";
                return View(request);
            }

            rollNumbers = students.Select(s => s.RollNumber).ToArray();
            registrationNumbers = students.Select(s => s.RegistrationNumber).ToArray();
        }
        else
        {
            rollNumbers = string.IsNullOrWhiteSpace(request.RollNumbers)
                ? Array.Empty<string>()
                : request.RollNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            registrationNumbers = string.IsNullOrWhiteSpace(request.RegistrationNumbers)
                ? Array.Empty<string>()
                : request.RegistrationNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var xmlConfigPath = Path.Combine(_environment.ContentRootPath, "XmlConfigs", request.XmlConfigName);
        if (!System.IO.File.Exists(xmlConfigPath))
        {
            TempData["Error"] = $"Configuration file not found: {request.XmlConfigName}";
            return View(request);
        }

        var imageDataList = new List<MemoryStream>();
        var fileNames = new List<string>();

        foreach (var image in images)
        {
            var ms = new MemoryStream();
            await image.CopyToAsync(ms);
            ms.Position = 0;
            imageDataList.Add(ms);
            fileNames.Add(image.FileName);
        }

        var result = await _omrFillerService.ProcessImagesAsync(
            imageDataList.Cast<Stream>(),
            fileNames,
            xmlConfigPath,
            rollNumbers,
            registrationNumbers,
            request.SaqExtraText ?? "",
            request.DotSize);

        foreach (var ms in imageDataList)
        {
            ms.Dispose();
        }

        if (result.Success)
        {
            var uniqueImages = result.ProcessedImages.Select(p => p.OriginalFileName).Distinct().Count();
            var totalOutputs = result.ProcessedImages.Count;
            TempData["Success"] = $"Successfully processed {uniqueImages} image(s) into {totalOutputs} output(s).";
        }
        else
        {
            TempData["Error"] = "Some images failed to process. Check the results below.";
        }

        ViewBag.ProcessingResult = result;
        return View(request);
    }

    // Config Manager
    public IActionResult ConfigManager()
    {
        var configs = _omrFillerService.GetAvailableXmlConfigs();
        return View(configs);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadConfig(IFormFile configFile)
    {
        if (configFile == null || configFile.Length == 0)
        {
            TempData["Error"] = "Please select a file to upload.";
            return RedirectToAction(nameof(ConfigManager));
        }

        var ext = Path.GetExtension(configFile.FileName).ToLowerInvariant();
        if (ext != ".xml")
        {
            TempData["Error"] = "Only XML files are allowed.";
            return RedirectToAction(nameof(ConfigManager));
        }

        var configPath = Path.Combine(_environment.ContentRootPath, "XmlConfigs");
        if (!Directory.Exists(configPath))
            Directory.CreateDirectory(configPath);

        var filePath = Path.Combine(configPath, configFile.FileName);

        using var stream = new FileStream(filePath, FileMode.Create);
        await configFile.CopyToAsync(stream);

        TempData["Success"] = $"Configuration file '{configFile.FileName}' uploaded successfully.";
        return RedirectToAction(nameof(ConfigManager));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteConfig(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            TempData["Error"] = "Invalid file name.";
            return RedirectToAction(nameof(ConfigManager));
        }

        var filePath = Path.Combine(_environment.ContentRootPath, "XmlConfigs", fileName);

        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
            TempData["Success"] = $"Configuration file '{fileName}' deleted successfully.";
        }
        else
        {
            TempData["Error"] = "Configuration file not found.";
        }

        return RedirectToAction(nameof(ConfigManager));
    }

    // CSV Generator - Excel to CSV Converter for Load Testing
    public IActionResult CsvGenerator()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProcessExcel(IFormFile excelFile, int rowsPerFile = 1000, int? maxRows = null)
    {
        if (excelFile == null || excelFile.Length == 0)
        {
            return Json(new { error = "Please upload an Excel file." });
        }

        var ext = Path.GetExtension(excelFile.FileName).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xls")
        {
            return Json(new { error = "Only Excel files (.xlsx, .xls) are allowed." });
        }

        // Validate parameters
        rowsPerFile = Math.Clamp(rowsPerFile, 100, 10000);
        if (maxRows.HasValue)
        {
            maxRows = Math.Max(1, maxRows.Value);
        }

        try
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using var stream = excelFile.OpenReadStream();
            using var package = new ExcelPackage(stream);

            var worksheet = package.Workbook.Worksheets.FirstOrDefault();
            if (worksheet == null)
            {
                return Json(new { error = "No worksheet found in the Excel file." });
            }

            var rowCount = worksheet.Dimension?.Rows ?? 0;
            var colCount = worksheet.Dimension?.Columns ?? 0;

            if (rowCount == 0 || colCount == 0)
            {
                return Json(new { error = "The Excel file appears to be empty." });
            }

            // Get header row
            var headers = new List<string>();
            for (int col = 1; col <= colCount; col++)
            {
                headers.Add(worksheet.Cells[1, col].Text ?? "");
            }

            // Calculate data rows to process
            var dataRowCount = rowCount - 1; // Exclude header
            if (maxRows.HasValue && maxRows.Value < dataRowCount)
            {
                dataRowCount = maxRows.Value;
            }

            // Generate CSV files
            var csvFiles = new List<object>();
            var fileIndex = 1;

            for (int startRow = 2; startRow <= dataRowCount + 1; startRow += rowsPerFile)
            {
                var endRow = Math.Min(startRow + rowsPerFile - 1, dataRowCount + 1);
                var csvLines = new List<string>();

                // Add header
                csvLines.Add(string.Join("|", headers.Select(h => EscapeCsvField(h))));

                // Add data rows
                for (int row = startRow; row <= endRow; row++)
                {
                    var rowData = new List<string>();
                    for (int col = 1; col <= colCount; col++)
                    {
                        var cellValue = worksheet.Cells[row, col].Text ?? "";
                        rowData.Add(EscapeCsvField(cellValue));
                    }
                    csvLines.Add(string.Join("|", rowData));
                }

                csvFiles.Add(new
                {
                    id = fileIndex,
                    name = $"{fileIndex}.csv",
                    preview = csvLines.Take(10).ToList(),
                    rowCount = csvLines.Count - 1, // Exclude header
                    fullContent = csvLines
                });

                fileIndex++;
            }

            return Json(new { files = csvFiles, totalRows = dataRowCount });
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Error processing file: {ex.Message}" });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DownloadCsv([FromBody] CsvDownloadRequest request)
    {
        if (request?.Content == null || !request.Content.Any())
        {
            return BadRequest("No content provided.");
        }

        var csvContent = string.Join("\n", request.Content);
        var bytes = Encoding.UTF8.GetBytes(csvContent);

        return File(bytes, "text/csv", request.FileName ?? "data.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DownloadAllCsv([FromBody] List<CsvFileData> files)
    {
        if (files == null || !files.Any())
        {
            return BadRequest("No files provided.");
        }

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name, System.IO.Compression.CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                var content = string.Join("\n", file.FullContent);
                var bytes = Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }

        memoryStream.Position = 0;
        return File(memoryStream.ToArray(), "application/zip", "csv_files.zip");
    }

    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";

        // Escape for pipe-delimited CSV
        if (field.Contains('|') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    // Question Generator
    public IActionResult QuestionGenerator()
    {
        return View();
    }

    // Payment Approval Script Generator
    public IActionResult PaymentApproval()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GenerateMcq([FromBody] McqGenerationRequest request)
    {
        try
        {
            // Validate inputs
            if (request.QuestionNumber <= 0)
                return Json(new { success = false, message = "Number of questions must be greater than 0." });

            if (string.IsNullOrWhiteSpace(request.SubjectList))
                return Json(new { success = false, message = "Subject list is required." });

            if (string.IsNullOrWhiteSpace(request.SequenceList))
                return Json(new { success = false, message = "Sequence list is required." });

            var outputPath = Path.Combine(_environment.ContentRootPath, "Generated");
            Directory.CreateDirectory(outputPath);

            var templatePath = Path.Combine(_environment.ContentRootPath, "Question", "McqSample.docx");

            if (!System.IO.File.Exists(templatePath))
                return Json(new { success = false, message = $"Template file not found: McqSample.docx" });

            var createdFiles = McqQuestionGenerator.McqGenerateQuestionDoc(
                outputPath,
                templatePath,
                request.QuestionNumber,
                request.SubjectList,
                request.SequenceList,
                request.IncludeAnswerTags,
                request.MultiSet,
                request.SetCount
            );

            if (createdFiles.Count > 0)
            {
                return Json(new { success = true, files = createdFiles, message = "MCQ document(s) generated successfully!" });
            }

            return Json(new { success = false, message = "Failed to generate document(s)." });
        }
        catch (Exception ex)
        {
            var errorMessage = ex.InnerException != null
                ? $"Error: {ex.Message} - {ex.InnerException.Message}"
                : $"Error: {ex.Message}";
            return Json(new { success = false, message = errorMessage });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GenerateSaq([FromBody] SaqGenerationRequest request)
    {
        try
        {
            // Validate inputs
            if (request.QuestionNumber <= 0)
                return Json(new { success = false, message = "Number of questions must be greater than 0." });

            if (string.IsNullOrWhiteSpace(request.SubjectListEnglish))
                return Json(new { success = false, message = "English subject list is required." });

            if (request.QuestionType != "OW" && string.IsNullOrWhiteSpace(request.SubjectListBangla))
                return Json(new { success = false, message = "Bangla subject list is required for SAQ type." });

            if (string.IsNullOrWhiteSpace(request.SequenceList))
                return Json(new { success = false, message = "Sequence list is required." });

            var outputPath = Path.Combine(_environment.ContentRootPath, "Generated");
            Directory.CreateDirectory(outputPath);

            var templatePath = Path.Combine(_environment.ContentRootPath, "Question", "SaqSample.docx");

            if (!System.IO.File.Exists(templatePath))
                return Json(new { success = false, message = $"Template file not found: SaqSample.docx" });

            var files = SaqQuestionGenerator.CreateSaqQuestion(
                outputPath,
                templatePath,
                request.QuestionNumber,
                request.QuestionMark,
                request.SubjectListBangla ?? "",
                request.SubjectListEnglish,
                request.SequenceList,
                request.MultiSet,
                request.SetCount,
                request.QuestionType
            );

            if (files.Count > 0)
            {
                return Json(new { success = true, files = files.Take(10).ToList(), message = "SAQ documents generated successfully!" });
            }

            return Json(new { success = false, message = "Failed to generate documents." });
        }
        catch (Exception ex)
        {
            var errorMessage = ex.InnerException != null
                ? $"Error: {ex.Message} - {ex.InnerException.Message}"
                : $"Error: {ex.Message}";
            return Json(new { success = false, message = errorMessage });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAnswerTag(IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "No file uploaded." });

            var uploadsPath = Path.Combine(_environment.ContentRootPath, "Uploads");
            Directory.CreateDirectory(uploadsPath);

            var filePath = Path.Combine(uploadsPath, file.FileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            AddAnswerTagToDoc(filePath);

            var fileName = Path.GetFileName(filePath);
            return Json(new { success = true, fileName = fileName, message = "Answer tags added successfully!" });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSubjectName(IFormFile file, [FromForm] string subjectList, [FromForm] string sequenceList)
    {
        try
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "No file uploaded." });

            var uploadsPath = Path.Combine(_environment.ContentRootPath, "Uploads");
            Directory.CreateDirectory(uploadsPath);

            var filePath = Path.Combine(uploadsPath, file.FileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            AddSubjectNameToDoc(filePath, subjectList, sequenceList);

            var fileName = Path.GetFileName(filePath);
            return Json(new { success = true, fileName = fileName, message = "Subject names added successfully!" });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    [HttpGet]
    public IActionResult DownloadDoc(string fileName)
    {
        try
        {
            var generatedPath = Path.Combine(_environment.ContentRootPath, "Generated", fileName);
            var uploadsPath = Path.Combine(_environment.ContentRootPath, "Uploads", fileName);

            string? filePath = null;
            if (System.IO.File.Exists(generatedPath))
                filePath = generatedPath;
            else if (System.IO.File.Exists(uploadsPath))
                filePath = uploadsPath;

            if (filePath == null)
                return NotFound(new { success = false, message = "File not found." });

            var bytes = System.IO.File.ReadAllBytes(filePath);

            // Delete the file immediately after reading it for download
            try
            {
                System.IO.File.Delete(filePath);
            }
            catch { }

            return File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    private void AddAnswerTagToDoc(string filePath)
    {
        using (var doc = WordprocessingDocument.Open(filePath, true))
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var tables = body.Descendants<Table>().ToList();

            var optionRowMap = new Dictionary<string, int> { { "a", 2 }, { "b", 3 }, { "c", 4 }, { "d", 5 } };

            foreach (var table in tables)
            {
                try
                {
                    var rows = table.Descendants<TableRow>().ToList();
                    if (rows.Count < 8) continue;

                    var ansCell = rows[7].Descendants<TableCell>().FirstOrDefault();
                    if (ansCell == null) continue;
                    string ansText = string.Join("", ansCell.Descendants<Text>().Select(t => t.Text)).Trim().ToLowerInvariant();
                    if (!optionRowMap.TryGetValue(ansText, out int targetRowIndex)) continue;
                    if (targetRowIndex >= rows.Count) continue;

                    var targetRow = rows[targetRowIndex];
                    var cells = targetRow.Descendants<TableCell>().ToList();
                    for (int col = 0; col < Math.Min(2, cells.Count); col++)
                    {
                        var cell = cells[col];
                        var paragraphs = cell.Elements<Paragraph>().ToList();

                        if (paragraphs.Count == 0)
                        {
                            var p = new Paragraph(new Run(new Text("(Ans)")));
                            cell.AppendChild(p);
                        }
                        else
                        {
                            var lastParagraph = paragraphs.Last();
                            lastParagraph.AppendChild(new Run(new Text(" (Ans)")));
                        }
                    }
                }
                catch { continue; }
            }

            doc.MainDocumentPart.Document.Save();
        }
    }

    private void AddSubjectNameToDoc(string filePath, string subjectList, string sequenceList)
    {
        var subjects = subjectList
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToList();

        var sequences = sequenceList
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToList();

        if (subjects.Count != sequences.Count)
            throw new Exception("Subjects and sequences must have equal counts!");

        using (var doc = WordprocessingDocument.Open(filePath, true))
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var tables = body.Descendants<Table>().ToList();

            int tableIndex = 0;
            foreach (var table in tables)
            {
                tableIndex++;

                for (int i = 0; i < subjects.Count; i++)
                {
                    string subject = subjects[i];
                    string seq = sequences[i];

                    var parts = seq.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 ||
                        !int.TryParse(parts[0], out int start) ||
                        !int.TryParse(parts[1], out int end))
                    {
                        continue;
                    }

                    if (tableIndex >= start && tableIndex <= end)
                    {
                        try
                        {
                            var rows = table.Descendants<TableRow>().ToList();
                            if (rows.Count == 0) continue;
                            var firstRow = rows[0];
                            var cells = firstRow.Descendants<TableCell>().ToList();
                            if (cells.Count < 2) continue;

                            var subjectCell = cells[1];
                            subjectCell.RemoveAllChildren<Paragraph>();
                            subjectCell.AppendChild(new Paragraph(new Run(new Text(subject))));
                        }
                        catch { }
                    }
                }
            }

            doc.MainDocumentPart.Document.Save();
        }
    }

    public class McqGenerationRequest
    {
        public int QuestionNumber { get; set; } = 100;
        public string SubjectList { get; set; } = "phy, chem, math, bio";
        public string SequenceList { get; set; } = "1-25,26-50,51-75,76-100";
        public bool IncludeAnswerTags { get; set; } = false;
        public bool MultiSet { get; set; } = false;
        public int SetCount { get; set; } = 1;
    }

    public class SaqGenerationRequest
    {
        public int QuestionNumber { get; set; } = 100;
        public int QuestionMark { get; set; } = 2;
        public string SubjectListBangla { get; set; } = "";
        public string SubjectListEnglish { get; set; } = "";
        public string SequenceList { get; set; } = "1-50,51-100";
        public bool MultiSet { get; set; } = false;
        public int SetCount { get; set; } = 1;
        public string QuestionType { get; set; } = "SAQ";
    }

    public class CsvDownloadRequest
    {
        public string? FileName { get; set; }
        public List<string>? Content { get; set; }
    }

    public class CsvFileData
    {
        public string Name { get; set; } = "";
        public List<string> FullContent { get; set; } = new();
    }
}
