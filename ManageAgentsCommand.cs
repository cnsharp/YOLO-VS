using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Opens the Manage Agents document tab (a virtual, non-file-backed document hosted by
    /// <see cref="ManageAgentsEditorFactory"/>). Registered on the View menu / Ctrl+W, A.
    /// </summary>
    internal sealed class ManageAgentsCommand
    {
        public const int CommandId = Constants.ManageAgentsCommandId;
        public static readonly Guid CommandSet = new Guid(Constants.YoloCmdSetGuid);

        private readonly AsyncPackage _package;

        private ManageAgentsCommand(AsyncPackage package, IMenuCommandService commandService)
        {
            _package = package;
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
        }

        public static ManageAgentsCommand? Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            // AddCommand must run on the UI thread (mirrors YoloToolWindowCommand). The async
            // InitializeAsync runs under AllowsBackgroundLoading, where IMenuCommandService can
            // resolve to null if queried off-thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as IMenuCommandService;
            if (commandService == null)
            {
                Log.Write("ManageAgentsCommand: IMenuCommandService unavailable; command has no handler.");
                return;
            }
            Instance = new ManageAgentsCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    OpenManageAgents();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError($"Manage Agents open failed: {ex}");
                }
            });
        }

        /// <summary>
        /// Open the Manage Agents document tab. Prefers <see cref="IVsUIShellOpenDocument.OpenStandardEditor"/>
        /// with <c>OpenAsNew</c> (the documented path for a synthetic document — the shell owns the
        /// RDT entry and de-dupes by moniker); falls back to <see cref="IVsUIShell.CreateDocumentWindow"/>
        /// if that is unavailable.
        /// </summary>
        internal static void OpenManageAgents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var package = YoloPackage.Instance;
            if (package == null) return;

            var openDoc = (IVsUIShellOpenDocument)package.GetService(typeof(SVsUIShellOpenDocument));
            if (openDoc != null)
            {
                var editorGuid = new Guid(Constants.ManageAgentsEditorGuid);
                var logicalView = VSConstants.LOGVIEWID.Primary_guid;
                var flags = (uint)Microsoft.VisualStudio.Shell.VSOSEFLAGS.OpenAsNew;
                int hr = openDoc.OpenStandardEditor(
                    flags,
                    Constants.ManageAgentsDocName,
                    ref editorGuid,
                    null,
                    null,
                    null,
                    null,
                    out IVsWindowFrame frame);
                if (ErrorHandler.Succeeded(hr) && frame != null)
                {
                    frame.Show();
                    Marshal.ReleaseComObject(frame);
                    Marshal.ReleaseComObject(openDoc);
                    return;
                }
                Marshal.ReleaseComObject(openDoc);
            }

            OpenViaCreateDocumentWindow(package);
        }

        /// <summary>
        /// Fallback: create the document frame directly, supplying our own doc view (the pane also
        /// serves as the doc data). Used when OpenStandardEditor cannot host the virtual document.
        /// </summary>
        private static void OpenViaCreateDocumentWindow(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var shell = (IVsUIShell)package.GetService(typeof(SVsUIShell));
            if (shell == null) return;

            var editorGuid = new Guid(Constants.ManageAgentsEditorGuid);
            var viewLogical = VSConstants.LOGVIEWID.Primary_guid;
            var pane = new ManageAgentsWindowPane();
            IntPtr punkView = Marshal.GetIUnknownForObject(pane);
            IntPtr punkData = Marshal.GetIUnknownForObject(pane);
            int hr = shell.CreateDocumentWindow(
                1u, // __VSCREATEDOCWIN.CDW_fCreateIfNotFound
                Constants.ManageAgentsDocName,
                null,
                null,
                ref editorGuid,
                ref viewLogical,
                null,
                0,
                punkView,
                punkData,
                out IVsWindowFrame frame);
            Marshal.Release(punkView);
            Marshal.Release(punkData);
            if (ErrorHandler.Failed(hr) || frame == null)
            {
                Marshal.ReleaseComObject(shell);
                return;
            }
            frame.Show();
            Marshal.ReleaseComObject(frame);
            Marshal.ReleaseComObject(shell);
        }
    }
}
