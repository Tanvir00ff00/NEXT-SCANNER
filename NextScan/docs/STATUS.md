# NextScan Studio — Build Status

Last updated: 2026-09-19
Plan of record: [`NEXTSCAN_STUDIO_MASTER_PLAN.md`](../../NEXTSCAN_STUDIO_MASTER_PLAN.md)

This file records what is **actually built and verified on hardware**, versus what
is still only planned. It is deliberately conservative: if something is not listed
as verified below, assume it does not work yet.

---

## Field regression: white portrait and passport panel — 2026-09-19

The new private 15:38:54Z preview reproduced the owner's screenshots: four
regions instead of five, a missing white portrait, and a 2.76 x 4.80 in passport
print panel returned as a complete item. The original local PNG and diagnostic
were preserved in ignored `tests/real/lide400_mixed_20260919.*`.

A faint physical side survived only over part of its length. The opposite-chain
seed stopped there because cross-edge extension was capped at 6 mm, although
an observed perpendicular stock edge was farther away. Extension now follows
that measured cross-edge up to the original seed span. The cross-edge must
cover at least 40% of the paired separation: a short neighbouring photograph
cannot extend a booklet to its own top edge. Perimeter validation and fitting
still decide acceptance; no document size is assumed.

The opened passport reaches the acquisition bottom. Blanket rejection of any
frame contact, and a preflight requiring contrast beyond the raster, discarded
its full-width hypothesis. A single clipped side is now allowed as visible
extent at review confidence. Seed preflight allows the existing 6 mm fit band
there; final support exempts only an actual 2-pixel frame edge and still measures
the other sides. The supported full stock suppresses nested printed panels.

| Captured item | Before | After |
| --- | --- | --- |
| White portrait | Missing | Separate 1.72 x 2.06 in region |
| Open passport | Inner panel, 2.76 x 4.80 in | Full visible width, 6.90 x 4.89 in |
| Total | 4 regions | 5 regions at the five physical locations |

These values describe measured output, not ruler-certified physical dimensions.
The pre-existing tilted-card width remains about 3.42 in (roughly +1.3 mm against
nominal ID-1), so this page is not a successful universal accuracy benchmark.
Its passport bottom is clipped and its true full height is unavailable.

Verification: `build.ps1 -NoWait` exited **0**, with no compiler warnings.
All six suites exited **0**: crop **50 independent + 18 engine cases**,
imaging, export/batch, cancellation, TWAIN golden **17**, eSCL **11**.
Logs: `out/field19_build.txt`, `out/field19_final_exits.txt` and the six
`out/field19_final_*.txt` files. Preview-only detection and bed-inch polygon
extraction are unchanged.

New tests: `platen_mixed_20260919` requires the five locations, white portrait
and full passport width; `platen_partial_boundary_chains` independently verifies
fragmented side recovery and a clipped visible extent within 1 mm. Existing
assertions were not relaxed. Final crop timing for the new reference was
**750 ms**, above the 600 ms target; this regression prioritises recovering the
missing physical boundaries and does not claim the latency target is met.

---
## Universal detection follow-up — 2026-09-19

The older September 12 mixed bed now returns **5/5 visible items**, up from
**3/5** in the preceding entry. Seed preflight was demanding final edge precision
before refinement could run; nearby diagonal print was also being mistaken for
a continuation of the tilted card's borders. Seed search now allows 1.5 mm of
local uncertainty, and continuing borders must agree in direction and polarity.
Final boundary validation remains strict. The frame regression now requires all
five physical locations and both nominal ID-1 dimensions within 1 mm; no existing
assertion was weakened.

A new non-standard white-bordered stock test exposed a separate 27-degree miss:
the detector returned the inner picture, approximately 16 mm short in each axis.
A separate faint-gradient chain pass preserves the weak raster staircase without
loosening the strong-print path. Stronger validated boundaries retain priority;
faint chains can recover a larger outer surround. Independent mask preparation
and proposal fitting now use bounded parallel work with deterministic merging.
No standard-size catalogue or snap is used. Preview still decides; scan only
extracts the stored bed-inch polygons.

| Validation cohort | Returned / present | False items | Worst dimension/boundary error | Worst angle error |
| --- | --- | --- | --- | --- |
| White-bordered arbitrary stock, every integer angle 0..89 | 90/90 | 0 | 0.180 mm | 0.308 degrees |
| Universal synthetic classes | 56/56 | 0 | 0.624 mm | 0.158 degrees |
| Rotation invariance | 11/11 | 0 | 0.256 mm | 0.007 degrees |
| Arbitrary sizes with companions | 8/8 | 0 | 0.074 mm | 0.098 degrees |
| Irregular triangle/concave/curve/torn placements | 16/16 | 0 | 0.695 mm boundary | Not a rectangular angle benchmark |
| Three five-item private beds, September 12/14/16 | 15/15 visible | 0 in location/count regressions | Ruler truth unavailable | Protractor truth unavailable |

The 90-angle stock cohort has signed long/short means **-0.035/-0.041 mm**;
the arbitrary-size companion cohort has **+0.016/+0.017 mm**. Polygon tests use
up to 115 outline points. Those are analytical raster measurements. The original
two-card acquisition still has **-0.735 mm mean signed height error** because its
top edges are clipped; that failed bias target remains a known limitation.

| Reference item | Before this follow-up | After, mm | Signed error against nominal ID-1, mm |
| --- | --- | --- | --- |
| September 12 near-horizontal card | Found; rounded 3.37 x 2.12 in | 85.505 x 53.946 | -0.095 / -0.054 |
| September 12 tilted card | Missed | 85.695 x 53.946 | +0.095 / -0.054 |
| September 12 passport | Missed | 175.428 x 125.024 | Ruler truth unavailable |
| September 16 tilted card | 85.675 x 54.440 mm | 85.675 x 54.440 | +0.075 / +0.440 |
| September 16 near-horizontal card | 85.572 x 54.025 mm | 85.572 x 54.025 | -0.028 / +0.025 |

The September 12 nominal card cohort has mean width/height errors
**0.000/-0.054 mm**. A missed item's dimensional error is undefined, not zero.
The synthetic 27-degree white-border failure changed from an approximately
55.3 x 27.7 mm inner picture to the 71.3 x 43.7 mm stock within the all-angle
errors above. No physical passport size was assumed from its document class.

Final build exited **0**, with no compiler warnings. All six suites exited **0**:
crop **48 independent cases + 18 engine cases**, imaging, export/batch,
cancellation, TWAIN golden **17**, eSCL **11**. Logs:
`out/universal_completion_build.txt`, `out/universal_final_exits.txt` and the six
`out/universal_final_*.txt` logs. The final crop run measured September 14 at
**593 ms**, and September 16 at **430/473 ms** preview/replicated300.

Isolated timing samples, three calls per page, include the process's first call:

| Private reference | 100 dpi preview, ms | Replicated 300 dpi, ms |
| --- | --- | --- |
| September 12 full-frame mixed bed | 623, 539, 529 | Not sampled |
| September 14 mixed bed | 521, 512, 519 | 515, 522, 528 |
| September 16 mixed bed | 347, 367, 381 | 400, 373, 398 |

All sampled calls return five items. The first cold call at **623 ms** exceeds
the 600 ms preview target by **23 ms**; latency compliance is therefore not
unconditional. Replicated300 is a scaling test, not a new optical scan. Detailed
dimensions and angles are in `out/universal_measurements.txt`.
The new regression names are `platen_boundary_context` and
`platen_white_border_all_angles`. The ruler corpus still has zero annotated pages;
its explicit skip is not hardware acceptance. Invisible boundaries, seamless
contact, substantial occlusion, curl and reflective surfaces remain unresolved
classes. A universal claim requires the local ruler/protractor capture matrix in
[PLATEN_CORPUS.md](PLATEN_CORPUS.md), including independent physical truth. No new
live lamp scan or Photoshop round trip was performed.

---
## Boundary hypotheses and research follow-up — 2026-09-17

The September 16 field preview returned two rectangles: one tilted card and one
8.39 x 9.69 in false enclosure covering several other items. Reproducing that
failure showed that fitting could move a candidate onto the platen frame, and a
first accepted enclosure then suppressed the real boundaries within it. The
starting checkout also failed two crop cases (thick-shadow boundary and the new
September 16 mixed bed); these were recorded in `out/research_baseline.txt`.

The [research note](PLATEN_RESEARCH.md) compares contour/contrast ranking, line
segments, GrabCut, watershed, learned document corners and promptable masks.
The implemented change uses independent strong/faint gradient chains, full-side
support, exterior texture after illumination-ramp removal, and rejection of
borders that continue across an alleged stock corner. Supported component and
free-form results retain priority. Multiple independently supported boundaries
can invalidate an incomplete enclosing fit; printed panels cannot automatically
become separate pages. This is original managed code, with no new dependencies,
size catalogue, model inference or scan-time detection. Failed recursive gap
re-detection was removed. Candidate decisions remain in the preview diagnostic.

A new non-standard-size test exposed two further causes: comparing a normalised
Sobel derivative with an unnormalised step threshold lost faint stock, and a
broad derivative sometimes selected the outside of a narrow shadow. Gradient
normalisation and a measured inward shadow-valley transition correct those cases.
The arbitrary-size test passes all eight objects at 0/19/45/73 degrees with no
extras: worst dimension **0.074 mm**, angle **0.098 degrees**, mean signed long/
short errors **+0.016/+0.017 mm**. The 16 irregular-shape tests remain separate;
this supplemental rectangular path does not replace their polygon outlines.

| September 16 item | Original diagnostic | Current measured output | Error against nominal ID-1 |
| --- | --- | --- | --- |
| Tilted card | 3.37 x 2.14 in, rounded diagnostic | 85.675 x 54.440 mm | +0.075 / +0.440 mm |
| Near-horizontal card | No independent region | 85.572 x 54.025 mm | -0.028 / +0.025 mm |
| Left tilted portrait | No independent region | 43.574 x 52.657 mm | Ruler truth unavailable |
| Right upright portrait | No independent region | 39.885 x 50.214 mm | Ruler truth unavailable |
| Opened passport | No independent region | 175.054 x 115.224 mm visible extent | Lower edge clipped; ruler truth unavailable |
| False multi-item enclosure | 8.39 x 9.69 in | Rejected | Not a physical item |

All five visible items now have separate regions on this captured page. Its two
cards have mean signed width/height error **+0.024/+0.233 mm**, worst **0.440 mm**
against nominal ID-1. The September 14 five-item page retains its earlier card
measurements **85.686 x 54.147** and **86.238 x 54.247 mm**, mean signed
**+0.362/+0.197 mm**. No standard-size snapping is applied. The oldest two-card
preview remains **86.206 x 53.190** and **85.654 x 53.341 mm**: its clipped-height
mean **-0.735 mm** still fails the earlier 0.5 mm bias target and is not concealed
by combining it with another cohort.

Final engine timing samples, three runs each, including the first call in the
measurement process: September 14 **506–584 ms** at 100 dpi, **505–606 ms** at
replicated 300 dpi; September 16 **451–496 ms** and **479–524 ms** respectively.
All return five items. Replication is a scale check, not a fresh scanner capture.
The newest replicated image changes dimensions by at most **0.425 mm** and angle
by **0.238 degrees**; its regression requires count/order stability and the
1 mm / 0.5 degree scale tolerances. Shared derivative planes and preparation
concurrent with mask work removed the repeated gradient calculation; earlier
experimental versions exceeded the preview time budget.

Final verification: `build.ps1 -NoWait` exited **0**, with no compiler warnings.
All six suites exited **0**: crop (**46 independent cases plus 18 engine cases**),
imaging, export/batch, cancellation, TWAIN golden (**17**) and eSCL (**11**).
Results are in `out/research_final_exits.txt` and the six corresponding logs.
Existing assertions were not relaxed: the older frame case was strengthened to
reject narrow passport print panels, and the new mixed case requires the five
physical locations rather than count alone. The final suite measured the newest
preview/replicated300 pair at **435/536 ms**. It also measured the September 14
preview at **652 ms** despite the isolated 506–584 ms samples above; the 600 ms
preview target is therefore **not met consistently on every reference**. This
remaining latency overrun is reported rather than omitted from the timing table.

**Not universal acceptance:** the ruler-annotated real corpus still has zero
pages. Real portrait/passport size and placement-angle errors cannot be certified.
The older September 12 frame-enclosed bed returns **3 of its 5 visible items**;
the tilted card and passport remain unresolved, and partial passport panels are
refused. Dense print, invisible edges, substantial occlusion, curl and specular
materials remain limitations. No live lamp scan or Photoshop round trip was
performed. Preview-only detection and inch-based polygon extraction are unchanged.

---

## Mixed-bed replacement and missing passport — 2026-09-15

The private 2026-09-14 14:56:24Z preview reproduced two separate faults. The first
card had a supported 3.37 x 2.13 in fit, but a later 3.82 x 2.91 in frame-connected
hypothesis superseded it. A candidate relying on an unmeasured image-frame edge
can no longer replace an already observed complete stock boundary. Passport and
lid shading were connected to the portrait area, so no isolated passport candidate
reached the fitter.

A bounded recovery now uses a wide, near-horizontal shadow transition preceded
by a quiet exterior. It selects the best-supported row in the shadow band and
passes that proposal through the existing independent edge fitter. It uses no
passport dimensions. This path retains review confidence and honours the requested
confidence/area limits. It is not an arbitrary-orientation or curl solution: the
observed shadow must span at least 60% of the bed width and the lower edge must be
within the fitter's search band near the bottom of the acquisition.

| Region on this reference | Before | After |
| --- | --- | --- |
| First card | 3.82 x 2.91 in, frame/shadow included | 3.37 x 2.13 in |
| Second card | 3.40 x 2.14 in | 3.40 x 2.14 in |
| First portrait | 1.79 x 2.04 in | 1.79 x 2.04 in, review |
| Second portrait | 1.72 x 2.12 in | 1.72 x 2.12 in, review |
| Opened passport | Missed | 6.90 x 4.92 in, review |

All five visible items are now returned on this page. Both cards are within 1 mm
of nominal ID-1. Portrait and passport ruler dimensions and placement angles are
still unknown; the returned visible dimensions are not certified physical sizes.
The private reference and its decision log stay in ignored `tests/real/`.

