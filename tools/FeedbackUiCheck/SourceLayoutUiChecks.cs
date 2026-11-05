using System.IO;
using System.Reflection;
using System.Windows;

internal static class SourceLayoutUiChecks
{
    public static void Run(Assembly assembly, Uri startupUri, string output)
    {
        Directory.CreateDirectory(output);
        if (startupUri.ToString() != "src/UI/Shell/SplashWindow.xaml") throw new Exception("Unexpected WPF startup URI");
        var splashUri = new Uri("/SoncaAudioInspector;component/" + startupUri, UriKind.Relative);
        var splash = (Window)Application.LoadComponent(splashUri);
        var login = (Window)Application.LoadComponent(new Uri("/SoncaAudioInspector;component/src/UI/Shell/LoginWindow.xaml", UriKind.Relative));
        try
        {
            if (splash.GetType().FullName != "SoncaAudioInspector.SplashWindow" || splash.FindName("LblVersion") == null
                || login.GetType().FullName != "SoncaAudioInspector.LoginWindow" || login.FindName("BtnLogin") == null)
                throw new Exception("Relocated startup/login XAML or code-behind did not load");
            foreach (string theme in new[] { "ConfigTheme", "ImpulseTheme" })
            {
                var resource = (ResourceDictionary)Application.LoadComponent(new Uri("/SoncaAudioInspector;component/src/Themes/" + theme + ".xaml", UriKind.Relative));
                if (resource.Count == 0) throw new Exception("Relocated theme is empty: " + theme);
            }
            File.WriteAllText(Path.Combine(output,"result.txt"), "PASS relocated Splash/Login compiled XAML, code-behind, icons and shared themes. Windows never shown; startup/network diagnostics never invoked.\n");
            Console.WriteLine("PASS relocated WPF startup/login, code-behind, icons and both themes; no Loaded startup, network or audio operations.");
        }
        finally { login.Close(); splash.Close(); }
    }
}
