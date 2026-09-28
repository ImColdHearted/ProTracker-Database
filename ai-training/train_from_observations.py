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
FEATURES_V6 = 205      # section 184's width
FEATURES_V7 = 483      # section 311's width - the OMNISCIENT one, see below
FEATURES = 1206        # ObservationSchema.FeatureCount (section 319, V8)
NEUTRAL_RISK = 0.0     # ObservationSchema.NeutralRisk

# Section 319. Two schema ids, and they must never be trained together.
#
# SCHEMA_FAIR is the observation as a player could have it: the opponent only
# as far as it has shown itself, with UNKNOWN as its own channel. SCHEMA_OMNI
# is section 311's vector, kept only so the cost of honesty can be measured -
# it contains the opponent's exact stats, ability and item, and a model
# trained on it has learned to play a game nobody can play.
#
# They are bucketed by SCHEMA ID and not by width. Widths happen to differ
# today; an id cannot start agreeing by accident.
SCHEMA_FAIR = 8        # ObservationSchema.Version
SCHEMA_OMNI = 108      # ObservationSchema.OmniscientVersion

# Section 184 appended two Sticky Web entries, and a state recorded before
# the move existed had no web on either side - which is what a zero says.
# So the ladder from 202 runs: add the risk the teacher was actually using
# (0), then add the two hazards that were genuinely not on the field.
WIDEN = {FEATURES_V4: [NEUTRAL_RISK, 0.0, 0.0], FEATURES_V5: [0.0, 0.0]}

# Section 319 deliberately adds NOTHING to that ladder. A 483-feature state is
# not a 1206-feature state with 723 values nobody knows - it is a different
# game, recorded by an agent that could see the opponent's sheet. Widening one
# would be inventing the evidence the fair vector is made of. They stay in
# their own bucket and the run says how many were left there and why.


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


def load_examples(corpus, keep_losers=False, strategy=None, omniscient=False):
    """Every move decision worth learning from, grouped by schema version.

    Section 175: a corpus can hold files from before and after the encoder
    widened. Those cannot train one model - the columns mean different
    things - so records are bucketed by their state width and the caller
    trains on the newest bucket and is told what was left out."""
    buckets = {}
    battles = skipped_unfinished = 0
    strategy_skipped = 0
    taught = widened = 0

    for battle_name, text in _iter_battle_texts(corpus):
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

                # Section 338: and remember, PER ROW, whether the label is a
                # teacher's or the actor's own. Every row is a legal action
                # somebody took; only some of them are a demonstration of how
                # to play. The difference is the whole of section 338.
                from_teacher = False

                if isinstance(advised, int) and 0 <= advised < ACTION_SLOTS:
                    index = advised
                    from_teacher = True
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
                                from_teacher,
                                str(row.get("Strategy", "")).endswith("(taught)"),
                                int(row.get("Schema", 0)),
                                # Section 320: the two new targets. Both are
                                # written by the observer AFTER the turn
                                # resolved and neither is in State - that
                                # separation is the whole point.
                                int(row.get("OpponentActionIndex", -1)),
                                bool(row.get("OpponentSwitched", False))))

        if score is None:
            skipped_unfinished += 1
            continue

        battles += 1

        for (state, index, mask, side, teacher, from_teacher, was_taught, schema,
             opponent_index, opponent_switched) in pending:
            # Section 320: the VALUE target, from the perspective of the side
            # that made this decision. P1's +1 is P2's -1 for the same battle,
            # and getting that backwards would train both sides to want the
            # same outcome.
            outcome = score if side == "P1" else -score

            if was_taught or outcome > 0 or keep_losers:
                # Bucketed by the SCHEMA ID and both widths. Section 319 put
                # the id first: a fair state and an omniscient one describe
                # different games, and no arithmetic on widths can be trusted
                # to keep them apart for ever.
                bucket = buckets.setdefault((schema, len(state), len(mask)),
                                            ([], [], [], [], [], [], [], []))
                bucket[0].append(state)
                bucket[1].append(index)
                bucket[2].append(mask)
                bucket[3].append(teacher if teacher else None)
                bucket[4].append(float(outcome))
                bucket[5].append(opponent_class(opponent_index, opponent_switched))
                # Section 320: which battle this came from, so the split can
                # be made at the battle and not at the decision.
                bucket[6].append(battle_name)
                bucket[7].append(from_teacher)

    if not buckets:
        raise SystemExit(
            "No usable decisions found. Battles must have finished (a 'final' "
            "record) and contain move decisions - run the Battle Lab first."
            + (f"\n(Every candidate was filtered out by --strategy "
               f"\"{strategy}\"; {strategy_skipped} decision(s) had another "
               f"strategy.)" if strategy and strategy_skipped else ""))

    # Section 319: the FAIR corpus is what trains, unless the caller asked
    # for the comparison model by name. Picking "the widest" would have
    # quietly trained the omniscient one the day it happened to be wider.
    wanted = SCHEMA_OMNI if omniscient else SCHEMA_FAIR

    matching = {key: value for key, value in buckets.items() if key[0] == wanted}

    if not matching:
        found = sorted({key[0] for key in buckets})

        raise SystemExit(
            f"No schema-{wanted} decisions found"
            + (" (asked for the omniscient comparison corpus)" if omniscient else "")
            + f". The corpus holds schema id(s) {found}.\n"
            + ("Records with schema 108 are the omniscient comparison set - "
               "pass --omniscient to train on them.\n" if SCHEMA_OMNI in found else "")
            + ("Records with an id below 8 are from before section 319 and "
               "cannot be upgraded: they were recorded by an observer that "
               "could see the opponent's stats, ability and item, and there "
               "is no way to un-know that.\n" if any(f < 8 for f in found) else ""))

    schema, width, actions = max(matching)
    states, moves, masks, teachers, values, opponents, battle_ids, from_teacher = \
        matching[(schema, width, actions)]

    older = sum(len(b[0]) for key, b in buckets.items()
                if key != (schema, width, actions))

    discarded = {}

    for key, value in buckets.items():
        if key == (schema, width, actions):
            continue

        label = ("omniscient" if key[0] == SCHEMA_OMNI
                 else f"schema {key[0]}" if key[0] >= SCHEMA_FAIR
                 else "pre-319 (the observation could see hidden information)")

        discarded[f"{label}, {key[1]} features"] = len(value[0])

    teacher_rows = sum(1 for t in teachers if t)

    switches = sum(1 for m in moves if m >= MOVE_SLOTS)

    opponent_rows = sum(1 for o in opponents if o >= 0)
    value_signs = sorted({int(np.sign(v)) for v in values})

    return Corpus(
        x=np.asarray(states, dtype=np.float32),
        y=np.asarray(moves, dtype=np.int64),
        mask=np.asarray(masks, dtype=np.float32),
        teacher=_teacher_matrix(teachers, actions),
        value=np.asarray(values, dtype=np.float32),
        opponent=np.asarray(opponents, dtype=np.int64),
        battle_ids=np.asarray(battle_ids),
        from_teacher=np.asarray(from_teacher, dtype=bool),
        opponent_rows=opponent_rows,
        value_signs=value_signs,
        width=width,
        actions=actions,
        switches=switches,
        battles=battles,
        decisions=len(states),
        teacher_rows=teacher_rows,
        older_schema=older,
        schema=schema,
        discarded=discarded,
        unfinished=skipped_unfinished,
        strategy_skipped=strategy_skipped,
        taught=taught,
        widened=widened)


