# Platen detection corpus — universal work order, section 5

This protocol keeps private documents on the scanner owner's machine and prevents
synthetic dimensions from being reported as ruler measurements. `tests/real/` is
ignored by Git. Do not upload, commit, or embed any of its images in a report.

For each arrangement, measure the complete physical item, including any white
border, in millimetres. Record the placement angle with a protractor or alignment
jig; do not copy the detector's angle into the truth. Press Preview and immediately
copy both `last_preview.png` and `last_preview.txt` from
`%LOCALAPPDATA%/NextScan/diagnostics/` to `tests/real/<descriptive-name>.*` before
another preview overwrites them. Keep the existing reference files.

Add `<descriptive-name>.truth.md` alongside the PNG only when every item has been
measured. The crop suite discovers these sidecars automatically. Use this format
(the illustrative row must be replaced with actual measurements):

```text
# Arrangement, material, ruler/protractor and acquisition notes
DPI: 100

| Item | Width mm | Height mm | Angle degrees | Centre X fraction | Centre Y fraction |
| --- | --- | --- | --- | --- | --- |
| 1 | 85.6 | 54 | 30 | 0.25 | 0.30 |
```

Centre fractions identify the item approximately in the whole raster; these are
annotation coordinates, not the crop plan. The production crop plan remains in
bed inches. Angles in this suite are geometric modulo 90 degrees: a rectangle
alone cannot establish the reading direction of its text. Dimensions are compared
as long and short sides. A count mismatch, unmatched position, size error over
1 mm, or angle error over 0.5 degrees fails the suite. Incomplete truth sidecars
fail rather than silently skipping. No sidecars means an explicit missing-corpus
note, not successful hardware acceptance.

Required capture matrix:

- ID-1 cards, closed and opened passport booklet, matte/glossy photographs with
  and without borders, A4/A5, receipts and irregular/torn stock.
- 0, 5, 15, 30, 45, 60 and near 90 degrees; include the same item at 0 and 45.
- 1, 2, 4 and 6 items, corner contacts, full-edge contacts, corner overlaps,
  and one item in each corner.
- White stock on white, dark stock on dark, reflective cover, and a lifted spine.

Record absent/occluded physical boundaries explicitly. A scanner cannot recover
hidden pixels or prove an invisible edge. These cases remain acceptance failures
for full physical dimensions even if the visible extent is usefully outlined.

Current private mixed preview: copied from the 2026-09-12 14:52:54 diagnostic.
Its complete inventory, ruler measurements, material descriptions and placement
angles have not been supplied. It is exercised for reproducibility and timing,
but does not count as a ruler-verified corpus page.
