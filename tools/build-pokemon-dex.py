#!/usr/bin/env python3
"""
§413. Builds SharedPokemonLibrary/Data/Pokemon/pokemon-dex.json - abilities
and base stats for every Pokémon and form the sprite library knows - and
adds the abilities abilities.json lacks, from PokeAPI's CSV data.

Get the CSVs (six files) from
  https://github.com/PokeAPI/pokeapi/tree/master/data/v2/csv
into tools/pokeapi/ (tools/get-pokeapi-csv.ps1 does it), then run from the
repository root:

  python tools/build-pokemon-dex.py

Options: --csv DIR (default tools/pokeapi), --generation N (default 7 -
PRO's rules; an ability a later game changed is given as it was then),
--root DIR (the repository, default the current directory).

The library's pokemonId IS PokeAPI's pokemon id (pokemon-species.json and
pokemon-forms.json were built from PokeAPI), so the join is on that id -
no name matching. Existing abilities.json entries are never changed; an
ability it lacks is appended with PokeAPI's id (the national ability
number, which the file's ids already follow), an Essentials-style key and
the in-game description.
"""
import argparse
import csv
import json
import os
import re
import sys

ENGLISH = 9

# PokeAPI version groups by generation's last games - the description a
# generation-N player saw is the newest one at or before that generation.
LAST_VERSION_GROUP = {3: 7, 4: 10, 5: 14, 6: 16, 7: 18, 8: 24, 9: 27}

STAT_KEYS = {1: "hp", 2: "attack", 3: "defense", 4: "spAttack", 5: "spDefense", 6: "speed"}

NEEDED = [
    "abilities.csv",
    "ability_names.csv",
    "ability_flavor_text.csv",
    "pokemon_abilities.csv",
    "pokemon_abilities_past.csv",
    "pokemon_stats.csv",
]


