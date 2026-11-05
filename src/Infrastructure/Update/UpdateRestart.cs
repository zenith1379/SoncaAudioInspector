using System.Diagnostics;
using System.IO;

namespace SoncaAudioInspector.Update;

public static class UpdateRestart
{
    public static ProcessStartInfo StartInfo()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Không xác định được exe hiện tại.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (string argument in Environment.GetCommandLineArgs().Skip(1)) start.ArgumentList.Add(argument);
        return start;
    }
    public static void Start() => _ = Process.Start(StartInfo()) ?? throw new IOException("Không khởi động lại được ứng dụng.");
}
