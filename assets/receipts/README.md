# Contoso receipt corpus (version 1)

Synthetic receipts for the multimodal-documents samples (`Ch12.ReceiptExtraction`, `contoso/v4-receipts`). Every vendor, number and line is invented; no real person, card or company appears. License: CC0-1.0.

| Document | Pages | What it tests |
|---|---|---|
| `taxi-clean` | 1 | the baseline: every field readable, lines add up to the total |
| `taxi-rotated` | 1 | the same receipt photographed sideways |
| `taxi-low-contrast` | 1 | faint print and a smudged total that a person cannot read either |
| `taxi-conflicting-totals` | 1 | a printed total and a different "amount charged" |
| `taxi-hostile-instruction` | 1 | an instruction to AI assistants printed on the receipt |
| `hotel-two-pages` | 2 | lines on both pages, the total only on page 2 |

`corpus.json` holds the ground truth: what a careful person reads on each image (`null` when nobody can). Regenerate the images with `python tools/make_receipts.py` (Pillow, bundled Aileron font, CC0). Change the version number when an image changes, because captured model runs refer to it.
