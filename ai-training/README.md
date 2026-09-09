## Before anything else, if you have a corpus from an earlier build

Section 175 replaced the ten-float state with a 76-float one that
describes the four move slots. Section 176 took it to 160 and added a
SWITCH output, so the model can leave a bad matchup instead of standing
there. Records from different generations cannot train one model - the
columns mean different things - and old ones cannot be upgraded, because
what was not recorded was never recorded.

So clear or move aside the old observation data before the first run on
a new build (Battle Lab has a Clear button; Export All Data first if you
want to keep it). The trainer will not silently mix them: it trains on
the newest encoding it finds and tells you how many older records it
left out.

## The loop

**1. Collect battles.** Admin Console (File > Admin Login first) >
Battle Lab. Set a count and press Run Battles. Half the battles play the
Monte Carlo brain against the baseline AI, half the baseline against
itself, and both sides of every decision are recorded. More is better;
10,000 battles is a few hundred thousand decisions.

**2. Export the corpus.** Same panel: Export All Data. You get one zip.
Keep it - this is the raw material, and it is the thing worth backing up
before you ever press Clear Observation Data.

**3. Train.** Open PowerShell and change into this folder first - the
script is here, and the model it writes lands in whatever folder you are
standing in:

    cd C:\Users\DeepF\source\repos\Pro_Tracker\ai-training
    py train_from_observations.py C:\Users\DeepF\Downloads\simulator-observations-YYYYMMDD.zip

simulator-observations-20260902-165539

It also accepts the live folder directly. Note the PowerShell spelling of
the variable - `$env:NAME`, not `%NAME%`:

    python train_from_observations.py "$env:LOCALAPPDATA\ProTracker\SimulatorObservations"

In old-style cmd.exe it is `%LOCALAPPDATA%` instead. The script expands
either, so a path pasted from the wrong place still works, and it says so
plainly if you hand it something it cannot find.

If `python` is not recognised, try `py` instead (the Windows launcher),
or install Python from python.org with "Add python.exe to PATH" ticked.

You do not have to cd at all if you would rather give full paths:

    python C:\Users\DeepF\source\repos\Pro_Tracker\ai-training\train_from_observations.py `
        "$env:LOCALAPPDATA\ProTracker\SimulatorObservations" `
        -o C:\Users\DeepF\Desktop\pokemon_ai.onnx

It prints per-epoch accuracy and finishes with something like:

    Best validation agreement: 61.4% (random baseline 21.0%)
    Wrote pokemon_ai.onnx (61971 bytes)
    Verified: onnxruntime loads it, input 'x' ['batch', 202] -> output ['batch', 10],
    max difference from the trained weights 9.54e-07

The file size is a useful sanity check, and it is fixed by the shape of
the network rather than by how much you trained. It does not grow with
epochs or with corpus size:

    61,971 bytes   202 -> 64 -> 32 -> 10   current
    51,219 bytes   160 -> 64 -> 32 -> 10   section 176
    28,921 bytes    76 -> 64 -> 32 ->  4   section 175
    12,022 bytes    10 -> 64 -> 32 ->  4   section 156

A smaller file than you expect means you trained on an older corpus.

Note that the random baseline drops when the switch head arrives - there
are more actions to guess between - so compare a run against the number
the script prints for THAT run, not against an older one.

**What "good" looks like:** validation agreement should sit clearly
above the random baseline the script prints. At or below it, the model
learned nothing - collect more battles or raise `--epochs`, and do not
ship it. The script warns you when that happens.

**4. Try it before publishing.** Admin Console > Battle Lab >
Install Trained Model, and pick the `pokemon_ai.onnx` you just made. That
copies it into this machine's observation folder, where
`ObservationStore.ResolveModelPath` prefers it over the shipped copy.
Play a Simulator battle with the observer on: the status line should read
`shadow model loaded (202 -> 10)` instead of a failure, and new records
carry `Shadow.Available: true` with an `Agreed` flag per decision. Older
models still load and say which generation they are - `(160 -> 10,
pre-177 features)`, `(76 -> 4, pre-176 features, no switch head)`,
`(10 -> 4, pre-175 features, no switch head)` - because every earlier
feature vector is a prefix of the current one and the four move slots are
still the first four outputs.

**The measurement that matters.** Battle Lab > Battle Monte Carlo tells
you the model's win rate against the boss brain, but on its own that
number means little. Run Battle Baseline too: if the network cannot beat
the coin-flip AI convincingly, it has not learned anything, whatever it
scores against Monte Carlo. And use a few thousand battles, not a
hundred - at 100 battles the confidence interval around a 6% win rate
runs from about 2% to 13%, so two runs that look different usually are
not.

