# Platen detection: research and evidence, 2026-09-17

This is the research follow-up to the universal platen work order, sections 4–7.
Without independent boundary hypotheses, a shadow-connected component can own
several documents and prevent their physical edges from ever being considered.
This note distinguishes implemented changes from methods considered, and measured
results from assumptions. Private images have remained on this machine.

## What the research supports

| Primary source | Relevant finding | Decision for NextScan |
| --- | --- | --- |
| [Tropin et al., Contours and Contrasts, ICPR](https://arxiv.org/pdf/2008.02615) | Competing quadrilaterals can be evaluated with boundary support, continuation beyond corners, and inside/outside colour distributions. The paper assumes a single document; its benchmark results are not flatbed guarantees. | Use complete-perimeter checks and evidence outside the proposed stock. Penalise boundaries whose opposite edges continue across an alleged side. Keep multiple hypotheses rather than committing to a connected component. Our exterior texture test is not the paper's trained colour-histogram ranking function. |
| [Grompone von Gioi et al., LSD, IPOL](https://www.ipol.im/pub/art/2012/gjmr-lsd/) | Locally aligned gradient regions can propose line segments independently of connected foreground masks. LSD also contains statistical false-alarm validation. | Implement our own managed gradient-chain proposals. We do not implement LSD's statistical test and do not claim its false-alarm guarantee. No reference implementation was copied. |
| [Rother et al., GrabCut, Microsoft Research](https://www.microsoft.com/en-us/research/wp-content/uploads/2004/08/siggraph04-grabcut.pdf) | Colour models and contrast-aware graph cuts refine an object given an initial region and user constraints. | Not a replacement for finding the number and locations of unknown sheets. Automatic seeds remain the unresolved problem when print, backing and stock have similar colours. No graph-cut implementation was added in this round. |
| [Meyer and Beucher, Morphological Segmentation](https://www.sciencedirect.com/science/article/pii/104732039090014M) | Marker-controlled watershed addresses oversegmentation through explicit object markers. | Potentially useful for touching objects with independently established seeds. Unconstrained watershed could turn photograph content into false documents, so it was not introduced as a blanket split operation. |
| [Multi-document detection via corner localization and association](https://doi.org/10.1016/j.neucom.2021.09.033) | Learned corner association targets document instances and their count. | Requires a trained model and inference implementation; quadrilateral instances do not cover torn or concave stock. It is not an inbox .NET replacement under this work order. |
| [Kirillov et al., Segment Anything, ICCV](https://openaccess.thecvf.com/content/ICCV2023/papers/Kirillov_Segment_Anything_ICCV_2023_paper.pdf) | Promptable masks support general shapes and explicitly account for ambiguous object/part interpretations. | A possible future model-based option with different deployment constraints, not evidence that arbitrary physical stock is automatically identifiable. No model, external runtime or network inference was added. |

The engineering conclusion is a combination of independent proposals, physical
edge refinement, full-boundary validation and explicit uncertainty. A named
algorithm alone cannot identify an invisible border or recover occluded geometry.
Known-size snapping is not used by this implementation.

## Implemented

`PlatenBoundaryProposals` groups locally aligned signed colour gradients at two
contrast levels. Keeping strong and faint chains separate prevents weak shading
from bending an otherwise useful edge. Opposite chains and nearby perpendicular
chains produce candidate orientations and extents, without a document catalogue.
The gradient operator's response is normalised correctly: its magnitude at a step
is half that step's contrast. Shared gradient planes and a background preparation
task avoid recomputing the same derivatives for each contrast level.

The existing component/contour path runs first. Already supported stock boundaries
retain their authority. New candidates undergo the physical line fitter, support
checks over each quarter of each side, and a local exterior-texture check. A linear
illumination ramp is removed from the texture measurement because a cast shadow
is not printed content. Two opposite borders continuing beyond the same side are
evidence of an internal division, such as a passport spine or a printed panel.

A weak enclosing result can be superseded only when at least two separate,
independently supported boundaries contradict its incomplete perimeter. An
explicit frame-limited large-sheet result and a measured free-form outline are
not removed by that rule. Supported stock is not merged merely because a larger
rectangle encloses it. The former recursive gap re-detection experiment was
removed; it repeated the same failure and added latency.

Physical refinement also resolves a narrow dark valley between similar bright
plateaus to its inward half-height transition. This corrects the outer-shadow
bias exposed by an arbitrary-size rotated white-border test. It measures an
observed transition; it does not add or subtract a fixed dimensional allowance.
Broad shadows, unequal plateaus and unsupported profiles do not qualify.

Preview remains the only detection caller. Scan uses the preview's stored inches
and polygon plan. Variable-length outlines, native pixel layouts, extraction,
Photoshop page separation and the original AutoCropEngine algorithms are unchanged.
Confidence remains an evidence category, not a calibrated probability of correctness.

## Verification scope

The new arbitrary-size fixture uses 71.3 × 43.7 mm white-bordered stock and
39.1 × 52.7 mm stock at 0/19/45/73 degrees, with an independently placed companion.
It checks eight detections, no extra items, both dimensions within 1 mm, angle
within 0.5 degrees, mean signed error below 0.5 mm, and unchanged source pixels.
Measured worst error is **0.074 mm**, worst angle error **0.098 degrees**, mean
signed long/short errors **+0.016/+0.017 mm**. These are analytical raster truths,
not ruler measurements of real material.

The newest private mixed-bed regression checks all five visible items, both
nominal ID-1 dimensions, the two portrait locations and the passport's visible
outer extent. The older frame regression now also rejects a narrow passport
panel being substituted for the complete booklet. Existing assertions were not
relaxed to pass the new implementation. Final run results and physical dimension
tables are recorded in `STATUS.md`.


## All-angle follow-up — 2026-09-19

The five-level thin-shadow fixture failed at 27 degrees: the returned dimensions
were the printed panel, approximately 16 mm short in each axis. Raster staircases
both attenuated the Sobel magnitude and varied its normal beyond the chain-growth
window. A separate faint extension pass admits magnitude 1.5 and a 30-degree
growth window, then retains the existing 1.5-pixel line-residual and full-perimeter
validation. It only retains chains with mean squared magnitude below the strong
pass floor. Strong and original faint hypotheses are fitted first; supplemental
chains can recover larger stock but cannot displace an already supported fit.
Applying the wider window to strong print was tested and rejected because it
regressed the private tilted card. No existing test tolerance was relaxed.

The new test checks every integer angle 0..89 on non-standard 71.3 x 43.7 mm
stock, including its 8 mm white surround. It returns 90/90 with zero extras:
worst dimension error 0.180 mm, angle 0.308 degrees, signed long/short means
-0.035/-0.041 mm. These are raster truths, not ruler measurements.

The older missing tilted card had another cause: contrast near a corner was
being treated as a continuation of the border without testing direction and
polarity. The continuation test now requires both. Seed preflight allows a
1.5 mm search band before fitting rounded/spine edges; final fitted support
retains its strict two-working-pixel band. A regression distinguishes nearby
diagonal printing from two genuinely continuing rules. Independent mask
hypotheses and independent proposal fits use bounded parallel work; result and
diagnostic merging retain deterministic hypothesis order.
## Remaining limits

The real ruler corpus still contains **zero annotated pages**. The latest two
mixed beds give five visible items each, but their portrait/passport dimensions
and placement angles have no independent ruler truth. The opened passport in the
September 16 acquisition reaches the image boundary: only its visible extent can
be measured. The older September 12 frame-enclosed bed remains incomplete; a
partial printed passport crop is refused rather than called a recovered item.

The supplemental chain path needs roughly straight visible runs at least 8 mm
long. It caps the retained chains to bound work on dense print. It is not a new
free-form segmentation engine, and it can refuse a genuine item when adjacent
objects continue its edges. Existing polygon detection still has its documented
limits on small notches, image-frame contact and automatic enclosed holes.
The wide-shadow recovery is still limited to near-horizontal stock near the
lower acquisition edge. Curl, specular stock, invisible edges, substantial
overlap and seamless contact are not universally solved.

No claim of 100% real-corpus recall, sub-millimetre physical accuracy for every
material, or world-best performance follows from these tests. No live scanner or
Photoshop round trip was performed in this research follow-up.
