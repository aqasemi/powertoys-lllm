using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms; // Required for Screen.PrimaryScreen
using System.IO;
using Wox.Plugin.Logger;


public static class ScreenCaptureUtility
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly Type WindowType = typeof(ScreenCaptureUtility);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // ShowWindow Commands
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5; // Or SW_SHOWNORMAL (1), SW_RESTORE (9)

    private static List<IntPtr> _hiddenWindowHandles = new List<IntPtr>();
    private static string _targetAppNameSubstring;

    private static bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam)
    {
        if (!IsWindowVisible(hWnd))
            return true; // Continue

        // Optional: Skip desktop window, etc.
        if (hWnd == GetShellWindow())
            return true;

        int length = GetWindowTextLength(hWnd);
        if (length == 0)
            return true;

        StringBuilder builder = new StringBuilder(length + 1);
        GetWindowText(hWnd, builder, builder.Capacity);
        string windowTitle = builder.ToString();

        bool match = false;
        if (!string.IsNullOrEmpty(windowTitle) && windowTitle.ToLower().Contains(_targetAppNameSubstring))
        {
            match = true;
        }

        // Also check process name
        if (!match)
        {
            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId != 0)
            {
                try
                {
                    Process p = Process.GetProcessById((int)processId);
                    if (p.ProcessName.ToLower().Contains(_targetAppNameSubstring))
                    {
                        match = true;
                    }
                }
                catch (ArgumentException) { /* Process might have exited */ }
            }
        }

        if (match)
        {
            _hiddenWindowHandles.Add(hWnd);
            ShowWindow(hWnd, SW_HIDE);
            Log.Info($"Hiding window: '{windowTitle}' (HWND: {hWnd})", WindowType);
        }

        return true;
    }

    public static List<IntPtr> FindAndHideAppWindows(string appNameSubstring)
    {
        _targetAppNameSubstring = appNameSubstring.ToLower();
        _hiddenWindowHandles.Clear(); // Clear from previous calls

        Log.Info($"Attempting to hide windows for '{appNameSubstring}'...", WindowType);
        EnumWindows(new EnumWindowsProc(EnumWindowsCallback), IntPtr.Zero);

        if (_hiddenWindowHandles.Count == 0)
        {
            Log.Info($"No visible windows found matching '{appNameSubstring}'.", WindowType);
        }
        return new List<IntPtr>(_hiddenWindowHandles); // Return a copy
    }

    public static void RestoreAppWindows(List<IntPtr> windowHandles)
    {
        if (windowHandles == null || windowHandles.Count == 0) return;

        Log.Info("Attempting to restore windows...", WindowType);
        foreach (IntPtr hWnd in windowHandles)
        {
            try
            {
                ShowWindow(hWnd, SW_SHOW);
                Log.Info($"Restoring window (HWND: {hWnd})", WindowType);
            }
            catch (Exception ex)
            {
                Log.Error($"Error restoring window {hWnd}: {ex.Message}", WindowType);
            }
        }
    }

    public static (string base64Image, string mimeType) ScreenshotWithAppHidden(string appNameSubstringToHide)
    {
        List<IntPtr> originalWindowStates = null;
        try
        {
            originalWindowStates = FindAndHideAppWindows(appNameSubstringToHide);

            // give the OS a moment to process the hide command
            Thread.Sleep(originalWindowStates.Count > 0 ? 200 : 100);

            Rectangle bounds = Screen.PrimaryScreen.Bounds;

            using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    byte[] imageBytes = ms.ToArray();
                    string base64Image = Convert.ToBase64String(imageBytes);
                    return (base64Image, "image/png");
                }
            }
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
        finally
        {
            if (originalWindowStates != null && originalWindowStates.Count > 0)
            {
                Thread.Sleep(50);
                RestoreAppWindows(originalWindowStates);
            }
        }
    }
}