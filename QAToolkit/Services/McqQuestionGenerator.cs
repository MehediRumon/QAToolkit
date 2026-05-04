using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.Drawing;
using System.IO.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace QAToolkit.Services;

public static class McqQuestionGenerator
{
    private class SubjectRange
    {
        public string Subject { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
    }

    public static List<string> McqGenerateQuestionDoc(
        string outputDirectoryPath,
        string templatePath,
        int questionNumber,
        string subjectList,
        string sequenceList,
        bool includeAnswerTags = false,
        bool multiSet = false,
        int setCount = 1)
    {
        if (questionNumber <= 0)
            throw new ArgumentException("Question number must be 1 or greater.", nameof(questionNumber));

        if (string.IsNullOrWhiteSpace(outputDirectoryPath))
            throw new ArgumentException("Output directory path cannot be empty.", nameof(outputDirectoryPath));

        if (!Directory.Exists(outputDirectoryPath))
        {
            Directory.CreateDirectory(outputDirectoryPath);
        }

        if (!File.Exists(templatePath))
            throw new FileNotFoundException("McqSample.docx not found.", templatePath);

        var subjectRanges = BuildSubjectRanges(subjectList, sequenceList);

        int sets = (multiSet && setCount > 1) ? setCount : 1;
        string batchId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var createdFiles = new List<string>();

        for (int setIndex = 1; setIndex <= sets; setIndex++)
        {
            string outputFileName = $"GeneratedQuestions_{batchId}_Set{setIndex}.docx";
            string finalDocPath = Path.Combine(outputDirectoryPath, outputFileName);

            using (var outDoc = WordprocessingDocument.Create(finalDocPath, WordprocessingDocumentType.Document))
            {
                var outMain = outDoc.AddMainDocumentPart();
                outMain.Document = new W.Document(new W.Body());
                var body = outMain.Document.Body;

                int sampleTableCount;
                using (var sampleDocCount = WordprocessingDocument.Open(templatePath, false))
                {
                    var mainPart = sampleDocCount.MainDocumentPart;
                    if (mainPart == null || mainPart.Document == null || mainPart.Document.Body == null)
                        throw new InvalidOperationException("Template document is invalid or corrupted.");

                    sampleTableCount = mainPart.Document.Body.Descendants<W.Table>().Count();
                    if (sampleTableCount == 0)
                        throw new InvalidOperationException("Template document contains no question tables.");
                }

                for (int q = 1; q <= questionNumber; q++)
                {
                    int sourceIndex = ((q - 1) % sampleTableCount);
                    using (var sampleDoc = WordprocessingDocument.Open(templatePath, false))
                    {
                        var sourceMain = sampleDoc.MainDocumentPart;
                        if (sourceMain?.Document?.Body == null)
                            throw new InvalidOperationException($"Failed to read template for question {q}.");

                        var tables = sourceMain.Document.Body.Descendants<W.Table>().ToList();
                        if (sourceIndex >= tables.Count)
                            throw new InvalidOperationException($"Template table index {sourceIndex} not found for question {q}.");

                        var sourceTable = tables[sourceIndex];
                        var newTable = CopyTableWithImages(sourceTable, sampleDoc, sourceMain, outMain);

                        EnsureTableBorders(newTable);
                        SetCellText(newTable, 0, 0, q.ToString());
                        string subject = GetSubjectForQuestion(q, subjectRanges) ?? string.Empty;
                        SetCellText(newTable, 0, 1, subject);

                        if (includeAnswerTags)
                        {
                            ApplyAnswerTagRobust(newTable);
                        }

                        if (sets > 1)
                        {
                            AppendLatinLabelRunToRow(newTable, 1, $" [Set-{setIndex}] ");
                        }

                        body!.AppendChild(newTable);
                        body.AppendChild(new W.Paragraph(new W.Run(new W.Text(" "))));
                    }
                }

                EnsureWordDefaults(outDoc);
                EnsureSectionPrLast(outDoc);
                StripHeaderFooterDrawings(outDoc.MainDocumentPart);
                SanitizeRelationships(outDoc);

                var validator = new OpenXmlValidator(FileFormatVersions.Office2013);
                var errors = validator.Validate(outDoc).ToList();
                if (errors.Count > 0)
                {
                    foreach (var d in outDoc.MainDocumentPart.Document.Body!.Descendants<W.Drawing>().ToList())
                    {
                        NormalizeDrawingToInline(d);
                    }
                }

                outMain.Document.Save();
            }

            FixImagePaths(finalDocPath);
            createdFiles.Add(outputFileName);
        }

        return createdFiles;
    }

