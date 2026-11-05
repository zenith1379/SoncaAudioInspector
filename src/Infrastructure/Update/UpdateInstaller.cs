using System.Diagnostics;
using System.IO;

namespace SoncaAudioInspector.Update;

public static class UpdateInstaller
{
    private static string RecoveryMarker(string appDirectory) => Path.Combine(appDirectory, "update-staging", "rollback-required.txt");
    public static string StageDirectory(string appDirectory, string version)
    {
        UpdateManifest.ParseVersion(version);
        string stage = Path.Combine(Path.GetFullPath(appDirectory), "update-staging", version);
        UpdatePaths.RejectLinks(stage);
        return stage;
    }
    public static void CheckWritable(string appDirectory)
    {
        if (File.Exists(RecoveryMarker(appDirectory))) throw new IOException("Cần khôi phục bản cập nhật trước từ các file .old-* trước khi cập nhật tiếp. Xem update-staging/rollback-required.txt.");
        string probe = Path.Combine(appDirectory, ".sonca-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            UpdatePaths.RejectLinks(appDirectory);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
        }
        catch (Exception ex) { throw new IOException("Không có quyền ghi thư mục ứng dụng. Hãy chuyển ứng dụng sang thư mục có quyền ghi để cập nhật.", ex); }
    }
    // The UI invokes this synchronously on its dispatcher after taking its update gate.
    // beforeMove is a fault-injection seam for the offline regression harness.
    public static void Apply(UpdatePlan plan, string appDirectory, Version current, Func<bool> isBusy, Action<int>? beforeMove = null)
    {
        plan.Manifest.Validate();
        string stage = StageDirectory(appDirectory, plan.Manifest.Version);
        var changes = new List<(string Target, string Backup, bool HadOriginal, bool Installed)>();
        bool rollbackIncomplete = false;
        try
        {
            CheckWritable(appDirectory);
            if (isBusy()) throw new InvalidOperationException("Đang đo hoặc sửa model; chưa thể cập nhật.");
            foreach (var file in plan.Files)
            {
                if (!plan.Manifest.Files.Contains(file)) throw new InvalidDataException("File không có trong manifest.");
                string target = UpdatePaths.Resolve(appDirectory, file.Path);
                string source = UpdatePaths.Resolve(stage, file.Path);
                if (!UpdateSignature.Matches(source, file)) throw new InvalidDataException("Staging sai size/SHA-256: " + file.Path);
                string backup = target + ".old-" + AppVersion.Normalize(current).ToString(3);
                UpdatePaths.RejectLinks(backup);
                if (Path.Exists(backup)) throw new IOException("Bản sao lưu cũ chưa dọn: " + backup);
                if (Directory.Exists(target)) throw new IOException("Đích cập nhật là thư mục: " + target);
            }
            if (isBusy()) throw new InvalidOperationException("Phép đo vừa bắt đầu; đã hủy cập nhật.");
            foreach (var file in plan.Files)
            {
                string target = UpdatePaths.Resolve(appDirectory, file.Path);
                string backup = target + ".old-" + AppVersion.Normalize(current).ToString(3);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool exists = File.Exists(target);
                if (exists) File.Move(target, backup);
                changes.Add((target, backup, exists, false));
                beforeMove?.Invoke(changes.Count);
                File.Move(UpdatePaths.Resolve(stage, file.Path), target);
                changes[^1] = (target, backup, exists, true);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.Installed) File.Delete(change.Target);
                    if (change.HadOriginal) File.Move(change.Backup, change.Target);
                }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            if (rollbackErrors.Count > 0)
            {
                rollbackIncomplete = true;
                string marker = RecoveryMarker(appDirectory);
                UpdatePaths.RejectLinks(marker);
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllLines(marker, changes.Select(c => c.Backup));
                throw new AggregateException("Khôi phục chưa hoàn tất; giữ bản .old-* để phục hồi thủ công. Không xóa dữ liệu trạm.", new[] { error }.Concat(rollbackErrors));
            }
            throw;
        }
        finally { if (!rollbackIncomplete) DeleteStage(stage); }
    }
    public static void DeleteStage(string stage)
    {
        try
        {
            UpdatePaths.RejectLinks(stage);
            if (!Directory.Exists(stage)) return;
            // Validate the complete tree first; Directory.Delete must never traverse a junction.
            ValidateTree(stage);
            Directory.Delete(stage, true);
        }
        catch (Exception ex) { Trace.WriteLine("Chưa dọn được staging: " + ex.Message); }
    }
    private static void ValidateTree(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            UpdatePaths.RejectLinks(entry);
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }
    public static void Cleanup(string appDirectory)
    {
        try
        {
            UpdatePaths.RejectLinks(appDirectory);
            if (File.Exists(RecoveryMarker(appDirectory)))
            {
                Trace.WriteLine("Giữ staging và .old-* để khôi phục lượt cập nhật lỗi; xem rollback-required.txt.");
                return;
            }
            foreach (string file in UpdatePaths.ApplicationFiles(appDirectory))
            {
                int marker = file.LastIndexOf(".old-", StringComparison.OrdinalIgnoreCase);
                if (marker < 0 || !Version.TryParse(file[(marker + 5)..], out _)) continue;
                string original = file[..marker];
                string relative = Path.GetRelativePath(appDirectory, original).Replace('\\', '/');
                if (UpdatePaths.IsExcluded(relative)) continue;
                UpdatePaths.Resolve(appDirectory, relative);
                try
                {
                    // An absent target can mean interrupted rollback: preserve the last good copy.
                    if (File.Exists(original)) File.Delete(file); else File.Move(file, original);
                }
                catch (Exception ex) { Trace.WriteLine("Chưa dọn được backup: " + ex.Message); }
            }
            DeleteStage(Path.Combine(appDirectory, "update-staging"));
        }
        catch (Exception ex) { Trace.WriteLine("Chưa dọn được cập nhật cũ: " + ex.Message); }
    }
}