class Corpus:
    """What load_examples found, in one bag so main() stays readable."""

    def __init__(self, **fields):
        self.__dict__.update(fields)


# Section 320. The opponent's action, reduced to what a player could identify.
#
# THIS IS THE RULE THAT MATTERS. The policy head predicts our own action over
# the full ten-slot space, because we know our own team. The opponent head
# cannot: when they switch we generally do not know WHICH of their six came in
# until it lands, so a target of "switch to team slot 4" would be asking the
# network to have known something it could not. The opponent target is five
# classes - four move slots and one SWITCH - which is the granularity an
# observer really has at the moment of the decision.
OPPONENT_CLASSES = MOVE_SLOTS + 1
OPPONENT_SWITCH = MOVE_SLOTS


def opponent_class(action_index, switched):
    """The opponent's recorded action as one of the five observable classes,
    or -1 when it was not recorded - a pre-320 file, or a turn on which they
    never got to act."""
    if switched:
        return OPPONENT_SWITCH

    if 0 <= action_index < MOVE_SLOTS:
        return action_index

    if action_index >= MOVE_SLOTS:
        return OPPONENT_SWITCH

    return -1


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

class MultiHeadMlp:
    """
    Section 320. One shared trunk, three heads.

        x -> Dense -> ReLU -> Dense -> ReLU -> LATENT
                                          |-> policy   (action logits)
                                          |-> value    (one scalar, tanh)
                                          '-> opponent (five class logits)

    THE POINT IS THE SHARED LATENT. Three separate networks would learn three
    separate pictures of the battle and share nothing; one trunk has to build
    a representation that is useful for choosing, for judging and for guessing
    - which is a far stronger pressure toward representing the things that
    actually matter than any one of the three applies alone. It is also what
    makes the eventual "if I do this, they probably do that, and the position
    is then worth this much" possible from ONE forward pass.

    It stays the project's own NumPy, with the gradients written out. §175's
    MLP was two hidden layers and a head; this is the same two hidden layers
    with three heads on top, so nothing about the training environment
    changes and no framework arrives.
    """

    def __init__(self, features, hidden=HIDDEN, actions=ACTION_SLOTS,
                 opponent_classes=OPPONENT_CLASSES, seed=170):
        rng = np.random.default_rng(seed)

        self.features = features
        self.actions = actions
        self.opponent_classes = opponent_classes

        sizes = (features,) + tuple(hidden)

        self.w, self.b = [], []

        for a, b in zip(sizes, sizes[1:]):
            # He initialization - these are ReLU layers.
            self.w.append(rng.normal(0, np.sqrt(2.0 / a), (a, b)).astype(np.float32))
            self.b.append(np.zeros(b, dtype=np.float32))

        latent = sizes[-1]

        def head(width):
            return (rng.normal(0, np.sqrt(2.0 / latent), (latent, width)).astype(np.float32),
                    np.zeros(width, dtype=np.float32))

        self.pw, self.pb = head(actions)
        self.vw, self.vb = head(1)
        self.ow, self.ob = head(opponent_classes)

    # -- the trunk ------------------------------------------------------

    def trunk(self, x, cache=None):
        """x -> latent. cache collects (pre-activation, post-activation) per
        hidden layer, which is what the backward pass needs."""
        for w, b in zip(self.w, self.b):
            x = x @ w + b

            if cache is not None:
                cache.append(x)

            x = np.maximum(x, 0.0)

            if cache is not None:
                cache.append(x)

        return x

    def forward(self, x, cache=None):
        latent = self.trunk(x, cache)

        policy = latent @ self.pw + self.pb
        value = np.tanh(latent @ self.vw + self.vb)[:, 0]
        opponent = latent @ self.ow + self.ob

        return policy, value, opponent, latent

    # -- the parameter list, in one fixed order, for the optimizer ------

    def params(self):
        return self.w + self.b + [self.pw, self.pb, self.vw, self.vb, self.ow, self.ob]

    def set_params(self, values):
        n = len(self.w)

        self.w = values[:n]
        self.b = values[n:2 * n]
        self.pw, self.pb, self.vw, self.vb, self.ow, self.ob = values[2 * n:]

    def copy_params(self):
        return [p.copy() for p in self.params()]


