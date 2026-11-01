using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Hosts the <see cref="ManageAgentsControl"/> in a tool window (docked tabbed with the YOLO
    /// panel — see the [ProvideToolWindow] on <see cref="YoloPackage"/>). This replaces the earlier
    /// synthetic-editor-document approach, whose custom IVsEditorFactory / WindowPane-as-doc-data
    /// path faulted with native AccessViolations inside the shell. A ToolWindowPane uses the exact
    /// same WPF hosting as the working YOLO panel.
    /// </summary>
    [Guid(Constants.ManageAgentsToolWindowGuid)]
    public sealed class ManageAgentsToolWindowPane : ToolWindowPane
    {
        public ManageAgentsToolWindowPane() : base(null)
        {
            this.Caption = Constants.ManageAgentsCaption;
            this.Content = new ManageAgentsControl();
        }

        /// <summary>
        /// The control subscribes to the background version checker while alive; stop it when the
        /// tool window is torn down so a closed pane cannot leak through the static event.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && Content is ManageAgentsControl control)
                control.Shutdown();
            base.Dispose(disposing);
        }
    }
}
