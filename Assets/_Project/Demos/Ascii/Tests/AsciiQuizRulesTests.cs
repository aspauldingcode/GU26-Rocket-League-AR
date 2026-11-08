using NUnit.Framework;

namespace CardboardXR.Demos.Ascii.Tests
{
    public class AsciiQuizRulesTests
    {
        static AsciiQuizRules Started(out float now)
        {
            var rules = new AsciiQuizRules();
            now = 0f;
            Assert.IsTrue(rules.Brief(now));
            Assert.IsTrue(rules.Start(now));
            return rules;
        }

        // Reads the question, then answers once the choices show.
        static void ReadAndAnswer(AsciiQuizRules rules, ref float now, bool correct)
        {
            Assert.AreEqual(AsciiQuizState.Reading, rules.State);
            now += AsciiQuizRules.k_ReadSeconds + k_Step;
            rules.Tick(now);
            Assert.AreEqual(AsciiQuizState.Answering, rules.State);
            now += 2f;
            Assert.IsTrue(rules.Answer(correct, now));
            Assert.AreEqual(AsciiQuizState.Feedback, rules.State);
            now += AsciiQuizRules.k_FeedbackSeconds + k_Step;
            rules.Tick(now);
        }

        // Past a limit even after float rounding of the running clock.
        const float k_Step = 0.01f;

        [Test]
        public void AnswersOnlyCountOnceTheChoicesShow()
        {
            var rules = Started(out var now);
            Assert.IsFalse(rules.Answer(true, now));
            rules.Tick(now + AsciiQuizRules.k_ReadSeconds - 0.01f);
            Assert.AreEqual(AsciiQuizState.Reading, rules.State);
            Assert.AreEqual(0, rules.Correct);
        }

        [Test]
        public void AngelsAreHeldWhileReadingAndBetweenQuestions()
        {
            var rules = Started(out var now);
            Assert.IsTrue(rules.AngelsHeld);
            now += AsciiQuizRules.k_ReadSeconds;
            rules.Tick(now);
            Assert.IsFalse(rules.AngelsHeld);
            rules.Answer(true, now);
            Assert.IsTrue(rules.AngelsHeld);
        }

        [Test]
        public void FiveRightAnswersOpenThePortalAndWalkingOutWins()
        {
            var rules = Started(out var now);
            for (var i = 0; i < AsciiQuizRules.k_Questions; i++)
                ReadAndAnswer(rules, ref now, true);
            Assert.AreEqual(AsciiQuizState.Escape, rules.State);
            Assert.AreEqual(5, rules.Correct);
            Assert.IsFalse(rules.AngelsHeld);
            Assert.IsTrue(rules.Exited(now));
            Assert.AreEqual(AsciiQuizState.Won, rules.State);
        }

        [Test]
        public void ThreeRightAndTwoWrongStillEscapes()
        {
            var rules = Started(out var now);
            ReadAndAnswer(rules, ref now, false);
            ReadAndAnswer(rules, ref now, true);
            ReadAndAnswer(rules, ref now, false);
            ReadAndAnswer(rules, ref now, true);
            ReadAndAnswer(rules, ref now, true);
            Assert.AreEqual(AsciiQuizState.Escape, rules.State);
            Assert.AreEqual(2, rules.Mistakes);
        }

        [Test]
        public void ThirdMistakeTurnsYouToStone()
        {
            var rules = Started(out var now);
            ReadAndAnswer(rules, ref now, false);
            ReadAndAnswer(rules, ref now, false);
            ReadAndAnswer(rules, ref now, false);
            Assert.AreEqual(AsciiQuizState.Dead, rules.State);
            Assert.AreEqual(AsciiQuizDeath.Mistakes, rules.Death);
            Assert.IsFalse(rules.Exited(now));
        }

        [Test]
        public void RunningOutOfTimeIsAMistake()
        {
            var rules = Started(out var now);
            now += AsciiQuizRules.k_ReadSeconds;
            rules.Tick(now);
            now += AsciiQuizRules.k_AnswerSeconds;
            rules.Tick(now);
            Assert.AreEqual(AsciiQuizState.Feedback, rules.State);
            Assert.IsTrue(rules.LastTimedOut);
            Assert.AreEqual(1, rules.Mistakes);
        }

        [Test]
        public void CaughtOnlyWhilePlayingAndOnlyOnce()
        {
            var rules = new AsciiQuizRules();
            Assert.IsFalse(rules.Caught(0f));
            rules.Brief(0f);
            Assert.IsFalse(rules.Caught(0f));
            rules.Start(0f);
            Assert.IsTrue(rules.Caught(1f));
            Assert.AreEqual(AsciiQuizDeath.Caught, rules.Death);
            Assert.IsFalse(rules.Caught(2f));
        }

        [Test]
        public void ExitingBeforeTheQuestionsAreDoneDoesNothing()
        {
            var rules = Started(out var now);
            Assert.IsFalse(rules.Exited(now));
            ReadAndAnswer(rules, ref now, true);
            Assert.IsFalse(rules.Exited(now));
            Assert.AreEqual(AsciiQuizState.Reading, rules.State);
        }

        [Test]
        public void TryingAgainAfterDyingStartsClean()
        {
            var rules = Started(out var now);
            rules.Caught(now);
            Assert.IsTrue(rules.Brief(now));
            Assert.AreEqual(AsciiQuizState.Briefing, rules.State);
            Assert.AreEqual(0, rules.Mistakes);
            Assert.AreEqual(0, rules.Question);
            Assert.AreEqual(AsciiQuizDeath.None, rules.Death);
            Assert.IsFalse(rules.Brief(now));
        }
    }
}