def huber(error, delta=1.0):
    """Huber loss and its gradient. Chosen over plain squared error because a
    value target is +/-1 and an untrained tanh sits near zero, so the first
    epochs are all large residuals - which squared error turns into large
    steps and Huber does not."""
    small = np.abs(error) <= delta

    loss = np.where(small, 0.5 * error * error, delta * (np.abs(error) - 0.5 * delta))
    grad = np.where(small, error, delta * np.sign(error))

    return loss, grad


def masked_softmax(logits, mask):
    """
    Illegal slots can never win, exactly as the evaluator masks them.

    The mask is applied to the LOGITS and not to the probabilities. Zeroing
    after the softmax leaves the illegal slots' mass distributed among the
    legal ones and, worse, leaves an argmax that can still land on an illegal
    slot when every legal logit is lower.

    Section 320: a row with NO legal action - a forced replacement recorded
    with an empty mask, and any future position that produces one - used to
    make every entry -1e9, subtract its own maximum, and come out as a uniform
    distribution by luck of the clip. It is made uniform on purpose now, so
    the behaviour is stated rather than emergent, and no row can yield NaN.
    """
    none_legal = mask.sum(axis=1, keepdims=True) <= 0

    safe = np.where(mask > 0, logits, -1e9)
    safe = np.where(none_legal, 0.0, safe)

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


def battle_split(battle_ids, fraction=0.1, seed=170):
    """
    Section 320. Split by BATTLE, never by decision.

    The old split permuted the examples, so turn 7 of a battle could train
    while turn 8 of the SAME battle validated. Consecutive turns of one battle
    are nearly the same position with nearly the same answer, so a model could
    score well on validation by having memorised the battle rather than having
    learned anything - and for opponent prediction it is worse than useless,
    because the opponent's habits within one battle are exactly what is being
    measured. No battle may appear on both sides.
    """
    rng = np.random.default_rng(seed)

    names = np.unique(battle_ids)
    rng.shuffle(names)

    held = max(1, int(len(names) * fraction))
    validation = set(names[:held].tolist())

    is_val = np.array([b in validation for b in battle_ids])

    return np.nonzero(~is_val)[0], np.nonzero(is_val)[0], len(names), held


def teacher_regret(choice, scores, mask):
    """
    Section 340. How much of the teacher's own valuation the student gave up.

    Agreement accuracy asks "did you pick the same slot". That is the wrong
    question on a decision where the teacher does not care, and it turns out
    it does not care rather often: across the recorded rankings the gap
    between its best and second-best action is under 0.01 on more than half
    of them, on a scale that runs zero to about one. A search that stops on
    a wall clock cannot reproduce its own answer on a margin that thin, so
    half the labels are a coin flip and a student is being marked wrong for
    calling it differently.

    Regret asks the question that survives a tie: of the value the teacher
    thought was on the table, how much did this pick leave there. Picking
    the other half of a dead heat costs nothing. Picking a move the teacher
    scored far below its best costs exactly what it should.

    Returned alongside the regret of choosing uniformly among the scored
    legal actions, because a regret with no scale is a number with no verb.
    """
    usable = np.isfinite(scores) & (mask > 0)
    rows = usable.sum(axis=1) >= 2

    if not rows.any():
        return None

    scored = np.where(usable, scores, -np.inf)
    best = scored[rows].max(axis=1)

    picked = scored[rows][np.arange(int(rows.sum())), choice[rows]]

    # A pick the teacher never scored is not evidence of anything - it was
    # legal and unevaluated - so it is left out rather than counted as an
    # infinite loss.
    known = np.isfinite(picked)

    if not known.any():
        return None

    mean_scored = np.where(usable[rows], np.nan_to_num(scores[rows], nan=0.0), 0.0).sum(axis=1)
    mean_scored = mean_scored / np.maximum(1, usable[rows].sum(axis=1))

    return {
        "policy_regret": float((best[known] - picked[known]).mean()),
        "policy_regret_random": float((best[known] - mean_scored[known]).mean()),
        "policy_regret_rows": int(known.sum()),
    }


