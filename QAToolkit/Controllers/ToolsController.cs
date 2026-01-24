using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QAToolkit.Models.Omr;
using QAToolkit.Services;

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
}
