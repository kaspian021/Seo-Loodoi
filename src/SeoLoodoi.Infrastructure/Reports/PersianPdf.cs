using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace SeoLoodoi.Infrastructure.Reports;

/// <summary>
/// Classic Arabic-script shaping: converts logical Persian/Arabic text into
/// Unicode Presentation Forms-B so a left-to-right glyph writer renders the
/// connected letters correctly (each run is reversed for display by the
/// caller). Covers the Arabic block plus the Persian letters پ چ ژ گ ک ی.
/// </summary>
public static class ArabicTextShaper
{
    private enum Join { None, Dual, Right, Transparent }

    public static bool IsArabicShaped(char c) => JoinType(c) is Join.Dual or Join.Right;

    public static IReadOnlyList<char> Shape(string text)
    {
        var result = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (JoinType(c) is not (Join.Dual or Join.Right)) { result[i] = c; continue; }
            var prev = NearestJoining(text, i, -1);
            var next = NearestJoining(text, i, +1);
            // Only a dual-joining previous letter connects forward into this
            // letter; this letter connects onward only when it is dual-joining
            // and the next letter accepts a connection (dual or right-joining).
            var prevJoins = prev is { } p && JoinType(p) == Join.Dual;
            var nextJoins = JoinType(c) == Join.Dual && next is { } n && JoinType(n) is Join.Dual or Join.Right;
            result[i] = PresentationForm(c, prevJoins, nextJoins);
        }
        return result;
    }

    private static char? NearestJoining(string text, int index, int step)
    {
        for (var i = index + step; i >= 0 && i < text.Length; i += step)
        {
            var jt = JoinType(text[i]);
            if (jt == Join.Transparent) continue;
            return text[i];
        }
        return null;
    }

    private static Join JoinType(char c) => c switch
    {
        // Right-joining: accept a connection from the previous letter only.
        '\u0622' or '\u0623' or '\u0624' or '\u0625' or '\u0627' // آ أ ؤ إ ا
            or '\u0629'                                          // ة
            or '\u062F' or '\u0630'                              // د ذ
            or '\u0631' or '\u0632' or '\u0698'                  // ر ز ژ
            or '\u0648'                                          // و
            or '\u06C0'                                          // ۀ
            => Join.Right,
        // Dual-joining Arabic + Persian letters (and tatweel).
        '\u0626'                                                 // ئ
            or '\u0628'                                          // ب
            or >= '\u062A' and <= '\u062E'                       // ت ث ج ح خ
            or >= '\u0633' and <= '\u063A'                       // س ش ص ض ط ظ ع غ
            or '\u0640'                                          // ـ
            or >= '\u0641' and <= '\u0647'                       // ف ق ك ل م ن ه
            or '\u0649' or '\u064A'                              // ى ي
            or '\u067E' or '\u0686' or '\u06A9' or '\u06AF' or '\u06CC' // پ چ ک گ ی
            => Join.Dual,
        // Diacritics and combining marks: skipped when choosing forms.
        >= '\u064B' and <= '\u065F' or '\u0670' => Join.Transparent,
        _ => Join.None,
    };

    private static char PresentationForm(char c, bool prevJoins, bool nextJoins)
    {
        if (!Forms.TryGetValue(c, out var forms)) return c;
        return (prevJoins, nextJoins) switch
        {
            (true, true) => forms.Medial,
            (false, true) => forms.Initial,
            (true, false) => forms.Final,
            _ => forms.Isolated,
        };
    }

    private sealed record FormSet(char Isolated, char Final, char Initial, char Medial);

    // Right-joining letters reuse isolated/final for initial/medial.
    private static FormSet Right(char isolated, char final) => new(isolated, final, isolated, final);
    private static FormSet Dual(char isolated, char final, char initial, char medial) => new(isolated, final, initial, medial);

    private static readonly Dictionary<char, FormSet> Forms = new()
    {
        ['\u0622'] = Right('\uFE81', '\uFE82'), ['\u0623'] = Right('\uFE83', '\uFE84'),
        ['\u0624'] = Right('\uFE85', '\uFE86'), ['\u0625'] = Right('\uFE87', '\uFE88'),
        ['\u0626'] = Dual('\uFE89', '\uFE8A', '\uFE8B', '\uFE8C'),
        ['\u0627'] = Right('\uFE8D', '\uFE8E'),
        ['\u0628'] = Dual('\uFE8F', '\uFE90', '\uFE91', '\uFE92'),
        ['\u0629'] = Right('\uFE93', '\uFE94'),
        ['\u062A'] = Dual('\uFE95', '\uFE96', '\uFE97', '\uFE98'),
        ['\u062B'] = Dual('\uFE99', '\uFE9A', '\uFE9B', '\uFE9C'),
        ['\u062C'] = Dual('\uFE9D', '\uFE9E', '\uFE9F', '\uFEA0'),
        ['\u062D'] = Dual('\uFEA1', '\uFEA2', '\uFEA3', '\uFEA4'),
        ['\u062E'] = Dual('\uFEA5', '\uFEA6', '\uFEA7', '\uFEA8'),
        ['\u062F'] = Right('\uFEA9', '\uFEAA'), ['\u0630'] = Right('\uFEAB', '\uFEAC'),
        ['\u0631'] = Right('\uFEAD', '\uFEAE'), ['\u0632'] = Right('\uFEAF', '\uFEB0'),
        ['\u0633'] = Dual('\uFEB3', '\uFEB4', '\uFEB5', '\uFEB6'),
        ['\u0634'] = Dual('\uFEB7', '\uFEB8', '\uFEB9', '\uFEBA'),
        ['\u0635'] = Dual('\uFEBB', '\uFEBC', '\uFEBD', '\uFEBE'),
        ['\u0636'] = Dual('\uFEBF', '\uFEC0', '\uFEC1', '\uFEC2'),
        ['\u0637'] = Dual('\uFEC3', '\uFEC4', '\uFEC5', '\uFEC6'),
        ['\u0638'] = Dual('\uFEC7', '\uFEC8', '\uFEC9', '\uFECA'),
        ['\u0639'] = Dual('\uFECB', '\uFECC', '\uFECD', '\uFECE'),
        ['\u063A'] = Dual('\uFECF', '\uFED0', '\uFED1', '\uFED2'),
        ['\u0641'] = Dual('\uFED5', '\uFED6', '\uFED7', '\uFED8'),
        ['\u0642'] = Dual('\uFED9', '\uFEDA', '\uFEDB', '\uFEDC'),
        ['\u0643'] = Dual('\uFEDD', '\uFEDE', '\uFEDF', '\uFEE0'),
        ['\u0644'] = Dual('\uFEE1', '\uFEE2', '\uFEE3', '\uFEE4'),
        ['\u0645'] = Dual('\uFEE5', '\uFEE6', '\uFEE7', '\uFEE8'),
        ['\u0646'] = Dual('\uFEE9', '\uFEEA', '\uFEEB', '\uFEEC'),
        ['\u0647'] = Dual('\uFEED', '\uFEEE', '\uFEEF', '\uFEF0'),
        ['\u0648'] = Right('\uFEF1', '\uFEF2'),
        ['\u0649'] = Dual('\uFEEF', '\uFEF0', '\uFBE8', '\uFBE9'),
        ['\u064A'] = Dual('\uFEF1', '\uFEF2', '\uFEF3', '\uFEF4'),
        // Persian letters
        ['\u067E'] = Dual('\uFB56', '\uFB57', '\uFB58', '\uFB59'), // پ
        ['\u0686'] = Dual('\uFB7A', '\uFB7B', '\uFB7C', '\uFB7D'), // چ
        ['\u0698'] = Right('\uFB8D', '\uFB8E'),                     // ژ
        ['\u06A9'] = Dual('\uFB8F', '\uFB90', '\uFB91', '\uFB92'), // ک
        ['\u06AF'] = Dual('\uFB9E', '\uFB9F', '\uFBA0', '\uFBA1'), // گ
        ['\u06CC'] = Dual('\uFBFC', '\uFBFD', '\uFBFE', '\uFBFF'), // ی
    };
}

