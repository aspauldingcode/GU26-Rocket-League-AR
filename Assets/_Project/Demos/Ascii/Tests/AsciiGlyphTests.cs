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

        [Test]
        public void DepthBrightnessFallsOffAndStaysVisible()
        {
            var touch = AsciiGlyph.DepthBrightness(0.4f);
            var step = AsciiGlyph.DepthBrightness(1.2f);
            var across = AsciiGlyph.DepthBrightness(3f);
            var far = AsciiGlyph.DepthBrightness(6f);
            Assert.Greater(touch, 0.9f);
            Assert.Greater(touch, step);
            Assert.Greater(step - across, 0.3f);
            Assert.Greater(across, far);
            Assert.Greater(far, 0.1f);
        }

        [Test]
        public void NearGlyphsStayDenserThanFarOnes()
        {
            var nearDark = AsciiGlyph.GlyphPick(0f, AsciiGlyph.DepthBrightness(0.4f));
            var farLit = AsciiGlyph.GlyphPick(1f, AsciiGlyph.DepthBrightness(4f));
            Assert.Greater(nearDark, farLit);
            Assert.AreEqual(1f, AsciiGlyph.GlyphPick(1f, 1f), 1e-4f);
            Assert.Less(AsciiGlyph.GlyphPick(0f, 0f), 0.05f);
        }

        [Test]
        public void CellSizeGrowsWhenCloseAndStaysNested()
        {
            Assert.AreEqual(4, AsciiGlyph.CellScale(0.4f));
            Assert.AreEqual(2, AsciiGlyph.CellScale(1.5f));
            Assert.AreEqual(1, AsciiGlyph.CellScale(4f));
            var near = AsciiGlyph.CellSize(0.4f);
            var far = AsciiGlyph.CellSize(4f);
            Assert.AreEqual(new Vector2(32f, 48f), near);
            Assert.AreEqual(new Vector2(8f, 12f), far);
            Assert.AreEqual(0f, near.x % far.x);
            Assert.AreEqual(0f, near.y % far.y);
        }

        [Test]
        public void ShadowThinsTheGlyph()
        {
            var lit = AsciiGlyph.GlyphPick(0.8f, 0.7f);
            var shaded = AsciiGlyph.ShadowedPick(lit, 1f);
            Assert.Less(shaded, lit * 0.25f);
            Assert.AreEqual(lit, AsciiGlyph.ShadowedPick(lit, 0f), 1e-4f);
        }
    }
}
