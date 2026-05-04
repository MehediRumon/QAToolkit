using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Xml.Linq;
using OfficeOpenXml;
using QAToolkit.Models.Omr;

namespace QAToolkit.Services;

public class OmrFillerService : IOmrFillerService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<OmrFillerService> _logger;

    public OmrFillerService(IWebHostEnvironment environment, ILogger<OmrFillerService> logger)
    {
        _environment = environment;
        _logger = logger;
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    public List<string> GetAvailableXmlConfigs()
    {
        var configPath = Path.Combine(_environment.ContentRootPath, "XmlConfigs");
        if (!Directory.Exists(configPath))
        {
            Directory.CreateDirectory(configPath);
            return new List<string>();
        }

        return Directory.GetFiles(configPath, "*.xml")
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .ToList();
    }

    public List<StudentData> ParseExcelFile(Stream excelStream)
    {
        var students = new List<StudentData>();

        using var package = new ExcelPackage(excelStream);
        var worksheet = package.Workbook.Worksheets.FirstOrDefault();

        if (worksheet == null)
            return students;

        int rowCount = worksheet.Dimension?.Rows ?? 0;

        for (int row = 2; row <= rowCount; row++)
        {
            var rollNumber = worksheet.Cells[row, 1].Text?.Trim() ?? "";
            var registrationNumber = worksheet.Cells[row, 2].Text?.Trim() ?? "";

            if (!string.IsNullOrEmpty(rollNumber) || !string.IsNullOrEmpty(registrationNumber))
            {
                students.Add(new StudentData
                {
                    RollNumber = rollNumber,
                    RegistrationNumber = registrationNumber
                });
            }
        }

        return students;
    }

    private int _currentDotSize = 22; // Default dot size

    public async Task<OmrProcessingResult> ProcessImagesAsync(
        IEnumerable<Stream> imageStreams,
        IEnumerable<string> fileNames,
        string xmlConfigPath,
        string[] rollNumbers,
        string[] registrationNumbers,
        string saqExtraText,
        int? dotSize = null)
    {
        _currentDotSize = dotSize ?? 22; // Use provided size or default
        var result = new OmrProcessingResult { Success = true };

        string sessionId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}".Substring(0, 30);
        result.OutputSessionId = sessionId;

        var streamList = imageStreams.ToList();
        var nameList = fileNames.ToList();

        // Pre-load all images into byte arrays (so we can reuse them)
        var imageBytesList = new List<byte[]>();
        foreach (var stream in streamList)
        {
            if (stream.CanSeek) stream.Position = 0;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            imageBytesList.Add(ms.ToArray());
        }

        var sessionOutputPath = Path.Combine(_environment.WebRootPath, "outputs", sessionId);
        if (!Directory.Exists(sessionOutputPath))
            Directory.CreateDirectory(sessionOutputPath);

        int studentCount = Math.Max(rollNumbers.Length, registrationNumbers.Length);
        int imageCount = imageBytesList.Count;

        // 1:1 mapping - each image pairs with corresponding student data
        // Process count = number of images (each image gets one student's data)
        int processCount = imageCount;

        _logger.LogInformation("Processing {ImageCount} images with {StudentCount} students (1:1 mapping, ProcessCount: {ProcessCount})",
            imageCount, studentCount, processCount);

        for (int idx = 0; idx < processCount; idx++)
        {
            // 1:1 mapping - image index matches student index
            byte[] imageBytes = imageBytesList[idx];
            var originalFileName = nameList[idx];

            // Get student data (use empty if index exceeds student count)
            string currentRoll = idx < rollNumbers.Length ? rollNumbers[idx] : "";
            string currentReg = idx < registrationNumbers.Length ? registrationNumbers[idx] : "";

            var imageResult = new ProcessedImageResult
            {
                OriginalFileName = originalFileName
            };

            try
            {
                using var imageStream = new MemoryStream(imageBytes);
                using var originalBmp = new Bitmap(imageStream);
                using var workBmp = new Bitmap(originalBmp);
                workBmp.SetResolution(originalBmp.HorizontalResolution, originalBmp.VerticalResolution);

                using var g = Graphics.FromImage(workBmp);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                var gridData = DetectAndCalculateGrid(workBmp);

                if (gridData != null)
                {
                    var omrNoRegion = LoadOmrInfoRegion(xmlConfigPath);
                    string detectedPageId = DetectPageIdFromImage(workBmp, gridData, omrNoRegion);
                    imageResult.DetectedPageId = detectedPageId;
                    imageResult.Logs.Add($"Detected Page ID: {detectedPageId}");

                    imageResult.AssignedRoll = currentRoll;
                    imageResult.AssignedRegistration = currentReg;
                    imageResult.Logs.Add($"Entry {idx + 1}: Roll={currentRoll}, Reg={currentReg}");

                    var activeRegions = LoadRegionsForPage(xmlConfigPath, detectedPageId);

                    foreach (var region in activeRegions)
                    {
                        if (region.MemberName == "rollNo")
                        {
                            DrawMappedField(g, gridData, region, currentRoll);
                            imageResult.Logs.Add($"Drew roll number: {currentRoll}");
                        }
                        else if (region.MemberName == "registrationNo")
                        {
                            DrawMappedField(g, gridData, region, currentReg);
                            imageResult.Logs.Add($"Drew registration number: {currentReg}");
                        }
                        else if (region.MemberName.StartsWith("answer"))
                        {
                            var answers = DrawRandomAnswers(g, gridData, region);
                            imageResult.Logs.Add($"Drew answers for {region.MemberName}: {answers}");
                        }
                        else if (region.MemberName.StartsWith("SaqAnswer"))
                        {
                            DrawSaqMappingLabel(g, gridData, region, saqExtraText);
                            imageResult.Logs.Add($"Drew SAQ label: {region.MemberName}");
                        }
                    }

                    string subFolder = string.IsNullOrEmpty(currentReg) ? $"student_{idx + 1}" : currentReg;
                    var regFolderPath = Path.Combine(sessionOutputPath, subFolder);
                    if (!Directory.Exists(regFolderPath))
                        Directory.CreateDirectory(regFolderPath);

                    imageResult.OutputSubFolder = subFolder;

                    using var outputStream = new MemoryStream();
                    workBmp.Save(outputStream, ImageFormat.Jpeg);

                    string baseName = Path.GetFileNameWithoutExtension(originalFileName);
                    string ext = Path.GetExtension(originalFileName);
                    imageResult.OutputFileName = $"{baseName}_{currentReg}_{DateTime.Now:HHmmss_fff}{ext}";

                    var fullOutputPath = Path.Combine(regFolderPath, imageResult.OutputFileName);
                    await File.WriteAllBytesAsync(fullOutputPath, outputStream.ToArray());
                }
                else
                {
                    imageResult.Logs.Add("FAILED: Grid detection failed.");
                    result.Success = false;
                }
            }
            catch (Exception ex)
            {
                imageResult.Logs.Add($"Error: {ex.Message}");
                result.Success = false;
                _logger.LogError(ex, "Error processing entry {Index} for image {FileName}",
                    idx, originalFileName);
            }

            result.ProcessedImages.Add(imageResult);
        }

        var registrationFolders = Directory.GetDirectories(sessionOutputPath);
        foreach (var regFolder in registrationFolders)
        {
            var regNumber = Path.GetFileName(regFolder);
            var zipFileName = await CreateRegistrationZipAsync(sessionId, regNumber);
            result.RegistrationZipFiles[regNumber] = zipFileName;
        }

        result.MasterZipFileName = await CreateMasterZipAsync(sessionId);

        return result;
    }

    public async Task<string> CreateRegistrationZipAsync(string sessionId, string registrationNumber)
    {
        var sessionPath = Path.Combine(_environment.WebRootPath, "outputs", sessionId);
        var regFolderPath = Path.Combine(sessionPath, registrationNumber);
        var zipFileName = $"{registrationNumber}.zip";
        var zipFilePath = Path.Combine(sessionPath, zipFileName);

        if (Directory.Exists(regFolderPath))
        {
            if (File.Exists(zipFilePath))
                File.Delete(zipFilePath);

            await Task.Run(() => ZipFile.CreateFromDirectory(regFolderPath, zipFilePath));
        }

        return zipFileName;
    }

    public async Task<string> CreateMasterZipAsync(string sessionId)
    {
        var sessionPath = Path.Combine(_environment.WebRootPath, "outputs", sessionId);
        var masterZipFileName = $"All_Results_{sessionId}.zip";
        var masterZipPath = Path.Combine(sessionPath, masterZipFileName);

        if (File.Exists(masterZipPath))
            File.Delete(masterZipPath);

        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(masterZipPath, ZipArchiveMode.Create);

            // Put all images in one folder (no subfolders)
            var registrationFolders = Directory.GetDirectories(sessionPath);
            foreach (var regFolder in registrationFolders)
            {
                var files = Directory.GetFiles(regFolder);
                foreach (var file in files)
                {
                    // All files go directly in zip root (no subfolder)
                    archive.CreateEntryFromFile(file, Path.GetFileName(file));
                }
            }
        });

        return masterZipFileName;
    }

    public OmrRegion? LoadOmrInfoRegion(string path)
    {
        try
        {
            var element = XDocument.Load(path)
                .Descendants("omrInfo")
                .FirstOrDefault(r => r.Attribute("memberName")?.Value == "omrNo");
            return ParseRegion(element);
        }
        catch
        {
            return null;
        }
    }

    public List<OmrRegion> LoadRegionsForPage(string path, string pageId)
    {
        var regions = new List<OmrRegion>();
        try
        {
            var tag = XDocument.Load(path)
                .Descendants("omr")
                .FirstOrDefault(o => o.Attribute("omrNo")?.Value == pageId);

            if (tag != null)
            {
                foreach (var elem in tag.Descendants("region"))
                {
                    var region = ParseRegion(elem);
                    if (region != null)
                        regions.Add(region);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading regions for page {PageId}", pageId);
        }
        return regions;
    }

    private OmrRegion? ParseRegion(XElement? e)
    {
        if (e == null) return null;

        return new OmrRegion
        {
            MemberName = e.Attribute("memberName")?.Value ?? "",
            Direction = e.Attribute("direction")?.Value ?? "y",
            CharSet = e.Attribute("charSet")?.Value ?? "",
            Lines = int.Parse(e.Attribute("lines")?.Value ?? "1"),
            StartX = int.Parse(e.Element("startCircle")?.Attribute("x")?.Value ?? "1"),
            StartY = int.Parse(e.Element("startCircle")?.Attribute("y")?.Value ?? "1"),
            PaddingX = float.Parse(e.Element("startPadding")?.Attribute("x")?.Value ?? "0"),
            PaddingY = float.Parse(e.Element("startPadding")?.Attribute("y")?.Value ?? "0"),
            SpacingX = float.Parse(e.Element("spacing")?.Attribute("x")?.Value ?? "0"),
            SpacingY = float.Parse(e.Element("spacing")?.Attribute("y")?.Value ?? "0"),
            ResultCountCellX = float.Parse(e.Attribute("resultCountCellX")?.Value ?? "0"),
            ResultCountCellY = float.Parse(e.Attribute("resultCountCellY")?.Value ?? "0"),
            BlackPixelPercent = double.Parse(e.Attribute("blackPixelPercent")?.Value ?? "20")
        };
    }

    private GridSystemData? DetectAndCalculateGrid(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var lt = FindMarkerRect(bmp, 0, 0, w / 5, h / 4, true);
        var rt = FindMarkerRect(bmp, w - (w / 5), 0, w, h / 4, true);

        if (lt.Width == 0 || rt.Width == 0) return null;

        var lb = FindMarkerRect(bmp, Math.Max(0, lt.X - 20), h * 3 / 4, Math.Min(w, lt.Right + 20), h, false);
        if (lb.Width == 0) return null;

        int scanX = (lt.X + lb.X) / 2 + (lt.Width / 2);
        var gridRows = FindTimingMarks(bmp, scanX, lt.Bottom + 5, lb.Top - 5);
        if (gridRows.Count == 0) return null;

        var data = new GridSystemData();
        data.YLines.Add(lt.Bottom);
        foreach (var row in gridRows) data.YLines.Add(row.Top);
        data.YLines.Add(lb.Top);

        float totalDistY = gridRows.Last().Top - gridRows.First().Top;
        data.StepY = totalDistY / (gridRows.Count - 1);
        data.StepX = data.StepY;

        float startX = lt.Right;
        float endLimitX = rt.Left;
        float currentX = startX;
        while (currentX <= endLimitX + 2)
        {
            data.XLines.Add(currentX);
            currentX += data.StepX;
        }

        return data;
    }

    private string DetectPageIdFromImage(Bitmap bmp, GridSystemData grid, OmrRegion? region)
    {
        if (region == null) return "1";

        string[] pageIds = string.IsNullOrEmpty(region.CharSet) ? new string[] { "1" } : region.CharSet.Select(c => c.ToString()).ToArray();

        if (pageIds.Length == 1) return pageIds[0];

        int bestMatchIndex = -1;
        double maxDarkness = -1;

        for (int i = 0; i < pageIds.Length; i++)
        {
            float originX = grid.XLines[region.StartX - 1];
            float originY = grid.YLines[region.StartY - 1];
            float checkX = originX + (region.PaddingX * grid.StepX);
            float checkY = originY + (region.PaddingY * grid.StepY);

            if (region.Direction?.ToLower() == "x")
                checkX += (i * grid.StepX * (1.0f + region.SpacingX));
            else
                checkY += (i * grid.StepY * (1.0f + region.SpacingY));

            double darkness = GetDarknessPercentage(bmp, (int)(checkX + grid.StepX / 2), (int)(checkY + grid.StepY / 2), 10);

            if (darkness > region.BlackPixelPercent && darkness > maxDarkness)
            {
                maxDarkness = darkness;
                bestMatchIndex = i;
            }
        }

        return bestMatchIndex != -1 ? pageIds[bestMatchIndex] : "1";
    }

    private unsafe double GetDarknessPercentage(Bitmap bmp, int cx, int cy, int r)
    {
        int black = 0, total = 0;
        int sx = Math.Max(0, cx - r);
        int sy = Math.Max(0, cy - r);
        int ex = Math.Min(bmp.Width, cx + r);
        int ey = Math.Min(bmp.Height, cy + r);

        var bData = bmp.LockBits(new Rectangle(sx, sy, ex - sx, ey - sy), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            byte* ptr = (byte*)bData.Scan0;
            for (int y = 0; y < (ey - sy); y++)
            {
                byte* row = ptr + (y * bData.Stride);
                for (int x = 0; x < (ex - sx); x++)
                {
                    if (row[x * 3] < 80) black++;
                    total++;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bData);
        }

        return total > 0 ? (double)black / total * 100 : 0;
    }

    private unsafe List<Rectangle> FindTimingMarks(Bitmap bmp, int scanX, int startY, int endY)
    {
        var found = new List<Rectangle>();
        var bData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            bool inBlock = false;
            int blockStart = -1;
            byte* ptr = (byte*)bData.Scan0;

            for (int y = startY; y < endY; y++)
            {
                byte* pixel = ptr + (y * bData.Stride) + (scanX * 3);

                if (pixel[0] < 100)
                {
                    if (!inBlock)
                    {
                        inBlock = true;
                        blockStart = y;
                    }
                }
                else if (inBlock)
                {
                    if (y - blockStart > 3)
                        found.Add(new Rectangle(scanX - 10, blockStart, 20, y - blockStart));
                    inBlock = false;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bData);
        }

        return found;
    }

    private unsafe Rectangle FindMarkerRect(Bitmap bmp, int startX, int startY, int endX, int endY, bool isTopDown)
    {
        var bData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        bool found = false;

        try
        {
            byte* ptr = (byte*)bData.Scan0;
            int yStart = isTopDown ? startY : endY - 1;
            int yEnd = isTopDown ? endY : startY;
            int yStep = isTopDown ? 1 : -1;

            for (int y = yStart; y != yEnd; y += yStep)
            {
                byte* row = ptr + (y * bData.Stride);
                bool rowBlack = false;

                for (int x = startX; x < endX; x++)
                {
                    if (row[x * 3] < 80)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        found = true;
                        rowBlack = true;
                    }
                }

                if (found && !rowBlack && (maxY - minY) > 10)
                    break;
            }
        }
        finally
        {
            bmp.UnlockBits(bData);
        }

        return found ? new Rectangle(minX, minY, maxX - minX, maxY - minY) : Rectangle.Empty;
    }

    private void DrawMappedField(Graphics g, GridSystemData grid, OmrRegion region, string value)
    {
        if (region.StartX > grid.XLines.Count || region.StartY > grid.YLines.Count) return;

        float originX = grid.XLines[region.StartX - 1];
        float originY = grid.YLines[region.StartY - 1];

        for (int i = 0; i < value.Length; i++)
        {
            char digit = value[i];
            int charIndex = region.CharSet.IndexOf(digit);
            if (charIndex == -1) continue;

            float lineX = originX + (region.PaddingX * grid.StepX) + (i * grid.StepX * (1.0f + region.SpacingX));
            float lineY = originY + (region.PaddingY * grid.StepY) + (charIndex * grid.StepY * (1.0f + region.SpacingY));
            DrawDot(g, lineX, lineY, grid.StepX, grid.StepY);
        }
    }

    private string DrawRandomAnswers(Graphics g, GridSystemData grid, OmrRegion region)
    {
        var rnd = new Random();
        float originX = grid.XLines[region.StartX - 1];
        float originY = grid.YLines[region.StartY - 1];
        var resultLog = new List<string>();

        for (int q = 0; q < region.Lines; q++)
        {
            int ansIndex = rnd.Next(0, region.CharSet.Length);
            float lineX = originX + (region.PaddingX * grid.StepX) + (ansIndex * grid.StepX * (1.0f + region.SpacingX));
            float lineY = originY + (region.PaddingY * grid.StepY) + (q * grid.StepY * (1.0f + region.SpacingY));
            DrawDot(g, lineX, lineY, grid.StepX, grid.StepY);

            char answerChar = region.CharSet.Length > ansIndex ? region.CharSet[ansIndex] : '?';
            resultLog.Add($"{q + 1}-{answerChar}");
        }

        return string.Join(", ", resultLog);
    }

    private void DrawSaqMappingLabel(Graphics g, GridSystemData grid, OmrRegion region, string extraText = null)
    {
        if (region.StartX > grid.XLines.Count || region.StartY > grid.YLines.Count) return;

        float originX = grid.XLines[region.StartX - 1];
        float originY = grid.YLines[region.StartY - 1];
        float startPixelX = originX + (region.PaddingX * grid.StepX);
        float startPixelY = originY + (region.PaddingY * grid.StepY);

        using (Font font = new Font("Arial", 7, FontStyle.Bold))
        using (Brush brush = new SolidBrush(Color.Black))
        using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            // লজিক চেক: resultCountCell ডাটা আছে কিনা (বড় এরিয়া)
            if (region.ResultCountCellX > 0 && region.ResultCountCellY > 0)
            {
                float totalWidth = region.ResultCountCellX * grid.StepX;
                float totalHeight = region.ResultCountCellY * grid.StepY;

                // --- EXTRA TEXT LOGIC (BOUNDED RECT — text wrap হবে) ---
                if (!string.IsNullOrEmpty(extraText))
                {
                    // Extra Text উপরের অর্ধেকে (RectangleF দিয়ে bound করা, লম্বা text wrap হবে)
                    float halfHeight = totalHeight / 2.0f;
                    RectangleF extraRect = new RectangleF(startPixelX, startPixelY, totalWidth, halfHeight);
                    g.DrawString(extraText, font, brush, extraRect, format);

                    // Main Label নিচের অর্ধেকে
                    RectangleF labelRect = new RectangleF(startPixelX, startPixelY + halfHeight, totalWidth, halfHeight);
                    g.DrawString(region.MemberName, font, brush, labelRect, format);
                }
                else
                {
                    RectangleF fullRect = new RectangleF(startPixelX, startPixelY, totalWidth, totalHeight);
                    g.DrawString(region.MemberName, font, brush, fullRect, format);
                }
            }
            else
            {
                // Fallback Logic (Line by Line) — RectangleF bound সহ
                int count = region.Lines > 0 ? region.Lines : 1;
                // Available width: left marker থেকে right marker পর্যন্ত
                float availableWidth = grid.XLines.Last() - grid.XLines[region.StartX - 1];

                for (int i = 0; i < count; i++)
                {
                    float spacingFactorY = 1.0f + region.SpacingY;
                    float cellTopY = startPixelY + (i * grid.StepY * spacingFactorY);
                    float cellHeight = grid.StepY;

                    string label = count > 1 ? $"{region.MemberName} {i + 1}" : region.MemberName;

                    // --- EXTRA TEXT LOGIC (BOUNDED RECT) ---
                    if (!string.IsNullOrEmpty(extraText))
                    {
                        float halfH = cellHeight / 2.0f;
                        RectangleF extraRect = new RectangleF(startPixelX, cellTopY, availableWidth, halfH);
                        g.DrawString(extraText, font, brush, extraRect, format);

                        RectangleF labelRect = new RectangleF(startPixelX, cellTopY + halfH, availableWidth, halfH);
                        g.DrawString(label, font, brush, labelRect, format);
                    }
                    else
                    {
                        RectangleF cellRect = new RectangleF(startPixelX, cellTopY, availableWidth, cellHeight);
                        g.DrawString(label, font, brush, cellRect, format);
                    }
                }
            }
        }
    }

    private void DrawDot(Graphics g, float x, float y, float stepX, float stepY)
    {
        float centerX = x + (stepX * 0.5f);
        float centerY = y + (stepY * 0.5f);
        int size = _currentDotSize;

        using var brush = new SolidBrush(Color.Black);
        g.FillEllipse(brush, centerX - (size / 2), centerY - (size / 2), size, size);
    }
}
