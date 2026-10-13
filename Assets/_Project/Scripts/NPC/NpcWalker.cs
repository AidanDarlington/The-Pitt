using ThePitt.UI;
using ThePitt.World;
using UnityEngine;
using UnityEngine.AI;

namespace ThePitt.NPC
{
    /// <summary>
    /// Moves an NPC around The Pitt: steps out of a doorway, walks between a few places, loitering at
    /// each, then walks into another doorway and is gone. Stops to face the player while trading.
    /// Destinations and doorways come from <see cref="NpcPopulation"/>.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent), typeof(NpcBuyer))]
    public class NpcWalker : MonoBehaviour
    {
        private enum State { Emerging, Walking, Loitering, Entering }

        [SerializeField] private Vector2Int stops = new(2, 4);
        [Tooltip("Seconds spent standing about at each stop.")]
        [SerializeField] private Vector2 loiterTime = new(3f, 12f);
        [Tooltip("Seconds to step through a doorway.")]
        [SerializeField, Min(0.1f)] private float doorTime = 0.9f;
        [Tooltip("Give up on a destination after this long without moving a couple of metres.")]
        [SerializeField, Min(1f)] private float stuckTime = 6f;

        private NavMeshAgent agent;
        private NpcBuyer buyer;
        private NpcPopulation population;
        private State state;
        private int stopsLeft;
        private float timer;
        private Vector3 doorFrom, doorTo;
        private PittGreybox.Doorway exit;
        private bool leaving;
        private int failures;
        private Vector3 progressFrom;
        private float lastProgress;

        public NpcBuyer Buyer => buyer;
        /// <summary>Heading for a doorway to go indoors.</summary>
        public bool IsLeaving => leaving || state == State.Entering;

        /// <summary>Back in control after something else had the agent (a fight): head straight indoors.</summary>
        public void LeaveNow()
        {
            stopsLeft = 0;
            leaving = false;
            Leave();
        }

        /// <summary>Cuts the outing short: head for the nearest sensible doorway and go in.</summary>
        public void SendHome()
        {
            if (IsLeaving) return;
            if (state == State.Emerging)
            {
                // Finish stepping out first; the next stop will be the way home.
                stopsLeft = 0;
                leaving = true;
                return;
            }
            Leave();
        }

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            buyer = GetComponent<NpcBuyer>();
        }

        /// <summary>Starts the NPC stepping out of a doorway.</summary>
        public void BeginFromDoor(NpcPopulation owner, PittGreybox.Doorway door, float speed)
        {
            Init(owner, speed);
            agent.enabled = false;
            doorFrom = door.Inside;
            doorTo = door.Approach;
            transform.SetPositionAndRotation(doorFrom, Quaternion.LookRotation(doorTo - doorFrom));
            state = State.Emerging;
            timer = 0f;
        }

        /// <summary>Starts the NPC partway through its day, already out on the street.</summary>
        public void BeginOnStreet(NpcPopulation owner, Vector3 position, float speed)
        {
            Init(owner, speed);
            transform.position = position;
            agent.enabled = true;
            agent.Warp(position);
            NextStop();
        }

        private void Init(NpcPopulation owner, float speed)
        {
            population = owner;
            agent.speed = speed;
            agent.angularSpeed = 360f;
            agent.acceleration = 6f;
            agent.stoppingDistance = 0.3f;
            agent.autoBraking = true;
            stopsLeft = Random.Range(stops.x, stops.y + 1);
            leaving = false;
            failures = 0;
        }

        private void Update()
        {
            switch (state)
            {
                case State.Emerging:
                case State.Entering:
                    StepThroughDoor();
                    return;
            }

            // Hold still and face the player while they trade.
            bool trading = TradeUI.Instance != null && TradeUI.Instance.CurrentBuyer == buyer;
            agent.isStopped = trading;
            if (trading)
            {
                var cam = Camera.main;
                if (cam != null) FaceTowards(cam.transform.position);
                return;
            }

            if (state == State.Loitering)
            {
                timer -= Time.deltaTime;
                if (timer <= 0f) NextStop();
                return;
            }

            // Walking.
            if (agent.pathPending) return;
            if (agent.remainingDistance <= agent.stoppingDistance + 0.1f)
            {
                Arrived();
                return;
            }
            // remainingDistance reads infinity on long paths, so judge progress by ground actually covered.
            if ((transform.position - progressFrom).sqrMagnitude > 4f)
            {
                progressFrom = transform.position;
                lastProgress = Time.time;
            }
            else if (Time.time - lastProgress > stuckTime)
            {
                // Blocked or wedged: try somewhere else (or another way out).
                if (leaving) Leave();
                else NextStop();
            }
        }

        private void StepThroughDoor()
        {
            timer += Time.deltaTime;
            float t = Mathf.Clamp01(timer / doorTime);
            transform.position = Vector3.Lerp(doorFrom, doorTo, t);
            if (t < 1f) return;

            if (state == State.Entering)
            {
                population.Despawn(this);
                return;
            }
            agent.enabled = true;
            agent.Warp(doorTo);
            NextStop();
        }

        private void Arrived()
        {
            if (leaving)
            {
                agent.enabled = false;
                doorFrom = transform.position;
                doorTo = exit.Inside;
                transform.rotation = Quaternion.LookRotation(Flat(doorTo - doorFrom));
                state = State.Entering;
                timer = 0f;
                return;
            }
            state = State.Loitering;
            timer = Random.Range(loiterTime.x, loiterTime.y);
        }

        private void NextStop()
        {
            if (leaving || stopsLeft-- <= 0)
            {
                Leave();
                return;
            }
            if (population.TryPickDestination(this, out var destination)) GoTo(destination);
            else Leave();
        }

        private void Leave()
        {
            leaving = true;
            if (population.TryPickExit(this, out exit))
            {
                GoTo(exit.Approach);
                return;
            }
            // Nowhere to go right now: wait and try again, and only vanish if it keeps failing.
            if (++failures > 5)
            {
                population.Despawn(this);
                return;
            }
            state = State.Loitering;
            timer = 2f;
        }

        private void GoTo(Vector3 destination)
        {
            state = State.Walking;
            failures = 0;
            agent.isStopped = false;
            agent.SetDestination(destination);
            progressFrom = transform.position;
            lastProgress = Time.time;
        }

        private void FaceTowards(Vector3 target)
        {
            var d = Flat(target - transform.position);
            if (d.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d), 360f * Time.deltaTime);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 0.0001f ? Vector3.forward : v;
        }
    }
}
