using PdfReader.Core;

int passed = 0, failed = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine($"  ok   {name}"); }
    else { failed++; Console.WriteLine($"  FAIL {name}{(detail is null ? "" : " — " + detail)}"); }
}

void Eq<T>(string name, T expected, T actual) =>
    Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"expected «{expected}», got «{actual}»");

Console.WriteLine("TextTools");
Eq("joins lines with a space", "Hello world again", TextTools.JoinLines(new[] { "Hello world", "again" }));
Eq("rejoins hyphenated word", "an example here", TextTools.JoinLines(new[] { "an exam-", "ple here" }));
Eq("keeps hyphen before capital", "Jean- Paul", TextTools.JoinLines(new[] { "Jean-", "Paul" }));
Eq("fixes ligatures", "first office", TextTools.JoinLines(new[] { "\uFB01rst o\uFB03ce" }));
Eq("drops soft hyphens", "reading", TextTools.Normalize("read\u00ADing"));
Eq("collapses whitespace", "a b c", TextTools.CollapseWhitespace("  a \t b\n\nc  "));
Eq("skips empty lines", "a b", TextTools.JoinLines(new[] { "a", "   ", "", "b" }));

var words = TextTools.SplitWords("\"Hello,\" she said.");
Eq("word count", 3, words.Count);
Eq("first word span", new WordSpan(0, 8), words[0]);
Eq("last word span", new WordSpan(13, 5), words[2]);

Check("noise: page number", TextTools.IsNoise("12"));
Check("noise: dashed page number", TextTools.IsNoise("- 12 -"));
Check("noise: Page 4 of 10", TextTools.IsNoise("Page 4 of 10"));
Check("noise: 4 / 10", TextTools.IsNoise("4 / 10"));
Check("noise: roman numeral", TextTools.IsNoise("xiv"));
Check("noise: symbols", TextTools.IsNoise("• • •"));
Check("not noise: sentence", !TextTools.IsNoise("Chapter 1"));
Check("not noise: word 'mix'", !TextTools.IsNoise("Mix it well."));
Check("not noise: a year in text", !TextTools.IsNoise("In 1999 it began."));

Eq("glues space before period", "the player experience.", TextTools.JoinLines(new[] { "the player experience ." }));
Eq("glues space inside a PDF word", "6 of 80 | unity.com", string.Join(" ", TokenCleaner.CleanLine(new[] { "6","of","80","|","unity .com" }.Select(t => new TextToken(t)).ToList(), out _).Select(t => t.Text)));
Eq("glues punctuation from next line", "power consumption.", TextTools.JoinLines(new[] { "power consumption", "." }));
Eq("glues brackets and quotes", "a (small) “test”, yes!", TextTools.JoinLines(new[] { "a ( small ) “ test ” , yes !" }));
Eq("glues apostrophe s", "Unity’s team", TextTools.JoinLines(new[] { "Unity ’s team" }));
Eq("keeps dashes spaced", "evolve – along", TextTools.JoinLines(new[] { "evolve – along" }));
Eq("header key ignores numbers", "# of # | unity.com", TextTools.HeaderKey("6 of 80 | unity.com"));

Console.WriteLine("Paragraph");
var para = new Paragraph(0, 1, "The quick brown fox.");
Eq("word at 0", 0, para.WordIndexAt(0));
Eq("word at 5 (inside 'quick')", 1, para.WordIndexAt(5));
Eq("word at 9 (the space)", 1, para.WordIndexAt(9));
Eq("word at 10", 2, para.WordIndexAt(10));
Eq("word at end", 3, para.WordIndexAt(19));
Eq("word text", "brown", para.WordText(2));

Console.WriteLine("BookBuilder");
var builder = new BookBuilder();
builder.AddBlock(1, new[] { "Chapter One" });
builder.AddBlock(1, new[] { "1" });
builder.AddBlock(2, new[] { "It was a dark and", "stormy night." });
var book = builder.Build(Path.Combine("books", "Test Book.pdf"), 2);
Eq("page-number block kept as header/footer", ParagraphKind.HeaderFooter, book.Paragraphs[1].Kind);
Eq("paragraph indexes", 2, book.Paragraphs[2].Index);
Eq("paragraph page", 2, book.Paragraphs[2].PageNumber);
Eq("title from file name", "Test Book", book.Title);

var hb = new BookBuilder();
for (int pg = 1; pg <= 10; pg++)
{
    hb.AddBlock(pg, new[] { $"{pg} of 10 | unity .com" });
    hb.AddBlock(pg, new[] { "© 2023 Unity Technologies" });
    hb.AddBlock(pg, new[] { "Real content " + new string((char)('a' + pg), 3) + " should be read." });
}
hb.AddBlock(10, new[] { "Introduction" });
var hbook = hb.Build("h.pdf", 10);
Eq("running headers/footers marked", 20, hbook.Paragraphs.Count(p => p.Kind == ParagraphKind.HeaderFooter));
Check("content not marked", hbook.Paragraphs.Where(p => p.Kind != ParagraphKind.HeaderFooter).All(p => !p.Text.Contains("unity.com") && !p.Text.StartsWith("©")));
var small = new BookBuilder();
small.AddBlock(1, new[] { "Chapter One" }); small.AddBlock(2, new[] { "Chapter One" });
Check("2-page repeats kept (too few to be a header)", small.Build("s.pdf", 2).Paragraphs.All(p => p.Kind == ParagraphKind.Body));


