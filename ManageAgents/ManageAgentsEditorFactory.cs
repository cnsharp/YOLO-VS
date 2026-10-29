using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Editor factory for the synthetic <c>ManageAgents.yolo</c> document — a real document-well
    /// tab (the Visual Studio analog of IntelliJ's editor-area Manage Agents tab). The document is
    /// not backed by a real file; it is opened with <c>VSOSEFLAGS_OpenAsNew</c> so the Running
    /// Document Table holds a virtual entry keyed by <see cref="Constants.ManageAgentsDocName"/>.
    ///
    /// <see cref="CreateEditorInstance"/> builds a single <see cref="ManageAgentsWindowPane"/> that
    /// serves as BOTH the doc view (<see cref="IVsWindowPane"/>) and the doc data
    /// (<see cref="IVsPersistDocData"/>); the pane carries no real persistence, so the tab never
    /// prompts to save.
    /// </summary>
    [Guid(Constants.ManageAgentsEditorGuid)]
    public sealed class ManageAgentsEditorFactory : IVsEditorFactory, IDisposable
    {
        private ServiceProvider? _serviceProvider;

        public int CreateEditorInstance(
            uint grfCreateDoc,
            string? pszMkDocument,
            string? pszPhysicalView,
            IVsHierarchy? pHier,
            uint itemid,
            IntPtr punkDocDataExisting,
            out IntPtr ppunkDocView,
            out IntPtr ppunkDocData,
            out string? pbstrEditorCaption,
            out Guid pguidCmdUI,
            out int pfCreated)
        {
            ppunkDocView = IntPtr.Zero;
            ppunkDocData = IntPtr.Zero;
            pbstrEditorCaption = string.Empty;
            pguidCmdUI = Guid.Empty;
            pfCreated = 0;

            // Deferred creation is a no-op for a logical, always-fresh document.
            if ((grfCreateDoc & (uint)__VSEDITORCREATEREASON.VSEDITORCREATEREASON_Deferred) != 0)
                return VSConstants.E_NOTIMPL;

            var pane = new ManageAgentsWindowPane();
            ppunkDocView = Marshal.GetIUnknownForObject(pane);

            // Reuse the existing doc data (RDT already holds one) or publish the pane as the doc data.
            if (punkDocDataExisting != IntPtr.Zero)
                ppunkDocData = punkDocDataExisting;
            else
                ppunkDocData = Marshal.GetIUnknownForObject(pane);

            pbstrEditorCaption = Constants.ManageAgentsCaption;
            pguidCmdUI = typeof(ManageAgentsEditorFactory).GUID;
            pfCreated = 1;
            return VSConstants.S_OK;
        }

        public int MapLogicalView(ref Guid rguidLogicalView, out string? pbstrPhysicalView)
        {
            pbstrPhysicalView = null;
            if (rguidLogicalView == VSConstants.LOGVIEWID.Primary_guid ||
                rguidLogicalView == VSConstants.LOGVIEWID.Code_guid ||
                rguidLogicalView == VSConstants.LOGVIEWID.TextView_guid ||
                rguidLogicalView == Guid.Empty)
            {
                return VSConstants.S_OK;
            }
            return VSConstants.E_NOTIMPL;
        }

        public int SetSite(IServiceProvider psp)
        {
            _serviceProvider?.Dispose();
            _serviceProvider = new ServiceProvider(psp);
            return VSConstants.S_OK;
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
            _serviceProvider = null;
        }
    }
}
