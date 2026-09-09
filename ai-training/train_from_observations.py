#!/usr/bin/env python3
"""
Section 170, widened by section 175. Trains the Simulator's shadow model
from the observation corpus the Battle Lab produces, and exports
pokemon_ai.onnx.

    python train_from_observations.py <corpus> [-o pokemon_ai.onnx] [--epochs 30]

<corpus> is either the folder the app records into

    %LOCALAPPDATA%\\ProTracker\\SimulatorObservations

or a zip exported by Admin Console > Battle Lab > Export All Data.

What it learns: every decision the WINNING side of a battle made is a
training example - state (the ten floats ObserverEncoder produces) in,
the move slot that side actually chose out. Half the lab's battles are
played by the Monte Carlo brain, so winning-side cloning distills the
expensive rollout search into a network that answers instantly. That is
the original ai-lab/SelfPlayTrainer idea ("Monte-Carlo-as-teacher"), fed
by real recorded battles instead of a throwaway run.

Section 175 changed two things about that.

First, the state got wider. It used to be ten floats that said nothing
about what the four move slots CONTAIN, while the lab shuffles a team's
moves into its slots - so the label was a randomly permuted index the
inputs never mentioned. Measured on a task with the same shape, that
scores exactly chance at 2,916 parameters AND at 137,988. The width of
the input, not the size of the model, was the ceiling. This script no
longer hard-codes ten: it reads the width out of the corpus.

Second, the target got richer. Monte Carlo ranks every candidate move by
mean rollout score and then throws all but the argmax away. The observer
now records that ranking, and --soft-targets fits the whole distribution
instead of the one choice, which is far more signal per battle.

Section 176 added a third thing: the model can say SWITCH. Its outputs
are the four move slots followed by six team positions, so "send in
Team[2]" is output 6. Switch decisions were always being recorded and
always being thrown away here; now they are examples like any other.
A corpus without them still trains a four-output model.

The exported graph is what PokemonSim.Shadow/OnnxShadowEvaluator
validates: ONE float input [batch, width], ONE float output [batch, 4],
width -> 64 -> 32 -> 4 with ReLU. Tensor names do not matter to the
evaluator, only shapes; the width does, and it accepts either the
current ObservationSchema.FeatureCount or the pre-175 ten.

Needs numpy and nothing else - no PyTorch, no onnx package. The ONNX
file is written directly.
"""

import argparse
import json
import os
import struct
import sys
import zipfile

import numpy as np

LEGACY_FEATURES = 10   # ObservationSchema.FeatureCountV1 (pre-175)
MOVE_SLOTS = 4         # ObservationSchema.MoveSlots
TEAM_SLOTS = 6         # ObservationSchema.TeamSlots      (section 176)
ACTION_SLOTS = MOVE_SLOTS + TEAM_SLOTS   # ObservationSchema.ActionSlots
HIDDEN = (64, 32)      # ai-lab/Train_Model.py's shape

# Section 182. The risk feature was appended to the vector, so a state
# recorded before it is the same 202 numbers with one missing - and the
# missing one has a known value. Every teacher before section 182 ranked by
# the plain rollout mean, which IS risk 0, so a version 4 state widens to a
# version 5 state rather than belonging to a different problem. That is why
# these two are bridged and the older widths are not: 160 and 76 are missing
# features whose values nobody knows.
FEATURES_V4 = 202      # ObservationSchema.FeatureCountV4
FEATURES_V5 = 203      # ObservationSchema.FeatureCountV5
FEATURES = 205         # ObservationSchema.FeatureCount
NEUTRAL_RISK = 0.0     # ObservationSchema.NeutralRisk

# Section 184 appended two Sticky Web entries, and a state recorded before
# the move existed had no web on either side - which is what a zero says.
# So the ladder from 202 runs: add the risk the teacher was actually using
# (0), then add the two hazards that were genuinely not on the field.
WIDEN = {FEATURES_V4: [NEUTRAL_RISK, 0.0, 0.0], FEATURES_V5: [0.0, 0.0]}


# --------------------------------------------------------------------------
# reading the corpus
# --------------------------------------------------------------------------

def resolve_path(path):
    """Accepts what either Windows shell hands over. PowerShell does not
    expand %LOCALAPPDATA%, and cmd does not expand $env:LOCALAPPDATA, so a
    path copied from the wrong set of instructions arrives literal.
    os.path.expandvars sorts out %VAR% on Windows; ~ works anywhere."""
    return os.path.expandvars(os.path.expanduser(path.strip().strip('"')))