Console.WriteLine("TokenCleaner (positioned words)");
TextToken T(string text, float x, float y, float w, float size = 9, bool bold = false) => new(text, new BoxF(x, y, w, size), size, bold);
TextLineInput L(params TextToken[] t) => new(t);
string Txt(CleanBlock b) => string.Join(" ", b.Words.Select(w => w.Text));

var spaced = TokenCleaner.Clean(new[] { L(T("I",45,155,10,44.6f), T("N",64,155,20,44.6f), T("T",102,155,18,44.6f), T("R",129,155,20,44.6f), T("O",160,155,22,44.6f)) });
Eq("letter-spaced heading joined", "INTRO", Txt(spaced));
var twoWords = TokenCleaner.Clean(new[] { L(T("T",0,0,8,20), T("H",12,0,8,20), T("E",24,0,8,20), T("E",60,0,8,20), T("N",72,0,8,20), T("D",84,0,8,20)) });
Eq("letter-spaced words keep the word gap", "THE END", Txt(twoWords));
var marker = TokenCleaner.Clean(new[] { L(T("the",300,604,15), T("team.",320,604,26), T("1",348,603,3,5.2f)) });
Eq("tiny footnote reference dropped", "the team.", Txt(marker));
Eq("superscript reference dropped", "the team.", TextTools.JoinLines(new[] { "the team.¹" }));
var foot = TokenCleaner.Clean(new[] { L(T("1",45,310,2,3.5f), T("Note",47,310,14,6), T("that",62,310,11,6), T("many",75,310,15,6)) });
Check("footnote leading marker detected", foot.StartsWithMarker);
Eq("footnote text without marker", "Note that many", Txt(foot));
var touch = TokenCleaner.Clean(new[] { L(T("unity",540,747,20,6), T(".com",560.2f,747,12,6)) });
Eq("touching pieces glued", "unity.com", Txt(touch));
var apart = TokenCleaner.Clean(new[] { L(T("the",0,0,12), T(".NET",16,0,20)) });
Eq("spaced .NET kept separate", "the .NET", Txt(apart));
var hy = TokenCleaner.Clean(new[] { L(T("an",0,0,10), T("exam-",14,0,30)), L(T("ple",0,12,16), T("here",20,12,20)) });
Eq("hyphen join keeps both boxes", "an example here", Txt(hy));
Check("hyphenated word has second box", !hy.Words[1].Box2.IsEmpty && hy.Words[1].Box2.Y == 12);

Console.WriteLine("Classification");
PageInput Page(int n, params List<TextLineInput>[] blocks) => new() { Number = n, Width = 612, Height = 792, Blocks = blocks.ToList() };
List<TextLineInput> Blk(params TextLineInput[] lines) => lines.ToList();
TextLineInput Words(float y, float size, string text, bool bold = false, float x = 135)
{
    var toks = new List<TextToken>();
    foreach (var w in text.Split(' ')) { toks.Add(new TextToken(w, new BoxF(x, y, w.Length * size * 0.5f, size), size, bold)); x += w.Length * size * 0.5f + size * 0.3f; }
    return new TextLineInput(toks);
}
string body1 = "Profiling is the process of measuring aspects of your game performance at runtime and it matters a lot.";
var cb = new BookBuilder();
var p6 = Page(6,
    Blk(Words(155, 44.6f, "INTRODUCTION")),
    Blk(Words(400, 9, body1), Words(413, 9, body1)),
    Blk(Words(430, 9, "Profile early, often, and on the target device")),
    Blk(Words(445, 9, body1)),
    Blk(Words(747, 6, "© 2023 Unity Technologies", x: 45)),
    Blk(Words(747, 6, "6 of 80 | unity.com", x: 514)));
p6.Images.Add(new BoxF(-2, 0, 615, 341));
p6.Images.Add(new BoxF(10, 10, 20, 20));
cb.AddPage(p6);
var p7 = Page(7,
    Blk(Words(250, 6, "Race against AI opponents shown in the screenshot above")),
    Blk(new TextLineInput(new[] { new TextToken("1", new BoxF(45,310,2,3.5f), 3.5f) }.Concat(Words(310, 6, "Note that many of the optimizations discussed here may add complexity.", x: 47).Tokens))),
    Blk(Words(400, 9, body1)),
    Blk(Words(747, 6, "7 of 80 | unity.com", x: 514)));
