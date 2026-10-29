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
File.WriteAllText(tmp, "{ not json");
Eq("corrupt file falls back to defaults", 1.0, AppSettings.Load(tmp).Rate);
File.Delete(tmp);

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
