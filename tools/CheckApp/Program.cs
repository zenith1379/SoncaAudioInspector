using SoncaAudioInspector;

string[] models = { "D'AURIS 200", "D'AURIS 300", "D'AURIS 500" };
string[] keys = { "D200", "D300", "D500" };
string[] types = { "POWER_AMPLIFIER", "POWER_SUPPLY", "BASS_SPEAKER", "MID_SPEAKER" };
string[] names = { "Bo công suất", "Bo nguồn", "Loa bass", "Loa mid" };

var bomRows = new List<BomImportRow>();
var itemRows = new List<ItemImportRow>();
for (int modelIndex = 0; modelIndex < models.Length; modelIndex++)
{
    if (BomCsvParser.NormalizeModelKey(models[modelIndex]) != keys[modelIndex])
        throw new InvalidOperationException($"Sai model key cho {models[modelIndex]}");

    for (int slot = 1; slot <= types.Length; slot++)
    {
        string prefix = $"ACNOS:BOM:1:{keys[modelIndex]}:{types[slot - 1]}:";
        string componentName = $"{names[slot - 1]} {models[modelIndex]}";
        string barcode = $"{prefix}{types[slot - 1]}-{keys[modelIndex]}-0001";
        bomRows.Add(new BomImportRow
        {
            SchemaVersion = 1,
            ProductModel = models[modelIndex],
            SlotIndex = slot,
            ComponentType = types[slot - 1],
            ComponentName = componentName,
            BarcodePrefix = prefix,
            Required = true
        });
        itemRows.Add(new ItemImportRow
        {
            Id = $"ITEM-{keys[modelIndex]}-{slot:00}-0001",
            ComponentBarcode = barcode,
            ImageLink = "",
            NameItems = componentName
        });

        if (!BomCsvParser.MatchesComponentBarcode(prefix, barcode))
            throw new InvalidOperationException($"Không nhận đúng prefix {prefix}");
    }
}

BomCsvParser.ValidatePackage(bomRows, itemRows);
if (BomCsvParser.MatchesComponentBarcode(
        "ACNOS:BOM:1:D200:POWER_SUPPLY:",
        "ACNOS:BOM:1:D300:POWER_SUPPLY:PSU-D300-0001"))
    throw new InvalidOperationException("Đã nhận nhầm prefix khác model");

Console.WriteLine($"PASS: {bomRows.Count} BOM rules, {itemRows.Count} items, D200/D300/D500 prefixes validated.");
