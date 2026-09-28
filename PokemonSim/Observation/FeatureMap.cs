using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PokemonSim.Observation
{
    /// <summary>Whether a feature is something the acting side can legitimately
    /// know. Every feature declares one, and the §319 battery uses these to
    /// check the encoder against itself.</summary>
    public enum Visibility
    {
        /// <summary>The acting side's own business - its Pokemon, its team,
        /// its items. Always knowable.</summary>
        Own,

        /// <summary>Plainly visible to both players: hit-point bars, status,
        /// stat stages, weather, hazards, a species standing on the field.</summary>
        Public,

        /// <summary>Something about the opponent that was LEARNED by watching -
        /// a move it used, a type it took nothing from, an item that visibly
        /// went away. Comes only from BattleKnowledge.</summary>
        Observed,

        /// <summary>A marker saying that something is NOT known. The most
        /// important visibility there is: it is what stops "unknown" and
        /// "zero" being the same number.</summary>
        Unknown,

        /// <summary>Derived from the recorded history rather than from the
        /// current position.</summary>
        History,

        /// <summary>Not about the battle at all - the §182 risk dial.</summary>
        Meta,

        /// <summary>HIDDEN. A feature no fair observation may carry. Only the
        /// omniscient encoder writes these, and only under its own schema id.
        /// A feature marked Hidden appearing in a fair map fails the build.</summary>
        Hidden,
    }

    /// <summary>§319. Where one block sits. A named type rather than a
    /// tuple: the tuple read fine and defeated the project's own
    /// identifier checker, which is a poor trade for two saved lines.</summary>
    public readonly struct FeatureBlock
    {
        public FeatureBlock(int start, int size)
        {
            Start = start;
            Size = size;
        }

        public int Start { get; }
        public int Size { get; }
    }

    /// <summary>§319. One column of the observation vector, described rather
    /// than counted.</summary>
    public sealed class FeatureSpec
    {
        public FeatureSpec(int index, string block, string name, string meaning,
                           string normalization, Visibility visibility)
        {
            Index = index;
            Block = block;
            Name = name;
            Meaning = meaning;
            Normalization = normalization;
            Visibility = visibility;
        }

        public int Index { get; }
        public string Block { get; }
        public string Name { get; }
        public string Meaning { get; }

        /// <summary>How the value is scaled - "0..1", "stage/6", "one-hot"
        /// and so on. Written down because a reader of a trained model has no
        /// other way to find out.</summary>
        public string Normalization { get; }

        public Visibility Visibility { get; }

        public override string ToString() =>
            $"{Index,5}  {Block}.{Name}  [{Visibility}, {Normalization}]  {Meaning}";
    }

    /// <summary>
    /// §319. The observation layout, DECLARED.
    ///
    /// WHY THIS EXISTS. §311's layout was a wall of
    /// <c>public const int XBlockStart = YBlockStart + YBlockSize;</c> and it
    /// worked, but it could only ever say where a block began - never what a
    /// column inside it meant, never whether that column was something a
    /// player could know. Adding a feature in the middle of a block meant
    /// counting by hand and hoping. The user asked for a schema that does not
    /// require manually counting hundreds of indices, and this is it.
    ///
    /// A map is BUILT by appending blocks. Each block appends its columns in
    /// order and the builder assigns the indices, so a column added anywhere
    /// moves everything after it automatically and nothing needs renumbering.
    /// The result is immutable, knows its own width, and can print itself -
    /// which is what the observation inspector renders and what the leak
    /// checker walks.
    /// </summary>
    public sealed class FeatureMap
    {
        private readonly List<FeatureSpec> features;
        private readonly Dictionary<string, FeatureBlock> blocks;

        internal FeatureMap(List<FeatureSpec> features,
                            Dictionary<string, FeatureBlock> blocks)
        {
            this.features = features;
            this.blocks = blocks;
        }

        public int Count => features.Count;

        public IReadOnlyList<FeatureSpec> Features => features;

        public IReadOnlyCollection<string> Blocks => blocks.Keys;

        /// <summary>The first index of a block. Throws rather than returning
        /// a plausible zero: a mistyped block name that silently wrote to the
        /// front of the vector is precisely the failure this type exists to
        /// make impossible.</summary>
        public int Start(string block) => Span(block).Start;

        public int Size(string block) => Span(block).Size;

        private FeatureBlock Span(string block)
        {
            if (blocks.TryGetValue(block, out FeatureBlock span))
                return span;

            throw new KeyNotFoundException($"No feature block named \"{block}\".");
        }

        /// <summary>Every feature whose visibility is one a fair observation
        /// may not carry. Empty is the only acceptable answer for the fair
        /// map, and the battery says so.</summary>
        public IEnumerable<FeatureSpec> HiddenFeatures =>
            features.Where(f => f.Visibility == Visibility.Hidden);

        /// <summary>The whole layout as text, one line per column. What the
        /// admin console prints and what a future reader of a trained model
        /// needs in order to know what column 704 was.</summary>
        public string Describe()
        {
            var text = new StringBuilder();

            text.AppendLine($"{Count} features in {blocks.Count} blocks");

            foreach (KeyValuePair<string, FeatureBlock> block in blocks)
            {
                FeatureBlock span = block.Value;

                text.AppendLine($"  {block.Key,-24} {span.Start,5} .. {span.Start + span.Size - 1,5}  ({span.Size})");
            }

            text.AppendLine();

            foreach (FeatureSpec f in features)
                text.AppendLine(f.ToString());

            return text.ToString();
        }
    }

    /// <summary>
    /// §319. Builds a FeatureMap by declaring blocks in order.
    ///
    /// The builder is the only thing that knows an index. Nothing that uses
    /// the map ever computes one; it asks for a block's start and writes
    /// within it, and the block's own declaration decides how wide that is.
    /// </summary>
    public sealed class FeatureMapBuilder
    {
        private readonly List<FeatureSpec> features = new();
        private readonly Dictionary<string, FeatureBlock> blocks = new();

        private string current = "";
        private int blockStart;

        /// <summary>Open a block. Closes the previous one.</summary>
        public FeatureMapBuilder Block(string name)
        {
            Close();

            if (blocks.ContainsKey(name))
                throw new ArgumentException($"Feature block \"{name}\" declared twice.");

            current = name;
            blockStart = features.Count;

            return this;
        }

        /// <summary>One column.</summary>
        public FeatureMapBuilder Add(string name, string meaning, string normalization,
                                     Visibility visibility)
        {
            if (current.Length == 0)
                throw new InvalidOperationException("A feature was added before any block was opened.");

            features.Add(new FeatureSpec(features.Count, current, name, meaning,
                                         normalization, visibility));

            return this;
        }

        /// <summary>A run of columns that mean the same kind of thing - a
        /// one-hot, or the eighteen types.</summary>
        public FeatureMapBuilder AddMany(int count, Func<int, string> name, string meaning,
                                         string normalization, Visibility visibility)
        {
            for (int i = 0; i < count; i++)
                Add(name(i), meaning, normalization, visibility);

            return this;
        }

        /// <summary>A group repeated per slot - four move slots, six team
        /// positions, sixteen history rows. The repeated body declares itself
        /// once and the builder lays it out <paramref name="times"/> over,
        /// which is what makes the stride impossible to get wrong.</summary>
        public FeatureMapBuilder Repeat(int times, string prefix,
                                        Action<FeatureMapBuilder, string> body)
        {
            for (int i = 0; i < times; i++)
                body(this, $"{prefix}{i}");

            return this;
        }

        public FeatureMap Build()
        {
            Close();

            return new FeatureMap(features, blocks);
        }

        private void Close()
        {
            if (current.Length == 0)
                return;

            blocks[current] = new FeatureBlock(blockStart, features.Count - blockStart);
            current = "";
        }
    }
}