p7.Images.Add(new BoxF(45, 48, 523, 196));
cb.AddPage(p7);
cb.AddPage(Page(5,
    Blk(Words(60, 10, "Audio . . . . . . . . . . 65"), Words(80, 10, "Make sound clips mono . . . . . . 65"), Words(100, 10, "Use original WAV files . . . . . 67"), Words(120, 10, "Animation . . . . . . . 68"))));
var cbook = cb.Build("c.pdf", 7);
ParagraphKind KindOf(string start) => cbook.Paragraphs.First(p => p.Text.StartsWith(start)).Kind;
Eq("big banner title is a heading", ParagraphKind.Heading, KindOf("INTRODUCTION"));
Eq("short title-case line is a heading", ParagraphKind.Heading, KindOf("Profile early"));
Eq("body is body", ParagraphKind.Body, KindOf("Profiling is"));
Eq("copyright footer", ParagraphKind.HeaderFooter, KindOf("©"));
Eq("page footer", ParagraphKind.HeaderFooter, KindOf("6 of 80"));
Eq("footnote with marker", ParagraphKind.Footnote, KindOf("Note that"));
Eq("small text under a picture is a caption", ParagraphKind.Caption, KindOf("Race against"));
Eq("dot-leader page is table of contents", ParagraphKind.TableOfContents, KindOf("Audio"));
Check("words carry positions", cbook.Paragraphs.All(p => p.HasGeometry));
Eq("full-page-ish banner kept, tiny icon dropped", 1, cbook.Page(6)!.Images.Count);

var opts = new ReadingOptions();
Check("default: footnotes, captions, toc, footers skipped",
    !opts.IsSpoken(cbook.Paragraphs.First(p => p.Kind == ParagraphKind.Footnote)) &&
    !opts.IsSpoken(cbook.Paragraphs.First(p => p.Kind == ParagraphKind.Caption)) &&
    !opts.IsSpoken(cbook.Paragraphs.First(p => p.Kind == ParagraphKind.TableOfContents)) &&
    !opts.IsSpoken(cbook.Paragraphs.First(p => p.Kind == ParagraphKind.HeaderFooter)));
opts.SkipCaptions = false;
Check("captions can be turned on", opts.IsSpoken(cbook.Paragraphs.First(p => p.Kind == ParagraphKind.Caption)));

var reflow = cbook.ReflowEntries();
Check("text view has no headers/footers", reflow.OfType<ReflowParagraph>().All(r => r.Paragraph.Kind != ParagraphKind.HeaderFooter));
int imgIdx = reflow.ToList().FindIndex(e => e is ReflowImage { PageNumber: 7 });
int raceIdx = reflow.ToList().FindIndex(e => e is ReflowParagraph rp && rp.Paragraph.Text.StartsWith("Race"));
Check("picture placed before the text below it", imgIdx >= 0 && imgIdx < raceIdx, $"{imgIdx} vs {raceIdx}");

var merged = BookBuilder.MergeImages(new[] { new BoxF(0, 0, 100, 100), new BoxF(100, 0, 100, 100), new BoxF(300, 300, 50, 50) }, 612, 792);
Eq("touching image tiles merged", 2, merged.Count);