def metrics(model, x, y, mask, value, opponent, from_teacher=None, scores=None):
    """Everything the run reports, from one forward pass."""
    if len(x) == 0:
        return {}

    policy, predicted_value, opponent_logits, _ = model.forward(x)

    choice = masked_softmax(policy, mask).argmax(axis=1)

    out = {
        "policy_accuracy": float((choice == y).mean()),
        "move_accuracy": _subset(choice, y, y < MOVE_SLOTS),
        "switch_accuracy": _subset(choice, y, y >= MOVE_SLOTS),
        "illegal_picks": int((mask[np.arange(len(y)), choice] <= 0).sum()),
        "value_mae": float(np.abs(predicted_value - value).mean()),
        "value_mean": float(predicted_value.mean()),
        "value_target_mean": float(value.mean()),
    }

    # Section 338. The same accuracy over the rows whose label is a
    # TEACHER'S, reported apart.
    #
    # Without this the headline number is a blend of two questions with
    # different answers. In a DAgger corpus the other half of the rows are
    # the sparring partner's own moves, and the sparring partner picks at
    # random - so half the score is measuring how well a network can
    # predict a coin flip, which is exactly the random baseline and no
    # better, forever, at any width. A ceiling that half of the number is
    # pinned to is not a ceiling anybody can lift.
    if from_teacher is not None and from_teacher.any():
        out["policy_accuracy_taught"] = float((choice[from_teacher] == y[from_teacher]).mean())
        out["taught_rows"] = int(from_teacher.sum())

        rest = ~from_teacher

        if rest.any():
            out["policy_accuracy_untaught"] = float((choice[rest] == y[rest]).mean())

    # Section 340: the same picks, scored on what the teacher thought they
    # were worth rather than on whether they matched its argmax.
    if scores is not None:
        regret = teacher_regret(choice, scores, mask)

        if regret is not None:
            out.update(regret)

    known = opponent >= 0

    if known.any():
        probs = masked_softmax(opponent_logits[known], np.ones_like(opponent_logits[known]))
        truth = opponent[known]
        order = np.argsort(-probs, axis=1)

        out["opponent_accuracy"] = float((order[:, 0] == truth).mean())
        out["opponent_top2"] = float((order[:, :2] == truth[:, None]).any(axis=1).mean())
        out["opponent_top3"] = float((order[:, :3] == truth[:, None]).any(axis=1).mean())

        predicted_switch = order[:, 0] == OPPONENT_SWITCH
        actual_switch = truth == OPPONENT_SWITCH

        out["opponent_switch_accuracy"] = float((predicted_switch == actual_switch).mean())
        out["opponent_rows"] = int(known.sum())

    return out


def _subset(choice, y, keep):
    return float((choice[keep] == y[keep]).mean()) if keep.any() else float("nan")


# Section 339. How many training rows the per-epoch training accuracy is
# measured on. A speed guard, not a statistical one - it is a random sample
# now, so more rows would only narrow an interval that is already tight.
PROBE_ROWS = 20000


