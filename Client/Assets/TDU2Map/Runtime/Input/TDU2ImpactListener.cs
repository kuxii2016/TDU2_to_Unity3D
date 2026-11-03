using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>Meldet Kollisionen des Spielerautos an die Vibration/das Force Feedback (wird von TDU2ForceFeedback angehängt).</summary>
    public sealed class TDU2ImpactListener : MonoBehaviour
    {
        Rigidbody _rb;
        void Awake() { _rb = GetComponent<Rigidbody>(); }
        void OnCollisionEnter(Collision c) => TDU2ForceFeedback.OnVehicleCollision(_rb, c);
    }
}
