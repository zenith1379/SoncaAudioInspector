using System.ComponentModel;
using System.Windows;

namespace SoncaAudioInspector.Update;

public sealed class UpdateCheckState : INotifyPropertyChanged
{
    private string _status = "Chưa kiểm";
    public string Feed => Environment.GetEnvironmentVariable("SONCA_UPDATE_FEED") ?? UpdateClient.DefaultFeed;
    public string Status => _status;
    public Func<Window, Task>? CheckRequested { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void SetStatus(string status)
    {
        _status = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }
}