def train(model, data, epochs, batch_size, learning_rate, seed=170,
          use_soft_targets=False, policy_weight=1.0, value_weight=0.5,
          opponent_weight=0.5):
    """
    Section 320. One optimizer, three objectives, one shared trunk.

        total = policy_weight   * masked cross-entropy
              + value_weight    * Huber(tanh(value), result)
              + opponent_weight * cross-entropy over the five observable
                                  opponent classes

    The three gradients meet at the latent and are added, which is the entire
    mechanism by which the heads share a representation.
    """
    rng = np.random.default_rng(seed)

    tr, val, battles, held = battle_split(data.battle_ids, seed=seed)

    print(f"  split by battle: {battles - held} training, {held} validation "
          f"({len(tr)} / {len(val)} decisions, no battle in both)")

    x, y, mask = data.x, data.y, data.mask
    value, opponent = data.value, data.opponent

    xt, yt, mt, vt, ot = x[tr], y[tr], mask[tr], value[tr], opponent[tr]
    xv, yv, mv, vv, ov = x[val], y[val], mask[val], value[val], opponent[val]

    # Section 338. Which rows are a DEMONSTRATION rather than merely a
    # legal action somebody took.
    #
    # The opponent head has always had this guard - a row with no recorded
    # opponent action has its gradient zeroed so that "a pre-320 corpus
    # trains the other two heads and leaves this one exactly where it
    # started rather than learning noise". The policy head never had it,
    # and under DAgger it needed it most: the student's seat is labelled
    # with the brain's advice, and the OTHER seat is the baseline, whose
    # every label is a move picked at random.
    #
    # On the corpus that produced this section that was 103,792 rows of
    # 210,749 - 49.2% of the policy head's training signal was a random
    # number generator presented as how to play. Under the old Observe only
    # collection it was 74%.
    #
    # So: when a corpus contains any teacher-labelled rows, the policy head
    # learns from those and no others. The value head still learns from
    # every row (an outcome is an outcome whoever played it) and so does
    # the opponent head (what the opponent did is a fact either way), which
    # is the whole reason this is a per-head gate and not a filter on the
    # corpus.
    ft = getattr(data, "from_teacher", None)

    if ft is None:
        ft = np.ones(len(x), dtype=bool)

    ftt, ftv = ft[tr], ft[val]

    if not ftt.any():
        # No teacher anywhere - a pre-182 corpus. Every row is the best
        # demonstration available, which is what it always was.
        ftt = np.ones(len(xt), dtype=bool)
        ftv = np.ones(len(xv), dtype=bool)
    else:
        print(f"  policy head: {int(ftt.sum())} of {len(ftt)} training decision(s) "
              f"carry a teacher's label ({ftt.mean():.1%}); the rest train the "
              "value and opponent heads only")

    # Section 175's soft targets, unchanged in meaning and now feeding the
    # POLICY head rather than the only head.
    if use_soft_targets and data.teacher is not None:
        soft_t, soft_rows = soft_targets(data.teacher[tr], mt)
    else:
        soft_t, soft_rows = None, None

    # Section 340: the validation rankings, for the regret figure. Always
    # available when the corpus carries rankings at all - it is a
    # measurement, so it does not wait for --soft-targets to be asked for.
    score_v = data.teacher[val] if data.teacher is not None else None
    score_t = data.teacher[tr] if data.teacher is not None else None

    # Section 339. The training-side probe: a RANDOM sample of the training
    # rows, drawn once and reused every epoch.
    #
    # It used to be xt[:20000] - the first twenty thousand training rows in
    # corpus order, which is the earliest battles in the corpus and not a
    # sample of anything. Every run of section 338's sweep reported training
    # accuracy about fourteen points BELOW validation accuracy, which is
    # backwards and cannot happen when the two are drawn from one
    # population. So they were not: the left-hand number was the first few
    # hundred battles and the right-hand number was a tenth of all of them.
    #
    # That matters beyond tidiness. "train / validation" is the pair a
    # reader uses to decide whether a model is overfitting, and it is the
    # pair that said 60.5% / 37.7% at 1024,512 in the first sweep. A
    # comparison between two different populations cannot answer that
    # question in either direction.
    #
    # Drawn from a SEPARATE generator seeded off the run's seed, so the
    # training stream is untouched: the same seed produces bit-identical
    # weights before and after this section. The battery checks that.
    probe_rng = np.random.default_rng(seed + 0x5EED)
    probe = (probe_rng.permutation(len(xt))[:PROBE_ROWS] if len(xt) > PROBE_ROWS
             else np.arange(len(xt)))

    params = model.params()
    m = [np.zeros_like(p) for p in params]
    v = [np.zeros_like(p) for p in params]
    step = 0
    best = None

    for epoch in range(1, epochs + 1):
        total_loss = 0.0
        batches = 0

        for _ in range(0, max(1, len(xt)), batch_size):
            idx = rng.integers(0, len(xt), size=min(batch_size, len(xt)))
            xb, yb, mb = xt[idx], yt[idx], mt[idx]
            vb, ob = vt[idx], ot[idx]
            n = len(yb)

            cache = []
            policy, predicted, opponent_logits, latent = model.forward(xb, cache)

            # ---- policy ------------------------------------------------
            probs = masked_softmax(policy, mb)

            target = np.zeros_like(probs)
            target[np.arange(n), yb] = 1.0

            if soft_t is not None:
                use = soft_rows[idx]
                target[use] = soft_t[idx][use]

            # Section 338: exactly the opponent head's guard, applied to the
            # policy head. A row that is not a demonstration contributes no
            # gradient and is not in the denominator either - averaging over
            # rows that were zeroed would quietly shrink the learning rate
            # in proportion to how much of the corpus is the sparring
            # partner's.
            demo = ftt[idx]
            shown = max(1, int(demo.sum()))

            policy_grad = (probs - target)
            policy_grad[~demo] = 0.0
            policy_grad /= shown

            policy_loss = -float(np.sum(target[demo] * np.log(
                np.clip(probs[demo], 1e-9, 1.0)))) / shown

            # ---- value -------------------------------------------------
            error = predicted - vb
            value_loss_terms, value_grad_terms = huber(error)
            value_loss = float(value_loss_terms.mean())

            # d/d(pre-tanh) = dHuber * (1 - tanh^2)
            value_grad = (value_grad_terms * (1.0 - predicted * predicted) / n)[:, None]

            # ---- opponent ----------------------------------------------
            known = ob >= 0

            opponent_probs = masked_softmax(opponent_logits,
                                            np.ones_like(opponent_logits))

            opponent_target = np.zeros_like(opponent_probs)

            if known.any():
                opponent_target[np.nonzero(known)[0], ob[known]] = 1.0

            # A row with no recorded opponent action contributes nothing -
            # its target is all zeroes AND its gradient is zeroed, so a
            # pre-320 corpus trains the other two heads and leaves this one
            # exactly where it started rather than learning noise.
            opponent_grad = (opponent_probs - opponent_target)
            opponent_grad[~known] = 0.0
            opponent_grad /= max(1, int(known.sum()))

            opponent_loss = 0.0

            if known.any():
                picked = opponent_probs[np.nonzero(known)[0], ob[known]]
                opponent_loss = -float(np.mean(np.log(np.clip(picked, 1e-9, 1.0))))

            loss = (policy_weight * policy_loss
                    + value_weight * value_loss
                    + opponent_weight * opponent_loss)

            total_loss += loss
            batches += 1

            # ---- backward ----------------------------------------------
            grads = {}

            grads["pw"] = latent.T @ (policy_grad * policy_weight)
            grads["pb"] = (policy_grad * policy_weight).sum(axis=0)

            grads["vw"] = latent.T @ (value_grad * value_weight)
            grads["vb"] = (value_grad * value_weight).sum(axis=0)

            grads["ow"] = latent.T @ (opponent_grad * opponent_weight)
            grads["ob"] = (opponent_grad * opponent_weight).sum(axis=0)

            # The three heads meet here. This sum IS the shared trunk.
            grad = ((policy_grad * policy_weight) @ model.pw.T
                    + (value_grad * value_weight) @ model.vw.T
                    + (opponent_grad * opponent_weight) @ model.ow.T)

            layers = len(model.w)
            activations = [xb] + [cache[2 * i + 1] for i in range(layers - 1)]

            grads_w = [None] * layers
            grads_b = [None] * layers

            for layer in range(layers - 1, -1, -1):
                grad = grad * (cache[2 * layer] > 0)

                grads_w[layer] = activations[layer].T @ grad
                grads_b[layer] = grad.sum(axis=0)

                if layer > 0:
                    grad = grad @ model.w[layer].T

            step += 1

            ordered = grads_w + grads_b + [grads["pw"], grads["pb"],
                                           grads["vw"], grads["vb"],
                                           grads["ow"], grads["ob"]]

            for i, (param, g) in enumerate(zip(model.params(), ordered)):
                m[i] = 0.9 * m[i] + 0.1 * g
                v[i] = 0.999 * v[i] + 0.001 * (g * g)
                mhat = m[i] / (1 - 0.9 ** step)
                vhat = v[i] / (1 - 0.999 ** step)
                param -= learning_rate * mhat / (np.sqrt(vhat) + 1e-8)

        report = metrics(model, xv, yv, mv, vv, ov, ftv, score_v)
        train_report = metrics(model, xt[probe], yt[probe], mt[probe],
                               vt[probe], ot[probe], ftt[probe],
                               None if score_t is None else score_t[probe])

        # Section 338: the epoch line shows the TAUGHT accuracy when there is
        # one, because that is the number the policy head is being trained
        # on. Watching the blended figure while training on half of it is how
        # a plateau that is really two different plateaus goes unnoticed.
        shown_train = train_report.get("policy_accuracy_taught",
                                       train_report["policy_accuracy"])
        shown_val = report.get("policy_accuracy_taught", report["policy_accuracy"])

        line = (f"  epoch {epoch:3d}   loss {total_loss / max(1, batches):7.4f}"
                f"   policy {shown_train:6.1%}"
                f" / {shown_val:6.1%}"
                f"   value MAE {report['value_mae']:5.3f}")

        if "policy_regret" in report:
            line += f"   regret {report['policy_regret']:5.3f}"

        if "opponent_accuracy" in report:
            line += f"   opponent {report['opponent_accuracy']:6.1%}"

        print(line)

        # Section 320: scored on ALL THREE heads, not on policy accuracy.
        # Choosing the best policy epoch on a multi-task model hands the value
        # and opponent heads whatever they happened to have at that moment -
        # a run here hit 100% policy at epoch 12 and was still improving both
        # of the others at epoch 25, and the "best" model kept epoch 12's.
        score = (policy_weight * report.get("policy_accuracy_taught",
                                            report["policy_accuracy"])
                 + value_weight * (1.0 - min(1.0, report["value_mae"]))
                 + opponent_weight * report.get("opponent_accuracy", 0.0))

        if best is None or score > best[0]:
            best = (score, model.copy_params(), report, epoch)

    model.set_params(best[1])

    return (best[2].get("policy_accuracy_taught", best[2]["policy_accuracy"]),
            best[2], best[3], (xv, yv, mv, vv, ov))


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
    """
    Section 320. THREE outputs now, from one shared trunk.

        x -> Gemm/Relu -> Gemm/Relu -> latent
                                    |-> Gemm -> policy_logits
                                    |-> Gemm -> Tanh -> value
                                    '-> Gemm -> opponent_logits

    "policy_logits" is deliberately the FIRST output, because that is the one
    an evaluator written before this section will reach for - and it is the
    one it wants. Widths come off the trained weights, not off constants, so
    a model of any schema exports correctly.
    """
    nodes, initializers = [], []
    current = "x"

    for layer, (w, b) in enumerate(zip(model.w, model.b)):
        wn, bn = f"W{layer}", f"B{layer}"
        initializers.append(_tensor(wn, w))
        initializers.append(_tensor(bn, b))

        out = f"gemm{layer}"
        nodes.append(_node("Gemm", [current, wn, bn], [out], f"gemm{layer}"))

        relu_out = f"relu{layer}"
        nodes.append(_node("Relu", [out], [relu_out], f"relu{layer}"))
        current = relu_out

    def head(prefix, w, b, output, activation=None):
        initializers.append(_tensor(prefix + "W", w))
        initializers.append(_tensor(prefix + "B", b))

        target = output if activation is None else prefix + "raw"

        nodes.append(_node("Gemm", [current, prefix + "W", prefix + "B"],
                           [target], prefix + "gemm"))

        if activation is not None:
            nodes.append(_node(activation, [target], [output], prefix + activation.lower()))

    head("P", model.pw, model.pb, "policy_logits")
    head("V", model.vw, model.vb, "value", activation="Tanh")
    head("O", model.ow, model.ob, "opponent_logits")

    graph = b"".join(_blob(1, n) for n in nodes)
    graph += _text(2, "pokemon_ai")
    graph += b"".join(_blob(5, t) for t in initializers)
    graph += _blob(11, _value_info("x", ["batch", int(model.w[0].shape[0])]))
    graph += _blob(12, _value_info("policy_logits", ["batch", int(model.pw.shape[1])]))
    graph += _blob(12, _value_info("value", ["batch", 1]))
    graph += _blob(12, _value_info("opponent_logits", ["batch", int(model.ow.shape[1])]))

    model_proto = _num(1, 8)                                   # ir_version 8
    model_proto += _text(2, "protracker-ai-training")
    model_proto += _blob(7, graph)
    model_proto += _blob(8, _text(1, "") + _num(2, 13))        # opset 13

    with open(path, "wb") as handle:
        handle.write(model_proto)


