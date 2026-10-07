using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace PhotoImportV2
{
    public static class FluentTheme
    {
        public static string Mode = "system";
        public static bool Dark { get; private set; }
        public static ResourceDictionary CreateResources()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PhotoImportV2.FluentStyles.xaml"))
                return (ResourceDictionary)XamlReader.Load(stream);
        }
        public static void SetPalette(ResourceDictionary resources, bool? dark)
        {
            bool useDark = false;
            try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) useDark = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0; } catch { }
            if (dark.HasValue) useDark = dark.Value;
            else if (Mode == "light" || Mode == "dark") useDark = Mode == "dark";
            Dark = useDark;
            string[] keys = { "CanvasBrush", "NavigationBrush", "SurfaceBrush", "SubtleBrush", "HoverBrush", "StrokeBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentSoftBrush", "SuccessBrush", "SuccessSoftBrush", "WarningBrush", "WarningSoftBrush", "DangerBrush", "FocusBrush" };
            string[] light = { "#F7F8FA", "#F0F2F5", "#FFFFFF", "#FAFBFC", "#EAEDF1", "#E1E5EB", "#19212E", "#667181", "#0067C0", "#E9F2FC", "#187544", "#EAF6EE", "#8C6100", "#FFF5D6", "#B42318", "#1E293B" };
            string[] darkColors = { "#202124", "#191B1F", "#2A2D32", "#25282D", "#353A42", "#3A4048", "#F4F6FA", "#ADB7C5", "#79B8FF", "#263D57", "#8DD7A6", "#253D30", "#F1CE78", "#443B25", "#FF9B91", "#F4F6FA" };
            for (int i = 0; i < keys.Length; i++) resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString((useDark ? darkColors : light)[i]));
            resources["AccentForegroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(useDark ? "#102236" : "#FFFFFF"));
            if (SystemParameters.HighContrast)
            {
                resources["CanvasBrush"] = resources["NavigationBrush"] = resources["SurfaceBrush"] = resources["SubtleBrush"] = SystemColors.WindowBrush;
                resources["TextBrush"] = resources["MutedBrush"] = resources["StrokeBrush"] = SystemColors.WindowTextBrush;
                resources["AccentBrush"] = SystemColors.HighlightBrush;
                resources["AccentForegroundBrush"] = SystemColors.HighlightTextBrush;
            }
        }
        public static void StyleWindow(Window window)
        {
            window.FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
            window.FontSize = 14; window.Resources.MergedDictionaries.Add(CreateResources());
            SetPalette(window.Resources, null);
            window.SetResourceReference(Window.BackgroundProperty, "SurfaceBrush");
            window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
            window.ResizeMode = ResizeMode.NoResize; window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.ShowInTaskbar = false;
            window.SourceInitialized += delegate { StyleChrome(new WindowInteropHelper(window).Handle); };
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void StyleChrome(IntPtr handle)
        {
            try { int dark = Dark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, 4); int rounded = 2; DwmSetWindowAttribute(handle, 33, ref rounded, 4); } catch { }
        }
    }
}
