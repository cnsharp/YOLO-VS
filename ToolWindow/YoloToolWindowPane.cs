using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Implements the YOLO tool window pane.
    /// </summary>
    [Guid(Constants.ToolWindowGuid)]
    public class YoloToolWindowPane : ToolWindowPane, IOleCommandTarget
    {
        // Cookie returned by IVsRegisterPriorityCommandTarget; used to unregister on dispose.
        private uint _priorityCookie;

        public YoloToolWindowPane() : base(null)
        {
            this.Caption = Constants.ProductName;

            // Create and set the pane content
            var panel = new YoloPanel();
            this.Content = panel;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            ThreadHelper.ThrowIfNotOnUIThread();
            // Register as a priority command target so VS consults us BEFORE its own editor
            // command bindings (Ctrl+C/H/E/A/..., Ctrl+V, Ctrl+Z, ...). When the terminal has
            // focus we forward the equivalent control byte; otherwise we report "not handled"
            // and VS continues its normal routing. VSFPROPID_CommandTarget does not exist in
            // the v18 SDK, hence the priority-target route.
            if (GetService(typeof(SVsRegisterPriorityCommandTarget)) is IVsRegisterPriorityCommandTarget reg)
            {
                reg.RegisterPriorityCommandTarget(0, this, out _priorityCookie);
            }
        }

        /// <summary>
        /// When the terminal has keyboard focus, VS's editor command bindings (Ctrl+C/H/E/A/...,
        /// Ctrl+V, Ctrl+Z, ...) would otherwise be intercepted before the WPF control sees the
        /// key, so the terminal never receives the control byte. We report those commands as
        /// available and, in <see cref="Exec"/>, forward the equivalent byte to the terminal.
        /// Commands we do not handle MUST be reported as OLECMDERR_E_NOTSUPPORTED so the shell
        /// keeps routing them; returning E_NOTIMPL instead makes the shell treat the whole
        /// command as failed and it pops up "The operation could not be completed. 尚未实现"
        /// for every IDE command (Manage Extensions, File.Exit, ...).
        /// </summary>
        int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (cCmds > 0 && GetFocusedTerminal() != null && IsMapped(pguidCmdGroup, prgCmds[0].cmdID))
            {
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                return 0; // S_OK
            }
            return NotSupported;
        }

        int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            var term = GetFocusedTerminal();
            if (term != null)
            {
                if (IsPaste(pguidCmdGroup, nCmdID))
                {
                    term.PasteInput();
                    return 0; // S_OK
                }
                if (TryMap(pguidCmdGroup, nCmdID, out var bytes))
                {
                    term.SendInput(bytes);
                    return 0; // S_OK
                }
            }
            return NotSupported;
        }

        /// <summary>
        /// "Not handled, keep routing" reply for a priority command target (see the class
        /// comment on <see cref="IOleCommandTarget.QueryStatus"/> for why E_NOTIMPL is wrong).
        /// Fully qualified because this file's namespace has its own <see cref="Constants"/>.
        /// </summary>
        private const int NotSupported = (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;

        /// <summary>Finds the <see cref="WpfTerminalView"/> that currently owns keyboard focus, if any.</summary>
        private static WpfTerminalView? GetFocusedTerminal()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            while (focused != null)
            {
                if (focused is WpfTerminalView v) return v;
                focused = VisualTreeHelper.GetParent(focused);
            }
            return null;
        }

        private static bool IsPaste(Guid g, uint id) =>
            (g == VSConstants.VSStd2K && (VSConstants.VSStd2KCmdID)id == VSConstants.VSStd2KCmdID.PASTE) ||
            (g == VSConstants.GUID_VSStandardCommandSet97 && (VSConstants.VSStd97CmdID)id == VSConstants.VSStd97CmdID.Paste);

        private static bool IsMapped(Guid g, uint id) => IsPaste(g, id) || TryMap(g, id, out _);

        /// <summary>
        /// Maps a VS editor command (when the terminal is focused) to the control byte the
        /// terminal should receive. Returns false for commands we don't intercept.
        /// </summary>
        private static bool TryMap(Guid g, uint id, out string bytes)
        {
            bytes = string.Empty;
            if (g == VSConstants.VSStd2K)
            {
                switch ((VSConstants.VSStd2KCmdID)id)
                {
                    case VSConstants.VSStd2KCmdID.COPY: bytes = "\x03"; return true;      // Ctrl+C -> SIGINT
                    case VSConstants.VSStd2KCmdID.CUT: bytes = "\x18"; return true;       // Ctrl+X
                    case VSConstants.VSStd2KCmdID.SELECTALL: bytes = "\x01"; return true;  // Ctrl+A
                    case VSConstants.VSStd2KCmdID.UNDO: bytes = "\x1a"; return true;       // Ctrl+Z
                    case VSConstants.VSStd2KCmdID.REDO: bytes = "\x19"; return true;       // Ctrl+Y
                    case VSConstants.VSStd2KCmdID.FIND: bytes = "\x06"; return true;       // Ctrl+F
                    case VSConstants.VSStd2KCmdID.REPLACE: bytes = "\x08"; return true;    // Ctrl+H
                    case VSConstants.VSStd2KCmdID.DELETE: bytes = "\x7f"; return true;     // Del
                }
            }
            else if (g == VSConstants.GUID_VSStandardCommandSet97)
            {
                switch ((VSConstants.VSStd97CmdID)id)
                {
                    case VSConstants.VSStd97CmdID.Copy: bytes = "\x03"; return true;
                    case VSConstants.VSStd97CmdID.Cut: bytes = "\x18"; return true;
                    case VSConstants.VSStd97CmdID.SelectAll: bytes = "\x01"; return true;
                    case VSConstants.VSStd97CmdID.Undo: bytes = "\x1a"; return true;
                    case VSConstants.VSStd97CmdID.Redo: bytes = "\x19"; return true;
                    case VSConstants.VSStd97CmdID.Find: bytes = "\x06"; return true;
                    case VSConstants.VSStd97CmdID.Replace: bytes = "\x08"; return true;
                    case VSConstants.VSStd97CmdID.Delete: bytes = "\x7f"; return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Unregisters the priority command target and kills the terminal's shell process and
        /// reader thread when the pane goes away. ToolWindowPane owns the WPF content but knows
        /// nothing about the ConPTY behind it, so the teardown has to be forwarded explicitly.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                if (_priorityCookie != 0 &&
                    GetService(typeof(SVsRegisterPriorityCommandTarget)) is IVsRegisterPriorityCommandTarget reg)
                {
                    reg.UnregisterPriorityCommandTarget(_priorityCookie);
                    _priorityCookie = 0;
                }
                if (Content is YoloPanel panel)
                    panel.Shutdown();
            }
            base.Dispose(disposing);
        }
    }
}