Console.WriteLine("Outline");
{
    var ob = new BookBuilder();
    PageInput Pg(int n, params List<TextLineInput>[] blocks) => new() { Number = n, Width = 612, Height = 792, Blocks = blocks.ToList() };
    TextLineInput W(float y, float size, string text, float x = 135)
    {
        var toks = new List<TextToken>();
        foreach (var w in text.Split(' ')) { toks.Add(new TextToken(w, new BoxF(x, y, w.Length * size * 0.5f, size), size)); x += w.Length * size * 0.5f + size * 0.3f; }
        return new TextLineInput(toks);
    }
    string body = "Profiling is the process of measuring aspects of your game performance at runtime and it matters.";
    ob.AddPage(Pg(1, new() { W(155, 40, "PROFILING") }, new() { W(300, 9, body), W(313, 9, body) }));
    ob.AddPage(Pg(2, new() { W(60, 14, "Use the Profiler") }, new() { W(100, 9, body), W(113, 9, body) }));
    ob.AddPage(Pg(3, new() { W(60, 14, "Memory") }, new() { W(100, 9, body), W(113, 9, body) }));
    ob.AddPage(Pg(4, new() { W(155, 40, "ADAPTIVE") }, new() { W(205, 40, "PERFORMANCE") }, new() { W(300, 9, body), W(313, 9, body) }));
    var b1 = ob.Build("o.pdf", 4);
    Eq("detected outline has 4 entries", 4, b1.Outline.Count);
    Eq("run-on banner joined", "Adaptive performance", b1.Outline[3].Title);
    Eq("all-caps banner tidied", "Profiling", b1.Outline[0].Title);
    Eq("big heading is level 0", 0, b1.Outline[0].Level);
    Eq("smaller heading is level 1", 1, b1.Outline[1].Level);
    Eq("entry links to its paragraph", "Use the Profiler", b1.Paragraphs[b1.Outline[1].ParagraphIndex].Text);
    Eq("entry for a body paragraph", 1, OutlineBuilder.EntryFor(b1.Outline, b1.Outline[1].ParagraphIndex + 1));
    Eq("entry before the first heading", -1, OutlineBuilder.EntryFor(b1.Outline, -5));

    ob.SetPdfOutline(new[] { new RawOutlineItem("Profiling", 0, 1), new RawOutlineItem("Use the profiler", 1, 2), new RawOutlineItem("Memory", 1, 3), new RawOutlineItem("Missing title", 1, 3) });
    var b2 = ob.Build("o.pdf", 4);
    Eq("pdf outline used", 4, b2.Outline.Count);
    Eq("pdf outline matched by title (case-insensitive)", "Use the Profiler", b2.Paragraphs[b2.Outline[1].ParagraphIndex].Text);
    Eq("unmatched title falls back to page's first paragraph", "Memory", b2.Paragraphs[b2.Outline[3].ParagraphIndex].Text);
    Eq("first paragraph on page 2", b2.Outline[1].ParagraphIndex, b2.FirstParagraphOnPage(2));
    Eq("first paragraph past the end", -1, b2.FirstParagraphOnPage(9));

    Console.WriteLine("Search");
    var hits = BookSearch.Find(b2, "PROFILER");
    Eq("search is case-insensitive", 1, hits.Count);
    Eq("hit word index", 2, hits[0].WordIndex);
    Eq("hit match text", "Profiler", hits[0].Match);
    var many = BookSearch.Find(b2, "game performance");
    Eq("phrase across words, all occurrences", 8, many.Count);
    Check("snippet has context", many[0].Before.EndsWith("your ") && many[0].After.StartsWith(" at runtime"));
    Eq("cafe matches café", 1, BookSearch.Find(new BookDocument("x.pdf", 1, new[] { new Paragraph(0, 1, "A café nearby.") }), "cafe").Count);
    Eq("too-short query ignored", 0, BookSearch.Find(b2, "a").Count);
    Eq("max hits respected", 2, BookSearch.Find(b2, "game", maxHits: 2).Count);

    Console.WriteLine("BookCache");
    var ms = new MemoryStream();
    BookCache.Write(ms, b2);
    ms.Position = 0;
    var back = BookCache.Read(ms, "o.pdf")!;
    Eq("cache paragraphs", b2.Paragraphs.Count, back.Paragraphs.Count);
    Eq("cache text", b2.Paragraphs[3].Text, back.Paragraphs[3].Text);
    Eq("cache kind", b2.Paragraphs[0].Kind, back.Paragraphs[0].Kind);
    Eq("cache geometry", b2.Paragraphs[3].Geometry[2], back.Paragraphs[3].Geometry[2]);
    Eq("cache outline", b2.Outline[1], back.Outline[1]);
    Eq("cache pages", b2.Pages[1].Height, back.Pages[1].Height);
    Check("cache file is compact", ms.Length < 4000, $"{ms.Length} bytes");
    var junk = new MemoryStream(new byte[] { 1, 2, 3 });
    bool threw = false; try { BookCache.Read(junk, "o.pdf"); } catch { threw = true; }
    Check("junk cache rejected", threw);
    string dir = Path.Combine(Path.GetTempPath(), "pdfreader-cache-" + Guid.NewGuid());
    string fake = Path.Combine(dir, "book.pdf"); Directory.CreateDirectory(dir); File.WriteAllText(fake, "x");
    var real = new BookDocument(fake, b2.PageCount, b2.Paragraphs, b2.Pages, b2.Outline);
    BookCache.TrySave(dir, real);
    Eq("cache round-trip via folder", real.Paragraphs.Count, BookCache.TryLoad(dir, fake)?.Paragraphs.Count);
    File.WriteAllText(fake, "changed!");
    Eq("edited file invalidates cache", (int?)null, BookCache.TryLoad(dir, fake)?.Paragraphs.Count);
    Directory.Delete(dir, true);
}


