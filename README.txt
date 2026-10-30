Simple PDF Narrator - reads PDF books and guides aloud, highlighting what it reads
==================================================================================

Install:  double-click SimplePDFNarrator-Setup.msi  (just for you, no administrator prompt)
          Adds Start menu and desktop shortcuts and "Open with > Simple PDF Narrator" for PDF files.
          Remove it from Settings > Apps. A newer setup installs over the old one.
          Works on other Windows 10/11 (64-bit) PCs too: copy the .msi over and run it.
Run without installing:  PDFReader.exe  (single file)

Build:      double-click build.cmd           -> PDFReader.exe      (details in build.log)
Installer:  double-click make-installer.cmd  -> SimplePDFNarrator-Setup.msi (details in installer.log)
            Both use the .NET 8 SDK in .sdk (installed there automatically, no admin).

Reading screen
  Top bar      Library (back to your books) - book and chapter - Page | Text - panel buttons:
               Contents, Search, Bookmarks, Reading options, Appearance
  Player       previous / play-pause / next paragraph, stop, Read from here,
               voice and speed button, page box (Ctrl+G) and progress
  Page view    the real PDF pages; the word (Windows voices) or sentence (natural voices) is highlighted
  Text view    clean text with headings and pictures

Shortcuts
  Space play/pause - Esc stop - Ctrl+Left/Right paragraph - Ctrl+O open - Ctrl+L library
  Ctrl+F search (F3 next) - Ctrl+D bookmark - Ctrl+G go to page - Ctrl+wheel / Ctrl+/- zoom - Ctrl+0 fit width
  Click a word: start there / place a cursor. Double-click: read from it. Right-click: bookmark, note, copy.
  Media keys and the Windows media panel work too, and reading continues when minimised.

Voices
  Windows voices are built in. Natural voices (Piper) are free downloads: player > voice button > Download more voices.

Your data (kept when you reinstall or uninstall; the folders keep the app's old name)
  %AppData%\PDF Reader        settings, library, bookmarks and notes
  %LocalAppData%\PDF Reader   page cache, covers, natural voices

Not yet: scanned (image-only) PDFs need OCR.

Source layout (source\)
  src\PdfReader.Core         text clean-up, layout, reading plan, pronunciation, search, library, bookmarks (unit tested)
  src\PdfReader.Core.Tests   `dotnet run` test runner
  src\PdfReader.App          WPF app: views, voices (OneCore, SAPI, Piper), media controls, themes
  installer\                 WiX MSI (Package.wxs)
