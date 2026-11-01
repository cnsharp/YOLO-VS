using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Opens the Manage Agents tool window (hosted by <see cref="ManageAgentsToolWindowPane"/>,
    /// docked tabbed with the YOLO panel). Registered on the View menu / Ctrl+W, A.
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

        private void Execute(object sender, EventArgs e) => Open();

        /// <summary>
        /// Shows the Manage Agents tool window. Entry point for both the View menu command and
        /// external callers (e.g. the YOLO panel's agent dropdown). Safe to call on the UI thread;
        /// the actual show is marshalled via the package's JoinableTaskFactory.
        /// </summary>
        public static void Open()
        {
            var cmd = Instance;
            if (cmd == null)
            {
                Log.Write("ManageAgentsCommand.Open: command not initialized.");
                return;
            }
            _ = cmd._package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await cmd.ShowManageAgentsAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError($"Manage Agents open failed: {ex}");
                }
            });
        }

        /// <summary>
        /// Create (if needed) and show/activate the Manage Agents tool window. Mirrors the YOLO
        /// panel's show path exactly, so it avoids the native editor-creation path that faulted.
        /// </summary>
        private async Task ShowManageAgentsAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await _package.FindToolWindowAsync(
                typeof(ManageAgentsToolWindowPane), 0, true, _package.DisposalToken);
            if (window?.Frame is not IVsWindowFrame frame)
            {
                Log.Write("ManageAgentsCommand: tool window frame unavailable.");
                return;
            }

            // Dock the pane in the document tab group (the editor well) rather than a docked
            // tool-window slot: an MDI-child frame IS a document window, so it tabs alongside open
            // editors. This is the VS analog of IntelliJ's editor-area Manage Agents tab.
            try
            {
                frame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_MdiChild);
            }
            catch (Exception ex)
            {
                // Non-fatal: if the shell rejects the mode change the window still opens as a
                // normal docked tool window.
                Log.Write("ManageAgentsCommand: could not set document frame mode: " + ex.Message);
            }
            frame.Show();
        }
    }
}
