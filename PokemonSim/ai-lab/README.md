# ai-lab: the original deep-learning material, preserved

Everything in this folder is the author's own work from the original
PokemonSim project (recovered in MIGRATION_GUIDE.md section 156 from
C:\Users\DeepF\PokemonSim), kept byte-for-byte and deliberately OUTSIDE
the build: pokemonsim.csproj has excluded `ai-lab\**` from compilation
since the project arrived, and these files target the pre-section-154
engine APIs anyway.

What each file is:

- `AI/BattleStateEncoder.cs` - the ten-float feature encoder. The live
  observer's ObserverEncoder (Observation/) reimplements exactly this
  layout, perspective-generalized.
- `AI/TrainingDataWriter.cs` - the training-example format:
  (state[10], move index, final outcome) per decision, appended as JSON.
  The observer's JSONL schema is a superset; section 156 of the guide
  documents the exact mapping back to this shape.
- `AI/NeuralNetworkAI.cs` - the neural battle class: loads
  DataFiles/pokemon_ai.onnx. Its ChooseMove never actually ran inference
  (with a session it picked randomly; without one, a heuristic) - the
  inference call now exists in PokemonSim.Shadow/OnnxShadowEvaluator,
  observer-only.
- `AI/MonteCarloAI.cs` - the original Monte Carlo chooser (per-move
  rollouts on clones, win-fraction scoring, an IsSimulating flag marking
  rollouts as not-real-battles). Completed as
  Engine/Strategies/MonteCarloStrategy.cs in section 155.
- `AI/SelfPlayTrainer.cs` - Monte-Carlo-as-teacher self-play: encode
  state, let MC choose, record, play on, then write the history with the
  result. The section-156 observer is this loop generalized to the
  visible Simulator battle.
- `AI/BattleEvaluator.cs` - the heuristic position evaluator (team HP +
  type advantage).
- `AI/BattleInitializer.cs` / `AI/Program.cs` - the original ability
  initializer and console entry point (rebuilt in section 154).
- `AI/TeamGenerator.cs`, `Models/ConvertedMove.cs` - empty placeholders,
  kept as found.
- `Train_Model.py` - the PyTorch definition and ONNX export
  (10 -> 64 -> 32 -> 4). NOTE: it has no training loop yet - it exports
  the freshly-initialized network, so pokemon_ai.onnx currently carries
  untrained weights.
- `pokemon_ai.onnx` - the author's exported model (also shipped to the
  app output as PokemonSimData/pokemon_ai.onnx for the shadow
  evaluator). Input tensor "x" float[1,10], output "linear_2" [1,4].
- `original-pokemonsim.csproj.txt` - the original project file, showing
  the live OnnxRuntime reference and the model's CopyToOutputDirectory
  entry.

Do not edit these; new work goes in PokemonSim/Observation,
PokemonSim.Shadow, and (future) a real training loop fed by the
observer's records.