/// <summary>Minimal TrueType reader: just enough to embed the font in a PDF
/// (glyph ids from cmap, advances from hmtx, metrics from head/hhea).</summary>
public sealed class TtfFont
{
    public int UnitsPerEm { get; private set; }
    public int Ascender { get; private set; }
    public int Descender { get; private set; }
    public int XMin { get; private set; }
    public int YMin { get; private set; }
    public int XMax { get; private set; }
    public int YMax { get; private set; }
    public int GlyphCount { get; private set; }
    public byte[] Raw { get; private set; } = [];
    private readonly Dictionary<int, int> _glyphToCodepoint = [];
    private readonly Dictionary<int, int> _codepointToGlyph = [];
    private readonly int[] _advances = [];

    public static TtfFont LoadVazirmatn()
    {
        var assembly = typeof(TtfFont).Assembly;
        using var stream = assembly.GetManifestResourceStream("SeoLoodoi.Vazirmatn-Regular.ttf")
            ?? throw new InvalidOperationException("Embedded Vazirmatn font resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray());
    }

    public static TtfFont Parse(byte[] data)
    {
        var font = new TtfFont { Raw = data };
        var r = new BigEndianReader(data);
        r.ReadUInt32(); // sfnt version
        var tableCount = r.ReadUInt16();
        var tables = new Dictionary<string, (int Offset, int Length)>();
        for (var i = 0; i < tableCount; i++)
        {
            var tag = r.ReadTag();
            r.ReadUInt32(); // checksum
            var offset = (int)r.ReadUInt32();
            var length = (int)r.ReadUInt32();
            tables[tag] = (offset, length);
        }

        var head = new BigEndianReader(data, tables["head"].Offset);
        head.Skip(18);
        font.UnitsPerEm = head.ReadUInt16();
        head.Skip(16); // created + modified timestamps
        font.XMin = head.ReadInt16(); font.YMin = head.ReadInt16();
        font.XMax = head.ReadInt16(); font.YMax = head.ReadInt16();

        var hhea = new BigEndianReader(data, tables["hhea"].Offset);
        hhea.Skip(4); // 32-bit version
        font.Ascender = hhea.ReadInt16();
        font.Descender = hhea.ReadInt16();
        hhea.Skip(26); // remainder of hhea up to numberOfHMetrics
        var hMetrics = hhea.ReadUInt16();

        var maxp = new BigEndianReader(data, tables["maxp"].Offset);
        maxp.Skip(4);
        font.GlyphCount = maxp.ReadUInt16();

        var hmtx = new BigEndianReader(data, tables["hmtx"].Offset);
        font._advances = new int[font.GlyphCount];
        var lastAdvance = 0;
        for (var i = 0; i < hMetrics && i < font.GlyphCount; i++)
        {
            lastAdvance = hmtx.ReadUInt16();
            hmtx.Skip(2); // lsb
            font._advances[i] = lastAdvance;
        }
        for (var i = hMetrics; i < font.GlyphCount; i++) font._advances[i] = lastAdvance;

        font.ParseCmap(data, tables["cmap"].Offset);
        return font;
    }

    private void ParseCmap(byte[] data, int tableOffset)
    {
        var r = new BigEndianReader(data, tableOffset);
        r.Skip(2); // version
        var subtables = r.ReadUInt16();
        var chosen = -1;
        var bestPriority = -1;
        for (var i = 0; i < subtables; i++)
        {
            var platform = r.ReadUInt16();
            var encoding = r.ReadUInt16();
            var offset = (int)r.ReadUInt32();
            // Prefer Windows Unicode BMP/full repertoires over legacy encodings.
            var priority = platform == 3 && encoding == 10 ? 3 : platform == 3 && encoding == 1 ? 2 : platform == 0 ? 1 : 0;
            if (priority > bestPriority) { bestPriority = priority; chosen = offset; }
        }
        if (chosen < 0) return;
        var sub = new BigEndianReader(data, tableOffset + chosen);
        var format = sub.ReadUInt16();
        if (format == 4) ParseCmapFormat4(data, tableOffset + chosen);
        else if (format == 12) ParseCmapFormat12(data, tableOffset + chosen);
    }

    private void ParseCmapFormat4(byte[] data, int offset)
    {
        var r = new BigEndianReader(data, offset);
        r.Skip(2); // format again
        r.Skip(2); // length
        r.Skip(2); // language
        var segCountX2 = r.ReadUInt16();
        var segCount = segCountX2 / 2;
        r.Skip(6); // search params
        var endCodes = new int[segCount];
        for (var i = 0; i < segCount; i++) endCodes[i] = r.ReadUInt16();
        r.Skip(2); // reservedPad
        var startCodes = new int[segCount];
        for (var i = 0; i < segCount; i++) startCodes[i] = r.ReadUInt16();
        var idDeltas = new int[segCount];
        for (var i = 0; i < segCount; i++) idDeltas[i] = (short)r.ReadUInt16();
        var idRangeOffsetPosition = r.Position;
        var idRangeOffsets = new int[segCount];
        for (var i = 0; i < segCount; i++) idRangeOffsets[i] = r.ReadUInt16();

        for (var seg = 0; seg < segCount; seg++)
        {
            if (startCodes[seg] == 0xFFFF) break;
            for (var code = startCodes[seg]; code <= endCodes[seg]; code++)
            {
                int glyph;
                if (idRangeOffsets[seg] == 0)
                {
                    glyph = (code + idDeltas[seg]) & 0xFFFF;
                }
                else
                {
                    var glyphIndexAddress = idRangeOffsetPosition + seg * 2 + idRangeOffsets[seg] + (code - startCodes[seg]) * 2;
                    glyph = new BigEndianReader(data, 0).PeekUInt16At(glyphIndexAddress);
                    if (glyph != 0) glyph = (glyph + idDeltas[seg]) & 0xFFFF;
                }
                Map(code, glyph);
            }
        }
    }

    private void ParseCmapFormat12(byte[] data, int offset)
    {
        var r = new BigEndianReader(data, offset);
        r.Skip(2); // reserved half of the u32 format
        r.Skip(4); // length
        r.Skip(4); // language
        var groups = (int)r.ReadUInt32();
        for (var i = 0; i < groups; i++)
        {
            var startChar = (int)r.ReadUInt32();
            var endChar = (int)r.ReadUInt32();
            var startGlyph = (int)r.ReadUInt32();
            for (var code = startChar; code <= endChar; code++) Map(code, startGlyph + (code - startChar));
        }
    }

    private void Map(int codepoint, int glyph)
    {
        if (glyph <= 0 || glyph >= GlyphCount) return;
        _codepointToGlyph.TryAdd(codepoint, glyph);
        _glyphToCodepoint.TryAdd(glyph, codepoint);
    }

    public int GlyphFor(int codepoint) => _codepointToGlyph.TryGetValue(codepoint, out var glyph) ? glyph : 0;
    public int CodepointFor(int glyph) => _glyphToCodepoint.TryGetValue(glyph, out var code) ? code : 0;
    public int Advance(int glyph) => glyph >= 0 && glyph < _advances.Length ? _advances[glyph] : 0;
    public int AdvanceScaled(int glyph) => (int)Math.Round(Advance(glyph) * 1000.0 / UnitsPerEm);

    private sealed class BigEndianReader(byte[] data, int start = 0)
    {
        public int Position { get; private set; } = start;
        public void Skip(int count) => Position += count;
        public ushort ReadUInt16() { var v = (ushort)((data[Position] << 8) | data[Position + 1]); Position += 2; return v; }
        public short ReadInt16() => (short)ReadUInt16();
        public uint ReadUInt32() { var v = (uint)((data[Position] << 24) | (data[Position + 1] << 16) | (data[Position + 2] << 8) | data[Position + 3]); Position += 4; return v; }
        public string ReadTag() { var s = Encoding.ASCII.GetString(data, Position, 4); Position += 4; return s; }
        public ushort PeekUInt16At(int position) => (ushort)((data[position] << 8) | data[position + 1]);
    }
}

/// <summary>Builds the PDF object bodies for a CIDFontType2 embedding
/// (Identity-H) of a TrueType font, restricted to the glyphs actually used.
/// The caller owns object numbering and passes cross-references.</summary>
public sealed class PdfCidFont(TtfFont font, IReadOnlyCollection<int> usedGlyphs)
{
    public string FontName => "Vazirmatn-Regular";

