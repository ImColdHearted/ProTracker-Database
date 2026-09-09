HOW TO ADD A CUSTOM FONT
========================

1. Drop the .ttf/.otf file(s) directly in this folder (Assets/Fonts/).
   Multiple files for one font family (Regular, Bold, Italic, etc.) can all
   go here together - Avalonia treats this whole folder as one font
   collection and picks the right file based on the weight/style requested.

2. Find the font's ACTUAL internal name (often different from the filename -
   e.g. a file called "Montserrat-Regular.ttf" might internally be named
   just "Montserrat"). Easiest way: double-click the .ttf/.otf file in
   Windows/File Explorer - the preview window's title bar shows the real
   font family name.

3. Open Services/ThemeManager.cs and add ONE line to the CustomFontCatalog
   dictionary near the top of the file:

       ["Whatever You Want It Called In The Dropdown"] = "TheFontsActualInternalName",

4. Rebuild. The new font shows up in Appearance > Font automatically - no
   other changes needed anywhere else in the app.

That's the whole process, every time - only steps 1 and 3 change per font.