    private static void FixImagePaths(string docPath)
    {
        try
        {
            using (var package = Package.Open(docPath, FileMode.Open, FileAccess.ReadWrite))
            {
                var imageParts = package.GetParts().Where(p =>
                    p.Uri.ToString().StartsWith("/media/") &&
                    (p.ContentType.StartsWith("image/"))).ToList();

                if (imageParts.Count == 0) return;

                var uriMap = new Dictionary<string, string>();

                foreach (var oldPart in imageParts)
                {
                    string oldUri = oldPart.Uri.ToString();
                    string fileName = Path.GetFileName(oldUri);
                    string newUri = $"/word/media/{fileName}";

                    uriMap[oldUri] = newUri;

                    var newPart = package.CreatePart(new Uri(newUri, UriKind.Relative),
                        oldPart.ContentType, CompressionOption.Normal);

                    using (var oldStream = oldPart.GetStream())
                    using (var newStream = newPart.GetStream())
                    {
                        oldStream.CopyTo(newStream);
                    }
                }

                try
                {
                    var docRelsPart = package.GetPart(new Uri("/word/_rels/document.xml.rels", UriKind.Relative));
                    string relsContent;
                    using (var stream = docRelsPart.GetStream())
                    using (var reader = new StreamReader(stream))
                    {
                        relsContent = reader.ReadToEnd();
                    }

                    foreach (var mapping in uriMap)
                    {
                        string oldTarget = $"Target=\"{mapping.Key}\"";
                        string newTarget = $"Target=\"media/{Path.GetFileName(mapping.Value)}\"";
                        relsContent = relsContent.Replace(oldTarget, newTarget);
                    }

                    using (var stream = docRelsPart.GetStream(FileMode.Create))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(relsContent);
                    }
                }
                catch (ArgumentException) { }

                foreach (var oldPart in imageParts)
                {
                    package.DeletePart(oldPart.Uri);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Failed to fix image paths: {ex.Message}");
        }
    }

    private static void EnsureWordDefaults(WordprocessingDocument doc)
    {
        var body = doc.MainDocumentPart!.Document.Body!;
        if (doc.MainDocumentPart.StyleDefinitionsPart == null)
        {
            var stylesPart = doc.MainDocumentPart.AddNewPart<StyleDefinitionsPart>();
            var styles = new W.Styles(
                new W.DocDefaults(
                    new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(new W.RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Calibri" }, new W.FontSize() { Val = "22" })),
                    new W.ParagraphPropertiesDefault(new W.ParagraphProperties())
                ),
                new W.Style(new W.StyleName() { Val = "Normal" }) { Type = W.StyleValues.Paragraph, Default = true, StyleId = "Normal" }
            );
            stylesPart.Styles = styles;
            stylesPart.Styles.Save();
        }

        if (doc.MainDocumentPart.DocumentSettingsPart == null)
        {
            var settingsPart = doc.MainDocumentPart.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings = new W.Settings(new W.Compatibility());
            settingsPart.Settings.Save();
        }

        if (body.GetFirstChild<W.SectionProperties>() == null)
        {
            body.AppendChild(new W.SectionProperties(
                new W.PageSize() { Width = 11906, Height = 16838 },
                new W.PageMargin() { Top = 720, Right = 720, Bottom = 720, Left = 720 }
            ));
        }
    }

    private static void EnsureSectionPrLast(WordprocessingDocument doc)
    {
        var body = doc.MainDocumentPart!.Document.Body!;
        var sectPr = body.Elements<W.SectionProperties>().LastOrDefault();
        if (sectPr == null)
        {
            sectPr = new W.SectionProperties(new W.PageSize() { Width = 11906, Height = 16838 }, new W.PageMargin() { Top = 720, Right = 720, Bottom = 720, Left = 720 });
            body.AppendChild(sectPr);
        }
        else if (body.LastChild != sectPr)
        {
            sectPr.Remove();
            body.AppendChild(sectPr);
        }
    }

    private static void StripHeaderFooterDrawings(MainDocumentPart main)
    {
        foreach (var hp in main.HeaderParts.ToList())
        {
            var hdr = hp.Header;
            if (hdr == null) continue;
            foreach (var d in hdr.Descendants<W.Drawing>().ToList()) d.Remove();
            foreach (var v in hdr.Descendants<DocumentFormat.OpenXml.Vml.Shape>().ToList()) v.Remove();
            foreach (var v in hdr.Descendants<DocumentFormat.OpenXml.Vml.ImageData>().ToList()) v.Remove();
            foreach (var unk in hdr.Descendants<OpenXmlUnknownElement>().ToList())
            {
                if (string.Equals(unk.LocalName, "pict", StringComparison.OrdinalIgnoreCase)) unk.Remove();
            }
            hp.Header.Save();
        }
        foreach (var fp in main.FooterParts.ToList())
        {
            var ftr = fp.Footer;
            if (ftr == null) continue;
            foreach (var d in ftr.Descendants<W.Drawing>().ToList()) d.Remove();
            foreach (var v in ftr.Descendants<DocumentFormat.OpenXml.Vml.Shape>().ToList()) v.Remove();
            foreach (var v in ftr.Descendants<DocumentFormat.OpenXml.Vml.ImageData>().ToList()) v.Remove();
            foreach (var unk in ftr.Descendants<OpenXmlUnknownElement>().ToList())
            {
                if (string.Equals(unk.LocalName, "pict", StringComparison.OrdinalIgnoreCase)) unk.Remove();
            }
            fp.Footer.Save();
        }
    }

    private static void SanitizeRelationships(WordprocessingDocument doc)
    {
        foreach (var part in doc.Parts.Select(p => p.OpenXmlPart).Concat(new[] { (OpenXmlPart)doc.MainDocumentPart! }))
        {
            if (part is MainDocumentPart mdp)
            {
                foreach (var rel in mdp.HyperlinkRelationships.ToList()) mdp.DeleteReferenceRelationship(rel);
                foreach (var r in mdp.ExternalRelationships.ToList()) mdp.DeleteExternalRelationship(r);
            }

            var blips = part.RootElement?.Descendants<A.Blip>().ToList();
            if (blips != null)
            {
                foreach (var b in blips)
                {
                    b.Link = null;
                }
            }
        }
    }

    private static ImagePart? TryResolveImagePart(WordprocessingDocument sourceDoc, OpenXmlPart containerPart, string? relId)
    {
        if (string.IsNullOrEmpty(relId)) return null;
        try { var part = containerPart.GetPartById(relId) as ImagePart; if (part != null) return part; } catch {}
        try { var part = sourceDoc.MainDocumentPart!.GetPartById(relId) as ImagePart; if (part != null) return part; } catch {}
        var relatedParts = new List<OpenXmlPart>();
        relatedParts.AddRange(sourceDoc.MainDocumentPart!.HeaderParts);
        relatedParts.AddRange(sourceDoc.MainDocumentPart.FooterParts);
        relatedParts.AddRange(sourceDoc.MainDocumentPart.ChartParts);
        relatedParts.AddRange(sourceDoc.MainDocumentPart.ImageParts);
        foreach (var rp in relatedParts)
        {
            try { var p = rp.GetPartById(relId) as ImagePart; if (p != null) return p; } catch {}
        }
        foreach (var p in sourceDoc.Parts.Select(pp => pp.OpenXmlPart))
        {
            if (p is ImagePart ip)
            {
                try { var id = sourceDoc.MainDocumentPart.GetIdOfPart(ip); if (id == relId) return ip; } catch {}
            }
        }
        return null;
    }

    private static string NormalizeImageContentType(string contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return "image/png";
        var ct = contentType.ToLowerInvariant();
        if (ct == "image/png" || ct == "image/jpeg" || ct == "image/jpg") return ct == "image/jpg" ? "image/jpeg" : ct;
        return "image/png";
    }

    private static void CopyImageDataToPart(ImagePart sourceImgPart, ImagePart targetImgPart)
    {
        var ct = sourceImgPart.ContentType.ToLowerInvariant();
        if (ct == "image/png" || ct == "image/jpeg")
        {
            using (var s = sourceImgPart.GetStream())
            using (var t = targetImgPart.GetStream(FileMode.Create, FileAccess.Write))
            {
                s.CopyTo(t);
            }
        }
        else
        {
            using (var s = sourceImgPart.GetStream())
            using (var img = Image.FromStream(s, useEmbeddedColorManagement: false, validateImageData: false))
            using (var t = targetImgPart.GetStream(FileMode.Create, FileAccess.Write))
            {
                img.Save(t, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }

    private static W.Table CopyTableWithImages(W.Table sourceTable, WordprocessingDocument sourceDoc, MainDocumentPart sourceMain, MainDocumentPart targetMain)
    {
        var cloned = (W.Table)sourceTable.CloneNode(true);

        foreach (var vShape in cloned.Descendants<DocumentFormat.OpenXml.Vml.Shape>().ToList()) vShape.Remove();
        foreach (var vImg in cloned.Descendants<DocumentFormat.OpenXml.Vml.ImageData>().ToList()) vImg.Remove();
        foreach (var unk in cloned.Descendants<OpenXmlUnknownElement>().ToList())
        {
            if (string.Equals(unk.LocalName, "pict", StringComparison.OrdinalIgnoreCase)) unk.Remove();
        }

        foreach (var drawing in cloned.Descendants<W.Drawing>())
        {
            NormalizeDrawingToInline(drawing);

            var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
            if (blip != null)
            {
                var relId = blip.Embed?.Value ?? blip.Link?.Value;
                var imgPart = TryResolveImagePart(sourceDoc, sourceMain, relId);
                if (imgPart != null)
                {
                    var newContentType = NormalizeImageContentType(imgPart.ContentType);
                    var newImgPart = targetMain.AddImagePart(newContentType);
                    CopyImageDataToPart(imgPart, newImgPart);
                    var newRelId = targetMain.GetIdOfPart(newImgPart);
                    blip.Embed = newRelId;
                    blip.Link = null;
                }
                else
                {
                    blip.Remove();
                }
            }
        }
        return cloned;
    }

    private static void NormalizeDrawingToInline(W.Drawing drawing)
    {
        var anchor = drawing.Descendants<DW.Anchor>().FirstOrDefault();
        if (anchor == null) return;
        var graphic = anchor.Descendants<A.Graphic>().FirstOrDefault();
        var extent = anchor.Descendants<DW.Extent>().FirstOrDefault() ?? new DW.Extent() { Cx = 0, Cy = 0 };
        var docPr = anchor.Descendants<DW.DocProperties>().FirstOrDefault() ?? new DW.DocProperties() { Id = 1U, Name = "Picture" };
        var inline = new DW.Inline(
            new DW.Extent() { Cx = extent.Cx, Cy = extent.Cy },
            new DW.EffectExtent() { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties() { Id = docPr.Id ?? 1U, Name = docPr.Name?.Value ?? "Picture" },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks() { NoChangeAspect = true }),
            graphic?.CloneNode(true)
        )
        { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U };

        anchor.Parent?.ReplaceChild(inline, anchor);
    }

    private static void AppendLatinLabelRunToRow(W.Table table, int rowIndex, string labelText)
    {
        var rows = table.Elements<W.TableRow>().ToList();
        if (rowIndex < 0 || rowIndex >= rows.Count) return;
        var row = rows[rowIndex];
        var cells = row.Elements<W.TableCell>().ToList();
        foreach (var cell in cells.Take(2))
        {
            var para = cell.Descendants<W.Paragraph>().LastOrDefault();
            if (para == null)
            {
                para = new W.Paragraph();
                cell.AppendChild(para);
            }
            var run = new W.Run();
            var rp = new W.RunProperties();
            rp.AppendChild(new W.Languages { Val = "en-US", EastAsia = "en-US", Bidi = "en-US" });
            rp.AppendChild(new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Calibri" });
            run.AppendChild(rp);
            run.AppendChild(new W.Text(labelText));
            para.AppendChild(run);
        }
    }

    private static void EnsureTableBorders(W.Table table)
    {
        var props = table.GetFirstChild<W.TableProperties>();
        if (props == null)
        {
            props = new W.TableProperties();
            table.PrependChild(props);
        }

        var borders = props.GetFirstChild<W.TableBorders>();
        if (borders == null)
        {
            borders = new W.TableBorders();
            props.AppendChild(borders);
        }

        borders.TopBorder = new W.TopBorder { Val = W.BorderValues.Single, Size = 8 };
        borders.BottomBorder = new W.BottomBorder { Val = W.BorderValues.Single, Size = 8 };
        borders.LeftBorder = new W.LeftBorder { Val = W.BorderValues.Single, Size = 8 };
        borders.RightBorder = new W.RightBorder { Val = W.BorderValues.Single, Size = 8 };
        borders.InsideHorizontalBorder = new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 8 };
        borders.InsideVerticalBorder = new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 8 };
    }

    private static void SetCellText(W.Table table, int rowIndex, int colIndex, string text)
    {
        var rows = table.Elements<W.TableRow>().ToList();
        if (rowIndex < 0 || rowIndex >= rows.Count) return;
        var cells = rows[rowIndex].Elements<W.TableCell>().ToList();
        if (colIndex < 0 || colIndex >= cells.Count) return;
        var cell = cells[colIndex];

        cell.RemoveAllChildren<W.Paragraph>();
        var p = new W.Paragraph();
        var r = new W.Run();
        r.AppendChild(new W.Text(text ?? string.Empty));
        p.AppendChild(r);
        cell.AppendChild(p);
    }

    private static List<SubjectRange> BuildSubjectRanges(string subjectList, string sequenceList)
    {
        var subjects = subjectList
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();

        var sequences = sequenceList
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();

        if (subjects.Count != sequences.Count)
            throw new Exception("Subject list and sequence list must have equal counts.");

        var list = new List<SubjectRange>();

        for (int i = 0; i < subjects.Count; i++)
        {
            string subject = subjects[i];
            string seq = sequences[i];

            var parts = seq.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out int start) ||
                !int.TryParse(parts[1], out int end) ||
                start <= 0 || end < start)
            {
                throw new Exception($"Sequence invalid: \"{seq}\". Format should be like: 1-25");
            }

            list.Add(new SubjectRange
            {
                Subject = subject,
                Start = start,
                End = end
            });
        }

        return list;
    }

    private static string? GetSubjectForQuestion(int qIndex, List<SubjectRange> ranges)
    {
        foreach (var r in ranges)
        {
            if (qIndex >= r.Start && qIndex <= r.End)
                return r.Subject;
        }
        return null;
    }

    private static void ApplyAnswerTagRobust(W.Table table)
    {
        var rows = table.Elements<W.TableRow>().ToList();
        if (rows.Count < 6) return;

        string? ansRaw = null;
        W.TableRow? sourceRow = null;
        if (rows.Count >= 8)
        {
            sourceRow = rows[7];
        }
        else
        {
            sourceRow = rows.Last();
        }

        if (sourceRow != null)
        {
            var texts = sourceRow.Descendants<W.Text>().Select(t => t.Text).ToList();
            ansRaw = string.Join(" ", texts).Trim();
        }

        string? ans = NormalizeAnswerToken(ansRaw);
        var optionRowMap = new Dictionary<string, int> { { "a", 2 }, { "b", 3 }, { "c", 4 }, { "d", 5 } };
        if (ans == null || !optionRowMap.TryGetValue(ans, out int targetIndex)) return;
        if (targetIndex >= rows.Count) return;

        var targetRow = rows[targetIndex];
        var cells = targetRow.Elements<W.TableCell>().ToList();
        for (int col = 0; col < Math.Min(2, cells.Count); col++)
        {
            var cell = cells[col];
            var paragraphs = cell.Elements<W.Paragraph>().ToList();

            if (paragraphs.Count == 0)
            {
                var p = new W.Paragraph(new W.Run(new W.Text("(Ans)")));
                cell.AppendChild(p);
            }
            else
            {
                var lastParagraph = paragraphs.Last();
                lastParagraph.AppendChild(new W.Run(new W.Text(" (Ans)")));
            }
        }
    }

    private static string? NormalizeAnswerToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim().ToLowerInvariant();
        foreach (char ch in t)
        {
            if (ch == 'a' || ch == 'b' || ch == 'c' || ch == 'd') return ch.ToString();
        }
        if (t.StartsWith("a")) return "a";
        if (t.StartsWith("b")) return "b";
        if (t.StartsWith("c")) return "c";
        if (t.StartsWith("d")) return "d";
        return null;
    }
}
