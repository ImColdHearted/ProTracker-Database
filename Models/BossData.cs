using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    public class BossData
    {
        [JsonPropertyName("bossId")]
        public string BossId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("location")]
        public string Location { get; set; } = string.Empty;

        [JsonPropertyName("locationPicture")]
        public string LocationPicture { get; set; } = string.Empty;

        [JsonPropertyName("locationImage")]
        public string LocationImage { get; set; } = string.Empty;

        [JsonPropertyName("cooldown")]
        public string Cooldown { get; set; } = string.Empty;

        [JsonPropertyName("requirement")]
        public string Requirement { get; set; } = string.Empty;

        [JsonPropertyName("requirements")]
        public string Requirements { get; set; } = string.Empty;

        [JsonPropertyName("difficulties")]
        public Dictionary<string, BossDifficultyData> Difficulties { get; set; }
            = new();
    }

    public class BossDifficultyData
    {
        [JsonPropertyName("rewards")]
        public BossRewards Rewards { get; set; } = new();

        [JsonPropertyName("team")]
        public List<BossPokemonData> Team { get; set; } = new();
    }

    public class BossRewards
    {
        [JsonPropertyName("pokedollars")]
        public PokedollarReward Pokedollars { get; set; } = new();

        [JsonPropertyName("pveCoins")]
        public int PveCoins { get; set; }

        [JsonPropertyName("items")]
        public List<BossItemReward> Items { get; set; } = new();

        [JsonPropertyName("pokemon")]
        public List<BossPokemonReward> Pokemon { get; set; } = new();
    }

    public class PokedollarReward
    {
        [JsonPropertyName("minimum")]
        public int Minimum { get; set; }

        [JsonPropertyName("maximum")]
        public int Maximum { get; set; }
    }

    public class BossPokemonData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dexNumber")]
        public int DexNumber { get; set; }

        [JsonPropertyName("nature")]
        public string Nature { get; set; } = string.Empty;

        [JsonPropertyName("ability")]
        public string Ability { get; set; } = string.Empty;

        [JsonPropertyName("item")]
        public string Item { get; set; } = string.Empty;

        [JsonPropertyName("moves")]
        public List<string> Moves { get; set; } = new();
    }

    public class BossItemReward
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("quantity")]
        public string Quantity { get; set; } = string.Empty;

        [JsonPropertyName("tier")]
        public int Tier { get; set; }

        [JsonPropertyName("picture")]
        public string Picture { get; set; } = string.Empty;
    }

    public class BossPokemonReward
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dexNumber")]
        public int DexNumber { get; set; }

        [JsonPropertyName("picture")]
        public string Picture { get; set; } = string.Empty;

        [JsonPropertyName("winStreakRequired")]
        public int WinStreakRequired { get; set; }
    }
}