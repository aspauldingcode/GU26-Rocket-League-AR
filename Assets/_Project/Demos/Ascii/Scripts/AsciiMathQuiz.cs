using System;
using CardboardXR.Interaction;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    public class AsciiMathQuiz : MonoBehaviour
    {
        const float k_SecondsPerQuestion = 15f;
        const float k_WallRetrySeconds = 1f;

        readonly (string prompt, int answer, int a, int b, int c, int d)[] m_Questions =
        {
            ("8 x 7 = ?", 56, 42, 49, 56, 63),
            ("36 / 4 = ?", 9, 6, 8, 9, 12),
            ("15 + 28 = ?", 43, 41, 42, 43, 45),
            ("9 x 6 = ?", 54, 45, 48, 54, 56),
            ("81 - 37 = ?", 44, 34, 42, 44, 46),
        };

        ARPlaneManager m_Planes;
        Transform m_Head;
        Action m_OnWrong;
        Action m_OnComplete;
        ARPlane m_Wall;
        RectTransform m_Panel;
        TMP_Text m_QuestionText;
        TMP_Text m_TimerText;
        TMP_Text m_FeedbackText;
        M3Button[] m_AnswerButtons;
        int m_QuestionIndex;
        float m_Deadline;
        float m_NextWallCheck;
        bool m_Active;
        bool m_Completed;

        public event Action<ARPlane> WallSelected;
        public ARPlane QuizWall => m_Wall;

        public void Configure(ARPlaneManager planes, Transform head, Action onWrong, Action onComplete)
        {
            m_Planes = planes;
            m_Head = head;
            m_OnWrong = onWrong;
            m_OnComplete = onComplete;
        }

        public void Begin()
        {
            if (m_Completed)
                return;
            m_Active = true;
            TrySelectWall();
            if (m_Panel != null)
                m_Panel.gameObject.SetActive(true);
        }

        public void Stop()
        {
            m_Active = false;
            if (m_Panel != null)
                m_Panel.gameObject.SetActive(false);
        }

        void Update()
        {
            if (!m_Active)
                return;
            if (m_Wall == null || m_Wall.subsumedBy != null || m_Wall.trackingState != TrackingState.Tracking)
            {
                m_Wall = null;
                if (m_Panel != null)
                    m_Panel.gameObject.SetActive(false);
                if (Time.unscaledTime >= m_NextWallCheck)
                {
                    m_NextWallCheck = Time.unscaledTime + k_WallRetrySeconds;
                    TrySelectWall();
                }
                return;
            }

            PlaceOnWall();
            if (m_Completed)
                return;
            var remaining = Mathf.Max(0f, m_Deadline - Time.unscaledTime);
            m_TimerText.text = $"Time  {Mathf.CeilToInt(remaining):00}";
            if (remaining <= 0f)
                WrongAnswer("Time's up. Try again.");
        }

        bool TrySelectWall()
        {
            if (m_Planes == null)
                return false;
            ARPlane best = null;
            var bestArea = 0f;
            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || plane.trackingState != TrackingState.Tracking || plane.subsumedBy != null ||
                    plane.alignment != PlaneAlignment.Vertical)
                    continue;
                var area = plane.size.x * plane.size.y;
                if (area <= bestArea)
                    continue;
                best = plane;
                bestArea = area;
            }
            if (best == null)
                return false;

            m_Wall = best;
            BuildPanel();
            WallSelected?.Invoke(m_Wall);
            Debug.Log($"[Ascii] Quiz panel placed on scanned wall ({bestArea:F1} m2)");
            return true;
        }

        void BuildPanel()
        {
            if (m_Panel != null)
                Destroy(m_Panel.gameObject);
            m_Panel = M3.Canvas("Ascii Math Quiz", transform, new Vector2(640f, 420f), 1.8f,
                M3Theme.SurfaceContainer, M3Theme.k_ShapePanel);
            m_Panel.gameObject.AddComponent<GazeCanvas>();
            m_QuestionText = M3.Text("Question", m_Panel, "", M3Theme.Type.HeadlineSmall,
                new Vector2(0f, 126f), new Vector2(560f, 64f));
            m_TimerText = M3.Text("Timer", m_Panel, "", M3Theme.Type.TitleMedium,
                new Vector2(0f, 76f), new Vector2(300f, 32f));
            m_FeedbackText = M3.Text("Feedback", m_Panel, "", M3Theme.Type.BodyMedium,
                new Vector2(0f, 38f), new Vector2(540f, 30f));

            m_AnswerButtons = new M3Button[4];
            const float width = 250f;
            const float x = 132f;
            m_AnswerButtons[0] = M3.Button("Answer A", m_Panel, "", M3Button.Variant.Tonal,
                new Vector2(-x, -32f), () => SelectAnswer(0), width: width);
            m_AnswerButtons[1] = M3.Button("Answer B", m_Panel, "", M3Button.Variant.Tonal,
                new Vector2(x, -32f), () => SelectAnswer(1), width: width);
            m_AnswerButtons[2] = M3.Button("Answer C", m_Panel, "", M3Button.Variant.Tonal,
                new Vector2(-x, -100f), () => SelectAnswer(2), width: width);
            m_AnswerButtons[3] = M3.Button("Answer D", m_Panel, "", M3Button.Variant.Tonal,
                new Vector2(x, -100f), () => SelectAnswer(3), width: width);
            PlaceOnWall();
            m_Panel.gameObject.SetActive(m_Active);
            if (m_Completed)
            {
                m_QuestionText.text = "All answers correct";
                m_TimerText.text = "The return portal is open";
                m_FeedbackText.text = "Go back through the doorway to escape.";
                foreach (var button in m_AnswerButtons)
                    button.Interactable = false;
            }
            else
            {
                PresentQuestion();
            }
        }

        void PlaceOnWall()
        {
            if (m_Panel == null || m_Wall == null || m_Head == null)
                return;
            var center = m_Wall.centerInPlaneSpace;
            var position = m_Wall.transform.TransformPoint(new Vector3(center.x, 0f, center.y));
            var towardHead = Vector3.ProjectOnPlane(m_Head.position - position, Vector3.up);
            var normal = m_Wall.transform.up;
            if (towardHead.sqrMagnitude > 1e-4f && Vector3.Dot(normal, towardHead) < 0f)
                normal = -normal;
            m_Panel.SetPositionAndRotation(position + normal * 0.06f, Quaternion.LookRotation(-normal, Vector3.up));
        }

        void PresentQuestion()
        {
            var question = m_Questions[m_QuestionIndex];
            m_QuestionText.text = $"{m_QuestionIndex + 1} / {m_Questions.Length}    {question.prompt}";
            m_FeedbackText.text = "";
            m_FeedbackText.color = M3Theme.OnSurfaceVariant;
            for (var i = 0; i < m_AnswerButtons.Length; i++)
                m_AnswerButtons[i].Text = Choice(question, i).ToString();
            m_Deadline = Time.unscaledTime + k_SecondsPerQuestion;
            m_TimerText.text = $"Time  {k_SecondsPerQuestion:00}";
        }

        static int Choice((string prompt, int answer, int a, int b, int c, int d) question, int index) => index switch
        {
            0 => question.a,
            1 => question.b,
            2 => question.c,
            _ => question.d,
        };

        void SelectAnswer(int index)
        {
            if (!m_Active || m_Completed || m_Wall == null)
                return;
            if (Choice(m_Questions[m_QuestionIndex], index) != m_Questions[m_QuestionIndex].answer)
            {
                WrongAnswer("Incorrect. The angel is faster. Try again.");
                return;
            }

            m_QuestionIndex++;
            if (m_QuestionIndex >= m_Questions.Length)
            {
                m_Completed = true;
                m_QuestionText.text = "All answers correct";
                m_TimerText.text = "The return portal is open";
                m_FeedbackText.text = "Go back through the doorway to escape.";
                foreach (var button in m_AnswerButtons)
                    button.Interactable = false;
                m_OnComplete?.Invoke();
                return;
            }
            PresentQuestion();
        }

        void WrongAnswer(string feedback)
        {
            m_OnWrong?.Invoke();
            m_FeedbackText.text = feedback;
            m_FeedbackText.color = M3Theme.Error;
            m_Deadline = Time.unscaledTime + k_SecondsPerQuestion;
            Debug.Log("[Ascii] Math answer incorrect; the angel speeds up");
        }
    }
}