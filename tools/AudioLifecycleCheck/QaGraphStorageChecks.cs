using SoncaAudioInspector;
using System.IO;

internal static class QaGraphStorageChecks
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var names = new[] { "FRA-IO001.png", "IO002-frequency-shift.png", "FRA-IO003.png", "FRA-IO004.png" };
        var paths = names.Select(n => Path.Combine(directory, n)).ToArray();
        foreach (string path in paths) File.WriteAllBytes(path, [137, 80, 78, 71]);
        var pending = new PendingAudioQaUpload("https://example.invalid", "test", "diagnostic", false, [], names, true, "storage-check", DateTimeOffset.UtcNow);
        string marker = LocalQaStorage.SavePending(pending, paths, directory) ?? throw new Exception("FBX image prevents pending upload persistence");
        if (LocalQaStorage.ReadPending(directory, marker)?.GraphFiles.Length != 4) throw new Exception("Pending images lost");
        if (LocalQaStorage.SavePending(pending with { GraphFiles = ["../outside.png"] }, [paths[0]], directory) != null) throw new Exception("Unsafe graph path accepted");
        LocalQaStorage.MarkUploaded(directory, marker);
        if (File.Exists(marker) || !File.Exists(Path.Combine(directory, ".uploaded-audio-qa.json")) || paths.Any(p => !File.Exists(p)))
            throw new Exception("Confirmed upload did not finish its pending lifecycle or removed new images");
        Console.WriteLine("PASS pending/readback/completion lifecycle for FRA and FBX graphs; traversal rejected; newly uploaded graphs retained.");
    }
}