Console.WriteLine("Library");
{
    var lib = new LibraryStore();
    var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    lib.Touch(@"C:\b\one.pdf", "One", 10, t0);
    lib.Touch(@"C:\b\two.pdf", "Two", 20, t0.AddHours(1));
    lib.Touch(@"C:\B\ONE.pdf", "One", 10, t0.AddHours(2));
    Eq("touch dedupes by path (case-insensitive)", 2, lib.Entries.Count);
    Eq("most recent first", "One", lib.Recent[0].Title);
    lib.UpdateProgress(@"c:\b\two.pdf", 7, 0.35);
    Eq("progress page", 7, lib.Find(@"C:\b\two.pdf")!.Page);
    lib.UpdateProgress(@"C:\b\two.pdf", 7, 3.0);
    Eq("progress clamped", 1.0, lib.Find(@"C:\b\two.pdf")!.Progress);

    lib.ApplyFolderScan(new[] { @"C:\b\two.pdf", @"C:\b\three.pdf", @"C:\b\sub\four.pdf" });
    string T3 = Path.GetFileNameWithoutExtension(@"C:\b\three.pdf"), T4 = Path.GetFileNameWithoutExtension(@"C:\b\sub\four.pdf");
    Eq("folder books listed by title", string.Join(",", new[] { T4, T3, "Two" }.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)), string.Join(",", lib.FolderBooks.Select(e => e.Title)));
    Check("opened book outside folder stays recent", lib.Recent.Any(e => e.Title == "One"));

    lib.Remove(@"C:\b\two.pdf", alsoFromFolder: false);
    Check("removed from recent", lib.Recent.All(e => e.Title != "Two"));
    Check("still in folder", lib.FolderBooks.Any(e => e.Title == "Two"));
    lib.Remove(@"C:\b\three.pdf", alsoFromFolder: true);
    lib.ApplyFolderScan(new[] { @"C:\b\two.pdf", @"C:\b\three.pdf" });
    Check("hidden book stays hidden after rescan", lib.FolderBooks.All(e => e.Title != T3));
    Check("book gone from folder disappears", lib.FolderBooks.All(e => e.Title != T4));
    lib.Touch(@"C:\b\three.pdf", "three", 5);
    Check("opening a hidden book un-hides it", lib.FolderBooks.Any(e => e.Title == "three"));
    lib.ClearFolder();
    Eq("clear folder keeps opened books only", "One,three", string.Join(",", lib.Entries.Select(e => e.Title).OrderBy(x => x)));

    var many = new LibraryStore();
    for (int i = 0; i < 50; i++) many.Touch($@"C:\m\{i}.pdf", i.ToString(), 1, t0.AddMinutes(i));
    Eq("recent list trimmed", LibraryStore.MaxRecent, many.Entries.Count);
    Eq("oldest dropped first", "10", many.Recent[^1].Title);

    string libFile = Path.Combine(Path.GetTempPath(), "pdfreader-lib-" + Guid.NewGuid() + ".json");
    lib.Save(libFile);
    Eq("library round-trips", lib.Entries.Count, LibraryStore.Load(libFile).Entries.Count);
    File.WriteAllText(libFile, "{oops");
    Eq("corrupt library -> empty", 0, LibraryStore.Load(libFile).Entries.Count);
    File.Delete(libFile);

    string dir = Path.Combine(Path.GetTempPath(), "pdfreader-scan-" + Guid.NewGuid());
    Directory.CreateDirectory(Path.Combine(dir, "sub"));
    File.WriteAllText(Path.Combine(dir, "a.pdf"), ""); File.WriteAllText(Path.Combine(dir, "sub", "b.PDF"), ""); File.WriteAllText(Path.Combine(dir, "c.txt"), "");
    Eq("folder scan finds PDFs in subfolders", 2, LibraryStore.ScanFolder(dir).Count);
    Directory.Delete(dir, true);
}

Console.WriteLine("Bookmarks");
{
    var nbb = new BookBuilder();
    nbb.AddBlock(1, new[] { "Chapter One" });
    nbb.AddBlock(1, new[] { string.Join(" ", Enumerable.Range(1, 40).Select(i => "word" + i)) });
    nbb.AddBlock(2, new[] { "Second page text here." });
    var nbook = nbb.Build("n.pdf", 2);
    var notes = new BookNotes { BookPath = "n.pdf" };
    var bm1 = notes.Add(nbook, new ReadingPosition(2, 3));
    var bm0 = notes.Add(nbook, new ReadingPosition(1, 8), "important");
    Eq("bookmarks sorted in reading order", bm0.Id, notes.Items[0].Id);
    Eq("bookmark snaps to word start", 6, bm0.Char);
    Eq("bookmark page", 2, bm1.Page);
    Check("snippet trimmed to a word", bm0.Snippet.EndsWith("…") && bm0.Snippet.Length < 95, bm0.Snippet);
    var again = notes.Add(nbook, new ReadingPosition(1, 9), "more");
    Eq("same word returns existing bookmark", bm0.Id, again.Id);
    Eq("note appended", "important\nmore", bm0.Note);
    Eq("bookmarks on page 2", 1, notes.OnPage(2).Count());
    string md = notes.ToMarkdown("Test");
    Check("markdown export", md.Contains("# Notes — Test") && md.Contains("## Page 1") && md.Contains("important"));
    string nd = Path.Combine(Path.GetTempPath(), "pdfreader-notes-" + Guid.NewGuid());
    notes.Save(nd);
    var loaded = BookNotes.Load(nd, "N.PDF");
    Eq("notes round-trip (path case-insensitive)", 2, loaded.Items.Count);
    Check("remove bookmark", loaded.Remove(bm1.Id) && loaded.Items.Count == 1);
    loaded.Remove(bm0.Id); loaded.Save(nd);
    Eq("empty notes file deleted", 0, Directory.GetFiles(nd).Length);
    Directory.Delete(nd, true);
}


