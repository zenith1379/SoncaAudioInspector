using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SoncaAudioInspector
{
    public sealed class BomImportRow
    {
        public int SchemaVersion { get; init; }
        public string ProductModel { get; init; } = "";
        public int SlotIndex { get; init; }
        public string ComponentType { get; init; } = "";
        public string ComponentName { get; init; } = "";
        public string BarcodePrefix { get; init; } = "";
        public bool Required { get; init; }
    }

    public sealed class ItemImportRow
    {
        public string Id { get; init; } = "";
        public string ComponentBarcode { get; init; } = "";
        public string ImageLink { get; init; } = "";
        public string NameItems { get; init; } = "";
    }

    public sealed class BomImportPackage
    {
        public IReadOnlyList<BomImportRow> BomRows { get; init; } = Array.Empty<BomImportRow>();
        public IReadOnlyList<ItemImportRow> ItemRows { get; init; } = Array.Empty<ItemImportRow>();
    }

    public sealed class BomComponentInfo
    {
        public int SlotIndex { get; set; }
        public string ComponentType { get; set; } = "";
        public string ComponentName { get; set; } = "";
        public string BarcodePrefix { get; set; } = "";
        public bool Required { get; set; }
    }

    public sealed class BomDefinitionInfo
    {
        public int SchemaVersion { get; set; }
        public string SpeakerModel { get; set; } = "";
        public string ModelKey { get; set; } = "";
        public List<BomComponentInfo> Components { get; set; } = new();
    }

    public sealed class BomImportResult
    {
        public int ImportedModels { get; set; }
        public int ImportedItems { get; set; }
        public List<BomDefinitionInfo> Definitions { get; set; } = new();
    }

    public sealed record BomProductQrValue(string Normalized, string ModelKey, string ProductSerial);

    public static class BomCsvParser
    {
        public const string DriveOwnerEmail = "hain1974211@gmail.com";

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string[] BomHeaders =
        {
            "schema_version", "product_model", "slot_index", "component_type",
            "component_name", "barcode_prefix", "required"
        };
        private static readonly string[] ItemHeaders =
        {
            "id", "component_barcode", "image_link", "name_items"
        };
        private static readonly Regex BarcodePattern = new(
            "^ACNOS:BOM:1:([A-Z0-9]+):([A-Z0-9_]+):([A-Z0-9._-]+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex BarcodePrefixPattern = new(
            "^ACNOS:BOM:1:([A-Z0-9]+):([A-Z0-9_]+):$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static BomImportPackage ParsePackage(IEnumerable<string> paths)
        {
            string[] selected = paths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (selected.Length != 2)
                throw new InvalidDataException("Hãy chọn đúng 2 file Excel: một file BOM_MODELS và một file ITEMS.");

            IReadOnlyList<BomImportRow>? bomRows = null;
            IReadOnlyList<ItemImportRow>? itemRows = null;
            foreach (string path in selected)
            {
                IReadOnlyList<string[]> records = ReadRecords(path);
                Dictionary<string, int> columns = GetColumns(records[0]);
                if (BomHeaders.All(columns.ContainsKey))
                {
                    if (bomRows is not null) throw new InvalidDataException("Đã chọn trùng hai file BOM_MODELS.");
                    bomRows = ParseBomRecords(records, columns);
                }
                else if (ItemHeaders.All(columns.ContainsKey))
                {
                    if (itemRows is not null) throw new InvalidDataException("Đã chọn trùng hai file ITEMS.");
                    itemRows = ParseItemRecords(records, columns);
                }
                else
                {
                    throw new InvalidDataException($"File {Path.GetFileName(path)} không đúng mẫu BOM_MODELS hoặc ITEMS.");
                }
            }

            if (bomRows is null || itemRows is null)
                throw new InvalidDataException("Cần một file BOM_MODELS và một file ITEMS trong cùng lần đồng bộ.");
            ValidatePackage(bomRows, itemRows);
            return new BomImportPackage { BomRows = bomRows, ItemRows = itemRows };
        }

        public static IReadOnlyList<BomImportRow> ParseFile(string path) => ParseBomFile(path);

        public static IReadOnlyList<BomImportRow> ParseBomFile(string path)
        {
            IReadOnlyList<string[]> records = ReadRecords(path);
            Dictionary<string, int> columns = GetColumns(records[0]);
            string[] missing = BomHeaders.Where(header => !columns.ContainsKey(header)).ToArray();
            if (missing.Length > 0) throw new InvalidDataException($"BOM thiếu cột: {string.Join(", ", missing)}");
            return ParseBomRecords(records, columns);
        }

        public static IReadOnlyList<ItemImportRow> ParseItemsFile(string path)
        {
            IReadOnlyList<string[]> records = ReadRecords(path);
            Dictionary<string, int> columns = GetColumns(records[0]);
            string[] missing = ItemHeaders.Where(header => !columns.ContainsKey(header)).ToArray();
            if (missing.Length > 0) throw new InvalidDataException($"ITEMS thiếu cột: {string.Join(", ", missing)}");
            return ParseItemRecords(records, columns);
        }

        public static void ValidatePackage(IReadOnlyList<BomImportRow> bomRows, IReadOnlyList<ItemImportRow> itemRows)
        {
            var rules = bomRows.ToDictionary(
                row => $"{NormalizeModelKey(row.ProductModel)}|{row.ComponentType}",
                row => row,
                StringComparer.OrdinalIgnoreCase);

            foreach (ItemImportRow item in itemRows)
            {
                Match barcode = BarcodePattern.Match(item.ComponentBarcode);
                string key = barcode.Success ? $"{barcode.Groups[1].Value}|{barcode.Groups[2].Value}" : "";
                if (!rules.TryGetValue(key, out BomImportRow? rule))
                    throw new InvalidDataException($"Item {item.Id}: component_barcode không khớp model/component_type nào trong BOM.");
                if (!MatchesComponentBarcode(rule.BarcodePrefix, item.ComponentBarcode))
                    throw new InvalidDataException($"Item {item.Id}: barcode phải bắt đầu bằng tiền tố {rule.BarcodePrefix}");
                if (!item.NameItems.Equals(rule.ComponentName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Item {item.Id}: name_items phải copy đúng từ BOM: {rule.ComponentName}");
            }
        }

        public static bool TryParseProductQr(string value, out BomProductQrValue? product)
        {
            Match match = BarcodePattern.Match(value?.Trim() ?? "");
            if (!match.Success || !match.Groups[2].Value.Equals("PRODUCT", StringComparison.OrdinalIgnoreCase))
            {
                product = null;
                return false;
            }
            product = new BomProductQrValue(match.Value.ToUpperInvariant(), match.Groups[1].Value.ToUpperInvariant(), match.Groups[3].Value.ToUpperInvariant());
            return true;
        }

        public static string NormalizeModelKey(string value)
        {
            string normalized = new string((value ?? "").Trim().ToUpperInvariant()
                .Where(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());
            return normalized switch
            {
                "DAURIS200" => "D200",
                "DAURIS300" => "D300",
                "DAURIS500" => "D500",
                _ => normalized
            };
        }

        public static bool MatchesComponentBarcode(string expectedPrefix, string scannedBarcode)
        {
            string prefix = (expectedPrefix ?? "").Trim().ToUpperInvariant();
            string scanned = (scannedBarcode ?? "").Trim().ToUpperInvariant();
            Match prefixMatch = BarcodePrefixPattern.Match(prefix);
            Match scannedMatch = BarcodePattern.Match(scanned);
            return prefixMatch.Success && scannedMatch.Success
                && prefixMatch.Groups[1].Value.Equals(scannedMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                && prefixMatch.Groups[2].Value.Equals(scannedMatch.Groups[2].Value, StringComparison.OrdinalIgnoreCase)
                && scanned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<BomImportRow> ParseBomRecords(IReadOnlyList<string[]> records, IReadOnlyDictionary<string, int> columns)
        {
            var rows = new List<BomImportRow>();
            for (int index = 1; index < records.Count; index++)
            {
                string[] fields = records[index];
                int rowNumber = index + 1;
                if (fields.All(string.IsNullOrWhiteSpace)) continue;
                string Read(string name) => ReadField(fields, columns[name]);
                if (!int.TryParse(Read("schema_version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) || version != 1)
                    throw new InvalidDataException($"Dòng {rowNumber}: schema_version phải bằng 1.");
                if (!int.TryParse(Read("slot_index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) || slot is < 1 or > 20)
                    throw new InvalidDataException($"Dòng {rowNumber}: slot_index phải từ 1 đến 20.");

                string model = RepairMojibake(Require(Read("product_model"), rowNumber, "product_model"));
                string componentType = Require(Read("component_type"), rowNumber, "component_type").ToUpperInvariant();
                string componentName = RepairMojibake(Require(Read("component_name"), rowNumber, "component_name"));
                string barcodePrefix = Require(Read("barcode_prefix"), rowNumber, "barcode_prefix").ToUpperInvariant();
                ValidateBarcodePrefix(model, componentType, barcodePrefix, rowNumber);
                rows.Add(new BomImportRow
                {
                    SchemaVersion = version,
                    ProductModel = model,
                    SlotIndex = slot,
                    ComponentType = componentType,
                    ComponentName = componentName,
                    BarcodePrefix = barcodePrefix,
                    Required = ParseBoolean(Read("required"), rowNumber)
                });
            }
            if (rows.Count == 0) throw new InvalidDataException("BOM không có dòng dữ liệu.");
            ValidateBomDuplicates(rows);
            return rows;
        }

        private static IReadOnlyList<ItemImportRow> ParseItemRecords(IReadOnlyList<string[]> records, IReadOnlyDictionary<string, int> columns)
        {
            var rows = new List<ItemImportRow>();
            for (int index = 1; index < records.Count; index++)
            {
                string[] fields = records[index];
                int rowNumber = index + 1;
                if (fields.All(string.IsNullOrWhiteSpace)) continue;
                string Read(string name) => ReadField(fields, columns[name]);
                string id = Require(Read("id"), rowNumber, "id");
                string barcode = Require(Read("component_barcode"), rowNumber, "component_barcode").ToUpperInvariant();
                string imageLink = Read("image_link");
                string name = RepairMojibake(Require(Read("name_items"), rowNumber, "name_items"));
                if (!BarcodePattern.IsMatch(barcode))
                    throw new InvalidDataException($"Dòng {rowNumber}: component_barcode phải theo mẫu ACNOS:BOM:1:<MODEL>:<TYPE>:<ID>.");
                ValidateDriveLink(imageLink, rowNumber);
                rows.Add(new ItemImportRow { Id = id, ComponentBarcode = barcode, ImageLink = imageLink, NameItems = name });
            }
            if (rows.Count == 0) throw new InvalidDataException("ITEMS không có dòng dữ liệu.");
            string? duplicateId = rows.GroupBy(row => row.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicateId is not null) throw new InvalidDataException($"ID item bị trùng: {duplicateId}");
            string? duplicateBarcode = rows.GroupBy(row => row.ComponentBarcode, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicateBarcode is not null) throw new InvalidDataException($"Barcode item bị trùng: {duplicateBarcode}");
            return rows;
        }

        private static void ValidateBarcodePrefix(string model, string componentType, string prefix, int rowNumber)
        {
            Match match = BarcodePrefixPattern.Match(prefix);
            if (!match.Success)
                throw new InvalidDataException($"Dòng {rowNumber}: barcode_prefix phải theo mẫu ACNOS:BOM:1:<MODEL>:<TYPE>: và kết thúc bằng dấu hai chấm.");
            if (!match.Groups[1].Value.Equals(NormalizeModelKey(model), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Dòng {rowNumber}: model trong barcode_prefix không khớp product_model.");
            if (!match.Groups[2].Value.Equals(componentType, StringComparison.OrdinalIgnoreCase) || componentType.Equals("PRODUCT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Dòng {rowNumber}: type trong barcode_prefix không khớp component_type.");
        }

        private static void ValidateDriveLink(string value, int rowNumber)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
                || !(uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Dòng {rowNumber}: image_link phải là link chia sẻ HTTPS của Google Drive ({DriveOwnerEmail}) hoặc để trống.");
        }

        private static void ValidateBomDuplicates(IReadOnlyList<BomImportRow> rows)
        {
            foreach (IGrouping<string, BomImportRow> model in rows.GroupBy(row => NormalizeModelKey(row.ProductModel), StringComparer.OrdinalIgnoreCase))
            {
                int? duplicateSlot = model.GroupBy(row => row.SlotIndex).FirstOrDefault(group => group.Count() > 1)?.Key;
                if (duplicateSlot.HasValue) throw new InvalidDataException($"Model {model.Key} bị trùng slot {duplicateSlot.Value}.");
                string? duplicateType = model.GroupBy(row => row.ComponentType, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1)?.Key;
                if (duplicateType is not null) throw new InvalidDataException($"Model {model.Key} bị trùng component_type {duplicateType}.");
            }
        }

        private static IReadOnlyList<string[]> ReadRecords(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Không tìm thấy file Excel/CSV.", path);
            string csvText;
            try
            {
                csvText = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                    ? BuildCsvText(ReadXlsxRows(path)) : ReadCsvText(path);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("CSV không đọc được encoding. Hãy lưu lại dạng CSV UTF-8 từ Excel.", ex);
            }

            using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csvText))
            {
                TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = true
            };
            parser.SetDelimiters(",");
            var records = new List<string[]>();
            while (!parser.EndOfData)
            {
                try { records.Add(parser.ReadFields() ?? Array.Empty<string>()); }
                catch (Microsoft.VisualBasic.FileIO.MalformedLineException ex)
                {
                    throw new InvalidDataException($"CSV lỗi định dạng ở dòng {parser.LineNumber}: {ex.Message}", ex);
                }
            }
            if (records.Count == 0) throw new InvalidDataException("File không có dữ liệu.");
            if (records[0].Length > 0) records[0][0] = records[0][0].TrimStart('\uFEFF');
            return records;
        }

        private static string ReadCsvText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var snapshot = new MemoryStream();
            stream.CopyTo(snapshot);
            return DecodeCsvText(snapshot.ToArray());
        }

        private static Dictionary<string, int> GetColumns(string[] headers) => headers
            .Select((header, index) => new { Header = header.Trim(), Index = index })
            .Where(value => value.Header.Length > 0)
            .ToDictionary(value => value.Header, value => value.Index, StringComparer.OrdinalIgnoreCase);
        private static string ReadField(string[] fields, int index) => index < fields.Length ? fields[index].Trim() : "";
        private static string Require(string value, int rowNumber, string column) => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Dòng {rowNumber}: {column} không được để trống.") : value;

        private static string DecodeCsvText(byte[] bytes)
        {
            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                Encoding[] fallbacks = { Encoding.GetEncoding(1258), Encoding.GetEncoding(1252), Encoding.Latin1 };
                string? decoded = fallbacks.Select(encoding => encoding.GetString(bytes)).OrderBy(MojibakeScore)
                    .FirstOrDefault(value => !value.Contains('\uFFFD'));
                if (decoded is not null) return decoded;
                throw;
            }
        }

        private static string RepairMojibake(string value)
        {
            string repaired = value;
            for (int attempt = 0; attempt < 2 && MojibakeScore(repaired) > 0; attempt++)
            {
                try
                {
                    string candidate = StrictUtf8.GetString(Encoding.Latin1.GetBytes(repaired));
                    if (candidate.Contains('\uFFFD') || MojibakeScore(candidate) >= MojibakeScore(repaired)) break;
                    repaired = candidate;
                }
                catch (DecoderFallbackException) { break; }
            }
            return repaired;
        }
        private static int MojibakeScore(string value)
        {
            string[] markers = { "Ã", "Â", "â€", "â€™", "â€œ", "â€“", "áº", "á»", "Ä", "Å", "�" };
            return markers.Sum(marker => CountOccurrences(value, marker));
        }
        private static int CountOccurrences(string value, string marker)
        {
            int count = 0, index = 0;
            while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0) { count++; index += marker.Length; }
            return count;
        }
        private static string BuildCsvText(IReadOnlyList<string[]> rows) => string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(EscapeCsv))));
        private static string EscapeCsv(string value)
        {
            value ??= "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }

        private static IReadOnlyList<string[]> ReadXlsxRows(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
            XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace officeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
            XDocument workbook = ReadZipXml(archive, "xl/workbook.xml");
            XDocument relationships = ReadZipXml(archive, "xl/_rels/workbook.xml.rels");
            XElement? firstSheet = workbook.Root?.Element(spreadsheet + "sheets")?.Element(spreadsheet + "sheet");
            string relationshipId = firstSheet?.Attribute(officeRelationships + "id")?.Value ?? "";
            string target = relationships.Root?.Elements(packageRelationships + "Relationship")
                .FirstOrDefault(item => item.Attribute("Id")?.Value == relationshipId)?.Attribute("Target")?.Value ?? "worksheets/sheet1.xml";
            string worksheetPath = target.TrimStart('/');
            if (!worksheetPath.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) worksheetPath = "xl/" + worksheetPath;
            Dictionary<int, string> sharedStrings = ReadSharedStrings(archive, spreadsheet);
            XDocument worksheet = ReadZipXml(archive, worksheetPath);
            var rows = new List<string[]>();
            foreach (XElement row in worksheet.Descendants(spreadsheet + "row"))
            {
                var values = new Dictionary<int, string>();
                int fallbackColumn = 0;
                foreach (XElement cell in row.Elements(spreadsheet + "c"))
                {
                    string cellReference = cell.Attribute("r")?.Value ?? "";
                    int column = string.IsNullOrWhiteSpace(cellReference) ? fallbackColumn : ColumnIndex(cellReference);
                    fallbackColumn = column + 1;
                    string value = cell.Attribute("t")?.Value switch
                    {
                        "s" when int.TryParse(cell.Element(spreadsheet + "v")?.Value, out int index) && sharedStrings.TryGetValue(index, out string? shared) => shared,
                        "inlineStr" => string.Concat(cell.Element(spreadsheet + "is")?.Descendants(spreadsheet + "t").Select(item => item.Value) ?? Enumerable.Empty<string>()),
                        _ => cell.Element(spreadsheet + "v")?.Value ?? ""
                    };
                    values[column] = value;
                }
                int width = values.Count == 0 ? 0 : values.Keys.Max() + 1;
                string[] parsed = Enumerable.Range(0, width).Select(index => values.TryGetValue(index, out string? value) ? value : "").ToArray();
                if (parsed.Length > 0) rows.Add(parsed);
            }
            if (rows.Count == 0) throw new InvalidDataException("File XLSX không có dữ liệu trong sheet đầu tiên.");
            return rows;
        }

        private static Dictionary<int, string> ReadSharedStrings(ZipArchive archive, XNamespace spreadsheet)
        {
            ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry is null) return new Dictionary<int, string>();
            using Stream stream = entry.Open();
            XDocument document = XDocument.Load(stream);
            return document.Root?.Elements(spreadsheet + "si").Select((item, index) => new
            {
                Index = index,
                Value = string.Concat(item.Descendants(spreadsheet + "t").Select(text => text.Value))
            }).ToDictionary(item => item.Index, item => item.Value) ?? new Dictionary<int, string>();
        }
        private static XDocument ReadZipXml(ZipArchive archive, string path)
        {
            ZipArchiveEntry? entry = archive.GetEntry(path.Replace('\\', '/')) ?? throw new InvalidDataException($"File XLSX thiếu thành phần {path}.");
            using Stream stream = entry.Open();
            return XDocument.Load(stream);
        }
        private static int ColumnIndex(string cellReference)
        {
            int index = 0;
            foreach (char character in cellReference.TakeWhile(char.IsLetter)) index = index * 26 + char.ToUpperInvariant(character) - 'A' + 1;
            return Math.Max(0, index - 1);
        }
        private static bool ParseBoolean(string value, int rowNumber)
        {
            if (bool.TryParse(value, out bool result)) return result;
            if (value == "1") return true;
            if (value == "0") return false;
            throw new InvalidDataException($"Dòng {rowNumber}: required chỉ nhận true/false.");
        }
    }
}