def rows(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        yield from csv.DictReader(f)


def clean(text):
    # Game text: line and page breaks, and a soft hyphen at a break.
    text = text.replace("­\n", "").replace("­", "")
    text = re.sub(r"[\f\n\r]+", " ", text)
    return re.sub(r"\s{2,}", " ", text).strip()


def essentials_key(identifier):
    return re.sub(r"[^A-Z0-9]", "", identifier.upper())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=".")
    ap.add_argument("--csv", default=None)
    ap.add_argument("--generation", type=int, default=7)
    args = ap.parse_args()

    root = args.root
    csv_dir = args.csv or os.path.join(root, "tools", "pokeapi")
    gen = args.generation
    last_vg = LAST_VERSION_GROUP.get(gen, 18)

    missing = [n for n in NEEDED if not os.path.exists(os.path.join(csv_dir, n))]
    if missing:
        sys.exit(f"Missing from {csv_dir}: {', '.join(missing)}")

    data = os.path.join(root, "SharedPokemonLibrary", "Data")
    species_path = os.path.join(data, "Pokemon", "pokemon-species.json")
    forms_path = os.path.join(data, "Pokemon", "pokemon-forms.json")
    abilities_path = os.path.join(data, "Abilities", "abilities.json")
    dex_path = os.path.join(data, "Pokemon", "pokemon-dex.json")

    # ------------------------------------------------------------ abilities
    ability_ident = {}
    ability_main = {}
    for r in rows(os.path.join(csv_dir, "abilities.csv")):
        aid = int(r["id"])
        ability_ident[aid] = r["identifier"]
        ability_main[aid] = r.get("is_main_series", "1") == "1"

    ability_name = {}
    for r in rows(os.path.join(csv_dir, "ability_names.csv")):
        if int(r["local_language_id"]) == ENGLISH:
            ability_name[int(r["ability_id"])] = r["name"].strip()

    # The newest English description at or before the generation's last
    # games; failing that (an ability from a later game), its first.
    best = {}
    for r in rows(os.path.join(csv_dir, "ability_flavor_text.csv")):
        if int(r["language_id"]) != ENGLISH:
            continue
        aid = int(r["ability_id"])
        vg = int(r["version_group_id"])
        rank = (0, vg) if vg <= last_vg else (-1, -vg)
        if aid not in best or rank > best[aid][0]:
            best[aid] = (rank, clean(r["flavor_text"]))
    ability_text = {aid: t for aid, (_, t) in best.items()}

    def name_of(aid):
        return ability_name.get(aid) or ability_ident.get(aid, str(aid)).replace("-", " ").title()

    # ------------------------------------------------------------ pokemon
    slots = {}  # pokemon id -> {slot: (ability id, hidden)}
    for r in rows(os.path.join(csv_dir, "pokemon_abilities.csv")):
        pid = int(r["pokemon_id"])
        slots.setdefault(pid, {})[int(r["slot"])] = (int(r["ability_id"]), r["is_hidden"] == "1")

    # Past sets: generation_id is the last generation a set applied in.
    # For generation N the set that applies is the one with the smallest
    # generation_id at or above N; its slots override (blank = no ability).
    past = {}  # pokemon id -> {generation: {slot: (ability id or None, hidden)}}
    for r in rows(os.path.join(csv_dir, "pokemon_abilities_past.csv")):
        pid = int(r["pokemon_id"])
        g = int(r["generation_id"])
        aid = int(r["ability_id"]) if r["ability_id"].strip() else None
        past.setdefault(pid, {}).setdefault(g, {})[int(r["slot"])] = (aid, r["is_hidden"] == "1")

    changed = 0
    for pid, by_gen in past.items():
        applicable = sorted(g for g in by_gen if g >= gen)
        if not applicable:
            continue
        for slot, (aid, hidden) in by_gen[applicable[0]].items():
            current = slots.setdefault(pid, {})
            if aid is None:
                current.pop(slot, None)
            else:
                current[slot] = (aid, hidden)
        changed += 1

    stats = {}
    for r in rows(os.path.join(csv_dir, "pokemon_stats.csv")):
        key = STAT_KEYS.get(int(r["stat_id"]))
        if key:
            stats.setdefault(int(r["pokemon_id"]), {})[key] = int(r["base_stat"])

    # ------------------------------------------------------------ library
    library = []
    for path in (species_path, forms_path):
        with open(path, encoding="utf-8-sig") as f:
            library.extend(json.load(f))

    entries = []
    used = set()
    no_abilities = []
    no_stats = []
    for e in library:
        pid = int(e["pokemonId"])
        name = e["name"]
        abilities = []
        seen = set()
        for slot in sorted(slots.get(pid, {})):
            aid, hidden = slots[pid][slot]
            n = name_of(aid)
            if n.lower() in seen:
                continue
            seen.add(n.lower())
            used.add(aid)
            abilities.append({"name": n, "hidden": hidden})
        # Hidden last, the order the games list them.
        abilities.sort(key=lambda a: a["hidden"])

        s = stats.get(pid)
        entry = {"id": pid, "name": name}
        aliases = [a for a in e.get("ocrAliases", []) if a and a.lower() != name.lower()]
        if aliases:
            entry["aliases"] = aliases
        entry["abilities"] = abilities
        if s and len(s) == 6:
            entry["stats"] = {k: s[k] for k in ("hp", "attack", "defense", "spAttack", "spDefense", "speed")}
        else:
            no_stats.append(name)
        if not abilities:
            no_abilities.append(name)
        entries.append(entry)

    entries.sort(key=lambda x: x["id"])

    out = {
        "source": "PokeAPI (github.com/PokeAPI/pokeapi, data/v2/csv), joined on pokemonId - built by tools/build-pokemon-dex.py (§413)",
        "generation": gen,
        "pokemon": entries,
    }

    lines = ["{"]
    lines.append(f'  "source": {json.dumps(out["source"], ensure_ascii=False)},')
    lines.append(f'  "generation": {gen},')
    lines.append('  "pokemon": [')
    for i, entry in enumerate(entries):
        comma = "," if i < len(entries) - 1 else ""
        lines.append("    " + json.dumps(entry, ensure_ascii=False, separators=(", ", ": ")) + comma)
    lines.append("  ]")
    lines.append("}")
    with open(dex_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")

    # ------------------------------------------------------------ abilities.json
    with open(abilities_path, encoding="utf-8-sig") as f:
        existing = json.load(f)
    have = {a["name"].lower() for a in existing}
    have_ids = {a["id"] for a in existing}

    added = []
    for aid in sorted(ability_ident):
        if not ability_main.get(aid, False) or aid >= 10000:
            continue
        n = name_of(aid)
        if n.lower() in have or aid in have_ids:
            continue
        added.append({
            "id": aid,
            "key": essentials_key(ability_ident[aid]),
            "name": n,
            "description": ability_text.get(aid, ""),
        })

    if added:
        combined = existing + added
        with open(abilities_path, "w", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(combined, ensure_ascii=False, indent=2) + "\n")

    # ------------------------------------------------------------ report
    print(f"pokemon-dex.json: {len(entries)} Pokémon and forms, generation {gen} abilities ({changed} changed back from later games).")
    print(f"  without abilities: {len(no_abilities)}" + (f" - {', '.join(no_abilities[:12])}" if no_abilities else ""))
    print(f"  without base stats: {len(no_stats)}" + (f" - {', '.join(no_stats[:12])}" if no_stats else ""))
    missing_text = [name_of(a) for a in used if not ability_text.get(a) and name_of(a).lower() not in have]
    print(f"abilities.json: {len(existing)} kept, {len(added)} added" + (f"; no description for {', '.join(missing_text[:8])}" if missing_text else "") + ".")


if __name__ == "__main__":
    main()
