# Measured outlines — irregular-document follow-up, 2026-09-14

This note records implementation and evidence, not universal hardware acceptance.
Without a separate contour, the oriented box replaces a missing corner or notch
with background. Without the preview plan, a new scan could make an unseen crop.

## Implemented

The finder still combines luminance, colour, gradient and texture evidence. Its
oriented box supplies orientation and legacy rectangle fitting; it is no longer
the only shape the preview or scan can carry. No document-size catalogue is used
to select, resize or snap a crop. Known dimensions in tests are independent truth.

`PlatenContour` traces directed pixel boundary segments and keeps the outer loop.
It uses evidence before morphological closing because closing rounded the inner
corners of the rotated concave fixture. Interior mask holes are filled for this
outer-boundary pass. A distance-bounded simplification, local gradient profiles,
small crossing-loop removal and a second simplification produce a variable-length
polygon. The implementation is managed C# with existing framework references.

It requires 98% supported perimeter samples, rejects self-intersections that
cannot be resolved locally, and refuses detail finer than the physical sampling
can support. A nearly rectangular mask (96% oriented solidity or higher) retains
the existing four-line path; slight corner damage on those items is not yet
represented. These are documented limits, not evidence of universal recognition.

`CropRegion.Outline` carries measured source-pixel vertices. The UI draws every
vertex and records it in bed inches, separately from the orientation rectangle.
Scan maps that stored polygon through the requested bed region, deskews if enabled,
and masks excluded pixels white. Output files remain rectangular rasters. It does
not detect again or infer the shape from the scan. The extractor supports an
explicit inner ring, but automatic hole detection and a hole-editing UI are **not
implemented**. A white printed patch must not silently become a cut-out.

A passport shadow correction can replace a broad weak recovery line with a sharp
step only when an outward dark valley and recovery corroborate it. Existing narrow
stock lines are retained, including faint white borders. Missing edges next to the
image frame cannot authorise the extended search. Incompatible alternatives fall
back to an already supported original line or are rejected with a reason.

## Measurements

The synthetic free-form set contains a triangle, a concave notched sheet, a curved
outline and a 24-vertex torn outline, each at 0/15/45/75 degrees. All 16 were found
without extra items. Maximum bidirectional boundary error was **0.695 mm**; sampled
mask intersection-over-union was at least 0.97 for every placement. Measured outlines
used up to **115 points**. A separate item placed inside a notch remained separate.
This is generated geometry, not a ruler-annotated real material corpus.

| Local reference | Before this follow-up | After | Independent truth / limitation |
| --- | --- | --- | --- |
| Original two-card preview, left | 86.206 x 53.190 mm | 86.206 x 53.190 mm | Nominal ID-1 85.6 x 54: +0.606 / -0.810 mm |
| Original two-card preview, right | 85.654 x 53.341 mm | 85.654 x 53.341 mm | Nominal ID-1 85.6 x 54: +0.054 / -0.659 mm |
| Latest opened-passport preview, right edge midpoint | x=787.34 px, in smooth shadow | x=764.00 px | Local profiles show paper/cover steps at x=759..765; no ruler truth |
| Latest opened-passport preview, dimensions | approximately 7.13 x 4.97 in | approximately 6.90 x 4.97 in | Visible extent only; top edge clipped; not a claim about physical size |
| Frame-enclosed mixed bed | Whole-page false crop in original application diagnostic; one supported photo before this follow-up | One supported photo, approximately 1.71 x 2.02 in | Other items remain unresolved; this is not complete recall |

The two-card mean signed error remains **+0.330 / -0.735 mm**. Its clipped height
still fails the earlier 0.5 mm mean-error requirement. This has not been hidden by
snapping to ID-1. The separate six-item synthetic bias set remains within that
requirement. Precise physical errors and recall on the latest mixed/passport pages
cannot be established without annotations; the ruler corpus currently has zero
annotated pages. PNGs and private diagnostics remain ignored under `tests/real/`.

## Final build and timings

`build.ps1 -NoWait` exited 0 without compiler warnings. All six required suites
exited 0: crop (43 independent cases and 18 engine cases), imaging, export/batch,
cancellation, TWAIN golden (17) and eSCL (11). Existing assertions were not relaxed.
The original rectangular class/rotation sets retain 67/67 recall with zero extras,
maximum dimension error 0.624 mm and angle error 0.158 degrees. Six synthetic known
sizes report mean signed width/height errors +0.026/+0.300 mm.

Final timings on this machine: mixed preview 512 ms; its pixel-replicated 300 dpi
version 574 ms; slowest free-form detection among 16 placements 445 ms; known-size
A4 fixture 181/196 ms at 100/300 dpi. Replication is not a new hardware acquisition.
Independent colour fields and gradient baselines run concurrently into separate
buffers, with deterministic concurrent-call tests. Preparation and per-pass times
are now recorded in the diagnostic to make future slow pages diagnosable.

## Verification scope and outstanding work

Tests exercise the original AutoCropEngine self-test unchanged, arbitrary boundary
accuracy, preservation of concavity, a second item in a notch, long shadows, and
polygon extraction across grey, BGR, bilevel and padded little-endian 16-bit data.
A 23-degree plan is applied to a featureless scan at 100 and 300 dpi and a changed
bed origin: this proves extraction obeys the preview rather than redetecting. Input
buffers and 16-bit low bytes are preserved. Approved inner-ring masking is tested
as an extractor capability only. The existing 0..89-degree rectangle matrix remains
separate from free-form geometry; an irregular shape has no unique reading angle.

Still unresolved: automatic enclosed holes; small notches on otherwise rectangular
stock; contours reaching the acquisition frame; fully invisible edges; strong curl;
seamlessly touching or substantially overlapping sheets; real specular materials;
and complete recovery of the captured mixed bed. Colour/gradient support cannot
prove that a visible boundary belongs to physical stock rather than printed content.
The preview warning and candidate diagnostic remain necessary. No live scanner or
Photoshop round trip was performed in this follow-up; extraction was verified offline.

## Research used

[Suzuki and Abe, 1985](https://www.sciencedirect.com/science/article/pii/0734189X85900167)
describes border following and boundary hierarchy. The code here uses its own
directed grid-edge traversal; it is not a port of that paper's algorithm.

[Ramer, 1972](https://doi.org/10.1016/S0146-664X(72)80017-0)
provides the distance-bounded polygon-approximation approach used to retain a
variable number of vertices instead of imposing four corners.

[GrabCut, Rother et al., 2004](https://www.microsoft.com/en-us/research/wp-content/uploads/2004/08/siggraph04-grabcut.pdf)
combines colour and contrast for foreground segmentation, with interactive
initialisation in the original method. It is relevant research, not a library added
to NextScan and not an implemented automatic-recognition claim.