def _iter_battle_texts(corpus):
    """Yields (name, text) for every obs-*.jsonl in a folder or a zip."""
    if os.path.isdir(corpus):
        names = sorted(n for n in os.listdir(corpus)
                       if n.startswith("obs-") and n.endswith(".jsonl"))
        for name in names:
            with open(os.path.join(corpus, name), "r", encoding="utf-8") as handle:
                yield name, handle.read()
        return

    if zipfile.is_zipfile(corpus):
        with zipfile.ZipFile(corpus) as archive:
            for name in sorted(archive.namelist()):
                base = os.path.basename(name)
                if base.startswith("obs-") and base.endswith(".jsonl"):
                    yield base, archive.read(name).decode("utf-8")
        return

    hint = ""

    if "%" in corpus or "$env:" in corpus:
        hint = ("\n(That still looks like an unexpanded shell variable. In PowerShell use "
                "\"$env:LOCALAPPDATA\\ProTracker\\SimulatorObservations\", or just pass the "
                "full path.)")

    raise SystemExit(f"'{corpus}' is neither an observation folder nor a zip archive." + hint)


def load_examples(corpus, keep_losers=False, strategy=None):
    """Every move decision worth learning from, grouped by schema version.

    Section 175: a corpus can hold files from before and after the encoder
    widened. Those cannot train one model - the columns mean different
    things - so records are bucketed by their state width and the caller
    trains on the newest bucket and is told what was left out."""
    buckets = {}
    battles = skipped_unfinished = 0
    strategy_skipped = 0
    taught = widened = 0

    for _, text in _iter_battle_texts(corpus):
        pending = []
        score = None

        for line in text.splitlines():
            line = line.strip()
            if not line:
                continue

            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue  # a torn last line from an interrupted run

            kind = row.get("Record")

            if kind == "final":
                score = row.get("Player1Score", 0)
            elif kind == "decision":
                action = row.get("Action") or {}

                # Section 180: Replacement joined Move and Switch. It used
                # to be excluded because the engine recorded it AFTER
                # sending the Pokemon in, so the state gave away its own
                # answer; it is recorded before the swap now. That matters
                # more than it sounds - a lab corpus had 681 voluntary
                # switches in 339,129 decisions, because the baseline never
                # switches by choice, while every faint is a replacement.
                # Struggle stays out: it has no slot to name.
                if action.get("Kind") not in ("Move", "Switch", "Replacement"):
                    continue

                # Section 176: the action mask when the recording build
                # wrote one, else the move-only mask - which is also what
                # decides whether this corpus can train a switch head.
                mask = row.get("LegalActions") or row.get("LegalMoves") or []
                state = row.get("State") or []

                if action.get("Kind") in ("Switch", "Replacement"):
                    team = action.get("SwitchTeamIndex", -1)
                    index = MOVE_SLOTS + team if 0 <= team < TEAM_SLOTS else -1
                else:
                    index = action.get("MoveIndex", -1)
                    if not (0 <= index < MOVE_SLOTS):
                        index = -1

                # Section 182. Under DAgger the acting strategy and the
                # strategy being learned from are different: the model
                # drives, and what the Monte Carlo brain says it would have
                # done is the label. Where a teacher recorded a choice it
                # wins over the action that was actually taken - which for
                # every record before section 182 are the same thing
                # anyway, because whoever ranked also played.
                advised = row.get("TeacherActionIndex", -1)

                if isinstance(advised, int) and 0 <= advised < ACTION_SLOTS:
                    index = advised
                    taught += 1

                # Sections 182 and 184: an older state is a current state
                # with known values for what had not been invented yet.
                if len(state) in WIDEN:
                    state = list(state) + WIDEN[len(state)]
                    widened += 1

                if index < 0 or index >= len(mask):
                    continue
                if len(state) < LEGACY_FEATURES or len(mask) not in (MOVE_SLOTS, ACTION_SLOTS):
                    continue
                if mask[index] <= 0:
                    continue  # the chosen action must be one the mask allows

                if strategy and row.get("Strategy", "") != strategy:
                    strategy_skipped += 1
                    continue

                # Section 182. A DAgger record's label is the teacher's
                # advice, and advice does not become wrong because the
                # student went on to lose - the whole point of collecting
                # it is the positions a LOSING model steers into. So those
                # records skip the winning-side filter below.
                pending.append((state, index, mask, row.get("Side", ""),
                                row.get("TeacherScores"),
                                str(row.get("Strategy", "")).endswith("(taught)")))

        if score is None:
            skipped_unfinished += 1
            continue

        battles += 1

        for state, index, mask, side, teacher, was_taught in pending:
            outcome = score if side == "P1" else -score

            if was_taught or outcome > 0 or keep_losers:
                # Bucketed by BOTH widths: a state and an action space of
                # different generations describe different problems and
                # cannot share a network.
                bucket = buckets.setdefault((len(state), len(mask)), ([], [], [], []))
                bucket[0].append(state)
                bucket[1].append(index)
                bucket[2].append(mask)
                bucket[3].append(teacher if teacher else None)

    if not buckets:
        raise SystemExit(
            "No usable decisions found. Battles must have finished (a 'final' "
            "record) and contain move decisions - run the Battle Lab first."
            + (f"\n(Every candidate was filtered out by --strategy "
               f"\"{strategy}\"; {strategy_skipped} decision(s) had another "
               f"strategy.)" if strategy and strategy_skipped else ""))

    # The newest encoding present wins - a wider state is a later schema.
    width, actions = max(buckets)
    states, moves, masks, teachers = buckets[(width, actions)]

    older = sum(len(b[0]) for key, b in buckets.items() if key != (width, actions))

    teacher_rows = sum(1 for t in teachers if t)

    switches = sum(1 for m in moves if m >= MOVE_SLOTS)

    return Corpus(
        x=np.asarray(states, dtype=np.float32),
        y=np.asarray(moves, dtype=np.int64),
        mask=np.asarray(masks, dtype=np.float32),
        teacher=_teacher_matrix(teachers, actions),
        width=width,
        actions=actions,
        switches=switches,
        battles=battles,
        decisions=len(states),
        teacher_rows=teacher_rows,
        older_schema=older,
        unfinished=skipped_unfinished,
        strategy_skipped=strategy_skipped,
        taught=taught,
        widened=widened)


