using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Windows.Forms.Integration;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Document view + data for the Manage Agents tab. A <see cref="WindowPane"/> hosts the WPF
    /// <see cref="ManageAgentsControl"/> through an <see cref="ElementHost"/> (WinForms bridges WPF
    /// into a doc view), and also implements <see cref="IVsPersistDocData"/> / <see cref="IPersistFileFormat"/>
    /// as no-ops so the virtual document never prompts to save and stays out of the RDT's dirty state.
    /// </summary>
    [Guid(Constants.ManageAgentsEditorGuid)]
    internal sealed class ManageAgentsWindowPane : WindowPane, IVsPersistDocData, IPersistFileFormat
    {
        private ManageAgentsControl? _control;

        public ManageAgentsWindowPane() : base(null)
        {
            _control = new ManageAgentsControl();
            var host = new ElementHost
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                Child = _control
            };
            this.Content = host;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Stop listening to background version checks before the WPF tree is torn down.
                _control?.Shutdown();
                _control = null;
            }
            base.Dispose(disposing);
        }

        // ── IVsPersistDocData (no real persistence) ────────────────

        public int GetGuidEditorType(out Guid pguidEditorType)
        {
            pguidEditorType = typeof(ManageAgentsEditorFactory).GUID;
            return VSConstants.S_OK;
        }

        public int Close() => VSConstants.S_OK;

        public int IsDocDataDirty(out int pfDirty)
        {
            pfDirty = 0;
            return VSConstants.S_OK;
        }

        public int SetUntitledDocPath(string pszDocDataPath) => VSConstants.S_OK;

        public int LoadDocData(string? pszMkDocument) => VSConstants.S_OK;

        public int SaveDocData(VSSAVEFLAGS dwSave, out string? pbstrMkDocumentNew, out int pfSaveCanceled)
        {
            pbstrMkDocumentNew = null;
            pfSaveCanceled = 0;
            return VSConstants.S_OK;
        }

        public int OnRegisterDocData(uint docCookie, IVsHierarchy? pHier, uint itemid) => VSConstants.S_OK;

        public int RenameDocData(uint grfAttrib, IVsHierarchy? pHier, uint itemid, string? pszMkDocumentNew)
            => VSConstants.S_OK;

        public int IsDocDataReloadable(out int pfReloadable)
        {
            pfReloadable = 0;
            return VSConstants.S_OK;
        }

        public int ReloadDocData(uint grfFlags) => VSConstants.S_OK;

        // ── IPersistFileFormat (no real file) ─────────────────────

        public int GetClassID(out Guid pClassID)
        {
            pClassID = typeof(ManageAgentsEditorFactory).GUID;
            return VSConstants.S_OK;
        }

        public int IsDirty(out int pfIsDirty)
        {
            pfIsDirty = 0;
            return VSConstants.S_OK;
        }

        public int InitNew(uint nFormatIndex) => VSConstants.S_OK;

        public int Load(string? pszFilename, uint grfMode, int fReadOnly) => VSConstants.S_OK;

        public int Save(string? pszFilename, int fRemember, uint nFormatIndex) => VSConstants.S_OK;

        public int SaveCompleted(string? pszFilename) => VSConstants.S_OK;

        public int GetCurFile(out string? pbstrFilename, out uint pnFormatIndex)
        {
            pbstrFilename = Constants.ManageAgentsDocName;
            pnFormatIndex = 0;
            return VSConstants.S_OK;
        }

        public int GetFormatList(out string? pbstrFormatList)
        {
            pbstrFormatList = null;
            return VSConstants.S_OK;
        }
    }
}
