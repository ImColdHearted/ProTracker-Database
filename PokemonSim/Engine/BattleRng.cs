using System;

namespace PokemonSim.Engine
{
    /// <summary>
    /// Section 154. The battle's one source of randomness. A PCG32 generator
    /// implemented here rather than System.Random for three reasons the
    /// simulator needs and Random cannot give: the stream is identical on
    /// every platform and .NET version (System.Random's algorithm is
    /// deliberately unspecified), a battle is exactly reproducible from its
    /// seed (every seeded test in PokemonSim.Tests depends on that), and the
    /// generator can be cloned mid-battle so a cloned BattleState rolls the
    /// same future without stealing rolls from the original - AI rollouts on
    /// clones must never advance the visible battle's dice.
    /// </summary>
    public sealed class BattleRng
    {
        private ulong state;
        private readonly ulong increment;

        public int Seed { get; }

        public BattleRng(int seed)
        {
            Seed = seed;
            increment = (((ulong)(uint)seed << 1) | 1UL) ^ 0xDA3E39CB94B95BDBUL;
            state = 0UL;
            NextUInt();
            state += 0x853C49E6748FEA9BUL + (ulong)(uint)seed;
            NextUInt();
        }

        private BattleRng(ulong state, ulong increment, int seed)
        {
            this.state = state;
            this.increment = increment;
            Seed = seed;
        }

        /// <summary>An identical generator in an identical position - the
        /// clone and the original produce the same future rolls, and rolling
        /// one never moves the other.</summary>
        public BattleRng Clone() => new BattleRng(state, increment, Seed);

        private uint NextUInt()
        {
            ulong old = state;
            state = old * 6364136223846793005UL + (increment | 1UL);
            uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
            int rotation = (int)(old >> 59);
            return (xorShifted >> rotation) | (xorShifted << ((-rotation) & 31));
        }

        /// <summary>0 <= result < maxExclusive. The tiny modulo bias is
        /// irrelevant at battle scale and keeps the generator simple.</summary>
        public int Next(int maxExclusive) =>
            maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);

        public int Next(int minInclusive, int maxExclusive) =>
            minInclusive >= maxExclusive ? minInclusive : minInclusive + Next(maxExclusive - minInclusive);

        /// <summary>0.0 <= result < 1.0.</summary>
        public double NextDouble() => NextUInt() / 4294967296.0;

        /// <summary>True with the given probability (0 never, 1 always).</summary>
        public bool Chance(double probability) =>
            probability >= 1.0 || (probability > 0.0 && NextDouble() < probability);
    }
}