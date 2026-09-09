using System.Collections.Generic;

namespace PokemonSim.Models
{
    /// <summary>The JSON shape of one DataFiles/moves.json entry. Section 154
    /// completed it: the original carried only eight fields, so effects,
    /// stat changes, flinch and secondary chances, multi-hit counts, crit
    /// stage and the weather/terrain setters were silently dropped on load -
    /// which is why Protect, Drain, the hazards and Swords Dance never fired
    /// in the old harness even though their classes existed.</summary>
    public class MoveJson
    {
        public string type { get; set; } = "";
        public string category { get; set; } = "";
        public int power { get; set; }
        public int accuracy { get; set; }
        public int pp { get; set; }
        public int priority { get; set; }
        public double statusChance { get; set; }
        public double secondaryChance { get; set; }
        public double flinchChance { get; set; }
        public int minHits { get; set; }
        public int maxHits { get; set; }
        public int critStage { get; set; }
        public List<string>? randomStatus { get; set; }
        public StatusCondition inflictStatus { get; set; }
        public List<StatChange>? statChanges { get; set; }
        public List<string>? effects { get; set; }
        public WeatherType setWeather { get; set; }

        // Section 155: Psyshock/Psystrike/Secret Sword - a Special
        // move whose damage runs against the target's DEFENSE.
        public bool usesTargetDefense { get; set; }

        // Section 158: Body Press / Foul Play / Tera Blast stat selection.
        public bool usesDefenseAsOffense { get; set; }
        public bool usesTargetAttack { get; set; }
        public bool usesHigherOffense { get; set; }
        public TerrainType setTerrain { get; set; }
        public string? target { get; set; }
    }
}