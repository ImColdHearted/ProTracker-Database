# Shared Pokémon Library

This starter library maps the supplied numbered sprite files to Pokémon and form records.

## Included data

- `Data/pokemon-library.json` — all mapped default species and alternate forms
- `Data/pokemon-species.json` — default species only
- `Data/pokemon-forms.json` — alternate forms only
- `Data/sprite-map.json` — compact lookup by sprite/Pokémon ID
- `Data/pokemon-library.csv` — spreadsheet-friendly review copy
- `CSharp/PokemonLibraryEntry.cs` — reusable model
- `CSharp/PokemonLibraryLoader.cs` — JSON loader and exact-name lookup
- `Reports/mapping-report.json` — missing and unmatched-file report

## Counts

- Mapped entries: 1340
- Default species with sprites: 1025
- Alternate forms with sprites: 315
- Special nonnumeric sprites: 191

## Recommended project layout

```text
YourApplication/
  Data/
    pokemon-library.json
  Assets/
    Sprites/
      1.png
      25.png
      10001.png
      ...
```

Set `pokemon-library.json` to **Copy if newer** in Visual Studio. Do the same for the sprite directory, or copy the sprite directory into the output folder during publishing.

The numeric sprite filename is preserved. No manual renaming is needed.

## Notes

The initial names and form mapping use the PokeAPI CSV dataset. The supplied sprites remain separate and are not duplicated inside this package.