Console.WriteLine("Pronunciation");
{
    var pr = new Pronouncer();
    string Say(string t) => pr.Transform(t).Text;
    Eq("e.g. with comma", "for example, Unity", Say("e.g., Unity"));
    Eq("i.e. and etc.", "that is this et cetera", Say("i.e. this etc."));
    Eq("camel case", "Use the Audio Source component", Say("Use the AudioSource component"));
    Eq("acronym then word", "GPU Instancing on", Say("GPUInstancing on"));
    Eq("trailing digits", "On Trigger Enter 2D", Say("OnTriggerEnter2D"));
    Eq("short or plain words kept", "iOS Hello macOS Unity iPhone", Say("iOS Hello macOS Unity iPhone"));
    Eq("file extension", "uncompressed dot wave files", Say("uncompressed .wav files"));
    Eq("file name", "my Texture dot P N G", Say("myTexture.png"));
    Eq("dotted API", "Debug dot Log statements", Say("Debug.Log statements"));
    Eq("method call", "Get Component dot Value", Say("GetComponent.Value()"));
    Eq("decimal kept", "costs 2.5 ms", Say("costs 2.5 ms"));
    Eq("version with v", "version 1 point 2 point 3", Say("v1.2.3"));
    Eq("three-part version", "Unity 2022 point 3 point 10", Say("Unity 2022.3.10"));
    Eq("dimensions", "1920 by 1080", Say("1920x1080"));
    Eq("C sharp", "in C sharp and C plus plus", Say("in C# and C++"));
    Eq("dot net", "dot net apps", Say(".NET apps"));
    Eq("url", "unity dot com slash learn slash tutorials", Say("https://www.unity.com/learn/tutorials?x=1"));
    Eq("email", "me at site dot com", Say("me@site.com"));
    Eq("slash between names", "CPU or GPU", Say("CPU/GPU"));
    Eq("slash in units kept", "50 km/h", Say("50 km/h"));
    Eq("snake case", "max texture size", Say("max_texture_size"));
    Eq("bullets dropped", "First item", Say("• First item"));
    Eq("arrow", "A to B", Say("A → B"));
    Eq("punctuation kept around", "(Audio Source)", Say("(AudioSource)"));
    Eq("number sign", "number 1 tip", Say("#1 tip"));
    pr.UserWords = new Dictionary<string, string> { ["LTS"] = "long term support", ["URP"] = "U R P" };
    Eq("user word list", "Unity long term support with U R P.", Say("Unity LTS with URP."));
    pr.Enabled = false;
    Eq("disabled: user words still apply", "AudioSource long term support", Say("AudioSource LTS"));
    pr.UserWords = new Dictionary<string, string>();
    Eq("disabled: unchanged", "e.g. AudioSource", Say("e.g. AudioSource"));

    var on = new Pronouncer();
    var st = on.Transform("Use e.g. the AudioSource now");
    int idxSource = st.Text.IndexOf("Source");
    Eq("map: spoken 'Source' -> original 'AudioSource'", 13, st.ToOriginal(idxSource));
    Eq("map: spoken 'example' -> original 'e.g.'", 4, st.ToOriginal(st.Text.IndexOf("example")));
    Eq("map: last word", 25, st.ToOriginal(st.Text.Length - 1));

    Console.WriteLine("Sentences");
    var ss = SentenceSplitter.Split("This is one. And two! Is it three? Yes.");
    Eq("four sentences", 4, ss.Count);
    Eq("first sentence", "This is one.", "This is one. And two! Is it three? Yes."[ss[0].Start..(ss[0].Start + ss[0].Length)]);
    Eq("abbreviation doesn't split", 1, SentenceSplitter.Split("Use tools, e.g. Profiler and so on.").Count);
    Eq("decimal doesn't split", 1, SentenceSplitter.Split("It costs 2.5 ms per frame.").Count);
    Eq("lowercase after period doesn't split", 1, SentenceSplitter.Split("Call Debug.Log. then continue").Count);
    string longSentence = string.Join(", ", Enumerable.Repeat("a fairly long clause of words here", 20)) + ".";
    var cut = SentenceSplitter.Split(longSentence);
    Check("long sentence cut", cut.Count > 1 && cut.All(c => c.Length <= SentenceSplitter.MaxSentenceLength));
    Check("cut pieces cover the text", cut[^1].Start + cut[^1].Length == longSentence.Length);

    Console.WriteLine("SpeechInput");
    var orig = "Profiling helps. Use the AudioSource, e.g. for music.";
    var input = SpeechInput.Build(orig, new Pronouncer());
    Eq("spoken text", "Profiling helps. Use the Audio Source, for example for music.", input.Text);
    Eq("two sentences", 2, input.Sentences.Count);
    Eq("sentence 2 original start", 17, input.Sentences[1].OriginalStart);
    Eq("sentence 2 spoken text", "Use the Audio Source, for example for music.", input.SentenceText(1));
    Eq("map in sentence 2", orig.IndexOf("AudioSource"), input.Spoken.ToOriginal(input.Text.IndexOf("Source,")));
    Eq("map 'example'", orig.IndexOf("e.g."), input.Spoken.ToOriginal(input.Text.IndexOf("example")));
    var plain = SpeechInput.Build("Only bullets • ", new Pronouncer());
    Check("non-empty input", plain.Text.Length > 0 && plain.Sentences.Count == 1);

    Console.WriteLine("PiperCatalog");
    string catalog = """
    {
      "en_GB-alan-medium": { "key": "en_GB-alan-medium", "name": "alan", "language": { "code": "en_GB", "name_english": "English", "country_english": "Great Britain" },
        "quality": "medium", "num_speakers": 1,
        "files": { "en/en_GB/alan/medium/en_GB-alan-medium.onnx": { "size_bytes": 63201294 }, "en/en_GB/alan/medium/en_GB-alan-medium.onnx.json": { "size_bytes": 4888 }, "en/en_GB/alan/medium/MODEL_CARD": {} } },
      "de_DE-thorsten-high": { "key": "de_DE-thorsten-high", "name": "thorsten", "language": { "code": "de_DE", "name_english": "German", "country_english": "Germany" },
        "quality": "high", "num_speakers": 1,
        "files": { "de/de_DE/thorsten/high/de_DE-thorsten-high.onnx": { "size_bytes": 114199011 }, "de/de_DE/thorsten/high/de_DE-thorsten-high.onnx.json": {} } },
      "en_US-lessac-medium": { "key": "en_US-lessac-medium", "name": "lessac", "language": { "code": "en_US", "name_english": "English", "country_english": "United States" },
        "quality": "medium", "num_speakers": 1,
        "files": { "en/en_US/lessac/medium/en_US-lessac-medium.onnx": { "size_bytes": 63201294 }, "en/en_US/lessac/medium/en_US-lessac-medium.onnx.json": {} } },
      "broken": { "name": "x" }
    }
    """;
    var voices = PiperCatalog.Parse(catalog);
    Eq("catalog voices (broken skipped)", 3, voices.Count);
    Eq("recommended first", "en_US-lessac-medium", voices[0].Key);
    Eq("language name", "English (Great Britain)", voices[1].LanguageName);
    Eq("model path", "en/en_GB/alan/medium/en_GB-alan-medium.onnx", voices[1].ModelFile);
    Eq("size label", "63 MB", voices[1].SizeLabel);
    Eq("display name", "Lessac", voices[0].DisplayName);
    Check("english flag", voices[0].IsEnglish && !voices[2].IsEnglish);
    var cfg = PiperCatalog.ReadConfig("""{ "audio": { "sample_rate": 22050 }, "language": { "code": "en_US" }, "dataset": "lessac" }""");
    Eq("config sample rate", 22050, cfg.SampleRate);
    Eq("config language", "en_US", cfg.Language);
}


