using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace EditBuddy
{
    // No generic input command is exposed through IPC. These helpers are private
    // implementation details of fixed, state-checked MAGIX command adapters.
    internal sealed class MagixEditingUi
    {
        private readonly MagixDesktopState target;
        private readonly Action markInput;
        internal MagixEditingUi(MagixDesktopState state, Action marker) { target = state; markInput = marker; }

        internal void VerifyProcess()
        {
            if (!String.Equals(Environment.MachineName, "MSI", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Editing input is restricted to MSI.");
            if (Process.GetCurrentProcess().SessionId != target.session_id || target.session_id == 0 || EditingNative.WTSGetActiveConsoleSessionId() != target.session_id || EditingNative.InputDesktop() != "Default") throw new InvalidOperationException("The MSI interactive desktop is no longer available.");
            using (Process p = Process.GetProcessById(target.pid))
                if (p.StartTime.ToUniversalTime().Ticks != target.process_start_utc_ticks || p.SessionId != target.session_id || !String.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(target.executable), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected MAGIX process identity changed.");
            if ((target.version ?? "").Replace(",", ".").Replace(" ", "") != "20.0.1.80") throw new InvalidOperationException("This MAGIX version has no validated editing adapter.");
        }
        internal void VerifyWindow(IntPtr window)
        {
            VerifyProcess(); uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            if (!Native.IsWindow(window) || pid != target.pid || !Native.IsWindowVisible(window) || !Native.IsWindowEnabled(window)) throw new InvalidOperationException("The intended MAGIX window is unavailable or blocked.");
            if (target.editor_hwnd != 0 && window.ToInt64() != target.editor_hwnd && Native.GetAncestor(window, 3).ToInt64() != target.editor_hwnd && !InputNative.IsOwnedMenu(window, new IntPtr(target.editor_hwnd)))
                throw new InvalidOperationException("The dialog is no longer owned by the intended MAGIX editor.");
        }
        internal AutomationElement Unique(IntPtr window, string automationId, string name, ControlType type, bool requireEnabled = true)
        {
            VerifyWindow(window);
            var available = CachedTree(window).Where(e => e.Cached.ProcessId == target.pid && (!requireEnabled || e.Cached.IsEnabled) && !e.Cached.IsOffscreen
                && (automationId == null || e.Cached.AutomationId == automationId) && (name == null || e.Cached.Name == name)
                && (type == null || e.Cached.ControlType == type)).ToList();
            if (available.Count != 1) throw new InvalidOperationException("The MAGIX control is missing or ambiguous: " + (name ?? automationId ?? "unnamed"));
            return available[0];
        }
        private static IList<AutomationElement> CachedTree(IntPtr window)
        {
            var request = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.RawViewCondition, AutomationElementMode = AutomationElementMode.Full };
            foreach (AutomationProperty property in new[] { AutomationElement.ProcessIdProperty, AutomationElement.NameProperty, AutomationElement.AutomationIdProperty, AutomationElement.ControlTypeProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.HasKeyboardFocusProperty }) request.Add(property);
            var items = new List<AutomationElement>();
            var pending = new Queue<AutomationElement>(); pending.Enqueue(AutomationElement.FromHandle(window).GetUpdatedCache(request));
            while (pending.Count != 0)
            {
                AutomationElement element = pending.Dequeue(); items.Add(element);
                if (items.Count > 1200) throw new InvalidOperationException("The MAGIX control tree exceeded its fixed bound.");
                foreach (AutomationElement child in element.CachedChildren) pending.Enqueue(child);
            }
            return items;
        }
        internal void Invoke(IntPtr window, string id, string name, ControlType type, Action beforeInvoke = null)
        {
            AutomationElement element = Unique(window, id, name, type); object pattern;
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) throw new InvalidOperationException("The MAGIX control does not support Invoke.");
            VerifyWindow(window); markInput(); VerifyWindow(window);
            element = Unique(window, id, name, type);
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) throw new InvalidOperationException("The invocation target changed while waiting for input permission.");
            if (beforeInvoke != null) beforeInvoke();
            ((InvokePattern)pattern).Invoke();
        }
        internal void Expand(IntPtr window, string id)
        {
            AutomationElement element = Unique(window, id, "", ControlType.MenuItem); object pattern;
            if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern)) throw new InvalidOperationException("The verified menu item has no submenu.");
            VerifyWindow(window); markInput(); VerifyWindow(window);
            element = Unique(window, id, "", ControlType.MenuItem);
            if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern)) throw new InvalidOperationException("The menu changed before expansion.");
            ((ExpandCollapsePattern)pattern).Expand();
        }
        internal void DeclineKnownProxyPrompt()
        {
            IntPtr dialog = FindWindow("MAGIX Movie Edit Pro", "#32770", true);
            if (dialog == IntPtr.Zero) return;
            string text = Unique(dialog, "371", null, ControlType.Text).Current.Name;
            if (text != "You have activated proxy edit.\r\nIn the project there are video objects which don't have\r\nproxy files. Would you like to create the proxy files now?") throw new InvalidOperationException("An unexpected MAGIX dialog blocks the next operation.");
            Invoke(dialog, "399", "No", ControlType.Button);
            if (!Wait(delegate { return !Native.IsWindow(dialog); }, 2000)) throw new InvalidOperationException("The proxy creation prompt did not close.");
        }
        internal string ReadValue(IntPtr window, string id, string name, ControlType type)
        {
            object pattern; AutomationElement element = Unique(window, id, name, type);
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) throw new InvalidOperationException("A readable value is unavailable for this MAGIX control.");
            return ((ValuePattern)pattern).Current.Value;
        }
        internal void SetValue(IntPtr window, string id, string name, ControlType type, string value)
        {
            object pattern; AutomationElement element = Unique(window, id, name, type);
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) || ((ValuePattern)pattern).Current.IsReadOnly) throw new InvalidOperationException("The MAGIX control has no writable Value pattern.");
            VerifyWindow(window); markInput(); VerifyWindow(window);
            element = Unique(window, id, name, type);
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) || ((ValuePattern)pattern).Current.IsReadOnly) throw new InvalidOperationException("The writable target changed while waiting for input permission.");
            ((ValuePattern)pattern).SetValue(value);
            string observed = ReadValue(window, id, name, type);
            if (!String.Equals(value, observed, StringComparison.Ordinal)) throw new InvalidOperationException("MAGIX did not retain the requested field value.");
        }
        internal void SetToggle(IntPtr window, string name, bool enabled)
        {
            object pattern; AutomationElement element = Unique(window, null, name, ControlType.CheckBox);
            if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) throw new InvalidOperationException("The checkbox has no readable Toggle pattern.");
            ToggleState desired = enabled ? ToggleState.On : ToggleState.Off;
            if (((TogglePattern)pattern).Current.ToggleState == desired) return;
            // Qt's accessibility Toggle changes checked appearance without firing
            // the QML onClicked callback that updates MAGIX's native view model.
            // Use the freshly bounded checkbox click and verify its resulting state.
            ClickElement(window, null, name, ControlType.CheckBox);
            if (!Wait(delegate { object fresh; AutomationElement check = Unique(window, null, name, ControlType.CheckBox); return check.TryGetCurrentPattern(TogglePattern.Pattern, out fresh) && ((TogglePattern)fresh).Current.ToggleState == desired; }, 1200))
                throw new InvalidOperationException("The checkbox did not reach its requested state: " + name);
        }
        internal void SetNativeCheck(IntPtr window, string id, string name, bool enabled)
        {
            AutomationElement element = Unique(window, id, name, ControlType.Button);
            IntPtr handle = new IntPtr(element.Current.NativeWindowHandle);
            if (handle == IntPtr.Zero || Native.ClassName(handle) != "Button") throw new InvalidOperationException("The native checkbox identity changed.");
            int desired = enabled ? 1 : 0;
            if (InputNative.CheckState(handle) == desired) return;
            ClickElement(window, id, name, ControlType.Button);
            if (InputNative.CheckState(new IntPtr(Unique(window, id, name, ControlType.Button).Current.NativeWindowHandle)) != desired) throw new InvalidOperationException("The native checkbox did not retain its requested state.");
        }
        private IList<AutomationElement> StartupFields(IntPtr window)
        {
            VerifyWindow(window);
            var tree = CachedTree(window);
            if (!tree.Any(e => e.Cached.Name == "Create project" && e.Cached.ControlType == ControlType.Button && e.Cached.IsEnabled && !e.Cached.IsOffscreen)) throw new InvalidOperationException("The verified New project form is not visible.");
            var fields = tree.Where(e => e.Cached.ProcessId == target.pid && e.Cached.ControlType == ControlType.Edit && e.Cached.IsEnabled && !e.Cached.IsOffscreen).OrderBy(e => e.Cached.BoundingRectangle.Top).ToList();
            if (fields.Count != 2) throw new InvalidOperationException("The New project form no longer has its observed pair of editable fields.");
            var first = fields[0].Cached.BoundingRectangle; var second = fields[1].Cached.BoundingRectangle;
            if (first.IsEmpty || second.IsEmpty || Math.Abs(first.Left - second.Left) > 3 || second.Top <= first.Bottom || second.Top - first.Bottom > first.Height * 2)
                throw new InvalidOperationException("The New project field arrangement changed.");
            return fields;
        }
        internal string StartupValue(IntPtr window, bool folder)
        {
            AutomationElement element = StartupFields(window)[folder ? 1 : 0]; object pattern;
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) throw new InvalidOperationException("The project field has no readable value.");
            return ((ValuePattern)pattern).Current.Value;
        }
        private bool FocusStartupField(IntPtr window, bool folder)
        {
            Focus(window); var element = StartupFields(window)[folder ? 1 : 0]; ClickResolved(window, element);
            return Wait(delegate { return element.Current.HasKeyboardFocus; }, 1000);
        }
        internal void EnsureStartupFolderEnabled(IntPtr window)
        {
            // The field's focus response, not a provider's Enabled bit or checkbox
            // appearance, proves the native copySources binding is active.
            if (FocusStartupField(window, true)) return;
            ClickElement(window, null, "Copy media to project folder", ControlType.CheckBox);
            if (!FocusStartupField(window, true)) throw new InvalidOperationException("The project folder field is still disabled or cannot receive verified focus.");
        }
        internal void SetStartupValue(IntPtr window, bool folder, string value)
        {
            Focus(window);
            AutomationElement element = StartupFields(window)[folder ? 1 : 0]; object pattern;
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) || ((ValuePattern)pattern).Current.IsReadOnly) throw new InvalidOperationException("The project field has no writable value.");
            // Qt 5.7 exposes Value while rejecting UIA SetFocus on these fields.
            // A bounded physical click focuses the freshly identified field; the
            // value still travels through its semantic ValuePattern and readback.
            if (!FocusStartupField(window, folder)) throw new InvalidOperationException("The intended project field did not receive focus; no text was sent.");
            element = StartupFields(window)[folder ? 1 : 0];
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) throw new InvalidOperationException("The project field changed before writing.");
            VerifyWindow(window); markInput(); VerifyWindow(window);
            string before = ((ValuePattern)pattern).Current.Value;
            try { ((ValuePattern)pattern).SetValue(value); }
            catch (InvalidOperationException)
            {
                string after = StartupValue(window, folder);
                if (after != value)
                {
                    if (after != before) throw new InvalidOperationException("The project field changed unexpectedly after a rejected Value write; no retry was sent.");
                    // Readback proves the rejected Value write did not alter this
                    // field. Re-identify/click it and use bounded Unicode input.
                    if (!FocusStartupField(window, folder)) throw new InvalidOperationException("The intended project field lost focus before fallback input.");
                    VerifyWindow(window);
                    if (Native.GetForegroundWindow() != window) throw new InvalidOperationException("MAGIX lost foreground focus before project-field input.");
                    InputNative.ReplaceText(value);
                }
            }
            // The folder binding commits on editingFinished. Transfer focus to the
            // other already-identified field, then re-resolve and read the value.
            if (!Wait(delegate { return ((ValuePattern)pattern).Current.Value == value; }, 1500)) throw new InvalidOperationException("The project field did not accept the requested text.");
            FocusStartupField(window, !folder);
            if (StartupValue(window, folder) != value) throw new InvalidOperationException("The New project field did not preserve its requested value.");
        }
        internal void Focus(IntPtr window)
        {
            VerifyWindow(window); markInput(); VerifyWindow(window);
            if (Native.IsIconic(window)) Native.ShowWindow(window, 9);
            Native.SetForegroundWindow(window);
            if (!Wait(delegate { return Native.GetForegroundWindow() == window; }, 1500)) throw new InvalidOperationException("Windows did not grant MAGIX foreground focus on MSI.");
        }
        internal bool Exists(IntPtr window, string id, string name, ControlType type)
        {
            try { Unique(window, id, name, type); return true; }
            catch (InvalidOperationException) { return false; }
            catch (ElementNotAvailableException) { return false; }
        }
        internal void ClickElement(IntPtr window, string id, string name, ControlType type)
        {
            Focus(window);
            AutomationElement element = Unique(window, id, name, type);
            ClickResolved(window, element);
        }
        private void ClickResolved(IntPtr window, AutomationElement element)
        {
            VerifyWindow(window);
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2) throw new InvalidOperationException("The MAGIX control has no usable current bounds.");
            int x = checked((int)Math.Round(bounds.X + bounds.Width / 2));
            int y = checked((int)Math.Round(bounds.Y + bounds.Height / 2));
            InputNative.POINT point = new InputNative.POINT { x = x, y = y };
            IntPtr hit = InputNative.WindowFromPoint(point); uint hitPid; Native.GetWindowThreadProcessId(hit, out hitPid);
            if (hitPid != target.pid || Native.GetAncestor(hit, 2) != window || Native.GetForegroundWindow() != window) throw new InvalidOperationException("Another window obstructs the intended MAGIX control.");
            VerifyWindow(window); markInput(); VerifyWindow(window);
            if (element.Current.BoundingRectangle != bounds || Native.GetForegroundWindow() != window || InputNative.WindowFromPoint(point) != hit) throw new InvalidOperationException("The control moved or focus changed while input permission was pending.");
            InputNative.Click(x, y);
        }
        internal IntPtr FindWindow(string title, string className, bool enabled)
        {
            VerifyProcess();
            var matches = Native.WindowsFor(target.pid).Where(w => (title == null || Native.Title(w) == title) && (className == null || Native.ClassName(w) == className) && (!enabled || Native.IsWindowEnabled(w))).ToList();
            if (matches.Count > 1) throw new InvalidOperationException("More than one matching MAGIX window exists.");
            return matches.Count == 1 ? matches[0] : IntPtr.Zero;
        }
        internal static bool Wait(Func<bool> predicate, int timeout)
        {
            var clock = Stopwatch.StartNew();
            do { if (predicate()) return true; Thread.Sleep(50); } while (clock.ElapsedMilliseconds < timeout);
            return false;
        }
    }

    internal static class InputNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint flags; public uint time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort key; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT keyboard; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION data; }
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out UIntPtr result);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { internal int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct GUITHREADINFO { internal uint cbSize, flags; internal IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; internal RECT rcCaret; }
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
        internal static bool IsOwnedMenu(IntPtr menu, IntPtr editor)
        {
            if (Native.ClassName(menu) != "#32768") return false;
            uint pid; uint thread = Native.GetWindowThreadProcessId(menu, out pid);
            var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf(typeof(GUITHREADINFO)) };
            return GetGUIThreadInfo(thread, ref info) && info.hwndMenuOwner != IntPtr.Zero && Native.GetAncestor(info.hwndMenuOwner, 2) == editor;
        }
        internal static int CheckState(IntPtr window)
        {
            UIntPtr result;
            if (SendMessageTimeout(window, 0x00F0, IntPtr.Zero, IntPtr.Zero, 2, 500, out result) == IntPtr.Zero) throw new InvalidOperationException("Native checkbox readback timed out.");
            return checked((int)result.ToUInt64());
        }
        internal static void Click(int x, int y)
        {
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            if (width < 2 || height < 2 || x < left || x >= left + width || y < top || y >= top + height) throw new InvalidOperationException("Control lies outside the MSI desktop.");
            var move = new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { dx = (int)(((long)x - left) * 65535 / (width - 1)), dy = (int)(((long)y - top) * 65535 / (height - 1)), flags = 0x8000 | 0x4000 | 0x0001 } } };
            var down = new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { flags = 0x0002 } } };
            var up = new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { flags = 0x0004 } } };
            if (SendInput(3, new[] { move, down, up }, Marshal.SizeOf(typeof(INPUT))) != 3) throw new InvalidOperationException("Windows did not accept the bounded MAGIX click.");
        }
        private static INPUT Key(ushort key, ushort scan, uint flags) { return new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { key = key, scan = scan, flags = flags } } }; }
        internal static void ReplaceText(string value)
        {
            if (value == null || value.Length > 1024 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new InvalidOperationException("Invalid bounded project-field text.");
            try
            {
                INPUT[] select = { Key(0x11, 0, 0), Key(0x41, 0, 0), Key(0x41, 0, 2), Key(0x11, 0, 2) };
                if (SendInput((uint)select.Length, select, Marshal.SizeOf(typeof(INPUT))) != select.Length) throw new InvalidOperationException("Project-field selection input was incomplete.");
                var text = new List<INPUT>();
                foreach (char character in value) { text.Add(Key(0, character, 4)); text.Add(Key(0, character, 4 | 2)); }
                if (SendInput((uint)text.Count, text.ToArray(), Marshal.SizeOf(typeof(INPUT))) != text.Count) throw new InvalidOperationException("Project-field text input was incomplete.");
            }
            finally { SendInput(1, new[] { Key(0x11, 0, 2) }, Marshal.SizeOf(typeof(INPUT))); }
        }
    }
}