**5. Publish it.** Copy the file over

    PokemonSim/DataFiles/pokemon_ai.onnx

then rebuild and publish. That path is already wired to copy into every
build as `PokemonSimData/pokemon_ai.onnx`. Delete the personal copy in
your observation folder afterwards if you want the app to fall back to
the shipped one.

## What it actually learns

Every decision the WINNING side of a battle made becomes one example:
the state in, the move slot that side chose out, with illegal slots
masked out of the loss exactly as the evaluator masks them at inference.
Since the Monte Carlo brain wins its share of those battles, the network
is distilling expensive rollout search into something that answers
instantly - the original `ai-lab/SelfPlayTrainer.cs` idea, fed by real
recorded battles.

Section 175 changed what "the state" means. It used to be ten floats -
two hp fractions, six stat stages, weather and terrain - which say
nothing about what the four move slots CONTAIN, while the lab shuffles
each team's moves into its slots. The label was therefore a randomly
permuted index the inputs never mentioned, and no network of any size
can fit that. The state now carries, for every slot, its type
effectiveness against the opposing active, power, category, STAB,
accuracy, PP and a damage estimate, plus who is faster, both sides'
status and how much team is left.

Section 176 did the same thing for switching. It added nine effect flags
per move slot (does it heal, boost, lower, inflict, flinch, multi-hit,
set the field, protect, cost the user something), the whole bench - hp
and type matchup for each of the six team positions against whatever is
out on the other side - and the entry hazards on both fields. Then it
gave the model somewhere to put that: outputs 4 to 9 mean "send in team
position 0 to 5". Switch decisions were always being recorded and always
being discarded here; now they are examples like any other.

Section 177 added the field - screens, Mist, Safeguard and Tailwind per
side, Trick Room, Gravity, the turns left on the weather and terrain, and
whether each side has spent its mega and its Z-Move - and then closed a
hole that had been open since section 156: the SPECIAL attack and defense
stages were never recorded at all. Only Attack, Defense and Speed were,
so a Nasty Plot or a Calm Mind was invisible to every model this project
had ever trained. Accuracy and evasion stages went in beside them, along
with the volatiles that decide whether a move is worth picking: confused,
leech seeded, behind a substitute, taunted, encored, disabled, trapped,
charging.

Every earlier vector is a strict prefix of the current one, entry for
entry, so a model trained before any of this still loads and still works
on the front of the state.

With `--soft-targets` the target changes too. Monte Carlo ranks every
candidate move by mean rollout score and then throws all but the winner
away; the observer now records that ranking, and this mode fits the
whole distribution instead of the single pick. That is far more signal
per battle, so it is worth using whenever the corpus has it.

The network shape (width -> 64 -> 32 -> 4, ReLU) follows
`ai-lab/Train_Model.py` apart from the input width, and the exported
graph is what `PokemonSim.Shadow/OnnxShadowEvaluator` validates: one
float input `[batch, width]`, one float output `[batch, 4]`. Tensor
names do not matter to it, only shapes; the width does, and it accepts
either the current one or the old ten.

## Useful flags

    --soft-targets       fit Monte Carlo's per-slot ranking, not just its
                         pick - the biggest single improvement available,
                         and free if your corpus was recorded after 175
    --strategy "Monte Carlo"
                         keep only that strategy's decisions, dropping the
                         coin-flip AI's wins. Measured on synthetic data
                         this roughly breaks even (half the data, twice the
                         purity), so reach for more battles first
    --hidden 128,64      a wider network. The evaluator does not care about
                         hidden widths, only the input and output. It bought
                         almost nothing when the input was ten floats, but
                         there is far more to fit now that the input is 160
                         wide and the output is 10 - worth one comparison
    --epochs 60          longer training (default 30). Measured, the curve
                         is flat by about epoch 30-50 at every learning
                         rate from 5e-4 to 1e-2; more epochs cost time and
                         change nothing
    --learning-rate 1e-3 gentler steps (default 2e-3)
    --keep-losers        also learn from the losing side (usually worse)
    -o some_name.onnx    write somewhere other than ./pokemon_ai.onnx

## The rule that has not changed

Section 156 still holds: this model only ever OBSERVES. It scores
decisions after the fact so you can see whether it agrees; it never
chooses a move in a real battle. Making it play is a separate, explicit
phase.