class Corpus:
    """What load_examples found, in one bag so main() stays readable."""

    def __init__(self, **fields):
        self.__dict__.update(fields)


def _teacher_matrix(teachers, actions):
    """One row per example: the recorded per-action scores, NaN where the
    strategy never evaluated that action, and an all-NaN row for a decision
    that carried no ranking at all."""
    rows = np.full((len(teachers), actions), np.nan, dtype=np.float32)

    for i, scores in enumerate(teachers):
        if not scores:
            continue
        for slot in range(min(actions, len(scores))):
            value = scores[slot]
            if value is not None:
                rows[i, slot] = value

    return rows


# --------------------------------------------------------------------------
# the network - 10 -> 64 -> 32 -> 4, plain numpy
# --------------------------------------------------------------------------

class Mlp:
    def __init__(self, features, hidden=HIDDEN, actions=MOVE_SLOTS, seed=170):
        rng = np.random.default_rng(seed)
        sizes = (features,) + tuple(hidden) + (actions,)
        self.w, self.b = [], []

        for a, b in zip(sizes, sizes[1:]):
            # He initialization - these are ReLU layers.
            self.w.append(rng.normal(0, np.sqrt(2.0 / a), (a, b)).astype(np.float32))
            self.b.append(np.zeros(b, dtype=np.float32))

    def forward(self, x, cache=None):
        for i, (w, b) in enumerate(zip(self.w, self.b)):
            x = x @ w + b
            if i < len(self.w) - 1:
                if cache is not None:
                    cache.append(x)
                x = np.maximum(x, 0.0)
                if cache is not None:
                    cache.append(x)
        return x


def masked_softmax(logits, mask):
    """Illegal slots can never win, exactly as the evaluator masks them."""
    safe = np.where(mask > 0, logits, -1e9)
    safe = safe - safe.max(axis=1, keepdims=True)
    exp = np.exp(safe)
    return exp / np.clip(exp.sum(axis=1, keepdims=True), 1e-12, None)


def accuracy(model, x, y, mask):
    scores = np.where(mask > 0, model.forward(x), -np.inf)
    return float((scores.argmax(axis=1) == y).mean())


