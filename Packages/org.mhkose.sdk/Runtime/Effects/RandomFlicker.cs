using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Shows one random child at a time and switches at a random interval, each time at a random yaw (lightning bolts,
    /// electric arcs: Thunder's three 'blitz' / arc layers).
    /// </summary>
    public class RandomFlicker : MonoBehaviour
    {
        [Tooltip("Seconds between switches (random in this range).")]
        [SerializeField] Vector2 interval = new(0.04f, 0.09f);
        [Tooltip("Turn the shown child to a random yaw on every switch.")]
        [SerializeField] bool randomYaw = true;
        [Tooltip("Chance per switch that nothing is shown (flicker gaps).")]
        [Range(0f, 1f)] [SerializeField] float gapChance = 0.15f;

        float next;
        int current = -1;

        void OnEnable()
        {
            next = 0f;
            Switch();
        }

        void Update()
        {
            next -= Time.deltaTime;
            if (next <= 0f) Switch();
        }

        void Switch()
        {
            next = Random.Range(interval.x, interval.y);
            var n = transform.childCount;
            if (n == 0) return;
            var pick = Random.value < gapChance ? -1 : (current + 1 + Random.Range(0, Mathf.Max(1, n - 1))) % n;
            for (var i = 0; i < n; i++)
            {
                var child = transform.GetChild(i);
                var on = i == pick;
                child.gameObject.SetActive(on);
                if (on && randomYaw) child.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            }
            current = pick;
        }
    }
}
