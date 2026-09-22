# Turning a scan into a document — the design, and where it came from

Research done 2026-09-22, before any code. The goal set by the owner: a scanned
page becomes an editable document that matches the original, and it must work
**even when the model doing the reading is not a strong one**. The application
is supposed to carry that weight, not the model.

Everything below is either a finding from published work or a decision taken
because of one. Where a finding contradicted what I had already proposed, that
is said rather than quietly fixed.

---

## 1. The model gets the whole page. It never gets asked where anything is.

I first proposed sending the model crops rather than whole pages, so it could
not get positions wrong. The owner rejected that, correctly: a model given only
a crop does not know what document it is reading, and `3811` on its own is a
number rather than a postcode on a national ID card.

The resolution is not a compromise between the two. It is a split by question:

| question | answered by |
| --- | --- |
| what document is this, what does it say, which part is a heading or a table | **the model**, from the whole page |
| where is each block, how large, what alignment, which table cell | **our measurements**, in millimetres |
| what did we get wrong | **the comparison**, region by region |

The model is never asked for a coordinate. We already measure geometry to a
millimetre for cropping, and that machinery — oriented boxes, measured skew,
edges verified against the page's own sensor noise — is exactly what a document
reconstructor needs.

## 2. Telling the model what we already know has a name: anchoring

This is the part that makes a weak model behave, and it is not our idea.

Microsoft's Copilot does not send a user's question to the model. An
orchestrator first performs **grounding**: it retrieves relevant context and
builds a *meta-prompt* combining the question, the retrieved material and its
directives, and only that goes to the model
([architecture](https://learn.microsoft.com/en-us/copilot/microsoft-365/microsoft-365-copilot-architecture)).

The OCR research does the same thing spatially, and calls it **anchoring**:
layout metadata, as bounding boxes, is supplied to the model to preserve reading
order and **reduce hallucination**
([survey](https://huggingface.co/blog/ocr-open-models)).

So our prompt will carry what has already been measured:

    page 210 x 297 mm at 300 dpi
    14 text blocks, at these positions, these heights
    a 6 x 4 table, rules at these coordinates
    this region is Bengali script, this one Latin

The model then confirms and fills in rather than inferring from scratch. A
weaker model fails at inference and is fine at confirmation, which is the whole
reason this design is expected to hold with one.

## 3. Reconstruction-as-Validation — and the trap in it

I claimed that rendering the reconstruction back and comparing it to the scan
was a technique few tools use. That was wrong. It is published as **RaV-IDP,
Reconstruction-as-Validation**
([paper](https://arxiv.org/pdf/2604.23644)): each extracted entity is rendered
back into something comparable with the original region, a comparator scores
fidelity, and where fidelity falls below a threshold a stronger vision model is
called for that region only.

Being wrong about its novelty is worth less than what the paper contains, which
is a constraint I would have got wrong on my own:

> the comparator always anchors against the **original** document region

Never against the previous reconstruction. Compare a repair against the thing it
repaired and the loop validates itself into agreement with its own mistakes.
This is the bootstrap constraint, and it is the single most important line in
this document.

The same loop answers the cost question. A commercial layout parser charges
about **$0.10 a page** ([comparison](https://mixpeek.com/curated-lists/best-document-parsing-tools)).
Calling a strong model on every page of a busy day is a real bill. Calling it
only on the regions a comparator says are wrong is a fraction of it.

## 4. What accuracy is actually available

Measured on complex PDFs: **LlamaParse 92% F1** via a multimodal LLM at
$0.10/page; **Docling 88%**, open source, no per-page cost
([comparison](https://mixpeek.com/curated-lists/best-document-parsing-tools)).

Nobody sells 100%. The owner's standard — *same to same, not a millimetre out* —
is not on offer from any tool at any price today, and saying otherwise would set
up a disappointment later.

What *is* available is better than it sounds: a high first pass, a validation
loop that finds its own weak spots, and correction that is cheap because the
operator fixes one instance and the fix propagates to structurally similar
regions — the approach ReforMe takes
([paper](https://arxiv.org/pdf/2606.03266)).

## 5. Bengali in a .docx breaks in a specific, known way

Word resolves fonts through separate stacks. For complex scripts — Bengali,
Arabic, Devanagari — the run takes its font from `w:rFonts/@cs`, its size from
`w:szCs`, bold from `w:bCs`, italic from `w:iCs`, and the run must carry `w:cs`
([reference](https://ooxml.org/wordml/)).

Set only the Latin attributes and Word renders Bengali text at the Latin font
and the Latin size. This is not theoretical: it is a live bug in shipping docx
tooling, filed in exactly those terms — *"Hebrew and Arabic runs take the Latin
font and size: w:szCs and w:rFonts/@cs are never read"*.

A generic generator gets this wrong. Ours must not, and the test for it is
trivial to write.

Beyond that sits the problem no foreign tool even knows about: Bangladeshi
documents are frequently **legacy SutonnyMJ ANSI**, not Unicode. A model returns
Unicode. Where the customer's existing Word workflow is ANSI, a conversion has
to happen on our side.

## 6. What the application contributes, concretely

The owner's requirement was that the software make the model's job easy. In
specific terms, three things:

**A clean page instead of a scan.** Deskewed, background-flattened,
bleed-through cleared, descreened, cropped to its own edges — the pipeline built
in 1.2. The same model reading a skewed, shadowed, moiré-ridden scan and reading
a clean flat page does not perform the same. This is already built and is the
largest single contribution to model accuracy available to us.

**Measurements in the prompt.** Section 2.

**A validation loop with a bootstrap constraint.** Section 3.

None of these require a better model. That is the point.

---

## Order of work

1. **The measurement layer** — text blocks, their positions in millimetres,
   font sizes from pixel height and dpi, table rules from projection profiles,
   script runs. No AI, so it can be looked at and judged before a model is
   involved.
2. **The docx writer** — complex-script attributes correct from the first line.
3. **The model client** — one page, anchored prompt, constrained output.
4. **The validation loop** — render, compare against the original region,
   re-ask only what failed.
5. **Editing**, in the application and through the model.

Mobile capture is independent of all of this and can be built at any point.