def soft_targets(scores, mask):
    """Section 175. Monte Carlo's per-slot rollout means turned into a
    target distribution over the legal slots: a softmax over the scores it
    actually produced, sharpened enough that a clearly better move is
    clearly preferred but a close call stays a close call. Rows with no
    usable ranking come back as None so the caller can fall back to the
    one-hot label for them."""
    usable = np.isfinite(scores) & (mask > 0)
    rows = usable.sum(axis=1) >= 2          # a ranking needs two to rank

    target = np.zeros(scores.shape, dtype=np.float32)

    if not rows.any():
        return target, rows

    # A large finite floor rather than -inf: the row maximum below then
    # never comes out as -inf, so no row can produce a NaN even when
    # nothing in it was usable.
    sharp = 6.0                              # scores live in roughly 0..1
    floor = np.float32(-1e30)

    safe = np.where(usable, np.nan_to_num(scores, nan=0.0) * sharp, floor)
    safe = safe - safe.max(axis=1, keepdims=True)

    exp = np.where(usable, np.exp(safe), 0.0)
    target = (exp / np.clip(exp.sum(axis=1, keepdims=True), 1e-12, None)).astype(np.float32)
    target[~rows] = 0.0

    return target, rows


def train(model, x, y, mask, epochs, batch_size, learning_rate, seed=170,
          teacher=None):
    rng = np.random.default_rng(seed)
    n = len(x)

    split = max(1, int(n * 0.1))
    order = rng.permutation(n)
    val, tr = order[:split], order[split:]

    xv, yv, mv = x[val], y[val], mask[val]
    xt, yt, mt = x[tr], y[tr], mask[tr]

    # Section 175: where a ranking exists, the target is the whole
    # distribution; where it does not, it stays the one-hot choice. Both
    # are just a target row in the same cross-entropy, so the training
    # step below does not care which a given example is.
    if teacher is None:
        soft_t, soft_rows = None, None
    else:
        soft_t, soft_rows = soft_targets(teacher[tr], mt)

    m = [np.zeros_like(p) for p in model.w] + [np.zeros_like(p) for p in model.b]
    v = [np.zeros_like(p) for p in model.w] + [np.zeros_like(p) for p in model.b]
    step = 0
    best = None

    for epoch in range(1, epochs + 1):
        for start in range(0, len(xt), batch_size):
            idx = rng.integers(0, len(xt), size=min(batch_size, len(xt)))
            xb, yb, mb = xt[idx], yt[idx], mt[idx]

            cache = []
            logits = model.forward(xb, cache)
            probs = masked_softmax(logits, mb)

            target = np.zeros_like(probs)
            target[np.arange(len(yb)), yb] = 1.0

            if soft_t is not None:
                use = soft_rows[idx]
                target[use] = soft_t[idx][use]

            grad = probs - target
            grad /= len(yb)

            grads_w, grads_b = [None] * len(model.w), [None] * len(model.b)
            activations = [xb, cache[1], cache[3]]

            for layer in range(len(model.w) - 1, -1, -1):
                grads_w[layer] = activations[layer].T @ grad
                grads_b[layer] = grad.sum(axis=0)

                if layer > 0:
                    grad = grad @ model.w[layer].T
                    grad = grad * (cache[2 * layer - 2] > 0)

            step += 1
            params = model.w + model.b
            grads = grads_w + grads_b

            for i, (param, g) in enumerate(zip(params, grads)):
                m[i] = 0.9 * m[i] + 0.1 * g
                v[i] = 0.999 * v[i] + 0.001 * (g * g)
                mhat = m[i] / (1 - 0.9 ** step)
                vhat = v[i] / (1 - 0.999 ** step)
                param -= learning_rate * mhat / (np.sqrt(vhat) + 1e-8)

        train_acc = accuracy(model, xt[:20000], yt[:20000], mt[:20000])
        val_acc = accuracy(model, xv, yv, mv)
        print(f"  epoch {epoch:3d}   train {train_acc:6.1%}   validation {val_acc:6.1%}")

        if best is None or val_acc > best[0]:
            best = (val_acc, [w.copy() for w in model.w], [b.copy() for b in model.b])

    # Keep the best validation epoch, not merely the last.
    model.w, model.b = best[1], best[2]
    return best[0], xv, yv, mv


# --------------------------------------------------------------------------
# writing the ONNX file (protobuf by hand - no onnx package needed)
# --------------------------------------------------------------------------

def _varint(value):
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        out.append(byte | (0x80 if value else 0))
        if not value:
            return bytes(out)


def _tag(field, wire):
    return _varint((field << 3) | wire)


def _blob(field, payload):
    return _tag(field, 2) + _varint(len(payload)) + payload


def _text(field, value):
    return _blob(field, value.encode("utf-8"))


def _num(field, value):
    return _tag(field, 0) + _varint(value)


