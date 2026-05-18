"""
Python mirror of Sts2CombatAICode/Core/Planner/EffectScoringWeights.cs.

MUST stay in sync with the C# constants. When changing a weight, also bump
SCHEMA_VERSION and re-run build_pool_means.py.
"""

SCHEMA_VERSION = 1

DAMAGE_FREE = 50
DAMAGE_INHAND = 35
BLOCK_FREE = 30
BLOCK_INHAND = 25

DRAW = 70
ENERGY_FREE = 130
ENERGY_INHAND = 60

POWER_DIVISOR_FREE = 5
POWER_DIVISOR_INHAND = 7

COST_0_BONUS = 80
COST_1_BONUS = 20
COST_3_PLUS_PENALTY = -100

CURSE_FREE = -100
CURSE_INHAND = -250
