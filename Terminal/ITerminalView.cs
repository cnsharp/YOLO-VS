using System;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Abstraction over the terminal display (WPF-rendered or WebView2/xterm.js).
    /// The ConPTY host talks to this interface; concrete implementations handle
    /// thread marshaling and rendering internally.
    /// </summary>
    internal interface ITerminalView
    {
        int Columns { get; }
        int Rows { get; }
        void WriteOutput(byte[] data);
        event Action<byte[]>? InputReceived;
        event Action? Resized;
    }
}