Console.WriteLine("WordListText");
{
    var wl = WordListText.Parse("LTS = long term support\r\n# comment\n  URP=U R P \nbad line\ntwo words = no\n= empty\nSkip =");
    Eq("parsed entries", 3, wl.Count);
    Eq("trimmed value", "U R P", wl["urp"]);
    Eq("empty say allowed (skip word)", "", wl["Skip"]);
    var round = WordListText.Parse(WordListText.Format(wl));
    Eq("format round-trips", "long term support", round["LTS"]);
}

Console.WriteLine("ReadingPlan");
var spoken = new ReadingOptions();
var plan = new ReadingPlan(book, spoken.IsSpoken);
var c0 = plan.ChunkAt(ReadingPosition.Start);
Eq("first chunk", new SpeechChunk(0, 0, 11), c0);
var c1 = plan.After(c0!.Value);
Eq("next chunk skips the page number", new SpeechChunk(2, 0, book.Paragraphs[2].Text.Length), c1);
Eq("end of book", (SpeechChunk?)null, plan.After(c1!.Value));
Eq("mid-word start snaps to word start", new SpeechChunk(2, 9, book.Paragraphs[2].Text.Length),
    plan.ChunkAt(new ReadingPosition(2, 11)));
Eq("past paragraph end moves on", (SpeechChunk?)null, plan.ChunkAt(new ReadingPosition(2, 999)));
Eq("start on skipped paragraph moves on", 2, plan.ChunkAt(new ReadingPosition(1, 0))!.Value.ParagraphIndex);
Eq("find spoken backwards", 0, plan.FindSpoken(1, -1));
Eq("find spoken forwards", 2, plan.FindSpoken(1, +1));
Eq("past end of book", (SpeechChunk?)null, plan.ChunkAt(new ReadingPosition(5, 0)));

