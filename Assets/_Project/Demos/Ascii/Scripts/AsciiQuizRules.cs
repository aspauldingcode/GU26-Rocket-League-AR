using System;

namespace CardboardXR.Demos.Ascii
{
    public enum AsciiQuizState
    {
        // Outside the gate, or waiting to go back in.
        Idle,
        // How to play, until Start.
        Briefing,
        // The question shows; the answers don't yet. Angels are held.
        Reading,
        // The answers show with the question. Angels move from behind while unseen.
        Answering,
        // Right or wrong, for a moment. Angels are held.
        Feedback,
        // Five questions done with fewer than three mistakes: the portal is open, angels still hunt.
        Escape,
        Dead,
        // Out through the reopened portal.
        Won,
    }

    public enum AsciiQuizDeath
    {
        None,
        Mistakes,
        Caught,
    }

    // The math game's rules, with no Unity in them: five questions, five seconds to read each and
    // ten to answer (with a five-second grace while choices appear). A timeout counts as a mistake,
    // three mistakes turn the player to stone, and finishing the five opens the portal, which has
    // to be walked through to win. Every move is checked against the current state, so a late click
    // or a second catch can't skip a step.
    public class AsciiQuizRules
    {
        public const int k_Questions = 5;
        public const int k_MaxMistakes = 3;
        public const float k_ReadSeconds = 5f;
        public const float k_AnswerSeconds = 10f;
        // First half of the answer window: angels stay held so the choices can be read.
        public const float k_GraceSeconds = 5f;
        public const float k_FeedbackSeconds = 1.6f;

        public AsciiQuizState State { get; private set; } = AsciiQuizState.Idle;
        public int Question { get; private set; }
        public int Mistakes { get; private set; }
        public int Correct { get; private set; }
        public bool LastCorrect { get; private set; }
        public bool LastTimedOut { get; private set; }
        public AsciiQuizDeath Death { get; private set; }
        public float StateStarted { get; private set; }

        // From, to.
        public event Action<AsciiQuizState, AsciiQuizState> Changed;

        // Angels only move once the player has had time to read the choices, or runs for the portal.
        public bool AngelsHeld(float now) => State switch
        {
            AsciiQuizState.Answering => GraceRemaining(now) > 0f,
            AsciiQuizState.Escape => false,
            _ => true,
        };

        public float GraceRemaining(float now) =>
            State == AsciiQuizState.Answering ? Math.Max(0f, k_GraceSeconds - (now - StateStarted)) : 0f;

        public bool CanWrite => State == AsciiQuizState.Reading || State == AsciiQuizState.Answering;
        public bool Playing => State is AsciiQuizState.Reading or AsciiQuizState.Answering or AsciiQuizState.Feedback or AsciiQuizState.Escape;

        public float Remaining(float now) => State switch
        {
            AsciiQuizState.Reading => Math.Max(0f, k_ReadSeconds - (now - StateStarted)),
            AsciiQuizState.Answering => Math.Max(0f, k_AnswerSeconds - (now - StateStarted)),
            AsciiQuizState.Feedback => Math.Max(0f, k_FeedbackSeconds - (now - StateStarted)),
            _ => 0f,
        };

        // Shows how to play, from the start or after dying.
        public bool Brief(float now)
        {
            if (State != AsciiQuizState.Idle && State != AsciiQuizState.Dead)
                return false;
            Question = 0;
            Mistakes = 0;
            Correct = 0;
            Death = AsciiQuizDeath.None;
            Enter(AsciiQuizState.Briefing, now);
            return true;
        }

        public bool Start(float now)
        {
            if (State != AsciiQuizState.Briefing)
                return false;
            Enter(AsciiQuizState.Reading, now);
            return true;
        }

        public void Tick(float now)
        {
            switch (State)
            {
                case AsciiQuizState.Reading when now - StateStarted >= k_ReadSeconds:
                    Enter(AsciiQuizState.Answering, now);
                    break;
                case AsciiQuizState.Answering when now - StateStarted >= k_AnswerSeconds:
                    Score(false, timedOut: true, now);
                    break;
                case AsciiQuizState.Feedback when now - StateStarted >= k_FeedbackSeconds:
                    Next(now);
                    break;
            }
        }

        public bool Answer(bool correct, float now)
        {
            if (State != AsciiQuizState.Answering)
                return false;
            Score(correct, timedOut: false, now);
            return true;
        }

        public bool Caught(float now)
        {
            if (!Playing)
                return false;
            Death = AsciiQuizDeath.Caught;
            Enter(AsciiQuizState.Dead, now);
            return true;
        }

        // Walked out through the reopened portal.
        public bool Exited(float now)
        {
            if (State != AsciiQuizState.Escape)
                return false;
            Enter(AsciiQuizState.Won, now);
            return true;
        }

        public void Reset(float now)
        {
            Question = 0;
            Mistakes = 0;
            Correct = 0;
            Death = AsciiQuizDeath.None;
            if (State != AsciiQuizState.Idle)
                Enter(AsciiQuizState.Idle, now);
        }

        void Score(bool correct, bool timedOut, float now)
        {
            LastCorrect = correct;
            LastTimedOut = timedOut;
            if (correct)
                Correct++;
            else
                Mistakes++;
            Enter(AsciiQuizState.Feedback, now);
        }

        void Next(float now)
        {
            if (Mistakes >= k_MaxMistakes)
            {
                Death = AsciiQuizDeath.Mistakes;
                Enter(AsciiQuizState.Dead, now);
                return;
            }
            if (Question + 1 >= k_Questions)
            {
                Enter(AsciiQuizState.Escape, now);
                return;
            }
            Question++;
            Enter(AsciiQuizState.Reading, now);
        }

        void Enter(AsciiQuizState state, float now)
        {
            var from = State;
            State = state;
            StateStarted = now;
            Changed?.Invoke(from, state);
        }
    }
}
