import torch
import torch.nn as nn

# Simple model
class PokemonModel(nn.Module):
    def __init__(self, input_size, output_size):
        super().__init__()
        self.net = nn.Sequential(
            nn.Linear(input_size, 64),
            nn.ReLU(),
            nn.Linear(64, 32),
            nn.ReLU(),
            nn.Linear(32, output_size)
        )

    def forward(self, x):
        return self.net(x)

# IMPORTANT: must match your encoder size
input_size = 10   # adjust later
output_size = 4   # max moves

model = PokemonModel(input_size, output_size)

# Dummy input (required for export)
dummy_input = torch.randn(1, input_size)

# Export to ONNX
torch.onnx.export(
    model,
    dummy_input,
    "pokemon_ai.onnx",
    input_names=["input"],
    output_names=["output"]
)

print("ONNX model created!")