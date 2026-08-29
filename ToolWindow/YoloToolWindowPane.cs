using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Implements the YOLO tool window pane.
    /// </summary>
    [Guid(Constants.ToolWindowGuid)]
    public class YoloToolWindowPane : ToolWindowPane
    {
        public YoloToolWindowPane() : base(null)
        {
            this.Caption = "YOLO";

            // Create and set the pane content
            var panel = new YoloPanel();
            this.Content = panel;
        }

        protected override void OnCreate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            base.OnCreate();
            SetDefaultWidth();
        }

        /// <summary>
        /// Kills the terminal's shell process and reader thread when the pane goes away.
        /// ToolWindowPane owns the WPF content but knows nothing about the ConPTY behind
        /// it, so the teardown has to be forwarded explicitly.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing && Content is YoloPanel panel)
                panel.Shutdown();
            base.Dispose(disposing);
        }

        /// <summary>
        /// Default the tool window to 45% of the main VS window width (full height),
        /// so the embedded terminal has enough room for full-screen TUIs like Claude
        /// Code. Non-critical: any failure is ignored so it never blocks the window.
        /// </summary>
        private void SetDefaultWidth()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(GetService(typeof(SVsUIShell)) is IVsUIShell uiShell)) return;
                uiShell.GetDialogOwnerHwnd(out IntPtr mainHwnd);
                if (mainHwnd == IntPtr.Zero) return;

                var rect = new RECT();
                if (!GetWindowRect(mainHwnd, ref rect)) return;
                int mainWidth = rect.right - rect.left;
                int width = (int)Math.Round(mainWidth * 0.45);

                if (Frame is IVsWindowFrame frame)
                {
                    // Read the current position and only widen it; SFP_fRect (0x4) is what
                    // actually applies the rectangle — passing 0 (as before) did nothing.
                    var sfp = new VSSETFRAMEPOS[] { (VSSETFRAMEPOS)0x0004 };
                    frame.GetFramePos(sfp, out Guid rel, out int x, out int y, out int _, out int cy);
                    frame.SetFramePos((VSSETFRAMEPOS)0x0004, Guid.Empty, x, y, width, cy);
                }
            }
            catch
            {
                // Sizing is best-effort; never fault the tool window over it.
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, ref RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }
    }
}
