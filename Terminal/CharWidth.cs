using System.Globalization;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Terminal display width of a Unicode scalar, in character cells.
    /// <para>
    /// A monospaced terminal grid is not "one char = one cell": CJK ideographs,
    /// Hangul, fullwidth forms and emoji occupy two cells, and combining marks
    /// occupy none. Getting this wrong shifts every column after a Chinese
    /// character, which visibly corrupts box-drawing TUIs.
    /// </para>
    /// Ranges follow the Unicode East Asian Width property (W and F classes).
    /// </summary>
    internal static class CharWidth
    {
        /// <summary>Returns 0 (combining/zero-width), 1 (narrow) or 2 (wide).</summary>
        public static int Of(int cp)
        {
            if (cp < 0x0300) return 1;          // fast path: Latin/ASCII and friends

            if (IsZeroWidth(cp)) return 0;
            return IsWide(cp) ? 2 : 1;
        }

        private static bool IsZeroWidth(int cp)
        {
            // Explicit zero-width and joiner controls that categories don't all cover.
            if (cp >= 0x200B && cp <= 0x200F) return true;   // ZWSP..RLM
            if (cp >= 0xFE00 && cp <= 0xFE0F) return true;   // variation selectors
            if (cp == 0xFEFF) return true;                   // BOM / ZWNBSP
            if (cp >= 0xE0100 && cp <= 0xE01EF) return true; // variation selectors supp.

            if (cp > 0xFFFF) return false;                   // categories below need a char
            switch (CharUnicodeInfo.GetUnicodeCategory((char)cp))
            {
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.Format:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsWide(int cp)
        {
            return (cp >= 0x1100 && cp <= 0x115F)    // Hangul Jamo initial consonants
                || (cp >= 0x2E80 && cp <= 0x303E)    // CJK radicals, Kangxi, CJK symbols
                || (cp >= 0x3041 && cp <= 0x33FF)    // Kana, Bopomofo, enclosed CJK, ...
                || (cp >= 0x3400 && cp <= 0x4DBF)    // CJK ext A
                || (cp >= 0x4E00 && cp <= 0x9FFF)    // CJK unified ideographs
                || (cp >= 0xA000 && cp <= 0xA4CF)    // Yi
                || (cp >= 0xA960 && cp <= 0xA97F)    // Hangul Jamo ext A
                || (cp >= 0xAC00 && cp <= 0xD7A3)    // Hangul syllables
                || (cp >= 0xF900 && cp <= 0xFAFF)    // CJK compatibility ideographs
                || (cp >= 0xFE10 && cp <= 0xFE19)    // vertical forms
                || (cp >= 0xFE30 && cp <= 0xFE6F)    // CJK compatibility forms
                || (cp >= 0xFF00 && cp <= 0xFF60)    // fullwidth ASCII variants
                || (cp >= 0xFFE0 && cp <= 0xFFE6)    // fullwidth signs
                || (cp >= 0x1F300 && cp <= 0x1F64F)  // pictographs, emoticons
                || (cp >= 0x1F900 && cp <= 0x1F9FF)  // supplemental pictographs
                || (cp >= 0x1FA70 && cp <= 0x1FAFF)  // extended pictographs
                || (cp >= 0x20000 && cp <= 0x2FFFD)  // CJK ext B..F
                || (cp >= 0x30000 && cp <= 0x3FFFD); // CJK ext G+
        }
    }
}
