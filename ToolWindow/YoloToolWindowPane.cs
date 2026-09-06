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
    }
}