def compare_onnx(model, path, x):
    """
    Section 320. Run the exported file and check it against the weights it
    came from.

    Worth doing every run rather than once: the ONNX writer here is
    hand-rolled protobuf, and the failure mode of a hand-rolled protobuf is
    not an exception - it is a file that loads and computes something else.
    Returns a description, or None when onnxruntime is not installed.
    """
    try:
        import onnxruntime as ort
    except ImportError:
        return None

    session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])

    name = session.get_inputs()[0].name
    outputs = [o.name for o in session.get_outputs()]

    sample = x[:64].astype(np.float32)
    got = session.run(None, {name: sample})

    policy, value, opponent, _ = model.forward(sample)

    native = {"policy_logits": policy, "value": value, "opponent_logits": opponent}

    worst = {}

    for i, output_name in enumerate(outputs):
        theirs = np.asarray(got[i])
        mine = native.get(output_name)

        if mine is None:
            continue

        if theirs.ndim == 2 and theirs.shape[1] == 1 and mine.ndim == 1:
            theirs = theirs[:, 0]

        worst[output_name] = float(np.max(np.abs(theirs - mine)))

    return outputs, worst


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
    parser.add_argument("--policy-weight", type=float, default=1.0,
                        help="weight on the policy head's loss (default 1.0)")
    parser.add_argument("--value-weight", type=float, default=0.5,
                        help="weight on the value head's loss (default 0.5); "
                             "0 turns the head off")
    parser.add_argument("--opponent-weight", type=float, default=0.5,
                        help="weight on the opponent-prediction head's loss "
                             "(default 0.5); 0 turns the head off")
    parser.add_argument("--omniscient", action="store_true",
                        help="train the COMPARISON model on section 311's "
                             "all-seeing vector instead of the fair one. Only "
                             "for measuring what honesty costs - a model "
                             "trained this way has learned a game nobody can "
                             "play, and it must never be shipped")
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
    data = load_examples(corpus, args.keep_losers, args.strategy, args.omniscient)

    if args.omniscient:
        print("\n  *** OMNISCIENT CORPUS ***  these states contain the "
              "opponent's stats,\n      ability and item. The model this "
              "produces is a yardstick, not a\n      player - do not ship it.\n")

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

    # Section 319: say exactly what was left out and why, so a corpus that
    # is mostly pre-319 does not look like a corpus that is mostly empty.
    if getattr(data, "discarded", None):
        print(f"  training on schema {data.schema} "
              + ("(omniscient)" if data.schema == SCHEMA_OMNI else "(fair)")
              + f": {data.decisions} decision(s)")

        for reason, count in sorted(data.discarded.items(), key=lambda kv: -kv[1]):
            print(f"    left out: {count:>8} decision(s)  -  {reason}")

        print("    nothing above was upgraded - a state recorded under a "
              "different schema\n              is a different problem, not a "
              "shorter version of this one")

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

    # Section 320: the value head needs BOTH outcomes to learn anything. A
    # winners-only corpus is every target at +1, which a tanh fits by
    # saturating and learning nothing about position quality.
    if args.value_weight > 0 and len(data.value_signs) < 2:
        print("  NOTE: every value target has the same sign, so the value head "
              "has nothing to\n        separate. Pass --keep-losers, or "
              "collect on 'Model taught by brain' -\n        a DAgger record "
              "keeps both sides by design.")

    if args.opponent_weight > 0 and data.opponent_rows == 0:
        print("  NOTE: no decision carries the opponent's action, so the "
              "opponent head cannot\n        train. Those labels start being "
              "written by section 319 builds; a corpus\n        collected "
              "before that trains the other two heads and leaves this one "
              "alone.")

    model = MultiHeadMlp(data.width, hidden, data.actions,
                         OPPONENT_CLASSES, seed=args.seed)

    val_acc, report, best_epoch, held = train(
        model, data, args.epochs, args.batch_size, args.learning_rate,
        args.seed,
        use_soft_targets=teacher is not None,
        policy_weight=args.policy_weight,
        value_weight=args.value_weight,
        opponent_weight=args.opponent_weight)

    xv, yv, mv, vv, ov = held

    print(f"\nBest epoch {best_epoch} (chosen on all three heads together, "
          f"weighted {args.policy_weight}/{args.value_weight}/{args.opponent_weight})")
    if "policy_accuracy_taught" in report:
        print(f"  POLICY    accuracy {report['policy_accuracy_taught']:.1%} "
              f"on the {report['taught_rows']} decision(s) a teacher labelled "
              f"(random baseline {baseline:.1%})")
        print(f"            the sparring partner's own moves scored "
              f"{report.get('policy_accuracy_untaught', 0.0):.1%} and are NOT "
              "trained on - section 338")
        print(f"            blended over every validation decision "
              f"{report['policy_accuracy']:.1%}")
    else:
        print(f"  POLICY    accuracy {report['policy_accuracy']:.1%} "
          f"(random baseline {baseline:.1%})")
    if "policy_regret" in report:
        share = (1.0 - report['policy_regret'] / report['policy_regret_random']
                 if report['policy_regret_random'] > 0 else 0.0)
        print(f"            REGRET {report['policy_regret']:.4f} of the teacher's own "
              f"score, over {report['policy_regret_rows']} ranked decision(s)")
        print(f"            picking at random among the actions it scored would "
              f"give up {report['policy_regret_random']:.4f} - so this recovers "
              f"{share:.0%} of what is on the table")
        print("            (regret, not agreement, is the honest one where the "
              "teacher is indifferent - section 340)")

    print(f"            moves {report['move_accuracy']:.1%}   "
          f"switches {report['switch_accuracy']:.1%}   "
          f"illegal picks {report['illegal_picks']}")
    print(f"  VALUE     MAE {report['value_mae']:.3f}   "
          f"mean predicted {report['value_mean']:+.3f}   "
          f"mean target {report['value_target_mean']:+.3f}")

    if "opponent_accuracy" in report:
        print(f"  OPPONENT  top-1 {report['opponent_accuracy']:.1%}   "
              f"top-2 {report['opponent_top2']:.1%}   "
              f"top-3 {report['opponent_top3']:.1%}")
        print(f"            move-vs-switch {report['opponent_switch_accuracy']:.1%}   "
              f"over {report['opponent_rows']} labelled decision(s)   "
              f"(chance {1.0 / OPPONENT_CLASSES:.1%})")
    else:
        print("  OPPONENT  untrained - no decision in this corpus carried the "
              "opponent's action")

    if report["illegal_picks"] > 0:
        print("WARNING: the masked policy chose an illegal action. That should "
              "be impossible - the mask is applied to the LOGITS.")

    if val_acc <= baseline:
        print("WARNING: the model did no better than guessing. Collect more "
              "battles, or raise --epochs, before shipping this.")

    write_onnx(model, output)
    print(f"\nWrote {os.path.abspath(output)} ({os.path.getsize(output)} bytes)")

    result = compare_onnx(model, output, xv)

    if result is None:
        print("(Install onnxruntime to have this script verify the export: "
              "pip install onnxruntime)")
        return

    outputs, worst = result

    print(f"Verified: onnxruntime loads it, outputs {outputs}")

    drift = max(worst.values()) if worst else 0.0

    for name, value in sorted(worst.items()):
        print(f"  {name:<16} max difference from the trained weights {value:.2e}")

    if drift > 1e-4:
        print("WARNING: the exported model does not match the trained weights.")


if __name__ == "__main__":
    main()