def _tensor(name, array):
    """TensorProto: dims(1) data_type(2) name(8) raw_data(9). 1 = FLOAT."""
    body = b"".join(_num(1, d) for d in array.shape)
    body += _num(2, 1)
    body += _text(8, name)
    body += _blob(9, array.astype(np.float32).tobytes())
    return body


def _value_info(name, dims):
    """dims entries: int for a fixed size, str for a named (dynamic) one."""
    shape = b""
    for d in dims:
        shape += _blob(1, _text(2, d) if isinstance(d, str) else _num(1, d))

    tensor_type = _num(1, 1) + _blob(2, shape)          # elem_type FLOAT, shape
    type_proto = _blob(1, tensor_type)                  # TypeProto.tensor_type
    return _text(1, name) + _blob(2, type_proto)


def _node(op_type, inputs, outputs, name):
    body = b"".join(_text(1, i) for i in inputs)
    body += b"".join(_text(2, o) for o in outputs)
    body += _text(3, name)
    body += _text(4, op_type)
    return body


def write_onnx(model, path):
    """width -> 64 -> 32 -> actions as Gemm/Relu, dynamic batch, opset 13.
    Sections 175 and 176: BOTH widths come off the trained weights rather
    than constants, so a model of any schema exports correctly."""
    nodes, initializers = [], []
    current = "x"

    for layer, (w, b) in enumerate(zip(model.w, model.b)):
        wn, bn = f"W{layer}", f"B{layer}"
        initializers.append(_tensor(wn, w))
        initializers.append(_tensor(bn, b))

        # Gemm defaults (alpha=1, beta=1, transA=0, transB=0) are exactly
        # what we want, so the nodes carry no attributes at all.
        out = "output" if layer == len(model.w) - 1 else f"gemm{layer}"
        nodes.append(_node("Gemm", [current, wn, bn], [out], f"gemm{layer}"))
        current = out

        if layer < len(model.w) - 1:
            relu_out = f"relu{layer}"
            nodes.append(_node("Relu", [current], [relu_out], f"relu{layer}"))
            current = relu_out

    graph = b"".join(_blob(1, n) for n in nodes)
    graph += _text(2, "pokemon_ai")
    graph += b"".join(_blob(5, t) for t in initializers)
    graph += _blob(11, _value_info("x", ["batch", int(model.w[0].shape[0])]))
    graph += _blob(12, _value_info("output", ["batch", int(model.w[-1].shape[1])]))

    model_proto = _num(1, 8)                                   # ir_version 8
    model_proto += _text(2, "protracker-ai-training")
    model_proto += _blob(7, graph)
    model_proto += _blob(8, _text(1, "") + _num(2, 13))        # opset 13

    with open(path, "wb") as handle:
        handle.write(model_proto)