Sliding-window morphology preserves the old neighbourhood and border rules while
avoiding repeated neighbour counts. Independent edge profiles run concurrently
into separate accumulators. The final crop run measured this preview at **506 ms**
(the original application's diagnostic was 620 ms). The new private regression
asserts five items, both card sizes, and the passport's approximate visible location;
no existing assertion was relaxed. Preview-only detection, bed-inch plans, polygon
extraction and one page per detected item are unchanged.

Final verification: build exited 0 without compiler warnings; all six suites exited
0: crop **44 independent cases plus 18 engine self-tests**, imaging, export/batch,
cancellation, TWAIN golden **17**, and eSCL **11**. A nearest-neighbour replicated
300 dpi version of this new page returned the same five items in **592 ms**; this
is a resolution test, not another hardware acquisition. Card measurements were
**85.686 x 54.147** and **86.238 x 54.247 mm**, signed errors **+0.086/+0.147** and
**+0.638/+0.247 mm** against nominal ID-1. Mean signed error across these two cards
is **+0.362/+0.197 mm**. The older clipped two-card reference retains its separately
documented height-bias limitation. Passport/portrait physical truth remains unknown.

## Free-form preview outlines and passport shadow — 2026-09-14

Unknown sizes no longer require a four-corner crop. The managed contour path
traces unclosed evidence, fits local gradients and carries a variable-length
outline through the preview and the bed-inch scan plan. The UI draws the measured
vertices with review confidence. Scan only applies that stored mask; pixels outside
it are white. No standard-size snapping or new scan-time detection was added.

Sixteen synthetic triangle, notched, curved and torn placements at 0/15/45/75
degrees are found with no extras: worst boundary error **0.695 mm**, up to **115
points**. A second item inside a notch remains separate. Rotated mask extraction at
100/300 dpi and a changed bed origin passes on featureless source pixels, including
padded 16-bit low-byte preservation. The existing rectangular tests remain intact.

On the latest private passport preview, a smooth shadow recovery had been fitted
at right-edge midpoint **x=787.34**. The supported sharp paper/cover step is now
**x=764.00**, with approximate visible output **6.90 x 4.97 in** rather than
**7.13 x 4.97 in**. This follows image evidence, not a passport size. Ruler truth is
absent and the top is clipped. The original two-card errors remain **+0.606/-0.810**
and **+0.054/-0.659 mm**; mean height error **-0.735 mm** remains outside the earlier
0.5 mm requirement. The full-frame mixed reference still yields only its supported
upper-right photo: complete mixed-bed recall is **not solved**.

Automatic enclosed holes, very small corner damage on nearly rectangular stock,
substantial overlap/curl and invisible boundaries remain limitations. The extractor
can mask a supplied inner ring, but the detector and UI do not yet create those
rings. Real universal recall and physical-angle accuracy remain unverified with
zero ruler-annotated corpus pages. [Implementation, measurements and research](PLATEN_CONTOURS.md)
record these limits and the before/after reference table. Private pages stay local.

Final verification: build exited 0 with no compiler warnings; all six suites exited
0: crop **43 cases plus 18 engine self-tests**, imaging, export/batch, cancellation,
TWAIN golden **17**, and eSCL **11**. No existing assertion was relaxed. Final mixed
preview timing was **512 ms**, its pixel-replicated 300 dpi page **574 ms**; the
slowest of the 16 free-form detections was **445 ms**. The known-size A4 fixture
took **181/196 ms** at 100/300 dpi. The rectangular class/rotation sets retain
**67/67 recall, zero extras**, worst dimension **0.624 mm**, angle **0.158 degrees**;
the six-item synthetic signed bias is **+0.026/+0.300 mm**. These generated cases
do not establish universal real-world acceptance.

## Full-frame false crop — 2026-09-13

The new private LiDE diagnostic dated 2026-09-12 15:23:28 reproduced a regression
introduced in the universal finder: an enclosing scanner frame was hole-filled,
then accepted as an **8.50 x 11.69 in** sheet because its interior contained texture.
That first accepted region suppressed all subsequent candidates as duplicates.

Frame contact plus interior texture no longer authorises a sheet. Only the separate
observed full-span stock-edge path can do that. Before filling holes, the finder
measures sustained mask support near the frame and removes the enclosing contour
within a bounded 3 mm search. Per-item sealing refuses three-sided contours, and
the measured removed inset remains part of the frame-contact check. Otherwise the
same false frame would reappear a few pixels inward as an apparently isolated item.

The synthetic enclosing-frame test retains both cards within 1 mm. On this exact
new real page the full-page false crop is refused and the upper-right photo is
returned separately, approximately **1.71 x 2.02 in**. **This is not complete
mixed-bed recovery**: the other photo fails opposite-edge agreement, and card /
passport shadows remain merged. Their reasons are in the diagnostic and the UI
requests review. Do not report this page as five correctly detected items.

An experimental 8 mm frame search returned three incorrect rectangles (including
a 2.16 x 0.49 in strip); it was rejected and is not in the final code. The new real
regression therefore checks that the known upper-right region survives, the platen
is never returned, and unresolved candidates are reported. It does not equate a
higher rectangle count with recall. No pre-existing assertion was changed.
The private PNG and its before/decision text remain under ignored `tests/real/`.

Final verification: build exited 0 without compiler warnings. Crop (39 independent
cases plus 18 engine self-tests), imaging, export/batch, cancellation, TWAIN golden
(17) and eSCL (11) all passed. This validates the false-frame fix; the real mixed-bed
recall limitation above remains open.

## Universal platen finding — 2026-09-12, partial hardware acceptance

The finder now measures solidity against a convex hull's minimum-area rectangle,
then measures edges in that orientation. The former 55% axis-aligned gate rejected
a clean 45-degree card, and the old edge search stopped at about 11 degrees.
Independent 6/14/28-level and gradient-only hypotheses now separate a contrast
failure from a geometric failure. Colour residuals and local variance corroborate
weak luminance evidence. Percentiles use histograms rather than sorting every
pixel. Corner-contact cores provide separation proposals; fitted gradients restore
their boundaries. A broader gradient baseline can measure softened transitions
at lower confidence. Frame contact needs additional interior evidence; a printed
three-sided sheet is retained as a **visible extent**, not an inferred full size.

Each call owns a candidate report. Rejections, duplicates, superseded inner
boundaries, failed edges, confidence filtering and region limits appear in the
preview diagnostic. Unresolved hypotheses trigger a preview status warning.
These counts are hypotheses across passes, not a count of missing documents.
Preview-only detection, bed-inch plans and scan extraction are unchanged.

Latest private mixed preview (850 x 1169, 100 dpi, diagnostic dated 2026-09-12
14:52:54) was preserved locally before changes. Before: **2 regions, 203 ms** in
its original application diagnostic. After: **4 regions, 446 ms** offline; a
pixel-replicated 300 dpi version took **469 ms** with identical dimensions/count.
This is a resolution/timing test, **not a new 300 dpi hardware acquisition**.

| Mixed-preview region | Before | After dimensions, mm | After geometric angle |
| --- | --- | --- | --- |
| Upper left | 1.57 x 1.97 in | 39.925 x 50.020 | -1.321 degrees |
| Upper right | 1.71 x 2.08 in | 43.544 x 52.873 | +3.202 degrees |
| Lower left card candidate | Missed | 85.590 x 54.193 | -1.473 degrees |
| Lower right skewed card candidate | Missed | 85.596 x 54.356 | -33.341 degrees |

The lower sizes agree with nominal ID-1, but this page has **no supplied ruler
truth or independently measured angles**. The upper regions may still be partial
prints. Therefore its recall, false-item count and worst physical error are
**unknown**; four returned regions does not prove four complete physical items.
No passport/photo identity or border size was inferred from personal information.

The original two-card reference remains within 1 mm per dimension. This round:
left **86.206 x 53.190 mm**, errors **+0.606 / -0.810 mm**; right
**85.654 x 53.341 mm**, errors **+0.054 / -0.659 mm**. Before this round the errors
were +0.428 / -0.803 and +0.054 / -0.659 mm respectively. Mean signed error is now
**+0.330 / -0.735 mm**. The clipped top edges still fail the earlier 0.5 mm mean
height requirement; their missing stock has not been extrapolated.

Synthetic verification: **67/67** items across the explicit ID-card rotation
invariance test and eight size classes at 0/5/15/30/45/60/89 degrees; **0 extra
items**, worst dimension error **0.624 mm**, worst geometric angle error
**0.158 degrees**. The angle comparison is modulo 90 degrees; a rectangle cannot
establish text reading direction. The 0/45 same-card size comparison passes.
A further **16/16** items pass 2/4/6-item ordering and corner contact/1 mm overlap
checks within 1 mm. Dark-cover, three-sided printed-sheet and softened-edge
fixtures pass. Six known-size cards retain mean signed **+0.026 / +0.300 mm**;
100/300 dpi A4 fixture detection took **249 / 243 ms** in the final crop run.

No existing assertion was changed. `platen_frame_and_debris` still rejects its
uniform three-sided slab: that raster does not establish sheet versus lid. Its
old description must not be read as a universal rule against three-sided paper;
the added printed-sheet test covers the evidence that distinguishes them.
AutoCropEngine's full-bleed refusal also remains unchanged because it is a separate
component contract. The misleading old “passport” test note refers to a 35 x 45 mm
photo; the new size matrix adds a 125 x 88 mm closed-booklet rectangle and a
176 x 125 mm spread rectangle, without claiming to simulate a real booklet.

Remaining limitations: seamless full-edge contact; larger or obscuring overlaps;
fully invisible white borders; a blank full-bed sheet with no observable edge;
substantial curl and torn/irregular physical edges; real matte/glossy/specular
materials; and physical-angle accuracy under strongly unequal X/Y sampling.
Only optical blur and small corner overlap are covered synthetically. A line
through an inner printed border can still have high edge support; confidence is
not proof that all surrounding stock was visible. **Universal acceptance is not
complete.** There are **0 ruler-annotated new corpus pages**. The suite explicitly
reports this missing coverage. Capture/annotation instructions and the automatic
`.truth.md` corpus format are in [PLATEN_CORPUS.md](PLATEN_CORPUS.md).

Verification: `build.ps1 -NoWait` exited 0 with no compiler warnings. All six
suites passed: crop (38 independent cases, including the explicit missing-corpus
note, plus the engine's 18 self-tests), imaging, export/batch, cancellation,
TWAIN golden (17) and eSCL (11). No clean build was used. The private images and
sidecars remain ignored; no commit or upload was made. This is offline validation
of the new build, not a fresh scanner acquisition or a manual UI smoke test.

## Auto crop accuracy — 2026-09-10

Offline verification against the private LiDE 400 preview; no new hardware scan
is claimed. The existing component finder remains. Four independent colour-gradient
lines now replace the old refinement round trip. On this reference the old
refinement accepted no result: its widths remained the threshold silhouette,
including right-hand shadow. Erosion also removed three top rows. Those rows are
now recovered at the frame, and open outlines of overhanging pale stock are closed
per component before hole filling. Real invisible top edges are not extrapolated.

The preview still decides. Its rectangles **and fitted corners** are stored in bed
inches; scan extraction maps those approved corners to straighten the output and
runs no detection. No preview still returns the untouched scan. Dashed amber
outlines identify weaker or clipped fits. Diagnostics now record the detector
that produced the displayed plan, including its time. The unused production gutter
splitter was moved to the test sources; its three original cases still run.

Reference dimensions (truth **85.6 x 54 mm**; signed errors are measured minus truth):

| Item | Before, in | After, in | Before error W / H, mm | After error W / H, mm |
|---|---|---|---|---|
| Left card | 3.4800 x 2.0700 | 3.3869 x 2.0944 | +2.792 / -1.422 | +0.428 / -0.803 |
| Right card | 3.4200 x 2.0700 | 3.3722 x 2.1000 | +1.268 / -1.422 | +0.054 / -0.659 |

Both reference items now meet **1 mm in each dimension**. Reference mean signed
error is **+0.241 mm width, -0.731 mm height** (before: +2.030 / -1.422 mm).
The reference therefore **does not meet the 0.5 mm mean-height target**: both cards
are clipped at the top of the acquired raster. Recovering their missing physical
height would invent pixels or assume a standard size. An unclipped reference is
needed to establish physical height without that ambiguity.

Six fully visible ID cards at 100/150/300 dpi give mean signed errors of
**+0.026 / +0.300 mm**. Passport, A6, 5x7 and 4x6 fixtures give **-0.012 / +0.010 mm**.
All dimensions are asserted within 1 mm. The rotated pair is -10.00 / +9.99 degrees
for -10 / +10 truth. Latest timed pair: **172 ms preview, 190 ms at 300 dpi**;
reference detection is about 160–200 ms. Baseline reference detection was about
105 ms. Timings cover detection, not file writing or image acquisition.

New cases cover an 8 mm white surround with a five-level edge shadow, a tint-only
border with less than one luminance level of contrast, a 4 mm gap, a white band,
a diffuse 4 mm shadow, frame overhang, debris, padded 16-bit pixels, concurrent
calls, source preservation, and approved-plan extraction across scan areas.

Verification: `build.ps1 -NoWait` succeeded. All six suites passed before and after:
`nsimgtest`, `nscroptest` (including all 18 engine self-tests), `nsexporttest`,
`nscanceltest`, TWAIN golden (17 cases) and eSCL (11 cases). Export journal tests
needed their normal LocalAppData write access outside the filesystem sandbox.

Still unverified: the operator's four-item page and its white-bordered photograph
are not available locally, and the two photographs' physical sizes are unknown.
The fitter cannot establish stock whose boundary has no measurable colour gradient;
subpixel texture alone is unresolved. Optional size snapping, neighbour-clamped
margins and blank-backing rejection remain deferred. Fitted dimensions and the
approved scan plan have no added margin. No private reference image was committed
or uploaded. Existing unrelated working-tree changes were preserved.

---

## Current audit — 2026-09-07

See [AUDIT_2026-09-07.md](AUDIT_2026-09-07.md) for fixes, verification and the
prioritized backlog. This audit preserves the pre-existing working-tree changes.
Historical hardware measurements below were not repeated during this audit.
Simulator verification is not hardware verification.

Fixed: cross-process host termination, unbounded USB recovery retries, malformed
frame acceptance, swallowed frame-delivery errors, destructive build behavior,
lossy rotation, non-atomic exports, partial-batch loss on failure, and eSCL URL /
HTTP chunk framing. HTTPS now uses OS certificate validation.

## 1. What is done and verified

Verified on this machine against a **Canon CanoScan LiDE 400** and a **Canon Color
Network ScanGear 2**.

| Area | Status | Evidence |
|---|---|---|
| Device abstraction contracts (§6.1) | ✅ built | `src/Core/Contracts.cs` |
| Dependency-free JSON (§3.2) | ✅ built | `src/Core/Json.cs` |
| Shared DIB/BMP decoder (§7.2, §18.2) | ✅ built | `src/Core/DibDecoder.cs` |
| Native TWAIN 2.x engine (§7.2) | ✅ **verified** | enumerates + scans through `twain_32.dll` |
| TWAIN state machine, msg pump, caps (§7.2) | ✅ **verified** | caps read, region set, memory transfer |
| Native WIA 2.0 engine, no `wiaaut.dll` (§7.3) | ✅ **verified** | enumerates + scans via `IWiaTransfer` |
| Out-of-process Host32 / Host64 (§3.1, §5.1) | ✅ **verified** | Host32 reaches a 32-bit-only driver Host64 cannot see |
| Shared-memory frame transport (§7.1, App. D) | ✅ **verified** | 128-byte header + pixels, read back by broker |
| Device broker, dedup, transport ranking (§6.2) | ✅ **verified** | one card per scanner, TWAIN preferred |
| Crash/hang isolation + watchdog (§7.7) | ✅ built | host kill/respawn, non-zero exit surfaced |
| `nsprobe` CLI (§12.3, §13.8) | ✅ **verified** | `list` / `caps` / `scan` |
| Integration into the existing Photoshop helper | ✅ **verified** | `scan engine: native (NextScan)`, NAPS2 unused |
| TWAIN simulator skeleton, well-behaved personality (§18.3, ADR-0002) | ✅ **verified** | `nsprobe` enumerates + scans it end-to-end on both hosts |
| Imaging core port onto `RawImage` (§9, HANDOFF §8.5) | ✅ **verified** | detection + 16-bit curves, `nsimgtest` 10/10 |
| Confidence-scored deskew policy (§3.5) | ✅ **verified** | 4 estimators + source-aware limits, `nsimgtest` 18/18 |
| eSCL transport: client + in-process driver + manual add (§7.4) | ✅ **verified** | 9/9 against the eSCL simulator (`tests\run_escl.ps1`) |

**The headline result: NAPS2 is no longer required.** The existing
`scanhelper.exe` UI now acquires images through the native engine, and falls back
to NAPS2 only if the native path fails and NAPS2 happens to be installed.

### Verified measurements

```
nsprobe scan "LiDE" --dpi 150 --region 0,0,3,2   ->  450x300  @150dpi  (exactly 3.00 x 2.00 in)
scanhelper -nodialog (crop_h=0.2567)             ->  2481x900  @300dpi  (exactly 8.27 x 3.00 in)
TWAIN scan wall time, 150 dpi preview region     ->  4.0 s
WIA   scan wall time, 150 dpi preview region     ->  2.7 s
simulator scan, same region                      ->  450x300, 0.2 s, all 8 colour bars byte-exact
```

### Imaging core on RawImage (HANDOFF section 8.5, first increment)

`src/Core/Imaging.cs` ports the proven scanhelper detection pipeline onto
`RawImage` input (any depth: 1/8/16-bit, gray or BGR): ring-median background
estimation with MAD noise, colour-distance + gradient foreground, measured
edge-strip guard, 8px block grid, per-blob connected components with hole
filling, exact minimum-area rectangle via hull edge alignment, projection-
profile refinement, full-page handling. Every evidence comment from the
original was carried over. Curves are now **native 16-bit**
(Fritsch–Carlson monotone spline, 65536-entry LUT, `Curves.BuildLut16`).

Verified by `nsimgtest` (10/10) on synthetic flatbed previews with exact
geometry: flat and 5.5° documents detected with IoU ≥ 0.90; 16-bit input
produces the identical box; curves are exact (identity, invert, monotone,
16-bit apply with zero rounding drift).

**Known limitation, documented in the test:** at small angles (≈3°) the 8px
block staircase hides the rotation from the minimum-area rectangle and the
legacy guard reports 0° with the box still correct (IoU 0.98). The original
behaves identically; resolving small angles is the job of plan §3.5's
confidence-scored estimators — **not yet implemented**. The
`DeskewGuard.StrictFlatbedGuard` preset preserves the legacy [0.6°, 6.0°]
rule meanwhile.

**Not yet done:** scanhelper still runs its own in-process copy — this engine
port is not wired into the app yet (deliberately: switch after the §3.5
policy lands, then remove the duplication); per-channel curve LUTs; deskew
confidence policy; whitening/deskew application on 16-bit data.

### eSCL transport (plan section 7.4) — first increment

`src/Net/`: `EsclClient` (raw-socket HTTP/1.1 with the 503 retry policy — 30×1 s
for NextDocument, 10 for everything else; job URI used verbatim from the
Location header), `EsclDriver` (capabilities XML → `DeviceCapabilities`, scan
settings XML in 1/300-inch units, JPEG pages → `RawImage` with the requested
dpi stamped, job DELETE on cancel/finish), `MdnsDiscovery` (raw mDNS on UDP
5353 with name-compression parsing, honours the `rs=` TXT key). eSCL runs
**in-process** in the broker (pure managed, plan §5.1); `NEXTSCAN_ESCL_URL`
pins a manual device, which also disables the 1.5 s mDNS listen window.

**Verified against the simulator** (`tests/escl_sim.py` + `tests/run_escl.ps1`,
9/9): probe/caps/scan through the real nsprobe→broker→driver path; caps decode
(8.5×11.7 bed, three colour modes, 75/150/300 dpi); two-page jobs; a 503 storm
survived at the documented ~1 s-per-retry cost; UUID and integer job ids.

**Not verified / not built — honest gaps:** no real eSCL device was available:
the two printers on this LAN carry `/eSCL/*` endpoints (everything else 404s)
but currently answer HTTP 500 to every eSCL request — service present, not
serving; re-test when they are in a scanning-ready state. mDNS discovery is
untested against a real responder (no `_uscan._tcp` advertiser on this LAN)
and its 1.5 s window still runs serially inside `Probe()` — parallelising it
with the host probes is the next optimisation. Not started: `octet-stream`
raw transfer (needs a real device to pin the vendor-specific pixel layout),
WSD, TLS pinning UI, HTTPS self-signed confirmation flow.

### Deskew policy (plan section 3.5)


`src/Core/Deskew.cs`: four independent estimators — text-baseline projection
variance (±30° at 0.1°), Hough over the paper's own boundary edges, Sobel
gradient-orientation histogram (folded mod 90°), and the detector's
pre-guard minimum-area-box angle — combined by a consensus with
**per-estimator measured resolutions** (0.5°/0.5°/5°/3°). The policy: dead
zone 0.6°, source-aware soft limits (flatbed 6°, ADF 10°, film 3°), hard
limit 20° → review, MinConfidence 0.72 / HighConfidence 0.90. The legacy
[0.6°, 6.0°] rule lives on verbatim as the `StrictFlatbedGuard` preset.

Verified (`nsimgtest`, synthetic antialiased text pages): 0°/3° auto-rotate
with correct angle; 8°/12° flatbed honestly land in review (the two coarse
estimators cannot second-guess beyond their resolution, so HighConfidence
0.90 is unreachable — the same pages rotate on a feeder at MinConfidence via
the 10° soft limit, per the source-aware unit tests); 25° review; straight
paper with a diagonal artwork bar is NOT rotated to follow the bar.

Measured limitations, documented in code: gradient-direction voting cannot
resolve shallow slopes on a pixel grid (a 3° antialiased edge displaces
~0.1px per kernel span, so Sobel's floor is ~5°); the min-area box inherits
the 8px block-staircase floor (~3°) and abstains by construction on
full-page scans (the detector's full-page rule forces angle 0 there). The
projection and Hough estimators carry the small-angle work — which is why
the per-estimator tolerance matters: without it those two floors veto every
small-angle page, the exact failure section 3.5 was written to fix.

### App switched onto engine imaging (HANDOFF section 8.5)

`scanhelper.cs` now runs document detection through
`NextScan.Core.DocumentDetector` with the §3.5 deskew policy overlaid on the
primary box (AI DocAligner cascade still runs first, unchanged). The
in-process pipeline stays compiled in as the fallback (`detect=legacy` in
scan.ini disables the engine path; any engine exception falls back
automatically).

**A/B verified on real hardware data** (full-bed LiDE 400 scan, 1276×1754 @
150 dpi, AI disabled to compare the classic paths): engine and legacy
returned **bit-identical** results — same 2 objects, same primary box
`W=121.6 H=1003 AABB=(0,215,122,1003)`, same background estimate
`bg=(234,232,233) noise=3`, and a zero-delta normalized crop rect. The
deskew policy ran and correctly stayed in the dead zone for that scene.
Note: `-nodialog` scans crop from scan.ini and never call detection (true of
the legacy path as well); the switch affects the interactive preview flow.



### TWAIN simulator (ADR-0002)


A fake `TWAINDSM.DLL` (`sim\TwainSim.cpp`, built x86 + x64 to `bin\sim\`), loaded
by setting `NEXTSCAN_TWAIN_DSM` to its path — no admin rights, nothing written
to the machine's TWAIN source list. Verified working against the full managed
stack: enumeration, capability negotiation (ENUM/ONEVALUE/RANGE containers,
FIX32), region clamping, memory-strip transfer, ENDXFER/RESET teardown, on both
Host32 and Host64. Image content is a deterministic 8-bar colour pattern
(`NEXTSCAN_SIM_IMAGE` = bars | gradient | checker | flat); all eight sampled
bar colours came back byte-exact, which also proves R/B order through our own
memory-transfer path (hardware-side confirmation still pending — see §3).

**Personalities, verified** (`NEXTSCAN_SIM_PERSONALITY`): oddwidth (451px at a
450px request), bw1/gray8/gray16/color48 forced modes (driver "accepts"
negotiation then delivers its own fixed mode — read-back catches it), bottomup
and topdown (memory transfer refused → native-DIB fallback, both row orders),
refusesui (`TwainEnableFailed` with condition code), setlies (SET succeeds,
device stays at 150 dpi — detected, image consistent with the lie), crash7
(host dies 0xC0000005 in state 7 → broker reports `HostCrashed`, caller
survives), duplex (feeder + 2 pages, back side rotated 180°, asserted both
against references and the rot180 relation).

**Golden harness, verified** (`tests\run_golden.ps1`): 16 cases — pixel-exact
image comparison (decoded 24bpp rows + dimensions + DPI) against committed
references in `tests\golden\`, plus behavioural assertions. Full run ≈ 10 s,
exit code non-zero on any failure. `-Generate` regenerates references;
`-WithHang` adds the slow watchdog proof.

**Bug the simulator found before any user could:** `DeviceBroker.RunHost`'s
crash-detection branch was unreachable — a crashed host emits no `result`
line, so the sentinel `HostPipeBroken` result (Ok=false) kept the branch's
`Result.Ok` condition false forever. Crashes were reported as pipe breaks with
no exit code. Fixed by tracking whether a `result` line was seen at all; the
`crash7_isolation` harness case is its regression test.

**Zombie-host lock recovery (field incident 2026-08-23, fixed same day).**
Symptom in the field: after a driver glitch mid-work, every later scan failed
with "another program is already using this scanner" (TWCC_MAXCONNECTIONS)
until a Windows reboot. Root cause in our architecture: a host process left
holding the TWAIN data source — Windows does not reap children when the app
exits, and a watchdog-killed host dies by TerminateProcess, which never runs
the vendor driver's CloseDS. Three-part fix, all engine-side so every client
benefits:

1. `DeviceBroker` now kills every `NextScan.Host32/64` process it did not
   spawn itself, before each host run (hosts are stateless one-shot workers;
   a foreign instance can only be garbage).
2. `DeviceBroker` tracks its children and kills them on process exit — the
   app can close or crash without orphaning a host.
3. `TwainSession.OpenSource` retries OPENDS twice with a 1.5 s backoff on
   TWCC_MAXCONNECTIONS, because the lock of a just-dead holder typically
   clears within seconds.

Verified: the simulator's `busy` personality (first OPENDS refused with
MAXCONNECTIONS, retry wins) is a permanent harness case; a deliberately
spawned hung host was detected and killed by the next `nsprobe list` (log
line + process gone); real-hardware scan unaffected (2481 px wide @ 300 dpi,
no leftover hosts). The simulator also moved DAT_STATUS above its data-source
gate to match real DSMs — an application can only learn *why* OPENDS refused
by asking status before any source is open.

**Hang watchdog, proven:** with the `hang` personality the host never returns
from `MSG_ENABLEDS`; the broker killed it at the 600 s scan timeout, the
caller survived and reported `HostTimeout` with a remedy (exit 1, no orphan
host process left behind). The harness's `-WithHang` switch asserts the same
output but re-runs the full 10-minute wait, so it is excluded from the default
fast pass.

**Parallel probe (2026-08-23).** `DeviceBroker.Probe` now runs the two host
probes and the eSCL/mDNS browse concurrently (three `Task.Run`s joined on
`.Result`); `MdnsDiscovery` got a 1500 ms window with quiet-period early exit.
Measured on this machine: full `nsprobe list` 1.9 s → 1.6 s with real devices
attached. The parallel spawn exposed a race in the stale-host sweep: the first
spawner released the lock between deciding to sweep and finishing the sweep,
so a sibling's fresh host could be enumerated before being tracked and killed
as garbage — symptom was a flaky 1-in-5 missing device (the simulator vanished
from `nsprobe list` 2/10 runs; `busy_retry` failed intermittently in the
golden harness). Fix: the sweep now runs while holding the child lock.
After the fix: 0/15 missed probes, golden 17/17 twice in a row, eSCL 9/9,
imaging 18/18.

**USB wedge auto-recovery (2026-08-23).** Second field incident with the
LiDE 400: the carriage sticks at the bottom of the glass and Canon ScanGear
itself gives up with "Detach the USB cable and reconnect. Code:2,250,4" —
the only cure was physically re-plugging the cable, sometimes several times.
The software equivalent is `pnputil /restart-device` on the PARENT composite
USB node (not the MI_ interfaces), which re-enumerates the whole device like
a re-plug does. It needs elevation, so it runs through a one-time-registered
scheduled task (`NextScan_UsbReset`, RunLevel Highest, registered with a
single consent via `tools\setup_usb_reset_task.ps1`); later triggers need no
prompt (`schtasks /run` from the unelevated app). `DeviceBroker.Scan` fires
it on exactly the failure codes a wedge produces (open/enable/transfer
failure, WIA offline) — narrow by design, a paper jam or user cancel must
never power-cycle — then retries the scan once after an 8 s settle window
(field evidence: the node returns in ~2 s but the Canon driver needs several
more before a retry can succeed). Kill switch: `NEXTSCAN_USB_RESET=0`.

Verified live on the real scanner: unelevated trigger → pnputil restart of
`USB\VID_04A9&PID_1912\4C24A8` → device back OK in 2 s (helper log with the
attempt/success trail); during the same session an actual scanhelper run hit
`TwainTransferFailed`, the engine reset the node automatically and retried —
the recovery path fires end-to-end without any user action. A later 300 dpi
`-nodialog` run completed normally (2481x600, 13 s). The wedge state itself
refuses to clear in software sometimes, matching Canon's own "re-plug
several times" advice; when one reset is not enough the engine reports the
original failure plus a human remedy message.

### Why the 32-bit host is not optional

On this machine:

```
C:\Windows\twaindsm.dll   does not exist
C:\Windows\twain_64\      does not exist
C:\Windows\twain_32\      SG20, ScanGearIR, wiatwain.ds     <- the real Canon driver
```

`NextScan.Host64.exe probe` finds **zero** TWAIN devices.
`NextScan.Host32.exe probe` finds the CanoScan LiDE 400.

This is plan §3.1 confirmed in practice, not in theory.

---

## 2. Bugs found and fixed during bring-up

Recorded because each one is a trap the next person will otherwise re-enter.

1. **`PRSPEC_PROPID` is 1, not 0.** Setting `PROPSPEC.ulKind = 0` selects
   `PRSPEC_LPWSTR`, so WIA dereferences the property id as a string pointer and
   the process dies with an access violation inside the WIA service. Symptom is
   an AV in `ReadMultiple` that survives every plausible marshalling fix, because
   the marshalling was never wrong.
2. **`TYMED_FILE` is 2, not 1.** 1 is `TYMED_HGLOBAL`. Writing 1 is rejected and
   the scan then only succeeds if the driver's default was already correct.
3. **`TYMED_CALLBACK` is the WIA 1.0 band mechanism.** `IWiaTransfer::Download`
   wants `TYMED_FILE`; asking for callback makes `Download` fail `E_INVALIDARG`
   even though every property write returned `S_OK`.
4. **`WIA_IPA_TYMED` must be written before `WIA_IPA_FORMAT`**, since the legal
   format set is scoped to the medium.
5. **`ICAP_UNITS` must be set before reading any dimension.** Without it the
   LiDE 400 reported its bed as `0.16 x 0` inches.
6. **`ICAP_BITDEPTH` is bits-per-channel on some drivers.** Canon rejects 24 for
   RGB and wants 8. We now try the spec value, then per-channel, then give up and
   keep the driver default.
7. **Advertising a capability is not the same as having the hardware.** The
   LiDE 400 exposes `CAP_FEEDERENABLED` and has no feeder. We now require that
   the device actually *accepts* being switched into the mode.
8. **`TW_IDENTITY` and friends need `Pack = 2`** (`#pragma pack(2)` in twain.h).
   Wrong packing does not fail loudly, it just returns shifted garbage.

---

## 3. Known issues and limitations

| Issue | Impact | Notes |
|---|---|---|
| `ICAP_BITDEPTH` enumeration is garbage on the LiDE 400 (`0,1,2,3,5,7,10,52,53,13` for both RGB and grey) | 48-bit capture cannot be detected on this device | Verified as a **driver** fault, not ours: the container decodes as `ENUM/uint16` and `ICAP_PIXELTYPE` decodes correctly with identical code. We correctly do **not** claim 48-bit support. Re-test against a second vendor before changing the decoder. |
| LiDE 400 accepts `ICAP_LIGHTPATH = TRANSMISSIVE` despite having no film unit | UI would offer a Film source that does nothing | Needs a Quirks DB entry (§7.6), which is not built yet |
| 48-bit / 16-bit-per-channel capture | Untested | Code paths exist in the transfer loop; no device here to exercise them |
| ADF / duplex | Untested | No feeder on the available hardware. WIA `WIA_TRANSFER_ACQUIRE_CHILDREN` and TWAIN `CAP_XFERCOUNT = -1` are wired but unproven |
| Colour channel order | Not independently verified | Only blank white pages were scanned; R/B swap would be invisible. Scan something with saturated colour to confirm |
| `MemoryMappedFile` frames are never explicitly released by the host | Host is short-lived so the OS reclaims them | Fine today; must be fixed if the host ever becomes long-lived |

### Studio shell v2 (plan section 13) — ground-up rebuild

`src/App/` was rebuilt from scratch. The previous shell (fixed top command bar,
permanently open right inspector, bottom filmstrip, navy/electric-blue theme) is
preserved unbuilt in `src/App/legacy/` and is no longer referenced.

New files: `StudioTheme.cs` (palette, type, easing, single 60 Hz `Animator`),
`StudioControls.cs` (rail button, pill, dropdown, toggle, segment, slider,
section, card, chip, glass bar — all owner-drawn), `StudioCanvasView.cs`
(canvas + vertical filmstrip), `StudioShell.cs` (window).

Layout is deliberately different: left icon **rail** (Device / Scan / Adjust /
Output) opening an animated **drawer**, dominant **canvas** with a floating
glass HUD and a floating action dock, **vertical** filmstrip on the right,
custom title bar carrying the live device chip, thin status strip.

Theme is **true-neutral graphite with an amber accent**, replacing the
blue-tinted obsidian. Two reasons: a blue surround biases judgement of scanned
colour (plan §13.5 asks for a neutral colour-critical ground), and amber cannot
be mistaken for image content the way cyan-on-blue could.

**Verified:** builds clean; launches; probes and selects a real device
(chip and drawer agree: CanoScan LiDE 400 / TWAIN 32-bit); rail, drawer
animation, HUD, dock and filmstrip render as designed. All three regression
suites still pass — `nsimgtest` 18/18, `tests
un_golden.ps1` 6/6,
`tests
un_escl.ps1` 6/6.

**Not verified:** no scan has been run through the new shell yet, so the sweep
overlay, page-to-filmstrip flow, export and the Photoshop handoff are written
but unproven. Curve editing is not wired into the Adjust drawer (the old curve
editor was left behind in `legacy/`); the drawer says so rather than showing a
dead control. Filmstrip placeholder text clips slightly at 104 px.

#### Shell v2, second pass

Two changes after the first review:

**Window sizing.** The shell opened at a hard-coded 1440x900 on a 1366x720 work
area, so it was always clipped and Restore appeared to do nothing - the restored
size was still larger than the screen. Size is now derived from
`Screen.PrimaryScreen.WorkingArea` (76% wide, 80% tall, capped at 1120x720) with
the minimum dropped from 1080x680 to 820x500. Measured after the change:
**1038 x 576**, a genuinely floating window.

**Device page removed from the rail.** Which scanner, over which transport, from
which tray is a property of the whole session rather than a step inside it, so it
moved into the title bar as `NsDeviceBar` (`StudioDeviceBar.cs`): status dot,
device name (click for the device menu), transport (click for the transport
menu), a re-probe button, and the four paper sources as drawn icons. The rail is
now three pages - Scan, Adjust, Output.

Paper-source icons are drawn with GraphicsPath rather than set as Unicode
glyphs: the nearest characters render inconsistently across fonts and appear as
mojibake at 12 px.

**Verified:** window measured at 1038x576; the device menu opens from the title
bar and lists both scanners with the current one marked; rail shows three pages;
the Scan drawer renders Resolution / Colour / driver-dialog / geometry toggles.
All three suites still pass (`nsimgtest`, `run_golden.ps1`, `run_escl.ps1`).

**Still unverified:** no scan has been run through the new shell, so the sweep
overlay, filmstrip population, export and Photoshop handoff remain unproven. The
transport menu and the source icons were not click-tested.

#### Shell v2, third pass - layout customisation and window chrome

New: `StudioLayout.cs` (layout state, persisted to
`%APPDATA%\NextScan\layout.json`) and `StudioSplitter.cs` (splitters, drag
grips, and the window-edge hit-test helper). A fourth rail page, **Layout**,
exposes it all.

What the user can now move or resize:

| Element | How |
|---|---|
| Settings drawer | drag the divider on its right edge, or the Layout slider |
| Session strip | drag its left divider, or the Layout slider (0 hides it) |
| Rail | Layout slider |
| Zoom bar and action bar | drag the dotted grip; Shift-drag (or right-drag) to resize |
| Device bar | drag its empty area along the title bar |
| Floating bar size | Layout slider, 80-150% |

**Save this layout as default** writes `layout.json`; it is read at startup.
Window size is remembered separately on close, but panel positions only persist
when explicitly saved - an accidental drag should not silently become the
default. **Reset to the default layout** restores the shipped arrangement
without touching scan settings, which is why layout lives in its own file rather
than in `scan.ini`.

**Rounded corners.** The shell is a borderless form, which does not get Windows
11 corner rounding for free - that is why it looked like a hard rectangle beside
every other app. Fixed with `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE,
DWMWCP_ROUND)`, plus `DWMWA_USE_IMMERSIVE_DARK_MODE`. On Windows 10 the call
fails harmlessly and corners stay square; a `Region` was deliberately not used
because it clips the drop shadow and aliases the edge. Verified by capturing the
corner and magnifying it 4x.

**Edge resizing now actually works.** The form already returned HTLEFT/HTBOTTOM
from WM_NCHITTEST and it did nothing, because Windows sends that message to the
window under the cursor and the rail / drawer / canvas / filmstrip covered the
entire client area - the child answered, the form never saw it. The canvas,
filmstrip and splitters now return HTTRANSPARENT within 6 px of the form edge,
handing the hit test up to the form.

**Window sizing** was already fixed in the second pass; the layout file can now
also restore a remembered size, clamped to the current work area so a size saved
on a larger monitor cannot reproduce the original off-screen bug.

**Verified:** window 1038x576 with visibly rounded corners (4x capture); the
Layout page renders all sliders, toggles and both buttons; drag grips appear on
both floating bars; defaults restore correctly after deleting `layout.json`. All
three suites pass.

**Not verified:** the splitters, grips, device-bar drag, Save and Reset were
exercised by reading the code and by driving synthetic clicks, not by a human
dragging them. Still no scan has been run through this shell.

**A bug class found and fixed three times over.** `NsSplitter`, `NsGrip`,
`NsSlider`, `NsDeviceBar` and `NsSegment` all set a dragging flag on mouse-down
and cleared it only on mouse-up. A mouse-up can be lost - focus moves, another
window takes capture, or the click was synthetic - and the control then tracks
the pointer forever, so the next mouse movement slams the value to an extreme.
It showed up as the drawer collapsing to its 180 px minimum and the rail and
float sliders pinning to their floor. Every one of them now also checks
`Control.MouseButtons` while moving. Caught only because the drag was driven
programmatically; a human tester would have hit it eventually and struggled to
reproduce it.

#### Shell v2, fourth pass - light theme, bigger page, panning

**Light theme, and it is now the default.** `Theme` colours changed from
`static readonly` to mutable statics with `Theme.Apply(bool light)`; every
control already read them at paint time, so switching the palette and
invalidating restyles the whole shell. Both palettes stay true-neutral (R=G=B) -
a colour cast in the surround biases judgement of a scan whichever way it leans
(plan 13.5). Amber survives on both grounds, so the accent does not change with
the theme; only the text drawn on top of it does. The switch is a segmented
control at the top of the Layout page, stored as `lightTheme` in `layout.json`,
and it also flips `DWMWA_USE_IMMERSIVE_DARK_MODE` so the window frame matches.

Hard-coded dark values in the floating bars, the canvas pills and the crop
overlay were routed through new `Theme.Glass / GlassLine / GlassTop / OnAccent`
tokens. The crop outline and corner brackets flip to near-black on light, and
the outside-crop scrim drops from 140 to 64 alpha there - at 140 a light page
looked dead rather than de-emphasised.

**Layout defaults are now the arrangement the user settled on**: drawer 240,
session strip 70, rail 60, float scale 0.8. The HUD and dock also park closer to
the canvas edges (`HudY` 0.035 to 0.012, `DockY` 0.90 to 0.945) and the canvas
margin dropped from 24 px to 8 with the reserved clearance from 12 to 4, so a
fitted page now fills the space between the two bars instead of floating in the
middle of it. Measured on the same preview: zoom 26% before, 34% after.

**Panning works.** Left-drag has always meant "adjust the crop", so a zoomed page
could not be moved at all. Panning now has its own gestures - middle-button drag,
or Space held with a left drag - plus wheel to scroll, Shift+wheel to scroll
sideways, and Ctrl+wheel to zoom. The first pan leaves fit mode (a fitted page
has nowhere to go) and `ClampPan` keeps at least a corner on screen.

**The lost-mouse-up bug had one more home.** The crop drag itself had no
`Control.MouseButtons` guard, so a drag that never saw its mouse-up left
`_dragging` true: the canvas kept rewriting the crop as the pointer moved and
permanently displayed the drag-time thirds grid. It presented as a scan opening
with a small band selected out of the middle of the page for no reason. That is
now the sixth and last control carrying this guard.

**Verified:** light theme renders correctly across chrome, drawer, floating bars
and canvas; defaults load when `layout.json` is absent; page fills the space
between the bars.

**Not verified:** the theme toggle, panning gestures and wheel behaviour were not
exercised by hand - only the resulting code paths and a fresh-start screenshot.

**Test isolation gap, worth fixing.** `run_golden.ps1` reported one failed case
on a run made while the studio app was mid-scan, then passed 17/17 immediately
afterwards with the app still open. The suite spawns its own host processes and
is evidently not isolated from a concurrently running studio instance. This is a
harness problem rather than a product regression, but it makes the suite
untrustworthy exactly when someone is most likely to run it - while working on
the app. It should either take a lock or refuse to run while `NextScanner.exe` is
alive.

#### Crop now drives the scan region (fifth pass)

**The bug.** Cropping a small area and pressing Scan acquired the whole platen.
The crop was purely a canvas overlay: `StartScan` built its `ScanSettings` from
`_settings.Region*`, which only the paper-size dropdown ever wrote, and
`StudioCanvasView.CropChanged` was never subscribed at all. The acquisition
engine had supported regions since the first increment - `nsprobe scan --region
0,0,3,2` produced exactly 450x300 px - so nothing was missing below the shell.

**The fix.** A hand-drawn crop is a fraction of the DISPLAYED page, which only
means something physical once you know where that page sat on the glass, so the
shell now tracks `_pageBedRect` - the platen rectangle, in inches, the current
page came from. A preview covers the whole bed; a scan made from a crop covers
just that crop, so cropping a cropped scan composes correctly instead of
re-measuring from the bed origin. `RecordPageBedRect` prefers the delivered
image's own pixels and dpi over the rectangle we asked for, because drivers clamp
and round the frame they are given.

At scan time a manual crop takes precedence over the paper-size preset: it is the
more specific instruction and the one the user is looking at. The region is
clamped to the platen (a crop dragged to the edge of a preview can round a hair
past it, and drivers reject an out-of-range frame outright rather than clipping
it) and rejected below 0.2 in. After a cropped scan the crop resets to full,
since the page just delivered IS the selected area and a box over it would mean
"crop the crop".

The status bar now reports the mapped region live as the crop is dragged, so the
area is confirmable before committing to a scan.

**Verified on hardware, end to end.** Preview of the full bed (8.50 x 11.69 in,
850 x 1169 px @ 100 dpi); a crop dragged with the mouse reporting "5.01 x 8.89
in, 3.49 in from the left and 2.80 in from the top"; F7 at 300 dpi produced
`ps_20260907_152223_1407656.png` at **1502 x 2667 px @ 300 dpi** = 5.007 x 8.89
in. One pixel narrow of the request, which is the driver rounding the frame.

All three suites pass with the studio closed.

#### Blank platen sheet on startup (sixth pass)

**The problem.** The canvas opened on "No page yet", so every job began with a
throwaway preview scan purely to have something to draw a crop rectangle on -
preview, crop, scan - when the user already knew which area they wanted.

**The fix.** `ShowBlankSheet` puts a white sheet the size of the selected
scanner's platen on the canvas as soon as the capabilities come back, with the
selected paper size marked on it. The crop tools work immediately, and pressing
Scan acquires that area directly. `StudioCanvasView.IsPlaceholder` marks it, and
a real preview or scan replaces it and sets `_hasRealPage`, which stops a sheet
from ever overwriting a scan.

Decisions worth keeping:

- **100 dpi for the sheet.** Enough to crop accurately, keeps an A4 platen near
  3 MB, and matches the dpi a preview would have produced - so the crop maths is
  identical whether the user previewed or not.
- **The hint is drawn as an overlay, not baked into the bitmap.** The sheet stays
  clean white and the text stays crisp at any zoom.
- **The paper crop is computed in `ShowBlankSheet` itself** rather than delegated
  to `ApplyPaperSizeToCanvasCrop`, which starts from `_settings.CropNorm` and was
  therefore re-applying a crop persisted in `scan.ini` from an earlier session to
  a sheet it had never been measured against. That is what produced an "A4" box
  8.27 x 2.89 in tall on first run.
- **The paper box is anchored at the origin corner, not centred**, because that is
  where the guides on a flatbed put the sheet.
- **Processing is skipped for a placeholder** - running a white sheet through
  curves and whitening only tints it - and **the Photoshop handoff refuses it**,
  since sending a sheet of white paper to Photoshop is never what was meant.

**Verified on hardware, without taking a preview:** the app opened showing
"Scanner glass 8.51 x 11.69 in" with an A4 box reading "8.27 x 11.69 in, 827 x
1169 px @ 100 DPI"; F7 produced `ps_20260907_153454_5483043.png` at **2481 x 3507
px @ 300 dpi = 8.27 x 11.69 in** - exactly A4, straight from the blank sheet.

All three suites pass with the studio closed.

#### The platen stays on screen after a partial scan (seventh pass)

**The problem.** Scanning a crop replaced the canvas with just that crop, so
widening the selection afterwards was impossible - the surrounding glass was
gone and the only way back was another preview scan.

**The fix.** After a scan that covered less than the full platen, the canvas shows
a **bed view**: a white sheet the size of the glass with the scan composited back
into the place it came from, and the crop box left exactly where it was. Dragging
it outwards and pressing Scan again just works, with no preview in between.
`StudioCanvasView.IsBedView` marks it.

**What must not leak.** The bed view is built at 150 dpi - a full A4 platen at
600 dpi would exceed 100 MB and this image is only ever looked at. Everything
that leaves the application must therefore avoid it:

- the filmstrip stores the original full-resolution page, unchanged;
- `SendToPhotoshop` detects a bed view and takes the filmstrip page instead of
  cropping the canvas, which would have silently downscaled the handoff.

To keep tone adjustments in that path, the pixel loop was extracted from
`UpdateProcessedPreview` into `ProcessBitmap(Bitmap)`, so the same adjustments run
over the full-resolution page on the way to Photoshop rather than only over the
display copy.

**Verified on hardware, one pass, no preview taken.** From the blank sheet: a crop
dragged to 5.88 x 8.49 in at 2.62 in from the left and 3.21 in from the top; F7 at
300 dpi. Afterwards, simultaneously on screen - canvas `1276 x 1754 px, 150 dpi`
(the whole 8.51 x 11.69 in glass) with the scan in position and the box still on
it; filmstrip thumbnail `1765 x 2545`; Photoshop reporting `1765 px x 2545 px
(300 ppi)`. Requested 1764 x 2547, so the driver rounded by a pixel or two.

All three suites pass with the studio closed.

**Not verified:** that tone adjustments still reach Photoshop correctly through
the new `ProcessBitmap` path - the refactor compiles and the handoff produced a
correct full-resolution file, but the scan used was of blank white paper, which
cannot show whether curves and whitening were applied.

#### Scan cancellation (eighth pass)

**How it works.** Cancelling is a cross-process problem: the driver runs inside a
host that cannot be called into, and the control channel only flows outwards
(ADR-0001). A **named event** solves it without adding a back channel. The broker
creates one per scan and passes its name as `--cancel-event`; the host polls it
between transfer strips and between pages, and stops the TWAIN or WIA session
through its own state machine. If the host has not exited within **4 seconds** it
is killed, because a driver wedged inside a vendor DLL never reaches the poll.

Signalling rather than only killing matters on real hardware: an abandoned data
source leaves the lamp on and the next open failing with `TWCC_MAXCONNECTIONS`.

- TWAIN: polled in the pump loop while waiting for `MSG_XFERREADY`, between pages
  in `TransferAll`, and between strips in `MemoryTransfer` - the only point where
  the DS is between calls and can still be reset cleanly.
- WIA: the transfer callback sets `Cancelled`, returns `E_ABORT` **and** calls
  `IWiaTransfer.Cancel()`; returning E_ABORT alone leaves some drivers finishing
  the page before they notice.
- UI: the Scan pill becomes a red **Cancel** while a scan runs, then **Stopping…**
  while it winds down; `Esc` does the same. Cancelling is reported as a normal
  outcome, not a fault: no red connection dot, no remedy text, and any pages
  already delivered are kept.

**New test tool: `nscanceltest.exe`,** built by `build.ps1` and run like the other
suites. Cancellation cannot be tested against real hardware with any
repeatability - a 300 dpi A4 page finishes in about ten seconds, so a hand-timed
Escape lands either before the scan starts or after it ends. That was tried first
and is exactly what happened. The tests instead drive the fake DSM (ADR-0002)
with a new **`slow`** personality that delays every strip, and cancel at a known
point. Five cases, all passing:

```
graceful_cancel        2.6s  reported TwainCancelled
cancel_is_prompt       2.9s  stopped in 0.48s
kill_fallback          7.2s  killed after 4.0s of grace
idle_cancel            0.0s  no-op when idle
scan_still_works_after 2.7s  next scan delivered 1 page(s)
```

`cancel_is_prompt` fails if stopping takes 3.5 s or more, which would mean the
host had to be killed rather than signalled. `scan_still_works_after` is the one
that would catch a data source left open by a cancel.

**A real bug the tests caught, that manual testing had not.** `CancelScan()`
looked for `_activeScan`, and nothing ever assigned it - the patch that published
the running process had silently failed to apply. Cancel therefore returned false
and did nothing at all: no signal, no kill. It was reported as working because a
scan that finishes on its own looks identical to one that was cancelled.

**A second trap, in the tooling.** Four cases failed for half an hour against a
**stale binary**: `build.ps1` refuses to build while something is running from
`bin\`, and the build output was being filtered through `grep error CS`, which
does not match that message. The build was failing and being reported as clean.
Filter build output for failure, not only for compiler errors.

#### Output and batch scanning (ninth pass)

Saving was the largest remaining hole: a PDF writer nothing had ever read back, an
`OutputNamePattern` setting that existed in the settings file and was used
nowhere, and no multi-page TIFF at all. The file-writing code moved out of the UI
project into `Core\PageWriter.cs` so it could be tested without starting a
window, with `StudioExport` left as a shim over it.

**Naming** (`Core\NameTemplate.cs`). Patterns such as `{yyyy}\{MM}\invoice_{nnnn}`
expand to a real path; a backslash in the pattern makes subfolders, a backslash
inside a token *value* is stripped so a vendor-supplied device name cannot
redirect where a file lands. The counter is **not** stored anywhere - it is found
by probing the folder for the first free name. A stored counter goes stale as
soon as files are moved or restored, and its failure mode is silently overwriting
a scan. Probing also fills gaps, so deleting a bad scan and rescanning reuses that
number.

**Containers.** Multi-page TIFF via GDI+ `SaveAdd` (CCITT G4 for bilevel, LZW
otherwise). The PDF writer gained an Info dictionary, and per-page encoding:

| page | filter | colourspace |
|---|---|---|
| colour | DCTDecode | DeviceRGB |
| greyscale document | FlateDecode | DeviceGray |
| bilevel | FlateDecode | DeviceGray |
| greyscale continuous tone | DCTDecode | DeviceRGB |

**A bug the tests caught that no viewer would have.** The first version of that
table declared `/DeviceGray` over a JPEG. GDI+ **always emits a three-component
JPEG**, even from an 8-bit greyscale surface - verified directly by reading the
SOF marker of what it produced. The file therefore declared one component and
carried three: rejected by strict readers, rendered by luck elsewhere. A
one-channel page now goes in deflated, where the byte layout is ours and
DeviceGray is truthful, and falls back to JPEG/DeviceRGB only for continuous tone
that deflates badly. A greyscale document page went from 32 KB to 4 KB as a side
effect. `pdf_colorspace_matches` now parses the JPEG frame header and asserts the
declared colourspace matches the component count, in both directions.

**Batch** (`Core\BatchJob.cs`). Blank detection works on a tile grid rather than a
pixel count, so dust darkens one tile and is discarded while a single line of text
is not, and the same threshold holds at any resolution; darkness is measured
against the page's own white point, since scanned paper reads about 235. Document
separation by fixed page count or by blank separator sheet, and blank-page
dropping for the duplex-over-single-sided case. Keeping the separator sheet
changes what a document contains, never how many there are - otherwise a stack
with a trailing separator files an extra document holding one blank sheet.

`s.PageCount` in the shell was hardcoded to 1, so a feeder scanned one sheet
whatever the settings said. It now asks for the configured count, or zero
("until the tray is empty") when the job says so. WIA needed a matching fix:
`WIA_IPS_PAGES` was only written when a count was given, and the WIA default is
one page, so an until-empty batch would have stopped after the first sheet.

**New test tool: `nsexporttest.exe`,** 20 cases, all passing. Every case reads the
file back rather than trusting a return value, because these bugs are silent - a
PDF with a broken xref opens in the reader that ignores it, a multi-page TIFF that
kept one page looks right in a thumbnail. `CheckXref` verifies every offset lands
on the object it claims. The last case is end to end: pages the engine really
delivered, through the host process and the TWAIN state machine, into the same
export path the Save button uses.

**Not built, and not claimed:** hot folders (watch a folder, process what lands in
it), barcode and patch-code separation, and a job journal for resuming a batch
that was interrupted part way. The new drawer controls - name pattern, split rule,
feed options - compile and are wired, but have not been exercised by hand.

#### Hot folders (tenth pass)

`Core\HotFolder.cs`: watch a folder, and anything dropped in is converted, named
and filed by the current job. Per-file, or one document per drop.

**It polls rather than using FileSystemWatcher.** A watcher is faster, but it
silently drops events when its buffer overflows - which happens exactly when a
batch of files lands at once, the case a hot folder exists for. Any correct
watcher implementation therefore needs a periodic rescan as a backstop; given
that, the rescan alone is the whole mechanism, and there is no second code path
that only misbehaves under load.

**Reading a file too early is the failure that matters.** A file is listed the
moment it is created, long before whatever is writing it has finished, and
reading then gives a truncated image - or worse, a valid image missing its bottom
half, which no error reports. Two conditions must both hold: size and timestamp
unchanged for a settling period, **and** the file opens with `FileShare.None`.
The second is what actually does the work, because a network copy can pause
longer than any settling period one would want to wait. `hf_waits_for_slow_write`
writes a JPEG in four chunks with 150 ms pauses against a 120 ms settling window
- deliberately the wrong way round - and asserts the output is the full
900x1200 image.

**A real bug the tests caught.** Files were tracked by path with a "handled"
flag, so re-dropping a corrected page under the same filename was ignored -
silently, for as long as the program ran. A handled entry whose file no longer
matches what was handled (length or last-write time) is now treated as a new
file, and paths are forgotten once the file leaves them.

Other decided behaviour: output nested inside the watched folder is refused in
the constructor, both directions, rather than discovered as an infinite loop;
unreadable files move to `_errors` with a `.txt` saying why and are not retried;
non-image files are left alone rather than counted as failures; multi-page TIFF
input contributes all its pages, since taking only the first would file the
original away as done and lose the rest; nothing overwrites anything, in the
output folder or in `_processed`.

Eight cases in `nsexporttest`, all passing, plus a live run outside the suite:
three PNGs dropped one at a time into a real folder produced `drop_001..003.pdf`
and filed the originals into `_processed`.

**Still not built:** barcode and patch-code separation, and a job journal for
resuming a batch interrupted part way. The watched-folder drawer controls
compile and are wired, but were exercised through the engine rather than by
clicking them.

#### Live pages and a crash-safe journal (eleventh pass)

The two things that stopped batch scanning being trustworthy.

**Pages now appear as they arrive.** The scan callback used to accumulate into a
list and fill the filmstrip only when the whole run finished, so a fifty-page
feed showed an empty window for several minutes and a run that failed at page 40
displayed nothing until it was over. `OnPageArrived` is now called per page and
marshals onto the UI thread. The canvas only follows from the second page on:
for a single-page scan its placement is decided at the end - a cropped sheet is
drawn back in its place on the glass - and setting it early just flashes the
wrong view.

**Every page is written to disk the moment it arrives** (`Core\ScanJournal.cs`).
Losing the program at page 87 of 100 used to lose all 87, and the paper had
already gone through the feeder, so the work was not merely unsaved but
unrepeatable without re-stacking the stack. Three decisions carry this:

- Spooling runs on a background thread behind a queue. Writing 26 MB per page on
  the acquisition thread would stall the feeder, and a stalled feeder on real
  hardware is a paper jam, not a slow scan.
- Each page is written under a temporary name and renamed once complete, so a
  session that died mid-write has no half-page to load. `jr_torn_page_refused`
  truncates a page file and asserts the good page still loads and the bad one
  does not.
- The session folder records the owning process id and name. A second copy of
  the studio must not offer to recover the batch the first is still scanning;
  `jr_live_session_skipped` covers both directions.

Pages are deflated but otherwise untouched - not re-encoded as PNG or JPEG - so
a 48-bit film scan comes back bit for bit. That is checked by comparing every
byte, not by comparing dimensions.

The journal is discarded **only** after a successful export. Not when the scan
ends, and not when the window closes: closing a window is not a decision to
throw away a scanned batch. On the next start the newest unfinished session is
offered in the File panel and the older ones are cleaned up.

Seven new cases, all passing, ending with `batch_survives_a_crash`: a real scan
from the simulator, spooled page by page, abandoned without saving, then
recovered byte-exact and exported as a PDF.

Also verified outside the suite: two crashed sessions planted on disk, the studio
started and closed, and afterwards exactly the newer one remained - proving the
startup path ran, kept the recoverable batch, and did not quietly eat it.

**Still not built:** barcode and patch-code separation, reordering or re-scanning
an individual page, and job presets. Blank-page dropping is still applied when
saving rather than as pages arrive, so blanks are visible in the filmstrip until
export. The recovery button itself was exercised through the engine, not clicked.

#### Colour rendering and the batch strip (twelfth pass)

**The reported defect: whites blowing out on ordinary scans.** Paper whitening
defaulted to 40 and worked by adding a flat amount to R, G and B for every pixel
brighter than a luminance of 170. Three faults, all visible on real paper:

- a hard cutoff at one luminance leaves a contour line across any smooth
  gradient, lit on one side and not the other;
- adding an equal amount to three channels changes their ratios, so a warm
  off-white walks towards neutral - colours looked broadly right while light
  areas quietly lost their tint;
- nothing stopped a channel reaching 255, so highlight detail was destroyed
  rather than brightened.

Replaced by `Core\ToneEngine.cs`. Polish now **multiplies** rather than adds, so
the channel ratios and therefore the hue are untouched; it fades in over a range
with a smoothstep instead of switching on at a threshold; and the multiplier is
capped one level short of white, so a pixel that arrived below 255 is still below
255 afterwards. Measured: worst ratio drift 0.0032, largest one-level step 2
levels (the old code stepped about 25 at its cutoff), zero channels driven to
clipping at any preset's strength.

**The default is now Original colour** - identity curve, polish 8 - and
`tone_original_is_faithful` asserts the whole page comes back pixel for pixel
when polish is off. Seven presets: Original colour, Photo, Document colour,
Greyscale, Black and white text, Faded document, Negative invert. Each carries
its own polish strength, and picking one resets polish to that value rather than
carrying a document-strength lift into Photo.

Two of them do work that a fixed curve cannot:

- **Black and white text** thresholds per area rather than per page, from a
  summed-area table, so the window costs nothing however wide it is. One global
  number fails on everything real - a book gutter, a lamp brighter on one side,
  a photocopy darkening towards an edge. On a test page whose paper fades from
  245 to 115, adaptive kept the text evenly across both halves (balance 1.00)
  while a global threshold kept 0.21.
- **Faded document** finds the range the page actually uses, ignoring the
  brightest and darkest 0.5% so a speck of dust cannot set the limits, and
  refuses to stretch a nearly blank page - which would amplify sensor noise into
  something that reads as content.

The old `whitening` key in `scan.ini` is deliberately not read: the number meant
something else on the algorithm it belonged to, and its default of 40 is the
setting that caused the complaint.

**The batch strip** (`App\StudioBatchBar.cs`). Batch scanning previously lived in
a settings drawer: choose a source, open the File panel, set a split rule, come
back, press Scan. It is now one strip on the scanning page, and it is the same
object through the whole job - armed, running, finished:

```
armed     SOURCE Flatbed | PAGES One sheet | SPLIT One document | SAVE AS JPG | BLANKS Kept | [Start batch]
running   RECEIVED 7 pages | DOCUMENTS 3 | ELAPSED 0:42 | RATE 9.9 ppm        | [Stop]
finished  BATCH 24 pages, 3 documents - not saved yet                          | [Save now]
```

Every field is a control: clicking one opens a menu under it. Nothing navigates
anywhere, because a batch is decided in the seconds between loading the tray and
starting, and navigation in that window is a reason to give up and scan one page
at a time instead. The progress hairline is determinate only when a page count
was asked for; a run until the tray empties gets a sweep, because a bar
pretending to know the total is a lie the operator notices at page 51 of "50".

The strip edits the same settings object the drawer does, so the two cannot
disagree; the drawer controls are refreshed when the strip changes them.

**A design fault caught by looking at it.** With the strip open the page had
three buttons filled in the accent colour - Scan, Batch and Start batch - which
says nothing about which one to press. The Batch pill stays a secondary control;
the strip carries its own primary action.

Nine tone cases added, all passing; 35 in `nsexporttest` overall, and the other
four suites unchanged.

**Not verified:** the running and finished states of the strip were reasoned
about, not watched - driving them needs a feeder this machine does not have.

#### Auto crop, single and multiple (thirteenth pass)

`Core\AutoCropEngine.cs` was written to a specification (`AUTOCROP_PROMPT.md`)
and arrived complete. It compiled against this build system first time and its
own self-test passes all 18 cases.

**Before this, "Find the document edges" and "Straighten automatically" did
nothing.** Both toggles set a settings flag that no code in the scan path read:
`DocumentDetector` was referenced only from `src\App\legacy\` and from tests. The
only auto crop reaching real scans was `ICAP_AUTOMATICBORDERDETECTION` - the
scanner's own, not ours. Both toggles are now live, and two more joined them.

**The engine.** Detection runs on a downscaled copy (1400 px long edge, box
averaged) and extraction at full resolution, which is both faster and more
accurate than working at 600 dpi where sensor noise dominates. It combines
colour distance from the estimated lid, gradient magnitude and local texture -
the second is what finds white paper on a white lid, where colour distance alone
cannot. The min-area rectangle is only a starting point: each of the four edges
is re-fitted to the actual boundary and the corners taken from their
intersections, which is what makes a rotated page come out exactly rather than
nearly right.

**Independent tests: `nscroptest.exe`.** The engine's own suite passing is worth
something, but a suite written beside the code covers what its author thought
of. Twelve further cases were written against the specification instead, and
probe what the engine's own suite leaves alone:

```
page_not_text_block        5.00x6.99 in, High   - cropped to the paper, not the print
dark_page_on_dark_lid      4.00x6.00 in, Good   - the mirror of white-on-white
gray8_input                extracted 756x1056, still 1ch 8bpc
bilevel_input              found 5.00x6.99 in at 1 bit per pixel
margin_is_applied          0 mm -> 750 px, 6 mm -> 821 px
max_regions_honoured       9 found, capped to 4
deterministic              identical across two runs
thread_safe                4 concurrent calls agreed
source_is_not_modified     pixels and geometry unchanged
extract_size_matches_region  750x1050 px for a 5.00x6.99 in region
disabled_returns_page      one page, unchanged
real_scan_through_engine   1200x1500 from the fake DSM, declined, 21 ms
```

`page_not_text_block` is the one worth keeping: a sheet with wide margins and a
small block of print must crop to the sheet. Cropping to the ink is the classic
failure of this feature and is what makes people switch it off.

`real_scan_through_engine` declining on a full-bed pattern is the correct answer,
not a miss - there is no border to find, so the engine says so instead of
inventing one.

**Wiring.** `ApplyAutoCrop` runs inside the scan callback, so one acquired page
becomes one or many session pages before anything else sees it: filmstrip,
journal, export and the Photoshop handoff all treat each item as a page in its
own right, which is what makes four cards on the glass become four files.

Two decisions worth recording:

- **A crop the operator drew themselves always wins.** They have already said
  which part of the glass they want; a detector second-guessing that turns a
  deliberate selection into a surprise.
- **The bed view is suppressed once auto crop has changed a page.** That view
  places a scanned page back inside the region requested from the scanner; after
  a crop the page no longer fills that region, and drawing it there would put it
  in the wrong place on the glass.

Settings `AutoCrop` and `MultiRegionCrop` both default to on and are now
persisted (they never were). The Scan page carries both toggles, with the
multiple-item one greyed while edge finding is off, since finding one document
is a prerequisite for deciding there are four.

**Not verified:** everything here ran on synthetic pages and the simulator. No
real photograph, ID card or printed page has been through it on the Canon
LiDE 400.

#### Auto crop was invisible until after the scan (fourteenth pass)

Reported as "nothing happened": both auto crop toggles on, cards on the glass,
no effect. The evidence was in the status bar of the screenshots - *100 dpi,
827x1169, 0 pages in session* - which is a **preview**, and auto crop was wired
only into the scan callback. So nothing happened, literally and correctly, and
there was no way to tell the feature existed until a full scan had already been
committed to.

Every competing suite draws the detected boundaries on the preview. That is the
feedback that makes the feature trustworthy, and it was missing.

`StudioCanvasView` gained `DetectedRegions`, drawn as **quads rather than
rectangles**: a detected document is usually a degree or two off square, and
drawing it upright would hide exactly the part of the detection worth checking.
With several items each is numbered, because that order decides which page is
which in the filmstrip and in the saved files.

After a preview the shell now runs the engine and:

- **one region** - moves the scan box onto it, so pressing Scan captures that
  document and nothing else, and reports its size and skew;
- **several regions** - leaves the box alone, since the whole platen has to be
  scanned for the items to be cut out of it afterwards, and says how many pages
  Scan will produce;
- **none** - says so, rather than leaving the operator to guess.

**A test that looked like an engine bug and was not.** Two ID cards on an A4
platen at preview resolution came back as four regions: `3.36x0.21 in` and
`0.83x1.11 in` - the green header strip and the photo panel of each card. The
engine was detecting the *contents* of the cards rather than the cards, because
pale card stock on a pale platen is nearly invisible while its printed panels
are bold.

The cause was the test, not the engine. The synthetic cards had no edge shadow,
and a card is about 0.8 mm thick, so its edge casts a thin dark line even with
the lid shut. That line is the only strong evidence a pale card exists at all,
and a test without it is testing something no scanner produces. With a
physically realistic edge added, both cases pass exactly:

```
id_cards_at_preview_dpi   2 cards at 100 dpi: 3.36x2.13 in; 3.36x2.13 in
one_card_at_preview_dpi   3.36x2.13 in
```

3.36 x 2.13 in is ID-1 format to within a hundredth of an inch.

Worth keeping in mind: the engine leans on that edge shadow when a document and
the platen are similarly pale. A very thin or very evenly lit item may still be
read by its contents instead of its border. Nothing has been done about that
because nothing has yet shown it happening on real hardware - it would be a
guess piled on a guess.

**Still not verified on real hardware.** All of this is synthetic pages and the
simulator.

#### Why auto crop never ran: two flags that were not the same thing (fifteenth pass)

Reported again after the preview overlay was added: previews showed no boxes and
a scan of two ID cards reached Photoshop as one whole A4 page.

The status line in the screenshot gave it away - *"Scan area: 8.27 x 11.69 in,
0.12 in from the left"*. A selection was active. And `ApplyAutoCrop` began with

```csharp
if (page == null || !_settings.AutoCrop || _lastScanUsedManualCrop) return single;
```

on the principle that a crop the operator drew themselves should win. The
principle is right. The flag was wrong.

`_manualCrop` is set by `_canvas.CropChanged` as `!IsFullCrop(...)`, and it fires
for **every** change to the box, including the ones the shell makes itself. A4 is
8.27 in wide; the LiDE 400 platen is 8.5 in. Selecting a paper size therefore
sets a box covering 0.97 of the glass - not the full bed, and so indistinguishable
from a hand-drawn selection. With any paper size chosen, which is the normal
state of the application, auto crop stood aside on every single scan.

Split into two flags:

- `_manualCrop` keeps its old meaning: the scan region is smaller than the bed.
- `_cropDrawnByUser` is set only when the change did not come from the shell.
  Every programmatic assignment to `_canvas.CropNorm` is now bracketed by
  `_settingCropInternally`, and only auto crop consults the new flag.

`StartScan` clears `_settingCropInternally` as a self-heal: if an exception ever
left it set, every later drag would be read as the shell moving the box and auto
crop would override selections drawn by hand - a silent misbehaviour worth one
line to make impossible.

**The feature was also silent, which is why it took two rounds to find.** A page
that comes back whole looks identical whether the detector declined, was
switched off, was never asked, or threw. The post-scan status now says which:
`auto crop separated 3 items`, `auto crop trimmed the page`, `auto crop found no
border to trim`, `auto crop is off`, `your own selection was used`, or the
exception message.

**Lesson worth keeping.** Both of the last two rounds were the same mistake in
different clothes: a feature wired in without any way to observe what it decided.
The engine had thirty passing tests and was never the problem; the wiring around
it had none, and was the problem twice.

#### What the diagnostic dump actually showed (sixteenth pass)

The preview the scanner really produces was captured and run through the engine
offline. Three rounds of reasoning from screenshots had produced three wrong
theories; ten minutes with the actual pixels settled it.

**Two separate faults, and one of them was mine from the previous pass.**

**1. `_cropDrawnByUser` was write-only.** The dump said `cropByUser : True` on a
page nobody had drawn on. The flag is set on a drag and was never cleared
anywhere, so a single drag - or a preview that happened to find one document -
switched auto crop off for the rest of the session. It is now cleared when a
preview arrives and when a paper size is chosen, both of which replace whatever
was selected before.

**The guard added in the previous pass was built on a wrong belief and has been
removed.** `_settingCropInternally` existed to stop programmatic changes being
read as drags - but `CropNorm`'s setter does not raise `CropChanged` at all;
only a real mouse-up does. There was never anything to guard against. Machinery
that encodes a mistaken model of a control is worse than no machinery.

**2. This scanner's 100 dpi preview genuinely defeats the detector.** Measured
from the captured page:

```
background 226,225,228   noise 11.9   colour threshold 35.6
mask on 101026 of 993650 pixels
one component: 764 x 647 px, from (2,2)
```

The platen is not flat - a wide soft smudge and faint vertical streaking put the
median absolute deviation at 8 levels, which sets the colour threshold at 35.6.
The pale green card body is only about 25 to 30 levels from the lid, so most of
each card fails that test while parts of the streaking pass it; the closing pass
then bridges everything into one 764 x 647 blob that is no longer card-shaped.

Things that did **not** help, each tried and measured: moving the items away from
the glass edges, painting out the dark bezel row, flattening the background over
64 and 32 px blocks, cropping to the top 30% away from the smudge, disabling the
downscale, dropping MinConfidence to None, and box blurs from 1 to 6 px. Blur is
the informative one: it left `noise` at exactly 11.9, which proves the figure
comes from a slow gradient across the platen and not from high-frequency noise.

Trimming three pixels off every edge **does** work - `2: 3.39x2.02 | 3.37x2.02` -
which is a useful clue for later but not a fix, since it also clips the cards.

**Where that leaves it.** Detection now runs on the full-resolution scan, which
is a far better image than the preview: cleaner, and four times the detail. The
preview overlay stays as a hint and says plainly when it cannot see anything. The
diagnostic dump now also fires when a *scan* comes back untrimmed, downscaled to
about 1400 px so the file stays a few megabytes rather than fifty, so the next
failure arrives with its evidence attached.

**Lesson, again, and more expensively this time.** Three passes were spent
reasoning from screenshots of a window. The fix each time was in the wiring, and
the wiring had no observability at all. The dump should have been the first thing
built, not the fourth.

#### Two cards became two pages (seventeenth pass)

Working from the real scan rather than from screenshots, at last.

**The 100 dpi preview is a dead end on this hardware, and the 300 dpi scan is
not.** Run against the actual file the app handed Photoshop, the detector found
the cards straight away - as `1: 7.00x2.06 in`, a single region spanning both.
Faint streaking on the platen bridges the 5 mm gap between them in the evidence
mask, so they arrive as one blob. The detector's own fragment-merge rule was not
at fault: its limit is 2.5 mm, and it never saw two fragments to merge.

**`Core\RegionSplitter.cs`.** Rather than re-tune evidence that is balanced
against many other cases, this asks a much narrower question of a finished
region: does a clean band of background run the full height across it? Two
documents side by side always have one; a single document with a white margin
down its middle does not, because its own edges interrupt the band. Rotated
pairs have no straight gutter, so nothing fires and the detector's answer stands
- the right failure, since a wrong split is worse than a merged pair the operator
can see.

Three measured mistakes on the way, each worth recording because each looked
like a fix:

- **A gutter minimum of 3 mm missed a 5 mm gap.** Only 2.4 mm of the gap is
  perfectly clean; each card's edge casts shadow a millimetre into it. The limit
  is 2.0 mm for that reason, and a plausibility check on the resulting parts -
  each must be between 1:5 and 5:1 - is what keeps that honest rather than a
  wider threshold.
- **A fixed ink threshold cannot work.** 42 read the streaky real platen
  correctly but called blank card stock "background" on a clean synthetic page;
  24 did exactly the reverse. Card stock sits only about fourteen levels above a
  platen, which is the whole difficulty. The threshold is now measured from the
  spread of the background ring itself, so a calm platen gets a low bar and a
  streaky one a high bar.
- **The multiplier on that spread is fitted, not derived,** and the comment says
  so: 3.5 puts the real page's bar at 46 where the gap measures 30 clean columns,
  and leaves the synthetic page at its floor of 18, below card stock at 42.
  Near 2.5 the first fails; near 6 faint print starts reading as empty.

Result on the real scan, against a true ID-1 card of 3.37 x 2.13 in:

```
engine   1: 7.00x2.06
split    2: 3.52x2.05 | 3.39x2.06
```

Three regression cases added: two cards 5 mm apart split; one printed sheet is
left alone; a two-column table with a 4 mm rule is left alone.

**Still honest about what is not solved.** The preview on this scanner still
detects nothing - its background gradient sets the detector's own colour
threshold at 35.6 while the pale cards sit 25 to 30 levels from the lid. Auto
crop therefore does its work on the scan, and the preview overlay is a hint that
sometimes has nothing to show. And the left card comes back 3.52 in against a
true 3.37: the split takes the gutter centre rather than each card's own edge.

#### Rebuilt around the preview, with our own detector (eighteenth pass)

Six rounds of "not working" ended when the right question was finally asked of
the real preview: is the evidence missing, or is it being discarded?

**It was being discarded.** A deliberately plain pass over that page - flatten
the illumination, threshold, label components - finds **both cards at every
threshold tried**:

```
drop  6   3.63 x 2.07 at 0.058   and   3.45 x 2.07 at 0.504
drop 14   3.48 x 2.07 at 0.073   and   3.42 x 2.07 at 0.504
drop 24   3.46 x 2.07 at 0.073   and   3.41 x 2.07 at 0.504
                        (the cards are 3.37 x 2.13 in, at x = 0.07 and 0.49)
```

AutoCropEngine on the same page: nothing at all as delivered, and exactly one
card after the illumination was removed, whatever the confidence floor or the
working scale. Its policy layer was throwing the other one away. Every attempt
to coax it was wasted effort, and that was a bad call held for too long.

**`Core\PlatenDetector.cs`** now does the finding, with every threshold under our
own control, and hands each item it finds to AutoCropEngine **on its own small
image** for the corner fitting - which is what that engine is genuinely good at,
as both its suite and ours show. Each is used for what it does well.

Two steps turned out to be load-bearing:

- **Flattening the illumination is not optional.** This platen carries a soft
  smudge and faint streaking; the engine reads that spread as sensor noise and
  lifts its colour threshold to 35.6 while a pale card sits 25 to 30 levels from
  the lid. A coarse grid of high percentiles, interpolated between block centres,
  removes it. A global quadratic surface was tried and rescued only one card: the
  smudge is local, and one surface cannot follow it.
- **Holes must be filled before anything judges solidity.** A pale card on a pale
  lid arrives as the shadow around its own edge - an outline, not a shape - and a
  solidity test rejects outlines. Filling from the frame inwards turns the
  outline back into the card.

**The design is now what was actually asked for.** The preview decides and the
scan obeys:

- Preview runs the detector and draws what it found, numbered where there are
  several.
- That result is kept as a **crop plan in inches on the glass**, not as a
  fraction of the preview, because the scan may cover a different area and inches
  are the one description both agree on.
- Scan cuts the page into exactly those pieces. **It never detects anything of
  its own.** Pressing Scan with no preview returns the whole selected area
  untouched, which is the behaviour asked for and also the safer default:
  cropping something nobody has been shown is how a scan comes back trimmed in a
  way nobody wanted.
- The plan is dropped when the paper size changes, when the selection is dragged
  by hand, or when either auto crop switch changes.

**The settings are now findable.** They were two switches at the bottom of
"Geometry"; they are now their own **Auto crop** section on the Scan page, with
the rule written underneath it.

**A regression test pinned to the real page.** `tests\real\` holds the actual
LiDE 400 preview, and `platen_real_lide400_preview` asserts two items of about
3.37 x 2.13 in at x = 0.07 and 0.49. Every synthetic page written for this
feature was easier than that one, which is precisely why three rounds of fixes
passed their tests and failed on the hardware. **It contains personal data, is
excluded by `.gitignore`, and the case skips with a note when the file is absent.**

Current measurements:

```
real 100 dpi preview   2 items   3.48x2.07 at 0.073   3.42x2.07 at 0.504
real 300 dpi scan      2 items   3.40x2.08 at 0.061   3.39x2.08 at 0.499   (edges fitted)
synthetic pair         2 items   3.38x2.14 at 0.064   3.38x2.14 at 0.493
```

**Bugs found and fixed during this rebuild**, both caught only by screenshotting
the running app:
1. Owner-drawn controls with `BackColor = Transparent` ghost previously painted
   text on every repaint — WinForms never clears the background for them. Every
   control now clears with `Theme.BackOf(this)` first.
2. `ShowSection()` rebuilds the drawer after a probe, and the device dropdown was
   hard-coded to index 0, so it disagreed with the device the shell had actually
   selected. It now resolves the live device.

---

## 4. Not started

Everything else in the master plan. Most notably:

- **WSD network transport** (§7.4) — not implemented; eSCL/mDNS exist and eSCL has simulator coverage.
- **Imaging pipeline integration** (§9): RawImage detection, curves and deskew estimators exist; complete full-depth adjustment/export integration remains unfinished.
- **AI subsystem** (§10) — still the Python `ai_doc_cascade.py` side-process
- **PDF / OCR / MRC output** (§11)
- **Film module, IT8, ICC colour management** (§10.3–10.6)
- **Photoshop connectors** (§14) — still the legacy `.jsx`
- **Batch/jobs, licensing, installer, quirks DB** (§12, §15, §19, §7.6)
- **Remaining test coverage**: real-device matrix, GUI workflows, long watchdog / cancellation and installer tests. Imaging, TWAIN golden, eSCL simulator and audit regression suites exist.

---

## 5. How to build and run

```powershell
# engine + hosts + CLI
cd C:\PS_Fix\NextScan
.\build.ps1

# the Photoshop helper, against the native engine
cd C:\PS_Fix
.\build_scan_native.ps1
```

```powershell
# diagnostics
.\NextScan\bin\nsprobe.exe list
.\NextScan\bin\nsprobe.exe caps "LiDE"
.\NextScan\bin\nsprobe.exe scan "LiDE" --dpi 300 --out page.png
```

To go back to the old behaviour without rebuilding, set `engine=naps2` in
`C:\PS_Fix\scan.ini`. `build_scan.ps1` (the original NAPS2-only build) is
untouched, and `backup_original\scanhelper.cs.pre-native` is the pre-integration source.

---

## 6. Suggested next steps

1. **Expand the existing TWAIN simulator coverage (plan §18.3)** and add real-device acceptance checks; see the current audit backlog.
2. **Verify colour channel order** with a colour target.
3. **Port the imaging pipeline** from `scanhelper.cs` onto `RawImage` so 16-bit
   data survives (it currently round-trips through `System.Drawing.Bitmap`, which
   caps everything at 8 bits per channel).
4. **eSCL transport** — the biggest coverage win per unit of work, and it needs no
   vendor driver at all.
5. **Quirks DB** — there are already two real entries to seed it with (LiDE 400
   bit depth, LiDE 400 phantom film source).

---

## 7. Delivered-page quality, 2026-09-19

Until now every auto crop test asked whether the detector **found** the right
thing. Nothing asked what the file the operator actually receives looks like,
and that is the half they judge. Two faults lived in that gap for a long time.

### Measured, on the owner's own bed

`tests/real/lide400_mixed_20260919_1646.png`, five items, through the same
`CropPlan` the application uses:

| item | delivered | off square | edge bleed |
|---|---|---|---|
| 1 ID card, tilted 19.4 deg | 87.1 x 54.9 mm | +0.27 deg | 5.2 % |
| 2 ID card, tilted 5.5 deg | 85.9 x 54.4 mm | +0.19 deg | 0.1 % |
| 3 photograph | 44.7 x 52.6 mm | -0.49 deg | 0.0 % |
| 4 photograph | 41.1 x 50.0 mm | -0.37 deg | 8.9 % |
| 5 passport, reaching the frame | 175.8 x 128.0 mm | not measurable | 3.0 % |

"Off square" is a projection-profile reading of the document's own edge inside
the delivered raster, with the raster's own boundary suppressed; a reading is
only reported where the angle sweep peaks clearly above its own background.
The passport's interior has no straight structure to measure, so no number is
claimed for it.

### Fixed

1. **Straightening was abandoned outright for any item whose quad left the
   page, and for anything at 45 degrees.** `Atan2` returns -45.000002 for a
   card at 45 degrees; `AutoCropEngine.Usable` refuses an angle whose magnitude
   exceeds 45, and `PlannedCropExtraction` never folded it. The caller then
   delivered the item's upright envelope - the item still crooked, inside a box
   of platen. Folding the angle into +/-45 fixes the rounding case; a clipped
   quad now goes to the polygon path, which already samples background beyond
   the capture. `delivered_page_is_square` sweeps thirteen angles from 0 to 89
   and every one now arrives within 0.33 degrees of square; before the fix, 45
   degrees arrived 44.97 degrees out, as a 584x584 diagonal envelope.

2. **An edge that was never measured voted on the angle.** `PlatenEdgeFitter`
   averaged `lines[1]` and `lines[3]` unconditionally. Where a document runs
   past the end of the capture, one of those is a line along the frame: dead
   flat, strength 0.0, no part of the document. Averaging halved the tilt. The
   owner's passport measured -2.3 degrees on its three real edges and was
   recorded as -1.2. Only edges with measured support now vote; the passport is
   now recorded at -2.5. `clipped_edge_does_not_vote` covers it, and was checked
   against the old code, which halves every angle in it.

### Measured, and NOT a fault after all

The first reading said "edge bleed 8.9 %" and that was blamed on platen kept
inside the crop. Measuring the depth of it per side, in millimetres, says
otherwise: no delivered page keeps more than **0.51 mm** (two pixels at 100 dpi)
of platen on any side. The percentage was counting the photograph's own dark
content near its border. Two of the owner's three example images turned out to
be viewer letterboxing rather than anything in the file.

The one real dark band is the passport's bottom, **4.06 mm**, where the capture
itself stopped. Nothing was scanned there, so there is nothing to trim back to
and nothing that may be invented. The delivery test exempts only sides whose
plan reaches the frame, and says so in its output.

### Fixed, second pass: the passport arriving in two pieces

On the bed of 17:12 the same passport came back as two fragments - its left
page, and a printed panel from inside its right page. The decision log named
the cause exactly:

```
rejected enclosing boundary {X=0.086, Y=0.575, Width=0.822, Height=0.425}:
   an unmeasured frame edge cannot replace observed stock edges
   blocked by {X=0.521, Y=0.752, W=0.282, H=0.242}
   [line strengths 20.7,10.8,23.6,15.4; broad gradient support: review softened edge]
```

The whole-passport boundary was proposed, fitted and validated. It was then
refused by `ReplaceContained`, because it leans on the frame where the capture
stopped and a contained item did not. That rule is right in principle - a
boundary resting on the image frame should not swallow a separately measured
document - but the thing objecting here was **printing on the passport's own
page**, not a document. Its edges are a broad tonal gradient; real stock gives a
sharp transition with a shadow beside it, and the fitter already says which it
saw. A contained item whose own edges are soft no longer blocks.

The passport is now one item of 6.89 x 4.89 in, matching the 16:46 capture's
6.89 x 4.88 for the same object. `platen_mixed_20260919_1712` asserts five
items, a whole passport, and that no accepted item lies inside another; it was
checked against the old code, which splits the passport in two.

**This was not caused by the angle-vote change above.** The 16:46 page still
returns five items with the passport whole under the current code; the 17:12
bed is a different arrangement, and a harder one.

### Not fixed, and still wrong

- **Oversize that grows with tilt.** Both ID cards are the same ID-1 stock,
  85.6 x 54.0 mm. The one tilted 5.5 degrees is detected 85.6 x 54.1; the one
  tilted 19.4 degrees is detected **86.6 x 54.4** and delivered 87.1 x 54.9.
  A flat scale error would affect both equally, so this is tilt-dependent and
  specific to real edges: the synthetic sweep is accurate to 0.18 mm at every
  integer angle from 0 to 89. This is the same open defect the previous round
  recorded, and it needs ruler truth to chase responsibly rather than a
  correction tuned until one page looks right.
- **A landscape document clipped at the bottom reads 1.87 degrees when drawn at
  3.0.** Within +/-2.5 degrees the same fixture is accurate to 0.06. Whether
  this is the fitter or the synthetic's own edge rendering is not yet settled,
  so `clipped_edge_does_not_vote` deliberately stops at 2.5 degrees rather than
  claim a range that has not been verified.
- Ruler truth for the private references still does not exist. Every dimension
  above is a measurement, not a ground truth.

### New in Core

`CropPlan` now owns converting a detection into a plan in bed inches and
cutting a scan with it. Both used to be inline in `StudioShell`, so a test could
only reach them by copying the arithmetic - and a copied calculation is how a
suite stays green while the field fails.

---

## 8. Stability work, 2026-09-19 evening

Research was read before changing anything, and most of what it suggested was
then measured and rejected. Recorded in full so the same ground is not covered
twice.

### What was tried, and what the measurements said

| change | synthetic angle error (90 angles) | synthetic size error | real pages |
|---|---|---|---|
| baseline | 0.179 deg | 0.180 mm | 72 cases pass |
| sub-pixel centroid of the gradient | **0.038 deg** | 0.191 mm, but bias +0.315 mm | 1 case lost |
| sub-pixel parabolic vertex | 0.176 deg | 0.180 mm, bias +0.040 mm | 1 case lost |
| centroid for direction, vertex for position | **0.038 deg** | **0.180 mm** | **2 cases lost** |
| Theil-Sen instead of least squares | 0.091 deg (worse) | - | 1 case lost |

The literature is clear that a gradient peak can be located to a fraction of a
pixel, and that the centroid is the more stable estimator on blurred edges while
a parabolic vertex is the more accurate one on sharp edges. Both held here, on
synthetic fixtures. **Neither improved a real page**: the tilted ID card on the
16:46 bed measured 86.6 mm before and 86.9 mm after, and the hybrid also broke
scale consistency - the same content replicated to 300 dpi changed angle by more
than half a degree, which is the opposite of stability.

Theil-Sen was tried for its 29 % breakdown point against samples landing on a
fold or a shadow. It measured worse: these edges are clean enough that the
efficiency of least squares matters more than robustness to outliers.

**All of it was reverted.** A 4.7x improvement on a synthetic metric that does
not move a real page, at the cost of scale consistency, is not an improvement.

### The premise was wrong, and that matters more

The 1.3 mm disagreement between the 16:46 and 17:12 captures was taken as a
repeatability figure. It is not: the bed was rearranged between them - a card
moved from 5.4 to 14.8 degrees. Two captures of a rearranged bed measure nothing
about repeatability, and the sub-pixel work was aimed at a target that was not
there.

Two cases now measure stability honestly, without needing the owner:

- `stable_across_placement` - the same card nudged by a third of a pixel, nine
  times. Width spread **0.012 mm**, height 0.024 mm, angle 0.021 deg. Pixel
  quantisation is not a source of error here, which is why sub-pixel refinement
  had nothing to give.
- `stable_under_sensor_noise` - the same bed, six passes, +/-3 levels of noise.
  Width spread 0.019 mm. **Height spread 0.955 mm.**

### The real instability, located

Five passes in six agree to 0.02 mm. The sixth reads 55.10 mm where the others
read 54.14. It is not a gradual sensitivity but a **discrete flip**: noise tips
the edge search from the stock edge to the outer edge of the item's own shadow,
about a millimetre out. That is exactly the fault the owner describes - mostly
right, occasionally a millimetre out, with nothing about the page to explain it.

Strengthening the existing preference for the nearer line (0.10 to 0.30) did not
move it, so the flip is decided elsewhere - the shadow resolution, or the
component mask itself changing shape under noise. Reverted rather than left in
as an unexplained constant.

**This is open.** The bound in the test is set where the behaviour is today so
any worsening is caught, and the real figure is printed on every run. It is not
a claim that 0.96 mm is acceptable.

### Also fixed

A fitted extent is now required to be physically possible, not merely positive.
Two opposite edges resolving a fraction of a millimetre apart used to be
accepted, and a 0.1 mm wide "document" reached the operator as an extra region.

### The glass taken for a document, full bed

With the paper size set to **Maximum** the preview takes in the platen's own
border, and a boundary drawn round the whole of it fits perfectly well: four
edges, complete perimeter support 1.00, quiet exterior 1.00. It was accepted and
superseded the five real items on the glass. The bed came back as one
8.47 x 11.63 in region and the operator saw no outlines at all.

The component path has refused this for a long time - reaching three sides of
the frame means the glass rather than something on it - but the proposal path
had no equivalent, and that is the path this boundary came through.

A proposal is now refused when, **after fitting**, it covers 98 % of the capture
in both dimensions. Both dimensions matter: A4 on this platen is 97.2 % of its
width and Letter is 94 % of its height, so a real sheet filling the bed one way
round is still found. Checked after the fit rather than before, because the seed
is smaller and only grows to the frame once its edges are fitted.

`platen_fullbed_20260919_1735` covers it: five items, none of them the glass.
**This was not a regression from the printed-panel change** - the same capture
fails identically with that change removed.

### A portrait refused on one busy side, 17:41

On the 17:41 bed the right-hand portrait is found, fitted, and carries
**complete perimeter support 1.00** on all four measured edges. It is then
refused because ONE of its four sides scores **quiet exterior 0.22** against a
required 0.70. `ExteriorSupport` returns the MINIMUM of the four sides, so any
one busy side vetoes the item - and a side is busy for reasons that have nothing
to do with the item: a neighbour a few millimetres away, the lid shadow falling
one way, a smudge on the glass.

Taking the second worst side instead of the worst was tried, on the reasoning
that a panel printed on a larger sheet has sheet on all four sides and so cannot
score well on three of them. It recovers the portrait and then **merges the two
cards of `platen_real_lide400_preview` into one 4.68 in region** and invents an
8.47 x 1.81 in region on another reference. The strictness is load-bearing;
reverted.

`platen_mixed_20260919_1741` asserts the four the detector returns today and
names the defect in its own output. Four is not the right answer.

**Not a regression from the whole-capture guard** - the same capture misses the
same portrait with that guard removed.

### The passport 3 mm short at the foot of the scan

Measured on `lide400_mixed_20260919_1746.png`: the passport's fitted bottom sits
at **row 1157 of 1169**, and the twelve rows below it hold median luminance
175-185 - the same as the passport above the line. There is no transition in the
image to justify an edge there. Three millimetres of a real document are cut off.

The cause is `RemoveEnclosingFrame`. A strip is blanked all round the capture so
the platen's own border is not read as a document, and the log names its width:
`removed enclosing frame evidence through working inset 11`. Anything that
genuinely runs off the end of the scan is truncated by that same strip, and the
fitter then measures an edge where the MASK was cut rather than where the item
ends.

The fitter already handles a seed that arrives at the frame - it marks the edge
clipped and places it at the capture boundary, measuring nothing beyond. The
seed simply never gets there.

Extending any blob that reaches the inner side of the cleared band out to the
frame was tried. It did not move the passport at all, and on the 17:41 bed it
merged two items into a single 7.47 x 8.91 in region: growing a blob's rectangle
makes it overlap its neighbours. Reverted.

**Open.** A fix has to extend the item's own boundary towards the frame without
growing its rectangle into anything beside it - which means doing it per edge in
the fitter, with the band width passed in, rather than on the blob.

### Two unmeasured sides are not a measurement, 17:52

The 17:52 bed came back as **three** items: the two cards, and one 7.47 x 8.92 in
region that had swallowed both portraits and the passport. The passport had
already been accepted correctly - `accepted shadow-separated stock {0.128,
0.596, 0.814, 0.393}` is in the log - and was then superseded.

The swallowing region's own evidence:

```
7.47 x 8.92 in, line strengths 26.3, 16.2, 0.0, 0.0
   reaches image frame; broad gradient support: review softened edge
```

**Two of its four edges carry no evidence at all.** An edge clipped by the
capture has no strength by construction - the fitter places it at the frame and
says so - and one of those is reasonable: a passport running off the end of the
scan still has three real edges and a known visible extent. Two is a guess in
both directions, and this guess outranked three items that had each been
measured on all four sides.

A fit now fails when two or more of its four edges are unmeasured. The 17:52 bed
returns four items with nothing nested inside anything else, and all 77 other
cases are unaffected.

**Not a regression from the printed-panel change** - the same capture fails
identically with that change removed. Checked, as with the previous two.

### FIXED: the portrait refused on one busy side

Four references bracketed this one - refused on the 17:41, 17:52 and 17:58 beds,
accepted on 17:46, same portrait and same scanner each time. `ExteriorSupport`
returned the MINIMUM of its four per-side scores, so any single busy side vetoed
the item, and a side is busy for reasons that have nothing to do with the item:
a neighbour a few millimetres away, the lid shadow falling one way, a smudge.

Taking the second worst side had already been tried and reverted, because it
merged the two cards of `platen_real_lide400_preview` into one 4.68 in region.
Printing the per-side figures rather than the minimum showed why, and showed the
discriminator:

| candidate | sides measured | per side | worst | second worst |
|---|---|---|---|---|
| the portrait (wanted) | **4** | 0.22, 1.00, 1.00, 1.00 | 0.22 | 1.00 |
| the card-merging boundary (not wanted) | **3**, one on the frame | 0.06, 1.00, 1.00 | 0.06 | 1.00 |

Second-worst is 1.00 for both, which is exactly why that attempt failed. The
number of MEASURED sides separates them cleanly. One busy side is now forgiven,
but only for a candidate whose four sides were all measured: one that has
already given up a side to the image frame does not get a second concession on
top of it.

All four beds now return five items, the portrait at High confidence on three of
them, and the two-card reference still returns two.

`ExteriorSupport` also lost a mutable static that had been added for the
diagnostic; the per-side figures travel back through an out parameter, because
detection runs on more than one thread and a test enforces that.

### A soft-edged region lying across the passport, 17:58

The 17:58 bed delivered a 2.29 x 2.49 in region straddling the top edge of the
passport - partly above it, mostly inside it - as a page of its own:

```
2.29 x 2.49 in at 0.565,0.528, line strengths 28.2, 116.1, 14.9, 21.1
   broad gradient support: review softened edge
```

A broad tonal gradient is what a shadow or printing produces; real stock gives a
sharp transition with a shadow beside it, and the fitter already records which
it saw. A region whose own edges are soft, sitting on top of something measured,
is that item's shadow or its print, not a second document lying across it.

Such a region is now dropped. The check runs once over the finished list rather
than inside `Covered`, because the two can be accepted in either order by
different passes - here the soft region was accepted at log line 232 and the
passport at 435, so `Covered` never saw them together. Overlap is measured
against the soft region's own area, so a large weak region cannot dilute its way
past by being big.

`platen_mixed_20260919_1758` asserts that no accepted item lies across another.

---

## 9. Why this kept taking so long, 2026-09-19 evening

Two findings explain most of a week of one-step-forward failures.

### The diagnostic destroyed its own evidence

`SaveCropDiagnostic` downscaled anything over 1400 pixels before writing it, "so
the file stays a few megabytes rather than fifty". On a 300 dpi preview that
meant:

```
source : 2552 x 3507 @ 300 dpi      what the detector saw
saved  : 1018 x 1400                what the file contained
```

So a 300 dpi failure could not be reproduced from its own diagnostic, because
the reduction was part of what went wrong. Every attempt to debug one was
working on a third-size copy. The page is now written exactly as the detector
saw it, at whatever it cost in megabytes: a diagnostic that alters the evidence
is worse than no diagnostic.

The diagnostic also **overwrote itself** on the next preview, and the first
thing anyone does with a wrong preview is press Preview again. The last five are
now kept as `previous1` .. `previous4`.

### Real 300 dpi had never been tested

Every "scale" case in the suite enlarges a 100 dpi page three times and checks
the answer does not move. That tests the arithmetic, not the scanner. A real
300 dpi pass has its own noise, its own lamp behaviour and genuinely finer
edges, and the reduction to working size averages three pixels into one rather
than none. On one real 300 dpi bed a card measured **101 mm against a true
85.6**, and its neighbour was missed entirely.

`platen_real_300dpi` is the first case measured against a real one:
five items, both cards within 1 mm, 534 ms.

### What that says about the design

The thresholds in this detector are absolute level counts - `texture < 4`,
`drop 14`, `noise up to 3.5 levels` - and the comments record that they were
measured on 100 dpi pages. Averaging a 300 dpi page down to working size cuts
its noise by about three, so the same numbers no longer mean the same physical
thing. That is why fixing one bed breaks another: each constant is fitted to the
beds that existed when it was written.

The direction that addresses it is to express those thresholds as multiples of
the page's OWN measured noise floor, taken from an empty part of the platen,
rather than as absolute levels. That makes one rule mean one physical thing at
any resolution and on any scanner. It is a real piece of work and it is not
started; recorded here so the next round does not begin by tuning constants
again.

Ruler truth now exists for one item: the owner measured an NID at 3.4 x 2.11 in,
which agrees with the ID-1 standard of 85.6 x 54.0 mm to within 0.8 mm.

## 10. The noise-normalised edge pass, 2026-09-19 night

### The capture that started it

`tests/real/lide400_halfcard_20260919.png` (gitignored; real identity
documents). The preview looked plausible on screen. The numbers did not:

```
3.37 x 1.06 in at 0.036,0.064  0.6 deg  High
      line strengths 15.4,112.7,51.0,25.8; complete perimeter support 1.00;
      quiet exterior 0.97
```

The right width to a tenth of a millimetre and **exactly half** the height, at
High confidence with complete perimeter support. Every test the boundary had to
pass was about the printed line it had landed on, so every test passed.

### What the pixels say

Row profile down a column band belonging to that card alone, 300 dpi:

| row | reading | what it is |
| --- | --- | --- |
| 105-185 | flat 201-203 | bare glass above the card |
| 215 | trough to 78 | the card's top edge shadow |
| 551 | 154 -> 168 -> 178 -> 184 | where the engine put the bottom edge |
| 805 | trough to 136 | a dark band printed near the foot of the card |
| 863 -> 864 | 183 -> 217 | the card's real bottom edge |
| 864-899 | flat 219 -> 213 | bare glass below the card |

Two things follow. The engine's bottom edge at row 551 is not an edge at all: it
is a gentle rise that ends at 184, still on the card. And the card's pale margin
reads 195-202 against glass at 203-219 -- inside the grain. Region evidence
cannot see a white card on white glass, so the mask breaks into bands and a
boundary settles on the first printed line it finds.

### The discriminator, in units of the page's own noise

Sensor noise measured from the capture itself: sigma = 2.1 to 3.0 levels at
300 dpi.

| edge | trough depth |
| --- | --- |
| true top | 34.1 sigma |
| true bottom | 16.8 sigma |
| the printed line the engine chose | 3.8 sigma |

This is the noise normalisation section 9 called for and did not start. Sigma is
taken from the page being measured, so one rule means one physical thing at any
resolution and on any scanner, unlike the absolute level counts elsewhere in the
engine that were measured on 100 dpi pages.

### `src/Core/PlatenShadowEdges.cs`

Runs on `found` at full resolution, after `DropSoftRegionsLyingAcrossItems` and
before `Order`. Only ever moves an edge outward.

An item shows its border in one of two ways and the pass accepts either: a
**shadow trough**, cast on the glass by an item lying loose, or a **sharp step
onto glass**, from one pressed flat by the lid -- the card above has a 34 sigma
trough at its top and no trough at all at its bottom, where it is simply 183 on
one row and 217 on the next. Whichever qualifies furthest out is the outside of
the item; anything nearer is printed on it.

Five guards, each added because measurement showed it was needed, and each
verified by removing it and watching a case fail:

- **The gate.** An edge with bare glass beyond it is finished and is left alone
  whatever it scores. Without this the pass re-placed correct edges about 2 mm
  out and broke five cases.
- **Glass is recognised past the shadow, not at the edge.** The first tenth of an
  inch outside an item is its own shadow. Measuring there says "not glass" about
  every correct edge there is.
- **Glass is measured against a straight line, not a level.** The lamp falls off
  six or seven levels across a tenth of an inch, which is the whole flatness
  budget. Glass does not depart from its own drift; a card's pale margin does.
- **A trough must be a local minimum.** Without it the shoulder reaches back
  across the real border into the bright inside of the item, so every sample part
  way up a ramp scores as a trough. This alone pushed a correct card 5 px into
  the shadow beside it.
- **A step must be sharp, and is measured clear of the transition.** A border is a
  geometric discontinuity; a 4 mm diffuse shadow reaches the same size over
  several millimetres. Measured up against the transition a gentle ramp passes
  for sharp, so both sides are sampled a step-width away from it.

Plus: the noise floor is clamped at one level, or a near-noiseless synthetic page
makes eight sigma a fraction of a level; columns with a neighbour beyond them are
dropped from the profile, and the search stops a clear run short of the next
region, so an item can never annex the one beside it; and where an edge cannot be
measured over bare glass at all -- a card with another card hard against its
right side -- it is left exactly as found.

### Measured result

| case | before | after |
| --- | --- | --- |
| `platen_halfcard_20260919` | 3.37 x 1.06 in (H -27.109 mm) | 3.38 x 2.13 in, within 1 mm |
| `platen_real_300dpi` | passes | passes, unchanged |
| other 80 cases | pass | pass |

All six suites green, zero warnings, 82 crop cases. Verified by disabling
`PlatenShadowEdges.Refine` and confirming `platen_halfcard_20260919` fails at
-27.109 mm again.

### Still open

- The passport foot, about 3 mm short (section 9), is untouched by this: its
  edge is measured, so the gate leaves it alone. It needs the cleared-band width
  passed into the fitter.
- An item hard against a neighbour on one side cannot have that side verified,
  by design. It keeps whatever the upstream fitter gave it.
- Skew beyond 3 degrees is not verified: the profile is taken along image rows
  and columns, and a tilted border smears until the trough stops being one.

### At preview resolution

The same capture box-averaged to 100 dpi, which is what the app previews at by
default, behaves the same way: 1.06 in without the pass, 2.19 in with it. The
defect is not a 300 dpi artefact. The residual at 100 dpi is +1.6 mm rather than
within 1 mm, because the shoulder, skip and sharpness widths are only a few
pixels there. Not tuned against, on purpose: a box-averaged 300 dpi page has
different grain from a real 100 dpi scan, and fitting to one would be fitting to
an artefact. Worth re-measuring against a real 100 dpi capture of the same
layout.

## 11. Tilted fragments, 2026-09-20

### The capture

`tests/real/lide400_tilted_20260920.png` (gitignored). Two portraits, two cards
and a passport; seven regions came back. The left card, lying at 21 degrees,
arrived as three pieces along its own long axis:

```
2.98 x 0.33 in  -21.0 deg  High     a strip
2.27 x 1.97 in  -20.8 deg  High     the middle, a whole inch too narrow
2.95 x 0.36 in  -23.6 deg  Good     a strip
```

The card beside it, at 5.6 degrees, came back whole. Same cause as section 10 --
a pale card on pale glass has no region evidence, so the mask breaks into bands
-- but this time each surviving band was accepted separately rather than one of
them being accepted alone.

### `src/Core/PlatenFragments.cs`

Two accepted regions cannot both be separate items and lie on top of each other:
items rest on a sheet of glass, so the second begins where the first ends. When
more than half of one region's area lies inside another and their angles agree
within eight degrees, they are joined into the smallest box at the shared angle
that holds both.

It runs before the shadow pass, and it has to: while the pieces were separate
they stood in each other's way, every search stopping at the next piece.

Seven regions become five and both strips disappear. Verified by disabling
`PlatenFragments.Join` and watching `platen_tilted_20260920` fail on the sliver
assertion again.

### Four real bugs found in the section 10 pass

All four were found by reading profiles and printing per-candidate decisions,
never by guessing, and all four had been silently wrong since the pass was
written:

- **Collisions were tested between enclosing rectangles.** A card at 21 degrees
  has an enclosing rectangle whose empty corners reach well past the card, so it
  collided with a photo two centimetres clear of it and was refused any room to
  grow before a pixel had been measured. Now tested between the boxes
  themselves, by separating axis.
- **The furthest candidate was preferred over the nearest.** Everything past the
  point where glass begins is also glass, so a candidate beyond the first is
  evidence of something else on the bed, not of this item continuing. Preferring
  the furthest only looked right while the search was clamped too tightly to
  reach that far; with the clamp corrected, every card grew four to five
  millimetres.
- **The sharpness test assumed the profile climbs once and stops.** It scans
  from `at - gap - run`, which on a card with a dark band beyond it is *inside
  the card* at 197 -- already past both the 20% and 80% thresholds -- so both
  crossings registered on the first sample and a nineteen pixel ramp measured as
  perfectly sharp. Left as it is, and noted here, because narrowing the scan
  needs the thresholds recomputed inside the narrowed window and that made two
  other cases worse. It no longer bites once the candidate order and the glass
  distances below are right, but it is a trap for whoever touches this next.
- **One glass distance was serving two different questions.** A step onto glass
  IS the glass beginning, so it must be there within a pixel or two. A trough is
  the middle of a shadow, which by definition lies on the glass outside the item,
  so glass begins a shadow's width later. Holding both to the trough's distance
  lets a step qualify a shadow's width short of the border and pulls the edge in
  -- the half-height card came back three millimetres under. Holding both to the
  step's distance rejects every genuine shadow and pushes edges out. They are
  now asked separately.

### A process note worth more than any of them

Several hours went into chasing "regressions" that were an artefact: a parameter
sweep left running in the background rewrote `GlassSkipInches` to its last trial
value, 0.05, and every snapshot taken afterwards inherited it. At 0.05 the glass
window opens while still on the shadow's ramp, so no near candidate can ever
qualify and every border is forced outward -- which looked exactly like a real
one-to-two millimetre regression across five cases, and was measured and
theorised about as if it were. Do not leave a sweep running in the background
over the file under test.

### State

Six suites green, zero warnings, 83 crop cases. Both new passes verified by
disabling each and watching the matching case fail: without the join the sliver
returns, without the shadow pass the half-height card returns at -27.1 mm.

### The three degree guard is gone

`MaxSkewDegrees` is now 45, which is every angle a rotated box can carry. One
more bug had to come out first:

- **A step was never required to be AT the candidate.** `StepOntoGlass` measured
  its two sides a step-width apart and asked only whether a step existed
  somewhere in that window. Every position for several pixels either side of a
  real border therefore scored identically, and which one won was decided by the
  glass-distance test rather than by where the card ended. On a 13 degree card
  whose border sat four pixels out, the edge was moved twelve. The step's own
  20-to-80 crossing midpoint must now land on the candidate.

With that fixed the guard could be lifted with the whole suite green, and the
joined card's height went from -3.6 mm to -0.6 mm.

### Open, and measured

`platen_tilted_20260920` prints both cards' errors on every run rather than
hiding them behind a pass:

```
[-20.8 deg: W -4.1, H -0.6 mm]   the joined card
[ 5.6 deg: W +0.0, H +1.1 mm]    the card beside it
```

The joined card's remaining width is **not** a detector error, as far as the
pixels go. Its left profile at 300 dpi reads

```
... 189 190 |190 193 196 198 198 197 198 201 203 206 208 209 209 210 210 211 211 211 ...
             ^ the box edge                      ^ glass
```

-- card material for about seven pixels past the box, then glass. The right
profile has a 26.6 sigma shadow one pixel out. So both ends are within about a
millimetre of where the box already is, and the card's long axis measures about
82.2 mm against the 85.6 the test names, while its short axis measures 53.8
against 54. Either that card is not an ID-1 card, or something at its end is not
visible in the capture. Settling which needs the owner to measure that specific
card with a ruler; it cannot be settled from this end without looking at the
document itself, which these references exist to avoid.

Two things were tried for the remaining millimetre and reverted, both measured:

- **Letting glass carry dust.** The flatness test takes the worst residual over
  the run, so a two pixel dip of five levels forty-eight pixels out -- a speck on
  the platen -- disqualifies an otherwise perfect stretch. Allowing 5% of samples
  to break the trend did not move the joined card at all and took
  `platen_real_lide400_preview` 1.4 mm wide. Reverted.
- **Shortening the glass run near a border.** The joined card's left end has only
  about 100 px of glass before the page edge and its right end about 104 px
  before the next item, against the 120 px the run asks for. Not attempted after
  the dust result suggested the run length is not what is holding it.

Still carried, and still true: `StepOntoGlass` scans its 20/80 crossings from
`at - gap - run`, which on a profile that goes bright, dark, bright starts inside
the item and past both thresholds. It no longer bites, because the candidate must
now sit on the crossing midpoint, but it is a trap for whoever touches this next.

## 12. The Photoshop connector, 2026-09-20

Photoshop acquires from NextScanner directly now: File, Import, Next Scanner
starts the application, the operator scans, and the pixels arrive as a document
without a file in between. Connector B, an acquire module (`.8ba`), because an
acquire plug-in is the only kind Photoshop lists under Import, which is where
someone looks for a scanner.

### What is verified, and by what

| | how |
| --- | --- |
| The byte layout the two languages share | a compiled `offsetof` dumper against `--ps-selftest` |
| BGR to RGB, and 65535 to 32768 | `--ps-selftest`, on a known frame |
| Pixels, size, orientation, resolution | the owner, in Photoshop, on a real scan |
| Page by page turn taking | `--ps-selftest`, against the real publisher |
| Several items into several documents | the owner, in Photoshop, four items at once |

Photoshop 2026 does honour `acquireAgain`: four items on the glass opened as four
documents. Worth recording, because Adobe permits a host to ignore it and there
was no way to find out from this end.

### Photoshop 16 bit is 0..32768

Not 0..65535. `PIColorSpaceSuite.h:233`. Getting this wrong produces an image
that looks right and measures wrong, which is the worst kind of wrong, so the
conversion is done once on the writing side and checked by name in the self
test. `imageHRes` and `imageVRes` are `Fixed` 16.16 for the same reason: 300 dpi
is `300 << 16`, and a plain 300 would arrive as a page four thousandths of an
inch across.

### Waiting without freezing Photoshop

Photoshop calls plug-ins on its own thread. The first working version waited
there for the scan, which stopped Photoshop's message loop: the window stopped
repainting and Windows greyed it over and called it not responding. The scan
happens in another process and cannot be hurried, so the wait has to pump for
Photoshop while it runs.

Pumping on its own would have been worse than freezing -- a dispatched message
can take the operator back into the menus and re-enter the plug-in -- so the
host window is disabled for the duration. A disabled window still repaints; it
only refuses input, which is right while the scan belongs to the other
application. Clicks into Photoshop doing nothing during a scan is deliberate.

The same wait watches the application's process handle. Closing NextScanner
without scanning is an ordinary thing to do, and before this it cost half an
hour of a dead Photoshop.

### Several items, one scan

Five cards on the glass are five pages, and only the first was handed over. The
API has the mechanism: `acquireAgain`, set in the Finish handler, asks the host
to start again (`PIAcquire.h:472`).

The frame carries `pageIndex` and `pageCount`, so Finish decides from the page
the application just sent -- read before the mapping that says so is unmapped.

Two things in the transport changed to carry more than one page:

- **A mapping name per page**, `Local\NextScan.Frame.<session>.<page>`. The
  application builds the next page while the plug-in may still hold the last one
  open, and two mappings cannot share a name.
- **Ready and Done became auto reset.** Each page is one exchange, and an auto
  reset event is emptied by the wait that receives it, so a page boundary needs
  no bookkeeping. With manual reset events a stale Ready sends the reader looking
  for a mapping that does not exist yet -- which is what the negative control
  below actually does.

`Cancel` now means stop early and never "we are finished". Setting it on the
ordinary path would make a completed one page scan look abandoned, because the
application waits on Done and Cancel together. It is set on failure, on giving
up, and by Finalize -- which is what a host that ignores `acquireAgain` looks
like from this end. Adobe says plainly that a host may ignore it and that Finish
must clean up regardless, so the session is left open holding nothing, and
Finalize closes it if Start never comes back.

### The handover test, and what happens when it is broken

The layout test proves the two sides agree about bytes. It says nothing about
whether they agree about turns, which is the half that fails by hanging rather
than by looking wrong -- and inside Photoshop is the least debuggable place for
a hang. So `--ps-selftest` also runs the real publisher against a stand in for
the plug-in, with every wait bounded, and checks two things:

    all three pages, in order             ok
    giving up stops the rest              ok

Three pages of different sizes, so a page delivered out of order reads as a
wrong string rather than as a count that happens to match. Both checks were
confirmed to fail when the thing they test is removed:

- **Publishing only the first page**, the behaviour this replaced: first check
  FAILED, second still ok.
- **Manual reset Ready and Done on the publisher**: first check FAILED. The
  first attempt at this control took the whole self test down with a stack
  trace from the reader thread, which is a poor way for a test to report; the
  reader now records what went wrong and releases the publisher either way, so
  a broken turn reads as one line instead of hanging the next ten minutes.

### The first band, and how the arithmetic found it

Every document opened with a white strip across the top and the top of the item
simply missing. The proportions gave it away before the code did: the strip was
about 43% and 38% of the two small photographs, but only about 14% of a passport
page. A fixed fraction would have meant a scaling fault. A fraction that shrinks
as the item grows means a fixed number of rows -- and the band is 256 rows.

Start was calling Deliver. Photoshop reads the description at Start, builds the
document, and asks for rows through Continue; a band handed over at Start is
dropped, and the cursor it moved meant the first Continue began one band in. So
the top 256 rows of every page were never written, which is what an untouched
Photoshop canvas looks like.

Adobe's own import sample ends its Start handler with `gStuff->data = NULL`
(`samplecode/import/gradientimport`, `DoStart`). `Deliver` now has exactly one
caller, in Continue, and the reason is written where the next person will
change it.

This was present in the single page handover too, and passed as working. One
page gives nothing to compare against; four items of different heights side by
side made the ratio visible. The lesson is the one from section 11 in a new
costume: a measurement that varies with something is worth more than a
measurement that only looks wrong.

### Still outstanding

- **No ICC profile.** The frame reserves room and the plug-in assigns rather
  than converts, which is the right behaviour once there is something to
  assign. The capture does not carry a profile yet; pulling one from WIA or
  TWAIN is separate work.
- **Always a new document.** New layer and smart object targets, and the named
  pipe negotiation from the plan, are not implemented.
