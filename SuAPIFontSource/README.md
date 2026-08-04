# SuAPI Font Source

`chinese32.png` and `chinese32data.txt` are source inputs for
`generate_suapi_fonts.py`. They are kept in the Mod repository so the main
game repository does not track the large source atlas.

Run the generator from this directory. It builds the embedded
`EntitySystem/SuAPICore/Resources/Fonts/SuAPIChinese32.png` atlas and metadata
used by `SuAPIFonts`. These source files are not shipped in the game package.