    public string Type0(string descendantRef, string toUnicodeRef) =>
        $"<< /Type /Font /Subtype /Type0 /BaseFont /{FontName} /Encoding /Identity-H /DescendantFonts [{descendantRef}] /ToUnicode {toUnicodeRef} >>";

    public string CidFont(string descriptorRef)
    {
        var widths = new StringBuilder();
        foreach (var glyph in usedGlyphs.OrderBy(x => x)) widths.Append($"{glyph} [{font.AdvanceScaled(glyph)}] ");
        return $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{FontName} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptorRef} /DW 1000 /W [{widths}] /CIDToGIDMap /Identity >>";
    }

    public string Descriptor(string fontFileRef)
    {
        var scale = 1000.0 / font.UnitsPerEm;
        return $"<< /Type /FontDescriptor /FontName /{FontName} /Flags 4 /FontBBox [{S(font.XMin * scale)} {S(font.YMin * scale)} {S(font.XMax * scale)} {S(font.YMax * scale)}] /ItalicAngle 0 /Ascent {S(font.Ascender * scale)} /Descent {S(font.Descender * scale)} /CapHeight {S(font.Ascender * scale)} /StemV 80 /FontFile2 {fontFileRef} >>";
    }

    private static string S(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private string BuildToUnicode()
    {
        var entries = usedGlyphs.Where(g => font.CodepointFor(g) != 0).OrderBy(g => g)
            .Select(g => $"<{g:X4}> <{font.CodepointFor(g):X4}>").ToArray();
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (var block = 0; block < entries.Length; block += 100)
        {
            var chunk = entries.Skip(block).Take(100).ToArray();
            sb.Append($"{chunk.Length} beginbfchar\n");
            foreach (var entry in chunk) sb.Append(entry).Append('\n');
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend");
        return sb.ToString();
    }

    public string ToUnicode() => BuildToUnicode();

    public static byte[] CompressFontFile(TtfFont font)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(font.Raw);
        return compressed.ToArray();
    }
}