// Long paragraph → several chunks that cover it exactly, each ending at a sentence end.
string sentence = "This is a fairly ordinary sentence for testing purposes. ";
string longText = string.Concat(Enumerable.Repeat(sentence, 60)).Trim();
var lb = new BookBuilder();
lb.AddBlock(1, new[] { longText });
var longBook = lb.Build("long.pdf", 1);
var lp = new ReadingPlan(longBook);
var chunks = new List<SpeechChunk>();
for (var c = lp.ChunkAt(ReadingPosition.Start); c is not null; c = lp.After(c.Value)) chunks.Add(c.Value);
Check("long paragraph split", chunks.Count > 1, $"{chunks.Count} chunks");
Check("chunks contiguous", chunks.Zip(chunks.Skip(1)).All(p => p.First.End == p.Second.Start ||
    longBook.Paragraphs[0].Text[p.First.End..p.Second.Start].Trim().Length == 0));
Eq("chunks reach end", longText.Length, chunks[^1].End);
Check("chunks within max", chunks.All(c => c.Length <= ReadingPlan.DefaultMaxChunk));
Check("chunks end at sentence", chunks.Take(chunks.Count - 1).All(c => longText[c.End - 1] == '.'));
Eq("chunk text", longText[chunks[1].Start..chunks[1].End], lp.TextOf(chunks[1]));

// Paragraph with no punctuation at all still splits.
var nb = new BookBuilder();
nb.AddBlock(1, new[] { string.Join(" ", Enumerable.Repeat("word", 800)) });
var np = new ReadingPlan(nb.Build("n.pdf", 1));
var nc = np.ChunkAt(ReadingPosition.Start)!.Value;
Check("unpunctuated split at space", nc.Length <= ReadingPlan.DefaultMaxChunk && nb.Build("n.pdf", 1).Paragraphs[0].Text[nc.End - 1] == ' ');

Console.WriteLine("AppSettings");
string tmp = Path.Combine(Path.GetTempPath(), "pdfreader-test-" + Guid.NewGuid() + ".json");
var s = new AppSettings { VoiceKey = "onecore:David", Rate = 1.4 };
s.Remember(book.FilePath, new ReadingPosition(2, 9));
s.Save(tmp);
var s2 = AppSettings.Load(tmp);
Eq("voice round-trips", "onecore:David", s2.VoiceKey);
Eq("rate round-trips", 1.4, s2.Rate);
Eq("position recalled (case-insensitive path)", new ReadingPosition(2, 9), s2.Recall(book.FilePath.ToUpperInvariant(), book));
Eq("out-of-range position ignored", (ReadingPosition?)null,
    new AppSettings { Positions = new() { ["x"] = new SavedPosition { Paragraph = 50 } } }.Recall("x", book));
var th = new AppSettings { Theme = "Dark", MatchPageColors = false, Maximized = true };
th.Save(tmp);
var th2 = AppSettings.Load(tmp);
Check("theme round-trips", th2.Theme == "Dark" && !th2.MatchPageColors && th2.Maximized);
new AppSettings { Theme = "Neon" }.Save(tmp);
Eq("unknown theme falls back to Light", "Light", AppSettings.Load(tmp).Theme);
Check("page colours match theme by default", new AppSettings().MatchPageColors);
File.WriteAllText(tmp, "{ not json");
Eq("corrupt file falls back to defaults", 1.0, AppSettings.Load(tmp).Rate);
File.Delete(tmp);

Console.WriteLine("PageTinter");
Eq("none leaves pixels", ((byte)10, (byte)20, (byte)30), PageTinter.Map(PageTint.None, 10, 20, 30));
Eq("sepia: white becomes paper", PageTinter.SepiaPaper, PageTinter.Map(PageTint.Sepia, 255, 255, 255));
Eq("sepia: black becomes brown ink", PageTinter.SepiaInk, PageTinter.Map(PageTint.Sepia, 0, 0, 0));
Eq("dark: white page becomes dark", (PageTinter.DarkPaper, PageTinter.DarkPaper, PageTinter.DarkPaper), PageTinter.Map(PageTint.Dark, 255, 255, 255));
Eq("dark: black text becomes light", (PageTinter.DarkInk, PageTinter.DarkInk, PageTinter.DarkInk), PageTinter.Map(PageTint.Dark, 0, 0, 0));
var link = PageTinter.Map(PageTint.Dark, 0x1A, 0x5F, 0xD0); // a blue link
Check("dark: blue stays blue", link.B > link.R + 40 && link.B > link.G, $"{link}");
var px = new byte[] { 255, 255, 255, 255, 0, 0, 0, 128 };
PageTinter.Apply(px, PageTint.Dark);
Check("apply works on BGRA and keeps alpha", px[0] == PageTinter.DarkPaper && px[3] == 255 && px[4] == PageTinter.DarkInk && px[7] == 128);

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
