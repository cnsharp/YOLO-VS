namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// xterm color lookups used by <see cref="TerminalEmulator"/>. The 16 base colors
    /// use the Windows Terminal "Campbell" scheme so output matches what the user sees
    /// in a normal Windows console.
    /// </summary>
    internal static class TerminalPalette
    {
        public const int DefaultForeground = 0xCCCCCC;
        public const int DefaultBackground = 0x0C0C0C;

        private static readonly int[] Base16 =
        {
            0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00,
            0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
            0x767676, 0xE74856, 0x16C60C, 0xF9F1A5,
            0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2
        };

        public static int Ansi(int index)
        {
            if (index < 0 || index > 15) return DefaultForeground;
            return Base16[index];
        }

        public static int Xterm256(int index)
        {
            if (index < 0) return DefaultForeground;
            if (index < 16) return Base16[index];
            if (index < 232)
            {
                int i = index - 16;
                int r = Level(i / 36);
                int g = Level(i / 6 % 6);
                int b = Level(i % 6);
                return (r << 16) | (g << 8) | b;
            }
            if (index < 256)
            {
                int v = 8 + (index - 232) * 10;
                return (v << 16) | (v << 8) | v;
            }
            return DefaultForeground;
        }

        private static int Level(int step)
        {
            return step == 0 ? 0 : 55 + step * 40;
        }
    }
}
