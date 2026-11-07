using CardboardXR.Demos.Ascii;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Ascii.Tests
{
    public class AsciiGlyphTests
    {
        [Test]
        public void BrightnessMapsEndsOfTheRamp()
        {
            Assert.AreEqual(0, AsciiGlyph.BrightnessToIndex(0f, AsciiGlyph.Ramp.Length));
            Assert.AreEqual(AsciiGlyph.Ramp.Length - 1, AsciiGlyph.BrightnessToIndex(1f, AsciiGlyph.Ramp.Length));
            Assert.AreEqual(AsciiGlyph.Ramp[0], AsciiGlyph.BrightnessToChar(0f));
            Assert.AreEqual(AsciiGlyph.Ramp[^1], AsciiGlyph.BrightnessToChar(1f));
        }

        [Test]
        public void MidBrightnessIsInTheMiddleOfTheRamp()
        {
            var mid = AsciiGlyph.BrightnessToIndex(0.5f, AsciiGlyph.Ramp.Length);
            Assert.Greater(mid, 0);
            Assert.Less(mid, AsciiGlyph.Ramp.Length - 1);
        }

        [Test]
        public void LuminanceMatchesRec601Weights()
        {
            Assert.AreEqual(0f, AsciiGlyph.Luminance(Color.black), 1e-5f);
            Assert.AreEqual(1f, AsciiGlyph.Luminance(Color.white), 1e-5f);
            Assert.Greater(AsciiGlyph.Luminance(Color.green), AsciiGlyph.Luminance(Color.blue));
        }

        [Test]
        public void SingleGlyphCountStaysAtZero()
        {
            Assert.AreEqual(0, AsciiGlyph.BrightnessToIndex(0.9f, 1));
        }
    }
}
