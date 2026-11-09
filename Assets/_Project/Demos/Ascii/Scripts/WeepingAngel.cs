using System;
using UnityEngine.AI;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // A stone angel that creeps toward the head. Looking at it doesn't stop it cold — it slows to a
    // crawl so the approach still reads as fear. Walk speed is a percentage of the gap it started
    // with, and rises as it gets closer. Held (read / grace) is the only full halt. Logs [Ascii].
    public class WeepingAngel : MonoBehaviour
    {
        const float k_Reach = 0.55f;
        // While watched: keep moving, but this fraction of current speed (visibly crawling).
        const float k_WatchedSpeed = 0.08f;
        // After gaze leaves, ease back to full speed over this many seconds.
        const float k_ReleaseSeconds = 0.7f;

        [SerializeField] float m_Speed = 0.35f;
        [SerializeField] float m_SeeDegrees = 28f;
        [SerializeField] float m_BodyRadius = 0.35f;
        [SerializeField] float m_BodyHeight = 1.8f;

        Transform m_Head;
        NavMeshAgent m_Agent;
        float m_StartDistance = 4f;
        float m_BaseSpeed = 0.35f;
        bool m_Seen;
        bool m_LoggedSlow;
        bool m_Reached;
        float m_WatchBlend = 1f;
        float m_NextDestination;
        Vector3 m_BaseScale = Vector3.one;
        public event Action Caught;

        // Full halt whether or not it's seen: while a question is read, and during answer grace.
        public bool Held { get; set; }

        public float StartDistance => m_StartDistance;

        public void Configure(Transform head, float speed, float startDistance = 0f)
        {
            m_Head = head;
            m_StartDistance = Mathf.Max(k_Reach + 0.25f, startDistance > 0f ? startDistance : speed * AsciiAngels.k_ReachSeconds + k_Reach);
            m_BaseSpeed = Mathf.Max(0.02f, (m_StartDistance - k_Reach) / AsciiAngels.k_ReachSeconds);
            m_Speed = m_BaseSpeed;
            m_Seen = false;
            m_LoggedSlow = false;
            m_Reached = false;
            m_WatchBlend = 1f;
            m_BaseScale = transform.localScale.sqrMagnitude > 1e-6f ? transform.localScale : Vector3.one;
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
            if (seen && !m_Seen && !m_LoggedSlow)
            {
                m_LoggedSlow = true;
                Debug.Log("[Ascii] Angel slowed under your gaze (still creeping)");
            }
            m_Seen = seen;
            // Snap down when watched; ease back up when you look away so the surge is felt.
            var watchTarget = seen ? 0f : 1f;
            var watchRate = seen ? 8f : (1f / k_ReleaseSeconds);
            m_WatchBlend = Mathf.MoveTowards(m_WatchBlend, watchTarget, Time.deltaTime * watchRate);

            FaceHead();
            var flat = m_Head.position - transform.position;
            flat.y = 0f;
            var distance = flat.magnitude;
            var progress = 1f - Mathf.Clamp01((distance - k_Reach) / Mathf.Max(0.01f, m_StartDistance - k_Reach));
            var chase = m_BaseSpeed * (1f + 1.2f * progress * progress);
            var watchMul = Mathf.Lerp(k_WatchedSpeed, 1f, m_WatchBlend);
            m_Speed = chase * watchMul;
            var loom = 1f + 0.18f * progress;
            transform.localScale = m_BaseScale * loom;

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

            // Reading / grace: full halt. Looking: crawl. Looking away: full chase.
            if (Held)
            {
                if (m_Agent != null && m_Agent.enabled && m_Agent.isOnNavMesh)
                    m_Agent.isStopped = true;
                return;
            }

            if (m_Agent != null && m_Agent.enabled && m_Agent.isOnNavMesh)
            {
                m_Agent.isStopped = false;
                m_Agent.speed = m_Speed;
                if (Time.unscaledTime >= m_NextDestination)
                {
                    SetDestination();
                    m_NextDestination = Time.unscaledTime + 0.25f;
                }
                return;
            }

            var step = Mathf.Min(m_Speed * Time.deltaTime, distance - k_Reach);
            transform.position += flat.normalized * step;
            var p = transform.position;
            p.y = AsciiAngels.GroundY(m_Head.position.y);
            transform.position = p;
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
            var side = Vector3.Cross(m_Head.forward, Vector3.up);
            if (side.sqrMagnitude < 1e-4f)
                return true;
            side.Normalize();
            var lateral = Mathf.Abs(Vector3.Dot(to, side));
            return lateral <= m_BodyRadius + distance * Mathf.Tan(m_SeeDegrees * Mathf.Deg2Rad);
        }
    }
}
