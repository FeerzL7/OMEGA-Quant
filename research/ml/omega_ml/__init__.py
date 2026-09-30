"""OMEGA Quant research pipeline (Phase 7): supervised learning on datasets exported by the C# system.

Python never recomputes features or labels. It consumes `omega-dataset-v1` files produced by
`POST /api/datasets`, verifies them against their manifest, and trains/evaluates models chronologically.
"""