# --------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description="Train pokemon_ai.onnx from observation data.")
    parser.add_argument("corpus", help="observation folder, or an exported .zip bundle")
    parser.add_argument("-o", "--output", default="pokemon_ai.onnx")
    parser.add_argument("--epochs", type=int, default=30)
    parser.add_argument("--batch-size", type=int, default=256)
    parser.add_argument("--learning-rate", type=float, default=2e-3)
    parser.add_argument("--seed", type=int, default=170)
    parser.add_argument("--keep-losers", action="store_true",
                        help="also learn from the losing side (usually worse)")
    parser.add_argument("--soft-targets", action="store_true",
                        help="fit Monte Carlo's per-slot ranking, not just its pick "
                             "(section 175; needs a corpus recorded by that build)")
    parser.add_argument("--strategy", default=None,
                        help='keep only decisions made by this strategy, e.g. '
                             '"Monte Carlo" - drops the coin-flip AI\'s wins')
    parser.add_argument("--hidden", default=",".join(str(h) for h in HIDDEN),
                        help=f"two hidden layer widths (default {HIDDEN[0]},{HIDDEN[1]})")
    args = parser.parse_args()

    try:
        hidden = tuple(int(part) for part in args.hidden.split(",") if part.strip())
    except ValueError:
        raise SystemExit(f"--hidden wants two numbers like 64,32 - got \"{args.hidden}\"")

    if len(hidden) != 2 or min(hidden) < 1:
        raise SystemExit(f"--hidden wants two positive numbers like 64,32 - got \"{args.hidden}\"")

    corpus = resolve_path(args.corpus)
    output = resolve_path(args.output)

    print(f"Reading {corpus} ...")
    data = load_examples(corpus, args.keep_losers, args.strategy)

    print(f"  {data.battles} finished battle(s), {data.decisions} training decision(s)"
          + (f", {data.unfinished} unfinished file(s) skipped" if data.unfinished else ""))
    print(f"  {data.width}-feature states, {data.actions} action outputs"
          + ("" if data.width != LEGACY_FEATURES
             else " (pre-175 encoding - these say nothing about what the move "
                  "slots contain, so expect close to chance)"))

    if data.widened:
        print(f"  {data.widened} state(s) were recorded before the newest "
              "features and were widened to fit - risk 0, which is what "
              "their teacher was, and no Sticky Web, which there was not")

    if data.taught:
        share = 100.0 * data.taught / max(1, data.decisions)
        print(f"  {data.taught} ({share:.1f}%) carry a teacher's advice as "
              "their label - DAgger data, kept whether or not that battle "
              "was won")
    elif data.actions > MOVE_SLOTS:
        print("  no DAgger data: every label here is the action that was "
              "actually taken. Collect on 'Model taught by brain' to record "
              "what the brain would have done in the positions the model "
              "steers itself into.")

    if data.actions > MOVE_SLOTS:
        share = 100.0 * data.switches / max(1, data.decisions)
        print(f"  {data.switches} of them are switch decisions ({share:.1f}%) - "
              "this trains a switch head")

        if share < 5.0:
            print("  That is thin. The baseline AI never switches by choice, so "
                  "a corpus collected against it has almost nothing for the "
                  "switch head to learn from; 'Observe only' on a section 180 "
                  "build records every post-faint replacement as well.")
    else:
        print("  move-only corpus: no switch head. Re-run the Battle Lab on a "
              "section 176 build to record switch legality.")

    if data.older_schema:
        print(f"  {data.older_schema} decision(s) from an OLDER encoding were "
              "left out - they cannot train one model with these. Clear or "
              "move aside the old observation files to stop seeing this.")

    if args.strategy:
        print(f"  --strategy \"{args.strategy}\": {data.strategy_skipped} "
              "decision line(s) recorded by another strategy were skipped "
              "before any of the above")

    teacher = None

    if args.soft_targets:
        if data.teacher_rows == 0:
            print("  --soft-targets asked for, but NO decision in this corpus "
                  "carries a ranking, so it falls back to the plain choice.")
            print("  Only the Monte Carlo brain ranks its options. A corpus "
                  "with none of its decisions in it was collected with the "
                  "Battle Lab set to one of the 'Battle ...' matchups, which "
                  "put the trained model against an opponent instead of "
                  "watching the teacher play. Re-collect with the matchup on "
                  "'Observe only'.")
        else:
            teacher = data.teacher
            print(f"  --soft-targets: {data.teacher_rows} of {data.decisions} "
                  "decision(s) carry Monte Carlo's per-slot ranking")

    # What a coin flip among the legal actions would score, for comparison.
    legal_counts = np.clip(data.mask.sum(axis=1), 1, None)
    baseline = float((1.0 / legal_counts).mean())
    print(f"  picking at random among legal moves would score {baseline:.1%}\n")

    model = Mlp(data.width, hidden, data.actions, seed=args.seed)
    val_acc, xv, yv, mv = train(model, data.x, data.y, data.mask, args.epochs,
                                args.batch_size, args.learning_rate, args.seed,
                                teacher)

    print(f"\nBest validation agreement: {val_acc:.1%} (random baseline {baseline:.1%})")

    if val_acc <= baseline:
        print("WARNING: the model did no better than guessing. Collect more "
              "battles, or raise --epochs, before shipping this.")

    write_onnx(model, output)
    print(f"Wrote {os.path.abspath(output)} ({os.path.getsize(output)} bytes)")

    # Prove the file loads and agrees with the trained weights before it
    # goes anywhere near the app.
    try:
        import onnxruntime as ort
    except ImportError:
        print("\n(Install onnxruntime to have this script verify the export: "
              "pip install onnxruntime)")
        return

    session = ort.InferenceSession(output, providers=["CPUExecutionProvider"])
    name = session.get_inputs()[0].name
    sample = xv[:256]
    got = session.run(None, {name: sample.astype(np.float32)})[0]
    want = model.forward(sample)
    drift = float(np.abs(got - want).max())

    print(f"Verified: onnxruntime loads it, input '{name}' "
          f"{session.get_inputs()[0].shape} -> output {session.get_outputs()[0].shape}, "
          f"max difference from the trained weights {drift:.2e}")

    if drift > 1e-4:
        print("WARNING: the exported model does not match the trained weights.")


if __name__ == "__main__":
    main()
