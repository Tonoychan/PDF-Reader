PDF Reader - reads PDFs aloud with Windows voices
==================================================

Run:    PDFReader.exe   (single file, nothing to install)
Build:  double-click build.cmd  (uses the .NET 8 SDK in .sdk; output goes to PDFReader.exe; details in build.log)

Views (top bar)
  Page   the real PDF pages (pictures, headings, layout), the spoken word highlighted on the page
  Text   clean text with headings and pictures inline, adjustable text size

Using it
  Open PDF / drag a PDF onto the window   load a book
  Play / Pause  (Space)                    start, pause, resume
  Stop  (Esc)                              stop and go back to the start of the paragraph
  Prev / Next paragraph  (Ctrl+Left/Right) skip back / forward one paragraph
  Click a word                             not reading: start from that word on Play
                                           reading/paused: places a cursor (blue box);
                                           Play (when paused) or "Read from cursor" jumps there
  Double-click a word                      read from that word right away
  Ctrl + mouse wheel, Ctrl +/-             zoom pages (Page view) or text size (Text view)
  Ctrl + 0                                 fit page width
  Scrolling away while reading pauses auto-follow; "Back to reading position" returns.

Reading options (top bar) - what is NOT read aloud (all on by default)
  Page headers, footers & page numbers / Footnotes / Table of contents pages / Picture captions
  Pause briefly around headings
  Text inside pictures is never read.

It remembers voice, speed, view, zoom, text size, reading options and where you stopped in each book
(%AppData%\PDF Reader\settings.json).

Not yet: scanned (image-only) PDFs need OCR.

Source layout (source\src)
  PdfReader.Core         text clean-up, word positions, block kinds (heading/footnote/...), reading plan, settings (unit tested)
  PdfReader.Core.Tests   `dotnet run` test runner
  PdfReader.App          WPF app: PdfPig text extraction, Windows PDF page rendering, OneCore + SAPI voices, both views
