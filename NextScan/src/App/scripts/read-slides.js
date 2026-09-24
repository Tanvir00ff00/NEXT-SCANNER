// Reads a presentation for the assistant: each slide's text, shape by shape.
// Arguments: none.
var pres = Api.GetPresentation();
var slides = [];
for (var i = 0; i < pres.GetSlidesCount(); i++) {
    var slide = pres.GetSlideByIndex(i);
    var shapes = [];
    var all = slide.GetAllShapes ? slide.GetAllShapes() : [];
    for (var j = 0; j < all.length; j++) {
        var text = '';
        try {
            var content = all[j].GetDocContent();
            if (content) {
                var parts = [];
                for (var k = 0; k < content.GetElementsCount(); k++) {
                    var p = content.GetElement(k);
                    if (p && p.GetText) parts.push(p.GetText().replace(/[\r\n]+$/, ''));
                }
                text = parts.join('\n');
            }
        } catch (e) { }
        if (text) shapes.push(text);
    }
    slides.push({ slide: i + 1, text: shapes });
}
return { kind: 'presentation', slides: slides };
