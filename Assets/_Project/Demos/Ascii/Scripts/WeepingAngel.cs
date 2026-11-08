using System;
using UnityEngine.AI;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // A stone angel that only moves when the head is not looking at it. When the head's forward
    // cone covers its body it freezes in place and faces the player; when the gaze leaves it
    // walks toward the head along the floor. Logs [Ascii] the first time it freezes and when it
    // reaches striking distance.
    public class WeepingAngel : MonoBehaviour
    {
        const float k_Reach = 0.55f;
        const float k_FreezeSeconds = 5f;

        [SerializeField] float m_Speed = 0.35f;
        [SerializeField] float m_SeeDegrees = 28f;
        [SerializeField] float m_BodyRadius = 0.35f;
        [SerializeField] float m_BodyHeight = 1.8f;

        Transform m_Head;
        NavMeshAgent m_Agent;
        bool m_Seen;
        bool m_LoggedFreeze;
        bool m_Reached;
        float m_FreezeUntil;
        float m_NextDestination;
        public event Action Caught;

        // Held still whether or not it's seen: while a question is read, and between questions.
        public bool Held { get; set; }

        public void Configure(Transform head, float speed)
        {
            m_Head = head;
            m_Speed = Mathf.Max(0.02f, speed);
            m_Seen = false;
            m_LoggedFreeze = false;
            m_Reached = false;
            m_FreezeUntil = 0f;
            m_Agent = GetComponent<NavMeshAgent>();
            if (m_Agent == null)
                m_Agent = gameObject.AddComponent<NavMeshAgent>();
            m_Agent.enabled = false;
            m_Agent.speed = m_Speed;
            m_Agent.stoppingDistance = k_Reach;
            m_Agent.height = m_BodyHeight;
            m_Agent.radius = m_BodyRadius;
            m_Agent.baseOffset = 0f;
            m_Agent.updateRotation = false;
            m_Agent.updateUpAxis = false;
            enabled = head != null;
        }

        public void RefreshNavigation()
        {
            if (m_Agent == null || m_Agent.enabled || !NavMesh.SamplePosition(transform.position, out var hit, 0.6f, NavMesh.AllAreas))
                return;
            transform.position = hit.position;
            m_Agent.enabled = true;
            m_Agent.Warp(hit.position);
            SetDestination();
        }

        void Update()
        {
            if (m_Head == null)
                return;

            var seen = IsSeen();
            if (seen && !m_Seen)
            {
                m_FreezeUntil = Time.unscaledTime + k_FreezeSeconds;
                if (!m_LoggedFreeze)
                {
                    m_LoggedFreeze = true;
                    Debug.Log("[Ascii] Angel frozen for five seconds");
                }
            }
            m_Seen = seen;

            FaceHead();
            var flat = m_Head.position - transform.position;
            flat.y = 0f;
            var distance = flat.magnitude;
            if (distance <= k_Reach)
            {
                if (!m_Reached)
                {
                    m_Reached = true;
                    Debug.Log("[Ascii] An angel reached you");
                    Caught?.Invoke();
                }
                return;
            }

            var frozen = Held || m_Seen || Time.unscaledTime < m_FreezeUntil;
            if (m_Agent != null && m_Agent.enabled && m_Agent.isOnNavMesh)
            {
                m_Agent.isStopped = frozen;
                if (frozen)
                    return;
                if (Time.unscaledTime >= m_NextDestination)
                {
                    SetDestination();
                    m_NextDestination = Time.unscaledTime + 0.25f;
                }
                return;
            }
            if (frozen)
                return;

            var step = Mathf.Min(m_Speed * Time.deltaTime, distance - k_Reach);
            transform.position += flat.normalized * step;
            if (m_Agent == null || !m_Agent.enabled)
            {
                var p = transform.position;
                p.y = m_Head.position.y - 1.55f;
                transform.position = p;
            }
        }

        void SetDestination()
        {
            if (m_Agent == null || !m_Agent.enabled || !m_Agent.isOnNavMesh)
                return;
            var destination = m_Head.position;
            destination.y = m_Agent.nextPosition.y;
            m_Agent.SetDestination(destination);
        }

        void FaceHead()
        {
            var look = m_Head.position - transform.position;
            look.y = 0f;
            if (look.sqrMagnitude < 1e-4f)
                return;
            transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        }

        // True when the angel's torso sits inside the head's forward cone. Cardboard has no
        // reliable Renderer.isVisible across both eyes, so the gaze cone is what counts.
        bool IsSeen()
        {
            var origin = m_Head.position;
            var chest = transform.position + Vector3.up * (m_BodyHeight * 0.55f);
            var to = chest - origin;
            var distance = to.magnitude;
            if (distance < 0.2f)
                return true;
            var dir = to / distance;
            if (Vector3.Dot(m_Head.forward, dir) < Mathf.Cos(m_SeeDegrees * Mathf.Deg2Rad))
                return false;
            // A little lateral width so glancing past a shoulder still counts as seeing them.
            var side = Vector3.Cross(m_Head.forward, Vector3.up);
            if (side.sqrMagnitude < 1e-4f)
                return true;
            side.Normalize();
            var lateral = Mathf.Abs(Vector3.Dot(to, side));
            return lateral <= m_BodyRadius + distance * Mathf.Tan(m_SeeDegrees * Mathf.Deg2Rad);
        }
    }
}
