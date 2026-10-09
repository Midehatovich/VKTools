using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VKTools.Revit.UI
{
    internal static class WindowOwner
    {
        /// <summary>Окно поверх Revit и сворачивается вместе с ним.</summary>
        public static void AttachTo(this Window window, IntPtr revitMainWindow)
        {
            if (revitMainWindow != IntPtr.Zero)
            {
                new WindowInteropHelper(window).Owner = revitMainWindow;
            }
        }

        public static void ApplyDefaultLook(this Window window)
        {
            window.FontFamily = new FontFamily("Segoe UI");
            window.FontSize = 12;
            window.Background = SystemColors.WindowBrush;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }
}
