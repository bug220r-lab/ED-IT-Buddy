using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace EditBuddy
{
    internal static class MagixEditingCapture
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT bounds);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        internal static string CaptureScene(string output, bool timelineOnly = false)
        {
            if (!String.Equals(Path.GetExtension(output), ".png", StringComparison.OrdinalIgnoreCase) || File.Exists(output)) throw new InvalidOperationException("Capture requires a new .png output file.");
            MagixDesktopState state = MagixEditingWorkerMain.Snapshot(false);
            var ui = new MagixEditingUi(state, delegate { throw new InvalidOperationException("Capture cannot send editing input."); });
            IntPtr editor = new IntPtr(state.editor_hwnd); ui.VerifyWindow(editor);
            if (Native.GetAncestor(Native.GetForegroundWindow(), 3) != editor) throw new InvalidOperationException("MAGIX is not foreground; visible capture is unavailable.");
            IntPtr captureWindow = editor;
            if (timelineOnly)
            {
                var movie = state.windows.SingleOrDefault(w => w.class_name == "#32770" && (w.title == "buddy-demo-original-clips *" || w.title == "buddy-demo-original-clips") && w.root_owner == state.editor_hwnd && w.enabled);
                if (movie == null) throw new InvalidOperationException("The verified imported movie timeline is unavailable.");
                captureWindow = new IntPtr(movie.hwnd); ui.VerifyWindow(captureWindow);
            }
            RECT bounds; if (!GetWindowRect(captureWindow, out bounds)) throw new InvalidOperationException("Cannot read MAGIX bounds.");
            int left = Math.Max(bounds.left, GetSystemMetrics(76)), top = Math.Max(bounds.top, GetSystemMetrics(77));
            int right = Math.Min(bounds.right, GetSystemMetrics(76) + GetSystemMetrics(78)), bottom = Math.Min(bounds.bottom, GetSystemMetrics(77) + GetSystemMetrics(79));
            if (right <= left || bottom <= top || right - left > 4096 || bottom - top > 4096) throw new InvalidOperationException("Visible MAGIX bounds are invalid.");
            for (int y = top + 1; y < bottom; y += 80) for (int x = left + 1; x < right; x += 80)
            {
                IntPtr hit = InputNative.WindowFromPoint(new InputNative.POINT { x = x, y = y }); uint pid; Native.GetWindowThreadProcessId(hit, out pid);
                if (pid != state.pid || Native.GetAncestor(hit, 3) != editor) throw new InvalidOperationException("Another window obstructs the MAGIX capture at " + x + "," + y + ": class=" + Native.ClassName(hit) + ", pid=" + pid + ", root_owner=" + Native.GetAncestor(hit, 3).ToInt64() + ".");
            }
            using (var bitmap = new Bitmap(right - left, bottom - top, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                ui.VerifyWindow(editor); graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) bitmap.Save(stream, ImageFormat.Png);
            }
            return output;
        }
        internal static string Capture(string output)
        {
            if (!String.Equals(Path.GetExtension(output), ".png", StringComparison.OrdinalIgnoreCase) || File.Exists(output)) throw new InvalidOperationException("Capture requires a new .png output file.");
            MagixDesktopState state = MagixEditingWorkerMain.Snapshot(false);
            var ui = new MagixEditingUi(state, delegate { throw new InvalidOperationException("Capture cannot send editing input."); });
            ui.VerifyProcess();
            var windows = state.windows.Where(w => w.enabled && (w.hwnd == state.editor_hwnd || w.root_owner == state.editor_hwnd || InputNative.IsOwnedMenu(new IntPtr(w.hwnd), new IntPtr(state.editor_hwnd)))).ToList();
            MagixWindowSnapshot chosen = windows.FirstOrDefault(w => w.class_name == "#32768") ?? windows.FirstOrDefault(w => w.hwnd == state.foreground_hwnd) ?? windows.FirstOrDefault(w => w.hwnd != state.editor_hwnd) ?? windows.FirstOrDefault(w => w.hwnd == state.editor_hwnd);
            if (chosen == null) throw new InvalidOperationException("No verified MAGIX window is available for capture.");
            IntPtr window = new IntPtr(chosen.hwnd); ui.VerifyWindow(window);
            RECT rect; if (!GetWindowRect(window, out rect)) throw new InvalidOperationException("Cannot read the MAGIX window bounds.");
            int width = rect.right - rect.left, height = rect.bottom - rect.top;
            if (width < 2 || height < 2 || width > 4096 || height > 4096) throw new InvalidOperationException("MAGIX capture dimensions are outside the bounded range.");
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc(); bool captured;
                try { captured = PrintWindow(window, dc, 2); } finally { graphics.ReleaseHdc(dc); }
                ui.VerifyProcess();
                if (!captured) throw new InvalidOperationException("MAGIX did not provide a PrintWindow snapshot.");
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) bitmap.Save(stream, ImageFormat.Png);
            }
            return output;
        }
    }
}
