using System;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos.Ascii
{
    // The math game inside the gate, played on AsciiQuizRules. A briefing explains it; Start puts a
    // weeping angel behind the player. The question and Kahoot-style answers share one facing in
    // front (MathJax via Tools/ascii_mathjax.cjs), stacked so they don't overlap: 5 s on the
    // question board, then answers underneath with a 5 s grace and 10 s to pick — no answer
    // countdown; prompts say "Be quick!" / "look behind you!". A red scratch pad (AsciiWhiteboard)
    // sits to the left of the question; it's wiped after every answer.
    // Three mistakes turn the player to stone; finishing the five opens the portal, and walking out
    // through it wins. Logs [Ascii].
    public class AsciiMathQuiz : MonoBehaviour
    {
        // Fixed, so a demo can be played through on purpose. Answer key: B, D, A, C, B.
        static readonly (string image, string plain, string[] choices, int answer)[] k_Questions =
        {
            ("Question1", "7 × 8", new[] { "54", "56", "63", "48" }, 1),
            ("Question2", "144 ÷ 12", new[] { "14", "11", "16", "12" }, 3),
            ("Question3", "3² + 4²", new[] { "25", "14", "49", "24" }, 0),
            ("Question4", "236 + 489", new[] { "715", "625", "725", "735" }, 2),
            ("Question5", "√81 + 6", new[] { "13", "15", "17", "87" }, 1),
        };
        static readonly string k_AnswerKey = "B, D, A, C, B";

        // Kahoot's red triangle, blue diamond, yellow circle and green square.
        static readonly Color[] k_ShapeColors =
        {
            new(0.89f, 0.11f, 0.24f), new(0.07f, 0.41f, 0.81f), new(0.85f, 0.62f, 0f), new(0.15f, 0.54f, 0.05f),
        };
        static readonly (string icon, float degrees)[] k_Shapes = { ("play_arrow", 90f), ("stop", 45f), ("circle", 0f), ("stop", 0f) };
        static readonly string[] k_Letters = { "A", "B", "C", "D" };
        static readonly Color k_Right = new(0.55f, 0.9f, 0.55f);
        static readonly int k_StoneId = Shader.PropertyToID("_AsciiStone");

        const float k_StoneSeconds = 1.6f;
        // One shared plane so panels don't z-fight. When answers show, stack: equation up, choices down.
        const float k_QuizDistance = 1.5f;
        const float k_QuestionDistance = k_QuizDistance;
        const float k_QuestionHeightAlone = 0.05f;
        const float k_QuestionHeightStacked = 0.5f;
        const float k_AnswersHeight = -0.55f;
        // Scratch pad clear of the equation (left); far enough that 460 dp and 640 dp don't meet.
        const float k_BoardDistance = k_QuizDistance;
        const float k_BoardSide = -1.35f;
        const float k_BoardHeightAlone = -0.4f;
        const float k_BoardHeightStacked = 0.35f;
        const float k_AnswerDistance = k_QuizDistance;
        const float k_FrontDistance = 1.4f;
        // Fake frosted glass: translucent M3 surface (no fullscreen blur pass).
        static readonly Color k_Glass = M3Theme.WithAlpha(M3Theme.SurfaceContainer, 0.42f);
        static readonly Color k_GlassHigh = M3Theme.WithAlpha(M3Theme.SurfaceContainerHigh, 0.5f);
        // The equation's height in dp per texture pixel: about 60 dp for a line of numbers.
        const float k_DpPerPixel = 0.5f;

        readonly AsciiQuizRules m_Rules = new();
        Transform m_Head;
        AsciiAngels m_Angels;
        AsciiWhiteboard m_Notes;
        Func<Vector3, Vector3> m_SpawnPoint;
        Action m_OnEscape;
        Action m_OnLobby;
        Action m_OnPlayAgain;
        Texture2D[] m_Images;
        // Panel stays in front; angels always spawn on the opposite side (behind the player).
        Vector3 m_PanelForward = Vector3.forward;
        Vector3 m_AngelDirection = Vector3.back;
        float m_Stone;

        RectTransform m_Briefing;
        RectTransform m_Question;
        RectTransform m_Board;
        RectTransform m_Answers;
        RectTransform m_End;
        TMP_Text m_QuestionHeader;
        TMP_Text m_Mistakes;
        RawImage m_Equation;
        TMP_Text m_PlainEquation;
        TMP_Text m_Count;
        TMP_Text m_Status;
        TMP_Text m_AnswersHeader;
        TMP_Text m_AnswersTimer;
        TMP_Text m_Feedback;
        M3Button[] m_Choices;
        TMP_Text m_EndTitle;
        TMP_Text m_EndBody;
        M3Button m_EndPrimary;

        public AsciiQuizState State => m_Rules.State;
        public bool CanWrite => m_Rules.CanWrite;
        public bool Playing => m_Rules.Playing;

        // spawnPoint takes the flat direction the player faces and returns where the angel stands.
        public void Configure(Transform head, AsciiAngels angels, AsciiWhiteboard notes, Func<Vector3, Vector3> spawnPoint,
            Action onEscape, Action onLobby, Action onPlayAgain)
        {
            m_Head = head;
            m_Angels = angels;
            m_Notes = notes;
            m_SpawnPoint = spawnPoint;
            m_OnEscape = onEscape;
            m_OnLobby = onLobby;
            m_OnPlayAgain = onPlayAgain;
            m_Angels.Caught += OnCaught;
            m_Rules.Changed += OnChanged;
            m_Images = new Texture2D[k_Questions.Length];
            for (var i = 0; i < k_Questions.Length; i++)
            {
                m_Images[i] = Resources.Load<Texture2D>("AsciiMath/" + k_Questions[i].image);
                if (m_Images[i] == null)
                    Debug.LogWarning($"[Ascii] No MathJax image for question {i + 1}; showing it as text");
            }
            BuildBriefing();
            BuildQuestion();
            BuildBoard();
            BuildAnswers();
            BuildEnd();
            HideAll(immediately: true);
        }

        // Stepping through the gate: how to play, then Start.
        public void Begin() => m_Rules.Brief(Time.unscaledTime);

        // Out through the reopened portal.
        public void Exited() => m_Rules.Exited(Time.unscaledTime);

        // Leaving the demo or moving the gate: back to waiting outside.
        public void Stop() => m_Rules.Reset(Time.unscaledTime);

        void OnDestroy()
        {
            if (m_Angels != null)
                m_Angels.Caught -= OnCaught;
            m_Rules.Changed -= OnChanged;
            Shader.SetGlobalFloat(k_StoneId, 0f);
        }

        void Update()
        {
            if (m_Head == null)
                return;
            var now = Time.unscaledTime;
            m_Rules.Tick(now);
            m_Angels.SetHeld(m_Rules.AngelsHeld(now));
            UpdateCountdowns(now);
            var stone = m_Rules.State == AsciiQuizState.Dead ? 1f : 0f;
            if (m_Stone != stone)
            {
                m_Stone = Mathf.MoveTowards(m_Stone, stone, Time.unscaledDeltaTime / k_StoneSeconds);
                Shader.SetGlobalFloat(k_StoneId, Mathf.SmoothStep(0f, 1f, m_Stone));
            }
        }

        void OnCaught() => m_Rules.Caught(Time.unscaledTime);

        void OnChanged(AsciiQuizState from, AsciiQuizState to)
        {
            Debug.Log($"[Ascii] Game {from} -> {to} (question {m_Rules.Question + 1}, {m_Rules.Correct} right, {m_Rules.Mistakes} wrong)");
            switch (to)
            {
                case AsciiQuizState.Idle:
                    HideAll();
                    m_Notes.Clear();
                    m_Angels.Despawn();
                    break;
                case AsciiQuizState.Briefing:
                    HideAll();
                    m_Notes.Clear();
                    m_Angels.Despawn();
                    WorldUi.PlaceInFront(m_Briefing, m_Head, k_FrontDistance, 0f);
                    M3Fade.Show(m_Briefing.gameObject);
                    break;
                case AsciiQuizState.Reading:
                    EnsureAngelsForQuestion();
                    ShowQuestion();
                    break;
                case AsciiQuizState.Answering:
                    ShowAnswers();
                    break;
                case AsciiQuizState.Feedback:
                    m_Notes.Clear();
                    ShowFeedback();
                    break;
                case AsciiQuizState.Escape:
                    ShowEscape();
                    m_OnEscape?.Invoke();
                    break;
                case AsciiQuizState.Dead:
                case AsciiQuizState.Won:
                    m_Notes.Clear();
                    ShowEnd(to);
                    break;
            }
        }

        // Q1–2: one angel. Q3: second. Q4: third. Q5: two more (five total). First stays furthest.
        static int AngelsForQuestion(int questionIndex) => questionIndex switch
        {
            0 or 1 => 1,
            2 => 2,
            3 => 3,
            _ => 5,
        };

        void EnsureAngelsForQuestion()
        {
            // Quiz stays where you're looking; angels always come from behind.
            m_PanelForward = Flat(m_Head.forward, m_PanelForward);
            m_AngelDirection = -m_PanelForward;
            var spawn = m_SpawnPoint(m_AngelDirection);
            m_AngelDirection = Flat(spawn - m_Head.position, m_AngelDirection);
            m_Angels.SetHeld(true);
            m_Angels.EnsureCount(AngelsForQuestion(m_Rules.Question), spawn, m_AngelDirection);
            if (m_Rules.Question == 0)
                Debug.Log($"[Ascii] Answer key {k_AnswerKey}");
        }

        void ShowQuestion()
        {
            var index = m_Rules.Question;
            M3Fade.Hide(m_Briefing.gameObject);
            M3Fade.Hide(m_Answers.gameObject);
            M3Fade.Hide(m_End.gameObject);

            m_QuestionHeader.text = $"Question {index + 1} of {AsciiQuizRules.k_Questions}";
            SetEquation(index);
            m_Count.color = M3Theme.Primary;
            PlaceQuizPanels(withAnswers: false);
            M3Fade.Show(m_Question.gameObject);
            M3Fade.Show(m_Board.gameObject);
        }

        void ShowAnswers()
        {
            var question = k_Questions[m_Rules.Question];
            m_AnswersHeader.text = $"Question {m_Rules.Question + 1} of {AsciiQuizRules.k_Questions}: pick the answer";
            for (var i = 0; i < m_Choices.Length; i++)
            {
                m_Choices[i].Text = question.choices[i];
                m_Choices[i].Selected = false;
                m_Choices[i].Interactable = true;
            }
            m_Feedback.text = "";
            // Same facing as the question — stacked under it, not on top of it.
            PlaceQuizPanels(withAnswers: true);
            M3Fade.Show(m_Question.gameObject);
            M3Fade.Show(m_Answers.gameObject);
            M3Fade.Show(m_Board.gameObject);
        }

        void PlaceQuizPanels(bool withAnswers)
        {
            var questionHeight = withAnswers ? k_QuestionHeightStacked : k_QuestionHeightAlone;
            var boardHeight = withAnswers ? k_BoardHeightStacked : k_BoardHeightAlone;
            PlaceOffset(m_Question, m_PanelForward, k_QuestionDistance, questionHeight, 0f);
            PlaceOffset(m_Board, m_PanelForward, k_BoardDistance, boardHeight, k_BoardSide);
            if (withAnswers)
                PlaceOffset(m_Answers, m_PanelForward, k_AnswerDistance, k_AnswersHeight, 0f);
        }

        void ShowFeedback()
        {
            var question = k_Questions[m_Rules.Question];
            foreach (var choice in m_Choices)
                choice.Interactable = false;
            var correct = question.choices[question.answer];
            string text;
            if (m_Rules.LastCorrect)
                text = "Correct!";
            else if (m_Rules.LastTimedOut)
                text = $"Time's up. It was {correct}.";
            else
                text = $"Wrong. It was {correct}.";
            if (!m_Rules.LastCorrect)
                text += $"  Mistakes {m_Rules.Mistakes} of {AsciiQuizRules.k_MaxMistakes}.";
            m_Feedback.text = text;
            m_Feedback.color = m_Rules.LastCorrect ? k_Right : M3Theme.Error;
            m_Status.text = text;
            m_Count.text = m_Rules.LastCorrect ? "Correct" : "Wrong";
            m_Count.color = m_Rules.LastCorrect ? k_Right : M3Theme.Error;
            m_Mistakes.text = MistakesText();
        }

        void ShowEscape()
        {
            M3Fade.Hide(m_Answers.gameObject);
            M3Fade.Hide(m_Board.gameObject);
            m_QuestionHeader.text = $"{m_Rules.Correct} of {AsciiQuizRules.k_Questions} right";
            m_Equation.gameObject.SetActive(false);
            m_PlainEquation.text = "The portal is open";
            m_PlainEquation.gameObject.SetActive(true);
            m_Count.text = "";
            m_Status.text = "Walk back through the doorway to escape. Keep an eye on the angel.";
            WorldUi.PlaceInFront(m_Question, m_Head, k_QuestionDistance, 5f);
            M3Fade.Show(m_Question.gameObject);
        }

        void ShowEnd(AsciiQuizState state)
        {
            HideAll();
            if (state == AsciiQuizState.Dead)
            {
                m_EndTitle.text = "You turned to stone";
                m_EndTitle.color = M3Theme.Error;
                m_EndBody.text = m_Rules.Death == AsciiQuizDeath.Caught
                    ? "The angel reached you while you weren't looking."
                    : $"Three wrong answers. You got {m_Rules.Correct} of {AsciiQuizRules.k_Questions} right.";
                m_EndPrimary.Text = "Try again";
                m_EndPrimary.Icon = "replay";
            }
            else
            {
                m_EndTitle.text = "Congratulations, you survived!";
                m_EndTitle.color = k_Right;
                m_EndBody.text = $"You escaped the ASCII dimension with {m_Rules.Correct} of {AsciiQuizRules.k_Questions} right " +
                                 $"and {m_Rules.Mistakes} {(m_Rules.Mistakes == 1 ? "mistake" : "mistakes")}.";
                m_EndPrimary.Text = "Play again";
                m_EndPrimary.Icon = "replay";
            }
            WorldUi.PlaceInFront(m_End, m_Head, k_FrontDistance, 0f);
            M3Fade.Show(m_End.gameObject);
        }

        void UpdateCountdowns(float now)
        {
            var remaining = Mathf.CeilToInt(m_Rules.Remaining(now));
            switch (m_Rules.State)
            {
                case AsciiQuizState.Reading:
                    m_Count.text = remaining.ToString();
                    m_Status.text = "Read it now. Look behind you!";
                    break;
                case AsciiQuizState.Answering:
                    var grace = m_Rules.GraceRemaining(now) > 0f;
                    m_Count.text = grace ? "…" : "Be quick!";
                    m_Count.color = grace ? M3Theme.Primary : M3Theme.Error;
                    m_Status.text = grace
                        ? "Choices are up. Look behind you!"
                        : "Be quick! Look behind you!";
                    m_AnswersTimer.text = grace ? "Look behind you!" : "Be quick!";
                    m_AnswersTimer.color = grace ? M3Theme.OnSurface : M3Theme.Error;
                    break;
            }
            if (m_Rules.Playing)
            {
                m_Mistakes.text = MistakesText();
                UpdateThreat(now);
            }
        }

        void UpdateThreat(float now)
        {
            if (m_Angels == null || !m_Angels.HasAngel)
                return;
            var distance = m_Angels.NearestDistance();
            if (m_Rules.State is AsciiQuizState.Reading or AsciiQuizState.Answering)
            {
                var near = distance < 2.2f;
                var pulse = near ? (0.55f + 0.45f * Mathf.Abs(Mathf.Sin(now * 6f))) : 1f;
                if (m_Status != null && m_Rules.State == AsciiQuizState.Answering && m_Rules.GraceRemaining(now) <= 0f)
                    m_Status.text = near
                        ? $"Look behind you! {distance:F1} m — Be quick!"
                        : "Be quick! Look behind you!";
                if (m_Count != null && near && m_Rules.State == AsciiQuizState.Answering && m_Rules.GraceRemaining(now) <= 0f)
                {
                    m_Count.text = "Look behind you!";
                    m_Count.color = Color.Lerp(M3Theme.Error, M3Theme.Primary, 1f - pulse);
                }
            }
        }

        string MistakesText() => $"Mistakes {m_Rules.Mistakes} / {AsciiQuizRules.k_MaxMistakes} · angels {m_Angels.Count}";

        void Choose(int index)
        {
            if (m_Rules.State != AsciiQuizState.Answering)
                return;
            var question = k_Questions[m_Rules.Question];
            m_Choices[index].Selected = true;
            var correct = index == question.answer;
            Debug.Log($"[Ascii] Question {m_Rules.Question + 1} ({question.plain}): picked {k_Letters[index]} " +
                      $"{question.choices[index]}, {(correct ? "right" : "wrong")}");
            m_Rules.Answer(correct, Time.unscaledTime);
        }

        void OnEndPrimary()
        {
            switch (m_Rules.State)
            {
                case AsciiQuizState.Dead:
                    m_Rules.Brief(Time.unscaledTime);
                    break;
                case AsciiQuizState.Won:
                    m_Rules.Reset(Time.unscaledTime);
                    m_OnPlayAgain?.Invoke();
                    break;
            }
        }

        void SetEquation(int index)
        {
            var image = m_Images[index];
            m_Equation.gameObject.SetActive(image != null);
            m_PlainEquation.gameObject.SetActive(image == null);
            m_PlainEquation.text = k_Questions[index].plain + " = ?";
            if (image == null)
                return;
            m_Equation.texture = image;
            var size = new Vector2(image.width, image.height) * k_DpPerPixel;
            var fit = Mathf.Min(1f, 560f / size.x, 190f / size.y);
            m_Equation.rectTransform.sizeDelta = size * fit;
        }

        void HideAll(bool immediately = false)
        {
            foreach (var panel in new[] { m_Briefing, m_Question, m_Board, m_Answers, m_End })
            {
                if (panel == null)
                    continue;
                if (immediately)
                    panel.gameObject.SetActive(false);
                else
                    M3Fade.Hide(panel.gameObject);
            }
        }

        void Place(RectTransform panel, Vector3 direction, float distance, float height) =>
            PlaceOffset(panel, direction, distance, height, 0f);

        void PlaceOffset(RectTransform panel, Vector3 direction, float distance, float height, float side)
        {
            var right = Vector3.Cross(Vector3.up, direction);
            if (right.sqrMagnitude < 1e-4f)
                right = Vector3.right;
            right.Normalize();
            var position = m_Head.position + direction * distance + right * side + Vector3.up * height;
            panel.SetPositionAndRotation(position, Quaternion.LookRotation(position - m_Head.position, Vector3.up));
            var canvas = panel.GetComponent<M3Canvas>();
            if (canvas != null)
                canvas.SetDistance(distance);
        }

        static Vector3 Flat(Vector3 direction, Vector3 fallback)
        {
            var flat = Vector3.ProjectOnPlane(direction, Vector3.up);
            return flat.sqrMagnitude > 1e-4f ? flat.normalized : fallback;
        }

        static TMP_Text Big(TMP_Text text, float size)
        {
            text.fontSize = size;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            return text;
        }

        void BuildBriefing()
        {
            m_Briefing = WorldUi.Panel("Ascii How To Play", transform, new Vector2(640f, 470f), k_FrontDistance, k_Glass);
            M3.Text("Title", m_Briefing, "Escape the ASCII dimension", M3Theme.Type.HeadlineSmall,
                new Vector2(0f, 195f), new Vector2(580f, 40f));
            M3.Text("Rules", m_Briefing,
                "• Answer 5 multiple-choice math questions.\n" +
                "• 5 s on the question board, then answers appear — Be quick! No countdown.\n" +
                "• Question and answers stay in front of you. Look behind you!\n" +
                "• Looking at an angel slows it (it still creeps). More join later.\n" +
                "• Pinch in the air to write red notes while you work it out.\n" +
                "• 3 wrong answers (running out of time counts) and you turn to stone.\n" +
                "• Get 3 or more right and the portal reopens. Walk out through it to win.",
                M3Theme.Type.BodyLarge, new Vector2(0f, 10f), new Vector2(580f, 300f), M3Theme.OnSurfaceVariant,
                TextAlignmentOptions.TopLeft);
            M3.Button("Start", m_Briefing, "Start", M3Button.Variant.Filled, new Vector2(0f, -190f),
                () => m_Rules.Start(Time.unscaledTime), "play_arrow", 200f);
        }

        // Toward the angel, so reading it means looking at the angel. No gaze surface, so a pinch in
        // front of it writes notes rather than hitting a panel.
        void BuildQuestion()
        {
            m_Question = M3.Canvas("Ascii Question", transform, new Vector2(640f, 400f), k_QuestionDistance, k_Glass);
            m_QuestionHeader = M3.Text("Header", m_Question, "", M3Theme.Type.TitleMedium, new Vector2(-140f, 165f),
                new Vector2(320f, 32f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            m_Mistakes = M3.Text("Mistakes", m_Question, "", M3Theme.Type.TitleMedium, new Vector2(140f, 165f),
                new Vector2(320f, 32f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Right);
            var equation = M3.Group("Equation", m_Question, new Vector2(0f, 45f), new Vector2(560f, 190f));
            m_Equation = equation.gameObject.AddComponent<RawImage>();
            m_Equation.raycastTarget = false;
            m_PlainEquation = Big(M3.Text("Plain Equation", m_Question, "", M3Theme.Type.HeadlineSmall,
                new Vector2(0f, 45f), new Vector2(560f, 120f)), 56f);
            m_Count = Big(M3.Text("Count", m_Question, "", M3Theme.Type.HeadlineSmall, new Vector2(0f, -95f),
                new Vector2(300f, 80f), M3Theme.Primary), 64f);
            m_Status = M3.Text("Status", m_Question, "", M3Theme.Type.BodyLarge, new Vector2(0f, -162f),
                new Vector2(590f, 48f), M3Theme.OnSurfaceVariant);
        }

        void BuildBoard()
        {
            m_Board = M3.Canvas("Ascii Scratch Pad", transform, new Vector2(460f, 280f), k_BoardDistance,
                M3Theme.WithAlpha(M3Theme.Surface, 0.35f));
            M3.Text("Label", m_Board, "Scratch pad: pinch in the air to write", M3Theme.Type.BodyMedium,
                new Vector2(0f, 118f), new Vector2(420f, 28f), M3Theme.OnSurfaceVariant);
        }

        // Behind the player, away from the angel.
        void BuildAnswers()
        {
            m_Answers = WorldUi.Panel("Ascii Answers", transform, new Vector2(680f, 340f), k_AnswerDistance, k_GlassHigh);
            m_AnswersHeader = M3.Text("Header", m_Answers, "", M3Theme.Type.TitleMedium, new Vector2(0f, 138f),
                new Vector2(620f, 32f), M3Theme.OnSurfaceVariant);
            m_AnswersTimer = Big(M3.Text("Timer", m_Answers, "", M3Theme.Type.HeadlineSmall, new Vector2(0f, 96f),
                new Vector2(620f, 40f)), 32f);
            m_Choices = new M3Button[4];
            const float width = 300f;
            for (var i = 0; i < m_Choices.Length; i++)
            {
                var index = i;
                var position = new Vector2(i % 2 == 0 ? -160f : 160f, i < 2 ? 28f : -48f);
                var choice = M3.Button($"Answer {k_Letters[i]}", m_Answers, "", M3Button.Variant.Tonal, position,
                    () => Choose(index), width: width);
                choice.Label.fontSize = 26f;
                var shape = M3.Icon("Shape", (RectTransform)choice.transform, k_Shapes[i].icon, 28f,
                    new Vector2(-width / 2f + 34f, 0f), k_ShapeColors[i]);
                shape.rectTransform.localEulerAngles = new Vector3(0f, 0f, k_Shapes[i].degrees);
                m_Choices[i] = choice;
            }
            m_Feedback = M3.Text("Feedback", m_Answers, "", M3Theme.Type.TitleMedium, new Vector2(0f, -128f),
                new Vector2(620f, 40f));
        }

        void BuildEnd()
        {
            m_End = WorldUi.Panel("Ascii Game Over", transform, new Vector2(580f, 330f), k_FrontDistance, k_Glass);
            m_EndTitle = Big(M3.Text("Title", m_End, "", M3Theme.Type.HeadlineSmall, new Vector2(0f, 100f),
                new Vector2(540f, 56f)), 34f);
            m_EndBody = M3.Text("Body", m_End, "", M3Theme.Type.BodyLarge, new Vector2(0f, 20f), new Vector2(500f, 80f),
                M3Theme.OnSurfaceVariant);
            m_EndPrimary = M3.Button("Primary", m_End, "Try again", M3Button.Variant.Filled, new Vector2(-115f, -105f),
                OnEndPrimary, "replay", 210f);
            M3.Button("Lobby", m_End, "Lobby", M3Button.Variant.Tonal, new Vector2(115f, -105f),
                () => m_OnLobby?.Invoke(), "home", 210f);
        }
    }
}
