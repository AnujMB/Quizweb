using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace QuizWeb.Data;

internal static class XlsxReader
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static List<List<string>> ReadGrid(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sharedStrings = ReadSharedStrings(zip);
        string sheetPath = FindFirstSheetPath(zip);
        var entry = zip.GetEntry(sheetPath)
            ?? throw new InvalidDataException($"Worksheet '{sheetPath}' not found in the workbook.");
        using var sheetStream = entry.Open();
        return ParseSheet(sheetStream, sharedStrings);
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Root!.Elements(Ns + "si"))
        {
            var sb = new StringBuilder();
            foreach (var t in si.Descendants(Ns + "t"))
                sb.Append(t.Value);
            result.Add(sb.ToString());
        }
        return result;
    }

    private static string FindFirstSheetPath(ZipArchive zip)
    {
        var wbEntry = zip.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("The workbook has no workbook.xml.");
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        var rels = new Dictionary<string, string>();
        if (relsEntry != null)
        {
            using var rs = relsEntry.Open();
            var relDoc = XDocument.Load(rs);
            foreach (var rel in relDoc.Root!.Elements())
            {
                var id = (string?)rel.Attribute("Id");
                var target = (string?)rel.Attribute("Target");
                if (id != null && target != null) rels[id] = target;
            }
        }

        using var wbStream = wbEntry.Open();
        var doc = XDocument.Load(wbStream);
        var sheets = doc.Descendants(Ns + "sheet").ToList();
        if (sheets.Count == 0) throw new InvalidDataException("The workbook has no sheets.");
        var chosen = sheets.FirstOrDefault(s =>
                string.Equals((string?)s.Attribute("name"), "Questions", StringComparison.OrdinalIgnoreCase))
            ?? sheets.First();

        var rid = (string?)chosen.Attribute(RelNs + "id");
        var sheetTarget = rid != null && rels.TryGetValue(rid, out var t) ? t : null;
        if (sheetTarget == null) throw new InvalidDataException("The workbook's sheet has no relationship.");

        return sheetTarget.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? sheetTarget
            : "xl/" + sheetTarget.TrimStart('/');
    }

    private static List<List<string>> ParseSheet(Stream stream, List<string> sharedStrings)
    {
        var doc = XDocument.Load(stream);
        var cells = new Dictionary<(int row, int col), string>();
        int maxRow = 0, maxCol = 0, rowSeq = 0;

        foreach (var row in doc.Descendants(Ns + "row"))
        {
            var rowNum = ParseRowNumber((string?)row.Attribute("r"));
            if (rowNum <= 0) rowNum = ++rowSeq;
            foreach (var c in row.Elements(Ns + "c"))
            {
                var refAttr = (string?)c.Attribute("r");
                int col = refAttr != null ? ColToIndex(refAttr) : -1;
                if (col < 0) continue;
                cells[(rowNum, col)] = GetCellValue(c, sharedStrings);
                maxRow = Math.Max(maxRow, rowNum);
                maxCol = Math.Max(maxCol, col);
            }
        }

        foreach (var mc in doc.Descendants(Ns + "mergeCell"))
        {
            var refStr = (string?)mc.Attribute("ref");
            if (refStr == null) continue;
            if (!ParseRange(refStr, out int r1, out int c1, out int r2, out int c2)) continue;
            string value = cells.TryGetValue((r1, c1), out var v) ? v : "";
            for (int rr = r1; rr <= r2; rr++)
                for (int cc = c1; cc <= c2; cc++)
                    cells[(rr, cc)] = value;
            maxRow = Math.Max(maxRow, r2);
            maxCol = Math.Max(maxCol, c2);
        }

        var grid = new List<List<string>>();
        for (int rr = 1; rr <= maxRow; rr++)
        {
            var row = new List<string>();
            for (int cc = 0; cc <= maxCol; cc++)
                row.Add(cells.TryGetValue((rr, cc), out var val) ? val : "");
            grid.Add(row);
        }
        return grid;
    }

    private static string GetCellValue(XElement c, List<string> sharedStrings)
    {
        string type = (string?)c.Attribute("t") ?? "";
        var v = c.Element(Ns + "v");
        if (type == "s" && v != null && int.TryParse(v.Value, out int si) && si >= 0 && si < sharedStrings.Count)
            return sharedStrings[si];
        if (type == "inlineStr")
        {
            var sb = new StringBuilder();
            foreach (var t in c.Descendants(Ns + "t"))
                sb.Append(t.Value);
            return sb.ToString();
        }
        return v?.Value ?? "";
    }

    private static int ParseRowNumber(string? r)
        => int.TryParse(r, out int n) ? n : 0;

    private static int ColToIndex(string refStr)
    {
        int i = 0;
        while (i < refStr.Length && char.IsLetter(refStr[i])) i++;
        int col = 0;
        foreach (char ch in refStr[..i])
            col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        return col - 1;
    }

    private static bool ParseRange(string range, out int r1, out int c1, out int r2, out int c2)
    {
        r1 = r2 = c1 = c2 = 0;
        var parts = range.Split(':');
        if (parts.Length == 1)
        {
            r1 = r2 = ParseRow(parts[0]);
            c1 = c2 = ColToIndex(parts[0]);
        }
        else if (parts.Length == 2)
        {
            r1 = ParseRow(parts[0]); c1 = ColToIndex(parts[0]);
            r2 = ParseRow(parts[1]); c2 = ColToIndex(parts[1]);
        }
        else return false;
        return r1 > 0 && r2 >= r1 && c1 >= 0 && c2 >= c1;
    }

    private static int ParseRow(string refStr)
    {
        int i = 0;
        while (i < refStr.Length && char.IsLetter(refStr[i])) i++;
        return int.TryParse(refStr[i..], out int n) ? n : 0;
    }

    // ---------------- Writing ----------------

    public static void WriteBank(string path, List<Question> questions, QuizInfo? info = null)
    {
        var sheetData = new XElement(Ns + "sheetData");
        var sheet = new XElement(Ns + "worksheet",
            new XAttribute(XNamespace.Xmlns + "r", RelNs),
            sheetData);

        if (info != null)
        {
            if (!string.IsNullOrWhiteSpace(info.Class)) AddRow(sheetData, new[] { "Class: " + info.Class });
            if (!string.IsNullOrWhiteSpace(info.Subject)) AddRow(sheetData, new[] { "Subject: " + info.Subject });
            if (!string.IsNullOrWhiteSpace(info.ExamType)) AddRow(sheetData, new[] { "Exam Type: " + info.ExamType });
        }

        AddRow(sheetData, new[]
        {
            "Q.No", "Passage", "Question", "Image", "Correct",
            "Option1", "Option2", "Option3", "Option4",
            "Option5", "Option6", "Option7", "Option8"
        });
        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var cells = new List<string>
            {
                (i + 1).ToString(),
                q.Passage ?? "",
                q.Text, q.ImagePath ?? "", QuestionGridParser.CorrectAsText(q.CorrectIndices)
            };
            cells.AddRange(q.Options);
            AddRow(sheetData, cells);
        }

        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "[Content_Types].xml", ContentTypesXml());
            WriteEntry(zip, "_rels/.rels", RelsXml());
            WriteEntry(zip, "xl/workbook.xml", WorkbookXml());
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());
            WriteEntry(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
    }

    private static void AddRow(XElement sheetData, IEnumerable<string> values)
    {
        int rowIndex = sheetData.Elements(Ns + "row").Count() + 1;
        var row = new XElement(Ns + "row", new XAttribute("r", rowIndex));
        int colIndex = 0;
        foreach (string value in values)
        {
            string cellRef = IndexToCol(colIndex++) + rowIndex;
            var text = new XElement(Ns + "t", value);
            text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            var cell = new XElement(Ns + "c",
                new XAttribute("r", cellRef),
                new XAttribute("t", "inlineStr"),
                new XElement(Ns + "is", text));
            row.Add(cell);
        }
        sheetData.Add(row);
    }

    private static string IndexToCol(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            int rem = (index - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            index = (index - 1) / 26;
        }
        return sb.ToString();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ContentTypesXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

    private static string RelsXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private static string WorkbookXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>
            <sheet name="Questions" sheetId="1" r:id="rId1"/>
          </sheets>
        </workbook>
        """;

    private static string WorkbookRelsXml() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """;
}
