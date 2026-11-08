// Renders the Ascii demo's quiz questions with MathJax (TeX in, SVG out) and rasterizes them to
// white-on-transparent PNGs that Unity loads from Resources. Unity has no MathJax, and the five
// questions are fixed, so they are typeset once here. Keep the TeX in step with AsciiMathQuiz.cs.
//
//   npm i --prefix /tmp/mathjax-render mathjax-full@3 @resvg/resvg-js
//   NODE_PATH=/tmp/mathjax-render/node_modules node Tools/ascii_mathjax.cjs
const fs = require('fs');
const path = require('path');
const { mathjax } = require('mathjax-full/js/mathjax.js');
const { TeX } = require('mathjax-full/js/input/tex.js');
const { SVG } = require('mathjax-full/js/output/svg.js');
const { liteAdaptor } = require('mathjax-full/js/adaptors/liteAdaptor.js');
const { RegisterHTMLHandler } = require('mathjax-full/js/handlers/html.js');
const { AllPackages } = require('mathjax-full/js/input/tex/AllPackages.js');
const { Resvg } = require('@resvg/resvg-js');

const questions = [
  '7 \\times 8 = \\,?',
  '\\dfrac{144}{12} = \\,?',
  '3^2 + 4^2 = \\,?',
  '236 + 489 = \\,?',
  '\\sqrt{81} + 6 = \\,?',
];
// Pixels per ex, so every question is typeset at the same size.
const pixelsPerEx = 64;
const out = path.join(__dirname, '..', 'Assets', '_Project', 'Demos', 'Ascii', 'Resources', 'AsciiMath');

const adaptor = liteAdaptor();
RegisterHTMLHandler(adaptor);
const doc = mathjax.document('', {
  InputJax: new TeX({ packages: AllPackages }),
  OutputJax: new SVG({ fontCache: 'none' }),
});

fs.mkdirSync(out, { recursive: true });
questions.forEach((tex, i) => {
  let svg = adaptor.innerHTML(doc.convert(tex, { display: true }));
  svg = svg
    .replace(/currentColor/g, '#FFFFFF')
    .replace(/(width|height)="([\d.]+)ex"/g, (_, attr, ex) => `${attr}="${(parseFloat(ex) * pixelsPerEx).toFixed(1)}"`)
    .replace(/style="[^"]*"/, '');
  // A margin all round, so strokes at the edge aren't cut by the texture's border.
  svg = svg.replace(/viewBox="([-\d.]+) ([-\d.]+) ([\d.]+) ([\d.]+)"/, (_, x, y, w, h) => {
    const pad = 0.12 * parseFloat(h);
    return `viewBox="${x - pad} ${y - pad} ${+w + 2 * pad} ${+h + 2 * pad}"`;
  });
  const box = /viewBox="[-\d.]+ [-\d.]+ ([\d.]+) ([\d.]+)"/.exec(svg);
  const height = parseFloat(/height="([\d.]+)"/.exec(svg)[1]);
  const scale = height / (parseFloat(box[2]) / 1.24);
  svg = svg
    .replace(/width="[\d.]+"/, `width="${(parseFloat(box[1]) * scale).toFixed(1)}"`)
    .replace(/height="[\d.]+"/, `height="${(parseFloat(box[2]) * scale).toFixed(1)}"`);
  const png = new Resvg(svg, { background: 'rgba(0,0,0,0)' }).render().asPng();
  const file = path.join(out, `Question${i + 1}.png`);
  fs.writeFileSync(file, png);
  console.log(`${file}  ${tex}`);
});
